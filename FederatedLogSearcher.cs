using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Logs_MCP;

/// <summary>
/// All file-system access, date filtering, concurrent search, and
/// output formatting for <c>search_federated_logs</c>.
/// </summary>
internal static class FederatedLogSearcher
{
    // ------------------------------------------------------------------ //
    //  Public entry point                                                 //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Searches logs across every node mapped to <paramref name="serverName"/>
    /// concurrently and returns a single formatted result string.
    /// </summary>
    public static async Task<string> SearchAsync(
        string serverName,
        string targetApp,
        LogSearchCriteria criteria,
        DateOnly startDate,
        DateOnly endDate,
        int maxResults,
        int offset,
        int pageSize,
        LogSearchOptions options,
        CancellationToken ct = default)
    {
        var (hits, statuses, errorMessage) = await CollectHitsAsync(
            serverName,
            targetApp,
            criteria,
            startDate,
            endDate,
            maxResults,
            options,
            ct);

        if (errorMessage is not null)
            return errorMessage;

        if (hits.Length == 0)
            return BuildNoMatchResponse(serverName, targetApp, criteria, startDate, endDate, statuses);

        return BuildSuccessResponse(hits, criteria, targetApp, serverName, startDate, endDate, maxResults, offset, pageSize);
    }

    public static async Task<string> GetLogContextAsync(
        string hitId,
        int before,
        int after,
        LogSearchOptions options,
        CancellationToken ct = default)
    {
        if (!TryDecodeHitId(hitId, out string? filePath, out int lineNumber))
            return $"ERROR: Invalid hit_id '{hitId}'.";

        if (!File.Exists(filePath))
            return $"ERROR: File not found for hit_id '{hitId}': {filePath}";

        ContextRange range = new(Math.Max(1, lineNumber - before), lineNumber + after, [lineNumber]);
        ContextBlock block = await ReadContextBlockAsync(filePath, range, options, ct);

        if (!block.ContainsAnyTarget)
            return $"ERROR: line_number {lineNumber} was not found in '{filePath}'.";

        return FormatContextBlock("Log context:", hitId, filePath, before, after, [range], block.Lines);
    }

