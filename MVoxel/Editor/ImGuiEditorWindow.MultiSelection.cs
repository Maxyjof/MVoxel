using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Editor;

/// <summary>
/// 多选检查器：只显示所有选中对象共有的信息。相同值正常显示并可批量写入，
/// 不同值显示横线输入框，输入后统一写入全部对象；嵌套结构与内部账本字段暗色锁定
/// </summary>
internal sealed partial class ImGuiEditorWindow
{
    /// <summary>混合值输入框里的草稿文本，按“组件 + 字段”区分</summary>
    private readonly Dictionary<string, string> _multiMixedText = new();

    /// <summary>多选时等待提交的浮点旋转，松手后统一重采样</summary>
    private readonly Dictionary<StableId, System.Numerics.Vector3> _multiRotationPending = new();

    /// <summary>多选检查器不允许批量改写的内部字段</summary>
    private static readonly string[] MultiLockedFields =
    [
        "voxelCount", "generatedSignature", "formatVersion", "sourceOrigin", "materialSlots",
        "points", "keyframes", "generatedVoxels", "model", "part", "template", "scaleX", "scaleY", "scaleZ",
        "rotationX", "rotationY", "rotationZ",
    ];

    private void DrawMultiSelectionInspector()
    {
        if (_sceneDocument is null) return;
        var objects = _selectedSceneObjectIds
            .Select(id => _sceneDocument.Objects.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null).Select(item => item!).ToArray();
        if (objects.Length == 0) return;
        ImGui.TextDisabled($"已选择 {objects.Length} 个对象");
        foreach (var item in objects)
        {
            ImGui.PushID(item.Id.ToString());
            if (ImGui.Selectable(item.Name, _selectedSceneObjectId == item.Id))
            {
                _selectedSceneObjectId = item.Id;
                _revealHierarchySelection = item.Id;
            }

            ImGui.PopID();
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("变换（多选）##MultiTransform", ImGuiTreeNodeFlags.DefaultOpen))
            DrawMultiTransformRows(objects);
        DrawSharedComponents(objects);
        CommitMultiRotation(objects);
    }

    // ---------- 变换：按每个对象自己的模式写入 ----------

    private void DrawMultiTransformRows(SceneObject[] objects)
    {
        var allGrid = objects.All(item => item.Transform.Mode == TransformMode.Grid);
        var allFree = objects.All(item => item.Transform.Mode == TransformMode.Free);
        if (allGrid || allFree)
        {
            // 全部同一模式时直接批量；数值统一后写入各自模式
            DrawMultiRow("位置 X", objects, static item => ReadMultiPosition(item, 0),
                (item, value) => WriteMultiPosition(item, 0, value));
            DrawMultiRow("位置 Y", objects, static item => ReadMultiPosition(item, 1),
                (item, value) => WriteMultiPosition(item, 1, value));
            DrawMultiRow("位置 Z", objects, static item => ReadMultiPosition(item, 2),
                (item, value) => WriteMultiPosition(item, 2, value));
        }
        else
        {
            // 混选时仍可批量：网格对象取整（去小数）、自由对象保留浮点
            DrawMultiRow("位置 X（混选）", objects, static item => ReadMultiPosition(item, 0),
                (item, value) => WriteMultiPosition(item, 0, value));
            DrawMultiRow("位置 Y（混选）", objects, static item => ReadMultiPosition(item, 1),
                (item, value) => WriteMultiPosition(item, 1, value));
            DrawMultiRow("位置 Z（混选）", objects, static item => ReadMultiPosition(item, 2),
                (item, value) => WriteMultiPosition(item, 2, value));
        }

        DrawMultiRow("旋转 X", objects, static item => ReadMultiRotationDegrees(item).X,
            (item, value) => WriteMultiRotation(item, 0, value));
        DrawMultiRow("旋转 Y", objects, static item => ReadMultiRotationDegrees(item).Y,
            (item, value) => WriteMultiRotation(item, 1, value));
        DrawMultiRow("旋转 Z", objects, static item => ReadMultiRotationDegrees(item).Z,
            (item, value) => WriteMultiRotation(item, 2, value));

        DrawMultiRow("缩放 X", objects, static item => ReadMultiScale(item).X,
            (item, value) => WriteMultiScale(item, 0, value));
        DrawMultiRow("缩放 Y", objects, static item => ReadMultiScale(item).Y,
            (item, value) => WriteMultiScale(item, 1, value));
        DrawMultiRow("缩放 Z", objects, static item => ReadMultiScale(item).Z,
            (item, value) => WriteMultiScale(item, 2, value));
    }

