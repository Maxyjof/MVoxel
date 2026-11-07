using ImGuiNET;
using System.Drawing;
using System.Diagnostics;
using System.Text.Json;
using MVoxel.Animation;
using MVoxel.Editor.Assets;
using MVoxel.Project;
using MVoxel.Scene;
using MVoxel.Voxels;
using MVoxel.Rendering;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MVoxel.Editor;

/// <summary>
/// 数据驱动的单栏资源浏览器：顶部显示当前位置和返回上级，正文按类型呈现资产卡片，
/// 配全局搜索与类型筛选；目录通过点击文件夹进入，不再占用左侧目录树
/// 目录遍历拒绝符号链接与 Assets 根之外的路径
/// </summary>
internal sealed class ProjectAssetBrowser
{
    private string _selectedFolder = string.Empty;
    private string _selectedAsset = string.Empty;
    private string _searchText = string.Empty;
    private string _newFolderName = string.Empty;
    private ProjectAssetKind? _filter;
    private bool _searchAll = true;
    private float _tileSize = 76F;
    private string? _renamePath;

    /// <summary>体素世界缩略图缓存；由编辑器窗口在管线初始化后注入</summary>
    internal ThumbnailCache? Thumbnails { get; set; }

    /// <summary>项目级反向依赖映射缓存；资产变更或项目切换后失效</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _referencedBy;

    /// <summary>反向依赖映射对应的项目根路径</summary>
    private string? _referencedByRoot;

    /// <summary>待移动资产的相对路径</summary>
    private string? _movePath;

    /// <summary>移动对话框中的目标文件夹（相对 Assets）</summary>
    private string _moveTargetFolder = string.Empty;

    /// <summary>移动对话框是否打开</summary>
    private string _renameName = string.Empty;

    /// <summary>重命名对话框中被改名的完整文件名，用于保留原扩展名</summary>
    private string _renameSourceName = string.Empty;

    /// <summary>重命名对话框下次打开时是否把键盘焦点交给名称输入框</summary>
    private bool _focusRenameInput;

    /// <summary>工具栏“新建”按钮请求打开创建菜单</summary>
    private bool _openCreateMenu;
    private string? _deletePath;
    private bool _openRenameDialog;
    private bool _openDeleteDialog;
    private bool _openMoveDialog;

    internal string SelectedAssetPath => _selectedAsset;
    internal string DraggingVoxelAssetPath { get; private set; } = string.Empty;

    /// <summary>当前正在独立编辑空间中打开的资产相对路径，用于在平铺里标出“编辑中”</summary>
    internal string EditingAssetPath { get; set; } = string.Empty;

    internal void SelectAsset(string relativePath)
    {
        _selectedAsset = relativePath.Replace('\\', '/');
        _selectedFolder = Path.GetDirectoryName(_selectedAsset)?.Replace('\\', '/') ?? string.Empty;
        _searchText = string.Empty;
        _filter = null;
    }

    /// <summary>
    /// 清空选择状态（切换项目或手动刷新时调用）
    /// </summary>
    internal void Reset()
    {
        _selectedFolder = string.Empty;
        _selectedAsset = string.Empty;
        _referencedBy = null;
    }

