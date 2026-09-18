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

    private VoxelGpuBvh(IReadOnlyList<VoxelGpuCell> orderedCells, byte[] nodeBytes, int nodeCount)
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

        var cells = workset.Cells;
        if (cells.Count >= 100_000)
        {
            return BuildOrderedFast(cells);
        }

        var nodes = new List<BvhNode>(Math.Max(1, cells.Count / 4));
        var orderedIndices = new List<int>(cells.Count);
        var indices = Enumerable.Range(0, cells.Count).ToArray();
        BuildNode(cells, indices, 0, indices.Length, nodes, orderedIndices, true);

        var orderedCells = new VoxelGpuCell[orderedIndices.Count];
        for (var index = 0; index < orderedIndices.Count; index++)
        {
            orderedCells[index] = cells[orderedIndices[index]];
        }

        return new VoxelGpuBvh(orderedCells, SerializeNodes(nodes, nodes.Count), nodes.Count);
    }

    /// <summary>
    /// 大工作集快速构建：树形与旧输入序路径逐节点一致（对半切分、8 条叶），
    /// 但先做纯算术结构确定节点编号，叶界并行扫描，内部界自底向上合并，
    /// 消除旧实现里每个节点重新全区间求界的 O(N log N) 串行扫描
    /// </summary>
    private static VoxelGpuBvh BuildOrderedFast(IReadOnlyList<VoxelGpuCell> cells)
    {
        var nodeTotal = CountStructure(cells.Count);
        var left = new int[nodeTotal];
        var right = new int[nodeTotal];
        var start = new int[nodeTotal];
        var leafCount = new int[nodeTotal];
        var minX = new int[nodeTotal];
        var minY = new int[nodeTotal];
        var minZ = new int[nodeTotal];
        var maxX = new int[nodeTotal];
        var maxY = new int[nodeTotal];
        var maxZ = new int[nodeTotal];
        var next = 0;
        BuildStructure(cells.Count, 0, cells.Count, left, right, start, leafCount, ref next);

        System.Threading.Tasks.Parallel.For(0, next, FastBuildOptions, index =>
        {
            if (left[index] >= 0)
            {
                return;
            }

            var first = cells[start[index]];
            var nodeMinX = first.X;
            var nodeMinY = first.Y;
            var nodeMinZ = first.Z;
            var nodeMaxX = first.X + first.ExtentX - 1;
            var nodeMaxY = first.Y + first.ExtentY - 1;
            var nodeMaxZ = first.Z + first.ExtentZ - 1;
            for (var offset = 1; offset < leafCount[index]; offset++)
            {
                var cell = cells[start[index] + offset];
                nodeMinX = Math.Min(nodeMinX, cell.X);
                nodeMinY = Math.Min(nodeMinY, cell.Y);
                nodeMinZ = Math.Min(nodeMinZ, cell.Z);
                nodeMaxX = Math.Max(nodeMaxX, cell.X + cell.ExtentX - 1);
                nodeMaxY = Math.Max(nodeMaxY, cell.Y + cell.ExtentY - 1);
                nodeMaxZ = Math.Max(nodeMaxZ, cell.Z + cell.ExtentZ - 1);
            }

            minX[index] = nodeMinX;
            minY[index] = nodeMinY;
            minZ[index] = nodeMinZ;
            maxX[index] = nodeMaxX;
            maxY[index] = nodeMaxY;
            maxZ[index] = nodeMaxZ;
        });

        // 前序编号保证子节点编号大于父节点，倒序遍历即可自底向上合并包围盒
        for (var index = next - 1; index >= 0; index--)
        {
            if (left[index] < 0)
            {
                continue;
            }

            var leftChild = left[index];
            var rightChild = right[index];
            minX[index] = Math.Min(minX[leftChild], minX[rightChild]);
            minY[index] = Math.Min(minY[leftChild], minY[rightChild]);
            minZ[index] = Math.Min(minZ[leftChild], minZ[rightChild]);
            maxX[index] = Math.Max(maxX[leftChild], maxX[rightChild]);
            maxY[index] = Math.Max(maxY[leftChild], maxY[rightChild]);
            maxZ[index] = Math.Max(maxZ[leftChild], maxZ[rightChild]);
        }

        var nodeBytes = new byte[checked(next * PackedNodeSize)];
        System.Threading.Tasks.Parallel.For(0, next, FastBuildOptions, index =>
        {
            var offset = index * PackedNodeSize;
            Write(nodeBytes, offset + 0, minX[index]);
            Write(nodeBytes, offset + 4, minY[index]);
            Write(nodeBytes, offset + 8, minZ[index]);
            Write(nodeBytes, offset + 12, 0);
            Write(nodeBytes, offset + 16, maxX[index] + 1);
            Write(nodeBytes, offset + 20, maxY[index] + 1);
            Write(nodeBytes, offset + 24, maxZ[index] + 1);
            Write(nodeBytes, offset + 28, 0);
            Write(nodeBytes, offset + 32, left[index]);
            Write(nodeBytes, offset + 36, right[index]);
            Write(nodeBytes, offset + 40, start[index]);
            Write(nodeBytes, offset + 44, leafCount[index]);
        });

        return new VoxelGpuBvh(cells, nodeBytes, next);
    }

    /// <summary>
    /// 快速并行构建使用的线程预算
    /// </summary>
    private static readonly System.Threading.Tasks.ParallelOptions FastBuildOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 16),
    };

    /// <summary>
    /// 递归统计树节点总数，与实际结构遍历使用相同的切分规则
    /// </summary>
    private static int CountStructure(int count)
    {
        if (count <= 8)
        {
            return 1;
        }

        return 1 + CountStructure(count / 2) + CountStructure(count - count / 2);
    }

    /// <summary>
    /// 纯算术结构遍历，按前序分配节点编号并登记叶区间，规则与旧输入序路径一致
    /// </summary>
    private static int BuildStructure(int total, int start2, int count, int[] left, int[] right,
        int[] start, int[] leafCount, ref int next)
    {
        var nodeIndex = next++;
        if (count <= 8)
        {
            left[nodeIndex] = -1;
            right[nodeIndex] = -1;
            start[nodeIndex] = start2;
            leafCount[nodeIndex] = count;
            return nodeIndex;
        }

        var middle = count / 2;
        var leftChild = BuildStructure(total, start2, middle, left, right, start, leafCount, ref next);
        var rightChild = BuildStructure(total, start2 + middle, count - middle, left, right, start, leafCount, ref next);
        left[nodeIndex] = leftChild;
        right[nodeIndex] = rightChild;
        return nodeIndex;
    }

    /// <summary>
    /// 把节点列表序列化为 std430 字节流，供小场景路径使用
    /// </summary>
    private static byte[] SerializeNodes(List<BvhNode> nodes, int nodeCount)
    {
        var nodeBytes = new byte[checked(nodes.Count * PackedNodeSize)];
        for (var index = 0; index < nodeCount; index++)
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

        return nodeBytes;
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
        minX = first.X;
        minY = first.Y;
        minZ = first.Z;
        maxX = first.X + first.ExtentX - 1;
        maxY = first.Y + first.ExtentY - 1;
        maxZ = first.Z + first.ExtentZ - 1;
        for (var index = 1; index < count; index++)
        {
            var cell = cells[indices[start + index]];
            minX = Math.Min(minX, cell.X);
            minY = Math.Min(minY, cell.Y);
            minZ = Math.Min(minZ, cell.Z);
            maxX = Math.Max(maxX, cell.X + cell.ExtentX - 1);
            maxY = Math.Max(maxY, cell.Y + cell.ExtentY - 1);
            maxZ = Math.Max(maxZ, cell.Z + cell.ExtentZ - 1);
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