    public static async Task<string> GetCorrelationContextAsync(
        string serverName,
        string targetApp,
        string correlationId,
        DateOnly startDate,
        DateOnly endDate,
        int maxHits,
        int before,
        int after,
        LogSearchOptions options,
        CancellationToken ct = default)
    {
        var criteria = new LogSearchCriteria(
            searchKeyword: null,
            phrase: null,
            logLevel: null,
            correlationId: correlationId,
            exceptionType: null,
            exactMatch: false,
            recentErrors: false);

        var (hits, statuses, errorMessage) = await CollectHitsAsync(
            serverName,
            targetApp,
            criteria,
            startDate,
            endDate,
            maxHits,
            options,
            ct);

        if (errorMessage is not null)
            return errorMessage;

        if (hits.Length == 0)
            return BuildNoMatchResponse(serverName, targetApp, criteria, startDate, endDate, statuses);

        var orderedHits = hits
            .OrderBy(h => h.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.LineNumber)
            .Take(maxHits)
            .ToArray();

        var contextBlocks = new List<string>();
        foreach (var fileGroup in orderedHits.GroupBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            ContextRange[] mergedRanges = MergeContextRanges(fileGroup.Select(hit =>
                new ContextRange(
                    Math.Max(1, hit.LineNumber - before),
                    hit.LineNumber + after,
                    [hit.LineNumber])));

            foreach (ContextRange range in mergedRanges)
            {
                ContextBlock block = await ReadContextBlockAsync(fileGroup.Key, range, options, ct);
                if (!block.ContainsAnyTarget)
                    continue;

                contextBlocks.Add(FormatContextBlock(
                    "Correlation context:",
                    hitId: null,
                    fileGroup.Key,
                    before,
                    after,
                    [range],
                    block.Lines));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("Correlation context summary:");
        sb.AppendLine($"- server_name: {serverName}");
        sb.AppendLine($"- target_app: {targetApp}");
        sb.AppendLine($"- correlation_id: {correlationId}");
        sb.AppendLine($"- date_range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        sb.AppendLine($"- hits_collected: {hits.Length}");
        sb.AppendLine($"- context_blocks_returned: {contextBlocks.Count}");
        sb.AppendLine($"- before: {before}");
        sb.AppendLine($"- after: {after}");
        sb.AppendLine();
        sb.Append(string.Join($"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}", contextBlocks));
        return sb.ToString().TrimEnd();
    }

    public static async Task<string> ListAppFoldersAsync(
        string serverName,
        string? filter,
        int maxResults,
        LogSearchOptions options,
        CancellationToken ct = default)
    {
        if (!TryResolveServerMapping(serverName, options,
                                     out string[]? servers,
                                     out string? errorMessage))
        {
            return errorMessage!;
        }

        var discoveredFolders = new ConcurrentBag<(string Server, string Folder)>();
        var statusMessages = new ConcurrentBag<string>();

        await Task.WhenAll(servers.Select(async server =>
        {
            string basePath = $@"\\{server}\{options.LogShare}";

            try
            {
                if (!Directory.Exists(basePath))
                {
                    statusMessages.Add($"- {server}: share not reachable ({basePath})");
                    return;
                }

                foreach (string folder in Directory.EnumerateDirectories(basePath))
                {
                    ct.ThrowIfCancellationRequested();
                    string? name = Path.GetFileName(folder);
                    if (!string.IsNullOrWhiteSpace(name))
                        discoveredFolders.Add((server, name));
                }
            }
            catch (Exception ex)
            {
                statusMessages.Add($"- {server}: {ex.Message}");
            }
        }));

        string[] distinctFolders = FilterFolderNames(discoveredFolders.Select(x => x.Folder), null, int.MaxValue);
        string[] folders = FilterFolderNames(discoveredFolders.Select(x => x.Folder), filter, maxResults);

        if (folders.Length == 0)
        {
            var noMatch = new StringBuilder();
            noMatch.AppendLine($"No app folders found for server_name '{serverName}'{(string.IsNullOrWhiteSpace(filter) ? "." : $" matching filter '{filter}'.")}");
            noMatch.AppendLine($"- total_discovered_folders: {distinctFolders.Length}");

            string suggestions = string.Join(", ", RankFolderSuggestions(distinctFolders, filter).Take(5));
            if (!string.IsNullOrWhiteSpace(suggestions))
                noMatch.AppendLine($"Suggested folders: {suggestions}");

            if (!statusMessages.IsEmpty)
            {
                noMatch.AppendLine("Warnings:");
                noMatch.Append(string.Join(Environment.NewLine, statusMessages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
            }

            return noMatch.ToString().TrimEnd();
        }

        var folderServerMap = discoveredFolders
            .Where(x => folders.Contains(x.Folder, StringComparer.OrdinalIgnoreCase))
            .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("App folder discovery:");
        sb.AppendLine($"- server_name: {serverName}");
        sb.AppendLine($"- filter: {(string.IsNullOrWhiteSpace(filter) ? "none" : filter.Trim())}");
        sb.AppendLine($"- total_discovered_folders: {distinctFolders.Length}");
        sb.AppendLine($"- folders_returned: {folderServerMap.Length}");
        sb.AppendLine($"- max_results: {maxResults}");
        sb.AppendLine("Folders:");

        foreach (var group in folderServerMap)
        {
            string hosts = string.Join(", ", group.Select(x => x.Server).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            sb.AppendLine($"- {group.Key} [servers: {hosts}]");
        }

        if (!statusMessages.IsEmpty)
        {
            sb.AppendLine("Warnings:");
            foreach (string message in statusMessages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(message);
        }

        return sb.ToString().TrimEnd();
    }

    internal static ContextRange[] MergeContextRanges(IEnumerable<ContextRange> ranges)
    {
        List<ContextRange> ordered = ranges
            .OrderBy(r => r.StartLine)
            .ThenBy(r => r.EndLine)
            .ToList();

        if (ordered.Count == 0)
            return [];

        var merged = new List<ContextRange> { ordered[0] };

        for (int i = 1; i < ordered.Count; i++)
        {
            ContextRange current = ordered[i];
            ContextRange previous = merged[^1];

            if (current.StartLine <= previous.EndLine + 1)
            {
                var combinedTargets = previous.TargetLines
                    .Union(current.TargetLines)
                    .OrderBy(x => x)
                    .ToArray();

                merged[^1] = new ContextRange(
                    previous.StartLine,
                    Math.Max(previous.EndLine, current.EndLine),
                    combinedTargets);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged.ToArray();
    }

    private static async Task<ContextBlock> ReadContextBlockAsync(
        string filePath,
        ContextRange range,
        LogSearchOptions options,
        CancellationToken ct)
    {
        var lines = new List<ContextLine>();
        int currentLineNumber = 0;

        await foreach (string line in ReadLinesAsync(filePath, ct))
        {
            currentLineNumber++;

            if (currentLineNumber < range.StartLine)
                continue;

            if (currentLineNumber > range.EndLine)
                break;

            lines.Add(new ContextLine(
                currentLineNumber,
                FormatLine(line, options),
                range.TargetLines.Contains(currentLineNumber)));
        }

        return new ContextBlock(lines, lines.Any(l => l.IsTarget));
    }

    private static string FormatContextBlock(
        string title,
        string? hitId,
        string filePath,
        int before,
        int after,
        IReadOnlyList<ContextRange> ranges,
        IReadOnlyList<ContextLine> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        if (!string.IsNullOrWhiteSpace(hitId))
            sb.AppendLine($"- hit_id: {hitId}");
        sb.AppendLine($"- file_path: {filePath}");
        sb.AppendLine($"- target_lines: {string.Join(", ", ranges.SelectMany(r => r.TargetLines).Distinct().OrderBy(x => x))}");
        sb.AppendLine($"- range_start: {ranges.Min(r => r.StartLine)}");
        sb.AppendLine($"- range_end: {ranges.Max(r => r.EndLine)}");
        sb.AppendLine($"- before: {before}");
        sb.AppendLine($"- after: {after}");
        sb.AppendLine("Lines:");

        foreach (ContextLine line in lines)
        {
            string prefix = line.IsTarget ? ">" : " ";
            sb.AppendLine($"{prefix} {line.LineNumber}: {line.Line}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryResolveServerMapping(
        string serverName,
        LogSearchOptions options,
        out string[]? servers,
        out string? errorMessage)
    {
        errorMessage = null;

        if (options.AppServerMap.TryGetValue(serverName, out servers))
            return true;

        errorMessage = $"ERROR: No server mapping found for server_name '{serverName}'. " +
                       $"Known server names: {string.Join(", ", options.AppServerMap.Keys)}";
        return false;
    }

    private static async Task<(LogSearchHit[] Hits, NodeSearchStatus[] Statuses, string? ErrorMessage)> CollectHitsAsync(
        string serverName,
        string targetApp,
        LogSearchCriteria criteria,
        DateOnly startDate,
        DateOnly endDate,
        int maxResults,
        LogSearchOptions options,
        CancellationToken ct)
    {
        if (!TryResolveServerMapping(serverName, options,
                                     out string[]? servers,
                                     out string? errorMessage))
        {
            return ([], [], errorMessage);
        }

        var hits = new ConcurrentBag<LogSearchHit>();
        var statuses = new ConcurrentBag<NodeSearchStatus>();
        int[] matchCount = [0];

        var nodeTasks = servers.Select(server =>
            SearchNodeAsync(server, targetApp, criteria,
                            startDate, endDate, maxResults,
                            hits, statuses, matchCount, options, ct));

        await Task.WhenAll(nodeTasks);

        return (hits.ToArray(), statuses.ToArray(), null);
    }

    // ------------------------------------------------------------------ //
    //  Per-node search                                                    //
    // ------------------------------------------------------------------ //

    private static async Task SearchNodeAsync(
        string server,
        string targetApp,
        LogSearchCriteria criteria,
        DateOnly start,
        DateOnly end,
        int maxResults,
        ConcurrentBag<LogSearchHit> hits,
        ConcurrentBag<NodeSearchStatus> statuses,
        int[] matchCount,
        LogSearchOptions options,
        CancellationToken ct)
    {
        string basePath = $@"\\{server}\{options.LogShare}";
        var status = new NodeSearchStatus
        {
            Server = server,
            BasePath = basePath,
            ShareReachable = false
        };

        try
        {
            if (!Directory.Exists(basePath))
            {
                status.ErrorMessage = $"Share not reachable: {basePath}";
                statuses.Add(status);
                return;
            }

            status.ShareReachable = true;

            var topLevelFolders = Directory.EnumerateDirectories(basePath).ToArray();
            status.AvailableFolders.AddRange(topLevelFolders.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name))!);
            var appFolders = GetCandidateAppFolders(basePath, targetApp);
            status.AppFolderCount = appFolders.Length;

            if (appFolders.Length == 0)
            {
                statuses.Add(status);
                return;
            }

            var startDt = start.ToDateTime(TimeOnly.MinValue);
            var endDt   = end.ToDateTime(TimeOnly.MaxValue);

            var logFiles = appFolders
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    bool isLog  = name.Contains(".log", StringComparison.OrdinalIgnoreCase);
                    if (!isLog) return false;

                    var lwt = File.GetLastWriteTime(f);
                    return lwt >= startDt && lwt <= endDt;
                })
                .ToArray();

            status.CandidateLogFileCount = logFiles.Length;

            foreach (string filePath in logFiles)
            {
                if (Volatile.Read(ref matchCount[0]) >= maxResults) break;
                ct.ThrowIfCancellationRequested();

                await SearchFileAsync(server, filePath, criteria,
                                      maxResults, hits, matchCount, options, ct);
            }

            statuses.Add(status);
        }
        catch (OperationCanceledException) { /* respect cancellation */ }
        catch (Exception ex)
        {
            status.ErrorMessage = ex.Message;
            statuses.Add(status);
        }
    }

    internal static string[] GetCandidateAppFolders(string basePath, string targetApp)
    {
        var topLevelFolders = Directory.EnumerateDirectories(basePath).ToArray();

        return topLevelFolders
            .Where(d => Path.GetFileName(d).Contains(targetApp, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    // ------------------------------------------------------------------ //
    //  Per-file line-by-line search                                       //
    // ------------------------------------------------------------------ //

    private static async Task SearchFileAsync(
        string server,
        string filePath,
        LogSearchCriteria criteria,
        int maxResults,
        ConcurrentBag<LogSearchHit> hits,
        int[] matchCount,
        LogSearchOptions options,
        CancellationToken ct)
    {
        string fileName = Path.GetFileName(filePath);
        DateTimeOffset fileTimestamp = new(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero);
        int lineNumber = 0;

        try
        {
            await foreach (string rawLine in ReadLinesAsync(filePath, ct))
            {
                lineNumber++;

                if (Volatile.Read(ref matchCount[0]) >= maxResults) return;

                ParsedLogEntry entry = ParseLogEntry(rawLine);

                if (!IsMatch(entry, criteria))
                    continue;

                string formatted = FormatLine(rawLine, options);
                hits.Add(new LogSearchHit(
                    CreateHitId(filePath, lineNumber),
                    server,
                    filePath,
                    fileName,
                    lineNumber,
                    $"[HitId: {CreateHitId(filePath, lineNumber)}] [{server}] [File: {fileName}] [Line: {lineNumber}] {formatted}",
                    entry.Timestamp,
                    fileTimestamp,
                    entry.Message ?? rawLine,
                    entry.ExceptionType));
                Interlocked.Increment(ref matchCount[0]);
            }
        }
        catch (Exception)
        {
        }
    }

    internal static ParsedLogEntry ParseLogEntry(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                string? timestamp = TryGetString(root, "timestamp", "@timestamp", "time", "ts");
                return new ParsedLogEntry(
                    line,
                    TryGetString(root, "message", "msg", "Message", "text") ?? line,
                    TryGetString(root, "level", "logLevel", "LogLevel", "Level", "severity", "Severity"),
                    TryGetString(root, "correlationId", "CorrelationId", "correlation_id", "traceId", "TraceId", "requestId", "RequestId"),
                    TryGetString(root, "exceptionType", "ExceptionType", "errorType", "ErrorType", "exception", "Exception"),
                    TryParseTimestamp(timestamp),
                    true);
            }
            catch (JsonException)
            {
            }
        }

        return new ParsedLogEntry(
            line,
            line,
            InferLogLevel(line),
            null,
            InferExceptionType(line),
            TryParseTimestampFromText(line),
            false);
    }

    internal static bool IsMatch(ParsedLogEntry entry, LogSearchCriteria criteria)
    {
        if (criteria.RecentErrors && !LooksLikeError(entry))
            return false;

        if (criteria.LogLevel is not null && !MatchesLogLevel(entry, criteria.LogLevel))
            return false;

        if (criteria.CorrelationId is not null && !MatchesField(entry.CorrelationId, entry.RawLine, criteria.CorrelationId, exact: false))
            return false;

        if (criteria.ExceptionType is not null && !MatchesField(entry.ExceptionType, entry.RawLine, criteria.ExceptionType, exact: false))
            return false;

        if (criteria.Phrase is not null &&
            !ContainsInsensitive(entry.Message ?? string.Empty, criteria.Phrase) &&
            !ContainsInsensitive(entry.RawLine, criteria.Phrase))
        {
            return false;
        }

        if (criteria.SearchKeyword is null)
            return true;

        return criteria.ExactMatch
            ? MatchesExact(entry, criteria.SearchKeyword)
            : ContainsNormalized(entry.Message, criteria.SearchKeyword) || ContainsNormalized(entry.RawLine, criteria.SearchKeyword);
    }

    private static bool MatchesExact(ParsedLogEntry entry, string searchKeyword)
    {
        string normalizedSearch = NormalizeForMatching(searchKeyword);
        string normalizedMessage = NormalizeForMatching(entry.Message);
        string normalizedRawLine = NormalizeForMatching(entry.RawLine);

        return string.Equals(normalizedMessage, normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRawLine, normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
               normalizedMessage.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
               normalizedRawLine.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesLogLevel(ParsedLogEntry entry, string expectedLogLevel)
    {
        string? actualLogLevel = NormalizeLogLevel(entry.LogLevel ?? InferLogLevel(entry.RawLine));
        string? expected = NormalizeLogLevel(expectedLogLevel);

        return actualLogLevel is not null &&
               expected is not null &&
               string.Equals(actualLogLevel, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesField(string? fieldValue, string rawLine, string expected, bool exact)
    {
        if (!string.IsNullOrWhiteSpace(fieldValue))
        {
            return exact
                ? string.Equals(fieldValue, expected, StringComparison.OrdinalIgnoreCase)
                : fieldValue.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }

        return rawLine.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeError(ParsedLogEntry entry)
    {
        string? normalizedLevel = NormalizeLogLevel(entry.LogLevel ?? InferLogLevel(entry.RawLine));
        if (normalizedLevel is not null)
        {
            return normalizedLevel.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                   normalizedLevel.Equals("Fatal", StringComparison.OrdinalIgnoreCase) ||
                   normalizedLevel.Equals("Critical", StringComparison.OrdinalIgnoreCase);
        }

        return ContainsInsensitive(entry.RawLine, "ERROR") ||
               ContainsInsensitive(entry.RawLine, "EXCEPTION") ||
               ContainsInsensitive(entry.RawLine, "FATAL") ||
               ContainsInsensitive(entry.RawLine, "CRITICAL");
    }

    private static string BuildSuccessResponse(
        IEnumerable<LogSearchHit> hits,
        LogSearchCriteria criteria,
        string targetApp,
        string serverName,
        DateOnly startDate,
        DateOnly endDate,
        int maxResults,
        int offset,
        int pageSize)
    {
        var orderedHits = hits
            .OrderBy(h => h.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.LineNumber)
            .ToArray();

        IReadOnlyList<LogSearchHit> pagedHits = ApplyPagination(orderedHits, offset, pageSize);

        string summary = BuildSummary(orderedHits, criteria, targetApp, serverName, startDate, endDate, maxResults, offset, pageSize, pagedHits.Count);

        if (pagedHits.Count == 0)
            return $"{summary}{Environment.NewLine}{Environment.NewLine}No hits fall within the requested page. Try a smaller offset.";

        string rawLines = string.Join(Environment.NewLine, pagedHits.Select(h => h.DisplayLine));
        return $"{summary}{Environment.NewLine}{Environment.NewLine}{rawLines}";
    }

    internal static string BuildSummary(
        IReadOnlyCollection<LogSearchHit> hits,
        LogSearchCriteria criteria,
        string targetApp,
        string serverName,
        DateOnly startDate,
        DateOnly endDate,
        int maxResults,
        int offset,
        int pageSize,
        int matchesInPage)
    {
        string servers = string.Join(", ", hits.Select(h => h.Server).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s));
        int fileCount = hits.Select(h => $"{h.Server}|{h.FileName}").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        DateTimeOffset[] parsedTimestamps = hits.Where(h => h.Timestamp.HasValue).Select(h => h.Timestamp!.Value).ToArray();
        DateTimeOffset[] timestamps = parsedTimestamps.Length > 0
            ? parsedTimestamps
            : hits.Where(h => h.FallbackTimestamp.HasValue).Select(h => h.FallbackTimestamp!.Value).ToArray();
        DateTimeOffset? firstSeen = timestamps.Length == 0 ? null : timestamps.Min();
        DateTimeOffset? lastSeen = timestamps.Length == 0 ? null : timestamps.Max();

        string topMessages = string.Join(", ", hits
            .Select(h => h.Message)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .GroupBy(m => m!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(g => $"{TrimForSummary(g.Key)} ({g.Count()})"));

        string topExceptions = string.Join(", ", hits
            .Select(h => h.ExceptionType)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .GroupBy(e => e!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(g => $"{g.Key} ({g.Count()})"));

        int currentPage = pageSize <= 0 ? 1 : (offset / pageSize) + 1;
        int? nextOffset = offset + matchesInPage < hits.Count ? offset + matchesInPage : null;
        int? previousOffset = offset > 0 ? Math.Max(0, offset - pageSize) : null;

        var sb = new StringBuilder();
        sb.AppendLine("Summary:");
        sb.AppendLine($"- server_name: {serverName}");
        sb.AppendLine($"- target_app: {targetApp}");
        sb.AppendLine($"- filters: {DescribeCriteria(criteria)}");
        sb.AppendLine($"- date_range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        sb.AppendLine($"- matches_collected: {hits.Count}");
        sb.AppendLine($"- page_size: {pageSize}");
        sb.AppendLine($"- offset: {offset}");
        sb.AppendLine($"- current_page: {currentPage}");
        sb.AppendLine($"- matches_in_page: {matchesInPage}");
        sb.AppendLine($"- max_results: {maxResults}");
        sb.AppendLine($"- more_results_available: {(offset + matchesInPage < hits.Count ? "true" : "false")}");
        sb.AppendLine($"- next_offset: {(nextOffset.HasValue ? nextOffset.Value.ToString() : "n/a")}");
        sb.AppendLine($"- previous_offset: {(previousOffset.HasValue ? previousOffset.Value.ToString() : "n/a")}");
        sb.AppendLine($"- servers_with_hits: {servers}");
        sb.AppendLine($"- files_with_hits: {fileCount}");
        sb.AppendLine($"- first_seen: {(firstSeen.HasValue ? firstSeen.Value.ToString("O") : "n/a")}");
        sb.AppendLine($"- last_seen: {(lastSeen.HasValue ? lastSeen.Value.ToString("O") : "n/a")}");

        if (!string.IsNullOrWhiteSpace(topMessages))
            sb.AppendLine($"- top_messages: {topMessages}");

        if (!string.IsNullOrWhiteSpace(topExceptions))
            sb.AppendLine($"- top_exception_types: {topExceptions}");

        return sb.ToString().TrimEnd();
    }

    internal static string BuildNoMatchResponse(
        string serverName,
        string targetApp,
        LogSearchCriteria criteria,
        DateOnly startDate,
        DateOnly endDate,
        IEnumerable<NodeSearchStatus> statuses)
    {
        NodeSearchStatus[] statusArray = statuses.OrderBy(s => s.Server, StringComparer.OrdinalIgnoreCase).ToArray();
        var sb = new StringBuilder();

        sb.Append("No matches found");
        if (criteria.SearchKeyword is not null)
            sb.Append($" for '{criteria.SearchKeyword}'");
        sb.AppendLine($" in '{targetApp}' on '{serverName}' between {startDate:yyyy-MM-dd} and {endDate:yyyy-MM-dd}.");
        sb.AppendLine($"Filters: {DescribeCriteria(criteria)}");

        if (statusArray.Length == 0 || statusArray.All(s => !s.ShareReachable))
        {
            sb.AppendLine("Reason: none of the mapped log shares were reachable.");
            foreach (NodeSearchStatus status in statusArray.Where(s => !string.IsNullOrWhiteSpace(s.ErrorMessage)))
                sb.AppendLine($"- {status.Server}: {status.ErrorMessage}");
            return sb.ToString().TrimEnd();
        }

        if (statusArray.Where(s => s.ShareReachable).All(s => s.AppFolderCount == 0))
        {
            sb.AppendLine($"Reason: no application folder matching '{targetApp}' was found on reachable servers.");
            string[] availableFolders = statusArray
                .SelectMany(s => s.AvailableFolders)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string suggestions = string.Join(", ", RankFolderSuggestions(availableFolders, targetApp).Take(5));

            if (!string.IsNullOrWhiteSpace(suggestions))
                sb.AppendLine($"Suggested folders: {suggestions}");

            return sb.ToString().TrimEnd();
        }

        if (statusArray.Where(s => s.AppFolderCount > 0).All(s => s.CandidateLogFileCount == 0))
        {
            sb.AppendLine("Reason: matching application folders were found, but no .log files were found in the requested date range.");
            sb.AppendLine("Try widening start_date/end_date or confirming the application wrote logs on that date.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("Reason: matching log files were found, but no log entries satisfied the requested filters.");
        sb.AppendLine("Suggestions: try log_level='Error', recent_errors=true, search_keyword='Exception', or search_keyword='404'.");
        return sb.ToString().TrimEnd();
    }

    internal static IReadOnlyList<LogSearchHit> ApplyPagination(
        IReadOnlyList<LogSearchHit> orderedHits,
        int offset,
        int pageSize) =>
        orderedHits.Skip(offset).Take(pageSize).ToArray();

    internal static string CreateHitId(string filePath, int lineNumber)
    {
        string payload = $"{filePath}|{lineNumber}";
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryDecodeHitId(string hitId, out string filePath, out int lineNumber)
    {
        filePath = string.Empty;
        lineNumber = 0;

        try
        {
            string padded = hitId.Replace('-', '+').Replace('_', '/');
            int padding = 4 - (padded.Length % 4);
            if (padding is > 0 and < 4)
                padded = padded.PadRight(padded.Length + padding, '=');

            string payload = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            int separatorIndex = payload.LastIndexOf('|');
            if (separatorIndex <= 0)
                return false;

            filePath = payload[..separatorIndex];
            return int.TryParse(payload[(separatorIndex + 1)..], out lineNumber) && !string.IsNullOrWhiteSpace(filePath);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string[] FilterFolderNames(
        IEnumerable<string> folderNames,
        string? filter,
        int maxResults)
    {
        IEnumerable<string> filtered = folderNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            string trimmedFilter = filter.Trim();
            filtered = filtered.Where(name => name.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
    }

    internal static IReadOnlyList<string> RankFolderSuggestions(
        IEnumerable<string> folderNames,
        string? targetApp)
    {
        string normalizedTarget = NormalizeForComparison(targetApp);

        return folderNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new
            {
                Name = name,
                Score = ScoreFolderSuggestion(name, normalizedTarget)
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name)
            .ToArray();
    }

    private static int ScoreFolderSuggestion(string folderName, string normalizedTarget)
    {
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return 0;

        string normalizedFolder = NormalizeForComparison(folderName);
        if (normalizedFolder.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            return 1000;

        int score = 0;
        if (normalizedFolder.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            score += 700;
        if (normalizedTarget.Contains(normalizedFolder, StringComparison.OrdinalIgnoreCase))
            score += 500;

        foreach (string token in normalizedTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (normalizedFolder.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 100;
        }

        return score;
    }

    private static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string DescribeCriteria(LogSearchCriteria criteria)
    {
        var parts = new List<string>();

        if (criteria.SearchKeyword is not null)
            parts.Add($"search_keyword='{criteria.SearchKeyword}'{(criteria.ExactMatch ? " (exact)" : string.Empty)}");
        if (criteria.Phrase is not null)
            parts.Add($"phrase='{criteria.Phrase}'");
        if (criteria.LogLevel is not null)
            parts.Add($"log_level='{criteria.LogLevel}'");
        if (criteria.CorrelationId is not null)
            parts.Add($"correlation_id='{criteria.CorrelationId}'");
        if (criteria.ExceptionType is not null)
            parts.Add($"exception_type='{criteria.ExceptionType}'");
        if (criteria.RecentErrors)
            parts.Add("recent_errors=true");

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string TrimForSummary(string value)
    {
        const int maxLength = 80;
        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }

    private static DateTimeOffset? TryParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;

    private static readonly Regex PlainTextTimestampRegex = new(
        @"^\s*\[?(?<ts>\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[\.,]\d{1,7})?(?:\s?(?:Z|[+-]\d{2}:\d{2}))?)\]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static DateTimeOffset? TryParseTimestampFromText(string line)
    {
        Match match = PlainTextTimestampRegex.Match(line);
        if (!match.Success)
            return null;

        string candidate = match.Groups["ts"].Value.Replace(',', '.');
        return DateTimeOffset.TryParse(
            candidate,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static string? InferLogLevel(string line)
    {
        if (ContainsInsensitive(line, "ERROR")) return "Error";
        if (ContainsInsensitive(line, "WARN")) return "Warning";
        if (ContainsInsensitive(line, "INFO")) return "Information";
        if (ContainsInsensitive(line, "DEBUG")) return "Debug";
        if (ContainsInsensitive(line, "TRACE")) return "Trace";
        if (ContainsInsensitive(line, "FATAL") || ContainsInsensitive(line, "CRITICAL")) return "Critical";
        return null;
    }

    private static string? InferExceptionType(string line) =>
        line.Split([' ', '\t', ':', ',', ';', '"', '\'', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.EndsWith("Exception", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsInsensitive(string source, string value) =>
        source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNormalized(string? source, string value)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        return NormalizeForMatching(source).Contains(NormalizeForMatching(value), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForMatching(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string decoded = WebUtility.HtmlDecode(value)
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('"', '"')
            .Replace('“', '"')
            .Replace('”', '"');

        return string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string? NormalizeLogLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToUpperInvariant() switch
        {
            "ERR" or "ERROR" => "Error",
            "WARN" or "WARNING" => "Warning",
            "INFO" or "INFORMATION" => "Information",
            "DBG" or "DEBUG" => "Debug",
            "TRACE" => "Trace",
            "FATAL" => "Fatal",
            "CRITICAL" => "Critical",
            _ => value.Trim()
        };
    }

    // ------------------------------------------------------------------ //
    //  Line formatting with JSON-aware truncation                         //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the line safe for inclusion in the AI context window:
    /// <list type="bullet">
    ///   <item>Short lines (? 500 chars) are returned as-is.</item>
    ///   <item>Long JSON lines: extract <c>message</c>/<c>timestamp</c> properties.</item>
    ///   <item>Long plain-text lines: truncate at 500 chars.</item>
    /// </list>
    /// </summary>
    private static string FormatLine(string line, LogSearchOptions options)
    {
        if (line.Length <= options.MaxLineLength)
            return line;

        // Try JSON extraction first.
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                string? ts  = TryGetString(root, "timestamp", "@timestamp", "time", "ts");
                string? msg = TryGetString(root, "message", "msg", "Message", "text");

                if (ts is not null || msg is not null)
                {
                    return $"{{\"timestamp\":\"{ts ?? "n/a"}\",\"message\":\"{EscapeJson(msg ?? "n/a")}\"}} " +
                           options.TruncationSuffix;
                }
            }
            catch (JsonException)
            {
                // Not valid JSON — fall through to plain truncation.
            }
        }

        // Plain-text truncation.
        return line[..options.MaxLineLength] + options.TruncationSuffix;
    }

    /// <summary>Tries a list of candidate property names and returns the first match.</summary>
    private static string? TryGetString(JsonElement root, params string[] candidates)
    {
        foreach (string name in candidates)
        {
            if (root.TryGetProperty(name, out JsonElement prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        return null;
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ------------------------------------------------------------------ //
    //  Async line reader (streaming — never loads full file into memory)  //
    // ------------------------------------------------------------------ //

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var fs     = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite, bufferSize: 65536,
                                          useAsync: true);
        using var reader = new StreamReader(fs);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is not null)
                yield return line;
        }
    }
}
