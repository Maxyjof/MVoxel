using System.Numerics;

namespace MVoxel.Core;

/// <summary>
/// 稀疏体素世界中的整数坐标，任何渲染和变换都以它为最小单位
/// </summary>
/// <param name="X">X 轴格坐标</param>
/// <param name="Y">Y 轴格坐标（竖直向上）</param>
/// <param name="Z">Z 轴格坐标</param>
public readonly record struct VoxelCoord64(long X, long Y, long Z)
{
    /// <summary>
    /// 原点坐标
    /// </summary>
    public static VoxelCoord64 Zero => new(0, 0, 0);

    /// <summary>
    /// 两个坐标逐分量相加
    /// </summary>
    /// <param name="left">左操作数</param>
    /// <param name="right">右操作数</param>
    /// <returns>相加后的坐标</returns>
    public static VoxelCoord64 operator +(VoxelCoord64 left, VoxelCoord64 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    /// <summary>
    /// 两个坐标逐分量相减
    /// </summary>
    /// <param name="left">左操作数</param>
    /// <param name="right">右操作数</param>
    /// <returns>相减后的坐标</returns>
    public static VoxelCoord64 operator -(VoxelCoord64 left, VoxelCoord64 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
}

/// <summary>
/// 双精度三分量值，用于自由变换物体的连续位置（相机、灯光等）
/// </summary>
/// <param name="X">X 分量</param>
/// <param name="Y">Y 分量</param>
/// <param name="Z">Z 分量</param>
public readonly record struct Double3(double X, double Y, double Z)
{
    /// <summary>
    /// 全零向量
    /// </summary>
    public static Double3 Zero => new(0, 0, 0);
}

/// <summary>
/// 带符号置换矩阵，表示体素模型允许的全部旋转：恒等与绕三个轴的 90 度整数倍
/// 它保证任意旋转作用在整数坐标上仍是整数坐标，杜绝浮点漂移
/// </summary>
public readonly struct OrthogonalRotation : IEquatable<OrthogonalRotation>
{
    /// <summary>
    /// 按九个矩阵元素构造旋转，非法矩阵立即抛异常
    /// </summary>
    /// <param name="m00">第 0 行第 0 列元素</param>
    /// <param name="m01">第 0 行第 1 列元素</param>
    /// <param name="m02">第 0 行第 2 列元素</param>
    /// <param name="m10">第 1 行第 0 列元素</param>
    /// <param name="m11">第 1 行第 1 列元素</param>
    /// <param name="m12">第 1 行第 2 列元素</param>
    /// <param name="m20">第 2 行第 0 列元素</param>
    /// <param name="m21">第 2 行第 1 列元素</param>
    /// <param name="m22">第 2 行第 2 列元素</param>
    public OrthogonalRotation(
        sbyte m00, sbyte m01, sbyte m02,
        sbyte m10, sbyte m11, sbyte m12,
        sbyte m20, sbyte m21, sbyte m22)
    {
        M00 = m00;
        M01 = m01;
        M02 = m02;
        M10 = m10;
        M11 = m11;
        M12 = m12;
        M20 = m20;
        M21 = m21;
        M22 = m22;

        if (!IsValid)
        {
            throw new ArgumentException("Rotation must be a proper signed permutation matrix.");
        }
    }

    /// <summary>第 0 行第 0 列元素</summary>
    public sbyte M00 { get; }
    /// <summary>第 0 行第 1 列元素</summary>
    public sbyte M01 { get; }
    /// <summary>第 0 行第 2 列元素</summary>
    public sbyte M02 { get; }
    /// <summary>第 1 行第 0 列元素</summary>
    public sbyte M10 { get; }
    /// <summary>第 1 行第 1 列元素</summary>
    public sbyte M11 { get; }
    /// <summary>第 1 行第 2 列元素</summary>
    public sbyte M12 { get; }
    /// <summary>第 2 行第 0 列元素</summary>
    public sbyte M20 { get; }
    /// <summary>第 2 行第 1 列元素</summary>
    public sbyte M21 { get; }
    /// <summary>第 2 行第 2 列元素</summary>
    public sbyte M22 { get; }

    /// <summary>
    /// 按行列读取矩阵元素，供通用的旋转合成使用
    /// </summary>
    /// <param name="row">行号 0–2</param>
    /// <param name="column">列号 0–2</param>
    public sbyte this[int row, int column] => (row * 3 + column) switch
    {
        0 => M00, 1 => M01, 2 => M02,
        3 => M10, 4 => M11, 5 => M12,
        6 => M20, 7 => M21, 8 => M22,
        _ => throw new ArgumentOutOfRangeException(nameof(row), "行号必须在 0–2 之间"),
    };

    /// <summary>
    /// 恒等旋转
    /// </summary>
    public static OrthogonalRotation Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);    /// <summary>
    /// 绕 X 轴右转 90 度
    /// </summary>
    public static OrthogonalRotation AroundX90 => new(1, 0, 0, 0, 0, -1, 0, 1, 0);

    /// <summary>
    /// 绕 Y 轴右转 90 度
    /// </summary>
    public static OrthogonalRotation AroundY90 => new(0, 0, 1, 0, 1, 0, -1, 0, 0);

    /// <summary>
    /// 绕 Z 轴右转 90 度
    /// </summary>
    public static OrthogonalRotation AroundZ90 => new(0, -1, 0, 1, 0, 0, 0, 0, 1);

    /// <summary>
    /// 行列式，合法旋转恒为 1（排除镜像）
    /// </summary>
    public int Determinant =>
        M00 * (M11 * M22 - M12 * M21)
        - M01 * (M10 * M22 - M12 * M20)
        + M02 * (M10 * M21 - M11 * M20);

    /// <summary>
    /// 是否为合法的正交带符号置换矩阵：每行每列恰有一个非零元素且行列式为 1
    /// </summary>
    public bool IsValid
    {
        get
        {
            var values = new[]
            {
                M00, M01, M02, M10, M11, M12, M20, M21, M22,
            };

            if (values.Any(value => value is < -1 or > 1))
            {
                return false;
            }

            for (var row = 0; row < 3; row++)
            {
                if (values[(row * 3)..(row * 3 + 3)].Count(value => value != 0) != 1)
                {
                    return false;
                }
            }

            for (var column = 0; column < 3; column++)
            {
                var count = 0;
                for (var row = 0; row < 3; row++)
                {
                    if (values[row * 3 + column] != 0)
                    {
                        count++;
                    }
                }

                if (count != 1)
                {
                    return false;
                }
            }

            return Determinant == 1;
        }
    }

    /// <summary>
    /// 把旋转作用到整数坐标上
    /// </summary>
    /// <param name="coordinate">待旋转的坐标</param>
    /// <returns>旋转后的整数坐标</returns>
    public VoxelCoord64 Apply(VoxelCoord64 coordinate) => new(
        M00 * coordinate.X + M01 * coordinate.Y + M02 * coordinate.Z,
        M10 * coordinate.X + M11 * coordinate.Y + M12 * coordinate.Z,
        M20 * coordinate.X + M21 * coordinate.Y + M22 * coordinate.Z);

    /// <summary>
    /// 逆旋转，正交矩阵的逆就是转置
    /// </summary>
    /// <returns>反向旋转</returns>
    public OrthogonalRotation Inverse() => new(
        M00, M10, M20,
        M01, M11, M21,
        M02, M12, M22);

    /// <summary>
    /// 复合两个旋转：先施加 right，再施加自身
    /// </summary>
    /// <param name="right">先执行的旋转</param>
    /// <returns>复合后的旋转</returns>
    public OrthogonalRotation Compose(OrthogonalRotation right)
    {
        // 旧场景或反序列化可能留下全零矩阵，直接合成会抛异常把整个编辑器带崩，这里按单位旋转兜底
        var left = IsValid ? this : Identity;
        var other = right.IsValid ? right : Identity;
        return new OrthogonalRotation(
            (sbyte)(left.M00 * other.M00 + left.M01 * other.M10 + left.M02 * other.M20),
            (sbyte)(left.M00 * other.M01 + left.M01 * other.M11 + left.M02 * other.M21),
            (sbyte)(left.M00 * other.M02 + left.M01 * other.M12 + left.M02 * other.M22),
            (sbyte)(left.M10 * other.M00 + left.M11 * other.M10 + left.M12 * other.M20),
            (sbyte)(left.M10 * other.M01 + left.M11 * other.M11 + left.M12 * other.M21),
            (sbyte)(left.M10 * other.M02 + left.M11 * other.M12 + left.M12 * other.M22),
            (sbyte)(left.M20 * other.M00 + left.M21 * other.M10 + left.M22 * other.M20),
            (sbyte)(left.M20 * other.M01 + left.M21 * other.M11 + left.M22 * other.M21),
            (sbyte)(left.M20 * other.M02 + left.M21 * other.M12 + left.M22 * other.M22));
    }

    /// <summary>
    /// 按九个元素逐一比较两个旋转
    /// </summary>
    /// <param name="other">另一个旋转</param>
    /// <returns>是否完全相同</returns>
    public bool Equals(OrthogonalRotation other) =>
        M00 == other.M00 && M01 == other.M01 && M02 == other.M02 &&
        M10 == other.M10 && M11 == other.M11 && M12 == other.M12 &&
        M20 == other.M20 && M21 == other.M21 && M22 == other.M22;

    /// <summary>
    /// 与任意对象的相等判断
    /// </summary>
    /// <param name="obj">待比较对象</param>
    /// <returns>是否为相同的合法旋转</returns>
    public override bool Equals(object? obj) => obj is OrthogonalRotation other && Equals(other);

    /// <summary>
    /// 组合九个元素的哈希码
    /// </summary>
    /// <returns>稳定哈希值</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(M00);
        hash.Add(M01);
        hash.Add(M02);
        hash.Add(M10);
        hash.Add(M11);
        hash.Add(M12);
        hash.Add(M20);
        hash.Add(M21);
        hash.Add(M22);
        return hash.ToHashCode();
    }

    /// <summary>
    /// 按行输出矩阵文本，便于日志与调试
    /// </summary>
    /// <returns>形如 [a,b,c; d,e,f; g,h,i] 的文本</returns>
    public override string ToString() =>
        $"[{M00},{M01},{M02}; {M10},{M11},{M12}; {M20},{M21},{M22}]";
}

