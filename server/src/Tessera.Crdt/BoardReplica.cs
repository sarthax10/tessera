using System.Security.Cryptography;
using System.Text;

namespace Tessera.Crdt;

/// <summary>
/// One replica of a board: a flat map of (shape, property) to last-writer-wins register, with
/// add-wins deletion. Shapes are projected over that map rather than owning merge logic, which is
/// what lets concurrent edits to different properties of one shape both survive.
/// </summary>
/// <remarks>
/// Not thread-safe. On the server each board is owned by a single room actor; in the browser there
/// is one thread.
/// </remarks>
public sealed class BoardReplica
{
    private sealed class Register
    {
        public PropertyValue Value;
        public Hlc At;
    }

    private sealed class ShapeState
    {
        public readonly Dictionary<string, Register> Properties = [];
        public Hlc? DeletedAt;
        public Hlc LastWrite = Hlc.MinValue;
    }

    private readonly Dictionary<ShapeId, ShapeState> _shapes = [];

    /// <summary>Returns false if the operation changed nothing.</summary>
    public bool Apply(Operation operation) => operation switch
    {
        SetProperty set => ApplySet(set),
        DeleteShape delete => ApplyDelete(delete),
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation), operation.GetType().Name, "Unknown operation type."),
    };

    public void ApplyAll(IEnumerable<Operation> operations)
    {
        foreach (var operation in operations) Apply(operation);
    }

    private bool ApplySet(SetProperty set)
    {
        var state = GetOrCreate(set.Shape);

        if (state.Properties.TryGetValue(set.Property, out var register))
        {
            // Strictly greater, so replaying an operation against itself is a no-op.
            if (set.At <= register.At) return false;

            register.Value = set.Value;
            register.At = set.At;
        }
        else
        {
            state.Properties[set.Property] = new Register { Value = set.Value, At = set.At };
        }

        if (set.At > state.LastWrite) state.LastWrite = set.At;
        return true;
    }

    private bool ApplyDelete(DeleteShape delete)
    {
        var state = GetOrCreate(delete.Shape);

        if (state.DeletedAt is { } existing && delete.At <= existing) return false;

        state.DeletedAt = delete.At;
        return true;
    }

    private ShapeState GetOrCreate(ShapeId id)
    {
        if (_shapes.TryGetValue(id, out var state)) return state;

        // A delete can arrive before the writes that create the shape, so the tombstone is kept
        // either way and those writes are resolved against it rather than resurrecting the shape.
        state = new ShapeState();
        _shapes[id] = state;
        return state;
    }

    /// <summary>
    /// Deletion is add-wins: a write concurrent with a delete keeps the shape. A shape that wrongly
    /// survives costs one keystroke; one wrongly removed is work the user may never get back.
    /// </summary>
    private static bool IsVisible(ShapeState state) =>
        state.Properties.Count > 0
        && (state.DeletedAt is not { } deleted || state.LastWrite > deleted);

    public bool Contains(ShapeId id) =>
        _shapes.TryGetValue(id, out var state) && IsVisible(state);

    public Shape? Get(ShapeId id) =>
        _shapes.TryGetValue(id, out var state) && IsVisible(state) ? Project(id, state) : null;

    /// <summary>
    /// Visible shapes in z-order. Ties on the index are broken by id so every replica draws
    /// overlapping shapes in the same order.
    /// </summary>
    public IReadOnlyList<Shape> Shapes =>
        _shapes.Where(pair => IsVisible(pair.Value))
               .Select(pair => Project(pair.Key, pair.Value))
               .OrderBy(shape => shape.Index, StringComparer.Ordinal)
               .ThenBy(shape => shape.Id)
               .ToList();

    private static Shape Project(ShapeId id, ShapeState state) =>
        new(id, state.Properties.ToDictionary(p => p.Key, p => p.Value.Value));

    /// <summary>
    /// Digest of the visible document, for asserting two replicas converged. Comparing operation
    /// logs would prove nothing: differing logs with identical state is the expected outcome.
    /// </summary>
    public string StateHash()
    {
        var writer = new StringBuilder();

        foreach (var shape in Shapes)
        {
            writer.Append(shape.Id.Value).Append('{');

            foreach (var property in shape.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                writer.Append(property.Key).Append('=').Append(property.Value).Append(';');

            writer.Append("}\n");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(writer.ToString())));
    }
}
