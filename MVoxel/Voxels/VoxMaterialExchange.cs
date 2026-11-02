using MVoxel.Project;

namespace MVoxel.Voxels;

/// <summary>
/// 原生材质到VOX参数的独立有损适配，不改变项目材质或体素
/// </summary>
public static class VoxMaterialExchange
{
    /// <summary>
    /// 原生槽必须是所有场景段共用的交换槽，不能直接混用不同模型的局部槽
    /// </summary>
    public static VoxExportPlan PrepareExport(IReadOnlyList<VoxFormat.VoxScenePart> parts,
        IReadOnlyDictionary<ushort, MaterialAsset> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var used = parts.SelectMany(part => part.World.EnumerateOccupied())
            .Select(item => item.Value.MaterialSlot).ToHashSet();
        var converted = new List<VoxMaterial>();
        var warnings = new List<string>();
        foreach (var (slot, asset) in materials.OrderBy(item => item.Key))
        {
            if (slot == 0 || !used.Contains(slot)) continue;
            ArgumentNullException.ThrowIfNull(asset);
            var emission = asset.EmissiveScale.GetValueOrDefault();
            var metallic = asset.Metallic.GetValueOrDefault();
            var glass = asset.Ior.HasValue || asset.Opacity.HasValue || asset.AbsorptionStep.HasValue;
            var type = emission > 0 ? "_emit" : glass ? "_glass" : metallic > 0 ? "_metal" : "_diffuse";
            var weight = type == "_metal" ? Math.Clamp(metallic, 0F, 1F) : 1F;
            converted.Add(new VoxMaterial(slot, type, weight, asset.Roughness ?? -1F,
                asset.Specular ?? -1F, asset.Ior ?? -1F, asset.AbsorptionStep ?? -1F,
                asset.EmissiveScale ?? -1F));
            if (asset.EmissiveScale.HasValue || asset.AbsorptionStep.HasValue || asset.Ior.HasValue ||
                asset.Metallic.HasValue || asset.Specular.HasValue || asset.Roughness.HasValue)
                warnings.Add($"槽{slot}的表面参数映射到MATL，外观只能近似");
            if (asset.AbsorptionColor.HasValue || asset.Opacity.HasValue || asset.VolumeScattering.HasValue ||
                asset.HazeDensity.HasValue || asset.ScatteringColor.HasValue)
                warnings.Add($"槽{slot}的吸收色、不透明度或体积散射参数不能由当前VOX出口完整表示");
        }

        var voxels = parts.SelectMany(part => part.World.EnumerateOccupied()).Select(item => item.Value).ToArray();
        var sourceCombinations = voxels.Select(value => (value.Color, value.MaterialSlot)).Distinct().Count();
        var paletteEntries = voxels.Select(value => value.Color).Distinct().Count();
        var materialCount = voxels.Select(value => value.MaterialSlot).Where(slot => slot != 0).Distinct().Count();
        var approximated = Math.Max(0L, sourceCombinations - Math.Min(sourceCombinations, 255));
        var missing = voxels.Select(value => value.MaterialSlot).Where(slot => slot != 0)
            .Distinct().Count(slot => !materials.ContainsKey(slot));
        var flagsLost = voxels.LongCount(value => value.Flags != (value.Color.A < 255 ? VoxelFlags.Translucent : VoxelFlags.None));
        var summary = new VoxExportLossSummary(sourceCombinations, paletteEntries, materialCount,
            approximated, flagsLost, missing, Array.AsReadOnly(warnings.ToArray()));
        return new VoxExportPlan(summary, path => VoxFormat.WriteScene(path, parts, converted));
    }
}
