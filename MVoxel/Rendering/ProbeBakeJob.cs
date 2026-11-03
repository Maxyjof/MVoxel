using System.Numerics;
using MVoxel.Core;
using MVoxel.Scene;
using MVoxel.Voxels;

namespace MVoxel.Rendering;

/// <summary>
/// 探针烘焙用的环境光参数
/// </summary>
/// <param name="SkyTop">天顶颜色</param>
/// <param name="SkyHorizon">地平颜色</param>
/// <param name="SunColor">太阳颜色</param>
/// <param name="SunDirection">太阳方向，单位向量，指向光源</param>
/// <param name="SunStrength">太阳强度权重</param>
internal readonly record struct ProbeBakeLighting(Vector3 SkyTop, Vector3 SkyHorizon, Vector3 SunColor,
    Vector3 SunDirection, float SunStrength);

/// <summary>
/// 探针烘焙质量参数
/// </summary>
/// <param name="RayCount">每个探针的采样方向数</param>
/// <param name="MaxProbes">单个体积的探针数量上限</param>
/// <param name="Smoothing">烘焙结果的三维盒式平滑次数</param>
/// <param name="Distance">最长探测距离，0 表示按体积对角线自动取值</param>
internal readonly record struct ProbeBakeQuality(int RayCount, int MaxProbes, int Smoothing, float Distance)
{
    /// <summary>默认质量：方向数足够压住斑块、探针够密、平滑一次</summary>
    public static ProbeBakeQuality Default => new(64, 65536, 1, 0F);
}

/// <summary>
/// 自动探针体积的 CPU 烘焙器
/// </summary>
/// <remarks>
/// 每个探针沿固定的球面方向集发出射线，用体素 DDA 找第一个遮挡物：
/// 飞出世界的方向计为可见天空，命中不透明体素按一次反弹取它的反照率，
/// 命中自发光体素直接把颜色算进辐照度。可见天空的比例同时作为可见度写入样本，
/// 供渲染端按相机位置加权混合
/// 烘焙最后按需做几次三维盒式平滑：单个探针的估计本身带噪，相邻探针平均能压掉斑块
/// 工作按时间片推进，一次只处理一小批探针，编辑器帧不会被一次烘焙顶住
/// </remarks>
internal sealed class ProbeBakeJob
{
    /// <summary>单个轴向允许的最大探针数</summary>
    private const int MaxAxisCount = 128;

    /// <summary>命中不透明体素时的一次反弹权重，近似把它当作朗伯反射面</summary>
    private const float BounceWeight = .35F;

    /// <summary>太阳方向的判定半角，超出这个角度的天空不算直射</summary>
    private const float SunHalfAngleDegrees = 28F;

    private readonly VoxelWorld _world;
    private readonly ProbeBakeLighting _lighting;
    private readonly Vector3[] _directions;
    private readonly int _smoothing;
    private readonly Vector3 _origin;
    private readonly Vector3 _size;
    private readonly int _countX;
    private readonly int _countY;
    private readonly int _countZ;
    private readonly int _total;
    private readonly List<ProbeSample> _samples;
    private readonly VoxelCoord64 _minimum;
    private readonly VoxelCoord64 _maximum;
    private readonly float _maximumDistance;
    private readonly float _sunCosine;
    private int _index;

    private ProbeBakeJob(VoxelWorld world, ProbeVolumeComponentData data, Vector3 probePosition,
        ProbeBakeLighting lighting, ProbeBakeQuality quality, long sceneSignature,
        int countX, int countY, int countZ)
    {
        _world = world;
        _lighting = lighting;
        SceneSignature = sceneSignature;
        _smoothing = quality.Smoothing;
        _size = new Vector3(MathF.Max(data.SizeX, .001F), MathF.Max(data.SizeY, .001F), MathF.Max(data.SizeZ, .001F));
        _origin = probePosition + new Vector3(data.OffsetX, data.OffsetY, data.OffsetZ) - _size * .5F;
        _countX = countX;
        _countY = countY;
        _countZ = countZ;
        _total = countX * countY * countZ;
        _samples = new List<ProbeSample>(_total);
        _world.TryGetBounds(out _minimum, out _maximum);
        _directions = BuildSphereDirections(Math.Clamp(quality.RayCount, 8, 256));
        _maximumDistance = quality.Distance > 0F ? quality.Distance : MathF.Max(8F, _size.Length() * 1.5F);
        _sunCosine = MathF.Cos(SunHalfAngleDegrees * MathF.PI / 180F);
    }

