using MVoxel.Core;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>资产文件操作结果：是否成功、相对路径与可直接展示给用户的说明</summary>
/// <param name="Success">是否成功完成</param>
/// <param name="RelativePath">操作后的资产相对路径，失败时为空串</param>
/// <param name="Message">给用户或调用方看的一句话说明</param>
internal readonly record struct AssetOperationResult(bool Success, string RelativePath, string Message)
{
    internal static AssetOperationResult Ok(string relativePath, string message) => new(true, relativePath, message);

    internal static AssetOperationResult Failed(string message) => new(false, string.Empty, message);
}

/// <summary>
/// 项目资产文件的创建、改名、移动与删除，资产面板和编辑器命令共用同一套实现；
/// 这里只处理文件与资源元数据，界面选中、刷新回调和重命名对话框由调用方负责
/// </summary>
internal static class ProjectAssetOperations
{
    /// <summary>创建体素数据 .mvmodel</summary>
    internal const string KindVoxel = "voxel";

    /// <summary>创建场景 .mvox</summary>
    internal const string KindScene = "scene";

    /// <summary>创建调色盘 .palette</summary>
    internal const string KindPalette = "palette";

    /// <summary>创建材质 .material</summary>
    internal const string KindMaterial = "material";

    /// <summary>创建渲染预设 .mvrenderpreset</summary>
    internal const string KindRenderPreset = "renderpreset";

    /// <summary>创建动画片段 .mvanim.json</summary>
    internal const string KindAnimationClip = "animationclip";

    /// <summary>创建时间轴 .mvtimeline.json</summary>
    internal const string KindTimeline = "timeline";

    /// <summary>创建文本文件 .txt</summary>
    internal const string KindDocument = "document";

    /// <summary>创建 Markdown 文档 .md</summary>
    internal const string KindMarkdown = "markdown";

    /// <summary>创建 JavaScript 生成器</summary>
    internal const string KindGeneratorJavaScript = "generator.js";

    /// <summary>创建 Lua 生成器</summary>
    internal const string KindGeneratorLua = "generator.lua";

    /// <summary>全部可创建资产类型，供编辑器命令自行发现</summary>
    internal static readonly string[] CreatableKinds =
    [
        KindVoxel, KindScene, KindPalette, KindMaterial, KindRenderPreset, KindAnimationClip,
        KindTimeline, KindDocument, KindMarkdown, KindGeneratorJavaScript, KindGeneratorLua,
    ];

    /// <summary>解析相对 Assets 的路径，越界或绝对路径返回 null</summary>
    /// <param name="project">当前项目</param>
    /// <param name="relativePath">相对 Assets 的路径</param>
    /// <returns>规范化绝对路径或 null</returns>
    internal static string? Resolve(ProjectHandle? project, string relativePath)
    {
        if (project is null || Path.IsPathRooted(relativePath)) return null;
        var root = Path.GetFullPath(project.AssetsPath);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? path : null;
    }

    /// <summary>拼接相对路径，目录为空时只返回文件名</summary>
    /// <param name="directory">相对目录</param>
    /// <param name="name">文件名</param>
    /// <returns>相对路径</returns>
    internal static string Combine(string? directory, string name) =>
        string.IsNullOrWhiteSpace(directory) ? name : $"{directory.Replace('\\', '/').Trim('/')}/{name}";

    /// <summary>规范化相对目录，去掉首尾斜杠与反斜杠</summary>
    /// <param name="folder">相对目录</param>
    /// <returns>规范化结果</returns>
    internal static string NormalizeFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? string.Empty : folder.Replace('\\', '/').Trim('/');

