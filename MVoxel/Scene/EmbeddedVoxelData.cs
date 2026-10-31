using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 模型组件内嵌体素的读写，坐标与体素属性均完整保存
/// </summary>
internal static class EmbeddedVoxelData
{
    internal const string PropertyName = "embeddedVoxels";
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly record struct Cell(long X, long Y, long Z, byte R, byte G, byte B, byte A,
        ushort MaterialSlot, byte Flags);

    private sealed record Payload(int Version, Cell[] Cells);

    internal static bool HasData(ComponentRecord component) =>
        component.Data.ValueKind == JsonValueKind.Object &&
        component.Data.TryGetProperty(PropertyName, out var data) && data.ValueKind == JsonValueKind.Object;

    internal static VoxelWorld Read(ComponentRecord component)
    {
        if (!SceneComponentTypes.IsVoxelModel(component) || !HasData(component))
            throw new InvalidDataException("模型组件不包含内嵌体素数据");
        var data = component.Data.GetProperty(PropertyName);
        var payload = data.Deserialize<Payload>(Options) ?? throw new InvalidDataException("内嵌体素数据为空");
        if (payload.Version != FormatVersion || payload.Cells is null)
            throw new InvalidDataException("内嵌体素数据格式无效或版本不受支持");
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        foreach (var cell in payload.Cells)
        {
            var coordinate = new VoxelCoord64(cell.X, cell.Y, cell.Z);
            var value = new VoxelValue(new Rgba32(cell.R, cell.G, cell.B, cell.A), cell.MaterialSlot, cell.Flags);
            if (value.IsEmpty || world.TryGet(coordinate, out _))
                throw new InvalidDataException("内嵌体素包含空数据或重复坐标");
            writer.Set(coordinate, value);
        }
        return world;
    }

    internal static void Remove(ComponentRecord component)
    {
        if (!HasData(component)) return;
        var json = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
        json.Remove(PropertyName);
        component.Data = JsonSerializer.SerializeToElement(json, Options);
    }

    internal static void Write(ComponentRecord component, VoxelWorld world)
    {
        if (!SceneComponentTypes.IsVoxelModel(component))
            throw new ArgumentException("组件不是体素模型", nameof(component));
        ArgumentNullException.ThrowIfNull(world);
        var cells = world.EnumerateOccupied().Select(item => new Cell(
            item.Coordinate.X, item.Coordinate.Y, item.Coordinate.Z,
            item.Value.Color.R, item.Value.Color.G, item.Value.Color.B, item.Value.Color.A,
            item.Value.MaterialSlot, item.Value.Flags)).ToArray();
        var data = SceneComponentTypes.ReadVoxelModel(component);
        SceneComponentTypes.WriteVoxelModel(component, data with { Model = string.Empty, VoxelCount = cells.Length });
        var json = JsonNode.Parse(component.Data.GetRawText())!.AsObject();
        json[PropertyName] = JsonSerializer.SerializeToNode(new Payload(FormatVersion, cells), Options);
        component.Data = JsonSerializer.SerializeToElement(json, Options);
    }
}