    /// <summary>烘焙开始时世界的实体内容版本</summary>
    internal int WorldVersion => _world.ContentVersion;

    /// <summary>烘焙开始时场景内容指纹，用于中途作废判断</summary>
    internal long SceneSignature { get; }

    /// <summary>已完成比例，0 到 1</summary>
    internal float Progress => _total == 0 ? 1F : (float)_index / _total;

    /// <summary>探针总数</summary>
    internal int ProbeCount => _total;

    /// <summary>每个探针的采样方向数</summary>
    internal int RayCount => _directions.Length;

    /// <summary>
    /// 按探针尺寸与密度算出实际网格，超出总量上限时整体等比降密度
    /// </summary>
    /// <param name="data">探针组件参数</param>
    /// <param name="maxProbes">探针数量上限</param>
    /// <returns>三轴探针数量</returns>
    internal static (int X, int Y, int Z) ResolveGrid(ProbeVolumeComponentData data, int maxProbes)
    {
        var cap = Math.Clamp(maxProbes, 1024, 262144);
        var density = Math.Clamp(data.Density, .05F, 32F);
        var x = Math.Clamp((int)MathF.Ceiling(data.SizeX * density) + 1, 1, MaxAxisCount);
        var y = Math.Clamp((int)MathF.Ceiling(data.SizeY * density) + 1, 1, MaxAxisCount);
        var z = Math.Clamp((int)MathF.Ceiling(data.SizeZ * density) + 1, 1, MaxAxisCount);
        // 体积参数与密度一起放大时探针会到百万级，按总量上限等比收缩
        while ((long)x * y * z > cap)
        {
            var scale = MathF.Pow(cap / (float)((long)x * y * z), 1F / 3F) * .98F;
            var nextX = Math.Max(1, (int)(x * scale));
            var nextY = Math.Max(1, (int)(y * scale));
            var nextZ = Math.Max(1, (int)(z * scale));
            if (nextX == x && nextY == y && nextZ == z) break;
            x = nextX;
            y = nextY;
            z = nextZ;
        }

        return (x, y, z);
    }

    /// <summary>
    /// 创建一次烘焙任务，世界为空或参数无效时不创建
    /// </summary>
    /// <param name="world">用于遮挡与反弹取色的体素世界</param>
    /// <param name="probePosition">探针对象的世界位置</param>
    /// <param name="data">探针组件参数</param>
    /// <param name="lighting">环境光参数</param>
    /// <param name="sceneSignature">开始烘焙时的场景内容指纹</param>
    /// <param name="quality">烘焙质量参数</param>
    /// <returns>烘焙任务，无法烘焙时为 null</returns>
    internal static ProbeBakeJob? TryCreate(VoxelWorld world, Vector3 probePosition,
        ProbeVolumeComponentData data, ProbeBakeLighting lighting, long sceneSignature, ProbeBakeQuality quality)
    {
        if (world.OccupiedVoxelCount == 0) return null;
        var (countX, countY, countZ) = ResolveGrid(data, quality.MaxProbes);
        if ((long)countX * countY * countZ <= 0) return null;
        return new ProbeBakeJob(world, data, probePosition, lighting, quality, sceneSignature, countX, countY, countZ);
    }

