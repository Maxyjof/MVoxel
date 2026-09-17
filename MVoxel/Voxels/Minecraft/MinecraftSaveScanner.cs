using System.Buffers.Binary;
using System.IO;

namespace MVoxel.Voxels.Minecraft;

/// <summary>
/// Minecraft Java 版存档中的一个维度
/// </summary>
/// <param name="Id">维度标识，例如 overworld</param>
/// <param name="DisplayName">中文显示名</param>
/// <param name="RegionDirectory">相对存档根目录的区域文件夹路径</param>
public sealed record MinecraftDimension(string Id, string DisplayName, string RegionDirectory);

/// <summary>
/// 一次存档扫描得到的元信息
/// </summary>
/// <param name="RootPath">存档根目录</param>
/// <param name="LevelName">世界名称</param>
/// <param name="DataVersion">存档 DataVersion</param>
/// <param name="MinecraftVersion">游戏版本名</param>
/// <param name="Dimension">被扫描的维度</param>
/// <param name="MinChunkX">已生成区块包围盒最小 X</param>
/// <param name="MinChunkZ">已生成区块包围盒最小 Z</param>
/// <param name="MaxChunkX">已生成区块包围盒最大 X</param>
/// <param name="MaxChunkZ">已生成区块包围盒最大 Z</param>
/// <param name="RegionFileCount">区域文件数量</param>
/// <param name="ChunkCount">已生成区块数量</param>
/// <param name="MinBlockY">世界 Y 下限</param>
/// <param name="MaxBlockY">世界 Y 上限</param>
public sealed record MinecraftSaveInfo(
    string RootPath,
    string LevelName,
    int DataVersion,
    string MinecraftVersion,
    MinecraftDimension Dimension,
    long MinChunkX,
    long MinChunkZ,
    long MaxChunkX,
    long MaxChunkZ,
    int RegionFileCount,
    int ChunkCount,
    int MinBlockY,
    int MaxBlockY);

/// <summary>
/// Minecraft Java 版存档目录的只读扫描器
/// </summary>
public static class MinecraftSaveScanner
{
    private static readonly MinecraftDimension[] _dimensions =
    {
        new("overworld", "主世界", "region"),
        new("the_nether", "下界", Path.Combine("DIM-1", "region")),
        new("the_end", "末地", Path.Combine("DIM1", "region")),
    };

    /// <summary>
    /// 判断给定路径是否为一个 Minecraft 存档（存在 level.dat）
    /// </summary>
    /// <param name="path">待检查目录</param>
    /// <returns>存在 level.dat 时为真</returns>
    public static bool IsMinecraftSave(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return File.Exists(Path.Combine(path, "level.dat"));
    }

    /// <summary>
    /// 列出存档中实际存在区域文件的维度
    /// </summary>
    /// <param name="savePath">存档根目录</param>
    /// <returns>维度只读列表</returns>
    public static IReadOnlyList<MinecraftDimension> ListDimensions(string savePath)
    {
        ArgumentNullException.ThrowIfNull(savePath);
        var result = new List<MinecraftDimension>();
        foreach (var dimension in _dimensions)
        {
            var regionDirectory = Path.Combine(savePath, dimension.RegionDirectory);
            if (Directory.Exists(regionDirectory) && HasRegionFiles(regionDirectory))
            {
                result.Add(dimension);
            }
        }

        return result;
    }

    /// <summary>
    /// 按维度标识扫描存档元信息
    /// </summary>
    /// <param name="savePath">存档根目录</param>
    /// <param name="dimensionId">维度标识</param>
    /// <returns>扫描结果</returns>
    public static MinecraftSaveInfo Inspect(string savePath, string dimensionId)
    {
        var dimension = FindDimension(dimensionId);
        return Inspect(savePath, dimension);
    }

