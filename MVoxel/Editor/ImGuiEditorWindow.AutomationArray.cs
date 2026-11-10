using System.Text.Json;
using MVoxel.Core;
using MVoxel.Scene;

namespace MVoxel.Editor;

/// <summary>
/// 对象阵列的自动化命令：读取当前设定、预览份数、执行一次阵列
/// </summary>
/// <remarks>
/// 阵列是一次性动作，没有组件状态可写，所以命令只覆盖三件事：
/// 看当前选择与包围盒、看这份参数会生成多少副本、真正落地。
/// 参数与界面共用同一个 SceneArraySettings，脚本执行和手动点「应用」结果一致
/// </remarks>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>
    /// 读取阵列窗口当前设定、选中对象与包围盒
    /// </summary>
    /// <param name="arguments">命令参数，可带 ids 指定参与阵列的对象</param>
    /// <returns>设定与预览摘要</returns>
    private object DescribeObjectArray(JsonElement arguments) => RunOnUi(() =>
    {
        var data = ApplyArrayFields(_arraySettings, arguments).Normalize();
        var roots = ResolveArrayRoots(arguments);
        var bounds = TryGetArrayBounds(roots, out var minimum, out var maximum);
        var size = bounds ? maximum - minimum + new VoxelCoord64(1, 1, 1) : new VoxelCoord64(1, 1, 1);
        var copies = bounds ? SceneObjectArrayMath.Plan(data, size) : [];
        return new
        {
            settings = DescribeArraySettings(data),
            roots = roots.Select(item => new { id = item.Id.ToString(), name = item.Name }).ToArray(),
            bounds = bounds
                ? new
                {
                    min = new[] { minimum.X, minimum.Y, minimum.Z },
                    max = new[] { maximum.X, maximum.Y, maximum.Z },
                    size = new[] { size.X, size.Y, size.Z },
                }
                : null,
            plannedCopies = copies.Count,
            maximumCopies = SceneObjectArrayMath.MaximumCopies,
        };
    });

    /// <summary>
    /// 执行一次对象阵列，参数覆盖阵列窗口的当前设定
    /// </summary>
    /// <param name="arguments">命令参数，可带 ids 指定参与阵列的对象</param>
    /// <returns>执行结果与生效的设定</returns>
    private object ApplyObjectArrayCommand(JsonElement arguments) => RunOnUi(() =>
    {
        var data = ApplyArrayFields(_arraySettings, arguments).Normalize();
        _arraySettings = data;
        var result = ApplyObjectArray(data, ResolveArrayRoots(arguments));
        if (result.Copies == 0) throw new InvalidOperationException(result.Message);
        return new
        {
            settings = DescribeArraySettings(data),
            copies = result.Copies,
            objects = result.Objects,
            grouped = result.Grouped,
            roots = result.Roots.Select(id => id.ToString()).ToArray(),
            message = result.Message,
        };
    });

    /// <summary>
    /// 把命令里的选择参数解析成阵列根对象；没给就沿用当前选择
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <returns>阵列根对象列表</returns>
    private List<SceneObject> ResolveArrayRoots(JsonElement arguments)
    {
        var scene = RequireScene();
        var tokens = ReadStringList(arguments, "ids");
        var single = ReadString(arguments, "id");
        if (single is { Length: > 0 }) tokens.Insert(0, single);
        if (tokens.Count == 0) return GetArrayRoots();

        var roots = new List<SceneObject>();
        foreach (var token in tokens)
        {
            var item = FindSceneObject(token, null) ??
                       scene.Objects.FirstOrDefault(candidate =>
                           string.Equals(candidate.Name, token, StringComparison.OrdinalIgnoreCase)) ??
                       throw new InvalidOperationException($"找不到对象：{token}");
            if (roots.Any(existing => existing.Id == item.Id)) continue;
            if (item.Locked) continue;
            roots.Add(item);
        }

        if (roots.Count == 0) throw new InvalidOperationException("指定对象全部被锁定，没有可阵列的内容");
        var ids = roots.Select(item => item.Id).ToHashSet();
        var byId = scene.Objects.ToDictionary(item => item.Id);
        return roots.Where(item => !HasSelectedAncestor(item, ids, byId)).ToList();
    }

    /// <summary>
    /// 把命令参数覆盖到阵列设定上
    /// </summary>
    /// <param name="settings">原设定</param>
    /// <param name="arguments">命令参数</param>
    /// <returns>覆盖后的设定，仍未规范化</returns>
    private static SceneArraySettings ApplyArrayFields(SceneArraySettings settings, JsonElement arguments)
    {
        if (ReadString(arguments, "mode") is { Length: > 0 } modeText)
            settings = settings with { Mode = ParseArrayMode(modeText) };
        if (ReadVector(arguments, "counts") is { } counts)
            settings = settings with
            {
                Counts = new VoxelCoord64((long)counts[0], (long)counts[1], (long)counts[2]),
            };
        if (ReadVector(arguments, "steps") is { } steps)
            settings = settings with
            {
                Steps = new VoxelCoord64((long)steps[0], (long)steps[1], (long)steps[2]),
            };
        if (ReadNumber(arguments, "gap") is { } gap) settings = settings with { Gap = (int)gap };
        if (ReadString(arguments, "spacing") is { Length: > 0 } spacingText)
            settings = settings with { Spacing = ParseArraySpacing(spacingText) };
        if (ReadNumber(arguments, "axis") is { } axis) settings = settings with { Axis = (int)axis };
        if (ReadNumber(arguments, "ringCount") is { } ringCount)
            settings = settings with { RingCount = (int)ringCount };
        if (ReadBoolean(arguments, "group") is { } group) settings = settings with { Group = group };
        if (ReadVector(arguments, "center") is { } center)
            settings = settings with
            {
                Center = new System.Numerics.Vector3((float)center[0], (float)center[1], (float)center[2]),
            };
        return settings;
    }

    private static SceneArrayMode ParseArrayMode(string text) => text.Trim().ToLowerInvariant() switch
    {
        "rectangular" or "rect" or "矩形" => SceneArrayMode.Rectangular,
        "ring" or "circle" or "环形" => SceneArrayMode.Ring,
        "mirror" or "镜像" => SceneArrayMode.Mirror,
        _ => throw new ArgumentException($"未知阵列方式：{text}，可用 Rectangular/Ring/Mirror"),
    };

    private static SceneArraySpacing ParseArraySpacing(string text) => text.Trim().ToLowerInvariant() switch
    {
        "cells" or "格数" => SceneArraySpacing.Cells,
        "boundingbox" or "bounds" or "包围盒" => SceneArraySpacing.BoundingBox,
        _ => throw new ArgumentException($"未知间距口径：{text}，可用 Cells/BoundingBox"),
    };

    private static object DescribeArraySettings(SceneArraySettings data) => new
    {
        mode = data.Mode.ToString(),
        counts = new[] { data.Counts.X, data.Counts.Y, data.Counts.Z },
        steps = new[] { data.Steps.X, data.Steps.Y, data.Steps.Z },
        gap = data.Gap,
        spacing = data.Spacing.ToString(),
        axis = data.Axis,
        ringCount = data.RingCount,
        group = data.Group,
        center = data.Center is { } center ? new[] { center.X, center.Y, center.Z } : null,
    };

    /// <summary>读取字符串数组参数，兼容单个字符串</summary>
    private static List<string> ReadStringList(JsonElement arguments, string name)
    {
        var result = new List<string>();
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value)) return result;
        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            if (!string.IsNullOrWhiteSpace(single)) result.Add(single);
            return result;
        }

        if (value.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in value.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } text)
                result.Add(text);
        return result;
    }
}
