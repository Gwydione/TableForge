using TableForge.Domain;

namespace TableForge.ViewModels;

/// <summary>
/// Where a Roll screen reads which of a table's Unique rows have been used, records the ones an accepted result uses up
/// (see <see cref="UniqueResults"/>), and resets them. Normally the database's used results; <see cref="InMemory"/> otherwise.
/// Any of them may throw.
/// </summary>
public sealed class UsedResultsStore(Func<long, IReadOnlySet<Guid>> get, Action<long, IReadOnlyList<Guid>> markUsed, Func<long, int>? reset = null)
{
    public IReadOnlySet<Guid> Get(long tableId) => get(tableId);

    public void MarkUsed(long tableId, IReadOnlyList<Guid> rowKeys) => markUsed(tableId, rowKeys);

    /// <summary>Forgets every used result of the table (never which rows are Unique); how many there were.</summary>
    public int Reset(long tableId) => reset is null ? throw new InvalidOperationException("Used results cannot be reset here.") : reset(tableId);

    /// <summary>Used results kept only as long as this store lives: for a Roll screen with no database behind it.</summary>
    public static UsedResultsStore InMemory()
    {
        var used = new Dictionary<long, HashSet<Guid>>();
        return new(
            tableId => used.TryGetValue(tableId, out var keys) ? new HashSet<Guid>(keys) : new HashSet<Guid>(),
            (tableId, rowKeys) =>
            {
                if (!used.TryGetValue(tableId, out var keys)) used[tableId] = keys = [];
                keys.UnionWith(rowKeys);
            },
            tableId => used.Remove(tableId, out var keys) ? keys.Count : 0);
    }
}