    /// <summary>
    /// 推进一段时间的烘焙
    /// </summary>
    /// <param name="budgetMs">本次允许占用的毫秒数</param>
    /// <returns>烘焙完成时返回结果，否则返回 null</returns>
    internal ProbeVolumeBakeData? Step(double budgetMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (_index < _total)
        {
            BakeProbe(_index++);
            if (watch.Elapsed.TotalMilliseconds >= budgetMs) break;
        }

        if (_index < _total) return null;
        if (_smoothing > 0) Smooth(_smoothing);
        var result = new ProbeVolumeBakeData { CountX = _countX, CountY = _countY, CountZ = _countZ };
        result.Samples.AddRange(_samples);
        result.Normalize();
        return result;
    }

    /// <summary>
    /// 取样单个探针：沿固定方向集收集可见天空与一次反弹辐照度
    /// </summary>
    /// <param name="index">探针线性序号，X 最快</param>
    private void BakeProbe(int index)
    {
        var x = index % _countX;
        var y = index / _countX % _countY;
        var z = index / (_countX * _countY);
        var fx = _countX == 1 ? .5F : (float)x / (_countX - 1);
        var fy = _countY == 1 ? .5F : (float)y / (_countY - 1);
        var fz = _countZ == 1 ? .5F : (float)z / (_countZ - 1);
        // 与渲染端一致的取样点映射：中心加归一化偏移
        var position = new Vector3(
            _origin.X + fx * _size.X,
            _origin.Y + fy * _size.Y,
            _origin.Z + fz * _size.Z);

        var sample = new ProbeSample { X = fx, Y = fy, Z = fz, Occlusion = 0F };
        // 探针落进实心体素时没有可用的可见性，按完全遮挡记一笔
        if (_world.TryGet(new VoxelCoord64(
                (long)MathF.Floor(position.X), (long)MathF.Floor(position.Y), (long)MathF.Floor(position.Z)),
                out var inside) && !inside.IsEmpty)
        {
            sample.R = sample.G = sample.B = 0F;
            _samples.Add(sample);
            return;
        }

        var skyVisibility = 0F;
        var radiance = Vector3.Zero;
        foreach (var direction in _directions)
        {
            var hit = TraceHit(position, direction);
            if (hit is null)
            {
                skyVisibility += 1F;
                // 未命中按天空渐变取色，接近太阳方向再叠加一次直射
                var up = Math.Clamp(direction.Y * .5F + .5F, 0F, 1F);
                var sky = Vector3.Lerp(_lighting.SkyHorizon, _lighting.SkyTop, up);
                if (_lighting.SunStrength > 0F && Vector3.Dot(direction, _lighting.SunDirection) >= _sunCosine)
                    sky += _lighting.SunColor * _lighting.SunStrength;
                radiance += sky;
                continue;
            }

            var coordinate = hit.Value;
            if (!_world.TryGet(coordinate, out var value)) continue;
            var albedo = new Vector3(value.Color.R / 255F, value.Color.G / 255F, value.Color.B / 255F);
            if (VoxelFlags.Has(value.Flags, VoxelFlags.Emissive))
            {
                // 自发光体素直接当作光源
                radiance += albedo;
            }
            else if (VoxelFlags.Has(value.Flags, VoxelFlags.Translucent))
            {
                // 半透明体素既不算遮挡也不算强光源，按弱反弹处理
                radiance += albedo * BounceWeight * .5F;
            }
            else
            {
                // 一次反弹：用被命中面的反照率近似它反射回来的能量
                radiance += albedo * BounceWeight;
            }
        }

        sample.Occlusion = Math.Clamp(skyVisibility / _directions.Length, 0F, 1F);
        var average = radiance / _directions.Length;
        sample.R = Math.Clamp(average.X, 0F, 1F);
        sample.G = Math.Clamp(average.Y, 0F, 1F);
        sample.B = Math.Clamp(average.Z, 0F, 1F);
        _samples.Add(sample);
    }

