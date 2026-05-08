namespace Logs_MCP;

internal sealed record LogSearchCriteria
{
    public LogSearchCriteria(
        string? searchKeyword,
        string? phrase,
        string? logLevel,
        string? correlationId,
        string? exceptionType,
        bool exactMatch,
        bool recentErrors)
    {
        SearchKeyword = Normalize(searchKeyword);
        Phrase = Normalize(phrase);
        LogLevel = Normalize(logLevel);
        CorrelationId = Normalize(correlationId);
        ExceptionType = Normalize(exceptionType);
        ExactMatch = exactMatch;
        RecentErrors = recentErrors;
    }

    public string? SearchKeyword { get; }
    public string? Phrase { get; }
    public string? LogLevel { get; }
    public string? CorrelationId { get; }
    public string? ExceptionType { get; }
    public bool ExactMatch { get; }
    public bool RecentErrors { get; }

    public bool HasAnyContentFilter =>
        SearchKeyword is not null ||
        Phrase is not null ||
        LogLevel is not null ||
        CorrelationId is not null ||
        ExceptionType is not null ||
        RecentErrors;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record LogSearchHit(
    string HitId,
    string Server,
    string FilePath,
    string FileName,
    int LineNumber,
    string DisplayLine,
    DateTimeOffset? Timestamp,
    DateTimeOffset? FallbackTimestamp,
    string? Message,
    string? ExceptionType);

internal sealed class NodeSearchStatus
{
    public required string Server { get; init; }
    public required string BasePath { get; init; }
    public bool ShareReachable { get; set; }
    public int AppFolderCount { get; set; }
    public int CandidateLogFileCount { get; set; }
    public List<string> AvailableFolders { get; } = [];
    public string? ErrorMessage { get; set; }
}

internal sealed record ParsedLogEntry(
    string RawLine,
    string? Message,
    string? LogLevel,
    string? CorrelationId,
    string? ExceptionType,
    DateTimeOffset? Timestamp,
    bool IsJson);

internal sealed record ContextRange(
    int StartLine,
    int EndLine,
    IReadOnlyList<int> TargetLines);

internal sealed record ContextLine(
    int LineNumber,
    string Line,
    bool IsTarget);

internal sealed record ContextBlock(
    IReadOnlyList<ContextLine> Lines,
    bool ContainsAnyTarget);
