using System.Numerics;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 曲线上的一个采样：世界无关的局部位置、走向、截面朝向与宽度
/// </summary>
/// <param name="Position">采样位置（局部体素坐标）</param>
/// <param name="Forward">切线方向，单位向量</param>
/// <param name="Up">截面上方向，已与切线正交</param>
/// <param name="Right">截面右方向，等于上方向叉乘切线</param>
/// <param name="Width">该处的宽度倍率</param>
public readonly record struct CurveSample(Vector3 Position, Vector3 Forward, Vector3 Up, Vector3 Right, float Width);

/// <summary>
/// 贝塞尔曲线的求值、切线推算与体素光栅化
/// </summary>
/// <remarks>
/// 求值分两步：先把每段曲线细分成折线并累计弧长，再按体素间距沿折线均匀取样。
/// 按弧长取样而不是按参数取样，拐弯处和长直线上的体素间距一致，不会出现一侧密集一侧稀疏。
/// 光栅化只产生局部坐标的体素，对象原点由编辑器在写入对象世界时补上
/// </remarks>
public static class CurveSpline
{
    /// <summary>单次光栅化的采样点上限，超出后截断并报告</summary>
    public const int MaximumStampCount = 40_000;

    /// <summary>
    /// 计算指定控制点处曲线的走向，用于摆放旋转手柄
    /// </summary>
    /// <param name="data">曲线数据</param>
    /// <param name="index">控制点索引</param>
    /// <returns>单位切向；无法计算时返回控制点朝向的前方</returns>
    public static Vector3 ForwardAt(CurveRendererComponentData data, int index)
    {
        var points = data.Points;
        if (points.Length == 0) return Vector3.UnitZ;
        index = Math.Clamp(index, 0, points.Length - 1);
        var count = points.Length;
        var forward = Vector3.Zero;
        if (count > 1)
        {
            var next = data.Closed ? points[(index + 1) % count] : index + 1 < count ? points[index + 1] : default;
            var previous = data.Closed ? points[(index - 1 + count) % count] : index > 0 ? points[index - 1] : default;
            var hasNext = data.Closed || index + 1 < count;
            var hasPrevious = data.Closed || index > 0;
            if (hasNext && hasPrevious) forward = next.Position - previous.Position;
            else if (hasNext) forward = next.Position - points[index].Position;
            else if (hasPrevious) forward = points[index].Position - previous.Position;
        }

        if (forward.LengthSquared() < 0.000001F) forward = Vector3.Transform(Vector3.UnitZ, points[index].Rotation);
        return forward.LengthSquared() < 0.000001F ? Vector3.UnitZ : Vector3.Normalize(forward);
    }

    /// <summary>
    /// 把控制点的旋转折算成已与曲线正交的上方向
    /// </summary>
    /// <param name="data">曲线数据</param>
    /// <param name="index">控制点索引</param>
    /// <param name="up">上方向</param>
    /// <param name="right">右方向</param>
    public static void BasisAt(CurveRendererComponentData data, int index, out Vector3 up, out Vector3 right)
    {
        var forward = ForwardAt(data, index);
        var hint = data.Points.Length == 0
            ? Vector3.UnitY
            : Vector3.Transform(Vector3.UnitY, data.Points[Math.Clamp(index, 0, data.Points.Length - 1)].Rotation);
        Orthonormalize(forward, hint, out up, out right);
    }

    /// <summary>
    /// 读取选中控制点的截面滚转角（度）：相对“世界 Y 为上参考”的正交基，
    /// 这是截面朝向里唯一真正影响生成形状的自由度
    /// </summary>
    /// <param name=data>曲线数据</param>
    /// <param name=index>控制点索引</param>
    /// <returns>滚转角，范围 -180 到 180 度</returns>
    public static float RollDegrees(CurveRendererComponentData data, int index)
    {
        if (data.Points.Length == 0) return 0F;
        var center = Math.Clamp(index, 0, data.Points.Length - 1);
        var forward = ForwardAt(data, center);
        ReferenceBasis(forward, out var referenceUp, out _);
        var hint = Vector3.Transform(Vector3.UnitY, data.Points[center].Rotation);
        Orthonormalize(forward, hint, out var up, out _);
        var cross = Vector3.Cross(referenceUp, up);
        var angle = MathF.Atan2(Vector3.Dot(cross, forward), Math.Clamp(Vector3.Dot(referenceUp, up), -1F, 1F));
        return angle * (180F / MathF.PI);
    }

