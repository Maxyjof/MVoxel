using System.Numerics;
using MVoxel.Core;

namespace MVoxel.Voxels;

/// <summary>
/// 稀疏世界上的 CPU 版 DDA（Amanatides 与 Woo 体素遍历）
/// 编辑器用它做鼠标拾取，GPU 管线在 GLSL 里实现同一套遍历，
/// 保留托管参考版意味着拾取、测试与着色器永远共享同一语义
/// </summary>
public static class VoxelDda
{
    /// <param name="Coordinate">命中的占用格</param>
    /// <param name="Normal">进入面单位法线（单轴 ±1）</param>
    /// <param name="Distance">沿射线行进的距离</param>
    /// <param name="Point">世界空间命中点</param>
    public readonly record struct Hit(VoxelCoord64 Coordinate, VoxelCoord64 Normal, float Distance, Vector3 Point);

    /// <summary>
    /// 沿射线查找第一个占用体素
    /// </summary>
    /// <param name="world">要遍历的世界</param>
    /// <param name="origin">射线起点</param>
    /// <param name="direction">射线方向，无需预先归一化</param>
    /// <param name="maximumDistance">最大行进距离，防止在空旷区域无限步进</param>
    /// <param name="ignoreOriginCell">是否忽略射线起点所在的占用格</param>
    /// <returns>命中结果，未命中返回 null</returns>
    public static Hit? RayCast(VoxelWorld world, Vector3 origin, Vector3 direction, float maximumDistance = 512F,
        bool ignoreOriginCell = false)
    {
        if (Vector3.Dot(direction, direction) < 1e-12F || maximumDistance <= 0F) return null;
        var step = Vector3.Normalize(direction);

        var cell = new VoxelCoord64(
            (long)MathF.Floor(origin.X), (long)MathF.Floor(origin.Y), (long)MathF.Floor(origin.Z));
        var delta = new Vector3(
            ReciprocalDelta(step.X), ReciprocalDelta(step.Y), ReciprocalDelta(step.Z));
        var next = new Vector3(
            BoundaryDistance(origin.X, cell.X, step.X),
            BoundaryDistance(origin.Y, cell.Y, step.Y),
            BoundaryDistance(origin.Z, cell.Z, step.Z));
        var sign = (X: (int)MathF.Sign(step.X), Y: (int)MathF.Sign(step.Y), Z: (int)MathF.Sign(step.Z));

        var travelled = 0F;
        var isOriginCell = true;
        var normal = InitialNormal(step);
        // 步数预算防止射线穿越超大空旷区域
        var stepsLeft = (int)Math.Clamp(maximumDistance * 4F, 256F, 250_000F);
        while (stepsLeft-- > 0)
        {
            if ((!ignoreOriginCell || !isOriginCell) && world.TryGet(cell, out var value) && !value.IsEmpty)
            {
                return new Hit(cell, normal, travelled, origin + step * travelled);
            }

            isOriginCell = false;

            if (next.X <= next.Y && next.X <= next.Z)
            {
                travelled = next.X;
                next.X += delta.X;
                cell = cell with { X = cell.X + sign.X };
                normal = new VoxelCoord64(-sign.X, 0, 0);
            }
            else if (next.Y <= next.Z)
            {
                travelled = next.Y;
                next.Y += delta.Y;
                cell = cell with { Y = cell.Y + sign.Y };
                normal = new VoxelCoord64(0, -sign.Y, 0);
            }
            else
            {
                travelled = next.Z;
                next.Z += delta.Z;
                cell = cell with { Z = cell.Z + sign.Z };
                normal = new VoxelCoord64(0, 0, -sign.Z);
            }

            if (travelled > maximumDistance) return null;
        }

        return null;
    }

    /// <summary>
    /// 射线与无限地面（y = 0）求交，拾取落空时让第一笔能落在空画布上
    /// </summary>
    /// <param name="origin">射线起点</param>
    /// <param name="direction">射线方向</param>
    /// <param name="maximumDistance">最大行进距离</param>
    /// <param name="point">命中点</param>
    /// <returns>是否在有效距离内命中地面</returns>
    public static bool TryGroundHit(Vector3 origin, Vector3 direction, float maximumDistance, out Vector3 point)
    {
        point = default;
        if (Math.Abs(direction.Y) < 1e-7F) return false;
        var t = -origin.Y / direction.Y;
        if (t is <= 0F or > float.MaxValue / 2F || t > maximumDistance) return false;
        point = origin + Vector3.Normalize(direction) * t;
        return true;
    }

    /// <summary>
    /// 单轴跨越一格所需的参数增量，轴分量为 0 时返回无穷
    /// </summary>
    /// <param name="step">归一化方向的分量</param>
    /// <returns>参数增量或无穷</returns>
    private static float ReciprocalDelta(float step) =>
        Math.Abs(step) < 1e-7F ? float.PositiveInfinity : Math.Abs(1F / step);

    /// <summary>
    /// 起点到所在格沿步进方向边界的参数距离
    /// </summary>
    /// <param name="axisOrigin">起点在该轴的分量</param>
    /// <param name="cellIndex">该轴的格索引</param>
    /// <param name="step">该轴的步进符号与大小</param>
    /// <returns>到边界的参数距离</returns>
    private static float BoundaryDistance(float axisOrigin, long cellIndex, float step)
    {
        if (Math.Abs(step) < 1e-7F) return float.PositiveInfinity;
        var boundary = step > 0 ? cellIndex + 1F : cellIndex;
        return Math.Abs((boundary - axisOrigin) / step);
    }

    /// <summary>
    /// 射线起点已在体素内部时没有进入面，取主轴方向作为近似法线，
    /// GPU 版使用同一规则保证像素与拾取一致
    /// </summary>
    /// <param name="step">归一化方向</param>
    /// <returns>近似进入面法线</returns>
    private static VoxelCoord64 InitialNormal(Vector3 step)
    {
        var (x, y, z) = (Math.Abs(step.X), Math.Abs(step.Y), Math.Abs(step.Z));
        if (x >= y && x >= z) return new VoxelCoord64(-(long)MathF.Sign(step.X), 0, 0);
        if (y >= z) return new VoxelCoord64(0, -(long)MathF.Sign(step.Y), 0);
        return new VoxelCoord64(0, 0, -(long)MathF.Sign(step.Z));
    }
}
