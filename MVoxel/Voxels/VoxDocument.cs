using System.Text;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>MagicaVoxel 文件里的一块体素网格：只有尺寸与体素，没有世界位置</summary>
public sealed class VoxModel
{
    internal VoxModel(int sizeX, int sizeY, int sizeZ, List<VoxModelVoxel> voxels)
    {
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        Voxels = voxels;
        _voxels = voxels;
    }

    private readonly List<VoxModelVoxel> _voxels;

    /// <summary>网格 X 尺寸</summary>
    public int SizeX { get; }

    /// <summary>网格 Y 尺寸</summary>
    public int SizeY { get; }

    /// <summary>网格 Z 尺寸（MagicaVoxel 的重力方向）</summary>
    public int SizeZ { get; }

    /// <summary>该网格的全部非空体素，坐标是网格局部坐标</summary>
    public IReadOnlyList<VoxModelVoxel> Voxels { get; }

    /// <summary>
    /// 用文件里最终的色板重算颜色并丢掉全透明体素。MagicaVoxel 允许 RGBA 块写在
    /// XYZI 之后，解析体素时色板可能仍是默认值，必须在整份文件读完后再统一校正，
    /// 否则颜色和透明度都会跟原文件不一致。
    /// </summary>
    /// <param name="palette">文件最终的 256 色色板，索引 0 为保留位</param>
    internal void ApplyPalette(Rgba32[] palette)
    {
        var kept = 0;
        for (var index = 0; index < _voxels.Count; index++)
        {
            var voxel = _voxels[index];
            // XYZA 体素自带颜色，PaletteIndex 为 0，不参与色板解析
            if (voxel.PaletteIndex > 0) voxel = voxel with { Color = palette[voxel.PaletteIndex] };
            if (voxel.Color.A == 0) continue;
            _voxels[kept++] = voxel;
        }

        if (kept < _voxels.Count) _voxels.RemoveRange(kept, _voxels.Count - kept);
    }
}

/// <summary>模型内的一个体素，坐标沿用 MagicaVoxel 的 x/y/z 轴向</summary>
/// <param name="X">网格 X</param>
/// <param name="Y">网格 Y</param>
/// <param name="Z">网格 Z（重力方向）</param>
/// <param name="Color">调色板解析出的颜色</param>
/// <param name="PaletteIndex">调色板索引，同时是 MATL 材质的绑定键</param>
public readonly record struct VoxModelVoxel(byte X, byte Y, byte Z, Rgba32 Color, int PaletteIndex);

/// <summary>场景图展开后的一个模型实例：把某块网格按平移和旋转摆到世界里</summary>
public readonly record struct VoxInstance(
    int ModelIndex, string Name, int LayerId, bool Hidden, VoxelCoord64 Offset, VoxTransform Rotation);

/// <summary>VOX 使用的带符号轴置换，允许镜像；导入时烘焙进模型，不改变原生旋转约束。</summary>
public readonly record struct VoxTransform(sbyte M00, sbyte M01, sbyte M02, sbyte M10, sbyte M11,
    sbyte M12, sbyte M20, sbyte M21, sbyte M22)
{
    public static VoxTransform Identity => new(1,0,0,0,1,0,0,0,1);
    public sbyte this[int row, int column] => (row * 3 + column) switch
    {
        0 => M00, 1 => M01, 2 => M02, 3 => M10, 4 => M11, 5 => M12,
        6 => M20, 7 => M21, 8 => M22, _ => throw new ArgumentOutOfRangeException()
    };
    public VoxelCoord64 Apply(VoxelCoord64 p) => new(
        M00*p.X + M01*p.Y + M02*p.Z, M10*p.X + M11*p.Y + M12*p.Z,
        M20*p.X + M21*p.Y + M22*p.Z);
}

/// <summary>MagicaVoxel 图层：只用于分组与显隐，不承载体素数据</summary>
public readonly record struct VoxLayer(int Id, string Name, bool Hidden);

