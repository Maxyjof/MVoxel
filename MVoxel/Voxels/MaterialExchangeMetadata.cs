using System.Text.Json;
using MVoxel.Project;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 原生交换材质表，标识只在当前文档内有效，不是项目路径
/// </summary>
public sealed class MaterialExchangeMetadata
{
    /// <summary>单模型体素相对于资产原点的包围盒偏移；旧文件默认为零。</summary>
    public VoxelCoord64 OriginOffset { get; init; } = VoxelCoord64.Zero;

    public Dictionary<ushort, string> Bindings { get; init; } = [];

    /// <summary>多对象导出时按稳定对象ID保存各对象的本地槽位映射</summary>
    public Dictionary<string, Dictionary<ushort, string>> ObjectBindings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, MaterialAsset> Definitions { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 预烘焙 LOD 链，按对象标识存放（单模型文件使用空键）；值为 <see cref="ModelLodChain.Encode"/> 的结果。
    /// 独立模型资产因此把简化数据放在资产文件内部，与场景和预制体各自保存内嵌数据的做法一致。
    /// </summary>
    public Dictionary<string, string> BakedLod { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 校验引用完整性，槽0始终保留为默认材质
    /// </summary>
    public void Validate()
    {
        if (Bindings is null || ObjectBindings is null || Definitions is null || BakedLod is null)
            throw new InvalidDataException("材质元数据缺少映射或定义");
        ValidateBindings(Bindings);
        foreach (var (objectId, bindings) in ObjectBindings)
        {
            if (string.IsNullOrWhiteSpace(objectId) || bindings is null)
                throw new InvalidDataException("对象材质映射标识或内容为空");
            ValidateBindings(bindings);
        }
        foreach (var (id, material) in Definitions)
        {
            if (string.IsNullOrWhiteSpace(id) || material is null)
                throw new InvalidDataException("材质定义标识或内容为空");
        }
        foreach (var (objectId, encoded) in BakedLod)
        {
            if (objectId is null || string.IsNullOrWhiteSpace(encoded))
                throw new InvalidDataException("预烘焙 LOD 数据标识或内容为空");
        }
    }

    private void ValidateBindings(IReadOnlyDictionary<ushort, string> bindings)
    {
        foreach (var (slot, id) in bindings)
        {
            if (slot == 0 || string.IsNullOrWhiteSpace(id) || !Definitions.ContainsKey(id))
                throw new InvalidDataException($"材质槽{slot}引用无效，槽0不能绑定资产");
        }
    }
}

public sealed record NativeVoxelPart(MVoxel.Core.StableId ObjectId, string Name, VoxelCoord64 Origin,
    VoxelWorld World, IReadOnlyDictionary<ushort, string> MaterialBindings);

public sealed record NativeVoxelPartsDocument(IReadOnlyList<NativeVoxelPart> Parts, MaterialExchangeMetadata Materials);

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
