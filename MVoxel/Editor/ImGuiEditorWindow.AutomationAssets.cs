using System.Text.Json;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 资产相关的编辑器命令：资产面板右键能做的事与编辑空间状态都从这里暴露给 MCP，
/// 文件操作统一走 ProjectAssetOperations，与界面右键菜单是同一条实现
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>列出项目资产，可按目录、类型筛选并选择是否递归子目录</summary>
    /// <param name="payload">命令参数：folder、kind、recursive</param>
    /// <returns>资产条目列表</returns>
    private object ListProjectAssets(JsonElement payload)
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var folder = ProjectAssetOperations.NormalizeFolder(ReadString(payload, "folder"));
        var kindText = ReadString(payload, "kind");
        ProjectAssetKind? kind = null;
        if (!string.IsNullOrWhiteSpace(kindText))
        {
            // 既接受 ProjectAssetKind 枚举名，也接受 asset.create 用的类型简写
            if (Enum.TryParse<ProjectAssetKind>(kindText, ignoreCase: true, out var parsed)) kind = parsed;
            else if (KindAlias(kindText) is { } alias) kind = alias;
            else throw new ArgumentException($"未知的资产类型：{kindText}");
        }

        var recursive = ReadBoolean(payload, "recursive") ?? false;
        var entries = new List<ProjectAssetEntry>();
        Collect(folder, 0);
        return new
        {
            folder,
            count = entries.Count,
            assets = entries.Select(entry => new
            {
                path = entry.RelativePath,
                name = entry.DisplayName,
                kind = entry.Kind.ToString(),
                kindLabel = ProjectAssetCatalog.KindLabel(entry.Kind),
                isFolder = entry.IsFolder,
            }).ToArray(),
        };

        // 递归深度设上限，避免异常目录结构把命令拖死
        void Collect(string current, int depth)
        {
            foreach (var entry in ProjectAssetCatalog.Children(project, current))
            {
                if (kind is null || entry.Kind == kind) entries.Add(entry);
                if (entry.IsFolder && recursive && depth < 8) Collect(entry.RelativePath, depth + 1);
            }
        }
    }

    /// <summary>把 asset.create 的类型简写映射成资产分类，供 asset.list 的 kind 过滤使用</summary>
    /// <param name="kind">类型简写</param>
    /// <returns>对应分类，未知时返回 null</returns>
    private static ProjectAssetKind? KindAlias(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        ProjectAssetOperations.KindVoxel => ProjectAssetKind.VoxelModel,
        ProjectAssetOperations.KindScene => ProjectAssetKind.Scene,
        ProjectAssetOperations.KindPalette => ProjectAssetKind.Palette,
        ProjectAssetOperations.KindMaterial => ProjectAssetKind.Material,
        ProjectAssetOperations.KindRenderPreset => ProjectAssetKind.RenderPreset,
        ProjectAssetOperations.KindAnimationClip => ProjectAssetKind.AnimationClip,
        ProjectAssetOperations.KindTimeline => ProjectAssetKind.Timeline,
        ProjectAssetOperations.KindDocument or ProjectAssetOperations.KindMarkdown => ProjectAssetKind.Document,
        ProjectAssetOperations.KindGeneratorJavaScript or ProjectAssetOperations.KindGeneratorLua => ProjectAssetKind.Generator,
        _ => null,
    };

    /// <summary>新建资产文件，等同资产面板右键“创建”</summary>
    /// <param name="payload">命令参数：kind、folder、name</param>
    /// <returns>是否创建成功与资产路径</returns>
    private object CreateProjectAsset(JsonElement payload) => RunOnUi(() =>
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var kind = ReadString(payload, "kind") ?? throw new ArgumentException("缺少 kind");
        var folder = ReadString(payload, "folder") ?? string.Empty;
        var result = ProjectAssetOperations.Create(project, kind, folder, ReadString(payload, "name"));
        if (!result.Success) throw new InvalidOperationException(result.Message);
        _assetBrowser.SelectAsset(result.RelativePath);
        HandleAssetChanged(new ProjectAssetChange(result.RelativePath, result.RelativePath));
        var opened = false;
        // open=true 时一步进入后续工作：体素数据进编辑空间，生成器进生成器面板
        if (ReadBoolean(payload, "open") == true)
        {
            if (kind is ProjectAssetOperations.KindVoxel or ProjectAssetOperations.KindScene)
            {
                if (kind == ProjectAssetOperations.KindVoxel) OpenVoxelAssetStage(result.RelativePath);
                opened = IsAssetStage;
            }
            else if (kind is ProjectAssetOperations.KindGeneratorJavaScript or ProjectAssetOperations.KindGeneratorLua)
            {
                OpenGeneratorAsset(result.RelativePath);
                opened = true;
            }
        }

        return new { created = true, path = result.RelativePath, opened, status = _statusText };
    });

    /// <summary>新建资产文件夹</summary>
    /// <param name="payload">命令参数：folder、name</param>
    /// <returns>是否创建成功与文件夹路径</returns>
    private object CreateProjectFolder(JsonElement payload) => RunOnUi(() =>
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var folder = ReadString(payload, "folder") ?? string.Empty;
        var name = ReadString(payload, "name") ?? throw new ArgumentException("缺少 name");
        var result = ProjectAssetOperations.CreateFolder(project, folder, name);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return new { created = true, path = result.RelativePath, status = result.Message };
    });

    /// <summary>重命名资产；name 只给主名时自动保留原扩展名</summary>
    /// <param name="payload">命令参数：path、name</param>
    /// <returns>新路径与引用跟随结果</returns>
    private object RenameProjectAsset(JsonElement payload) => RunOnUi(() =>
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = ReadString(payload, "path") ?? throw new ArgumentException("缺少 path");
        var name = ReadString(payload, "name") ?? throw new ArgumentException("缺少 name");
        var result = ProjectAssetOperations.Rename(project, path, name);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        HandleAssetChanged(new ProjectAssetChange(path, result.RelativePath));
        _assetBrowser.SelectAsset(result.RelativePath);
        return new { renamed = true, path = result.RelativePath, status = _statusText };
    });

    /// <summary>把资产移动到 Assets 下的另一个目录</summary>
    /// <param name="payload">命令参数：path、folder</param>
    /// <returns>新路径与引用跟随结果</returns>
    private object MoveProjectAsset(JsonElement payload) => RunOnUi(() =>
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = ReadString(payload, "path") ?? throw new ArgumentException("缺少 path");
        var folder = ReadString(payload, "folder") ?? throw new ArgumentException("缺少 folder");
        var result = ProjectAssetOperations.Move(project, path, folder);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        HandleAssetChanged(new ProjectAssetChange(path, result.RelativePath));
        _assetBrowser.SelectAsset(result.RelativePath);
        return new { moved = true, path = result.RelativePath, status = _statusText };
    });

    /// <summary>创建资产副本，可指定目标目录</summary>
    /// <param name="payload">命令参数：path、folder</param>
    /// <returns>副本路径</returns>
    private object DuplicateProjectAsset(JsonElement payload) => RunOnUi(() =>
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = ReadString(payload, "path") ?? throw new ArgumentException("缺少 path");
        var result = ProjectAssetOperations.Duplicate(project, path, ReadString(payload, "folder"));
        if (!result.Success) throw new InvalidOperationException(result.Message);
        HandleAssetChanged(new ProjectAssetChange(result.RelativePath, result.RelativePath));
        _assetBrowser.SelectAsset(result.RelativePath);
        return new { created = true, path = result.RelativePath, status = result.Message };
    });

    /// <summary>删除资产文件；引用会保留为失效引用，由界面提示断链</summary>
    /// <param name="payload">命令参数：path</param>
    /// <returns>是否删除成功</returns>
    private object DeleteProjectAsset(JsonElement payload) => RunOnUi(() =>
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = ReadString(payload, "path") ?? throw new ArgumentException("缺少 path");
        var editingPath = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath;
        if (string.Equals(editingPath, path.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("该资产正在编辑空间中打开；请先返回场景，或先另存副本再删除");
        var result = ProjectAssetOperations.Delete(project, path);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        HandleAssetChanged(new ProjectAssetChange(path, null));
        return new { deleted = true, path, status = _statusText };
    });

    /// <summary>查询资产被哪些对象和项目文件引用，删除前用来判断会不会断链</summary>
    /// <param name="payload">命令参数：path</param>
    /// <returns>对象引用名、项目文件引用与是否正在编辑</returns>
    private object DescribeAssetReferences(JsonElement payload)
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = (ReadString(payload, "path") ?? throw new ArgumentException("缺少 path"))
            .Replace('\\', '/').Trim('/');
        var names = new List<string>();
        if (_sceneDocument is not null)
            names.AddRange(AssetReferences.FindReferencingObjects(_sceneDocument, path)
                .Select(item => IsAssetStage ? $"编辑空间：{item.Name}" : item.Name));
        var suspended = _voxelStage?.Scene ?? _prefabStage?.Scene;
        if (suspended is not null)
            names.AddRange(AssetReferences.FindReferencingObjects(suspended, path)
                .Select(item => $"原场景：{item.Name}"));
        var loaded = ProjectAssetDatabase.Load(project.AssetsPath,
            ProjectAssetDatabase.DefaultCachePath(project.RootPath));
        var files = AssetDependencies.ReferencingFiles(loaded.ReferencedBy, path);
        var editing = string.Equals(_voxelStage?.RelativePath ?? _prefabStage?.RelativePath, path,
            StringComparison.OrdinalIgnoreCase);
        return new
        {
            path,
            objectCount = names.Count,
            objects = names,
            fileCount = files.Count,
            files,
            editing,
        };
    }

    /// <summary>读取资产基本信息；体素数据额外给出体素数量、包围盒与对象片段数</summary>
    /// <param name="payload">命令参数：path</param>
    /// <returns>资产信息</returns>
    private object DescribeAssetInfo(JsonElement payload)
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = (ReadString(payload, "path") ?? throw new ArgumentException("缺少 path"))
            .Replace('\\', '/').Trim('/');
        var absolute = ProjectAssetOperations.Resolve(project, path)
            ?? throw new ArgumentException("资产路径无效");
        if (File.Exists(absolute) is false) throw new FileNotFoundException("资产不存在", absolute);
        var file = new FileInfo(absolute);
        var kind = ProjectAssetCatalog.Classify(path, false);
        var info = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["kind"] = kind.ToString(),
            ["kindLabel"] = ProjectAssetCatalog.KindLabel(kind),
            ["bytes"] = file.Length,
            ["modifiedUtc"] = file.LastWriteTimeUtc,
        };
        if (kind == ProjectAssetKind.VoxelModel)
        {
            FillVoxelInfo(info, absolute);
        }

        return info;
    }

    /// <summary>补充体素数量、包围盒与对象片段数；损坏文件只回报状态不抛出</summary>
    private static void FillVoxelInfo(IDictionary<string, object?> info, string absolutePath)
    {
        try
        {
            if (LargeWorldFormat.IsObjectDocument(absolutePath))
            {
                var document = LargeWorldFormat.ReadObjects(absolutePath);
                info["objects"] = document.Objects.Count;
                info["voxels"] = document.Objects.Sum(segment => segment.World.OccupiedVoxelCount);
                info["materials"] = document.Materials.Definitions.Count;
                return;
            }

            var single = LargeWorldFormat.ReadDocument(absolutePath);
            info["objects"] = 1;
            info["voxels"] = single.World.OccupiedVoxelCount;
            info["materials"] = single.Materials.Definitions.Count;
            if (single.World.TryGetBounds(out var minimum, out var maximum))
            {
                info["bounds"] = new { min = new[] { minimum.X, minimum.Y, minimum.Z }, max = new[] { maximum.X, maximum.Y, maximum.Z } };
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            info["readable"] = false;
            info["error"] = exception.Message;
        }
    }

    /// <summary>按内置模板生成项目文件，供模板场景的脚本验收</summary>
    /// <param name="payload">参数：folder 新项目目录，template 模板 Id 或名称</param>
    /// <returns>项目目录与模板 Id</returns>
    private object CreateProjectFromTemplate(JsonElement payload)
    {
        var folder = ReadString(payload, "folder") ?? throw new ArgumentException("缺少 folder");
        var templateText = ReadString(payload, "template") ?? throw new ArgumentException("缺少 template");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException("目标项目目录必须为空");
        var template = MVoxel.Editor.Templates.ProjectTemplateCatalog.All
            .FirstOrDefault(item => string.Equals(item.Id.ToString(), templateText, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(item.Name, templateText, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"未知模板：{templateText}");
        var project = ProjectLayout.Create(Path.GetFullPath(folder), new DirectoryInfo(folder).Name);
        MVoxel.Editor.Templates.ProjectTemplateCatalog.Populate(project, template.Id);
        return new { project = project.RootPath, template = template.Id.ToString() };
    }

    /// <summary>
    /// 按欢迎界面“从 vox 创建项目”的同一路径生成项目文件，供脚本复现与验证该导入流程
    /// </summary>
    /// <param name="payload">参数：folder 新项目目录，vox 源文件路径</param>
    /// <returns>项目目录与导入体素数</returns>
    private object CreateProjectFromVox(JsonElement payload)
    {
        var folder = ReadString(payload, "folder") ?? throw new ArgumentException("缺少 folder");
        var vox = ReadString(payload, "vox") ?? throw new ArgumentException("缺少 vox");
        if (!File.Exists(vox)) throw new FileNotFoundException("vox 文件不存在", vox);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException("目标项目目录必须为空");
        var project = ProjectLayout.Create(Path.GetFullPath(folder), new DirectoryInfo(folder).Name);
        var voxels = MVoxel.Editor.Templates.ProjectTemplateCatalog.PopulateFromVox(project, Path.GetFullPath(vox));
        return new { project = project.RootPath, voxels };
    }

    /// <summary>读取体素资产内的材质定义与槽位绑定，核对导入后的金属度与粗糙度</summary>
    /// <param name="payload">参数：path 为相对 Assets 的路径</param>
    /// <returns>材质定义与绑定列表</returns>
    private object DescribeAssetMaterials(JsonElement payload)
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = (ReadString(payload, "path") ?? throw new ArgumentException("缺少 path"))
            .Replace('\\', '/').Trim('/');
        var absolute = ProjectAssetOperations.Resolve(project, path)
            ?? throw new ArgumentException("资产路径无效");
        if (File.Exists(absolute) is false) throw new FileNotFoundException("资产不存在", absolute);
        var metadata = LargeWorldFormat.ReadMaterials(absolute);
        var definitions = metadata.Definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new Dictionary<string, object?>
            {
                ["id"] = pair.Key,
                ["name"] = pair.Value.Name,
                ["metallic"] = pair.Value.Metallic,
                ["roughness"] = pair.Value.Roughness,
                ["specular"] = pair.Value.Specular,
                ["ior"] = pair.Value.Ior,
                ["opacity"] = pair.Value.Opacity,
                ["emissive"] = pair.Value.EmissiveScale,
            }).ToArray();
        var bindings = metadata.Bindings.OrderBy(pair => pair.Key)
            .Select(pair => new { slot = pair.Key, id = pair.Value }).ToArray();
        return new { path, definitions, bindings };
    }

    /// <summary>把项目内体素资产导出为 .vox，包含 MATL 材质，返回导出结果与输出路径</summary>
    /// <param name="payload">参数：path 为相对 Assets 的路径</param>
    /// <returns>导出状态与 Exports 下的相对路径</returns>
    private object ExportAssetVox(JsonElement payload) => RunOnUi(() =>
    {
        var project = _project ?? throw new InvalidOperationException("请先打开项目");
        var path = (ReadString(payload, "path") ?? throw new ArgumentException("缺少 path"))
            .Replace('\\', '/').Trim('/');
        var exportDirectory = Path.Combine(project.AssetsPath, "Exports");
        ExportVoxAsset(path);
        var exported = Directory.Exists(exportDirectory)
            ? Directory.EnumerateFiles(exportDirectory, "*.vox")
                .Select(file => new FileInfo(file))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        return new
        {
            exported = exported is not null,
            path = exported is null ? string.Empty : "Exports/" + exported.Name,
            status = _statusText,
        };
    });

    /// <summary>把当前编辑的资产另存为副本并切换编辑副本</summary>
    /// <returns>原路径、新路径与状态</returns>
    private object SaveAssetStageCopyFromAutomation() => RunOnUi(() =>
    {
        if (!IsAssetStage) throw new InvalidOperationException("当前不在资产编辑空间");
        var before = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath ?? string.Empty;
        SaveAssetStageCopy();
        var after = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath ?? string.Empty;
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(_statusText);
        return new { saved = true, previous = before, path = after, status = _statusText };
    });

    /// <summary>读取独立编辑空间状态：是否打开、编辑哪种资产、脏标记</summary>
    /// <returns>编辑空间状态</returns>
    private object DescribeAssetStage()
    {
        var stagePath = _voxelStage?.RelativePath ?? _prefabStage?.RelativePath;
        return new
        {
            open = IsAssetStage,
            kind = _prefabStage is not null ? "prefab" : _voxelStage is not null ? "voxel" : string.Empty,
            path = stagePath ?? string.Empty,
            name = IsAssetStage ? ActiveEditingName : string.Empty,
            dirty = _sceneDocumentDirty,
            sceneDirty = HasSuspendedSceneChanges,
            status = _statusText,
        };
    }

    /// <summary>保存当前编辑空间中的资产，等价顶栏“保存资产”与 Ctrl+S</summary>
    /// <returns>是否保存成功与剩余脏标记</returns>
    private object SaveAssetStageFromAutomation() => RunOnUi(() =>
    {
        if (!IsAssetStage) throw new InvalidOperationException("当前不在资产编辑空间");
        var saved = SaveAssetStage();
        return new { saved, dirty = _sceneDocumentDirty, status = _statusText };
    });
}