/// <summary>MagicaVoxel 材质：按调色板索引绑定，未写的字段保持 -1 表示不覆盖</summary>
/// <param name="Id">调色板索引</param>
/// <param name="Type">_diffuse / _metal / _glass / _emit / _media</param>
/// <param name="Weight">混合权重</param>
/// <param name="Roughness">粗糙度，-1 表示未指定</param>
/// <param name="Specular">旧版 _spec 镜面强度，-1 表示未指定</param>
/// <param name="Ior">_ior 字段；0.99.7 起是相对 1 的偏移量，只在缺少 _ri 时按折射率解释</param>
/// <param name="Absorption">旧版 _att 吸收系数，-1 表示未指定</param>
/// <param name="Flux">发光通量倍率 _flux，-1 表示未指定</param>
/// <param name="Metal">金属度 _metal，-1 表示未指定</param>
/// <param name="Transmission">透射率 _trans/_alpha，0 不透光、1 全透光，-1 表示未指定</param>
/// <param name="Emissive">发光强度 _emit，-1 表示未指定</param>
/// <param name="RefractionIndex">真实折射率 _ri，-1 表示未指定</param>
/// <param name="Density">介质密度 _d，-1 表示未指定</param>
/// <param name="ScatterAnisotropy">散射各向异性 _g，-1 表示未指定</param>
/// <param name="SpecularLevel">镜面档位 _sp，-1 表示未指定</param>
/// <param name="MediaType">体积介质类型 _media_type（_scatter / _emit），未指定为 null</param>
public readonly record struct VoxMaterial(int Id, string Type, float Weight, float Roughness,
    float Specular, float Ior, float Absorption, float Flux,
    float Metal = -1F, float Transmission = -1F, float Emissive = -1F,
    float RefractionIndex = -1F, float Density = -1F, float ScatterAnisotropy = -1F,
    float SpecularLevel = -1F, string? MediaType = null);

/// <summary>解析后的 MagicaVoxel 文件：模型、实例、图层三层结构</summary>
public sealed class VoxDocument
{
    internal VoxDocument(IReadOnlyList<VoxModel> models, IReadOnlyList<VoxInstance> instances,
        IReadOnlyList<VoxLayer> layers, IReadOnlyList<VoxMaterial> materials, bool hasSceneGraph)
    {
        Models = models;
        Instances = instances;
        Layers = layers;
        Materials = materials;
        HasSceneGraph = hasSceneGraph;
    }

    /// <summary>文件内的全部模型，索引即模型 id</summary>
    public IReadOnlyList<VoxModel> Models { get; }

    /// <summary>按场景图展开的实例；旧格式没有场景图时每个模型一个实例</summary>
    public IReadOnlyList<VoxInstance> Instances { get; }

    /// <summary>文件声明的图层；没有 LAYR 时为空</summary>
    public IReadOnlyList<VoxLayer> Layers { get; }

    /// <summary>文件声明的材质；没有 MATL 时为空</summary>
    public IReadOnlyList<VoxMaterial> Materials { get; }

    /// <summary>文件是否带 MagicaVoxel 场景图</summary>
    public bool HasSceneGraph { get; }
}

/// <summary>
/// 读取 MagicaVoxel .vox：多个 SIZE/XYZI 是多个模型，nTRN/nGRP/nSHP 是场景图，LAYR 是图层。
/// 图层只决定分组和显隐，真正能拆成对象的是模型实例。
/// </summary>
internal static class VoxDocumentReader
{
    private const int MaximumVoxels = 4_000_000;
    private const int MaximumAxisExtent = 256;
    private const int MaximumModels = 4096;

    internal static VoxDocument Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var palette = VoxFormat.DefaultPalette();
        var models = new List<VoxModel>();
        var transitions = new Dictionary<int, TransitionNode>();
        var groups = new Dictionary<int, GroupNode>();
        var shapes = new Dictionary<int, ShapeNode>();
        var layers = new Dictionary<int, VoxLayer>();
        var materials = new List<VoxMaterial>();
        var size = (X: 0, Y: 0, Z: 0);
        var pendingSize = false;

