using MVoxel.Project;

namespace MVoxel.Voxels;

/// <summary>
/// 原生材质到VOX参数的独立有损适配，不改变项目材质或体素
/// </summary>
public static class VoxMaterialExchange
{
    public static VoxExportPlan PrepareExport(IReadOnlyList<VoxFormat.VoxScenePart> parts,
        IReadOnlyDictionary<ushort, MaterialAsset> materials)
    {
        var contextual = new Dictionary<(int Part, ushort Slot), MaterialAsset>();
        for (var index = 0; index < parts.Count; index++)
        foreach (var item in materials)
            contextual[(index, item.Key)] = item.Value;
        return PrepareExport(parts, contextual);
    }

    /// <summary>
    /// 按模型段和本地槽位建立VOX导出材质，避免不同组件的同号槽位合并
    /// </summary>
    public static VoxExportPlan PrepareExport(IReadOnlyList<VoxFormat.VoxScenePart> parts,
        IReadOnlyDictionary<(int Part, ushort Slot), MaterialAsset> materials)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(materials);
        var used = parts.SelectMany((part, index) => part.World.EnumerateOccupied()
            .Where(item => item.Value.MaterialSlot != 0)
            .Select(item => (Part: index, Slot: item.Value.MaterialSlot))).ToHashSet();
        var converted = new List<VoxMaterial>();
        var warnings = new List<string>();
        var remap = new Dictionary<(int Part, ushort Slot), ushort>();
        ushort next = 1;
        foreach (var key in used.OrderBy(item => item.Part).ThenBy(item => item.Slot))
        {
            if (!materials.TryGetValue(key, out var asset)) continue;
            if (next > 255) break;
            remap[key] = next;
            var emission = asset.EmissiveScale.GetValueOrDefault();
            var metallic = asset.Metallic.GetValueOrDefault();
            var glass = asset.Ior.HasValue || asset.Opacity.HasValue || asset.AbsorptionStep.HasValue;
            var type = emission > 0 ? "_emit" : glass ? "_glass" : metallic > 0 ? "_metal" : "_diffuse";
            var weight = type == "_metal" ? Math.Clamp(metallic, 0F, 1F) : 1F;
            converted.Add(new VoxMaterial(next, type, weight, asset.Roughness ?? -1F,
                asset.Specular ?? -1F, asset.Ior ?? -1F, asset.AbsorptionStep ?? -1F,
                asset.EmissiveScale ?? -1F));
            if (asset.EmissiveScale.HasValue || asset.AbsorptionStep.HasValue || asset.Ior.HasValue ||
                asset.Metallic.HasValue || asset.Specular.HasValue || asset.Roughness.HasValue)
                warnings.Add($"模型段{key.Part}槽{key.Slot}的表面参数映射到MATL，外观只能近似");
            if (asset.AbsorptionColor.HasValue || asset.Opacity.HasValue || asset.VolumeScattering.HasValue ||
                asset.HazeDensity.HasValue || asset.ScatteringColor.HasValue)
                warnings.Add($"模型段{key.Part}槽{key.Slot}的吸收色、不透明度或体积散射参数不能由当前VOX出口完整表示");
            next++;
        }

