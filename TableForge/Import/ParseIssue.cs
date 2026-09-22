namespace TableForge.Import;

public enum ParseIssueSeverity { Info, Warning, Error }

public enum ParseIssueCode
{
    NoTableName,
    NoDiceExpression,
    UnsupportedDice,
    ContinuationJoined,
    UnrecognizedLine,
    EmptyEntryText,
    NoEntries,

    /// <summary>A line was taken as a result set heading without a blank line or other strong separation.</summary>
    ProbableResultSetHeading,

    /// <summary>A line looks like a result set heading but the numbering does not restart, so no new set was started.</summary>
    AmbiguousSectionBreak,

    /// <summary>A line (or block of lines) with side-by-side columns was split into separate entries.</summary>
    SideBySideSplit,

    /// <summary>A line seems to hold more than one range/result pair but could not be split safely.</summary>
    MultipleRangesOnLine,

    /// <summary>Rows appeared before the first heading and were placed in an unnamed result set.</summary>
    UnnamedResultSet,

    /// <summary>Row numbers stood on their own lines, so the paragraph after each number was attached to it.</summary>
    ParagraphsAttached,

    /// <summary>A row that began as a lone number took more than one paragraph (a page header or footer may have slipped in).</summary>
    MultipleParagraphs,

    /// <summary>A line starting with a number was kept as paragraph text because it did not continue the numbering.</summary>
    NumberedLineKeptAsText,

    /// <summary>The heading was printed once per column ("D100 SYLLABLE D100 SYLLABLE") and was read as a single heading.</summary>
    HeadingRepeated,

    /// <summary>The heading holds several dice expressions or headings that do not repeat, so it was left as one heading.</summary>
    AmbiguousHeading,

    /// <summary>The heading's words were read as separate output columns that share one roll, creating several result sets.</summary>
    ParallelOutputsSplit,
}

/// <summary>What part of the draft an issue is about, so the Review UI can highlight it.</summary>
public enum ParseIssueTarget { Source, TableName, Dice, ResultSet, Entry }

/// <param name="ResultSetIndex">Set when the issue targets a result set or an entry.</param>
/// <param name="EntryIndex">Zero-based entry position within its result set, when the issue targets an entry.</param>
/// <param name="SourceLine">One-based line in the pasted text that triggered the issue, if any.</param>
public sealed record ParseIssue(
    ParseIssueCode Code,
    ParseIssueSeverity Severity,
    ParseIssueTarget Target,
    string Message,
    int? ResultSetIndex = null,
    int? EntryIndex = null,
    int? SourceLine = null);