    /// <summary>
    /// 按截面滚转角重建控制点朝向
    /// </summary>
    /// <param name=data>曲线数据</param>
    /// <param name=index>控制点索引</param>
    /// <param name=rollDegrees>滚转角（度）</param>
    /// <returns>新的控制点朝向</returns>
    public static Quaternion WithRoll(CurveRendererComponentData data, int index, float rollDegrees)
    {
        if (data.Points.Length == 0) return Quaternion.Identity;
        var center = Math.Clamp(index, 0, data.Points.Length - 1);
        var forward = ForwardAt(data, center);
        ReferenceBasis(forward, out var referenceUp, out _);
        var radians = rollDegrees * (MathF.PI / 180F);
        var up = Vector3.Transform(referenceUp, Quaternion.CreateFromAxisAngle(forward, radians));
        return CurveRotation.FromBasis(forward, up);
    }

    /// <summary>以世界 Y 为上参考、与切线正交的基准上方向</summary>
    private static void ReferenceBasis(Vector3 forward, out Vector3 up, out Vector3 right)
    {
        if (forward.LengthSquared() < 0.000001F) forward = Vector3.UnitZ;
        var normalized = Vector3.Normalize(forward);
        var hint = MathF.Abs(normalized.Y) > 0.98F ? Vector3.UnitX : Vector3.UnitY;
        Orthonormalize(normalized, hint, out up, out right);
    }

