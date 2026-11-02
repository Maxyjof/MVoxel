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
        return new VoxExportPlan(summary, path => VoxFormat.WriteScene(path, convertedParts, converted));
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
}
