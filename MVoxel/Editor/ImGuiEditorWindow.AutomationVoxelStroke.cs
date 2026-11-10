using System.Text.Json;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor;

/// <summary>
/// 自动化体素笔画：让脚本和 MCP 能按屏幕点列复现一次绘制，
/// 默认只计算落格不写入，便于断言“一笔是否锁在同一平面”
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>返回的格子明细上限，避免一次响应塞进几十万格</summary>
    private const int MaximumReportedStrokeCells = 4096;

    /// <summary>
    /// 执行一次模拟笔画。绘制走与真实鼠标相同的平面解析，擦除按命中格，换色只取命中格，框选填充取首尾长方体
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>落格统计、平面与层值</returns>
    private object SimulateVoxelStroke(JsonElement arguments) => RunOnUi(() =>
    {
        if (_sceneDocument is null) throw new InvalidOperationException("请先打开项目");
        var points = ReadStrokePoints(arguments);
        if (points.Count == 0) throw new ArgumentException("points 至少需要一个 [x, y] 屏幕点");
        var toolText = (ReadString(arguments, "tool") ?? "draw").Trim().ToLowerInvariant();
        if (toolText is not ("draw" or "erase" or "recolor" or "box"))
            throw new ArgumentException("tool 只能是 draw、erase、recolor 或 box");
        var shift = ReadBoolean(arguments, "shift") ?? false;
        var commit = ReadBoolean(arguments, "commit") ?? false;
        // hold 用于验收：提交后保持笔划冻结状态，便于截图确认区域缓冲是否显示新方块
        var hold = ReadBoolean(arguments, "hold") ?? false;
        var radius = Math.Clamp(ReadInteger(arguments, "radius") ?? _brushRadius, 1, 8);
        var camera = GetSceneCamera();
        var origin = _lastVoxelViewportOrigin;
        var size = _lastVoxelViewportSize;
        if (size.X <= 1F || size.Y <= 1F)
            throw new InvalidOperationException("场景视口还没有尺寸，请先让编辑器渲染过至少一帧");

        var target = _voxelEditTargetId is { } current
            ? _sceneDocument.Objects.FirstOrDefault(item => item.Id == current)
            : null;
        var targetText = ReadString(arguments, "target");
        if (!string.IsNullOrWhiteSpace(targetText)) target = FindSceneObject(_sceneDocument, targetText);
        if (target is null || !target.Components.Any(component =>
                SceneComponentTypes.IsVoxelModel(component) && component.Enabled))
            throw new InvalidOperationException("请先在带模型组件的对象上进入绘制模式，或用 target 指定对象");

        //Shift 在画笔与橡皮擦之间互换，和界面上的左右手规则一致
        var erase = toolText switch
        {
            "draw" or "box" => shift,
            "erase" => !shift,
            _ => false,
        };

        var previousTarget = _voxelEditTargetId;
        var previousPlaneActive = _strokePlaneActive;
        var previousPlaneNormal = _strokePlaneNormal;
        var previousPlaneCell = _strokePlaneCell;
        var cells = new HashSet<VoxelCoord64>();
        var planeActive = false;
        var planeNormal = default(VoxelCoord64);
        long planeCell = 0;
        try
        {
            _voxelEditTargetId = target.Id;
            var resolved = toolText is "recolor" or "erase" || erase
                ? ResolveHitCells(points, origin, size, camera)
                : ResolvePlaneCells(points, origin, size, camera, out planeActive, out planeNormal, out planeCell);

            switch (toolText)
            {
                case "box":
                    if (resolved.Count >= 2)
                        foreach (var cell in VoxelEditingTools.BoxCells(resolved[0], resolved[^1], hollow: false))
                            cells.Add(cell);
                    break;
                case "recolor":
                case "erase":
                    foreach (var cell in resolved)
                        foreach (var brushCell in VoxelBrush.CellsAround(cell, _brushShape,
                                     erase ? Math.Max(1, (radius + 1) / 2) : radius))
                            cells.Add(brushCell);
                    break;
                default:
                    for (var index = 0; index < resolved.Count; index++)
                    {
                        if (index > 0 && resolved[index - 1] != resolved[index])
                            foreach (var lineCell in VoxelEditingTools.LineCells(resolved[index - 1], resolved[index]))
                                if (lineCell != resolved[index - 1] && lineCell != resolved[index])
                                    foreach (var brushCell in VoxelBrush.CellsAround(lineCell, _brushShape, radius))
                                        cells.Add(brushCell);
                        foreach (var brushCell in VoxelBrush.CellsAround(resolved[index], _brushShape, radius))
                            cells.Add(brushCell);
                    }
                    break;
            }

            if (commit && cells.Count > 0)
            {
                CommitCells(toolText switch
                {
                    "erase" => "擦除体素",
                    "recolor" => "换色体素",
                    "box" => "填充长方体",
                    _ => erase ? "擦除体素" : "绘制体素",
                }, cells.ToArray(), CurrentBrushValue(erase), target.Id);
                if (hold)
                {
                    // 模拟真实拖动中的笔划状态：区域设为这次落格的外扩一格，并保持冻结，
                    // 便于用截图确认区域缓冲里的新方块是否真的显示出来
                    var minimum = new VoxelCoord64(long.MaxValue, long.MaxValue, long.MaxValue);
                    var maximum = new VoxelCoord64(long.MinValue, long.MinValue, long.MinValue);
                    foreach (var cell in cells)
                    {
                        minimum = new VoxelCoord64(Math.Min(minimum.X, cell.X), Math.Min(minimum.Y, cell.Y),
                            Math.Min(minimum.Z, cell.Z));
                        maximum = new VoxelCoord64(Math.Max(maximum.X, cell.X), Math.Max(maximum.Y, cell.Y),
                            Math.Max(maximum.Z, cell.Z));
                    }

                    _strokeEditRegionMinimum = minimum - new VoxelCoord64(1, 1, 1);
                    _strokeEditRegionMaximum = maximum + new VoxelCoord64(1, 1, 1);
                    _strokeEditRegionActive = true;
                    _simulatedStrokeHold = true;
                }
                else
                {
                    _simulatedStrokeHold = false;
                }
            }
            else if (!hold)
            {
                _simulatedStrokeHold = false;
            }
        }
        finally
        {
            _voxelEditTargetId = previousTarget;
            _strokePlaneActive = previousPlaneActive;
            _strokePlaneNormal = previousPlaneNormal;
            _strokePlaneCell = previousPlaneCell;
        }

        var axis = planeActive ? AxisIndexOf(planeNormal) : -1;
        var layerValues = axis >= 0
            ? cells.Select(cell => AxisValue(cell, axis)).Distinct().OrderBy(value => value).ToArray()
            : Array.Empty<long>();
        return new
        {
            tool = toolText,
            erase,
            commit,
            target = new { id = target.Id.ToString(), target.Name },
            plane = planeActive
                ? (object?)new
                {
                    active = true,
                    normal = new[] { planeNormal.X, planeNormal.Y, planeNormal.Z },
                    cell = planeCell,
                    axis,
                }
                : null,
            layerValues,
            //越层断言：平面模式下所有格子必须落在同一个法线层
            sameLayer = axis < 0 || layerValues.Length <= 1,
            cellCount = cells.Count,
            cells = cells.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z)
                .Take(MaximumReportedStrokeCells)
                .Select(cell => new[] { cell.X, cell.Y, cell.Z }).ToArray(),
            truncated = cells.Count > MaximumReportedStrokeCells,
            viewport = new { origin = new[] { origin.X, origin.Y }, size = new[] { size.X, size.Y } },
        };
    });

    /// <summary>
    /// 把屏幕点解析成锁定平面内的落格；第一个点建立平面，其余点投影到同一平面
    /// </summary>
    /// <param name="points">屏幕点列</param>
    /// <param name="origin">视口左上角</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <param name="planeActive">是否成功建立平面</param>
    /// <param name="planeNormal">平面法线</param>
    /// <param name="planeCell">绘制层坐标</param>
    /// <returns>每个屏幕点对应的落格</returns>
    private List<VoxelCoord64> ResolvePlaneCells(IReadOnlyList<Vector2> points, Vector2 origin, Vector2 size,
        SceneCamera camera, out bool planeActive, out VoxelCoord64 planeNormal, out long planeCell)
    {
        var resolved = new List<VoxelCoord64>();
        planeActive = false;
        planeNormal = default;
        planeCell = 0;
        for (var index = 0; index < points.Count; index++)
        {
            VoxelCoord64 cell;
            if (resolved.Count == 0)
            {
                var hit = PickVoxel(points[index], origin, size, camera);
                if (!TryBeginStrokePlane(hit, points[index], origin, size, camera, out cell))
                    throw new InvalidOperationException("射线无法建立绘制平面，请把点放在视口内");
                planeActive = true;
                planeNormal = _strokePlaneNormal;
                planeCell = _strokePlaneCell;
            }
            else if (!TryPickCellOnStrokePlane(points[index], origin, size, camera, out cell))
            {
                continue;
            }

            resolved.Add(cell);
        }

        return resolved;
    }

    /// <summary>
    /// 把屏幕点解析成命中格；用于擦除与换色这类不铺平面的操作
    /// </summary>
    /// <param name="points">屏幕点列</param>
    /// <param name="origin">视口左上角</param>
    /// <param name="size">视口尺寸</param>
    /// <param name="camera">观察相机</param>
    /// <returns>命中的格子，未命中的点被跳过</returns>
    private List<VoxelCoord64> ResolveHitCells(IReadOnlyList<Vector2> points, Vector2 origin, Vector2 size,
        SceneCamera camera)
    {
        var resolved = new List<VoxelCoord64>();
        foreach (var point in points)
            if (PickVoxel(point, origin, size, camera) is { } hit) resolved.Add(hit.Coordinate);
        return resolved;
    }

    /// <summary>读取 points 参数：二维屏幕坐标数组</summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>屏幕点列</returns>
    private static List<Vector2> ReadStrokePoints(JsonElement arguments)
    {
        var points = new List<Vector2>();
        if (!arguments.TryGetProperty("points", out var array) || array.ValueKind != JsonValueKind.Array)
            return points;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2) continue;
            if (!item[0].TryGetSingle(out var x) || !item[1].TryGetSingle(out var y)) continue;
            points.Add(new Vector2(x, y));
        }

        return points;
    }
}
