using System.Numerics;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 笔刷形状
/// </summary>
public enum VoxelBrushShape
{
    /// <summary>
    /// 立方体笔刷
    /// </summary>
    Cube,

    /// <summary>
    /// 球形笔刷，切掉立方角
    /// </summary>
    Sphere,
}

/// <summary>
/// 纯笔刷几何计算：给定 DDA 命中（格子与进入面）产出一笔应当写入的格子集合
/// 绘制把笔刷放在命中面的外侧，擦除以命中格自身为中心
/// </summary>
public static class VoxelBrush
{
    /// <summary>
    /// 依据命中结果计算描边格子集合
    /// </summary>
    /// <param name="hit">DDA 命中</param>
    /// <param name="erase">true 表示擦除（以命中格为中心），false 表示绘制（贴到命中面外侧）</param>
    /// <param name="shape">笔刷形状</param>
    /// <param name="radius">半径，1 表示单格</param>
    /// <returns>要写入的格子列表</returns>
    public static IReadOnlyList<VoxelCoord64> CellsForStroke(
        VoxelDda.Hit hit, bool erase, VoxelBrushShape shape, int radius)
    {
        radius = Math.Clamp(radius, 1, 8);
        var center = erase
            ? hit.Coordinate
            : hit.Coordinate + hit.Normal;
        return CellsAround(center, shape, radius);
    }

    /// <summary>
    /// 拾取落空但命中地面时，让笔刷贴着地面平面落笔
    /// </summary>
    /// <param name="groundPoint">地面命中点</param>
    /// <param name="shape">笔刷形状</param>
    /// <param name="radius">半径</param>
    /// <returns>要写入的格子列表</returns>
    public static IReadOnlyList<VoxelCoord64> CellsForGround(Vector3 groundPoint, VoxelBrushShape shape, int radius)
    {
        radius = Math.Clamp(radius, 1, 8);
        var cell = new VoxelCoord64(
            (long)MathF.Floor(groundPoint.X),
            (long)MathF.Floor(groundPoint.Y), // 平面命中在 y=0，落在 [0,1) 这一层格子里
            (long)MathF.Floor(groundPoint.Z));
        return CellsAround(cell, shape, radius);
    }

    /// <summary>
    /// 以某格为中心生成形状覆盖的格子集合
    /// </summary>
    /// <param name="center">中心格</param>
    /// <param name="shape">笔刷形状</param>
    /// <param name="radius">半径</param>
    /// <returns>覆盖的格子列表</returns>
    public static IReadOnlyList<VoxelCoord64> CellsAround(VoxelCoord64 center, VoxelBrushShape shape, int radius)
    {
        var cells = new List<VoxelCoord64>();
        var reach = radius - 1;
        for (var y = -reach; y <= reach; y++)
        for (var z = -reach; z <= reach; z++)
        for (var x = -reach; x <= reach; x++)
        {
            if (shape == VoxelBrushShape.Sphere)
            {
                var squared = x * x + y * y + z * z;
                if (squared > radius * radius - radius * 0.5F) continue;
            }

            cells.Add(center + new VoxelCoord64(x, y, z));
        }

        return cells;
    }
}

/// <summary>
/// 一次可撤销的描边，逐格记录原值，Undo 精确恢复，
/// 同时持有世界引用供 Redo 重新施加
/// </summary>
public sealed class VoxelStrokeCommand : IEditorCommand
{
    private readonly VoxelWorld _world;
    private readonly (VoxelCoord64 Coordinate, VoxelValue Before, VoxelValue After)[] _edits;

    /// <summary>
    /// 捕获描边前后的逐格差异，目标值与当前值相同的格子自动跳过
    /// </summary>
    /// <param name="name">命令显示名</param>
    /// <param name="world">目标世界</param>
    /// <param name="cells">描边覆盖的格子</param>
    /// <param name="after">写入的体素值，空值表示删除</param>
    public VoxelStrokeCommand(string name, VoxelWorld world, IEnumerable<VoxelCoord64> cells, VoxelValue after)
    {
        ArgumentNullException.ThrowIfNull(world);
        Name = name;
        _world = world;
        var edits = new List<(VoxelCoord64, VoxelValue, VoxelValue)>();
        foreach (var cell in cells)
        {
            var before = world.TryGet(cell, out var existing) ? existing : default;
            if (before == after) continue;
            edits.Add((cell, before, after));
        }

        _edits = edits.ToArray();
    }

    /// <summary>
    /// 命令显示名
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 实际发生变化的格子数，为 0 时命令可以安全丢弃
    /// </summary>
    public int ChangedCellCount => _edits.Length;

    /// <summary>
    /// 执行（或重做）描边
    /// </summary>
    public void Execute()
    {
        foreach (var (coordinate, _, after) in _edits) _world.Set(coordinate, after);
    }

    /// <summary>
    /// 撤销描边，逐格恢复到执行前的值
    /// </summary>
    public void Undo()
    {
        foreach (var (coordinate, before, _) in _edits) _world.Set(coordinate, before);
    }
}
