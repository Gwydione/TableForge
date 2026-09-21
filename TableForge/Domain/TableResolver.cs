namespace TableForge.Domain;

public enum ResolutionStatus { Matched, NoMatch, Ambiguous }

/// <summary>Outcome of one numeric roll against one result set.</summary>
public sealed record ResultSetResolution(ResultSet ResultSet, IReadOnlyList<TableEntry> Matches)
{
    public ResolutionStatus Status => Matches.Count switch
    {
        0 => ResolutionStatus.NoMatch,
        1 => ResolutionStatus.Matched,
        _ => ResolutionStatus.Ambiguous,
    };

    /// <summary>The single matching entry; null for no-match or ambiguous (overlapping) results.</summary>
    public TableEntry? Entry => Matches.Count == 1 ? Matches[0] : null;
}

public sealed record TableResolution(RollableTable Table, int Roll, IReadOnlyList<ResultSetResolution> Results);

public static class TableResolver
{
    /// <summary>
    /// Resolves one numeric result independently against every result set.
    /// Never throws for uncovered values and never guesses: no match is reported as no match,
    /// and overlapping entries are reported as ambiguous rather than picking one.
    /// </summary>
    public static TableResolution Resolve(RollableTable table, int roll)
    {
        var results = table.ResultSets
            .Select(set => new ResultSetResolution(set, set.Entries.Where(e => e.Covers(roll)).ToList()))
            .ToList();
        return new TableResolution(table, roll, results);
    }
}
