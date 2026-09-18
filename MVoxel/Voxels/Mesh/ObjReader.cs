using System.Globalization;

namespace MVoxel.Voxels.Mesh;

/// <summary>
/// Wavefront OBJ 读取器：解析 v 与 f 记录，支持多边形扇形三角化、负索引、
/// v/vt/vn 组合面顶点形式；其余记录（vn、vt、usemtl、o、s 等）安全忽略。
/// 任何无法恢复的结构错误抛 InvalidDataException
/// </summary>
internal static class ObjReader
{
    /// <summary>
    /// 从文件读取并校验网格
    /// </summary>
    /// <param name="path">.obj 文件路径</param>
    /// <returns>三角网格</returns>
    internal static MeshData Read(string path) => Parse(File.ReadLines(path));

    /// <summary>
    /// 从文本解析网格，供测试与内存导入共用
    /// </summary>
    /// <param name="text">OBJ 文本</param>
    /// <returns>三角网格</returns>
    internal static MeshData ParseText(string text) =>
        Parse(text.Split('\n').Select(line => line.TrimEnd('\r')));

    private static MeshData Parse(IEnumerable<string> lines)
    {
        var positions = new List<float>();
        var indices = new List<int>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (fields[0])
            {
                case "v" when fields.Length >= 4:
                    positions.Add(ParseFloat(fields[1]));
                    positions.Add(ParseFloat(fields[2]));
                    positions.Add(ParseFloat(fields[3]));
                    if (positions.Count > MeshData.MaximumVertexCount * 3)
                        throw new InvalidDataException("网格顶点数超过上限");
                    break;
                case "f" when fields.Length >= 4:
                    ParseFace(fields, positions.Count / 3, indices);
                    break;
                default:
                    break;
            }
        }

        return new MeshData { Positions = positions.ToArray(), Indices = indices.ToArray() }.Validate();
    }

    /// <summary>
    /// 面记录扇形三角化：每个顶点取 v 索引部分，支持负索引（相对当前顶点数）
    /// </summary>
    private static void ParseFace(string[] fields, int vertexCount, List<int> indices)
    {
        var corners = new List<int>(fields.Length - 1);
        foreach (var field in fields[1..])
        {
            var slash = field.IndexOf('/');
            var indexText = slash < 0 ? field : field[..slash];
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawIndex))
                throw new InvalidDataException($"面顶点索引非法：{field}");
            var resolved = rawIndex > 0 ? rawIndex - 1 : vertexCount + rawIndex;
            if (resolved < 0 || resolved >= vertexCount)
                throw new InvalidDataException($"面顶点索引越界：{field}");
            corners.Add(resolved);
        }

        for (var i = 2; i < corners.Count; i++)
        {
            indices.Add(corners[0]);
            indices.Add(corners[i - 1]);
            indices.Add(corners[i]);
        }
    }

    private static float ParseFloat(string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            throw new InvalidDataException($"顶点坐标非法：{text}");
        return value;
    }
}