        void Chunks(long start, long end, int depth)
        {
            if (depth > 32) throw new InvalidDataException("VOX 块嵌套过深。");
            var cursor = start;
            while (cursor < end)
            {
                if (end - cursor < 12) throw new InvalidDataException("VOX 块头被截断。");
                stream.Position = cursor;
                var id = new string(reader.ReadChars(4));
                var contentSize = reader.ReadInt32();
                var childrenSize = reader.ReadInt32();
                // 部分 MagicaVoxel 文件（如 VOX 200）用 -1 表示 MAIN 子块延续到文件尾。
                // 仅允许无内容的顶层 MAIN 使用该写法，内部块仍必须声明有效长度。
                var mainToEnd = depth == 0 && cursor == 8 && id == "MAIN" &&
                    contentSize == 0 && childrenSize == -1;
                if (mainToEnd) childrenSize = checked((int)(end - cursor - 12L));
                var contentEnd = cursor + 12L + contentSize;
                var next = contentEnd + childrenSize;
                // 少数VOX导出器会把MAIN的childrenSize按理论长度写入，实际文件却更短。
                // MAIN没有内容，实际文件尾就是它唯一可信的子块边界；普通块仍严格拒绝越界。
                var mainLengthMismatch = depth == 0 && cursor == 8 && id == "MAIN" && contentSize == 0 && next > end;
                if (contentSize < 0 || childrenSize < 0 || contentEnd < cursor || next < contentEnd ||
                    (!mainLengthMismatch && next > end))
                {
                    throw new InvalidDataException($"VOX块{id}越过父块边界：偏移{cursor}，内容{contentSize}字节，子块{childrenSize}字节，父边界{end}。文件可能被截断或使用了不兼容的块长度声明。");
                }
                if (mainLengthMismatch) next = end;
                var contentStart = cursor + 12L;
                switch (id)
                {
                    case "SIZE":
                        if (contentSize != 12) throw new InvalidDataException("VOX SIZE 长度无效。");
                        size = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                        if (size.X is < 1 or > MaximumAxisExtent || size.Y is < 1 or > MaximumAxisExtent ||
                            size.Z is < 1 or > MaximumAxisExtent)
                            throw new InvalidDataException("VOX 单模型尺寸必须在 1–256 格之间。");
                        pendingSize = true;
                        break;
                    case "XYZI" or "XYZA":
                        if (!pendingSize) throw new InvalidDataException("VOX 缺少有效 SIZE。");
                        if (models.Count >= MaximumModels) throw new InvalidDataException("VOX 模型数量超过上限。");
                        var count = reader.ReadInt32();
                        var stride = id == "XYZA" ? 7 : 4;
                        if (count is < 0 or > MaximumVoxels || count * (long)stride != contentSize - 4L)
                            throw new InvalidDataException("VOX 体素数量与块长度不符。");
                        var voxels = new List<VoxModelVoxel>(count);
                        for (var index = 0; index < count; index++)
                        {
                            var x = reader.ReadByte(); var y = reader.ReadByte(); var z = reader.ReadByte();
                            if (x >= size.X || y >= size.Y || z >= size.Z) throw new InvalidDataException("VOX 坐标超出 SIZE。");
                            Rgba32 color;
                            int paletteIndex;
                            if (id == "XYZA")
                            {
                                color = new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(),
                                    reader.ReadByte());
                                paletteIndex = 0;
                            }
                            else
                            {
                                paletteIndex = reader.ReadByte();
                                color = palette[paletteIndex];
                            }

                            // 颜色此时可能还是默认色板，透明度与 RGB 统一在整份文件读完后校正
                            voxels.Add(new VoxModelVoxel(x, y, z, color, paletteIndex));
                        }
                        models.Add(new VoxModel(size.X, size.Y, size.Z, voxels));
                        pendingSize = false;
                        break;
                    case "RGBA":
                        if (contentSize != 1024) throw new InvalidDataException("VOX 色板长度无效。");
                        for (var index = 1; index <= 256; index++)
                        {
                            var color = new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                            if (index < 256) palette[index] = color;
                        }
                        break;
                    case "LAYR":
                    {
                        var layerId = reader.ReadInt32();
                        var attributes = ReadDictionary(reader, contentStart + contentSize);
                        reader.ReadInt32();
                        var name = ReadText(attributes, "_name");
                        layers[layerId] = new VoxLayer(layerId,
                            string.IsNullOrWhiteSpace(name) ? $"图层 {layerId}" : name.Trim(),
                            ReadFlag(attributes, "_hidden"));
                        break;
                    }
                    case "MATL":
                        ReadMaterial(reader, contentStart + contentSize, materials);
                        break;
                    case "nTRN":
                        ReadTransition(reader, contentStart + contentSize, transitions);
                        break;
                    case "nGRP":
                        ReadGroup(reader, contentStart + contentSize, groups);
                        break;
                    case "nSHP":
                        ReadShape(reader, contentStart + contentSize, shapes);
                        break;
                }
                if (childrenSize > 0) Chunks(contentEnd, next, depth + 1);
                cursor = next;
            }
        }

        Chunks(8, bytes.Length, 0);
        if (models.Count == 0) throw new InvalidDataException("VOX 文件没有模型数据。");
        // RGBA 块可能写在 XYZI 之后，体素的颜色与透明度要等整份文件读完、色板确定后再校正
        foreach (var model in models) model.ApplyPalette(palette);
        var hasSceneGraph = transitions.Count > 0 || shapes.Count > 0;
        var instances = hasSceneGraph
            ? BuildInstances(transitions, groups, shapes, layers, models)
            : BuildLooseInstances(models);
        if (instances.Count == 0) throw new InvalidDataException("VOX 文件场景图没有引用任何模型。");
        return new VoxDocument(models, instances, [.. layers.Values.OrderBy(layer => layer.Id)],
            [.. materials.OrderBy(material => material.Id)], hasSceneGraph && transitions.Count > 0);
    }

    /// <summary>没有场景图的旧文件：每个模型一个实例，按包围盒沿 X 依次排开避免重叠</summary>
    private static List<VoxInstance> BuildLooseInstances(List<VoxModel> models)
    {
        var instances = new List<VoxInstance>(models.Count);
        long offset = 0;
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            instances.Add(new VoxInstance(index, $"模型 {index + 1}", 0, false,
                new VoxelCoord64(offset, 0, 0), VoxTransform.Identity));
            offset += model.SizeX + 1;
        }

        return instances;
    }

    /// <summary>从场景图根节点递归展开实例，累积平移与旋转</summary>
    private static List<VoxInstance> BuildInstances(Dictionary<int, TransitionNode> transitions,
        Dictionary<int, GroupNode> groups, Dictionary<int, ShapeNode> shapes,
        Dictionary<int, VoxLayer> layers, IReadOnlyList<VoxModel> models)
    {
        var referenced = new HashSet<int>();
        foreach (var node in transitions.Values) referenced.Add(node.ChildId);
        foreach (var node in groups.Values)
        foreach (var child in node.Children)
            referenced.Add(child);
        var roots = transitions.Keys.Where(id => !referenced.Contains(id)).OrderBy(id => id).ToArray();
        if (roots.Length == 0) roots = [.. transitions.Keys.OrderBy(id => id)];

        var instances = new List<VoxInstance>();
        var visiting = new HashSet<int>();
        void Walk(int nodeId, VoxelCoord64 offset, VoxTransform rotation, int layerId, bool hidden,
            string? branchName)
        {
            if (instances.Count >= MaximumModels * 64) throw new InvalidDataException("VOX 实例数量超过上限。");
            if (!visiting.Add(nodeId)) throw new InvalidDataException("VOX 场景图存在环。");
            try
            {
                if (transitions.TryGetValue(nodeId, out var transition))
                {
                    var childOffset = offset + rotation.Apply(transition.Offset);
                    var childRotation = Multiply(rotation, transition.Rotation);
                    Walk(transition.ChildId, childOffset, childRotation,
                        transition.LayerId ?? layerId, hidden || transition.Hidden || IsLayerHidden(layers, transition.LayerId ?? layerId),
                        transition.Name ?? branchName);
                }
                else if (groups.TryGetValue(nodeId, out var group))
                {
                    foreach (var child in group.Children) Walk(child, offset, rotation, layerId, hidden, branchName);
                }
                else if (shapes.TryGetValue(nodeId, out var shape))
                {
                    // nSHP 携带多个模型时是动画帧序列，各帧占据同一位置。
                    // 只取第 0 帧；全部导入会得到一叠互相重叠的模型。
                    if (shape.ModelIds.Count == 0) return;
                    var modelId = shape.ModelIds[0];
                    if (modelId < 0 || modelId >= models.Count) return;
                    var name = branchName;
                    if (string.IsNullOrWhiteSpace(name)) name = $"模型 {modelId + 1}";
                    var model = models[modelId];
                    var pivot = new VoxelCoord64(-(model.SizeX / 2), model.SizeZ / 2, model.SizeY / 2);
                    // 实例以模型尺寸中心为枢轴；负轴旋转需从旋转后的格角回到格子的最小角。
                    long CellShift(int row) => Math.Min(0, rotation[row, 0] + rotation[row, 1] + rotation[row, 2]);
                    var cellShift = new VoxelCoord64(CellShift(0), CellShift(1), CellShift(2));
                    instances.Add(new VoxInstance(modelId, name, layerId, hidden,
                        offset - rotation.Apply(pivot) + cellShift, rotation));
                }
            }
            finally
            {
                visiting.Remove(nodeId);
            }
        }

        foreach (var root in roots) Walk(root, VoxelCoord64.Zero, VoxTransform.Identity, 0, false, null);
        return instances;
    }

    private static bool IsLayerHidden(Dictionary<int, VoxLayer> layers, int layerId) =>
        layers.TryGetValue(layerId, out var layer) && layer.Hidden;

    /// <summary>两个带符号置换矩阵相乘，结果仍是带符号置换矩阵</summary>
    private static VoxTransform Multiply(VoxTransform a, VoxTransform b)
    {
        sbyte Element(int row, int column) => (sbyte)(a[row, 0] * b[0, column] +
            a[row, 1] * b[1, column] + a[row, 2] * b[2, column]);
        return new VoxTransform(Element(0, 0), Element(0, 1), Element(0, 2),
            Element(1, 0), Element(1, 1), Element(1, 2), Element(2, 0), Element(2, 1), Element(2, 2));
    }

    /// <summary>
    /// 读取 MATL：材质按调色板索引绑定。数值写在 STRING 里是 ASCII 文本，
    /// 也有实现写 4 字节 float，两种都要认。
    /// 0.99.7 起材质改成参数化字段：金属用 _metal、半透明用 _trans/_alpha、发光强度用 _emit、
    /// 真实折射率用 _ri；_type 只在少数显式材质上出现，_ior 退化为相对 1 的偏移量。
    /// </summary>
    private static void ReadMaterial(BinaryReader reader, long contentEnd, List<VoxMaterial> materials)
    {
        var id = reader.ReadInt32();
        var properties = ReadDictionary(reader, contentEnd);
        var metal = ReadNumber(properties, "_metal", -1F);
        var transmission = ReadNumber(properties, "_trans", ReadNumber(properties, "_alpha", -1F));
        var emissive = ReadNumber(properties, "_emit", -1F);
        var mediaType = ReadText(properties, "_media_type");
        var type = ReadText(properties, "_type") ?? InferMaterialType(metal, transmission, emissive, mediaType);
        materials.Add(new VoxMaterial(id, type,
            ReadNumber(properties, "_weight", 1F),
            ReadNumber(properties, "_rough", -1F),
            ReadNumber(properties, "_spec", -1F),
            ReadNumber(properties, "_ior", -1F),
            ReadNumber(properties, "_att", -1F),
            ReadNumber(properties, "_flux", -1F),
            metal, transmission, emissive,
            ReadNumber(properties, "_ri", -1F),
            ReadNumber(properties, "_d", -1F),
            ReadNumber(properties, "_g", -1F),
            ReadNumber(properties, "_sp", -1F),
            mediaType));
    }

    /// <summary>没有 _type 时按参数推断材质类别，顺序按视觉特征从强到弱</summary>
    private static string InferMaterialType(float metal, float transmission, float emissive, string? mediaType)
    {
        if (metal > 0F) return "_metal";
        if (!string.IsNullOrWhiteSpace(mediaType)) return "_media";
        if (emissive > 0F) return "_emit";
        if (transmission > 0F && transmission < 1F) return "_glass";
        return "_diffuse";
    }

    /// <summary>字典里的数值既可能是 ASCII 文本，也可能是打包的 4 字节 float</summary>
    private static float ReadNumber(Dictionary<string, byte[]> attributes, string key, float fallback)
    {
        if (!attributes.TryGetValue(key, out var raw) || raw.Length == 0) return fallback;
        var text = Encoding.UTF8.GetString(raw).Trim();
        if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return raw.Length == 4 ? BitConverter.ToSingle(raw, 0) : fallback;
    }

    private static void ReadTransition(BinaryReader reader, long contentEnd,
        Dictionary<int, TransitionNode> nodes)
    {
        var nodeId = reader.ReadInt32();
        var attributes = ReadDictionary(reader, contentEnd);
        var childId = reader.ReadInt32();
        reader.ReadInt32();
        var layerId = reader.ReadInt32();
        var frames = reader.ReadInt32();
        VoxelCoord64 offset = VoxelCoord64.Zero;
        var rotation = VoxTransform.Identity;
        if (frames > 0)
        {
            var frame = ReadDictionary(reader, contentEnd);
            offset = ReadTranslation(frame);
            rotation = ReadRotation(frame);
            for (var index = 1; index < frames; index++) ReadDictionary(reader, contentEnd);
        }

        nodes[nodeId] = new TransitionNode(childId, layerId, ReadText(attributes, "_name"),
            ReadFlag(attributes, "_hidden"), offset, rotation);
    }

    private static void ReadGroup(BinaryReader reader, long contentEnd, Dictionary<int, GroupNode> nodes)
    {
        var nodeId = reader.ReadInt32();
        ReadDictionary(reader, contentEnd);
        var count = reader.ReadInt32();
        if (count is < 0 or > ushort.MaxValue) throw new InvalidDataException("VOX 组节点子项数量无效。");
        var children = new List<int>(count);
        for (var index = 0; index < count; index++) children.Add(reader.ReadInt32());
        nodes[nodeId] = new GroupNode(children);
    }

    private static void ReadShape(BinaryReader reader, long contentEnd, Dictionary<int, ShapeNode> nodes)
    {
        var nodeId = reader.ReadInt32();
        ReadDictionary(reader, contentEnd);
        var count = reader.ReadInt32();
        if (count is < 0 or > ushort.MaxValue) throw new InvalidDataException("VOX 形状节点模型数量无效。");
        var models = new List<int>(count);
        for (var index = 0; index < count; index++)
        {
            models.Add(reader.ReadInt32());
            ReadDictionary(reader, contentEnd);
        }

        nodes[nodeId] = new ShapeNode(models);
    }

    /// <summary>读取 DICT：键和值都是 STRING（长度前缀 + 原始字节），值可能是打包的二进制</summary>
    private static Dictionary<string, byte[]> ReadDictionary(BinaryReader reader, long contentEnd)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 256) throw new InvalidDataException("VOX 字典条目数量无效。");
        var result = new Dictionary<string, byte[]>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var keyLength = reader.ReadInt32();
            if (keyLength is < 0 or > 256 || reader.BaseStream.Position + keyLength > contentEnd)
                throw new InvalidDataException("VOX 字典键长度无效。");
            var key = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
            var valueLength = reader.ReadInt32();
            if (valueLength is < 0 or > 4096 || reader.BaseStream.Position + valueLength > contentEnd)
                throw new InvalidDataException("VOX 字典值长度无效。");
            result[key] = reader.ReadBytes(valueLength);
        }

        return result;
    }

    private static string? ReadText(Dictionary<string, byte[]> attributes, string key) =>
        attributes.TryGetValue(key, out var value) ? Encoding.UTF8.GetString(value) : null;

    private static bool ReadFlag(Dictionary<string, byte[]> attributes, string key) =>
        attributes.TryGetValue(key, out var value) && value.Length > 0 && value[0] != (byte)'0';

    /// <summary>_t 是三个 int32；坐标交换 Y/Z 以匹配 MVoxel 的 Y 轴向上</summary>
    private static VoxelCoord64 ReadTranslation(Dictionary<string, byte[]> attributes)
    {
        if (!attributes.TryGetValue("_t", out var value)) return VoxelCoord64.Zero;
        var parts = Encoding.UTF8.GetString(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && long.TryParse(parts[0], out var tx) &&
            long.TryParse(parts[1], out var ty) && long.TryParse(parts[2], out var tz))
            return new VoxelCoord64(-tx, tz, ty);
        if (value.Length != 12) return VoxelCoord64.Zero;
        var x = BitConverter.ToInt32(value, 0);
        var y = BitConverter.ToInt32(value, 4);
        var z = BitConverter.ToInt32(value, 8);
        return new VoxelCoord64(-(long)x, z, y);
    }

    /// <summary>_r 是一个字节编码的行主序置换矩阵，这里换算到交换 Y/Z 后的坐标系</summary>
    private static VoxTransform ReadRotation(Dictionary<string, byte[]> attributes)
    {
        if (!attributes.TryGetValue("_r", out var value)) return VoxTransform.Identity;
        var text = Encoding.UTF8.GetString(value);
        byte bits;
        if (!byte.TryParse(text, out bits))
        {
            if (value.Length != 1) return VoxTransform.Identity;
            bits = value[0];
        }
        var rows = new int[3];
        rows[0] = bits & 3;
        rows[1] = (bits >> 2) & 3;
        if (rows[0] > 2 || rows[1] > 2 || rows[0] == rows[1]) return VoxTransform.Identity;
        rows[2] = 3 - rows[0] - rows[1];
        var matrix = new sbyte[3, 3];
        for (var row = 0; row < 3; row++)
            matrix[row, rows[row]] = (sbyte)(((bits >> (4 + row)) & 1) == 0 ? 1 : -1);
        // MagicaVoxel 是 Z 轴向上，MVoxel 是 Y 轴向上：交换 Y/Z 需要左右各乘一次置换矩阵
        var result = new sbyte[3, 3];
        int[] swap = [0, 2, 1];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            result[row, column] = (sbyte)(matrix[swap[row], swap[column]] *
                (row == 0 ? -1 : 1) * (column == 0 ? -1 : 1));
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            if (result[row, column] != 0 && Math.Abs(result[row, column]) != 1) return VoxTransform.Identity;
        try
        {
            return new VoxTransform(result[0, 0], result[0, 1], result[0, 2],
                result[1, 0], result[1, 1], result[1, 2], result[2, 0], result[2, 1], result[2, 2]);
        }
        catch (ArgumentException)
        {
            return VoxTransform.Identity;
        }
    }

    private readonly record struct TransitionNode(int ChildId, int? LayerId, string? Name, bool Hidden,
        VoxelCoord64 Offset, VoxTransform Rotation);

    private readonly record struct GroupNode(IReadOnlyList<int> Children);

    private readonly record struct ShapeNode(IReadOnlyList<int> ModelIds);
}
