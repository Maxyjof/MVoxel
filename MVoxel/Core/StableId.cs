namespace MVoxel.Core;

/// <summary>
/// Stable identity used by scenes, assets and components. It is deliberately
/// independent from an object's display name or its position in a hierarchy.
/// </summary>
public readonly record struct StableId(Guid Value)
{
    public static StableId Empty => new(Guid.Empty);

    public static StableId New() => new(Guid.NewGuid());

    public static StableId Parse(string text) => new(Guid.Parse(text));

    public static bool TryParse(string? text, out StableId id)
    {
        if (Guid.TryParse(text, out var value))
        {
            id = new StableId(value);
            return true;
        }

        id = Empty;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}
