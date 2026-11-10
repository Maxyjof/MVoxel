using System.Numerics;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 场景对象阵列的方式
/// </summary>
public enum SceneArrayMode
{
    /// <summary>矩形：按三轴数量与间距复制</summary>
    Rectangular,

    /// <summary>环形：绕指定轴复制一圈</summary>
    Ring,

    /// <summary>镜像：越过中心平面翻一份</summary>
    Mirror,
}

/// <summary>
/// 矩形阵列的间距口径
/// </summary>
public enum SceneArraySpacing
{
    /// <summary>直接按格数间距</summary>
    Cells,

    /// <summary>按选中内容的包围尺寸加固定间隔</summary>
    BoundingBox,
}

/// <summary>
/// 对象阵列参数：模式、数量、间距与旋转轴
/// </summary>
/// <param name="Mode">阵列方式</param>
/// <param name="Counts">矩形阵列三轴份数，每轴 1 到 256</param>
/// <param name="Steps">矩形阵列三轴间距（格）</param>
/// <param name="Gap">按包围盒时的额外间隔格数</param>
/// <param name="Spacing">间距口径</param>
/// <param name="Axis">环形与镜像使用的轴，0 为 X、1 为 Y、2 为 Z</param>
/// <param name="RingCount">环形份数，4 到 64</param>
/// <param name="Group">是否把副本打到一个新建父对象下</param>
/// <param name="Center">自定义旋转与镜像中心，null 表示按选中内容自动</param>
public readonly record struct SceneArraySettings(
    SceneArrayMode Mode = SceneArrayMode.Rectangular,
    VoxelCoord64 Counts = default,
    VoxelCoord64 Steps = default,
    int Gap = 1,
    SceneArraySpacing Spacing = SceneArraySpacing.Cells,
    int Axis = 1,
    int RingCount = 8,
    bool Group = true,
    Vector3? Center = null)
{
    /// <summary>默认参数：沿 X 复制 3 份、间距按选中包围盒</summary>
    public static SceneArraySettings Default => new(
        SceneArrayMode.Rectangular, new VoxelCoord64(3, 1, 1), new VoxelCoord64(4, 0, 0));

    /// <summary>
    /// 把数量、间距与轴约束到可用范围
    /// </summary>
    /// <returns>规范化后的参数</returns>
    public SceneArraySettings Normalize() => this with
    {
        Counts = new VoxelCoord64(
            Math.Clamp(Counts.X, 1, 256), Math.Clamp(Counts.Y, 1, 256), Math.Clamp(Counts.Z, 1, 256)),
        Steps = new VoxelCoord64(
            Math.Clamp(Steps.X, -4096, 4096), Math.Clamp(Steps.Y, -4096, 4096), Math.Clamp(Steps.Z, -4096, 4096)),
        Gap = Math.Clamp(Gap, 0, 256),
        Axis = Axis is 0 or 1 or 2 ? Axis : 1,
        RingCount = Math.Clamp(RingCount, 4, 64),
        Spacing = Enum.IsDefined(Spacing) ? Spacing : SceneArraySpacing.BoundingBox,
    };
}

/// <summary>
/// 一个副本的变换：平移、绕轴旋转与镜像
/// </summary>
/// <param name="Index">副本序号，从 1 开始；原件不算在内</param>
/// <param name="Translation">整格平移</param>
/// <param name="RotationDegrees">绕阵列轴的旋转角，0 表示不转</param>
/// <param name="MirrorAxis">镜像轴，-1 表示不镜像</param>
public readonly record struct SceneArrayCopy(
    int Index, VoxelCoord64 Translation, float RotationDegrees, int MirrorAxis);

/// <summary>
/// 对象阵列的纯几何计算
/// </summary>
/// <remarks>
/// 体素世界存的是世界坐标，副本不能只改变换，必须把体素真正搬到新位置。
/// 90 度倍数用整数公式保证不破格；其他角度用目标格反查重采样，允许轻微形变但不留空洞
/// </remarks>
public static class SceneObjectArrayMath
{
    /// <summary>单次阵列允许生成的最大副本数，避免误填数量撑爆场景</summary>
    public const int MaximumCopies = 4096;

