using System.Buffers.Binary;
using System.Globalization;

namespace MVoxel.Voxels.Mesh;

/// <summary>
/// STL 读取器：自动识别二进制与 ASCII 形态。二进制按 80 字节头 + 面数 + 50 字节面记录
/// 直接读取；ASCII 按 facet/vertex 关键字提取。法线仅作跳过，几何以顶点为准
/// </summary>
internal static class StlReader
{
    private const int BinaryFacetBytes = 50;

    /// <summary>
    /// 从文件读取并校验网格
    /// </summary>
    /// <param name="path">.stl 文件路径</param>
    /// <returns>三角网格</returns>
    internal static MeshData Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// 从字节内容解析网格：二进制魔数特征优先，否则按 ASCII 解析
    /// </summary>
    /// <param name="bytes">STL 文件内容</param>
    /// <returns>三角网格</returns>
    internal static MeshData Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 84)
            throw new InvalidDataException("STL 文件过小");

        // 二进制 STL：80 字节头 + uint32 面数，总长必须严格等于 84 + 50*面数；
        // 满足即判二进制，否则尝试 ASCII
        var facetCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[80..]);
        if (facetCount > 0 &&
            facetCount <= MeshData.MaximumIndexCount / 3 &&
            bytes.Length == 84L + (long)facetCount * BinaryFacetBytes)
            return ParseBinary(bytes, (int)facetCount);
        return ParseAscii(System.Text.Encoding.ASCII.GetString(bytes));
    }

    private static MeshData ParseBinary(ReadOnlySpan<byte> bytes, int facetCount)
    {
        var positions = new float[facetCount * 9];
        var indices = new int[facetCount * 3];
        var offset = 84;
        for (var facet = 0; facet < facetCount; facet++)
        {
            // 跳过法线 12 字节，读三个顶点
            for (var vertex = 0; vertex < 3; vertex++)
            {
                for (var component = 0; component < 3; component++)
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(
                        bytes[(offset + 12 + vertex * 12 + component * 4)..]);
                    if (!float.IsFinite(value))
                        throw new InvalidDataException("STL 顶点包含 NaN 或无穷坐标");
                    positions[facet * 9 + vertex * 3 + component] = value;
                }
            }

            indices[facet * 3] = facet * 3;
            indices[facet * 3 + 1] = facet * 3 + 1;
            indices[facet * 3 + 2] = facet * 3 + 2;
            offset += BinaryFacetBytes;
        }

        return new MeshData { Positions = positions, Indices = indices }.Validate();
    }

    private static MeshData ParseAscii(string text)
    {
        var positions = new List<float>();
        var indices = new List<int>();
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var facetVertices = new List<float>(9);
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "vertex" when i + 3 < tokens.Length:
                    facetVertices.Add(ParseFloat(tokens[i + 1]));
                    facetVertices.Add(ParseFloat(tokens[i + 2]));
                    facetVertices.Add(ParseFloat(tokens[i + 3]));
                    i += 3;
                    break;
                case "endfacet":
                    if (facetVertices.Count != 9)
                        throw new InvalidDataException("ASCII STL 面顶点数量不是 3 个");
                    foreach (var value in facetVertices)
                        positions.Add(value);
                    var start = positions.Count / 3 - 3;
                    indices.Add(start);
                    indices.Add(start + 1);
                    indices.Add(start + 2);
                    facetVertices.Clear();
                    break;
                default:
                    break;
            }
        }

        return new MeshData { Positions = positions.ToArray(), Indices = indices.ToArray() }.Validate();
    }

    private static float ParseFloat(string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            throw new InvalidDataException($"STL 顶点坐标非法：{text}");
        return value;
    }
}
