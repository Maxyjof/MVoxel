namespace MVoxel.Voxels.Mesh;

/// <summary>
/// 网格导入统一入口：按扩展名分发到具体读取器，再体素化为体素世界。
/// 供编辑器与命令行共同调用
/// </summary>
public static class MeshImport
{
    /// <summary>
    /// 支持的文件扩展名（小写，含点）
    /// </summary>
    internal static readonly string[] SupportedExtensions = [".obj", ".stl", ".glb", ".gltf"];

    /// <summary>
    /// 读取网格文件
    /// </summary>
    /// <param name="path">网格文件路径</param>
    /// <returns>已校验的三角网格</returns>
    internal static MeshData ReadMesh(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".obj" => ObjReader.Read(path),
            ".stl" => StlReader.Read(path),
            ".glb" or ".gltf" => GltfReader.Read(path),
            _ => throw new InvalidDataException($"不支持的网格格式：{extension}"),
        };
    }

    /// <summary>
    /// 读取并体素化网格文件
    /// </summary>
    /// <param name="path">网格文件路径，支持 obj/stl/glb/gltf</param>
    /// <param name="fallbackColor">网格未提供建议色时使用的体素颜色</param>
    /// <param name="resolution">最长轴目标体素数，2 到 256</param>
    /// <param name="fillInterior">true 为实体填充，false 仅表面壳</param>
    /// <returns>体素世界，模型占据从原点起的紧凑包围盒</returns>
    /// <exception cref="InvalidDataException">文件损坏或格式非法</exception>
    /// <exception cref="InvalidOperationException">体素数超过上限</exception>
    public static VoxelWorld Import(string path, Rgba32 fallbackColor, int resolution, bool fillInterior)
    {
        var mesh = ReadMesh(path);
        var color = mesh.SuggestedColor ?? fallbackColor;
        return MeshVoxelizer.Voxelize(mesh, new VoxelValue(color), resolution, fillInterior);
    }
}
