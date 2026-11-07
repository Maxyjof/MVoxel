using System.Buffers.Binary;
using System.Text;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// MagicaVoxel .vox 交换格式。
/// 一个文件可以包含多个模型（每个 ≤256³）和一张把它们摆到世界里的场景图；
/// 内部项目始终使用原生世界格式，这里只做格式转换。
/// </summary>
public static class VoxFormat
{
    /// <summary>文件魔数 "VOX "（0x56 0x4F 0x58 0x20），小端读作 0x20584F56</summary>
    public const uint Magic = 0x2058_4F56;

    /// <summary>单模型体素上限</summary>
    public const int MaximumVoxels = 4_000_000;

    /// <summary>MagicaVoxel 单模型每轴上限</summary>
    public const int MaximumAxisExtent = 256;

    /// <summary>单文件最多携带的模型数</summary>
    public const int MaximumModels = 4096;

    /// <summary>MagicaVoxel 世界每轴范围：-1024 到 +1024，共 2048 格</summary>
    public const int WorldRegionExtent = 2048;

    /// <summary>MagicaVoxel 世界坐标的下限，-1024</summary>
    public const int WorldRegionMinimum = -1024;

    private const int MaximumFileBytes = 128 * 1024 * 1024;
    private const int MaximumPaletteColors = 255;

    /// <summary>场景导出的一段：世界、它在场景中的原点、以及显示名</summary>
    /// <param name="Name">段名称，仅用于导出说明</param>
    /// <param name="World">体素世界</param>
    /// <param name="Origin">该世界原点在世界坐标中的位置</param>
    public readonly record struct VoxScenePart(string Name, VoxelWorld World, VoxelCoord64 Origin);

