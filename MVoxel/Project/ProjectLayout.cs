using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

public sealed class ProjectManifest
{
    public int FormatVersion { get; set; } = 1;
    public StableId ProjectId { get; set; } = StableId.New();
    public string Name { get; set; } = "MVoxel Project";
    public string EngineVersion { get; set; } = "0.1.0-dev";
}

public sealed class PluginManifest
{
    public int FormatVersion { get; set; } = 1;
    public List<string> Enabled { get; set; } = new();
}

public sealed class PluginLockFile
{
    public int FormatVersion { get; set; } = 1;
    public List<LockedPlugin> Plugins { get; set; } = new();
}

public sealed class LockedPlugin
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Source { get; set; } = "embedded";
}

public sealed class ProjectHandle
{
    internal ProjectHandle(string rootPath, ProjectManifest manifest)
    {
        RootPath = rootPath;
        Manifest = manifest;
    }

    public string RootPath { get; }
    public ProjectManifest Manifest { get; }

    public string AssetsPath => Path.Combine(RootPath, "Assets");
    public string PluginsPath => Path.Combine(RootPath, "Plugins");
}

public static class ProjectLayout
{
    public const string ProjectFileName = "Project.mvoxproj";

    public static readonly string[] AssetDirectories =
    {
        "Scenes",
        "Prefabs",
        "VoxelModels",
        "Materials",
        "Palettes",
        "Textures",
        "Environments",
        "RenderPresets",
        "Generators",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static ProjectHandle Create(string rootPath, string? name = null)
    {
        var absolutePath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(absolutePath);
        var manifest = new ProjectManifest
        {
            Name = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(absolutePath).Name : name,
        };
        EnsureDirectories(absolutePath);
        WriteJson(Path.Combine(absolutePath, ProjectFileName), manifest);
        WriteJson(Path.Combine(absolutePath, "Plugins", "manifest.json"), new PluginManifest());
        WriteJson(Path.Combine(absolutePath, "Plugins", "plugins-lock.json"), new PluginLockFile());
        return new ProjectHandle(absolutePath, manifest);
    }

    public static ProjectHandle Open(string rootPath)
    {
        var absolutePath = Path.GetFullPath(rootPath);
        var manifestPath = Path.Combine(absolutePath, ProjectFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Not an MVoxel project: {manifestPath}", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException($"Invalid project manifest: {manifestPath}");
        EnsureDirectories(absolutePath);
        return new ProjectHandle(absolutePath, manifest);
    }

    public static void EnsureDirectories(string rootPath)
    {
        Directory.CreateDirectory(Path.Combine(rootPath, "Assets"));
        Directory.CreateDirectory(Path.Combine(rootPath, "Plugins"));
        foreach (var directory in AssetDirectories)
        {
            Directory.CreateDirectory(Path.Combine(rootPath, "Assets", directory));
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        AtomicFile.WriteText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}