    /// <summary>
    /// 对烘焙结果做三维盒式平滑，压掉相邻探针之间的斑块
    /// </summary>
    /// <param name="iterations">平滑次数</param>
    private void Smooth(int iterations)
    {
        if (_total <= 1) return;
        if (_countX < 3 && _countY < 3 && _countZ < 3) return;
        var source = _samples.ToArray();
        var target = new ProbeSample[source.Length];
        for (var pass = 0; pass < iterations; pass++)
        {
            for (var z = 0; z < _countZ; z++)
            for (var y = 0; y < _countY; y++)
            for (var x = 0; x < _countX; x++)
            {
                var sumR = 0F;
                var sumG = 0F;
                var sumB = 0F;
                var sumOcclusion = 0F;
                var count = 0F;
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    var nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= _countX || ny >= _countY || nz >= _countZ) continue;
                    var neighbor = source[nx + _countX * (ny + _countY * nz)];
                    sumR += neighbor.R;
                    sumG += neighbor.G;
                    sumB += neighbor.B;
                    sumOcclusion += neighbor.Occlusion;
                    count += 1F;
                }

                var index = x + _countX * (y + _countY * z);
                target[index] = new ProbeSample
                {
                    X = source[index].X,
                    Y = source[index].Y,
                    Z = source[index].Z,
                    R = sumR / count,
                    G = sumG / count,
                    B = sumB / count,
                    Occlusion = sumOcclusion / count,
                };
            }

            (source, target) = (target, source);
        }

        for (var index = 0; index < source.Length; index++) _samples[index] = source[index];
    }

    /// <summary>
    /// 在世界包围盒范围内找第一条射线命中的体素，飞出包围盒视为可见天空
    /// </summary>
    /// <param name="origin">射线起点</param>
    /// <param name="direction">单位方向</param>
    /// <returns>命中体素坐标，未命中返回 null</returns>
    private VoxelCoord64? TraceHit(Vector3 origin, Vector3 direction)
    {
        var enter = 0F;
        var exit = _maximumDistance;
        if (!ClipAxis(origin.X, direction.X, _minimum.X, _maximum.X + 1D, ref enter, ref exit) ||
            !ClipAxis(origin.Y, direction.Y, _minimum.Y, _maximum.Y + 1D, ref enter, ref exit) ||
            !ClipAxis(origin.Z, direction.Z, _minimum.Z, _maximum.Z + 1D, ref enter, ref exit))
        {
            return null;
        }

        // 入射点前留半格空气，让 DDA 精确判定边界格
        var start = MathF.Max(0F, enter - .5F);
        var length = MathF.Min(_maximumDistance, exit) - start;
        if (length <= 0F) return null;
        var hit = VoxelDda.RayCast(_world, origin + direction * start, direction, length);
        return hit?.Coordinate;
    }

    private static bool ClipAxis(float origin, float direction, double minimum, double maximum,
        ref float enter, ref float exit)
    {
        if (MathF.Abs(direction) < 1e-7F) return origin >= minimum && origin <= maximum;
        var near = (float)((minimum - origin) / direction);
        var far = (float)((maximum - origin) / direction);
        if (near > far) (near, far) = (far, near);
        enter = MathF.Max(enter, near);
        exit = MathF.Min(exit, far);
        return enter <= exit;
    }

    /// <summary>
    /// 生成均匀分布的球面方向集，用于探针的全向采样
    /// </summary>
    /// <param name="count">方向数量</param>
    /// <returns>单位方向数组</returns>
    private static Vector3[] BuildSphereDirections(int count)
    {
        var directions = new Vector3[count];
        var golden = MathF.PI * (3F - MathF.Sqrt(5F));
        for (var index = 0; index < count; index++)
        {
            var y = 1F - 2F * (index + .5F) / count;
            var radius = MathF.Sqrt(MathF.Max(0F, 1F - y * y));
            var theta = golden * index;
            directions[index] = new Vector3(MathF.Cos(theta) * radius, y, MathF.Sin(theta) * radius);
        }

        return directions;
    }
}