    private static double ReadMultiPosition(SceneObject item, int axis) => item.Transform.Mode == TransformMode.Grid
        ? axis switch
        {
            0 => item.Transform.Grid.Position.X,
            1 => item.Transform.Grid.Position.Y,
            _ => item.Transform.Grid.Position.Z,
        }
        : axis switch
        {
            0 => item.Transform.Free.Position.X,
            1 => item.Transform.Free.Position.Y,
            _ => item.Transform.Free.Position.Z,
        };

    private static void WriteMultiPosition(SceneObject item, int axis, double value)
    {
        if (item.Transform.Mode == TransformMode.Grid)
        {
            // 网格坐标一律去掉小数部分，不做四舍五入
            var cell = (long)value;
            var current = item.Transform.Grid.Position;
            var next = axis switch
            {
                0 => new VoxelCoord64(cell, current.Y, current.Z),
                1 => new VoxelCoord64(current.X, cell, current.Z),
                _ => new VoxelCoord64(current.X, current.Y, cell),
            };
            item.Transform.SetGrid(new GridTransform(next, item.Transform.Grid.Rotation));
            UpdateTransformMarker(item);
            return;
        }

        var position = item.Transform.Free.Position;
        var freeNext = axis switch
        {
            0 => new Double3(value, position.Y, position.Z),
            1 => new Double3(position.X, value, position.Z),
            _ => new Double3(position.X, position.Y, value),
        };
        item.Transform.SetFree(new FreeTransform(freeNext, item.Transform.Free.Rotation));
        UpdateTransformMarker(item);
    }