    /// <summary>
    /// 绘制整个浏览器。project 为 null 时展示新项目的标准目录结构
    /// </summary>
    /// <param name="project">当前项目或 null</param>
    /// <param name="reportStatus">向状态栏汇报的回调</param>
    /// <param name="assetChanged">资产文件变更回调</param>
    /// <param name="openWorldRequest">把体素文件打开为当前世界的回调</param>
    /// <param name="instantiatePrefabRequest">把预制体文件实例化到场景的回调</param>
    /// <param name="exportVoxRequest">把体素世界导出为 .vox 的回调</param>
    /// <param name="exportQubicleRequest">把体素世界导出为 .qb 的回调</param>
    internal void Draw(ProjectHandle? project, Action<string> reportStatus, Action<ProjectAssetChange> assetChanged,
        Action<string>? openWorldRequest = null, Action<string>? instantiatePrefabRequest = null,
        Action<string>? exportVoxRequest = null, Action<string>? openGeneratorRequest = null,
        Action<string>? exportQubicleRequest = null, Func<string, int>? countReferences = null,
        Func<string, IReadOnlyList<string>>? findReferenceNames = null, Action<string>? openPrefabRequest = null,
        Action<string>? instantiateVoxelRequest = null, Action<string>? openTimelineRequest = null)
    {
        if (!ProjectAssetCatalog.FolderExists(project, _selectedFolder))
        {
            Reset();
        }

        // 任何资产变更都使项目级依赖映射失效，删除确认据此重新扫描
        Action<ProjectAssetChange> trackedChange = item =>
        {
            _referencedBy = null;
            assetChanged(item);
        };

        DrawToolbar(project);
        if (_openCreateMenu)
        {
            ImGui.OpenPopup("##新建资产菜单");
            _openCreateMenu = false;
        }
        if (project is not null && ImGui.BeginPopup("##新建资产菜单"))
        {
            DrawCreateAssetMenu(project, _selectedFolder, reportStatus, trackedChange, openGeneratorRequest);
            ImGui.EndPopup();
        }
        ImGui.Separator();

        var bodyHeight = Math.Max(1F, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild("##资源平铺内容", new Vector2(0F, bodyHeight), ImGuiChildFlags.Borders);
        DrawGrid(project, reportStatus, trackedChange, openWorldRequest, instantiatePrefabRequest, exportVoxRequest, openGeneratorRequest, exportQubicleRequest, countReferences, openPrefabRequest, instantiateVoxelRequest, openTimelineRequest);
        if (_openRenameDialog)
        {
            ImGui.OpenPopup("##资源重命名");
            _openRenameDialog = false;
        }
        if (_openDeleteDialog)
        {
            ImGui.OpenPopup("##资源删除确认");
            _openDeleteDialog = false;
        }
        if (_openMoveDialog)
        {
            ImGui.OpenPopup("##资源移动");
            _openMoveDialog = false;
        }
        DrawAssetDialogs(project, reportStatus, trackedChange, countReferences, findReferenceNames);
        ImGui.EndChild();

    }

    private void DrawToolbar(ProjectHandle? project)
    {
        ImGui.TextColored(ToColor(EditorPalette.Text), BuildBreadcrumb(project));
        // 资产编辑空间里底部工具条只负责导航：新建入口只在右键菜单里，
        // 避免编辑资产时误在资产目录里再建一份
        if (project is not null && string.IsNullOrEmpty(EditingAssetPath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("新建")) _openCreateMenu = true;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("在当前文件夹创建资产，与右键“创建”菜单一致");
        }
        if (!string.IsNullOrEmpty(_selectedFolder))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("返回上级")) ActivateFolder(ParentFolder(_selectedFolder));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("返回上一级资产文件夹");
        }

