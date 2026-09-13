using System.Numerics;

namespace MVoxel.Core;

/// <summary>Integer coordinates in the sparse voxel world.</summary>
public readonly record struct VoxelCoord64(long X, long Y, long Z)
{
    public static VoxelCoord64 Zero => new(0, 0, 0);

    public static VoxelCoord64 operator +(VoxelCoord64 left, VoxelCoord64 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static VoxelCoord64 operator -(VoxelCoord64 left, VoxelCoord64 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
}

public readonly record struct Double3(double X, double Y, double Z)
{
    public static Double3 Zero => new(0, 0, 0);
}

/// <summary>
/// A signed permutation matrix. It represents the only rotations allowed for
/// voxel models: identity and 90-degree turns around the three grid axes.
/// </summary>
public readonly struct OrthogonalRotation : IEquatable<OrthogonalRotation>
{
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

    public sbyte M00 { get; }
    public sbyte M01 { get; }
    public sbyte M02 { get; }
    public sbyte M10 { get; }
    public sbyte M11 { get; }
    public sbyte M12 { get; }
    public sbyte M20 { get; }
    public sbyte M21 { get; }
    public sbyte M22 { get; }

    public static OrthogonalRotation Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static OrthogonalRotation AroundX90 => new(1, 0, 0, 0, 0, -1, 0, 1, 0);

    public static OrthogonalRotation AroundY90 => new(0, 0, 1, 0, 1, 0, -1, 0, 0);

    public static OrthogonalRotation AroundZ90 => new(0, -1, 0, 1, 0, 0, 0, 0, 1);

    public int Determinant =>
        M00 * (M11 * M22 - M12 * M21)
        - M01 * (M10 * M22 - M12 * M20)
        + M02 * (M10 * M21 - M11 * M20);

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

    public VoxelCoord64 Apply(VoxelCoord64 coordinate) => new(
        M00 * coordinate.X + M01 * coordinate.Y + M02 * coordinate.Z,
        M10 * coordinate.X + M11 * coordinate.Y + M12 * coordinate.Z,
        M20 * coordinate.X + M21 * coordinate.Y + M22 * coordinate.Z);

    public OrthogonalRotation Inverse() => new(
        M00, M10, M20,
        M01, M11, M21,
        M02, M12, M22);

    public OrthogonalRotation Compose(OrthogonalRotation right) => new(
        (sbyte)(M00 * right.M00 + M01 * right.M10 + M02 * right.M20),
        (sbyte)(M00 * right.M01 + M01 * right.M11 + M02 * right.M21),
        (sbyte)(M00 * right.M02 + M01 * right.M12 + M02 * right.M22),
        (sbyte)(M10 * right.M00 + M11 * right.M10 + M12 * right.M20),
        (sbyte)(M10 * right.M01 + M11 * right.M11 + M12 * right.M21),
        (sbyte)(M10 * right.M02 + M11 * right.M12 + M12 * right.M22),
        (sbyte)(M20 * right.M00 + M21 * right.M10 + M22 * right.M20),
        (sbyte)(M20 * right.M01 + M21 * right.M11 + M22 * right.M21),
        (sbyte)(M20 * right.M02 + M21 * right.M12 + M22 * right.M22));

    public bool Equals(OrthogonalRotation other) =>
        M00 == other.M00 && M01 == other.M01 && M02 == other.M02 &&
        M10 == other.M10 && M11 == other.M11 && M12 == other.M12 &&
        M20 == other.M20 && M21 == other.M21 && M22 == other.M22;

    public override bool Equals(object? obj) => obj is OrthogonalRotation other && Equals(other);

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

    public override string ToString() =>
        $"[{M00},{M01},{M02}; {M10},{M11},{M12}; {M20},{M21},{M22}]";
}

public readonly record struct GridTransform(VoxelCoord64 Position, OrthogonalRotation Rotation)
{
    public static GridTransform Identity => new(VoxelCoord64.Zero, OrthogonalRotation.Identity);
}

public readonly record struct FreeTransform(Double3 Position, Quaternion Rotation)
{
    public static FreeTransform Identity => new(Double3.Zero, Quaternion.Identity);
}

public enum TransformMode
{
    Grid,
    Free,
}

/// <summary>
/// Shared transform component. Voxel models use Grid mode; cameras, lights
/// and other editor/runtime objects may use Free mode without weakening the
/// model's integer/90-degree invariants.
/// </summary>
public sealed class TransformComponent
{
    public TransformComponent(TransformMode mode, GridTransform grid, FreeTransform free)
    {
        Mode = mode;
        Grid = grid;
        Free = free;
    }

    public TransformMode Mode { get; private set; }
    public GridTransform Grid { get; private set; }
    public FreeTransform Free { get; private set; }

    public static TransformComponent FromGrid(GridTransform transform) =>
        new(TransformMode.Grid, transform, FreeTransform.Identity);

    public static TransformComponent FromFree(FreeTransform transform) =>
        new(TransformMode.Free, GridTransform.Identity, transform);

    public void SetGrid(GridTransform transform)
    {
        Mode = TransformMode.Grid;
        Grid = transform;
    }

    public void SetFree(FreeTransform transform)
    {
        Mode = TransformMode.Free;
        Free = transform;
    }

    public bool CanBeParentOf(TransformComponent child) =>
        Mode == TransformMode.Grid || child.Mode == TransformMode.Free;
}