    private static System.Numerics.Vector3 ReadMultiRotationDegrees(SceneObject item)
    {
        if (item.Transform.Mode == TransformMode.Free)
        {
            var freeEuler = item.Transform.GetEulerDegrees();
            return new System.Numerics.Vector3((float)freeEuler.X, (float)freeEuler.Y, (float)freeEuler.Z);
        }
        var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is null) return ReadGeneratedObjectTransform(item).Rotation;
        var data = SceneComponentTypes.ReadVoxelModel(component);
        return new System.Numerics.Vector3(data.RotationX, data.RotationY, data.RotationZ);
    }

    private void WriteMultiRotation(SceneObject item, int axis, double value)
    {
        if (item.Transform.Mode == TransformMode.Free)
        {
            var euler = item.Transform.GetEulerDegrees();
            var next = axis switch
            {
                0 => new Double3(value, euler.Y, euler.Z),
                1 => new Double3(euler.X, value, euler.Z),
                _ => new Double3(euler.X, euler.Y, value),
            };
            item.Transform.SetFreeEuler(item.Transform.Free.Position, next);
            UpdateTransformMarker(item);
            return;
        }

        var current = _multiRotationPending.TryGetValue(item.Id, out var pending)
            ? pending
            : ReadMultiRotationDegrees(item);
        var target = axis switch
        {
            0 => new System.Numerics.Vector3((float)value, current.Y, current.Z),
            1 => new System.Numerics.Vector3(current.X, (float)value, current.Z),
            _ => new System.Numerics.Vector3(current.X, current.Y, (float)value),
        };
        if (_editorSettings.ApplyTransformImmediately)
        {
            // 设置开启时立即重建，拖动就能看到旋转结果
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is not null)
                ApplyModelRotation(item, component, RotationFromDegrees(target), target);
            else if (item.Components.FirstOrDefault(SceneComponentTypes.IsGeneratedVoxelComponent) is { } generated)
                ApplyGeneratedObjectTransform(item, generated, rotation: target);
            return;
        }

        _multiRotationPending[item.Id] = target;
    }

    private void CommitMultiRotation(SceneObject[] objects)
    {
        if (_multiRotationPending.Count == 0) return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsAnyItemActive()) return;
        foreach (var item in objects)
        {
            if (!_multiRotationPending.TryGetValue(item.Id, out var degrees)) continue;
            var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
            if (component is not null)
                ApplyModelRotation(item, component, RotationFromDegrees(degrees), degrees);
            else if (item.Components.FirstOrDefault(SceneComponentTypes.IsGeneratedVoxelComponent) is { } generated)
                ApplyGeneratedObjectTransform(item, generated, rotation: degrees);
            else if (item.Transform.Mode == TransformMode.Free)
            {
                item.Transform.SetFreeEuler(item.Transform.Free.Position,
                    new Double3(degrees.X, degrees.Y, degrees.Z));
                UpdateTransformMarker(item);
            }
        }

        _multiRotationPending.Clear();
        MarkSceneDocumentDirty();
        RebuildObjectVoxelWorld();
    }

    private static System.Numerics.Vector3 ReadMultiScale(SceneObject item)
    {
        var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is not null)
        {
            var data = SceneComponentTypes.ReadVoxelModel(component);
            return new System.Numerics.Vector3(data.ScaleX, data.ScaleY, data.ScaleZ);
        }

        if (item.Components.FirstOrDefault(SceneComponentTypes.IsGeneratedVoxelComponent) is { } generated)
            return ReadGeneratedObjectTransform(item).Scale;
        return new System.Numerics.Vector3(item.Transform.ScaleX, item.Transform.ScaleY, item.Transform.ScaleZ);
    }

    private void WriteMultiScale(SceneObject item, int axis, double value)
    {
        var factor = MathF.Max(0F, (float)value);
        var component = item.Components.FirstOrDefault(SceneComponentTypes.IsVoxelModel);
        if (component is not null)
        {
            var data = SceneComponentTypes.ReadVoxelModel(component);
            var scale = axis switch
            {
                0 => (factor, data.ScaleY, data.ScaleZ),
                1 => (data.ScaleX, factor, data.ScaleZ),
                _ => (data.ScaleX, data.ScaleY, factor),
            };
            ApplyModelScale(item, scale.Item1, scale.Item2, scale.Item3);
            return;
        }

        if (item.Components.FirstOrDefault(SceneComponentTypes.IsGeneratedVoxelComponent) is { } generatedScale)
        {
            var current = ReadGeneratedObjectTransform(item).Scale;
            var next = axis switch
            {
                0 => new System.Numerics.Vector3(factor, current.Y, current.Z),
                1 => new System.Numerics.Vector3(current.X, factor, current.Z),
                _ => new System.Numerics.Vector3(current.X, current.Y, factor),
            };
            ApplyGeneratedObjectTransform(item, generatedScale, scale: next);
            return;
        }

        var x = axis == 0 ? factor : item.Transform.ScaleX;
        var y = axis == 1 ? factor : item.Transform.ScaleY;
        var z = axis == 2 ? factor : item.Transform.ScaleZ;
        ApplyObjectScale(item, x, y, z);
    }

    // ---------- 共有组件字段 ----------

    private void DrawSharedComponents(SceneObject[] objects)
    {
        var sharedTypes = objects
            .SelectMany(item => item.Components.Select(component => component.TypeId))
            .Distinct(StringComparer.Ordinal)
            .Where(typeId => objects.All(item => item.Components.Any(component => component.TypeId == typeId)))
            .ToArray();
        foreach (var typeId in sharedTypes)
        {
            var components = objects
                .Select(item => item.Components.First(component => component.TypeId == typeId)).ToArray();
            var display = SceneComponentTypes.GetDisplayName(components[0]);
            if (!ImGui.CollapsingHeader($"{display}（多选）##Multi_{typeId}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            var names = new List<string>();
            foreach (var component in components)
            {
                if (component.Data.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in component.Data.EnumerateObject())
                    if (!names.Contains(property.Name)) names.Add(property.Name);
            }

            var colorGroups = CollectColorGroups(names);
            foreach (var name in names)
            {
                if (MultiLockedFields.Contains(name, StringComparer.Ordinal)) continue;
                if (colorGroups.Contains(name)) continue;
                DrawSharedComponentField(components, name);
            }

            foreach (var group in colorGroups)
                DrawSharedColorField(components, group);
        }
    }

    /// <summary>识别 XXR/XXG/XXB(/XXA) 这类颜色通道组</summary>
    private static HashSet<string> CollectColorGroups(List<string> names)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefix in new[] { "color", "bark", "leaf", "leaf2", "paint", "sky", "fog", "water", "emissive" })
        {
            var red = prefix + "R";
            if (!names.Contains(red, StringComparer.OrdinalIgnoreCase)) continue;
            members.Add(red);
            foreach (var suffix in new[] { "G", "B", "A" })
            {
                var name = names.FirstOrDefault(item =>
                    string.Equals(item, prefix + suffix, StringComparison.OrdinalIgnoreCase));
                if (name is not null) members.Add(name);
            }
        }

        return members;
    }

    private void DrawSharedColorField(ComponentRecord[] components, string redName)
    {
        var prefix = redName[..^1];
        var greenName = FindName(components, prefix + "G");
        var blueName = FindName(components, prefix + "B");
        var alphaName = FindName(components, prefix + "A");
        if (greenName is null || blueName is null) return;
        var colors = components.Select(component => (
            R: TryReadNumber(component, redName, out var r) ? r : double.NaN,
            G: TryReadNumber(component, greenName, out var g) ? g : double.NaN,
            B: TryReadNumber(component, blueName, out var b) ? b : double.NaN)).ToArray();
        if (colors.Any(color => double.IsNaN(color.R))) return;
        var same = colors.Skip(1).All(color => Math.Abs(color.R - colors[0].R) < .001D &&
                                                Math.Abs(color.G - colors[0].G) < .001D &&
                                                Math.Abs(color.B - colors[0].B) < .001D);
        BeginInspectorField(prefix);
        var value = new System.Numerics.Vector3((float)colors[0].R / 255F, (float)colors[0].G / 255F,
            (float)colors[0].B / 255F);
        // 颜色不一致时仍可批量覆盖：显示首个对象的颜色作为起点，改动即写入全部
        if (!same && ImGui.IsItemHovered()) ImGui.SetTooltip("颜色不一致，改这里会统一覆盖全部选中对象");

        if (!ImGui.ColorEdit3("##MultiColor_" + prefix, ref value)) return;
        foreach (var component in components)
        {
            WriteNumber(component, redName, MathF.Round(value.X * 255F));
            WriteNumber(component, greenName, MathF.Round(value.Y * 255F));
            WriteNumber(component, blueName, MathF.Round(value.Z * 255F));
        }

        MarkSceneDocumentDirty();
        RebuildObjectVoxelWorld();
    }

    private static string? FindName(ComponentRecord[] components, string name) =>
        components.SelectMany(component => component.Data.ValueKind == JsonValueKind.Object
                ? component.Data.EnumerateObject().Select(property => property.Name)
                : Enumerable.Empty<string>())
            .FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

    private void DrawSharedComponentField(ComponentRecord[] components, string name)
    {
        var numbers = components.Select(component => TryReadNumber(component, name, out var number) ? (double?)number : null)
            .ToArray();
        var booleans = components.Select(component => TryReadBoolean(component, name, out var flag) ? (bool?)flag : null)
            .ToArray();
        var strings = components.Select(component => TryReadString(component, name, out var text) ? text : null).ToArray();
        var key = components[0].TypeId + ":" + name;
        BeginInspectorField(name);
        if (numbers.All(value => value is not null))
        {
            var values = numbers.Select(value => value!.Value).ToArray();
            if (values.Skip(1).Any(value => Math.Abs(value - values[0]) > .000001D))
            {
                DrawMixedInput(key, value => ApplyNumbers(components, name, value));
                return;
            }

            var current = (float)values[0];
            if (ImGui.DragFloat("##Multi_" + key, ref current, .05F))
                ApplyNumbers(components, name, current);
            return;
        }

        if (booleans.All(value => value is not null))
        {
            var flags = booleans.Select(value => value!.Value).ToArray();
            if (flags.Skip(1).Any(value => value != flags[0]))
            {
                DrawMixedInput(key, value => ApplyBooleans(components, name, value > .5D));
                return;
            }

            var current = flags[0];
            if (ImGui.Checkbox("##Multi_" + key, ref current))
                ApplyBooleans(components, name, current);
            return;
        }

        if (strings.All(value => value is not null))
        {
            // 字符串（排序图层、资产引用等）批量统一写入
            var values = strings.Select(value => value!).ToArray();
            var mixedText = values.Skip(1).Any(value => !string.Equals(value, values[0], StringComparison.Ordinal));
            var draftKey = "text:" + key;
            if (!_multiMixedText.TryGetValue(draftKey, out var draft)) draft = mixedText ? "—" : values[0];
            ImGui.SetNextItemWidth(-1F);
            if (ImGui.InputText("##MultiText_" + key, ref draft, 256, ImGuiInputTextFlags.AutoSelectAll))
            {
                _multiMixedText[draftKey] = draft;
                if (!mixedText || !string.Equals(draft, "—", StringComparison.Ordinal))
                {
                    foreach (var component in components) WriteString(component, name, draft);
                    _multiMixedText.Remove(draftKey);
                    MarkSceneDocumentDirty();
                    RebuildObjectVoxelWorld();
                }
            }
            else _multiMixedText[draftKey] = draft;
            return;
        }

        // 嵌套对象、数组等无法合并：暗色锁定
        DrawLockedRow(name, "该字段是嵌套结构或内部数据，不支持多选批量编辑");
    }

    private void ApplyNumbers(ComponentRecord[] components, string name, double value)
    {
        foreach (var component in components) WriteNumber(component, name, value);
        MarkSceneDocumentDirty();
        RebuildObjectVoxelWorld();
    }

    private void ApplyBooleans(ComponentRecord[] components, string name, bool value)
    {
        foreach (var component in components) WriteBoolean(component, name, value);
        MarkSceneDocumentDirty();
        RebuildObjectVoxelWorld();
    }

    /// <summary>混合值输入：显示横线，输入数值后统一写入</summary>
    private void DrawMixedInput(string key, Action<double> apply)
    {
        if (!_multiMixedText.TryGetValue(key, out var text)) text = "—";
        ImGui.SetNextItemWidth(-1F);
        if (ImGui.InputText("##Mixed_" + key, ref text, 32, ImGuiInputTextFlags.AutoSelectAll))
        {
            _multiMixedText[key] = text;
            if (double.TryParse(text, out var parsed))
            {
                apply(parsed);
                _multiMixedText.Remove(key);
            }

            return;
        }

        _multiMixedText[key] = text;
    }

    /// <summary>暗色锁定的字段行：提示不支持批量编辑</summary>
    private static void DrawLockedRow(string label, string reason)
    {
        BeginInspectorField(label);
        ImGui.BeginDisabled();
        ImGui.TextDisabled("—");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(reason);
    }

    /// <summary>一行多选数值：全部相同显示可拖数值，不同显示横线输入框</summary>
    private void DrawMultiRow(string label, SceneObject[] objects, Func<SceneObject, double> read,
        Action<SceneObject, double> write)
    {
        var values = objects.Select(read).ToArray();
        var first = values[0];
        var mixed = values.Skip(1).Any(value => Math.Abs(value - first) > .000001D);
        BeginInspectorField(label);
        if (mixed)
        {
            DrawMixedInput("Transform:" + label, value =>
            {
                foreach (var item in objects) write(item, value);
                MarkSceneDocumentDirty();
                RebuildObjectVoxelWorld();
            });
            return;
        }

        var current = (float)first;
        if (ImGui.DragFloat("##Multi_" + label, ref current, label.StartsWith("位置") ? 1F : .05F))
        {
            foreach (var item in objects) write(item, current);
            MarkSceneDocumentDirty();
            RebuildObjectVoxelWorld();
        }
    }

    // 组件字段读写

    private static bool TryReadNumber(ComponentRecord component, string name, out double value)
    {
        value = 0D;
        if (component.Data.ValueKind != JsonValueKind.Object ||
            !component.Data.TryGetProperty(name, out var property)) return false;
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value);
    }

    private static bool TryReadBoolean(ComponentRecord component, string name, out bool value)
    {
        value = false;
        if (component.Data.ValueKind != JsonValueKind.Object ||
            !component.Data.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadString(ComponentRecord component, string name, out string value)
    {
        value = string.Empty;
        if (component.Data.ValueKind != JsonValueKind.Object ||
            !component.Data.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static void WriteNumber(ComponentRecord component, string name, double value)
    {
        var node = ToNode(component);
        node[name] = JsonValue.Create(value);
        component.Data = JsonSerializer.SerializeToElement(node);
    }

    private static void WriteBoolean(ComponentRecord component, string name, bool value)
    {
        var node = ToNode(component);
        node[name] = JsonValue.Create(value);
        component.Data = JsonSerializer.SerializeToElement(node);
    }

    private static void WriteString(ComponentRecord component, string name, string value)
    {
        var node = ToNode(component);
        node[name] = JsonValue.Create(value);
        component.Data = JsonSerializer.SerializeToElement(node);
    }

    private static JsonObject ToNode(ComponentRecord component) =>
        component.Data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(component.Data.GetRawText())!.AsObject()
            : new JsonObject();
}