    /// <summary>在指定目录创建资产；name 缺省时按类型自动编号</summary>
    /// <param name="project">当前项目</param>
    /// <param name="kind">资产类型，见 CreatableKinds</param>
    /// <param name="folder">相对 Assets 的目录</param>
    /// <param name="name">不含扩展名的文件名，调用方已给定时不覆盖同名文件</param>
    /// <returns>操作结果</returns>
    internal static AssetOperationResult Create(ProjectHandle project, string kind, string folder, string? name = null)
    {
        folder = NormalizeFolder(folder);
        var directory = Resolve(project, folder);
        if (directory is null) return AssetOperationResult.Failed("资产目录无效");
        if (!Directory.Exists(directory)) return AssetOperationResult.Failed($"资产目录不存在：{folder}");
        var extension = ExtensionOf(kind);
        if (extension is null) return AssetOperationResult.Failed($"不支持的资产类型：{kind}");
        var baseName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (baseName is not null && (!IsValidName(baseName) ||
                                     baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            return AssetOperationResult.Failed("文件名无效：不能为空、不能含路径分隔符或非法字符，也不要带扩展名");
        string relative;
        try
        {
            relative = baseName is null
                ? UniqueRelativePath(project, folder, DefaultBaseName(kind), extension)
                : Combine(folder, baseName + extension);
        }
        catch (InvalidOperationException exception)
        {
            return AssetOperationResult.Failed($"创建{KindLabelOf(kind)}失败：{exception.Message}");
        }
        if (File.Exists(Resolve(project, relative)) || Directory.Exists(Resolve(project, relative)))
            return AssetOperationResult.Failed($"同名文件已存在：{relative}");
        var absolute = Resolve(project, relative)!;
        try
        {
            var write = PrepareWrite(kind);
            write?.Invoke(absolute, Path.GetFileNameWithoutExtension(absolute), project);
            AssetMetaStore.EnsureMeta(absolute);
            return AssetOperationResult.Ok(relative, $"已创建{KindLabelOf(kind)}：{relative}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException or InvalidDataException
                                              or System.Text.Json.JsonException or ArgumentException)
        {
            return AssetOperationResult.Failed($"创建{KindLabelOf(kind)}失败：{exception.Message}");
        }
    }

    /// <summary>在指定目录创建子文件夹</summary>
    /// <param name="project">当前项目</param>
    /// <param name="folder">父目录</param>
    /// <param name="name">文件夹名称</param>
    /// <returns>操作结果</returns>
    internal static AssetOperationResult CreateFolder(ProjectHandle project, string folder, string name)
    {
        var trimmed = name.Trim();
        if (!IsValidName(trimmed)) return AssetOperationResult.Failed("文件夹名称无效");
        var relative = Combine(NormalizeFolder(folder), trimmed);
        var path = Resolve(project, relative);
        if (path is null) return AssetOperationResult.Failed("文件夹路径无效");
        if (Directory.Exists(path) || File.Exists(path)) return AssetOperationResult.Failed("文件夹已存在或路径无效");
        try
        {
            FileEditHistory.CreateDirectory(path);
            return AssetOperationResult.Ok(relative, $"已创建文件夹：{relative}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return AssetOperationResult.Failed($"创建文件夹失败：{exception.Message}");
        }
    }

    /// <summary>重命名资产；name 只给主名时自动补回原扩展名，复合扩展名整体保留</summary>
    /// <param name="project">当前项目</param>
    /// <param name="relativePath">原相对路径</param>
    /// <param name="name">新名称，可以是主名也可以是完整文件名</param>
    /// <returns>操作结果</returns>
    internal static AssetOperationResult Rename(ProjectHandle project, string relativePath, string name)
    {
        relativePath = relativePath.Replace('\\', '/').Trim('/');
        var extension = SplitName(Path.GetFileName(relativePath)).Extension;
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return AssetOperationResult.Failed("重命名失败：名称不能为空");
        if (extension.Length > 0 && trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^extension.Length];
        var newName = trimmed + extension;
        if (!IsValidName(newName)) return AssetOperationResult.Failed("重命名失败：文件名无效");
        var newRelative = Combine(Path.GetDirectoryName(relativePath), newName);
        if (string.Equals(newRelative, relativePath, StringComparison.OrdinalIgnoreCase))
            return AssetOperationResult.Ok(relativePath, $"名称未变化：{newName}");
        var oldPath = Resolve(project, relativePath);
        var newPath = Resolve(project, newRelative);
        if (oldPath is null || newPath is null || !File.Exists(oldPath))
            return AssetOperationResult.Failed("重命名失败：文件不存在或路径无效");
        if (File.Exists(newPath) || Directory.Exists(newPath))
            return AssetOperationResult.Failed("重命名失败：目标名称已存在");
        try
        {
            FileEditHistory.Move(oldPath, newPath);
            AssetMetaStore.MoveMeta(oldPath, newPath);
            return AssetOperationResult.Ok(newRelative, $"已重命名文件：{Path.GetFileName(relativePath)} → {newName}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return AssetOperationResult.Failed($"重命名失败：{exception.Message}");
        }
    }

    /// <summary>拆分主名与扩展名，复合扩展名（如 .mvanim.json）整体保留</summary>
    /// <param name="fileName">完整文件名</param>
    /// <returns>主名与含点扩展名，无扩展名时后者为空串</returns>
    internal static (string Base, string Extension) SplitName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            var inner = Path.GetExtension(baseName);
            if (inner.Length > 1)
            {
                extension = inner + extension;
                baseName = Path.GetFileNameWithoutExtension(baseName);
            }
        }

        return (baseName, extension);
    }

    /// <summary>把资产移动到 Assets 下的另一个目录；目标同名文件存在即拒绝</summary>
    /// <param name="project">当前项目</param>
    /// <param name="relativePath">原相对路径</param>
    /// <param name="targetFolder">目标目录</param>
    /// <returns>操作结果</returns>
    internal static AssetOperationResult Move(ProjectHandle project, string relativePath, string targetFolder)
    {
        relativePath = relativePath.Replace('\\', '/').Trim('/');
        var newRelative = Combine(NormalizeFolder(targetFolder), Path.GetFileName(relativePath));
        if (string.Equals(newRelative, relativePath, StringComparison.OrdinalIgnoreCase))
            return AssetOperationResult.Ok(relativePath, "资产已在该目录，无需移动");
        var oldPath = Resolve(project, relativePath);
        var newPath = Resolve(project, newRelative);
        if (oldPath is null || newPath is null || !File.Exists(oldPath))
            return AssetOperationResult.Failed("移动失败：文件不存在或路径无效");
        if (File.Exists(newPath) || Directory.Exists(newPath))
            return AssetOperationResult.Failed("移动失败：目标位置已存在同名文件");
        try
        {
            FileEditHistory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            FileEditHistory.Move(oldPath, newPath);
            AssetMetaStore.MoveMeta(oldPath, newPath);
            return AssetOperationResult.Ok(newRelative, $"已移动：{relativePath} → {newRelative}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return AssetOperationResult.Failed($"移动失败：{exception.Message}");
        }
    }

    /// <summary>删除资产文件</summary>
    /// <param name="project">当前项目</param>
    /// <param name="relativePath">相对路径</param>
    /// <returns>操作结果</returns>    /// <summary>创建资产副本；新文件生成独立的资源元数据，便于后续单独改名</summary>
    /// <param name="project">当前项目</param>
    /// <param name="relativePath">原资产相对路径</param>
    /// <param name="folder">目标目录，缺省与原资产同目录</param>
    /// <returns>操作结果，成功时为副本路径</returns>
    internal static AssetOperationResult Duplicate(ProjectHandle project, string relativePath, string? folder = null)
    {
        relativePath = relativePath.Replace('\\', '/').Trim('/');
        var source = Resolve(project, relativePath);
        if (source is null || !File.Exists(source)) return AssetOperationResult.Failed("复制失败：文件不存在或路径无效");
        var targetFolder = string.IsNullOrWhiteSpace(folder)
            ? Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty
            : NormalizeFolder(folder);
        var directory = Resolve(project, targetFolder);
        if (directory is null || !Directory.Exists(directory)) return AssetOperationResult.Failed("复制失败：目标目录不存在");
        var (baseName, extension) = SplitName(Path.GetFileName(relativePath));
        string relative;
        try
        {
            relative = UniqueRelativePath(project, targetFolder, baseName + " 副本", extension);
        }
        catch (InvalidOperationException exception)
        {
            return AssetOperationResult.Failed($"复制失败：{exception.Message}");
        }

        var target = Resolve(project, relative)!;
        try
        {
            File.Copy(source, target, overwrite: false);
            AssetMetaStore.EnsureMeta(target);
            return AssetOperationResult.Ok(relative, $"已创建副本：{relative}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return AssetOperationResult.Failed($"复制失败：{exception.Message}");
        }
    }

    internal static AssetOperationResult Delete(ProjectHandle project, string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/').Trim('/');
        var path = Resolve(project, relativePath);
        if (path is null || !File.Exists(path)) return AssetOperationResult.Failed("删除失败：文件不存在或路径无效");
        try
        {
            FileEditHistory.Delete(path);
            AssetMetaStore.DeleteMeta(path);
            return AssetOperationResult.Ok(relativePath, $"已删除文件：{Path.GetFileName(relativePath)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AssetOperationResult.Failed($"删除失败：{exception.Message}");
        }
    }

    /// <summary>按类型返回写入动作，path 为目标文件、baseName 为不含扩展名的名称、project 为当前项目</summary>
    private static Action<string, string, ProjectHandle>? PrepareWrite(string kind) => kind switch
    {
        KindVoxel => (path, _, _) => LargeWorldFormat.WriteEmpty(path),
        KindScene => (path, baseName, _) => SceneSerializer.Save(path, new SceneDocument { Name = baseName }),
        KindPalette => (path, _, project) => PaletteFormat.Save(path, EditorPalettes.ResolveProjectPalette(project)),
        KindMaterial => (path, baseName, _) => MaterialAsset.CreateDefault(baseName).Save(path),
        KindRenderPreset => (path, _, _) => new MVoxel.Rendering.RenderSettings().SaveTo(path),
        KindAnimationClip => (path, _, _) => AtomicFile.WriteText(path, System.Text.Json.JsonSerializer.Serialize(
            new MVoxel.Animation.AnimationClip(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true })),
        KindTimeline => (path, _, _) => WriteTimeline(path),
        KindDocument => (path, _, _) => AtomicFile.WriteText(path, string.Empty),
        KindMarkdown => (path, _, _) => AtomicFile.WriteText(path, string.Empty),
        KindGeneratorJavaScript => (path, _, _) => WriteGenerator(path, lua: false),
        KindGeneratorLua => (path, _, _) => WriteGenerator(path, lua: true),
        _ => null,
    };

    private static void WriteTimeline(string path)
    {
        var timeline = new MVoxel.Animation.TimelineAsset();
        timeline.Normalize();
        AtomicFile.WriteText(path, System.Text.Json.JsonSerializer.Serialize(timeline,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteGenerator(string path, bool lua) =>
        new GeneratorAsset
        {
            Language = lua ? GeneratorAsset.LuaLanguage : GeneratorAsset.JavaScriptLanguage,
            Source = lua ? GeneratorAsset.LuaTemplate : GeneratorAsset.JavaScriptTemplate,
        }.Save(path);

    /// <summary>资产类型的默认名称</summary>
    private static string DefaultBaseName(string kind) => kind switch
    {
        KindVoxel => "新体素数据",
        KindScene => "新场景",
        KindPalette => "新调色盘",
        KindMaterial => "新材质",
        KindRenderPreset => "新渲染预设",
        KindAnimationClip => "新动画片段",
        KindTimeline => "新时间轴",
        KindDocument => "新文本文件",
        KindMarkdown => "新文档",
        KindGeneratorJavaScript or KindGeneratorLua => "生成器",
        _ => "新资产",
    };

    /// <summary>资产类型的扩展名</summary>
    private static string? ExtensionOf(string kind) => kind switch
    {
        KindVoxel => ".mvmodel",
        KindScene => ".mvox",
        KindPalette => ".palette",
        KindMaterial => ".material",
        KindRenderPreset => ".mvrenderpreset",
        KindAnimationClip => ".mvanim.json",
        KindTimeline => ".mvtimeline.json",
        KindDocument => ".txt",
        KindMarkdown => ".md",
        KindGeneratorJavaScript or KindGeneratorLua => ".mvgenerator",
        _ => null,
    };

    /// <summary>资产类型的中文名，用于提示与命令说明</summary>
    internal static string KindLabelOf(string kind) => kind switch
    {
        KindVoxel => "体素数据",
        KindScene => "场景",
        KindPalette => "调色盘",
        KindMaterial => "材质",
        KindRenderPreset => "渲染预设",
        KindAnimationClip => "动画片段",
        KindTimeline => "时间轴",
        KindDocument => "文本文件",
        KindMarkdown => "Markdown 文档",
        KindGeneratorJavaScript => "JavaScript 生成器",
        KindGeneratorLua => "Lua 生成器",
        _ => "资产",
    };

    /// <summary>生成不与现有文件冲突的相对路径，默认名后按序号递增</summary>
    private static string UniqueRelativePath(ProjectHandle project, string folder, string baseName, string extension)
    {
        var index = 0;
        while (true)
        {
            var name = index == 0 ? baseName : $"{baseName} {index}";
            var relative = Combine(folder, name + extension);
            var absolute = Resolve(project, relative);
            if (absolute is not null && !File.Exists(absolute) && !Directory.Exists(absolute)) return relative;
            index++;
            if (index > 9999) throw new InvalidOperationException("同名资产过多，无法自动命名");
        }
    }

    /// <summary>文件名是否合法：非空、无路径分隔符与非法字符、不以点或空格结尾</summary>
    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (Path.GetFileName(name) != name) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return !name.EndsWith('.') && !name.EndsWith(' ');
    }
}
