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

    private const int MaximumFileBytes = 128 * 1024 * 1024;
    private const int MaximumPaletteColors = 255;

    /// <summary>场景导出的一段：世界、它在场景中的原点、以及显示名</summary>
    /// <param name="Name">段名称，仅用于导出说明</param>
    /// <param name="World">体素世界</param>
    /// <param name="Origin">该世界原点在世界坐标中的位置</param>
    public readonly record struct VoxScenePart(string Name, VoxelWorld World, VoxelCoord64 Origin);

    /// <summary>
    /// 把世界导出为 .vox。超过 256 格时按 256 边界切成多个模型，再用场景图拼回原位。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="world">要导出的体素世界</param>
    public static void Write(string path, VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.TryGetBounds(out var minimum, out _))
            throw new InvalidOperationException("空模型不能导出 VOX，请先绘制方块。");
        WriteScene(path, [new VoxScenePart("模型", world, VoxelCoord64.Zero)]);
    }

    /// <summary>
    /// 把多个世界导出成一个 .vox：每段按 256 边界切块，块之间用 nTRN 摆到各自位置。
    /// 所有段共用一张 255 色色板，超出的颜色按出现频率保留，其余映射到最近色。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="parts">场景段列表</param>
    public static void WriteScene(string path, IReadOnlyList<VoxScenePart> parts) =>
        WriteScene(path, parts, materials: null);

    /// <summary>
    /// 把多个世界导出成一个 .vox，并把材质写成 MATL。
    /// 材质按调色板索引绑定：这里先算出每个颜色主要使用的材质槽，再按导出后的新索引写回。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="parts">场景段列表</param>
    /// <param name="materials">材质列表，Id 为材质槽号；为空时不写 MATL</param>
    public static void WriteScene(string path, IReadOnlyList<VoxScenePart> parts,
        IReadOnlyList<VoxMaterial>? materials)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var chunks = new List<Chunk>();
        foreach (var part in parts)
        {
            if (part.World.OccupiedVoxelCount == 0) continue;
            if (!part.World.TryGetBounds(out var minimum, out var maximum)) continue;
            SplitIntoChunks(part.World, minimum, maximum, part.Origin, chunks);
        }

        if (chunks.Count == 0) throw new InvalidOperationException("没有可导出的体素，请先绘制方块。");
        if (chunks.Count > MaximumModels) throw new InvalidOperationException("场景切块后超过 VOX 模型数量上限。");
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
            WriteSceneGraph(writer, chunks);
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

    /// <summary>把文档里的每个实例按平移与旋转摆好，合并成一个世界</summary>
    /// <param name="document">解析结果</param>
    /// <returns>合并后的世界</returns>
    public static VoxelWorld Compose(VoxDocument document) => Compose(document, VoxelCoord64.Zero);

    /// <summary>把文档里的每个实例按平移与旋转摆好，合并成一个世界</summary>
    private static VoxelWorld Compose(VoxDocument document, VoxelCoord64 origin)
    {
        var world = new VoxelWorld();
        foreach (var instance in document.Instances)
        {
            var model = document.Models[instance.ModelIndex];
            foreach (var voxel in model.Voxels)
            {
                // MagicaVoxel 的 Z 是重力方向，映射到 MVoxel 的 Y 轴向上
                var local = new VoxelCoord64(voxel.X, voxel.Z, voxel.Y);
                var coordinate = origin + instance.Offset + instance.Rotation.Apply(local);
                // 调色板索引同时是材质槽：导入后材质面板与着色器都按它取参数
                world.Set(coordinate, new VoxelValue(voxel.Color,
                    (ushort)Math.Clamp(voxel.PaletteIndex, 0, Project.MaterialAsset.TableSlots - 1),
                    voxel.Color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
            }
        }

        return world;
    }

    /// <summary>按 256 边界切块，块内坐标从零开始，块在世界中的位置交给场景图</summary>
    private static void SplitIntoChunks(VoxelWorld world, VoxelCoord64 minimum, VoxelCoord64 maximum,
        VoxelCoord64 origin, List<Chunk> chunks)
    {
        var spanX = maximum.X - minimum.X + 1;
        var spanY = maximum.Y - minimum.Y + 1;
        var spanZ = maximum.Z - minimum.Z + 1;
        var totalChunks = ((spanX + MaximumAxisExtent - 1) / MaximumAxisExtent) *
                          ((spanY + MaximumAxisExtent - 1) / MaximumAxisExtent) *
                          ((spanZ + MaximumAxisExtent - 1) / MaximumAxisExtent);
        if (chunks.Count + totalChunks > MaximumModels)
            throw new InvalidOperationException("场景切块后超过 VOX 模型数量上限。");
        // 先按块收集体素，避免为每个块都遍历整张世界
        var buckets = new Dictionary<(long X, long Y, long Z), List<ChunkVoxel>>();
        foreach (var (coordinate, value) in world.EnumerateOccupied())
        {
            if (value.Color.A == 0) continue;
            var cell = new VoxelCoord64(coordinate.X - minimum.X, coordinate.Y - minimum.Y, coordinate.Z - minimum.Z);
            var key = (cell.X / MaximumAxisExtent, cell.Y / MaximumAxisExtent, cell.Z / MaximumAxisExtent);
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = [];
            bucket.Add(new ChunkVoxel((byte)(cell.X % MaximumAxisExtent), (byte)(cell.Z % MaximumAxisExtent),
                (byte)(cell.Y % MaximumAxisExtent), value.Color, value.MaterialSlot));
        }

        foreach (var (key, voxels) in buckets.OrderBy(item => item.Key.Z).ThenBy(item => item.Key.Y)
                     .ThenBy(item => item.Key.X))
        {
            var chunkMinimum = new VoxelCoord64(minimum.X + key.X * MaximumAxisExtent,
                minimum.Y + key.Y * MaximumAxisExtent, minimum.Z + key.Z * MaximumAxisExtent);
            var sizeX = (int)Math.Min(MaximumAxisExtent, maximum.X - chunkMinimum.X + 1);
            var sizeY = (int)Math.Min(MaximumAxisExtent, maximum.Z - chunkMinimum.Z + 1);
            var sizeZ = (int)Math.Min(MaximumAxisExtent, maximum.Y - chunkMinimum.Y + 1);
            chunks.Add(new Chunk(sizeX, sizeY, sizeZ, voxels, chunkMinimum - minimum + origin));
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

    /// <summary>写出场景图：根变换 → 组 → 每个模型一个变换 + 形状节点</summary>
    private static void WriteSceneGraph(BinaryWriter writer, List<Chunk> chunks)
    {
        const int rootId = 0;
        const int groupId = 1;
        var transformStart = 2;
        var shapeStart = transformStart + chunks.Count;

        var rootPayload = new MemoryStream();
        using (var node = new BinaryWriter(rootPayload, Encoding.ASCII, leaveOpen: true))
        {
            node.Write(rootId);
            WriteDictionary(node, ("_name", "Scene"));
            node.Write(groupId);
            node.Write(-1);
            node.Write(0);
            node.Write(1);
            WriteTransformFrame(node, 0, 0, 0);
        }

        WriteChunk(writer, "nTRN", rootPayload.ToArray());

        var groupPayload = new MemoryStream();
        using (var node = new BinaryWriter(groupPayload, Encoding.ASCII, leaveOpen: true))
        {
            node.Write(groupId);
            WriteDictionary(node, ("_name", "Root"));
            node.Write(chunks.Count);
            for (var i = 0; i < chunks.Count; i++) node.Write(transformStart + i);
        }

        WriteChunk(writer, "nGRP", groupPayload.ToArray());

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var nodePayload = new MemoryStream();
            using (var node = new BinaryWriter(nodePayload, Encoding.ASCII, leaveOpen: true))
            {
                node.Write(transformStart + i);
                WriteDictionary(node, ("_name", $"Model {i + 1}"));
                node.Write(shapeStart + i);
                node.Write(-1);
                node.Write(0);
                node.Write(1);
                // MagicaVoxel 的平移是 Z 轴向上：MVoxel 的 Y 换到 vox 的 Z
                WriteTransformFrame(node, ToInt32(chunk.Origin.X), ToInt32(chunk.Origin.Z), ToInt32(chunk.Origin.Y));
            }

            WriteChunk(writer, "nTRN", nodePayload.ToArray());

            var shapePayload = new MemoryStream();
            using (var node = new BinaryWriter(shapePayload, Encoding.ASCII, leaveOpen: true))
            {
                node.Write(shapeStart + i);
                WriteDictionary(node);
                node.Write(1);
                node.Write(i);
                WriteDictionary(node, ("_f", "0"));
            }

            WriteChunk(writer, "nSHP", shapePayload.ToArray());
        }
    }

    /// <summary>写出一帧变换：恒等旋转加平移。_r 的 4 对应单位矩阵（各行非零列依次为 0、1、2，全为正）</summary>
    private static void WriteTransformFrame(BinaryWriter node, int x, int y, int z)
    {
        node.Write(3);
        node.Write(2); node.Write("_r"u8.ToArray()); node.Write(1); node.Write(PackIdentityRotation());
        node.Write(2); node.Write("_t"u8.ToArray()); node.Write(12);
        node.Write(x); node.Write(y); node.Write(z);
        node.Write(2); node.Write("_f"u8.ToArray()); node.Write(4); node.Write(0);
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
