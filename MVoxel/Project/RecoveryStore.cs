using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Project;

/// <summary>
/// 一条可恢复的自动保存快照
/// </summary>
/// <param name="SavedAt">快照写入时间</param>
/// <param name="ScenePath">快照场景文件绝对路径</param>
public sealed record RecoverySnapshot(DateTimeOffset SavedAt, string ScenePath);

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
    private const string DirtyChunkFileName = "dirty-chunks.json";

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
    /// 流式写入包含对象体素的场景恢复快照，避免整份场景先拼成字符串；
    /// 同时记录本次自动保存重新压缩过的区块差异，便于诊断与确认增量编码生效
    /// </summary>
    /// <param name="projectRootPath">项目根目录</param>
    /// <param name="scene">要写入快照的场景文档</param>
    /// <param name="reencodedChunks">本次重新编码了区块的对象与区块坐标；为空表示不写差异日志</param>
    public static void WriteSnapshot(string projectRootPath, MVoxel.Scene.SceneDocument scene,
        IReadOnlyDictionary<StableId, IReadOnlyList<MVoxel.Voxels.ChunkCoord64>>? reencodedChunks = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var directory = DirectoryFor(projectRootPath);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteStream(Path.Combine(directory, SceneFileName),
            stream => MVoxel.Scene.SceneSerializer.Serialize(stream, scene), backupExtension: null);
        AtomicFile.WriteText(Path.Combine(directory, MetaFileName),
            JsonSerializer.Serialize(new Meta(DateTimeOffset.Now), Options), backupExtension: null);
        if (reencodedChunks is not null)
        {
            AtomicFile.WriteText(Path.Combine(directory, DirtyChunkFileName),
                JsonSerializer.Serialize(BuildDirtyLog(scene, reencodedChunks), Options),
                backupExtension: null);
        }
    }

    private sealed record DirtyChunkLog(DateTimeOffset SavedAt, DirtyObjectEntry[] Objects);

    private sealed record DirtyObjectEntry(string Id, string Name, DirtyChunk[] Chunks);

    private sealed record DirtyChunk(long X, long Y, long Z);

    private static DirtyChunkLog BuildDirtyLog(MVoxel.Scene.SceneDocument scene,
        IReadOnlyDictionary<StableId, IReadOnlyList<MVoxel.Voxels.ChunkCoord64>> reencodedChunks)
    {
        var entries = new List<DirtyObjectEntry>(reencodedChunks.Count);
        foreach (var objectEntry in scene.Objects)
        {
            if (!reencodedChunks.TryGetValue(objectEntry.Id, out var chunks) || chunks.Count == 0) continue;
            var mapped = new DirtyChunk[chunks.Count];
            for (var index = 0; index < chunks.Count; index++)
                mapped[index] = new DirtyChunk(chunks[index].X, chunks[index].Y, chunks[index].Z);
            entries.Add(new DirtyObjectEntry(objectEntry.Id.Value.ToString(), objectEntry.Name, mapped));
        }

        return new DirtyChunkLog(DateTimeOffset.Now, entries.ToArray());
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
            snapshot = new RecoverySnapshot(meta?.SavedAt ?? DateTimeOffset.MinValue, scenePath);
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
    /// <param name="snapshotScenePath">恢复区快照场景文件绝对路径</param>
    /// <returns>新建场景文件相对 Assets 的路径；写入失败时抛出异常由调用方回退</returns>
    public static string MaterializeAsScene(string assetsPath, string sceneRelativePath, string snapshotScenePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotScenePath);

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

        var scene = MVoxel.Scene.SceneSerializer.Load(snapshotScenePath);
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