/// <summary>
/// 体素模型的格变换：整数位置加离散旋转，没有缩放
/// </summary>
/// <param name="Position">整数格位置</param>
/// <param name="Rotation">离散旋转</param>
public readonly record struct GridTransform(VoxelCoord64 Position, OrthogonalRotation Rotation)
{
    /// <summary>
    /// 位于原点、不带旋转的默认变换
    /// </summary>
    public static GridTransform Identity => new(VoxelCoord64.Zero, OrthogonalRotation.Identity);
}

/// <summary>
/// 相机、灯光等对象的自由变换：连续双精度位置与四元数旋转，同样没有缩放
/// </summary>
/// <param name="Position">连续位置</param>
/// <param name="Rotation">连续旋转</param>
public readonly record struct FreeTransform(Double3 Position, Quaternion Rotation)
{
    /// <summary>
    /// 位于原点、单位旋转的默认变换
    /// </summary>
    public static FreeTransform Identity => new(Double3.Zero, Quaternion.Identity);
}

/// <summary>
/// 变换模式，决定对象受格对齐约束还是自由摆放
/// </summary>
public enum TransformMode
{
    /// <summary>
    /// 体素模型模式：整数位置与 90 度离散旋转
    /// </summary>
    Grid,

    /// <summary>
    /// 自由模式：连续位置与旋转，供相机灯光等使用
    /// </summary>
    Free,
}