    /// <summary>
    /// 按曲线参数细分出折线采样，闭合曲线会包含回到起点的那一段
    /// </summary>
    /// <param name="data">曲线数据</param>
    /// <param name="samplesPerSegment">每段采样数</param>
    /// <param name="output">复用的输出列表，会先被清空</param>
    public static void BuildSamples(CurveRendererComponentData data, int samplesPerSegment, List<CurveSample> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        var points = data.Points;
        if (points.Length == 0) return;
        if (points.Length == 1)
        {
            BasisAt(data, 0, out var up, out var right);
            output.Add(new CurveSample(points[0].Position, ForwardAt(data, 0), up, right, points[0].Width));
            return;
        }

        var resolution = Math.Clamp(samplesPerSegment, CurveRendererComponentData.MinimumResolution,
            CurveRendererComponentData.MaximumResolution);
        var segmentCount = data.Closed ? points.Length : points.Length - 1;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var start = points[segment];
            var end = points[data.Closed ? (segment + 1) % points.Length : segment + 1];
            for (var step = 0; step <= resolution; step++)
            {
                // 闭合曲线最后一段的终点就是起点，跳过避免重复采样
                if (step == resolution && (data.Closed || segment < segmentCount - 1)) break;
                output.Add(EvaluateSegment(start, end, step / (float)resolution, data.Interpolation));
            }
        }
    }

    /// <summary>
    /// 求一段三次贝塞尔曲线上的采样
    /// </summary>
    /// <param name="start">起点</param>
    /// <param name="end">终点</param>
    /// <param name="t">0 到 1 的参数</param>
    /// <param name="interpolation">插值方式</param>
    /// <returns>采样结果</returns>
    public static CurveSample EvaluateSegment(CurvePointData start, CurvePointData end, float t,
        CurveInterpolation interpolation)
    {
        t = Math.Clamp(t, 0F, 1F);
        var p0 = start.Position;
        var p3 = end.Position;
        Vector3 position;
        Vector3 derivative;
        if (interpolation == CurveInterpolation.Linear)
        {
            position = Vector3.Lerp(p0, p3, t);
            derivative = p3 - p0;
        }
        else
        {
            var p1 = p0 + start.TangentOut;
            var p2 = p3 + end.TangentIn;
            var u = 1F - t;
            position = p0 * (u * u * u) + p1 * (3F * u * u * t) + p2 * (3F * u * t * t) + p3 * (t * t * t);
            derivative = (p1 - p0) * (3F * u * u) + (p2 - p1) * (6F * u * t) + (p3 - p2) * (3F * t * t);
        }

        if (derivative.LengthSquared() < 0.0000001F) derivative = p3 - p0;
        var forward = derivative.LengthSquared() < 0.0000001F
            ? Vector3.Transform(Vector3.UnitZ, start.Rotation) : Vector3.Normalize(derivative);
        var rotation = Quaternion.Slerp(start.Rotation, end.Rotation, t);
        Orthonormalize(forward, Vector3.Transform(Vector3.UnitY, rotation), out var up, out var right);
        return new CurveSample(position, forward, up, right, start.Width + (end.Width - start.Width) * t);
    }

    /// <summary>
    /// 由已知切线方向构造一组正交的截面基
    /// </summary>
    /// <param name="forward">切线方向</param>
    /// <param name="upHint">期望的上方向，会被投影到切线的垂面</param>
    /// <param name="up">正交后的上方向</param>
    /// <param name="right">右方向</param>
    public static void Orthonormalize(Vector3 forward, Vector3 upHint, out Vector3 up, out Vector3 right)
    {
        if (forward.LengthSquared() < 0.000001F) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        up = upHint - forward * Vector3.Dot(upHint, forward);
        if (up.LengthSquared() < 0.000001F)
        {
            // 俯仰到与切线平行的参考轴无法确定截面，换一条垂直轴
            var fallback = MathF.Abs(forward.Y) > 0.9F ? Vector3.UnitX : Vector3.UnitY;
            up = fallback - forward * Vector3.Dot(fallback, forward);
        }

        up = Vector3.Normalize(up);
        right = Vector3.Cross(up, forward);
        right = right.LengthSquared() < 0.000001F ? Vector3.UnitX : Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(forward, right));
    }

    /// <summary>
    /// 把切线向量拆成相对弦方向的长度、水平角与俯仰角
    /// </summary>
    /// <param name="tangent">切线向量</param>
    /// <param name="chord">参考弦方向</param>
    /// <param name="upHint">上方向参考</param>
    /// <param name="length">切线长度</param>
    /// <param name="yawDegrees">在水平面内的偏角</param>
    /// <param name="pitchDegrees">抬升角</param>
    public static void DecomposeTangent(Vector3 tangent, Vector3 chord, Vector3 upHint, out float length,
        out float yawDegrees, out float pitchDegrees)
    {
        length = tangent.Length();
        if (length < 0.0001F)
        {
            yawDegrees = 0F;
            pitchDegrees = 0F;
            return;
        }

        if (chord.LengthSquared() < 0.000001F) chord = Vector3.UnitX;
        Orthonormalize(chord, upHint, out var up, out var right);
        var forward = chord.LengthSquared() < 0.000001F ? Vector3.UnitX : Vector3.Normalize(chord);
        var direction = tangent / length;
        var forwardAmount = Vector3.Dot(direction, forward);
        var rightAmount = Vector3.Dot(direction, right);
        var upAmount = Vector3.Dot(direction, up);
        yawDegrees = MathF.Atan2(rightAmount, forwardAmount) * (180F / MathF.PI);
        pitchDegrees = MathF.Asin(Math.Clamp(upAmount, -1F, 1F)) * (180F / MathF.PI);
    }

    /// <summary>
    /// 由长度、水平角与俯仰角构造切线向量
    /// </summary>
    /// <param name="length">切线长度</param>
    /// <param name="yawDegrees">水平偏角</param>
    /// <param name="pitchDegrees">抬升角</param>
    /// <param name="chord">参考弦方向</param>
    /// <param name="upHint">上方向参考</param>
    /// <returns>切线向量</returns>
    public static Vector3 ComposeTangent(float length, float yawDegrees, float pitchDegrees, Vector3 chord,
        Vector3 upHint)
    {
        if (!float.IsFinite(length) || length < 0.0001F) return Vector3.Zero;
        if (chord.LengthSquared() < 0.000001F) chord = Vector3.UnitX;
        Orthonormalize(chord, upHint, out var up, out var right);
        var forward = Vector3.Normalize(chord);
        var yaw = yawDegrees * (MathF.PI / 180F);
        var pitch = Math.Clamp(pitchDegrees, -89F, 89F) * (MathF.PI / 180F);
        var horizontal = MathF.Cos(pitch);
        var direction = forward * (MathF.Cos(yaw) * horizontal) + right * (MathF.Sin(yaw) * horizontal) +
                        up * MathF.Sin(pitch);
        return direction * length;
    }

    /// <summary>
    /// 按相邻点重新推算自动模式控制点的切线
    /// </summary>
    /// <remarks>
    /// 采用 Catmull-Rom 的思路：切线方向取前后两点连线，长度按到相邻点的距离缩放。
    /// 这样点距不均匀时曲线也不会忽扁忽鼓，首尾点和闭合曲线分别按各自的邻点处理
    /// </remarks>
    /// <param name="points">控制点集合，会被就地修改</param>
    /// <param name="closed">是否闭合</param>
    public static void RecomputeAutoTangents(List<CurvePointData> points, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        var count = points.Count;
        if (count < 2) return;
        var updated = new CurvePointData[count];
        for (var index = 0; index < count; index++)
        {
            var point = points[index];
            if (point.TangentMode != CurveTangentMode.Auto)
            {
                updated[index] = point;
                continue;
            }

            var hasPrevious = closed || index > 0;
            var hasNext = closed || index + 1 < count;
            var previous = points[closed ? (index - 1 + count) % count : Math.Max(0, index - 1)];
            var next = points[closed ? (index + 1) % count : Math.Min(count - 1, index + 1)];
            var direction = next.Position - previous.Position;
            if (!hasPrevious) direction = next.Position - point.Position;
            else if (!hasNext) direction = point.Position - previous.Position;
            if (direction.LengthSquared() < 0.000001F)
            {
                updated[index] = point with { TangentIn = Vector3.Zero, TangentOut = Vector3.Zero };
                continue;
            }

            direction = Vector3.Normalize(direction);
            var toNext = Vector3.Distance(point.Position, next.Position);
            var toPrevious = Vector3.Distance(point.Position, previous.Position);
            var outLength = hasNext ? MathF.Max(0.01F, toNext / 3F) : 0F;
            var inLength = hasPrevious ? MathF.Max(0.01F, toPrevious / 3F) : 0F;
            updated[index] = point with
            {
                TangentOut = direction * outLength,
                TangentIn = -direction * inLength,
            };
        }

        for (var index = 0; index < count; index++) points[index] = updated[index];
    }

    /// <summary>
    /// 按联动方式修正某个控制点的另一侧切线
    /// </summary>
    /// <param name="point">控制点</param>
    /// <param name="editedOut">true 表示刚改了出切线，false 表示刚改入切线</param>
    /// <param name="forceSameLength">是否强制两侧等长</param>
    /// <returns>修正后的控制点</returns>
    public static CurvePointData AlignTangent(CurvePointData point, bool editedOut, bool forceSameLength = false)
    {
        var driven = editedOut ? point.TangentOut : point.TangentIn;
        var otherLength = MathF.Max(0.001F, (editedOut ? point.TangentIn : point.TangentOut).Length());
        if (driven.LengthSquared() < 0.000001F) return point;
        var direction = Vector3.Normalize(driven);
        var mirrored = -direction * (forceSameLength ? driven.Length() : otherLength);
        return editedOut ? point with { TangentIn = mirrored } : point with { TangentOut = mirrored };
    }

    /// <summary>
    /// 沿曲线均匀取样并盖章成体素，返回局部坐标的体素世界
    /// </summary>
    /// <param name="data">曲线数据</param>
    /// <param name="maximumVoxels">体素数量上限</param>
    /// <param name="truncated">是否因达到上限而截断</param>
    /// <returns>局部坐标的体素世界</returns>
    public static VoxelWorld Rasterize(CurveRendererComponentData data, int maximumVoxels, out bool truncated)
    {
        truncated = false;
        var world = new VoxelWorld();
        var normalized = data.Normalize();
        var points = normalized.Points;
        if (points.Length == 0) return world;
        var samples = new List<CurveSample>(Math.Max(8, points.Length * 16));
        BuildSamples(normalized, normalized.Resolution, samples);
        if (samples.Count == 0) return world;
        var distances = new float[samples.Count];
        for (var index = 1; index < samples.Count; index++)
            distances[index] = distances[index - 1] + Vector3.Distance(samples[index].Position, samples[index - 1].Position);
        var totalLength = distances[^1];
        var step = Math.Clamp(normalized.Step, 0.1F, 8F);
        var stamps = totalLength < 0.0001F ? 1 : (int)MathF.Ceiling(totalLength / step) + 1;
        stamps = Math.Clamp(stamps, 1, MaximumStampCount);
        var value = normalized.VoxelValue;
        var seen = new HashSet<VoxelCoord64>();
        var writer = world.CreateBulkWriter();
        if (normalized.Shape == CurveBrushShape.Beads)
        {
            // 珠链是沿曲线排列的离散球珠，仍按采样间距盖章，间距决定珠距
            var cursor = 1;
            for (var stamp = 0; stamp < stamps; stamp++)
            {
                var distance = stamps == 1 ? 0F : MathF.Min(totalLength, stamp * step);
                while (cursor < samples.Count - 1 && distances[cursor] < distance) cursor++;
                var sample = Interpolate(samples, distances, cursor, distance);
                Stamp(writer, seen, sample, normalized, step, maximumVoxels, value, ref truncated);
                if (truncated) return world;
            }

            Stamp(writer, seen, samples[^1], normalized, step, maximumVoxels, value, ref truncated);
            return world;
        }

        // 方块管、圆管与十字管改成沿相邻采样的连线扫掠：只按采样点盖章时，
        // 拐角与截面朝向变化处会漏格，扫掠能保证整条管连续
        if (samples.Count == 1)
        {
            Stamp(writer, seen, samples[0], normalized, step, maximumVoxels, value, ref truncated);
            return world;
        }

        for (var index = 1; index < samples.Count; index++)
        {
            StampSegment(writer, seen, samples[index - 1], samples[index], normalized, maximumVoxels, value,
                ref truncated);
            if (truncated) return world;
        }

        return world;
    }

    /// <summary>
    /// 沿相邻两个采样的连线扫掠截面，拐角处也不会断
    /// </summary>
    /// <param name="writer">批量写入器</param>
    /// <param name="seen">已写入的坐标集合</param>
    /// <param name="start">线段起点采样</param>
    /// <param name="end">线段终点采样</param>
    /// <param name="data">曲线数据</param>
    /// <param name="maximumVoxels">体素上限</param>
    /// <param name="value">写入值</param>
    /// <param name="truncated">是否截断</param>
    private static void StampSegment(VoxelWorld.BulkWriter writer, HashSet<VoxelCoord64> seen,
        CurveSample start, CurveSample end, CurveRendererComponentData data, int maximumVoxels,
        VoxelValue value, ref bool truncated)
    {
        var delta = end.Position - start.Position;
        var lengthSquared = delta.LengthSquared();
        var length = MathF.Sqrt(lengthSquared);
        var forward = length > 0.0001F ? delta / length : start.Forward;
        var radiusStart = MathF.Max(0.1F, data.Radius * MathF.Max(0.05F, start.Width));
        var radiusEnd = MathF.Max(0.1F, data.Radius * MathF.Max(0.05F, end.Width));
        var reach = MathF.Max(radiusStart, radiusEnd) + 1F;
        var minimumX = (long)MathF.Floor(MathF.Min(start.Position.X, end.Position.X) - reach);
        var maximumX = (long)MathF.Ceiling(MathF.Max(start.Position.X, end.Position.X) + reach);
        var minimumY = (long)MathF.Floor(MathF.Min(start.Position.Y, end.Position.Y) - reach);
        var maximumY = (long)MathF.Ceiling(MathF.Max(start.Position.Y, end.Position.Y) + reach);
        var minimumZ = (long)MathF.Floor(MathF.Min(start.Position.Z, end.Position.Z) - reach);
        var maximumZ = (long)MathF.Ceiling(MathF.Max(start.Position.Z, end.Position.Z) + reach);
        for (var x = minimumX; x <= maximumX; x++)
        for (var y = minimumY; y <= maximumY; y++)
        for (var z = minimumZ; z <= maximumZ; z++)
        {
            var center = new Vector3(x + 0.5F, y + 0.5F, z + 0.5F);
            // 先求体素中心在线段上的最近点，再用该处的截面基判断是否落在管体内
            var t = lengthSquared < 0.000001F
                ? 0F
                : Math.Clamp(Vector3.Dot(center - start.Position, delta) / lengthSquared, 0F, 1F);
            var closest = start.Position + delta * t;
            var radius = radiusStart + (radiusEnd - radiusStart) * t;
            Orthonormalize(forward, Vector3.Lerp(start.Up, end.Up, t), out var up, out var right);
            var offset = center - closest;
            var along = Vector3.Dot(offset, right);
            var height = Vector3.Dot(offset, up);
            var inside = data.Shape switch
            {
                CurveBrushShape.Square => MathF.Abs(along) <= radius && MathF.Abs(height) <= radius,
                CurveBrushShape.Round => along * along + height * height <= radius * radius,
                _ => (MathF.Abs(along) <= radius * 0.34F && MathF.Abs(height) <= radius ||
                      MathF.Abs(height) <= radius * 0.34F && MathF.Abs(along) <= radius),
            };
            if (!inside) continue;
            var coordinate = new VoxelCoord64(x, y, z);
            if (!seen.Add(coordinate)) continue;
            if (seen.Count > maximumVoxels)
            {
                truncated = true;
                return;
            }

            writer.Set(coordinate, value);
        }
    }

    /// <summary>
    /// 按弧长在相邻采样之间插值
    /// </summary>
    private static CurveSample Interpolate(List<CurveSample> samples, float[] distances, int index, float distance)
    {
        var previous = samples[Math.Max(0, index - 1)];
        var next = samples[Math.Min(samples.Count - 1, index)];
        var span = MathF.Max(0.00001F, distances[Math.Min(samples.Count - 1, index)] -
            distances[Math.Max(0, index - 1)]);
        var t = Math.Clamp((distance - distances[Math.Max(0, index - 1)]) / span, 0F, 1F);
        var forward = Vector3.Lerp(previous.Forward, next.Forward, t);
        var up = Vector3.Lerp(previous.Up, next.Up, t);
        Orthonormalize(forward, up, out var upResult, out var right);
        return new CurveSample(Vector3.Lerp(previous.Position, next.Position, t),
            forward.LengthSquared() < 0.000001F ? next.Forward : Vector3.Normalize(forward),
            upResult, right, previous.Width + (next.Width - previous.Width) * t);
    }

    /// <summary>
    /// 把一处采样的截面盖进体素世界，重复坐标只写一次
    /// </summary>
    /// <param name="writer">批量写入器</param>
    /// <param name="seen">已写入的坐标集合</param>
    /// <param name="sample">采样</param>
    /// <param name="data">曲线数据</param>
    /// <param name="step">采样间距，用来决定沿切线的盖印厚度</param>
    /// <param name="maximumVoxels">体素上限</param>
    /// <param name="value">写入值</param>
    /// <param name="truncated">是否截断</param>
    private static void Stamp(VoxelWorld.BulkWriter writer, HashSet<VoxelCoord64> seen, CurveSample sample,
        CurveRendererComponentData data, float step, int maximumVoxels, VoxelValue value, ref bool truncated)
    {
        var radius = MathF.Max(0.1F, data.Radius * MathF.Max(0.05F, sample.Width));
        // 沿切线方向留出半步加上半格，采样之间即使有间隔也能连起来
        var halfSlab = MathF.Max(0.5F, step * 0.5F + 0.5F);
        var reach = radius + halfSlab + 1F;
        var minimumX = (long)MathF.Floor(sample.Position.X - reach);
        var maximumX = (long)MathF.Ceiling(sample.Position.X + reach);
        var minimumY = (long)MathF.Floor(sample.Position.Y - reach);
        var maximumY = (long)MathF.Ceiling(sample.Position.Y + reach);
        var minimumZ = (long)MathF.Floor(sample.Position.Z - reach);
        var maximumZ = (long)MathF.Ceiling(sample.Position.Z + reach);
        for (var x = minimumX; x <= maximumX; x++)
        for (var y = minimumY; y <= maximumY; y++)
        for (var z = minimumZ; z <= maximumZ; z++)
        {
            var center = new Vector3(x + 0.5F, y + 0.5F, z + 0.5F);
            var offset = center - sample.Position;
            var along = Vector3.Dot(offset, sample.Right);
            var height = Vector3.Dot(offset, sample.Up);
            var depth = Vector3.Dot(offset, sample.Forward);
            var inside = data.Shape switch
            {
                CurveBrushShape.Square => MathF.Abs(along) <= radius && MathF.Abs(height) <= radius &&
                                          MathF.Abs(depth) <= halfSlab,
                CurveBrushShape.Round => along * along + height * height <= radius * radius &&
                                         MathF.Abs(depth) <= halfSlab,
                CurveBrushShape.Beads => along * along + height * height + depth * depth <= radius * radius,
                _ => (MathF.Abs(along) <= radius * 0.34F && MathF.Abs(height) <= radius ||
                      MathF.Abs(height) <= radius * 0.34F && MathF.Abs(along) <= radius) &&
                     MathF.Abs(depth) <= halfSlab,
            };
            if (!inside) continue;
            var coordinate = new VoxelCoord64(x, y, z);
            if (!seen.Add(coordinate)) continue;
            if (seen.Count > maximumVoxels)
            {
                truncated = true;
                return;
            }

            writer.Set(coordinate, value);
        }
    }
}

