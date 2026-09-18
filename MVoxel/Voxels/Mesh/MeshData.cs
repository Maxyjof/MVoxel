using System.Numerics;

namespace MVoxel.Voxels.Mesh;

/// <summary>
/// 通过校验的三角网格：位置三元组与三角形索引，附带轴对齐包围盒。
/// 所有读取器必须先做完整性与数值校验再构造它，损坏数据一律抛 InvalidDataException
/// </summary>
internal sealed class MeshData
{
    /// <summary>单个网格允许的顶点与索引上限，防御超大或恶意文件</summary>
    internal const int MaximumVertexCount = 4_000_000;

    internal const int MaximumIndexCount = 12_000_000;

    /// <summary>
    /// 顶点位置，每三个 float 一个点
    /// </summary>
    internal required float[] Positions { get; init; }

    /// <summary>
    /// 三角形索引，长度为 3 的倍数
    /// </summary>
    internal required int[] Indices { get; init; }

    /// <summary>
    /// 可选的导入建议色（来自 glTF 基础色因子等），null 表示使用调用方默认色
    /// </summary>
    internal Rgba32? SuggestedColor { get; init; }

    internal int VertexCount => Positions.Length / 3;

    internal int TriangleCount => Indices.Length / 3;

    internal Vector3 BoundsMin { get; private set; }

    internal Vector3 BoundsMax { get; private set; }

    /// <summary>
    /// 校验数组完整性与数值合法性，并计算包围盒；退化（零体积）网格抛异常
    /// </summary>
    /// <returns>完成包围盒计算的网格自身</returns>
    internal MeshData Validate()
    {
        if (Positions.Length % 3 != 0 || Positions.Length == 0)
            throw new InvalidDataException("网格顶点数组不是有限的三元组序列");
        if (VertexCount > MaximumVertexCount)
            throw new InvalidDataException($"网格顶点数 {VertexCount} 超过上限 {MaximumVertexCount}");
        if (Indices.Length == 0 || Indices.Length % 3 != 0)
            throw new InvalidDataException("网格索引数组不是非空的三角形序列");
        if (Indices.Length > MaximumIndexCount)
            throw new InvalidDataException($"网格索引数 {Indices.Length} 超过上限 {MaximumIndexCount}");

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < Positions.Length; i += 3)
        {
            var x = Positions[i];
            var y = Positions[i + 1];
            var z = Positions[i + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                throw new InvalidDataException("网格顶点包含 NaN 或无穷坐标");
            min = Vector3.Min(min, new Vector3(x, y, z));
            max = Vector3.Max(max, new Vector3(x, y, z));
        }

        for (var i = 0; i < Indices.Length; i++)
        {
            if (Indices[i] < 0 || Indices[i] >= VertexCount)
                throw new InvalidDataException($"网格索引 {Indices[i]} 超出顶点范围");
        }

        // 退化包围盒（任一无限延伸或全部零跨度）无法建立体素映射
        if (max.X - min.X < 1e-6F && max.Y - min.Y < 1e-6F && max.Z - min.Z < 1e-6F)
            throw new InvalidDataException("网格包围盒退化，无法体素化");

        BoundsMin = min;
        BoundsMax = max;
        return this;
    }
}