    /// <summary>
    /// 把世界导出为 .vox。超过 256 格时按 256 边界切成子模型；
    /// 超出 MagicaVoxel 的 2048 格世界时按分区写成多个文件。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="world">要导出的体素世界</param>
    public static void Write(string path, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out _, out _))
            throw new InvalidOperationException("空模型不能导出 VOX，请先绘制方块。");
        WriteSceneFiles(path, [new VoxScenePart("模型", world, VoxelCoord64.Zero)]);
    }

    /// <summary>
    /// 把多个世界导出为 .vox：每段按 256 边界切块，块之间用 nTRN 摆到各自位置。
    /// 所有段共用一张 255 色色板，超出的颜色按出现频率保留，其余映射到最近色。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="parts">场景段列表</param>
    public static void WriteScene(string path, IReadOnlyList<VoxScenePart> parts) =>
        WriteScene(path, parts, materials: null);

    /// <summary>
    /// 把多个世界导出为 .vox，并把材质写成 MATL。
    /// 材质按调色板索引绑定：这里先算出每个颜色主要使用的材质槽，再按导出后的新索引写回。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="parts">场景段列表</param>
    /// <param name="materials">材质列表，Id 为材质槽号；为空时不写 MATL</param>
    public static void WriteScene(string path, IReadOnlyList<VoxScenePart> parts,
        IReadOnlyList<VoxMaterial>? materials) =>
        WriteSceneFiles(path, parts, materials);

    /// <summary>
    /// 按世界分区把场景写成多个 .vox：每个文件最多覆盖 <see cref="WorldRegionExtent"/> 格见方，
    /// 文件内每个对象是一个组节点，对象内超过 256 格的模型作为子对象分层摆放。
    /// 只有一个分区时直接写 <paramref name="path"/>，否则按分区块坐标追加到文件名。
    /// </summary>
    /// <param name="path">目标文件路径，也是多文件命名的基名</param>
    /// <param name="parts">场景段列表</param>
    /// <param name="materials">材质列表；为空时不写 MATL</param>
    /// <returns>实际写出的文件路径</returns>
    public static IReadOnlyList<string> WriteSceneFiles(string path, IReadOnlyList<VoxScenePart> parts,
        IReadOnlyList<VoxMaterial>? materials = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(parts);
        if (WorldRegionExtent % MaximumAxisExtent != 0 || WorldRegionExtent < MaximumAxisExtent)
            throw new InvalidOperationException("VOX 世界分区必须大于等于单模型上限且为整数倍");

        var regions = CollectRegions(parts);
        if (regions.Count == 0) throw new InvalidOperationException("没有可导出的体素，请先绘制方块。");

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        if (extension.Length == 0) extension = ".vox";
        var written = new List<string>(regions.Count);
        foreach (var (minimum, partChunks) in regions)
        {
            if (partChunks.Sum(item => item.Chunks.Count) > MaximumModels)
                throw new InvalidOperationException("场景切块后超过 VOX 模型数量上限。");
            var target = regions.Count == 1
                ? path
                : Path.Combine(directory, FormattableString.Invariant(
                    $"{stem}_X{minimum.X}_Y{minimum.Y}_Z{minimum.Z}{extension}"));
            WriteDocument(target, partChunks, materials);
            written.Add(target);
        }

        return written;
    }

    private static void WriteDocument(string path, IReadOnlyList<PartChunks> parts,
        IReadOnlyList<VoxMaterial>? materials)
    {
        var chunks = parts.SelectMany(part => part.Chunks).ToList();
        var palette = BuildPalette(chunks);

        var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var chunk in chunks)
            {
                Header(writer, "SIZE", 12);
                writer.Write(chunk.SizeX); writer.Write(chunk.SizeY); writer.Write(chunk.SizeZ);
                Header(writer, "XYZI", 4 + chunk.Voxels.Count * 4);
                writer.Write(chunk.Voxels.Count);
                foreach (var voxel in chunk.Voxels)
                {
                    writer.Write(voxel.X); writer.Write(voxel.Y); writer.Write(voxel.Z);
                    writer.Write(palette.Index[(voxel.Color, voxel.MaterialSlot)]);
                }
            }

            Header(writer, "RGBA", 1024);
            foreach (var color in palette.Colors)
            {
                writer.Write(color.R); writer.Write(color.G); writer.Write(color.B); writer.Write(color.A);
            }

            for (var index = palette.Colors.Count; index < 256; index++) writer.Write(0U);
            WriteMaterials(writer, palette, chunks, materials);
            WriteSceneGraph(writer, parts);
            writer.Flush();
        }

        var bytes = body.ToArray();
        var file = new MemoryStream(12 + bytes.Length);
        using (var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Magic); writer.Write(150);
            Header(writer, "MAIN", 0, bytes.Length);
            writer.Write(bytes);
            writer.Flush();
        }

        AtomicFile.WriteBytes(path, file.ToArray());
    }

    /// <summary>
    /// 按世界分区收集每个对象落在该分区内的体素块。
    /// 分区按 MagicaVoxel 的 [-1024, 1024) 世界窗口对齐，每轴 2048 格；
    /// 文件内的几何坐标平移到该窗口内，因此所有文件的场景图平移都落在世界范围内。
    /// </summary>
    private static List<(VoxelCoord64 Minimum, List<PartChunks> Parts)> CollectRegions(
        IReadOnlyList<VoxScenePart> parts)
    {
        var offset = -WorldRegionMinimum;
        var regions = new Dictionary<VoxelCoord64, RegionContent>();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            foreach (var (coordinate, value) in part.World.EnumerateOccupied())
            {
                if (value.Color.A == 0) continue;
                var worldX = coordinate.X + part.Origin.X;
                var worldY = coordinate.Y + part.Origin.Y;
                var worldZ = coordinate.Z + part.Origin.Z;
                var regionIndex = new VoxelCoord64(
                    FloorDiv(worldX + offset, WorldRegionExtent),
                    FloorDiv(worldY + offset, WorldRegionExtent),
                    FloorDiv(worldZ + offset, WorldRegionExtent));
                var localX = worldX - regionIndex.X * WorldRegionExtent;
                var localY = worldY - regionIndex.Y * WorldRegionExtent;
                var localZ = worldZ - regionIndex.Z * WorldRegionExtent;
                var chunkMinimum = new VoxelCoord64(
                    FloorDiv(localX, MaximumAxisExtent) * MaximumAxisExtent,
                    FloorDiv(localY, MaximumAxisExtent) * MaximumAxisExtent,
                    FloorDiv(localZ, MaximumAxisExtent) * MaximumAxisExtent);
                var regionEdge = new VoxelCoord64(
                    regionIndex.X * WorldRegionExtent + WorldRegionMinimum,
                    regionIndex.Y * WorldRegionExtent + WorldRegionMinimum,
                    regionIndex.Z * WorldRegionExtent + WorldRegionMinimum);
                if (!regions.TryGetValue(regionEdge, out var region))
                    regions[regionEdge] = region = new RegionContent();
                if (!region.Parts.TryGetValue(index, out var chunks))
                    region.Parts[index] = chunks = new Dictionary<VoxelCoord64, List<ChunkVoxel>>();
                if (!chunks.TryGetValue(chunkMinimum, out var voxels))
                    chunks[chunkMinimum] = voxels = [];
                voxels.Add(new ChunkVoxel(
                    (byte)(MaximumAxisExtent - 1 - (localX - chunkMinimum.X)),
                    (byte)(localZ - chunkMinimum.Z),
                    (byte)(localY - chunkMinimum.Y),
                    value.Color, value.MaterialSlot));
            }
        }

        var result = new List<(VoxelCoord64, List<PartChunks>)>(regions.Count);
        foreach (var (minimum, region) in regions
                     .OrderBy(item => item.Key.Z).ThenBy(item => item.Key.Y).ThenBy(item => item.Key.X))
        {
            var partChunks = new List<PartChunks>(region.Parts.Count);
            foreach (var (index, chunks) in region.Parts.OrderBy(item => item.Key))
            {
                var models = chunks
                    .OrderBy(item => item.Key.Z).ThenBy(item => item.Key.Y).ThenBy(item => item.Key.X)
                    .Select(item => new Chunk(MaximumAxisExtent, MaximumAxisExtent, MaximumAxisExtent,
                        item.Value, item.Key))
                    .ToList();
                partChunks.Add(new PartChunks(parts[index].Name, models));
            }

            result.Add((minimum, partChunks));
        }

        return result;
    }

    /// <summary>向下取整的整数除法，负坐标也按统一的世界网格分区</summary>
    private static long FloorDiv(long value, int divisor)
    {
        var quotient = value / divisor;
        if (value % divisor != 0 && value < 0) quotient--;
        return quotient;
    }

    private sealed class RegionContent
    {
        internal Dictionary<int, Dictionary<VoxelCoord64, List<ChunkVoxel>>> Parts { get; } = new();
    }

    /// <summary>读取 .vox 并合并成单个世界；多模型按场景图摆位后合并</summary>
    /// <param name="path">文件路径</param>
    /// <returns>合并后的世界</returns>
    public static VoxelWorld Read(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("VOX 文件超过读取上限。");
        return Read(File.ReadAllBytes(path));
    }

    /// <summary>读取 .vox 并合并成单个世界</summary>
    /// <param name="bytes">文件内容</param>
    /// <param name="origin">世界原点偏移</param>
    /// <returns>合并后的世界</returns>
    public static VoxelWorld Read(byte[] bytes, VoxelCoord64 origin = default) =>
        Compose(ReadDocument(bytes), origin);

    /// <summary>读取 .vox 的模型、实例与图层结构，不合并</summary>
    /// <param name="path">文件路径</param>
    /// <returns>解析结果</returns>
    public static VoxDocument ReadDocument(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("VOX 文件超过读取上限。");
        return ReadDocument(File.ReadAllBytes(path));
    }

    /// <summary>读取 .vox 的模型、实例与图层结构，不合并</summary>
    /// <param name="bytes">文件内容</param>
    /// <returns>解析结果</returns>
    public static VoxDocument ReadDocument(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 20 || bytes.Length > MaximumFileBytes) throw new InvalidDataException("VOX 文件长度无效。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic) throw new InvalidDataException("不是 VOX 文件。");
        return VoxDocumentReader.Read(bytes);
    }

    /// <summary>把文档里的每个实例按平移与旋转摆好，合并成一个世界。</summary>
    public static VoxelWorld Compose(VoxDocument document) => Compose(document, VoxelCoord64.Zero);

    /// <summary>把单个实例的变换烘焙到独立世界，保留颜色和材质槽。</summary>
    public static VoxelWorld ComposeInstance(VoxDocument document, VoxInstance instance, VoxelCoord64 origin = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var world = new VoxelWorld();
        AppendInstance(document, instance, origin, world.CreateBulkWriter());
        return world;
    }

    private static VoxelWorld Compose(VoxDocument document, VoxelCoord64 origin)
    {
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        foreach (var instance in document.Instances) AppendInstance(document, instance, origin, writer);
        return world;
    }

    private static void AppendInstance(VoxDocument document, VoxInstance instance, VoxelCoord64 origin, VoxelWorld.BulkWriter writer)
    {
        var model = document.Models[instance.ModelIndex];
        foreach (var voxel in model.Voxels)
        {
            var local = new VoxelCoord64(-voxel.X - 1L, voxel.Z, voxel.Y);
            var coordinate = origin + instance.Offset + instance.Rotation.Apply(local);
            writer.Set(coordinate, new VoxelValue(voxel.Color,
                (ushort)Math.Clamp(voxel.PaletteIndex, 0, Project.MaterialAsset.TableSlots - 1),
                voxel.Color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
        }
    }

    /// <summary>收集所有块用到的颜色与材质组合；超过255项时按出现频率保留并映射其余组合</summary>
    private static PaletteResult BuildPalette(List<Chunk> chunks)
    {
        var counts = new Dictionary<(Rgba32 Color, ushort MaterialSlot), int>();
        foreach (var chunk in chunks)
        foreach (var voxel in chunk.Voxels)
            counts[(voxel.Color, voxel.MaterialSlot)] = counts.GetValueOrDefault((voxel.Color, voxel.MaterialSlot)) + 1;

        var combinations = counts.Count <= MaximumPaletteColors
            ? counts.Keys.ToList()
            : counts.OrderByDescending(item => item.Value).Take(MaximumPaletteColors).Select(item => item.Key).ToList();
        var index = new Dictionary<(Rgba32 Color, ushort MaterialSlot), byte>(combinations.Count);
        for (var i = 0; i < combinations.Count; i++) index[combinations[i]] = (byte)(i + 1);
        foreach (var combination in counts.Keys)
        {
            if (index.ContainsKey(combination)) continue;
            var nearest = combinations[0];
            var best = int.MaxValue;
            foreach (var candidate in combinations)
            {
                if (candidate.MaterialSlot != combination.MaterialSlot) continue;
                var distance = Squared(combination.Color, candidate.Color);
                if (distance >= best) continue;
                best = distance;
                nearest = candidate;
            }
            if (best == int.MaxValue)
            {
                foreach (var candidate in combinations)
                {
                    var distance = Squared(combination.Color, candidate.Color);
                    if (distance >= best) continue;
                    best = distance;
                    nearest = candidate;
                }
            }
            index[combination] = index[nearest];
        }

        return new PaletteResult(combinations.Select(item => item.Color).ToList(), index,
            combinations.ToDictionary(item => (byte)(index[item]), item => item));
    }

    private static int Squared(Rgba32 a, Rgba32 b)
    {
        var dr = a.R - b.R; var dg = a.G - b.G; var db = a.B - b.B; var da = a.A - b.A;
        return dr * dr + dg * dg + db * db + da * da;
    }

    /// <summary>
    /// 写出 MATL：材质按调色板索引绑定，所以先按“颜色 → 主材质槽”投票，
    /// 再为该调色板索引写一份材质。没有材质的颜色不写，MagicaVoxel 侧按默认材质处理。
    /// </summary>
    private static void WriteMaterials(BinaryWriter writer, PaletteResult palette,
        List<Chunk> chunks, IReadOnlyList<VoxMaterial>? materials)
    {
        if (materials is not { Count: > 0 }) return;
        var bySlot = new Dictionary<int, VoxMaterial>();
        foreach (var material in materials) bySlot.TryAdd(material.Id, material);
        for (var index = 0; index < palette.Colors.Count; index++)
        {
            var paletteIndex = (byte)(index + 1);
            if (!palette.CombinationByIndex.TryGetValue(paletteIndex, out var combination) ||
                !bySlot.TryGetValue(combination.MaterialSlot, out var material)) continue;
            var payload = new MemoryStream();
            using (var node = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
            {
                node.Write(index + 1);
                // 未覆盖的字段（-1）不写：MagicaVoxel 见到缺失键会走默认值，写 -1 反而被当成有效数值
                var properties = new List<(string Key, string Value)>
                {
                    ("_type", string.IsNullOrWhiteSpace(material.Type) ? "_diffuse" : material.Type),
                    ("_weight", FloatText(material.Weight)),
                };
                if (material.Roughness >= 0F) properties.Add(("_rough", FloatText(material.Roughness)));
                if (material.Specular >= 0F) properties.Add(("_spec", FloatText(material.Specular)));
                if (material.Ior > 0F) properties.Add(("_ior", FloatText(material.Ior)));
                if (material.Absorption >= 0F) properties.Add(("_att", FloatText(material.Absorption)));
                if (material.Flux >= 0F) properties.Add(("_flux", FloatText(material.Flux)));
                WriteDictionary(node, [.. properties]);
            }

            WriteChunk(writer, "MATL", payload.ToArray());
        }
    }

    /// <summary>MagicaVoxel 把数值写在 STRING 里，统一用不受区域设置影响的小数格式</summary>
    private static string FloatText(float value) =>
        value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 写出分层场景图：根 → 每个对象一个组 → 对象内的子模型变换与形状节点。
    /// MagicaVoxel 的场景图支持组节点，单个对象超过 256 格时按子对象分层，而不是把多个模型平铺在同一层。
    /// </summary>
    private static void WriteSceneGraph(BinaryWriter writer, IReadOnlyList<PartChunks> parts)
    {
        const int rootId = 0;
        const int rootGroupId = 1;
        var nextId = 2;
        var nodes = new List<PartNode>(parts.Count);
        foreach (var part in parts)
        {
            var objectTransform = nextId++;
            var objectGroup = nextId++;
            var transformations = new int[part.Chunks.Count];
            var shapes = new int[part.Chunks.Count];
            for (var index = 0; index < part.Chunks.Count; index++)
            {
                transformations[index] = nextId++;
                shapes[index] = nextId++;
            }

            nodes.Add(new PartNode(objectTransform, objectGroup, transformations, shapes));
        }

        WriteTransformNode(writer, rootId, "Scene", rootGroupId, 0, 0, 0);
        WriteGroupNode(writer, rootGroupId, "Root", nodes.Select(node => node.ObjectTransform));

        var modelIndex = 0;
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            var node = nodes[partIndex];
            WriteTransformNode(writer, node.ObjectTransform, part.Name, node.ObjectGroup, 0, 0, 0);
            WriteGroupNode(writer, node.ObjectGroup, part.Name + " 子对象", node.Transformations);
            for (var index = 0; index < part.Chunks.Count; index++)
            {
                var chunk = part.Chunks[index];
                // MagicaVoxel 的平移是 Z 轴向上：MVoxel 的 Y 换到 vox 的 Z
                WriteTransformNode(writer, node.Transformations[index],
                    $"{part.Name} 子对象 {index + 1}", node.Shapes[index],
                    -chunk.Origin.X - chunk.SizeX + chunk.SizeX / 2,
                    chunk.Origin.Z + chunk.SizeY / 2,
                    chunk.Origin.Y + chunk.SizeZ / 2);
                WriteShapeNode(writer, node.Shapes[index], modelIndex++);
            }
        }
    }

    private static void WriteTransformNode(BinaryWriter writer, int nodeId, string name, int childId,
        long x, long y, long z)
    {
        var payload = new MemoryStream();
        using (var node = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
        {
            node.Write(nodeId);
            WriteDictionary(node, ("_name", name));
            node.Write(childId);
            node.Write(-1);
            node.Write(0);
            node.Write(1);
            WriteTransformFrame(node, ToInt32(x), ToInt32(y), ToInt32(z));
            node.Flush();
        }

        WriteChunk(writer, "nTRN", payload.ToArray());
    }

    private static void WriteGroupNode(BinaryWriter writer, int nodeId, string name, IEnumerable<int> children)
    {
        var payload = new MemoryStream();
        using (var node = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
        {
            node.Write(nodeId);
            WriteDictionary(node, ("_name", name));
            var list = children.ToList();
            node.Write(list.Count);
            foreach (var child in list) node.Write(child);
            node.Flush();
        }

        WriteChunk(writer, "nGRP", payload.ToArray());
    }

    private static void WriteShapeNode(BinaryWriter writer, int nodeId, int modelId)
    {
        var payload = new MemoryStream();
        using (var node = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
        {
            node.Write(nodeId);
            WriteDictionary(node);
            node.Write(1);
            node.Write(modelId);
            WriteDictionary(node, ("_f", "0"));
            node.Flush();
        }

        WriteChunk(writer, "nSHP", payload.ToArray());
    }

    private sealed record PartChunks(string Name, List<Chunk> Chunks);

    private sealed record PartNode(int ObjectTransform, int ObjectGroup,
        int[] Transformations, int[] Shapes);

    /// <summary>写出一帧变换：恒等旋转加平移。_r 的 4 对应单位矩阵（各行非零列依次为 0、1、2，全为正）</summary>
    private static void WriteTransformFrame(BinaryWriter node, int x, int y, int z)
    {
        WriteDictionary(node, ("_r", "4"),
            ("_t", FormattableString.Invariant($"{x} {y} {z}")), ("_f", "0"));
    }

    /// <summary>场景图平移是 int32，超出范围直接报错，不做静默截断</summary>
    private static int ToInt32(long value) => value is < int.MinValue or > int.MaxValue
        ? throw new InvalidOperationException("模型位置超出 .vox 场景图可表示的整数范围")
        : (int)value;

    /// <summary>单位旋转：bit0-1=0、bit2-3=1、符号位全为零</summary>
    private static byte PackIdentityRotation() => (1 << 2);

    private static void WriteDictionary(BinaryWriter node, params (string Key, string Value)[] entries)
    {
        node.Write(entries.Length);
        foreach (var (key, value) in entries)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            node.Write(keyBytes.Length); node.Write(keyBytes);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            node.Write(valueBytes.Length); node.Write(valueBytes);
        }
    }

    /// <summary>写出带长度头的独立块，用于场景图节点</summary>
    private static void WriteChunk(BinaryWriter writer, string id, byte[] content)
    {
        Header(writer, id, content.Length);
        writer.Write(content);
    }

    private static void Header(BinaryWriter writer, string id, int content, int children = 0)
    {
        writer.Write(Encoding.ASCII.GetBytes(id));
        writer.Write(content); writer.Write(children);
    }

    /// <summary>官方默认色板：6³ 色立方（不含黑色）加红/绿/蓝/灰各十级。</summary>
    /// <remarks>https://github.com/ephtracy/voxel-model/blob/master/MagicaVoxel-file-format-vox.txt</remarks>
    internal static Rgba32[] DefaultPalette()
    {
        var palette = new Rgba32[256];
        var index = 1;
        for (var r = 255; r >= 0; r -= 51)
        for (var g = 255; g >= 0; g -= 51)
        for (var b = 255; b >= 0; b -= 51)
            if (r != 0 || g != 0 || b != 0) palette[index++] = new Rgba32((byte)r, (byte)g, (byte)b, 255);
        byte[] shades = [238, 221, 187, 170, 136, 119, 85, 68, 34, 17];
        for (var channel = 0; channel < 4; channel++)
        foreach (var value in shades)
            palette[index++] = new Rgba32(channel is 0 or 3 ? value : (byte)0,
                channel is 1 or 3 ? value : (byte)0, channel is 2 or 3 ? value : (byte)0, 255);
        return palette;
    }

    private readonly record struct ChunkVoxel(byte X, byte Y, byte Z, Rgba32 Color, ushort MaterialSlot);

    private sealed record Chunk(int SizeX, int SizeY, int SizeZ, List<ChunkVoxel> Voxels, VoxelCoord64 Origin);

    private sealed record PaletteResult(List<Rgba32> Colors,
        Dictionary<(Rgba32 Color, ushort MaterialSlot), byte> Index,
        Dictionary<byte, (Rgba32 Color, ushort MaterialSlot)> CombinationByIndex);
}
