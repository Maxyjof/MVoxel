using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 程序化体素组件的共用逻辑：曲线渲染器与树生成器都用同一套重建、补齐与清理流程
/// </summary>
/// <remarks>
/// 组件的生成结果保存在组件内部，对象世界只是它的镜像。这里统一负责
/// “重建并写回负载”“缺少世界时补齐”“移除组件时清理世界”，
/// 具体怎么生成由 SceneComponentTypes 按组件类型分派
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>单个程序化对象允许生成的体素上限，超出后截断并提示</summary>
    private const int ProceduralVoxelBudget = 600_000;

    /// <summary>生成体素被截断的对象，用于检查器提示</summary>
    private readonly Dictionary<StableId, bool> _generatedVoxelTruncated = new();

    /// <summary>检查器长提示的自动分行缓存，键为文本与可见宽度</summary>
    private static readonly Dictionary<(string Text, int Width), string[]> InspectorHintCache = new();

    /// <summary>
    /// 按检查器的可见宽度把长提示拆成多行
    /// </summary>
    /// <remarks>
    /// 检查器带横向滚动条时不能用 ImGui 自带换行：换行宽度会跟着被撑开的内容区一起变大，
    /// 结果是文字越来越长、右侧一直被裁掉。这里只按窗口可见宽度切分，
    /// 保证提示永远落在面板内，不会把检查器撑宽
    /// </remarks>
    /// <param name="text">提示文本</param>
    private static void DrawInspectorHint(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var style = ImGui.GetStyle();
        var available = MathF.Max(ImGui.GetFontSize() * 8F,
            ImGui.GetWindowSize().X - style.WindowPadding.X * 2F - style.ScrollbarSize - 4F);
        var key = (text, (int)MathF.Round(available));
        if (!InspectorHintCache.TryGetValue(key, out var lines))
        {
            var result = new List<string>();
            var builder = new System.Text.StringBuilder();
            foreach (var character in text)
            {
                builder.Append(character);
                if (ImGui.CalcTextSize(builder.ToString()).X <= available) continue;
                builder.Length--;
                if (builder.Length > 0) result.Add(builder.ToString());
                builder.Clear();
                builder.Append(character);
            }

            if (builder.Length > 0) result.Add(builder.ToString());
            lines = result.ToArray();
            if (InspectorHintCache.Count > 64) InspectorHintCache.Clear();
            InspectorHintCache[key] = lines;
        }

        foreach (var line in lines) ImGui.TextDisabled(line);
    }

    /// <summary>
    /// 取对象上启用的程序化体素组件
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>组件记录，没有时返回 null</returns>
    private static ComponentRecord? GetGeneratedVoxelComponent(SceneObject? sceneObject) =>
        SceneComponentTypes.FindGeneratedVoxelComponent(sceneObject);

    /// <summary>
    /// 判断对象是否可以作为体素绘制目标
    /// </summary>
    /// <remarks>
    /// 程序化组件生成的体素不暴露编辑能力，绘制目标必须是有启用模型组件的对象
    /// </remarks>
    /// <param name="objectId">对象标识</param>
    /// <returns>是否允许进入绘制模式</returns>
    private bool IsVoxelEditableObject(StableId objectId) =>
        _sceneDocument?.Objects.FirstOrDefault(item => item.Id == objectId)?.Components
            .Any(item => SceneComponentTypes.IsVoxelModel(item) && item.Enabled) == true;

    /// <summary>
    /// 按组件当前参数重建体素，并把结果接入对象世界
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    /// <param name="component">程序化组件记录</param>
    /// <param name="action">状态栏提示，空字符串表示不提示</param>
    /// <param name="refreshWorld">是否立即重新合成；批量加载时由调用方统一合成</param>
    private void RebuildGeneratedVoxels(SceneObject sceneObject, ComponentRecord component, string action,
        bool refreshWorld = true)
    {
        if (_sceneDocument is null || !SceneComponentTypes.IsGeneratedVoxelComponent(component)) return;
        var local = SceneComponentTypes.BuildGeneratedVoxels(component, ProceduralVoxelBudget, out var truncated);
        _generatedVoxelTruncated[sceneObject.Id] = truncated;
        // 体素数据按局部坐标保存，进入对象世界时补上格原点，对象平移后仍能对上
        var origin = GetObjectGridOrigin(sceneObject);
        _objectVoxels.AssignWorld(sceneObject.Id, origin == VoxelCoord64.Zero
            ? local
            : local.CloneShifted(origin.X, origin.Y, origin.Z));
        if (refreshWorld)
        {
            RebuildObjectVoxelWorld();
            MarkSceneDocumentDirty();
            _pipeline?.ResetAccumulation();
            _accumulationSignature = string.Empty;
        }

        if (!string.IsNullOrEmpty(action))
            SetStatus($"{action} · {local.OccupiedVoxelCount:N0} 格" + (truncated ? "（已截断）" : string.Empty));
    }

    /// <summary>
    /// 为缺少体素世界的程序化对象补齐数据，供场景加载、复制与撤销后复用
    /// </summary>
    /// <remarks>
    /// 曲线渲染器要求负载指纹与参数一致，不一致就按当前曲线重算；
    /// 树生成器只在用户点生成时替换结果，参数改了但没点生成时，旧树原样保留
    /// </remarks>
    private void EnsureGeneratedVoxelWorlds()
    {
        if (_sceneDocument is null) return;
        foreach (var sceneObject in _sceneDocument.Objects)
        {
            var component = GetGeneratedVoxelComponent(sceneObject);
            if (component is null || _objectVoxels.TryGetWorld(sceneObject.Id, out _)) continue;
            if (!SceneComponentTypes.TryPrepareGeneratedVoxels(component, ProceduralVoxelBudget, out var local,
                    out var regenerated, out var truncated)) continue;
            var origin = GetObjectGridOrigin(sceneObject);
            _objectVoxels.AssignWorld(sceneObject.Id,
                origin == VoxelCoord64.Zero ? local : local.CloneShifted(origin.X, origin.Y, origin.Z));
            _generatedVoxelTruncated[sceneObject.Id] = truncated;
            if (regenerated) MarkSceneDocumentDirty();
        }
    }

    /// <summary>
    /// 移除程序化组件时同步清理隐藏体素，避免留下不可见的独立世界
    /// </summary>
    /// <param name="sceneObject">组件所属对象</param>
    private void RemoveGeneratedVoxelWorld(SceneObject sceneObject)
    {
        _objectVoxels.Remove(sceneObject.Id);
        _generatedVoxelTruncated.Remove(sceneObject.Id);
        if (_curvePointObjectId == sceneObject.Id)
        {
            _curvePointObjectId = StableId.Empty;
            _curvePointIndex = -1;
        }

        RebuildObjectVoxelWorld();
    }

    /// <summary>
    /// 把程序化组件当前生成的体素实例化为真正的模型组件
    /// </summary>
    /// <remarks>
    /// 组件被移除、模型组件补上，隐藏负载转成模型的内嵌体素；
    /// 之后可以正常进入绘制模式，但不能再变回生成器，只能靠撤销恢复
    /// </remarks>
    /// <param name="sceneObject">目标对象</param>
    /// <param name="component">要替换掉的程序化组件</param>
    /// <param name="partName">模型组件里显示的名称</param>
    private void InstantiateGeneratedVoxels(SceneObject sceneObject, ComponentRecord component, string partName)
    {
        if (_sceneDocument is null) return;
        if (sceneObject.Components.Any(SceneComponentTypes.IsVoxelModel))
        {
            SetStatus("对象已经有模型组件，无法再实例化");
            return;
        }

        var world = _objectVoxels.TryGetWorld(sceneObject.Id, out var current)
            ? current
            : new VoxelWorld();
        if (world.OccupiedVoxelCount == 0)
        {
            // 还没有生成过或生成结果为空：按当前参数先生成一次，避免实例化出空模型
            var local = SceneComponentTypes.BuildGeneratedVoxels(component, ProceduralVoxelBudget, out _);
            var origin = GetObjectGridOrigin(sceneObject);
            world = origin == VoxelCoord64.Zero ? local : local.CloneShifted(origin.X, origin.Y, origin.Z);
        }

        var sortingLayer = "默认";
        var sortingOrder = 0;
        if (SceneComponentTypes.IsTreeGenerator(component))
            SceneComponentTypes.ReadTreeSorting(component, out sortingLayer, out sortingOrder);
        else if (SceneComponentTypes.IsCurveRenderer(component))
            SceneComponentTypes.ReadCurveSorting(component, out sortingLayer, out sortingOrder);
        sceneObject.Components.Remove(component);
        var model = SceneComponentTypes.CreateVoxelModel(string.Empty, world.OccupiedVoxelCount, partName,
            string.IsNullOrWhiteSpace(sceneObject.Name) ? partName : sceneObject.Name);
        SceneComponentTypes.WriteVoxelModel(model, SceneComponentTypes.ReadVoxelModel(model) with
        {
            SortingLayer = sortingLayer,
            SortingOrder = sortingOrder,
        });
        sceneObject.Components.Add(model);
        _objectVoxels.AssignWorld(sceneObject.Id, world);
        _generatedVoxelTruncated.Remove(sceneObject.Id);
        _selectedSceneObjectId = sceneObject.Id;
        _voxelEditTargetId = null;
        _activeTool = EditorTool.Pick;
        MarkSceneDocumentDirty();
        RebuildObjectVoxelWorld();
        _pipeline?.ResetAccumulation();
        SetStatus($"已实例化为体素模型：{world.OccupiedVoxelCount:N0} 格。" +
                  "现在可以进入绘制模式；按 Ctrl+Z 可以恢复成生成器");
    }
}