    /// <summary>
    /// 按维度对象扫描存档元信息
    /// </summary>
    /// <param name="savePath">存档根目录</param>
    /// <param name="dimension">维度对象</param>
    /// <returns>扫描结果</returns>
    public static MinecraftSaveInfo Inspect(string savePath, MinecraftDimension dimension)
    {
        ArgumentNullException.ThrowIfNull(savePath);
        ArgumentNullException.ThrowIfNull(dimension);
        if (!Directory.Exists(savePath))
        {
            throw new FileNotFoundException($"存档目录不存在：{savePath}");
        }

        var levelPath = Path.Combine(savePath, "level.dat");
        if (!File.Exists(levelPath))
        {
            throw new FileNotFoundException($"存档缺少 level.dat：{levelPath}");
        }

        var levelBytes = File.ReadAllBytes(levelPath);
        NbtCompound root;
        try
        {
            root = NbtReader.ReadCompressed(levelBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException($"level.dat 已损坏：{exception.Message}");
        }

        if (!root.TryGetCompound("Data", out var data))
        {
            throw new InvalidDataException("level.dat 缺少 Data 节点");
        }

        var levelName = data.GetString("LevelName");
        var dataVersion = data.GetInt("DataVersion");
        var minecraftVersion = "未知";
        if (data.TryGetCompound("Version", out var version)
            && version.TryGetString("Name", out var versionName))
        {
            minecraftVersion = versionName;
        }

        var (minBlockY, maxBlockY) = dataVersion >= 2860 ? (-64, 319) : (0, 255);

        var regionDirectory = Path.Combine(savePath, dimension.RegionDirectory);
        long minChunkX = 0;
        long minChunkZ = 0;
        long maxChunkX = 0;
        long maxChunkZ = 0;
        var regionFileCount = 0;
        var chunkCount = 0;
        if (Directory.Exists(regionDirectory))
        {
            foreach (var file in Directory.GetFiles(regionDirectory, "r.*.mca"))
            {
                if (!TryParseRegionFileName(Path.GetFileName(file), out var regionX, out var regionZ))
                {
                    continue;
                }

                regionFileCount++;
                chunkCount += CountChunksInRegion(file);
                var chunkXMin = (long)regionX * 32;
                var chunkZMin = (long)regionZ * 32;
                var chunkXMax = chunkXMin + 31;
                var chunkZMax = chunkZMin + 31;
                if (regionFileCount == 1)
                {
                    minChunkX = chunkXMin;
                    minChunkZ = chunkZMin;
                    maxChunkX = chunkXMax;
                    maxChunkZ = chunkZMax;
                }
                else
                {
                    minChunkX = Math.Min(minChunkX, chunkXMin);
                    minChunkZ = Math.Min(minChunkZ, chunkZMin);
                    maxChunkX = Math.Max(maxChunkX, chunkXMax);
                    maxChunkZ = Math.Max(maxChunkZ, chunkZMax);
                }
            }
        }

        return new MinecraftSaveInfo(
            savePath,
            levelName,
            dataVersion,
            minecraftVersion,
            dimension,
            minChunkX,
            minChunkZ,
            maxChunkX,
            maxChunkZ,
            regionFileCount,
            chunkCount,
            minBlockY,
            maxBlockY);
    }

    private static MinecraftDimension FindDimension(string dimensionId)
    {
        foreach (var dimension in _dimensions)
        {
            if (string.Equals(dimension.Id, dimensionId, StringComparison.Ordinal))
            {
                return dimension;
            }
        }

        throw new ArgumentException($"未知的维度标识：{dimensionId}", nameof(dimensionId));
    }

    private static bool HasRegionFiles(string regionDirectory) =>
        Directory.Exists(regionDirectory) && Directory.GetFiles(regionDirectory, "r.*.mca").Length > 0;

    private static bool TryParseRegionFileName(string fileName, out int regionX, out int regionZ)
    {
        regionX = 0;
        regionZ = 0;
        var parts = fileName.Split('.');
        return parts.Length == 4
            && string.Equals(parts[0], "r", StringComparison.Ordinal)
            && string.Equals(parts[3], "mca", StringComparison.Ordinal)
            && int.TryParse(parts[1], out regionX)
            && int.TryParse(parts[2], out regionZ);
    }

    private static int CountChunksInRegion(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 4096)
        {
            return 0;
        }

        var header = new byte[4096];
        if (stream.Read(header, 0, 4096) < 4096)
        {
            return 0;
        }

        var count = 0;
        for (var index = 0; index < 1024; index++)
        {
            var entry = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(index * 4, 4));
            if ((entry >> 8) != 0)
            {
                count++;
            }
        }

        return count;
    }
}
