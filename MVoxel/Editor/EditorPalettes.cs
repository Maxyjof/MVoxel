using MVoxel.Project;
using MVoxel.Voxels;
using MVoxel.Core;

namespace MVoxel.Editor;

/// <summary>
/// 调色板双层管理：编辑器级调色板是全局默认与恢复来源，项目级调色板随项目
/// 文件夹独立演化。打开项目时若项目级缺失，就从编辑器级复制一份
/// </summary>
public static class EditorPalettes
{
    /// <summary>
    /// 项目级调色板在 Assets/Palettes 下的固定文件名
    /// </summary>
    public const string ProjectPaletteName = "主调色板.palette";

    /// <summary>
    /// 编辑器级调色板路径，位于程序目录的 MVoxelData/Config 下
    /// </summary>
    public static string EditorPalettePath => AppPaths.ConfigPath("editor.palette");

    /// <summary>
    /// 解析当前生效的编辑器级调色板：文件缺失或损坏时落回内置默认色板并顺手修复磁盘文件
    /// </summary>
    /// <returns>256 项色表</returns>
    public static Rgba32[] ResolveEditorPalette()
    {
        try
        {
            if (File.Exists(EditorPalettePath))
            {
                return PaletteFormat.Load(EditorPalettePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 损坏的编辑器色板不阻塞启动，用默认值重建
        }

        var fallback = VoxelPalette.Build();
        TrySave(EditorPalettePath, fallback);
        return fallback;
    }

    /// <summary>
    /// 解析项目级调色板路径
    /// </summary>
    /// <param name="project">已打开的项目</param>
    /// <returns>Assets/Palettes/主调色板.palette 的绝对路径</returns>
    public static string ProjectPalettePath(ProjectHandle project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Combine(project.AssetsPath, "Palettes", ProjectPaletteName);
    }

    /// <summary>
    /// 打开项目时解析其调色板：项目级存在则直接读取，缺失则从编辑器级复制一份，
    /// 保证每个项目都拥有一份可独立修改的色板
    /// </summary>
    /// <param name="project">正在打开的项目</param>
    /// <returns>项目生效的 256 项色表</returns>
    public static Rgba32[] ResolveProjectPalette(ProjectHandle project)
    {
        var path = ProjectPalettePath(project);
        try
        {
            if (File.Exists(path))
            {
                return PaletteFormat.Load(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 项目色板损坏时重建，不阻塞打开流程
        }

        var seed = ResolveEditorPalette();
        TrySave(path, seed);
        return seed;
    }

    /// <summary>
    /// 把当前色板写回项目级调色板
    /// </summary>
    /// <param name="project">目标项目</param>
    /// <param name="colors">要保存的色表</param>
    /// <returns>写入成功返回 true，IO 失败返回 false</returns>
    public static bool SaveProjectPalette(ProjectHandle project, Rgba32[] colors) =>
        TrySave(ProjectPalettePath(project), colors);

    /// <summary>
    /// 把当前色板写回编辑器级调色板，作为以后新项目的默认
    /// </summary>
    /// <param name="colors">要保存的色表</param>
    /// <returns>写入成功返回 true，IO 失败返回 false</returns>
    public static bool SaveEditorPalette(Rgba32[] colors) => TrySave(EditorPalettePath, colors);

    private static bool TrySave(string path, Rgba32[] colors)
    {
        try
        {
            PaletteFormat.Save(path, colors);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