    /// <summary>
    /// 计算每个副本的变换；原件不在列表里
    /// </summary>
    /// <param name="settings">阵列参数</param>
    /// <param name="boundsSize">选中内容的包围尺寸，按包围盒间距时使用</param>
    /// <returns>副本变换列表</returns>
    public static IReadOnlyList<SceneArrayCopy> Plan(SceneArraySettings settings, VoxelCoord64 boundsSize)
    {
        var data = settings.Normalize();
        var copies = new List<SceneArrayCopy>();
        switch (data.Mode)
        {
            case SceneArrayMode.Rectangular:
            {
                var stepX = data.Spacing == SceneArraySpacing.Cells ? (int)data.Steps.X : (int)boundsSize.X + data.Gap;
                var stepY = data.Spacing == SceneArraySpacing.Cells ? (int)data.Steps.Y : (int)boundsSize.Y + data.Gap;
                var stepZ = data.Spacing == SceneArraySpacing.Cells ? (int)data.Steps.Z : (int)boundsSize.Z + data.Gap;
                var index = 0;
                for (var z = 0; z < data.Counts.Z; z++)
                for (var y = 0; y < data.Counts.Y; y++)
                for (var x = 0; x < data.Counts.X; x++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    copies.Add(new SceneArrayCopy(++index,
                        new VoxelCoord64(x * stepX, y * stepY, z * stepZ), 0F, -1));
                    if (copies.Count >= MaximumCopies) return copies;
                }

                break;
            }
            case SceneArrayMode.Ring:
            {
                var stepDegrees = 360F / data.RingCount;
                for (var index = 1; index < data.RingCount; index++)
                {
                    copies.Add(new SceneArrayCopy(index, VoxelCoord64.Zero, stepDegrees * index, -1));
                    if (copies.Count >= MaximumCopies) return copies;
                }

                break;
            }
            default:
                copies.Add(new SceneArrayCopy(1, VoxelCoord64.Zero, 0F, data.Axis));
                break;
        }

        return copies;
    }

    /// <summary>
    /// 把体素世界按副本变换复制一份；平移与 90 度旋转走精确路径
    /// </summary>
    /// <param name="source">原件体素世界</param>
    /// <param name="copy">副本变换</param>
    /// <param name="pivot">阵列中心，旋转与镜像都围绕它</param>
    /// <param name="axis">阵列轴</param>
    /// <returns>新的体素世界</returns>
    public static VoxelWorld Transform(VoxelWorld source, SceneArrayCopy copy, Vector3 pivot, int axis)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (copy.Translation == VoxelCoord64.Zero && copy.RotationDegrees == 0F && copy.MirrorAxis < 0)
            return source.Clone();
        var world = new VoxelWorld();
        var writer = world.CreateBulkWriter();
        if (copy.MirrorAxis >= 0)
        {
            foreach (var (coordinate, value) in source.EnumerateOccupied())
                writer.Set(MirrorCell(coordinate, copy.MirrorAxis, pivot), value);
            return world;
        }

        // 只有 90 度倍数才走整数格精确旋转；其他角度必须先排除，
        // 否则 45、315 这类角度会被四舍五入成 0 圈，副本直接叠回原位
        var exactQuarter = copy.RotationDegrees % 90F == 0F;
        var quarters = exactQuarter ? QuarterTurns(copy.RotationDegrees) : 0;
        if (exactQuarter && quarters == 0)
        {
            foreach (var (coordinate, value) in source.EnumerateOccupied())
                writer.Set(coordinate + copy.Translation, value);
            return world;
        }

        if (exactQuarter)
        {
            foreach (var (coordinate, value) in source.EnumerateOccupied())
                writer.Set(RotateCellExact(coordinate, pivot, axis, quarters) + copy.Translation, value);
            return world;
        }