/// <summary>
/// 场景对象共享的变换组件，体素模型用 Grid 模式，相机灯光用 Free 模式
/// 两种模式互不削弱对方的不变量：模型永远保持整数与 90 度约束
/// </summary>
public sealed class TransformComponent
{
    /// <summary>各轴整数缩放倍数。</summary>
    public float ScaleX { get; set; } = 1F;
    public float ScaleY { get; set; } = 1F;
    public float ScaleZ { get; set; } = 1F;

    private static readonly OrthogonalRotation[] VoxelRotations = BuildVoxelRotations();
    /// <summary>
    /// 用显式模式与两套值构造组件
    /// </summary>
    /// <param name="mode">生效模式</param>
    /// <param name="grid">格变换值</param>
    /// <param name="free">自由变换值</param>
    public TransformComponent(TransformMode mode, GridTransform grid, FreeTransform free)
    {
        Mode = mode;
        Grid = grid;
        Free = free;
    }

    /// <summary>
    /// 当前生效的变换模式
    /// </summary>
    public TransformMode Mode { get; private set; }

    /// <summary>
    /// 格变换值，仅 Mode 为 Grid 时具有意义
    /// </summary>
    public GridTransform Grid { get; private set; }

    /// <summary>
    /// 自由变换值，仅 Mode 为 Free 时具有意义
    /// </summary>
    public FreeTransform Free { get; private set; }

    /// <summary>
    /// 对象体素数据使用的世界原点：格模式直接用格位置，自由模式用位置向下取整
    /// 模型资产按局部坐标保存，加载与选择都必须用这个原点还原世界坐标
    /// </summary>
    public VoxelCoord64 VoxelOrigin => Mode == TransformMode.Grid
        ? Grid.Position
        : new VoxelCoord64(
            (long)Math.Floor(Free.Position.X),
            (long)Math.Floor(Free.Position.Y),
            (long)Math.Floor(Free.Position.Z));

    /// <summary>
    /// 以格变换模式创建组件
    /// </summary>
    /// <param name="transform">格变换值</param>
    /// <returns>Grid 模式的组件</returns>
    public static TransformComponent FromGrid(GridTransform transform) =>
        new(TransformMode.Grid, transform, FreeTransform.Identity);

