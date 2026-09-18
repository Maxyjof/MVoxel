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
/// 直线、长方体与洪水填充等扩展编辑工具
/// </summary>
public static class VoxelEditingTools
{
    /// <summary>
    /// 三维整数 Bresenham 直线覆盖的格子集合，两端点都包含，负方向正确
    /// </summary>
    /// <param name="from">起点格</param>
    /// <param name="to">终点格</param>
    /// <returns>沿线的格子列表</returns>
    public static IReadOnlyList<VoxelCoord64> LineCells(VoxelCoord64 from, VoxelCoord64 to)
    {
        var cells = new List<VoxelCoord64>();
        var x = from.X;
        var y = from.Y;
        var z = from.Z;
        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);
        var dz = Math.Abs(to.Z - from.Z);
        var stepX = (long)Math.Sign(to.X - from.X);
        var stepY = (long)Math.Sign(to.Y - from.Y);
        var stepZ = (long)Math.Sign(to.Z - from.Z);
        var errorX = (dx << 1) - dy - dz;
        var errorY = (dy << 1) - dx - dz;
        var errorZ = (dz << 1) - dx - dy;
        var steps = Math.Max(dx, Math.Max(dy, dz)) + 1;
        for (var step = 0; step < steps; step++)
        {
            cells.Add(new VoxelCoord64(x, y, z));
            if (step == steps - 1) break;
            var allNegative = errorX < 0 && errorY < 0 && errorZ < 0;
            var advanceX = allNegative || (errorX >= 0 && errorY < 0 && errorZ < 0) ||
                       (errorX >= 0 && errorY >= 0 && errorZ < 0) ||
                       (errorX >= 0 && errorY < 0 && errorZ >= 0) ||
                       (errorX >= 0 && errorY >= 0 && errorZ >= 0);
            var advanceY = allNegative || (errorX < 0 && errorY >= 0 && errorZ < 0) ||
                           (errorX >= 0 && errorY >= 0 && errorZ < 0) ||
                           (errorX < 0 && errorY >= 0 && errorZ >= 0) ||
                           (errorX >= 0 && errorY >= 0 && errorZ >= 0);
            var advanceZ = allNegative || (errorX < 0 && errorY < 0 && errorZ >= 0) ||
                           (errorX >= 0 && errorY < 0 && errorZ >= 0) ||
                           (errorX < 0 && errorY >= 0 && errorZ >= 0) ||
                           (errorX >= 0 && errorY >= 0 && errorZ >= 0);
            if (advanceX) { x += stepX; errorX -= dy + dz; }
            if (advanceY) { y += stepY; errorY -= dx + dz; }
            if (advanceZ) { z += stepZ; errorZ -= dx + dy; }
        }

        return cells;
    }

    /// <summary>
    /// 两个对角格确定的长方体格子集合，端点包含；空心只保留外壳
    /// </summary>
    /// <param name="cornerA">对角 A</param>
    /// <param name="cornerB">对角 B</param>
    /// <param name="hollow">是否只保留外壳</param>
    /// <returns>长方体覆盖的格子列表</returns>
    public static IReadOnlyList<VoxelCoord64> BoxCells(VoxelCoord64 cornerA, VoxelCoord64 cornerB, bool hollow)
    {
        var min = new VoxelCoord64(Math.Min(cornerA.X, cornerB.X), Math.Min(cornerA.Y, cornerB.Y), Math.Min(cornerA.Z, cornerB.Z));
        var max = new VoxelCoord64(Math.Max(cornerA.X, cornerB.X), Math.Max(cornerA.Y, cornerB.Y), Math.Max(cornerA.Z, cornerB.Z));
        var cells = new List<VoxelCoord64>();
        for (var z = min.Z; z <= max.Z; z++)
        for (var y = min.Y; y <= max.Y; y++)
        for (var x = min.X; x <= max.X; x++)
        {
            var onShell = x == min.X || x == max.X || y == min.Y || y == max.Y || z == min.Z || z == max.Z;
            if (hollow && !onShell) continue;
            cells.Add(new VoxelCoord64(x, y, z));
        }

        return cells;
    }

    /// <summary>
    /// 六邻接同值洪水填充：从起点扩散到连通的同值区域，起点必须是占用体素；
    /// 超过预算时返回已收集的部分并标记截断，由调用方决定是否二次确认
    /// </summary>
    /// <param name="world">目标世界</param>
    /// <param name="start">起点格</param>
    /// <param name="maxCells">最大格子数预算</param>
    /// <param name="truncated">达到预算仍未扫完时为 true</param>
    /// <returns>连通区域格子列表，起点为空体素时返回空列表</returns>
    public static IReadOnlyList<VoxelCoord64> FloodFillCells(VoxelWorld world, VoxelCoord64 start,
        int maxCells, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(world);
        truncated = false;
        if (!world.TryGet(start, out var target)) return [];
        var visited = new HashSet<VoxelCoord64> { start };
        var cells = new List<VoxelCoord64> { start };
        var queue = new Queue<VoxelCoord64>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            for (var direction = 0; direction < 6; direction++)
            {
                var (dx, dy, dz) = direction switch
                {
                    0 => (1L, 0L, 0L), 1 => (-1L, 0L, 0L), 2 => (0L, 1L, 0L),
                    3 => (0L, -1L, 0L), 4 => (0L, 0L, 1L), _ => (0L, 0L, -1L),
                };
                var next = new VoxelCoord64(current.X + dx, current.Y + dy, current.Z + dz);
                if (!visited.Add(next)) continue;
                if (!world.TryGet(next, out var value) || value != target) continue;
                cells.Add(next);
                if (cells.Count >= maxCells)
                {
                    truncated = true;
                    return cells;
                }

                queue.Enqueue(next);
            }
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
    /// <param name="after">统一写入的体素值，空值表示删除</param>
    public VoxelStrokeCommand(string name, VoxelWorld world, IEnumerable<VoxelCoord64> cells, VoxelValue after)
        : this(name, world, cells.Select(cell => (cell, after)))
    {
    }

    /// <summary>
    /// 捕获逐格不同目标值的批量差异，同一格子多次出现时以最后一次为准
    /// </summary>
    /// <param name="name">命令显示名</param>
    /// <param name="world">目标世界</param>
    /// <param name="cells">格子与各自目标值的序列，空值表示删除</param>
    public VoxelStrokeCommand(string name, VoxelWorld world, IEnumerable<(VoxelCoord64 Coordinate, VoxelValue After)> cells)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(cells);
        Name = name;
        _world = world;
        var byCoordinate = new Dictionary<VoxelCoord64, VoxelValue>();
        foreach (var (coordinate, after) in cells)
        {
            byCoordinate[coordinate] = after;
        }

        var edits = new List<(VoxelCoord64, VoxelValue, VoxelValue)>(byCoordinate.Count);
        foreach (var (coordinate, after) in byCoordinate)
        {
            var before = world.TryGet(coordinate, out var existing) ? existing : default;
            if (before == after) continue;
            edits.Add((coordinate, before, after));
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
