using System.Text.Json;
using MVoxel.Project;

namespace MVoxel.Voxels;

/// <summary>
/// 原生交换材质表，标识只在当前文档内有效，不是项目路径
/// </summary>
public sealed class MaterialExchangeMetadata
{
    public Dictionary<ushort, string> Bindings { get; init; } = [];
    public Dictionary<string, MaterialAsset> Definitions { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 校验引用完整性，槽0始终保留为默认材质
    /// </summary>
    public void Validate()
    {
        if (Bindings is null || Definitions is null)
            throw new InvalidDataException("材质元数据缺少映射或定义");
        foreach (var (slot, id) in Bindings)
        {
            if (slot == 0 || string.IsNullOrWhiteSpace(id) || !Definitions.ContainsKey(id))
                throw new InvalidDataException($"材质槽{slot}引用无效，槽0不能绑定资产");
        }
        foreach (var (id, material) in Definitions)
        {
            if (string.IsNullOrWhiteSpace(id) || material is null)
                throw new InvalidDataException("材质定义标识或内容为空");
        }
    }
}

/// <summary>
/// 原生模型与独立材质定义，导入方负责创建新资产并重建项目引用
/// </summary>
public sealed record NativeVoxelDocument(VoxelWorld World, MaterialExchangeMetadata Materials);

internal static class MaterialExchangeCodec
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static byte[] Encode(MaterialExchangeMetadata metadata)
    {
        metadata.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("材质元数据超过64MiB上限");
        return bytes;
    }

    internal static MaterialExchangeMetadata Decode(byte[] bytes)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<MaterialExchangeMetadata>(bytes, Options)
                ?? throw new InvalidDataException("材质元数据为空");
            metadata.Validate();
            return metadata;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("材质元数据损坏", exception);
        }
    }
}