    /// <summary>
    /// 以自由变换模式创建组件
    /// </summary>
    /// <param name="transform">自由变换值</param>
    /// <returns>Free 模式的组件</returns>
    public static TransformComponent FromFree(FreeTransform transform) =>
        new(TransformMode.Free, GridTransform.Identity, transform);

    /// <summary>
    /// 将自由变换吸附为体素模型允许的整数位置和 90 度旋转
    /// </summary>
    /// <returns>吸附后的格变换</returns>
    public GridTransform ToGrid()
    {
        var position = Mode == TransformMode.Grid
            ? Grid.Position
            : new VoxelCoord64(
                (long)Math.Round(Free.Position.X),
                (long)Math.Round(Free.Position.Y),
                (long)Math.Round(Free.Position.Z));
        if (Mode == TransformMode.Grid) return Grid;

        var rotation = Free.Rotation;
        if (rotation.LengthSquared() < 0.000001F || !float.IsFinite(rotation.LengthSquared()))
            return new GridTransform(position, OrthogonalRotation.Identity);
        rotation = Quaternion.Normalize(rotation);
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        var best = OrthogonalRotation.Identity;
        var bestScore = float.NegativeInfinity;
        foreach (var candidate in VoxelRotations)
        {
            var score = matrix.M11 * candidate.M00 + matrix.M12 * candidate.M01 + matrix.M13 * candidate.M02 +
                        matrix.M21 * candidate.M10 + matrix.M22 * candidate.M11 + matrix.M23 * candidate.M12 +
                        matrix.M31 * candidate.M20 + matrix.M32 * candidate.M21 + matrix.M33 * candidate.M22;
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return new GridTransform(position, best);
    }

    /// <summary>
    /// 读取自由变换的 XYZ 欧拉角，单位为度
    /// </summary>
    public Double3 GetEulerDegrees()
    {
        var q = Free.Rotation;
        if (q.LengthSquared() < 0.000001F || !float.IsFinite(q.LengthSquared()))
            return Double3.Zero;
        q = Quaternion.Normalize(q);
        var matrix = Matrix4x4.CreateFromQuaternion(q);
        var x = MathF.Atan2(matrix.M32, matrix.M33);
        var y = MathF.Asin(Math.Clamp(-matrix.M31, -1F, 1F));
        var z = MathF.Atan2(matrix.M21, matrix.M11);
        const float degrees = 180F / MathF.PI;
        return new Double3(-x * degrees, -y * degrees, -z * degrees);
    }

    /// <summary>
    /// 使用 XYZ 欧拉角设置自由旋转，输入单位为度
    /// </summary>
    public void SetFreeEuler(Double3 position, Double3 eulerDegrees)
    {
        const float radians = MathF.PI / 180F;
        var rotationMatrix =
            Matrix4x4.CreateRotationZ((float)eulerDegrees.Z * radians) *
            Matrix4x4.CreateRotationY((float)eulerDegrees.Y * radians) *
            Matrix4x4.CreateRotationX((float)eulerDegrees.X * radians);
        var rotation = Quaternion.CreateFromRotationMatrix(rotationMatrix);
        SetFree(new FreeTransform(position, Quaternion.Normalize(rotation)));
    }

    /// <summary>
    /// 切换为格变换模式
    /// </summary>
    /// <param name="transform">新的格变换值</param>
    public void SetGrid(GridTransform transform)
    {
        Mode = TransformMode.Grid;
        Grid = transform;
    }

    private static OrthogonalRotation[] BuildVoxelRotations()
    {
        var rotations = new List<OrthogonalRotation> { OrthogonalRotation.Identity };
        for (var index = 0; index < rotations.Count; index++)
        {
            foreach (var axis in new[] { OrthogonalRotation.AroundX90, OrthogonalRotation.AroundY90, OrthogonalRotation.AroundZ90 })
            {
                var candidate = rotations[index].Compose(axis);
                if (!rotations.Contains(candidate)) rotations.Add(candidate);
            }
        }
        return rotations.ToArray();
    }

    /// <summary>
    /// 切换为自由变换模式
    /// </summary>
    /// <param name="transform">新的自由变换值</param>
    public void SetFree(FreeTransform transform)
    {
        Mode = TransformMode.Free;
        Free = transform;
    }

    /// <summary>
    /// 层级约束：格变换父级只能携带自由变换子级以外的组合按模式规则判定
    /// 该规则防止自由物体（相机）成为体素模型的父级后破坏整数对齐
    /// </summary>
    /// <param name="child">候选子对象的变换组件</param>
    /// <returns>是否允许作为其父级</returns>
    public bool CanBeParentOf(TransformComponent child) =>
        Mode == TransformMode.Grid || child.Mode == TransformMode.Free;
}