        var filterLabel = FilterLabel(_filter);
        var filterWidth = Math.Max(156F, ImGui.CalcTextSize(filterLabel).X + ImGui.GetFrameHeight() + 20F);
        var scopeWidth = Math.Max(52F, ImGui.CalcTextSize(_searchAll ? "全局" : "当前").X + 20F);
        var searchWidth = Math.Max(176F, ImGui.CalcTextSize("搜索资产…").X + 72F);
        var sliderWidth = Math.Max(ImGui.GetFontSize() * 7F, ImGui.CalcTextSize("112").X + ImGui.GetFrameHeight() * 2F);
        var left = ImGui.GetCursorPosX();
        var right = left + ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var toolbarWidth = filterWidth + scopeWidth + searchWidth + sliderWidth + spacing * 3F;
        var previousRight = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
        if (right - previousRight >= toolbarWidth + spacing)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(right - toolbarWidth);
        }
        else if (right - left >= toolbarWidth)
        {
            ImGui.SetCursorPosX(right - toolbarWidth);
        }

        void SameLineWhenFits(float width)
        {
            var itemRight = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
            if (itemRight + spacing + width <= right) ImGui.SameLine();
        }

        if (ImGui.Button(_searchAll ? "全局" : "当前", new Vector2(scopeWidth, 0F)))
        {
            _searchAll = !_searchAll;
        }

        SameLineWhenFits(filterWidth);
        ImGui.SetNextItemWidth(filterWidth);
        if (ImGui.BeginCombo("##资源类型筛选", filterLabel))
        {
            if (ImGui.Selectable("全部类型", _filter is null)) _filter = null;
            foreach (var kind in ProjectAssetCatalog.FilterKinds)
            {
                if (ImGui.Selectable(ProjectAssetCatalog.KindLabel(kind), _filter == kind)) _filter = kind;
            }

            ImGui.EndCombo();
        }

        SameLineWhenFits(searchWidth);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##资源搜索", "搜索资产…", ref _searchText, 256, ImGuiInputTextFlags.AutoSelectAll);

        SameLineWhenFits(sliderWidth);
        ImGui.SetNextItemWidth(sliderWidth);
        ImGui.SliderFloat("##资源图标大小", ref _tileSize, 48F, 112F, "%.0f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("调整资产图标大小");
    }

    private void DrawGrid(ProjectHandle? project, Action<string> reportStatus, Action<ProjectAssetChange> assetChanged,
        Action<string>? openWorldRequest = null, Action<string>? instantiatePrefabRequest = null,
        Action<string>? exportVoxRequest = null, Action<string>? openGeneratorRequest = null,
        Action<string>? exportQubicleRequest = null, Func<string, int>? countReferences = null, Action<string>? openPrefabRequest = null,
        Action<string>? instantiateVoxelRequest = null, Action<string>? openTimelineRequest = null)
    {
        if (project is not null)
        {
            if (ImGui.BeginPopupContextWindow("##资产空白菜单", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
            {
                DrawCreateAssetMenu(project, _selectedFolder, reportStatus, assetChanged, openGeneratorRequest);
                ImGui.EndPopup();
            }
        }
        var entries = CollectEntries(project);
        if (entries.Count == 0)
        {
            ImGui.Dummy(new Vector2(0F, 14F));
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                ImGui.TextDisabled("此文件夹暂时没有资产。");
                var description = ProjectAssetCatalog.FolderDescription(_selectedFolder);
                if (description.Length > 0) ImGui.TextWrapped(description);
            }
            else
            {
                ImGui.TextDisabled("没有找到符合条件的资产。");
            }
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        // 平铺始终按可见宽度换行；窄窗口至少容纳一列，不产生横向滚动。
        var tileWidth = Math.Min(Math.Max(1F, available.X), Math.Max(80F, _tileSize + 28F));
        var tileHeight = _tileSize + 46F;
        var columns = Math.Max(1, (int)((available.X + 6F) / (tileWidth + 6F)));
        var draw = ImGui.GetWindowDrawList();

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (index % columns != 0) ImGui.SameLine(0F, 6F);
            ImGui.PushID(entry.RelativePath);
            var selected = _selectedAsset == entry.RelativePath;
            var activated = ImGui.Selectable("##资源平铺项", selected, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(tileWidth, tileHeight));
            // 必须在 Selectable 返回后立即读取悬停和双击状态。后面的右键菜单、图标绘制
            // 都会改变 ImGui 的 last-item，上移到后面读取会导致文件夹双击偶发失效。
            var itemHovered = ImGui.IsItemHovered();
            var doubleClicked = itemHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
            if (activated)
            {
                _selectedAsset = entry.RelativePath;
                reportStatus($"已选择{ProjectAssetCatalog.KindLabel(entry.Kind)}：{entry.DisplayName}");
            }

            // 文件夹是导航项，不要求用户先选中再双击。单击立即进入，双击自然保持
            // 幂等；这也修复了编辑器刚打开时第一次点击只留下选中态、没有切换目录的问题。
            if (entry.IsFolder && activated)
            {
                ActivateFolder(entry.RelativePath);
                reportStatus($"已打开文件夹：{entry.DisplayName}");
            }
            else if (!entry.IsFolder && entry.Kind == ProjectAssetKind.VoxelModel &&
                     doubleClicked)
            {
                openWorldRequest?.Invoke(entry.RelativePath);
            }
            else if (!entry.IsFolder && entry.Kind == ProjectAssetKind.Prefab &&
                     doubleClicked)
            {
                openPrefabRequest?.Invoke(entry.RelativePath);
            }
            else if (!entry.IsFolder && entry.Kind == ProjectAssetKind.Generator &&
                     doubleClicked)
            {
                openGeneratorRequest?.Invoke(entry.RelativePath);
            }
            else if (!entry.IsFolder && entry.Kind == ProjectAssetKind.Timeline &&
                     doubleClicked)
            {
                openTimelineRequest?.Invoke(entry.RelativePath);
            }
            else if (!entry.IsFolder && doubleClicked)
            {
                // 其余类型没有独立编辑空间：有系统关联的直接打开，材质与场景给出明确去处
                switch (entry.Kind)
                {
                    case ProjectAssetKind.Material:
                        reportStatus("材质资产：在右侧检查器中修改参数后保存");
                        break;
                    case ProjectAssetKind.Scene:
                        reportStatus("场景资产：用菜单“文件 > 打开场景”切换");
                        break;
                    default:
                        OpenWithShell(project, entry.RelativePath, reportStatus);
                        break;
                }
            }

            if (!entry.IsFolder && entry.Kind == ProjectAssetKind.VoxelModel &&
                (entry.RelativePath.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase) ||
                 entry.RelativePath.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase)) &&
                ImGui.BeginDragDropSource())
            {
                DraggingVoxelAssetPath = entry.RelativePath;
                unsafe
                {
                    var marker = 1;
                    ImGui.SetDragDropPayload("MVoxelVoxelAsset", (nint)(&marker), sizeof(int));
                }
                ImGui.TextUnformatted(entry.DisplayName);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginPopupContextItem("##资源右键菜单"))
            {
                _selectedAsset = entry.RelativePath;
                if (ImGui.MenuItem("打开"))
                {
                    if (entry.IsFolder)
                    {
                        _selectedFolder = entry.RelativePath;
                        _selectedAsset = string.Empty;
                    }
                    else if (entry.Kind == ProjectAssetKind.VoxelModel) openWorldRequest?.Invoke(entry.RelativePath);
                    else if (entry.Kind == ProjectAssetKind.Prefab) openPrefabRequest?.Invoke(entry.RelativePath);
                    else if (entry.Kind == ProjectAssetKind.Generator) openGeneratorRequest?.Invoke(entry.RelativePath);
                    else if (entry.Kind == ProjectAssetKind.Timeline) openTimelineRequest?.Invoke(entry.RelativePath);
                    else OpenWithShell(project, entry.RelativePath, reportStatus);
                }

                if (!entry.IsFolder && entry.Kind == ProjectAssetKind.VoxelModel &&
                    (entry.RelativePath.EndsWith(".mvmodel", StringComparison.OrdinalIgnoreCase) ||
                     entry.RelativePath.EndsWith(".mvworld", StringComparison.OrdinalIgnoreCase)) &&
                    ImGui.MenuItem("添加引用到场景"))
                {
                    instantiateVoxelRequest?.Invoke(entry.RelativePath);
                }

                if (!entry.IsFolder && entry.Kind == ProjectAssetKind.VoxelModel &&
                    ImGui.MenuItem("导出为 .vox…"))
                {
                    exportVoxRequest?.Invoke(entry.RelativePath);
                }

                if (!entry.IsFolder && entry.Kind == ProjectAssetKind.VoxelModel &&
                    ImGui.MenuItem("导出为 .qb…"))
                {
                    exportQubicleRequest?.Invoke(entry.RelativePath);
                }

                if (!entry.IsFolder && entry.Kind == ProjectAssetKind.Prefab &&
                    ImGui.MenuItem("实例化到场景"))
                {
                    instantiatePrefabRequest?.Invoke(entry.RelativePath);
                }
                if (!entry.IsFolder && entry.Kind == ProjectAssetKind.Generator &&
                    ImGui.MenuItem("在生成器面板打开"))
                {
                    openGeneratorRequest?.Invoke(entry.RelativePath);
                }
                if (entry.IsFolder && project is not null)
                    DrawCreateAssetMenu(project, entry.RelativePath, reportStatus, assetChanged, openGeneratorRequest);
                if (ImGui.MenuItem("在资源管理器中打开"))
                    RevealInExplorer(project, entry.RelativePath, reportStatus);
                if (!entry.IsFolder)
                {
                    if (ImGui.MenuItem("重命名"))
                        BeginRename(entry.RelativePath);
                    if (ImGui.MenuItem("创建副本"))
                    {
                        var result = ProjectAssetOperations.Duplicate(project!, entry.RelativePath);
                        reportStatus(result.Message);
                        if (result.Success)
                        {
                            _selectedAsset = result.RelativePath;
                            assetChanged(new ProjectAssetChange(result.RelativePath, result.RelativePath));
                            BeginRenameCreatedAsset(result.RelativePath);
                        }
                    }
                    if (ImGui.MenuItem("移动到文件夹…"))
                    {
                        _movePath = entry.RelativePath;
                        _moveTargetFolder = Path.GetDirectoryName(entry.RelativePath)?.Replace('\\', '/') ?? string.Empty;
                        _openMoveDialog = true;
                    }
                    if (ImGui.MenuItem("删除"))
                    {
                        _deletePath = entry.RelativePath;
                        _referencedBy = null;
                        _openDeleteDialog = true;
                    }
                }
                if (ImGui.MenuItem("复制相对路径"))
                {
                    ImGui.SetClipboardText(entry.RelativePath);
                    reportStatus($"已复制相对路径：{entry.RelativePath}");
                }
                if (ImGui.MenuItem("复制绝对路径"))
                {
                    var absolutePath = ResolveAssetPath(project, entry.RelativePath);
                    if (absolutePath is not null)
                    {
                        ImGui.SetClipboardText(absolutePath);
                        reportStatus($"已复制绝对路径：{absolutePath}");
                    }
                }
                ImGui.EndPopup();
            }

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var hovered = ImGui.IsItemHovered();
            if (hovered) ImGui.SetTooltip(DescribeAssetAction(entry));
            // 每个资产都铺一层卡片底，鼠标悬停与选中再叠加高亮，列表看起来更有层次
            var cardBackground = selected ? EditorPalette.AccentSoft : EditorPalette.PanelHeader;
            draw.AddRectFilled(min, max, ToPackedColor(Color.FromArgb(selected || hovered ? 235 : 165,
                cardBackground.R, cardBackground.G, cardBackground.B)), 5F);
            if (selected) draw.AddRect(min, max, ToPackedColor(EditorPalette.Accent), 5F, ImDrawFlags.None, 1.2F);
            else if (hovered) draw.AddRect(min, max, ToPackedColor(EditorPalette.AccentSoft), 5F, ImDrawFlags.None, 1F);

            var iconSize = Math.Min(_tileSize, tileWidth - 18F);
            var iconMin = new Vector2(min.X + (tileWidth - iconSize) / 2F, min.Y + 8F);
            // 图标底板用类型主色淡淡的铺一层，不同类型的资产一眼可分
            var accent = AssetIconPainter.KindColor(entry.Kind, selected);
            draw.AddRectFilled(iconMin - new Vector2(3F, 3F), iconMin + new Vector2(iconSize + 3F, iconSize + 3F),
                ToPackedColor(Color.FromArgb(46, accent.R, accent.G, accent.B)), 5F);
            // 类型色条：卡片顶部一条细横线，快速区分文件夹与各类资产
            draw.AddRectFilled(new Vector2(min.X + 6F, min.Y + 3F), new Vector2(max.X - 6F, min.Y + 5F),
                ToPackedColor(Color.FromArgb(210, accent.R, accent.G, accent.B)), 1F);
            // 目前统一用按类型的程序化图标：CPU 缩略图上传的纹理是空白块，
            // 画出来反而看不出资产类型；等缩略图链路修好再切回贴图预览
            AssetIconPainter.Draw(draw, entry.Kind, iconMin, iconSize, selected);

            DrawCenteredText(draw, entry.DisplayName, min.X, tileWidth, iconMin.Y + iconSize + 4F, EditorPalette.Text);
            var editing = IsEditingAsset(entry.RelativePath);
            var kindLabel = ProjectAssetCatalog.KindLabel(entry.Kind) + (editing ? " · 编辑中" : string.Empty);
            DrawCenteredText(draw, kindLabel, min.X, tileWidth,
                iconMin.Y + iconSize + ImGui.GetTextLineHeight() + 5F,
                editing ? EditorPalette.Accent : EditorPalette.MutedText);
            ImGui.PopID();
        }
    }

    private void DrawAssetDialogs(ProjectHandle? project, Action<string> reportStatus,
        Action<ProjectAssetChange> assetChanged, Func<string, int>? countReferences,
        Func<string, IReadOnlyList<string>>? findReferenceNames)
    {
        if (project is null) return;

        if (_renamePath is not null && CenteredModal.Begin("##资源重命名", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var extension = ProjectAssetOperations.SplitName(_renameSourceName).Extension;
            ImGui.TextUnformatted($"重命名{ProjectAssetCatalog.KindLabel(ProjectAssetCatalog.Classify(_renamePath, false))}");
            ImGui.SetNextItemWidth(360F);
            if (_focusRenameInput)
            {
                ImGui.SetKeyboardFocusHere();
                _focusRenameInput = false;
            }
            var entered = ImGui.InputText("##资源新名称", ref _renameName, 260,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);
            if (extension.Length > 0) ImGui.TextDisabled($"扩展名保持不变：{extension}");
            var confirm = ImGui.Button("确定", new Vector2(110F, 0F)) || entered;
            ImGui.SameLine();
            var cancel = ImGui.Button("取消", new Vector2(110F, 0F)) ||
                         ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (confirm)
            {
                var oldPath = _renamePath;
                var result = ProjectAssetOperations.Rename(project, oldPath, _renameName);
                reportStatus(result.Message);
                if (result.Success)
                {
                    _selectedAsset = result.RelativePath;
                    assetChanged(new ProjectAssetChange(oldPath, result.RelativePath));
                }
                _renamePath = null;
                ImGui.CloseCurrentPopup();
            }
            else if (cancel)
            {
                _renamePath = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (_movePath is not null && CenteredModal.Begin("##资源移动", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("移动文件到 Assets 下的目标文件夹");
            var folders = CollectFolders(project);
            var current = ProjectAssetOperations.NormalizeFolder(_moveTargetFolder);
            var currentLabel = current.Length == 0 ? "（Assets 根目录）" : current;
            ImGui.SetNextItemWidth(360F);
            if (ImGui.BeginCombo("##资源目标文件夹", currentLabel))
            {
                foreach (var folder in folders)
                {
                    var label = folder.Length == 0 ? "（Assets 根目录）" : folder;
                    if (ImGui.Selectable(label, string.Equals(folder, current, StringComparison.OrdinalIgnoreCase)))
                        _moveTargetFolder = folder;
                }
                ImGui.EndCombo();
            }
            var moveConfirm = ImGui.Button("确定", new Vector2(110F, 0F));
            ImGui.SameLine();
            var moveCancel = ImGui.Button("取消", new Vector2(110F, 0F)) ||
                             ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (moveConfirm)
            {
                var moveOldPath = _movePath;
                var result = ProjectAssetOperations.Move(project, moveOldPath, _moveTargetFolder);
                reportStatus(result.Message);
                if (result.Success)
                {
                    _selectedAsset = result.RelativePath;
                    assetChanged(new ProjectAssetChange(moveOldPath, result.RelativePath));
                }
                _movePath = null;
                ImGui.CloseCurrentPopup();
            }
            else if (moveCancel)
            {
                _movePath = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (_deletePath is not null && CenteredModal.Begin("##资源删除确认", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var referenceNames = findReferenceNames?.Invoke(_deletePath) ?? Array.Empty<string>();
            var referenceCount = Math.Max(countReferences?.Invoke(_deletePath) ?? 0, referenceNames.Count);
            var referencingFiles = GetReferencingFiles(project, _deletePath);
            var isMaterial = ProjectAssetCatalog.Classify(_deletePath, false) == ProjectAssetKind.Material;
            var editingThisAsset = IsEditingAsset(_deletePath);
            ImGui.TextWrapped($"确定删除文件“{Path.GetFileName(_deletePath)}”吗？");
            if (editingThisAsset)
            {
                ImGui.TextColored(new Vector4(1F, 0.5F, 0.3F, 1F),
                    "该资产正在编辑空间中打开，删除后无法保存回原文件；请先返回场景再删除。");
            }
            if (referenceCount > 0)
            {
                ImGui.TextColored(new Vector4(1F, 0.5F, 0.3F, 1F),
                    $"警告：该资产正被 {referenceCount} 个场景对象引用，删除后将出现断链。");
                if (referenceNames.Count > 0)
                    ImGui.TextWrapped($"引用模型或对象：{string.Join("、", referenceNames)}");
            }
            if (isMaterial)
            {
                ImGui.TextWrapped("删除后保留组件中的失效材质引用和本地槽位编号，不清空、不重编号；引用模型将使用默认外观。仍可确认继续删除。");
            }

            if (referencingFiles.Count > 0)
            {
                ImGui.TextColored(new Vector4(1F, 0.5F, 0.3F, 1F),
                    $"警告：{string.Join("、", referencingFiles.Take(4))}" +
                    (referencingFiles.Count > 4 ? $" 等 {referencingFiles.Count} 个项目文件" : "") +
                    "引用了该资产，删除后将出现断链。");
            }
            ImGui.BeginDisabled(editingThisAsset);
            var confirm = ImGui.Button("删除", new Vector2(110F, 0F));
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && editingThisAsset)
                ImGui.SetTooltip("正在编辑空间的资产不允许删除");
            ImGui.SameLine();
            var cancel = ImGui.Button("取消", new Vector2(110F, 0F));
            if (confirm)
            {
                var deletedPath = _deletePath;
                var result = ProjectAssetOperations.Delete(project, deletedPath);
                reportStatus(result.Message);
                if (result.Success)
                {
                    _selectedAsset = string.Empty;
                    assetChanged(new ProjectAssetChange(deletedPath, null));
                }
                _deletePath = null;
                ImGui.CloseCurrentPopup();
            }
            else if (cancel)
            {
                _deletePath = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private static string CombineRelative(string? directory, string name) =>
        ProjectAssetOperations.Combine(directory, name);

    /// <summary>判断资产是否正是独立编辑空间里打开的那个</summary>
    /// <param name="relativePath">资产相对 Assets 的路径</param>
    /// <returns>是否正在编辑</returns>
    private bool IsEditingAsset(string relativePath) =>
        EditingAssetPath.Length > 0 &&
        string.Equals(EditingAssetPath.Replace('\\', '/'), relativePath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>资产平铺的悬停提示，说明单击与双击分别会做什么</summary>
    /// <param name="entry">资产条目</param>
    /// <returns>提示文本</returns>
    private string DescribeAssetAction(ProjectAssetEntry entry)
    {
        if (entry.IsFolder) return "单击进入文件夹";
        var editing = IsEditingAsset(entry.RelativePath) ? "（正在编辑）" : string.Empty;
        return entry.Kind switch
        {
            ProjectAssetKind.VoxelModel when IsExternalVoxelFormat(entry.RelativePath) =>
                $"双击导入为原生体素资产并进入编辑空间{editing}",
            ProjectAssetKind.VoxelModel => $"双击进入体素编辑空间{editing}",
            ProjectAssetKind.Prefab => $"双击进入预制体编辑空间{editing}",
            ProjectAssetKind.Timeline => "双击在时间轴窗口打开",
            ProjectAssetKind.Generator => "双击在生成器面板打开",
            ProjectAssetKind.Material => "双击选中；材质参数在右侧检查器中修改",
            ProjectAssetKind.Scene => "双击选中；切换场景请用“文件 > 打开场景”",
            _ => "双击用系统程序打开",
        };
    }

    /// <summary>判断是否为需要先导入的外部体素格式</summary>
    /// <param name="relativePath">资产相对路径</param>
    /// <returns>是否需要导入</returns>
    private static bool IsExternalVoxelFormat(string relativePath) =>
        relativePath.EndsWith(".vox", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".qb", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".schem", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 进入重命名状态：输入框只编辑主名，扩展名由对话框固定保留，
    /// 避免用户漏写或改写扩展名导致资产失去类型
    /// </summary>
    /// <param name="relativePath">待重命名的资产相对路径</param>
    private void BeginRename(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        _renamePath = relativePath;
        _renameSourceName = fileName;
        _renameName = ProjectAssetOperations.SplitName(fileName).Base;
        _openRenameDialog = true;
        _focusRenameInput = true;
    }

    /// <summary>
    /// 创建成功后让新资产可见：切到它所在目录并清掉可能把它挡在平铺之外的搜索与类型筛选，
    /// 再进入重命名，避免出现“重命名框已弹出但列表里看不到目标”的状态
    /// </summary>
    /// <param name="relativePath">新建资产的相对路径</param>
    private void BeginRenameCreatedAsset(string relativePath)
    {
        _selectedFolder = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        _searchText = string.Empty;
        _filter = null;
        BeginRename(relativePath);
    }

    /// <summary>
    /// 查询资产被项目内哪些场景/预制体文件引用；依赖查询走落盘缓存的增量数据库
    /// </summary>
    private IReadOnlyList<string> GetReferencingFiles(ProjectHandle? project, string relativePath)
    {
        if (project is null)
        {
            return Array.Empty<string>();
        }

        if (_referencedBy is null || _referencedByRoot != project.RootPath)
        {
            var loaded = ProjectAssetDatabase.Load(project.AssetsPath, ProjectAssetDatabase.DefaultCachePath(project.RootPath));
            _referencedBy = loaded.ReferencedBy;
            _referencedByRoot = project.RootPath;
        }

        return AssetDependencies.ReferencingFiles(_referencedBy, relativePath);
    }

    private List<ProjectAssetEntry> CollectEntries(ProjectHandle? project)
    {
        var source = string.IsNullOrWhiteSpace(_searchText)
            ? ProjectAssetCatalog.Children(project, _selectedFolder)
            : ProjectAssetCatalog.Search(project, _searchText, _searchAll ? null : _selectedFolder);
        return source
            .Where(entry => _filter is null || entry.Kind == _filter)
            // 附属文件（资源元数据、备份、临时与日志）不是资产，不应出现在浏览列表里
            .Where(entry => !IsSidecarFile(entry.RelativePath))
            .OrderByDescending(entry => entry.IsFolder)
            .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 判断是否为资产附属文件：元数据、备份、临时文件与隐藏文件都不进浏览列表
    /// </summary>
    /// <param name="relativePath">相对 Assets 的路径</param>
    /// <returns>属于附属文件时返回 true</returns>
    private static bool IsSidecarFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath.Replace('\\', '/'));
        if (name.Length == 0 || name.StartsWith('.')) return true;
        return name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".journal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 统一处理目录导航，避免树节点、资产卡片和右键菜单各自维护一套状态。
    /// </summary>
    private void ActivateFolder(string relativePath)
    {
        _selectedFolder = relativePath.Replace('\\', '/').Trim('/');
        _selectedAsset = string.Empty;
    }

    /// <summary>返回上一级资产文件夹；已在根目录时返回空字符串</summary>
    private static string ParentFolder(string folder)
    {
        var trimmed = folder.Replace('\\', '/').Trim('/');
        if (trimmed.Length == 0) return string.Empty;
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? string.Empty : trimmed[..index];
    }

    private string BuildBreadcrumb(ProjectHandle? project)
    {
        var root = project is null ? "资产（未打开项目）" : "资产";
        var path = ProjectAssetCatalog.DisplayPath(_selectedFolder);
        return path == "资产" ? root : $"{root}  /  {path["资产".Length..].TrimStart(' ', '/', '／')}";
    }

    private static string FilterLabel(ProjectAssetKind? kind) => kind is null ? "筛选：全部" : ProjectAssetCatalog.KindLabel(kind.Value);

    /// <summary>收集 Assets 下可用于移动的目录，含根目录；深度设上限避免异常结构拖慢界面</summary>
    /// <param name="project">当前项目</param>
    /// <returns>目录相对路径列表，第一项为空串表示根目录</returns>
    private static List<string> CollectFolders(ProjectHandle project)
    {
        var folders = new List<string> { string.Empty };
        Walk(string.Empty, 0);
        return folders;

        void Walk(string parent, int depth)
        {
            if (depth > 8) return;
            foreach (var entry in ProjectAssetCatalog.Folders(project, parent))
            {
                folders.Add(entry.RelativePath);
                Walk(entry.RelativePath, depth + 1);
            }
        }
    }

    private static void OpenWithShell(ProjectHandle? project, string relativePath, Action<string> reportStatus)
    {
        var path = ResolveAssetPath(project, relativePath);
        if (path is null)
        {
            reportStatus("请先打开项目再打开资产");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { reportStatus($"打开资产失败：{exception.Message}"); }
    }

    private static void RevealInExplorer(ProjectHandle? project, string relativePath, Action<string> reportStatus)
    {
        var path = ResolveAssetPath(project, relativePath);
        if (path is null)
        {
            reportStatus("请先打开项目再定位资产");
            return;
        }
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception) { reportStatus($"打开资源管理器失败：{exception.Message}"); }
    }

    private void DrawCreateAssetMenu(ProjectHandle project, string folder, Action<string> reportStatus,
        Action<ProjectAssetChange> assetChanged, Action<string>? openGeneratorRequest)
    {
        if (!ImGui.BeginMenu("创建")) return;
        if (ImGui.BeginMenu("文件夹"))
        {
            ImGui.SetNextItemWidth(Math.Max(ImGui.CalcTextSize("文件夹名称").X * 3F, ImGui.GetFontSize() * 12F));
            ImGui.InputTextWithHint("##新文件夹名", "文件夹名称", ref _newFolderName, 128, ImGuiInputTextFlags.AutoSelectAll);
            if (ImGui.Button("创建"))
            {
                var result = ProjectAssetOperations.CreateFolder(project, folder, _newFolderName);
                reportStatus(result.Message);
                if (result.Success)
                {
                    _newFolderName = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("体素数据")) CreateAssetAndRename(project, ProjectAssetOperations.KindVoxel, folder, reportStatus, assetChanged);
        if (ImGui.MenuItem("场景")) CreateAssetAndRename(project, ProjectAssetOperations.KindScene, folder, reportStatus, assetChanged);
        if (ImGui.MenuItem("调色盘")) CreateAssetAndRename(project, ProjectAssetOperations.KindPalette, folder, reportStatus, assetChanged);
        if (ImGui.MenuItem("材质")) CreateAssetAndRename(project, ProjectAssetOperations.KindMaterial, folder, reportStatus, assetChanged);
        if (ImGui.MenuItem("渲染预设")) CreateAssetAndRename(project, ProjectAssetOperations.KindRenderPreset, folder, reportStatus, assetChanged);
        if (ImGui.MenuItem("动画片段")) CreateAssetAndRename(project, ProjectAssetOperations.KindAnimationClip, folder, reportStatus, assetChanged);
        if (ImGui.MenuItem("时间轴")) CreateAssetAndRename(project, ProjectAssetOperations.KindTimeline, folder, reportStatus, assetChanged);
        if (ImGui.MenuItem("生成器（JavaScript）")) CreateGeneratorAndOpen(project, ProjectAssetOperations.KindGeneratorJavaScript, folder, reportStatus, assetChanged, openGeneratorRequest);
        if (ImGui.MenuItem("生成器（Lua）")) CreateGeneratorAndOpen(project, ProjectAssetOperations.KindGeneratorLua, folder, reportStatus, assetChanged, openGeneratorRequest);
        if (ImGui.BeginMenu("文档"))
        {
            if (ImGui.MenuItem("文本文件")) CreateAssetAndRename(project, ProjectAssetOperations.KindDocument, folder, reportStatus, assetChanged);
            if (ImGui.MenuItem("Markdown 文档")) CreateAssetAndRename(project, ProjectAssetOperations.KindMarkdown, folder, reportStatus, assetChanged);
            ImGui.EndMenu();
        }
        ImGui.EndMenu();
    }

    /// <summary>创建资产后选中它并直接进入重命名</summary>
    /// <param name="project">当前项目</param>
    /// <param name="kind">资产类型</param>
    /// <param name="folder">目标目录</param>
    /// <param name="reportStatus">状态栏回调</param>
    /// <param name="assetChanged">资产变更回调</param>
    private void CreateAssetAndRename(ProjectHandle project, string kind, string folder,
        Action<string> reportStatus, Action<ProjectAssetChange> assetChanged)
    {
        var result = ProjectAssetOperations.Create(project, kind, folder);
        reportStatus(result.Message);
        if (!result.Success) return;
        _selectedAsset = result.RelativePath;
        assetChanged(new ProjectAssetChange(result.RelativePath, result.RelativePath));
        BeginRenameCreatedAsset(result.RelativePath);
    }

    /// <summary>创建生成器后立刻在生成器面板打开；不改名以免面板持有的路径失效</summary>
    /// <param name="project">当前项目</param>
    /// <param name="kind">生成器类型</param>
    /// <param name="folder">目标目录</param>
    /// <param name="reportStatus">状态栏回调</param>
    /// <param name="assetChanged">资产变更回调</param>
    /// <param name="openGeneratorRequest">打开生成器面板的回调</param>
    private void CreateGeneratorAndOpen(ProjectHandle project, string kind, string folder,
        Action<string> reportStatus, Action<ProjectAssetChange> assetChanged, Action<string>? openGeneratorRequest)
    {
        var result = ProjectAssetOperations.Create(project, kind, folder);
        reportStatus(result.Message);
        if (!result.Success) return;
        _selectedAsset = result.RelativePath;
        assetChanged(new ProjectAssetChange(result.RelativePath, result.RelativePath));
        openGeneratorRequest?.Invoke(result.RelativePath);
    }

    private static string? ResolveAssetPath(ProjectHandle? project, string relativePath) =>
        ProjectAssetOperations.Resolve(project, relativePath);

    private static void DrawCenteredText(ImDrawListPtr draw, string text, float x, float width, float y, Color color)
    {
        var truncated = Truncate(text, width - 8F);
        var textSize = ImGui.CalcTextSize(truncated);
        draw.AddText(new Vector2(x + Math.Max(4F, (width - textSize.X) / 2F), y), ToPackedColor(color), truncated);
    }

    private static string Truncate(string value, float width)
    {
        if (ImGui.CalcTextSize(value).X <= width) return value;
        const string suffix = "…";
        var length = value.Length;
        while (length > 1 && ImGui.CalcTextSize(value[..length] + suffix).X > width) length--;
        return value[..length] + suffix;
    }

    private static Vector4 ToColor(Color color) => new(color.R / 255F, color.G / 255F, color.B / 255F, color.A / 255F);
    private static uint ToPackedColor(Color color) => ImGui.ColorConvertFloat4ToU32(ToColor(color));
}
