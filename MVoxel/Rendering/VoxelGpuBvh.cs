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
    /// 从 GPU 工作集构建适合体素求交的 BVH
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
        var indices = Enumerable.Range(0, workset.Cells.Count).ToArray();
        var useInputOrder = workset.Cells.Count >= 100_000;
        BuildNode(workset.Cells, indices, 0, indices.Length, nodes, orderedIndices, !useInputOrder);

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
    private static int BuildNode(IReadOnlyList<VoxelGpuCell> cells, int[] indices, int start, int count,
        List<BvhNode> nodes, List<int> orderedIndices, bool sortSpatially)
    {
        GetBounds(cells, indices, start, count, out var minX, out var minY, out var minZ,
            out var maxX, out var maxY, out var maxZ);
        var nodeIndex = nodes.Count;
        nodes.Add(default);

        if (count <= 8)
        {
            var leafStart = orderedIndices.Count;
            for (var index = 0; index < count; index++)
            {
                orderedIndices.Add(indices[start + index]);
            }

            nodes[nodeIndex] = new BvhNode(minX, minY, minZ, maxX + 1, maxY + 1, maxZ + 1,
                -1, -1, leafStart, count);
            return nodeIndex;
        }

        var extentX = maxX - minX;
        var extentY = maxY - minY;
        var extentZ = maxZ - minZ;
        var axis = extentX >= extentY && extentX >= extentZ ? 0 : extentY >= extentZ ? 1 : 2;
        if (sortSpatially)
        {
            Array.Sort(indices, start, count, new VoxelCenterComparer(cells, axis));
        }

        var middle = count / 2;
        var left = BuildNode(cells, indices, start, middle, nodes, orderedIndices, sortSpatially);
        var right = BuildNode(cells, indices, start + middle, count - middle, nodes, orderedIndices, sortSpatially);
        nodes[nodeIndex] = new BvhNode(minX, minY, minZ, maxX + 1, maxY + 1, maxZ + 1,
            left, right, 0, 0);
        return nodeIndex;
    }

    /// <summary>
    /// 读取一组体素的包围盒
    /// </summary>
    private static void GetBounds(IReadOnlyList<VoxelGpuCell> cells, int[] indices, int start, int count,
        out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
    {
        var first = cells[indices[start]];
        minX = maxX = first.X;
        minY = maxY = first.Y;
        minZ = maxZ = first.Z;
        for (var index = 1; index < count; index++)
        {
            var cell = cells[indices[start + index]];
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

    /// <summary>
    /// 按体素中心坐标对小型工作集进行空间排序
    /// </summary>
    private sealed class VoxelCenterComparer(IReadOnlyList<VoxelGpuCell> cells, int axis) : IComparer<int>
    {
        public int Compare(int left, int right) => GetCenter(cells[left], axis).CompareTo(GetCenter(cells[right], axis));
    }
}
