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
/// <param name="WorldModelPath">恢复用体素模型文件路径</param>
public sealed record RecoverySnapshot(DateTimeOffset SavedAt, string SceneJson, string WorldModelPath);

/// <summary>
/// 自动保存恢复区：按项目根路径派生独立目录，存放场景文本与体素模型副本，
/// 正式保存成功即清空；恢复区永不覆盖用户的正式保存文件
/// </summary>
public static class RecoveryStore
{
    private const string SceneFileName = "scene.mvox.json";
    private const string WorldFileName = "world.mvmodel";
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
    /// 恢复区内世界模型快照的目标路径，调用方可直接把当前体素世界写入该文件
    /// </summary>
    /// <param name="projectRootPath">项目根目录</param>
    /// <returns>恢复用 mvmodel 路径</returns>
    public static string WorldFilePath(string projectRootPath)
    {
        var directory = DirectoryFor(projectRootPath);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, WorldFileName);
    }

    /// <summary>
    /// 写入一份恢复快照：场景 JSON 与体素模型文件，原子替换旧快照
    /// </summary>
    /// <param name="projectRootPath">项目根目录</param>
    /// <param name="sceneJson">场景文档 JSON 文本</param>
    /// <param name="worldModelSource">当前体素模型文件路径，可为空表示无模型</param>
    public static void Write(string projectRootPath, string sceneJson, string? worldModelSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneJson);
        var directory = DirectoryFor(projectRootPath);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteText(Path.Combine(directory, SceneFileName), sceneJson, backupExtension: null);
        if (!string.IsNullOrWhiteSpace(worldModelSource) && File.Exists(worldModelSource))
        {
            File.Copy(worldModelSource, Path.Combine(directory, WorldFileName), overwrite: true);
        }

        AtomicFile.WriteText(Path.Combine(directory, MetaFileName),
            JsonSerializer.Serialize(new Meta(DateTimeOffset.Now), Options), backupExtension: null);
    }

    /// <summary>
    /// 尝试读取项目的恢复快照，缺文件视为损坏并整体丢弃
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
            var worldPath = Path.Combine(directory, WorldFileName);
            snapshot = new RecoverySnapshot(meta?.SavedAt ?? DateTimeOffset.MinValue,
                File.ReadAllText(scenePath), File.Exists(worldPath) ? worldPath : null!);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Clear(projectRootPath);
            return false;
        }
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
