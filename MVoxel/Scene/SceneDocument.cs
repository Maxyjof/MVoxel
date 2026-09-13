using System.Text.Json;
using MVoxel.Core;

namespace MVoxel.Scene;

public sealed class SceneDocument
{
    public int FormatVersion { get; set; } = 1;
    public StableId SceneId { get; set; } = StableId.New();
    public string Name { get; set; } = "Untitled Scene";
    public List<SceneObject> Objects { get; set; } = new();
}

public sealed class SceneObject
{
    public StableId Id { get; set; } = StableId.New();
    public string Name { get; set; } = "Object";
    public StableId? ParentId { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public TransformComponent Transform { get; set; } = TransformComponent.FromGrid(GridTransform.Identity);
    public List<ComponentRecord> Components { get; set; } = new();
}

public sealed class ComponentRecord
{
    public string TypeId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public JsonElement Data { get; set; }
}

public sealed class SceneGraph
{
    private readonly SceneDocument _document;

    public SceneGraph(SceneDocument document)
    {
        _document = document;
    }

    public IReadOnlyList<SceneObject> Objects => _document.Objects;

    public SceneObject Add(string name, StableId? parentId = null, TransformComponent? transform = null)
    {
        if (parentId is not null && Find(parentId.Value) is null)
        {
            throw new InvalidOperationException($"Parent does not exist: {parentId.Value}");
        }

        var sceneObject = new SceneObject
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Object" : name,
            ParentId = parentId,
            Transform = transform ?? TransformComponent.FromGrid(GridTransform.Identity),
        };
        _document.Objects.Add(sceneObject);
        return sceneObject;
    }

    public SceneObject? Find(StableId id) => _document.Objects.FirstOrDefault(item => item.Id == id);

    public void SetParent(StableId childId, StableId? parentId)
    {
        var child = Find(childId) ?? throw new KeyNotFoundException($"Object does not exist: {childId}");
        if (parentId == childId)
        {
            throw new InvalidOperationException("An object cannot be its own parent.");
        }

        SceneObject? parent = null;
        if (parentId is not null)
        {
            parent = Find(parentId.Value) ?? throw new KeyNotFoundException($"Parent does not exist: {parentId}");
            if (!parent.Transform.CanBeParentOf(child.Transform))
            {
                throw new InvalidOperationException("Grid transform objects can only contain free-transform children.");
            }

            for (var current = parent; current is not null; current = current.ParentId is null ? null : Find(current.ParentId.Value))
            {
                if (current.Id == childId)
                {
                    throw new InvalidOperationException("Parenting would create a hierarchy cycle.");
                }
            }
        }

        child.ParentId = parentId;
    }
}

public static class SceneSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void Save(string path, SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        AtomicFile.WriteText(path, JsonSerializer.Serialize(scene, Options));
    }

    public static SceneDocument Load(string path)
    {
        var scene = JsonSerializer.Deserialize<SceneDocument>(File.ReadAllText(path), Options);
        return scene ?? throw new InvalidDataException($"Invalid scene: {path}");
    }
}