        var convertedParts = parts.Select((part, index) => new VoxFormat.VoxScenePart(part.Name,
            RemapWorld(part.World, index, remap), part.Origin)).ToArray();
        var voxels = convertedParts.SelectMany(part => part.World.EnumerateOccupied()).Select(item => item.Value).ToArray();
        var sourceCombinations = used.Count;
        var paletteEntries = voxels.Select(value => value.Color).Distinct().Count();
        var missing = used.Count(key => !materials.ContainsKey(key));
        var flagsLost = voxels.LongCount(value => value.Flags != (value.Color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
        var approximated = Math.Max(0L, sourceCombinations - Math.Min(sourceCombinations, 255));
        var summary = new VoxExportLossSummary(sourceCombinations, paletteEntries, sourceCombinations,
            approximated, flagsLost, missing, Array.AsReadOnly(warnings.ToArray()));
        return new VoxExportPlan(summary, path => VoxFormat.WriteSceneFiles(path, convertedParts, converted));
    }

    /// <summary>
    /// 把项目材质元数据反向映射成 .vox 的 MATL 列表：导出时保留金属、玻璃与发光参数，
    /// 否则镜面材质在导出后会被 MagicaVoxel 当成普通漫反射
    /// </summary>
    /// <param name="metadata">体素资产内的材质元数据</param>
    /// <returns>按槽号排序的 MATL 列表；没有可用材质时为空</returns>
    public static IReadOnlyList<VoxMaterial> BuildExportMaterials(MaterialExchangeMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var result = new List<VoxMaterial>(metadata.Bindings.Count);
        foreach (var (slot, id) in metadata.Bindings.OrderBy(pair => pair.Key))
        {
            if (slot == 0 || !metadata.Definitions.TryGetValue(id, out var asset)) continue;
            var metallic = asset.Metallic.GetValueOrDefault();
            var emissive = asset.EmissiveScale.GetValueOrDefault();
            var isGlass = asset.Ior.HasValue || asset.Opacity.HasValue;
            var type = emissive > 0F ? "_emit" : isGlass ? "_glass" : metallic > 0F ? "_metal" : "_diffuse";
            var weight = type == "_metal" ? Math.Clamp(metallic, 0F, 1F) : 1F;
            var roughness = asset.Roughness ?? -1F;
            var specular = asset.Specular ?? -1F;
            var ior = isGlass ? (asset.Ior ?? -1F) : -1F;
            var transmission = asset.Opacity.HasValue ? Math.Clamp(1F - asset.Opacity.Value, 0F, 1F) : -1F;
            var emissiveValue = emissive > 0F ? emissive : -1F;
            result.Add(new VoxMaterial(slot, type, weight, roughness, specular, ior, -1F, -1F,
                metallic > 0F ? metallic : -1F, transmission, emissiveValue,
                isGlass ? (asset.Ior ?? -1F) : -1F));
        }

        return result;
    }

    private static VoxelWorld RemapWorld(VoxelWorld source, int part,
        IReadOnlyDictionary<(int Part, ushort Slot), ushort> remap)
    {
        var result = new VoxelWorld();
        foreach (var (coordinate, value) in source.EnumerateOccupied())
        {
            var slot = value.MaterialSlot == 0 ? (ushort)0 :
                remap.TryGetValue((part, value.MaterialSlot), out var mapped) ? mapped : (ushort)0;
            result.Set(coordinate, value with { MaterialSlot = slot });
        }
        return result;
    }

    /// <summary>
    /// 把 .vox 的 MATL 材质转成项目材质元数据：材质按调色板索引绑定，
    /// 导入时随世界一起写入，渲染视图才能拿到金属度与粗糙度做镜面反射
    /// </summary>
    /// <param name="document">解析后的 .vox 文档</param>
    /// <param name="baseName">素材名，用于生成材质显示名</param>
    /// <returns>可直接写入世界文件的材质元数据</returns>
    public static MaterialExchangeMetadata BuildImportMetadata(VoxDocument document, string baseName)
    {
        ArgumentNullException.ThrowIfNull(document);
        var metadata = new MaterialExchangeMetadata();
        foreach (var material in document.Materials)
        {
            if (material.Id <= 0 || material.Id >= MaterialAsset.TableSlots) continue;
            var id = $"vox-material-{material.Id}";
            metadata.Definitions[id] = ConvertToAsset(material, baseName);
            metadata.Bindings[(ushort)material.Id] = id;
        }

        return metadata;
    }

    /// <summary>
    /// 单个 MATL 材质到项目材质的映射：金属走金属度与粗糙度，玻璃走折射率与通透度，发光走自发光
    /// </summary>
    private static MaterialAsset ConvertToAsset(VoxMaterial material, string baseName)
    {
        var type = material.Type ?? string.Empty;
        var isMetal = type.Contains("metal", StringComparison.OrdinalIgnoreCase) || material.Metal > 0F;
        // 只有明确声明玻璃、透射大于 0 或给了折射率的材质才按玻璃处理：
        // MagicaVoxel 会给普通材质写 _trans=0，按“>=0”判断会把所有漫反射都变成透射
        var isGlass = !isMetal && (type.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                                   material.Transmission > 0F || material.RefractionIndex > 0F);
        var isEmissive = !isMetal && !isGlass && (type.Contains("emit", StringComparison.OrdinalIgnoreCase) ||
                                                  material.Emissive > 0F);
        var asset = new MaterialAsset { Name = $"{baseName} 材质 {material.Id}" };
        if (isMetal)
        {
            // MagicaVoxel 的金属材质用 _metal 做权重、_rough 做粗糙度；
            // 缺省 _metal 时按满金属处理，粗糙度缺省取 0 得到镜面反射
            var metallic = material.Metal >= 0F ? material.Metal : Math.Max(material.Weight, 0F);
            asset.Metallic = Math.Clamp(metallic <= 0F ? 1F : metallic, 0F, 1F);
            asset.Roughness = material.Roughness >= 0F ? material.Roughness : 0F;
            if (material.Specular >= 0F) asset.Specular = material.Specular;
            return asset;
        }

        if (isGlass)
        {
            asset.Ior = material.RefractionIndex > 0F
                ? material.RefractionIndex
                : material.Ior > 0F ? 1F + material.Ior : 1.5F;
            if (material.Transmission >= 0F) asset.Opacity = 1F - Math.Clamp(material.Transmission, 0F, 1F);
            if (material.Roughness >= 0F) asset.Roughness = material.Roughness;
            return asset;
        }

        if (isEmissive)
        {
            asset.EmissiveScale = material.Emissive > 0F ? material.Emissive : Math.Max(material.Flux, 0.01F);
            return asset;
        }

        if (material.Roughness >= 0F) asset.Roughness = material.Roughness;
        if (material.Specular >= 0F) asset.Specular = material.Specular;
        return asset;
    }
}
