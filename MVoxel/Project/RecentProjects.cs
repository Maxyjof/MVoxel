using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

public sealed class RecentProjectEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset LastOpenedUtc { get; set; }
}

/// <summary>Stores the small, user-level list shown on the welcome page.</summary>
public static class RecentProjects
{
    public const int MaxEntries = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string DefaultStoragePath
    {
        get
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(applicationData, "MVoxel", "recent-projects.json");
        }
    }

    public static IReadOnlyList<RecentProjectEntry> Load(string? storagePath = null)
    {
        var path = storagePath ?? DefaultStoragePath;
        if (!File.Exists(path))
        {
            return Array.Empty<RecentProjectEntry>();
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<RecentProjectEntry>>(File.ReadAllText(path), JsonOptions)
                ?? new List<RecentProjectEntry>();
            return entries
                .Where(entry => entry is not null && IsExistingProject(entry))
                .GroupBy(entry => NormalizePath(entry.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(entry => entry.LastOpenedUtc).First())
                .OrderByDescending(entry => entry.LastOpenedUtc)
                .Take(MaxEntries)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
        catch (IOException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
        catch (ArgumentException)
        {
            return Array.Empty<RecentProjectEntry>();
        }
    }

    public static void Record(ProjectHandle project, string? storagePath = null, DateTimeOffset? openedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var path = storagePath ?? DefaultStoragePath;
        var normalizedProjectPath = NormalizePath(project.RootPath);
        var entries = Load(path)
            .Where(entry => !string.Equals(NormalizePath(entry.Path), normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        entries.Add(new RecentProjectEntry
        {
            Name = project.Manifest.Name,
            Path = normalizedProjectPath,
            LastOpenedUtc = openedAtUtc ?? DateTimeOffset.UtcNow,
        });

        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Recent-project storage must include a directory.", nameof(storagePath));
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(
            entries.OrderByDescending(entry => entry.LastOpenedUtc).Take(MaxEntries),
            JsonOptions);
        AtomicFile.WriteText(path, json);
    }

    private static bool IsExistingProject(RecentProjectEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
        {
            return false;
        }

        try
        {
            var rootPath = NormalizePath(entry.Path);
            return Directory.Exists(rootPath)
                && File.Exists(System.IO.Path.Combine(rootPath, ProjectLayout.ProjectFileName));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(fullPath);
        return root is not null && fullPath.Length > root.Length
            ? fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            : fullPath;
    }
}