        // 任意角度：按旋转后的包围盒逐格反查源坐标，保证不留空洞
        var rotation = Quaternion.CreateFromAxisAngle(AxisVector(axis), copy.RotationDegrees * MathF.PI / 180F);
        var inverse = Quaternion.Conjugate(rotation);
        if (!source.TryGetBounds(out var minimum, out var maximum)) return world;
        var corners = new Vector3[8];
        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? minimum.X : maximum.X + 1,
                (corner & 2) == 0 ? minimum.Y : maximum.Y + 1,
                (corner & 4) == 0 ? minimum.Z : maximum.Z + 1);
            corners[corner] = Vector3.Transform(point - pivot, rotation) + pivot;
        }

        var min = corners[0];
        var max = corners[0];
        foreach (var corner in corners)
        {
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }

        for (var z = (long)MathF.Floor(min.Z); z <= (long)MathF.Ceiling(max.Z); z++)
        for (var y = (long)MathF.Floor(min.Y); y <= (long)MathF.Ceiling(max.Y); y++)
        for (var x = (long)MathF.Floor(min.X); x <= (long)MathF.Ceiling(max.X); x++)
        {
            var center = new Vector3(x + 0.5F, y + 0.5F, z + 0.5F);
            var local = Vector3.Transform(center - pivot, inverse) + pivot;
            var sourceCell = new VoxelCoord64(
                (long)MathF.Floor(local.X), (long)MathF.Floor(local.Y), (long)MathF.Floor(local.Z));
            if (!source.TryGet(sourceCell, out var value)) continue;
            writer.Set(new VoxelCoord64(x, y, z) + copy.Translation, value);
        }

        return world;
    }

    /// <summary>
    /// 把世界点按副本变换旋转或镜像，用于非体素对象的坐标
    /// </summary>
    /// <param name="point">原世界坐标</param>
    /// <param name="copy">副本变换</param>
    /// <param name="pivot">阵列中心</param>
    /// <param name="axis">阵列轴</param>
    /// <returns>变换后的世界坐标</returns>
    public static Vector3 TransformPoint(Vector3 point, SceneArrayCopy copy, Vector3 pivot, int axis)
    {
        if (copy.MirrorAxis >= 0)
        {
            var value = point;
            switch (copy.MirrorAxis)
            {
                case 0: value.X = 2F * pivot.X - point.X; break;
                case 1: value.Y = 2F * pivot.Y - point.Y; break;
                default: value.Z = 2F * pivot.Z - point.Z; break;
            }

            return value;
        }

        if (copy.RotationDegrees == 0F) return point + new Vector3(copy.Translation.X, copy.Translation.Y,
            copy.Translation.Z);
        var rotation = Quaternion.CreateFromAxisAngle(AxisVector(axis), copy.RotationDegrees * MathF.PI / 180F);
        return Vector3.Transform(point - pivot, rotation) + pivot +
               new Vector3(copy.Translation.X, copy.Translation.Y, copy.Translation.Z);
    }

    /// <summary>
    /// 镜像后的朝向：对旋转做一次平面反射共轭
    /// </summary>
    /// <param name="rotation">原朝向</param>
    /// <param name="mirrorAxis">镜像轴</param>
    /// <returns>镜像后的朝向</returns>
    public static Quaternion MirrorRotation(Quaternion rotation, int mirrorAxis) => mirrorAxis switch
    {
        0 => new Quaternion(rotation.W, rotation.X, -rotation.Y, -rotation.Z),
        1 => new Quaternion(rotation.W, -rotation.X, rotation.Y, -rotation.Z),
        _ => new Quaternion(rotation.W, -rotation.X, -rotation.Y, rotation.Z),
    };

    /// <summary>轴索引转单位向量</summary>
    public static Vector3 AxisVector(int axis) => axis switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>90 度倍数换算成四分之一圈数</summary>
    public static int QuarterTurns(float degrees) =>
        ((int)MathF.Round(degrees / 90F) % 4 + 4) % 4;

    private static VoxelCoord64 RotateCellExact(VoxelCoord64 cell, Vector3 pivot, int axis, int quarters) =>
        RotateAroundPivot(cell, pivot.X, pivot.Y, pivot.Z, axis, quarters);

    /// <summary>
    /// 绕中心旋转整数格：抬高到双倍坐标后再旋转，半格中心也是精确的
    /// </summary>
    private static VoxelCoord64 RotateAroundPivot(VoxelCoord64 cell, float pivotX, float pivotY, float pivotZ,
        int axis, int quarters)
    {
        var px2 = (long)MathF.Round(pivotX * 2F);
        var py2 = (long)MathF.Round(pivotY * 2F);
        var pz2 = (long)MathF.Round(pivotZ * 2F);
        var x2 = (cell.X * 2 + 1) - px2;
        var y2 = (cell.Y * 2 + 1) - py2;
        var z2 = (cell.Z * 2 + 1) - pz2;
        for (var turn = 0; turn < quarters; turn++)
            (x2, y2, z2) = axis switch
            {
                0 => (x2, -z2, y2),
                1 => (z2, y2, -x2),
                _ => (-y2, x2, z2),
            };
        return new VoxelCoord64(
            (long)Math.Floor((x2 + px2 - 1) / 2D), (long)Math.Floor((y2 + py2 - 1) / 2D),
            (long)Math.Floor((z2 + pz2 - 1) / 2D));
    }

    private static VoxelCoord64 MirrorCell(VoxelCoord64 cell, int axis, Vector3 pivot)
    {
        var value = cell;
        switch (axis)
        {
            case 0:
                value = new VoxelCoord64((long)Math.Round(2D * pivot.X - cell.X - 1D), cell.Y, cell.Z);
                break;
            case 1:
                value = new VoxelCoord64(cell.X, (long)Math.Round(2D * pivot.Y - cell.Y - 1D), cell.Z);
                break;
            default:
                value = new VoxelCoord64(cell.X, cell.Y, (long)Math.Round(2D * pivot.Z - cell.Z - 1D));
                break;
        }

        return value;
    }
}
