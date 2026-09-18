using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace MVoxel.Voxels.Mesh;

/// <summary>
/// glTF 2.0 读取器：支持 .glb 容器与 .gltf + data URI / 伴生 bin 缓冲。
/// 合并全部 mesh primitive 的三角形，位置取 POSITION 浮点访问器，
/// 索引支持 ubyte/ushort/uint，可无索引；读取材质基础色因子作为建议色。
/// 不支持 sparse 访问器与压缩扩展，遇到即受控拒绝
/// </summary>
internal static class GltfReader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBinary = 0x004E4942;
    private const int MaximumFileSize = 512 * 1024 * 1024;

    /// <summary>
    /// 从文件读取并校验网格，.gltf 的相对资源以文件所在目录解析
    /// </summary>
    /// <param name="path">.glb 或 .gltf 文件路径</param>
    /// <returns>三角网格</returns>
    internal static MeshData Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumFileSize)
            throw new InvalidDataException("glTF 文件超过 512MB 上限");
        var bytes = File.ReadAllBytes(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return Parse(bytes, directory);
    }

    /// <summary>
    /// 从内存内容解析网格（外部 bin 引用不可用时仅支持 data URI）
    /// </summary>
    /// <param name="bytes">文件内容</param>
    /// <param name="baseDirectory">相对资源解析目录，null 表示禁止外部文件</param>
    /// <returns>三角网格</returns>
    internal static MeshData Parse(byte[] bytes, string? baseDirectory)
    {
        byte[] jsonBytes;
        byte[]? binChunk = null;
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic)
        {
            var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            if (version != 2)
                throw new InvalidDataException($"不支持的 GLB 版本 {version}");
            var total = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
            if (total < 12 || total > bytes.Length)
                throw new InvalidDataException("GLB 声明长度与实际内容不符");
            var offset = 12;
            jsonBytes = ReadChunk(bytes, ref offset, ChunkJson);
            // BIN 块可选，且块顺序只保证 JSON 在前
            if (offset + 8 <= total && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)) == ChunkBinary)
                binChunk = ReadChunk(bytes, ref offset, ChunkBinary);
        }
        else
        {
            jsonBytes = bytes;
        }

        using var document = ParseJson(jsonBytes);
        try
        {
            var root = document.RootElement;
            var buffers = LoadBuffers(root, binChunk, baseDirectory);
            return Assemble(root, buffers);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or IndexOutOfRangeException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"glTF 结构非法：{exception.Message}");
        }
    }

    private static JsonDocument ParseJson(byte[] jsonBytes)
    {
        var text = Encoding.UTF8.GetString(jsonBytes);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"glTF JSON 非法：{exception.Message}");
        }
    }

    private static byte[] ReadChunk(byte[] bytes, ref int offset, uint expectedType)
    {
        if (offset + 8 > bytes.Length)
            throw new InvalidDataException("glTF 块头不完整");
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
        if (type != expectedType || length < 0 || offset + 8 + length > bytes.Length)
            throw new InvalidDataException("glTF 块类型或长度非法");
        var data = bytes[(offset + 8)..(offset + 8 + length)];
        offset += 8 + length;
        return data;
    }

    private static List<byte[]> LoadBuffers(JsonElement root, byte[]? binChunk, string? baseDirectory)
    {
        var buffers = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out var bufferArray))
            return buffers;
        foreach (var buffer in bufferArray.EnumerateArray())
        {
            var byteLength = buffer.TryGetProperty("byteLength", out var lengthElement) ? lengthElement.GetInt32() : -1;
            if (byteLength < 0)
                throw new InvalidDataException("glTF 缓冲缺少 byteLength");
            if (!buffer.TryGetProperty("uri", out var uriElement))
            {
                // 无 uri 即 GLB 内嵌 BIN 块
                if (binChunk is null || binChunk.Length < byteLength)
                    throw new InvalidDataException("glTF 引用内嵌缓冲但 BIN 块缺失");
                buffers.Add(binChunk);
                continue;
            }

            var uri = uriElement.GetString() ?? throw new InvalidDataException("glTF 缓冲 uri 非法");
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = uri.IndexOf(',');
                if (comma < 0 || !uri[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("glTF 仅支持 base64 data URI");
                buffers.Add(Convert.FromBase64String(uri[(comma + 1)..]));
            }
            else
            {
                if (baseDirectory is null)
                    throw new InvalidDataException("内存解析不支持外部 bin 引用");
                var combined = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(uri)));
                if (!combined.StartsWith(Path.GetFullPath(baseDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("glTF 外部资源路径越出文件目录");
                buffers.Add(File.ReadAllBytes(combined));
            }
        }

        return buffers;
    }

    private static MeshData Assemble(JsonElement root, List<byte[]> buffers)
    {
        var positions = new List<float>();
        var indices = new List<int>();
        Rgba32? suggestedColor = null;
        if (!root.TryGetProperty("meshes", out var meshes))
            throw new InvalidDataException("glTF 没有 meshes");
        foreach (var mesh in meshes.EnumerateArray())
        {
            if (!mesh.TryGetProperty("primitives", out var primitives))
                continue;
            foreach (var primitive in primitives.EnumerateArray())
            {
                var accessorIndices = root.GetProperty("accessors");
            var bufferViews = root.GetProperty("bufferViews");
            var triangleBase = positions.Count / 3;
            foreach (var position in ReadFloatAttribute(primitive, accessorIndices, bufferViews, buffers, "POSITION"))
                positions.Add(position);
            if (primitive.TryGetProperty("indices", out var indicesAccessorElement))
            {
                foreach (var index in ReadScalarAccessor(indicesAccessorElement.GetInt32(), accessorIndices, bufferViews, buffers))
                {
                    if (triangleBase + index >= positions.Count / 3)
                        throw new InvalidDataException("glTF 索引越界");
                    indices.Add(triangleBase + index);
                }
            }
            else
            {
                var vertexCount = (positions.Count - triangleBase * 3) / 3;
                if (vertexCount % 3 != 0)
                    throw new InvalidDataException("glTF 无索引顶点数不是 3 的倍数");
                for (var i = 0; i < vertexCount; i++)
                    indices.Add(triangleBase + i);
            }

                if (suggestedColor is null &&
                    primitive.TryGetProperty("material", out var materialIndex) &&
                    root.TryGetProperty("materials", out var materials) &&
                    materialIndex.TryGetInt32(out var materialSlot) && materialSlot >= 0 && materialSlot < materials.GetArrayLength())
                    suggestedColor = ReadBaseColor(materials[materialSlot]);
            }
        }

        return new MeshData
        {
            Positions = positions.ToArray(),
            Indices = indices.ToArray(),
            SuggestedColor = suggestedColor,
        }.Validate();
    }

    private static JsonElement AccessorAt(JsonElement accessors, int index)
    {
        if (index < 0 || index >= accessors.GetArrayLength())
            throw new InvalidDataException("glTF 访问器索引越界");
        return accessors[index];
    }

    private static Rgba32? ReadBaseColor(JsonElement material)
    {
        if (!material.TryGetProperty("pbrMetallicRoughness", out var pbr) ||
            !pbr.TryGetProperty("baseColorFactor", out var factor) ||
            factor.GetArrayLength() < 3)
            return null;
        var r = (int)Math.Round(Math.Clamp(factor[0].GetSingle(), 0F, 1F) * 255F);
        var g = (int)Math.Round(Math.Clamp(factor[1].GetSingle(), 0F, 1F) * 255F);
        var b = (int)Math.Round(Math.Clamp(factor[2].GetSingle(), 0F, 1F) * 255F);
        var a = factor.GetArrayLength() >= 4 ? (int)Math.Round(Math.Clamp(factor[3].GetSingle(), 0F, 1F) * 255F) : 255;
        return new Rgba32((byte)r, (byte)g, (byte)b, (byte)a);
    }

    private static IEnumerable<float> ReadFloatAttribute(JsonElement primitive, JsonElement accessors,
        JsonElement bufferViews, List<byte[]> buffers, string attributeName)
    {
        if (!primitive.TryGetProperty("attributes", out var attributes) ||
            !attributes.TryGetProperty(attributeName, out var accessorElement))
            throw new InvalidDataException($"glTF primitive 缺少 {attributeName}");
        var accessor = AccessorAt(accessors, accessorElement.GetInt32());
        if (accessor.TryGetProperty("sparse", out _) ||
            accessor.GetProperty("type").GetString() != "VEC3" ||
            accessor.GetProperty("componentType").GetInt32() != 5126)
            throw new InvalidDataException($"glTF {attributeName} 访问器形态不受支持");
        var count = accessor.GetProperty("count").GetInt32();
        var bytes = AccessorBytes(accessor, bufferViews, buffers);
        var byteOffset = accessor.TryGetProperty("byteOffset", out var offsetElement) ? offsetElement.GetInt32() : 0;
        if (bytes.Length < byteOffset + count * 12)
            throw new InvalidDataException($"glTF {attributeName} 缓冲长度不足");
        for (var i = 0; i < count * 3; i++)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(byteOffset + i * 4));
            if (!float.IsFinite(value))
                throw new InvalidDataException($"glTF {attributeName} 包含 NaN 或无穷坐标");
            yield return value;
        }
    }

    private static IEnumerable<int> ReadScalarAccessor(int accessorIndex, JsonElement accessors,
        JsonElement bufferViews, List<byte[]> buffers)
    {
        var accessor = AccessorAt(accessors, accessorIndex);
        if (accessor.TryGetProperty("sparse", out _) ||
            accessor.GetProperty("type").GetString() != "SCALAR")
            throw new InvalidDataException("glTF 索引访问器形态不受支持");
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var componentSize = componentType switch
        {
            5121 => 1,
            5123 => 2,
            5125 => 4,
            _ => throw new InvalidDataException($"glTF 索引 componentType {componentType} 不受支持"),
        };
        var count = accessor.GetProperty("count").GetInt32();
        if (count % 3 != 0)
            throw new InvalidDataException("glTF 索引数不是 3 的倍数");
        var bytes = AccessorBytes(accessor, bufferViews, buffers);
        var byteOffset = accessor.TryGetProperty("byteOffset", out var offsetElement) ? offsetElement.GetInt32() : 0;
        if (bytes.Length < byteOffset + count * componentSize)
            throw new InvalidDataException("glTF 索引缓冲长度不足");
        for (var i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(byteOffset + i * componentSize);
            yield return componentSize switch
            {
                1 => span[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                _ => (int)BinaryPrimitives.ReadUInt32LittleEndian(span),
            };
        }
    }

    private static byte[] AccessorBytes(JsonElement accessor, JsonElement bufferViews, List<byte[]> buffers)
    {
        var viewIndex = accessor.GetProperty("bufferView").GetInt32();
        if (viewIndex < 0 || viewIndex >= bufferViews.GetArrayLength())
            throw new InvalidDataException("glTF bufferView 索引越界");
        var view = bufferViews[viewIndex];
        var bufferIndex = view.GetProperty("buffer").GetInt32();
        if (bufferIndex < 0 || bufferIndex >= buffers.Count)
            throw new InvalidDataException("glTF 引用了不存在的缓冲");
        if (view.TryGetProperty("byteStride", out _))
            throw new InvalidDataException("暂不支持交错 stride 的 glTF 访问器");
        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetElement) ? viewOffsetElement.GetInt32() : 0;
        var viewLength = view.GetProperty("byteLength").GetInt32();
        var buffer = buffers[bufferIndex];
        if (viewOffset + viewLength > buffer.Length)
            throw new InvalidDataException("glTF bufferView 越出缓冲");
        return buffer.AsSpan(viewOffset, viewLength).ToArray();
    }
}
