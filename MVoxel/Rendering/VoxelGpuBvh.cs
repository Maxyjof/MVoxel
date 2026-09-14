using System.Runtime.InteropServices;

namespace MVoxel.Rendering;

/// <summary>
/// 体素 GPU BVH 的构建结果
/// </summary>
public sealed class VoxelGpuBvh
{
    /// <summary>
    /// GPU BVH 节点的字节尺寸
    /// </summary>
    public const int PackedNodeSize = 48;

    private VoxelGpuBvh(VoxelGpuCell[] orderedCells, byte[] nodeBytes, int nodeCount)
    {
        OrderedCells = orderedCells;
        NodeBytes = nodeBytes;
        NodeCount = nodeCount;
    }

    /// <summary>
    /// 按 BVH 叶节点顺序排列的体素
    /// </summary>
    public IReadOnlyList<VoxelGpuCell> OrderedCells { get; }

    /// <summary>
    /// 符合 std430 布局的节点字节流
    /// </summary>
    public byte[] NodeBytes { get; }

    /// <summary>
    /// BVH 节点数量
    /// </summary>
    public int NodeCount { get; }

    /// <summary>
    /// 从 GPU 工作集构建中位数分割 BVH
    /// </summary>
    /// <param name="workset">待构建的体素工作集</param>
    /// <returns>GPU 可直接上传的 BVH</returns>
    public static VoxelGpuBvh Build(VoxelGpuWorkset workset)
    {
        ArgumentNullException.ThrowIfNull(workset);
        if (workset.IsEmpty)
        {
            return new VoxelGpuBvh([], [], 0);
        }

        var nodes = new List<BvhNode>(Math.Max(1, workset.Cells.Count / 4));
        var orderedIndices = new List<int>(workset.Cells.Count);
        var indices = Enumerable.Range(0, workset.Cells.Count).ToList();
        BuildNode(workset.Cells, indices, nodes, orderedIndices);

        var orderedCells = new VoxelGpuCell[orderedIndices.Count];
        for (var index = 0; index < orderedIndices.Count; index++)
        {
            orderedCells[index] = workset.Cells[orderedIndices[index]];
        }

        var nodeBytes = new byte[checked(nodes.Count * PackedNodeSize)];
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var offset = index * PackedNodeSize;
            Write(nodeBytes, offset + 0, node.MinX);
            Write(nodeBytes, offset + 4, node.MinY);
            Write(nodeBytes, offset + 8, node.MinZ);
            Write(nodeBytes, offset + 12, 0);
            Write(nodeBytes, offset + 16, node.MaxX);
            Write(nodeBytes, offset + 20, node.MaxY);
            Write(nodeBytes, offset + 24, node.MaxZ);
            Write(nodeBytes, offset + 28, 0);
            Write(nodeBytes, offset + 32, node.Left);
            Write(nodeBytes, offset + 36, node.Right);
            Write(nodeBytes, offset + 40, node.Start);
            Write(nodeBytes, offset + 44, node.Count);
        }

        return new VoxelGpuBvh(orderedCells, nodeBytes, nodes.Count);
    }

    /// <summary>
    /// 递归创建一个 BVH 节点
    /// </summary>
    private static int BuildNode(IReadOnlyList<VoxelGpuCell> cells, List<int> indices,
        List<BvhNode> nodes, List<int> orderedIndices)
    {
        GetBounds(cells, indices, out var minX, out var minY, out var minZ,
            out var maxX, out var maxY, out var maxZ);
        var nodeIndex = nodes.Count;
        nodes.Add(default);

        if (indices.Count <= 8)
        {
            var start = orderedIndices.Count;
            orderedIndices.AddRange(indices);
            nodes[nodeIndex] = new BvhNode(minX, minY, minZ, maxX + 1, maxY + 1, maxZ + 1,
                -1, -1, start, indices.Count);
            return nodeIndex;
        }

        var extentX = maxX - minX;
        var extentY = maxY - minY;
        var extentZ = maxZ - minZ;
        var axis = extentX >= extentY && extentX >= extentZ ? 0 : extentY >= extentZ ? 1 : 2;
        indices.Sort((left, right) => GetCenter(cells[left], axis).CompareTo(GetCenter(cells[right], axis)));
        var middle = indices.Count / 2;
        var leftIndices = indices.GetRange(0, middle);
        var rightIndices = indices.GetRange(middle, indices.Count - middle);
        var left = BuildNode(cells, leftIndices, nodes, orderedIndices);
        var right = BuildNode(cells, rightIndices, nodes, orderedIndices);
        nodes[nodeIndex] = new BvhNode(minX, minY, minZ, maxX + 1, maxY + 1, maxZ + 1,
            left, right, 0, 0);
        return nodeIndex;
    }

    /// <summary>
    /// 读取一组体素的包围盒
    /// </summary>
    private static void GetBounds(IReadOnlyList<VoxelGpuCell> cells, IReadOnlyList<int> indices,
        out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
    {
        var first = cells[indices[0]];
        minX = maxX = first.X;
        minY = maxY = first.Y;
        minZ = maxZ = first.Z;
        for (var index = 1; index < indices.Count; index++)
        {
            var cell = cells[indices[index]];
            minX = Math.Min(minX, cell.X);
            minY = Math.Min(minY, cell.Y);
            minZ = Math.Min(minZ, cell.Z);
            maxX = Math.Max(maxX, cell.X);
            maxY = Math.Max(maxY, cell.Y);
            maxZ = Math.Max(maxZ, cell.Z);
        }
    }

    /// <summary>
    /// 读取体素中心在指定轴上的排序值
    /// </summary>
    private static int GetCenter(VoxelGpuCell cell, int axis) => axis switch
    {
        0 => cell.X,
        1 => cell.Y,
        _ => cell.Z
    };

    /// <summary>
    /// 把整数写入 GPU 节点字节流
    /// </summary>
    private static void Write(byte[] bytes, int offset, int value) =>
        MemoryMarshal.Write(bytes.AsSpan(offset, sizeof(int)), in value);

    private readonly record struct BvhNode(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ,
        int Left, int Right, int Start, int Count);
}