/// <summary>
/// 欧拉角与四元数的转换，供检查器按角度编辑控制点朝向
/// </summary>
public static class CurveRotation
{
    /// <summary>
    /// 四元数转 XYZ 顺序的欧拉角（度）
    /// </summary>
    /// <param name="rotation">旋转</param>
    /// <returns>X、Y、Z 三个角度</returns>
    public static Vector3 ToEulerDegrees(Quaternion rotation)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        // 与 CreateFromYawPitchRoll 对称的分解：X 是俯仰、Y 是偏航、Z 是翻滚
        var pitch = MathF.Atan2(-matrix.M32,
            MathF.Sqrt(matrix.M31 * matrix.M31 + matrix.M33 * matrix.M33));
        var yaw = MathF.Atan2(matrix.M31, matrix.M33);
        var roll = MathF.Atan2(matrix.M12, matrix.M22);
        const float toDegrees = 180F / MathF.PI;
        return new Vector3(pitch * toDegrees, yaw * toDegrees, roll * toDegrees);
    }

    /// <summary>
    /// XYZ 顺序欧拉角（度）转四元数
    /// </summary>
    /// <param name="degrees">X、Y、Z 三个角度</param>
    /// <returns>单位旋转</returns>
    public static Quaternion FromEulerDegrees(Vector3 degrees)
    {
        const float toRadians = MathF.PI / 180F;
        var rotation = Quaternion.CreateFromYawPitchRoll(degrees.Y * toRadians, degrees.X * toRadians,
            degrees.Z * toRadians);
        return Quaternion.Normalize(rotation);
    }

    /// <summary>
    /// 由曲线走向与上方向构造控制点朝向
    /// </summary>
    /// <param name="forward">曲线走向</param>
    /// <param name="up">期望的上方向</param>
    /// <returns>控制点旋转</returns>
    public static Quaternion FromBasis(Vector3 forward, Vector3 up)
    {
        CurveSpline.Orthonormalize(forward, up, out var orthonormalUp, out _);
        if (forward.LengthSquared() < 0.000001F) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
            Matrix4x4.CreateWorld(Vector3.Zero, forward, orthonormalUp)));
    }
}
