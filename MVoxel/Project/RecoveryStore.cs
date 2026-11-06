using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 一条可恢复的自动保存快照
/// </summary>
/// <param name="SavedAt">快照写入时间</param>
/// <param name="SceneJson">场景文档 JSON 文本</param>
public sealed record RecoverySnapshot(DateTimeOffset SavedAt, string SceneJson);

/// <summary>
/// 自动保存恢复区：按项目根路径派生独立目录，存放包含对象体素的场景快照，
/// 正式保存成功即清空。崩溃后重新打开时快照不会写回原场景，而是由
/// MaterializeAsScene 物化为同目录下的「&lt;场景名&gt;-恢复快照.mvox」独立文件，
/// 用户最后一次主动保存的场景始终保持不变
/// </summary>
public static class RecoveryStore
{
    private const string SceneFileName = "scene.mvox.json";
    private const string MetaFileName = "meta.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 计算项目的恢复区目录，同名项目以根路径哈希区分
    /// </summary>
    /// <param name="projectRootPath">项目根目录</param>
    /// <returns>恢复区绝对路径</returns>
    public static string DirectoryFor(string projectRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        var full = Path.GetFullPath(projectRootPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant()));
        var tag = Convert.ToHexString(hash.AsSpan(0, 4));
        var name = new string(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character)).ToArray());
        if (name.Length == 0) name = "project";
        return Path.Combine(AppPaths.RecoveryDirectory, $"{name}-{tag}");
    }

    /// <summary>
    /// 写入包含对象体素的场景恢复快照
    /// </summary>
    /// <param name="projectRootPath">项目根目录</param>
    /// <param name="sceneJson">场景文档 JSON 文本</param>
    public static void Write(string projectRootPath, string sceneJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneJson);
        var directory = DirectoryFor(projectRootPath);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteText(Path.Combine(directory, SceneFileName), sceneJson, backupExtension: null);
        AtomicFile.WriteText(Path.Combine(directory, MetaFileName),
            JsonSerializer.Serialize(new Meta(DateTimeOffset.Now), Options), backupExtension: null);
    }

    /// <summary>
    /// 尝试读取项目的恢复快照，读取失败时保留原始恢复数据
    /// </summary>
    /// <param name="projectRootPath">项目根目录</param>
    /// <param name="snapshot">读到的快照</param>
    /// <returns>是否存在有效快照</returns>
    public static bool TryLoad(string projectRootPath, out RecoverySnapshot? snapshot)
    {
        snapshot = null;
        var directory = DirectoryFor(projectRootPath);
        var scenePath = Path.Combine(directory, SceneFileName);
        var metaPath = Path.Combine(directory, MetaFileName);
        if (!File.Exists(scenePath) || !File.Exists(metaPath)) return false;
        try
        {
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), Options);
            snapshot = new RecoverySnapshot(meta?.SavedAt ?? DateTimeOffset.MinValue,
                File.ReadAllText(scenePath));
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            MVoxelLog.Warn($"恢复区快照读取失败，保留原始数据：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把恢复快照物化为原场景旁边的独立场景文件，绝不覆盖用户最后主动保存的场景。
    /// 新文件与源场景同目录、以「&lt;源场景名&gt;-恢复快照.mvox」命名，重名时追加序号；
    /// 场景内部的显示名同步改为「恢复快照」，便于在层级与场景列表里辨认。
    /// </summary>
    /// <param name="assetsPath">项目 Assets 绝对路径</param>
    /// <param name="sceneRelativePath">源场景相对 Assets 的路径，如 Scenes/主场景.mvox</param>
    /// <param name="sceneJson">快照中的场景文档 JSON</param>
    /// <returns>新建场景文件相对 Assets 的路径；写入失败时抛出异常由调用方回退</returns>
    public static string MaterializeAsScene(string assetsPath, string sceneRelativePath, string sceneJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneJson);

        var sourcePath = Path.GetFullPath(Path.Combine(assetsPath,
            sceneRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidDataException("源场景路径没有有效目录");
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(stem)) stem = "主场景";
        // 已经是恢复快照文件时不再叠加后缀，避免每次崩溃产生「恢复快照-恢复快照」
        const string suffix = "-恢复快照";
        if (stem.EndsWith(suffix, StringComparison.Ordinal)) stem = stem[..^suffix.Length];
        if (stem.EndsWith("恢复快照", StringComparison.Ordinal)) stem = "主场景";

        var relative = (string relativePath) => Path.GetRelativePath(assetsPath, relativePath).Replace('\\', '/');
        var target = Path.Combine(directory, stem + suffix + ".mvox");
        for (var index = 2; File.Exists(target); index++)
            target = Path.Combine(directory, $"{stem}{suffix} {index}.mvox");

        var scene = MVoxel.Scene.SceneSerializer.LoadFromJson(sceneJson);
        scene.Name = "恢复快照";
        MVoxel.Scene.SceneSerializer.Save(target, scene);
        MVoxelLog.Info($"已把未保存工作另存为独立场景文件：{relative(target)}（原场景保持不变）");
        return relative(target);
    }

    /// <summary>
    /// 清空项目恢复区，正式保存成功或快照失效后调用
    /// </summary>
    /// <param name="projectRootPath">项目根目录</param>
    public static void Clear(string projectRootPath)
    {
        var directory = DirectoryFor(projectRootPath);
        if (!System.IO.Directory.Exists(directory)) return;
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Warn($"清理恢复区失败：{directory}，{exception.Message}");
        }
    }

    private sealed record Meta(DateTimeOffset SavedAt);
}
