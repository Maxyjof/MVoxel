using MVoxel.Editor.Rendering.Vulkan;
using MVoxel.Project;
using MVoxel.Voxels;

namespace MVoxel.Editor.Assets;

/// <summary>
/// 资源浏览器体素世界缩略图缓存：按文件改动时间惰性渲染等轴测预览，
/// 并注册为 ImGui Vulkan 纹理；项目关闭或管线重建时统一释放 GPU 资源
/// 渲染失败按负缓存记录，避免每帧重复读取损坏文件
/// </summary>
internal sealed class ThumbnailCache : IDisposable
{
    private readonly VulkanImGuiRenderer? _renderer;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(nint TextureId, long Length, DateTime ModifiedUtc);

    /// <param name="renderer">ImGui Vulkan 渲染器；CPU 预览模式下可为 null，此时不生成缩略图</param>
    public ThumbnailCache(VulkanImGuiRenderer? renderer)
    {
        _renderer = renderer;
    }

    /// <summary>
    /// 获取体素世界资产的缩略图纹理句柄；尚未渲染或文件已变化时惰性重建
    /// </summary>
    /// <param name="project">当前项目或 null</param>
    /// <param name="relativePath">资产相对路径</param>
    /// <returns>纹理句柄；无法渲染（无渲染器、体素超预算、读取失败）时为 null</returns>
    public nint? GetOrRender(ProjectHandle? project, string relativePath)
    {
        if (_renderer is null || project is null || relativePath.Length == 0)
        {
            return null;
        }

        var absolute = Path.GetFullPath(Path.Combine(project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(absolute))
        {
            return null;
        }

        var info = new FileInfo(absolute);
        var key = $"{project.RootPath}|{relativePath}";
        if (_entries.TryGetValue(key, out var cached))
        {
            if (cached.Length == info.Length && cached.ModifiedUtc == info.LastWriteTimeUtc)
            {
                return cached.TextureId != 0 ? cached.TextureId : null;
            }

            Discard(key);
        }

        var textureId = RenderAndRegister(absolute);
        if (textureId is not null)
        {
            info.Refresh();
            _entries[key] = new Entry(textureId.Value, info.Length, info.LastWriteTimeUtc);
        }
        else
        {
            // 负缓存：记录零句柄与当前时间戳，直到文件变化才重试
            _entries[key] = new Entry(0, info.Length, info.LastWriteTimeUtc);
        }

        return textureId;
    }

    /// <summary>
    /// 清空全部缓存并释放已注册的 ImGui 纹理
    /// </summary>
    public void Reset()
    {
        foreach (var key in _entries.Keys.ToList())
        {
            Discard(key);
        }
    }

    /// <summary>
    /// 渲染世界并注册纹理；体素数超预算或读取失败时返回 null
    /// </summary>
    private nint? RenderAndRegister(string absolutePath)
    {
        try
        {
            VoxelWorld world;
            switch (Path.GetExtension(absolutePath).ToLowerInvariant())
            {
                case ".vox":
                    world = VoxFormat.Read(absolutePath);
                    break;
                case ".mvmodel" or ".mvworld":
                    world = LargeWorldFormat.IsObjectDocument(absolutePath)
                        ? LargeWorldFormat.ReadObjectPreview(absolutePath) : LargeWorldFormat.Read(absolutePath);
                    break;
                default:
                    return null;
            }

            if (world.OccupiedVoxelCount > VoxelThumbnailRenderer.MaximumVoxels)
            {
                return null;
            }

            var pixels = VoxelThumbnailRenderer.RenderRgba(world, 96);
            return _renderer!.RegisterTexture(pixels, 96, 96);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 释放并移除单个缓存条目的纹理
    /// </summary>
    private void Discard(string key)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.TextureId != 0)
        {
            _renderer?.UnregisterTexture(entry.TextureId);
        }

        _entries.Remove(key);
    }

    /// <summary>
    /// 释放全部纹理
    /// </summary>
    public void Dispose()
    {
        Reset();
    }
}
