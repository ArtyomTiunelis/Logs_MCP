using System.Runtime.CompilerServices;
using System.Text.Json;

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
        string searchKeyword,
        DateOnly startDate,
        DateOnly endDate,
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

        // Shared, thread-safe result bag + a single-element array used as
        // a ref-free atomic counter (int[] is safe to pass into async methods).
        var results        = new System.Collections.Concurrent.ConcurrentBag<string>();
        int[] matchCount   = [0]; // matchCount[0] accessed via Interlocked

        // 2. Fan-out: one Task per load-balanced node
        var nodeTasks = servers.Select(server =>
            SearchNodeAsync(server, targetApp, searchKeyword,
                            startDate, endDate, maxResults,
                            results, matchCount, options, ct));

        await Task.WhenAll(nodeTasks);

        // 3. Format output
        if (results.IsEmpty)
        {
            string noMatches = $"No matches found for '{searchKeyword}' in '{targetApp}' " +
                               $"between {startDate:yyyy-MM-dd} and {endDate:yyyy-MM-dd}.";
            return noMatches;
        }

        // results are added in arrival order; sort so output is deterministic.
        string output = string.Join(Environment.NewLine, results.OrderBy(l => l));

        return output;
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

    // ------------------------------------------------------------------ //
    //  Per-node search                                                    //
    // ------------------------------------------------------------------ //

    private static async Task SearchNodeAsync(
        string server,
        string targetApp,
        string keyword,
        DateOnly start,
        DateOnly end,
        int maxResults,
        System.Collections.Concurrent.ConcurrentBag<string> results,
        int[] matchCount,
        LogSearchOptions options,
        CancellationToken ct)
    {
        try
        {
            string basePath = $@"\\{server}\{options.LogShare}";

            if (!Directory.Exists(basePath))
            {
                results.Add($"[{server}] WARN: Share not reachable ? {basePath}");
                return;
            }

            var appFolders = GetCandidateAppFolders(basePath, targetApp);

            if (appFolders.Length == 0)
            {
                results.Add($"[{server}] WARN: No subfolder matching '{targetApp}' found under {basePath}");
                return;
            }

            // ?? Collect candidate log files filtered by LastWriteTime ???
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
                });

            // ?? Search each file ????????????????????????????????????????
            foreach (string filePath in logFiles)
            {
                if (Volatile.Read(ref matchCount[0]) >= maxResults) break;
                ct.ThrowIfCancellationRequested();

                await SearchFileAsync(server, filePath, keyword,
                                      maxResults, results, matchCount, options, ct);
            }
        }
        catch (OperationCanceledException) { /* respect cancellation */ }
        catch (Exception ex)
        {
            results.Add($"[{server}] ERROR scanning node: {ex.Message}");
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
        string keyword,
        int maxResults,
        System.Collections.Concurrent.ConcurrentBag<string> results,
        int[] matchCount,
        LogSearchOptions options,
        CancellationToken ct)
    {
        string fileName = Path.GetFileName(filePath);

        try
        {
            await foreach (string rawLine in ReadLinesAsync(filePath, ct))
            {
                if (Volatile.Read(ref matchCount[0]) >= maxResults) return;

                if (!rawLine.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                // We have a hit — format it safely.
                string formatted = FormatLine(rawLine, options);
                string output    = $"[{server}] [File: {fileName}] {formatted}";

                results.Add(output);
                Interlocked.Increment(ref matchCount[0]);
            }
        }
        catch (Exception ex)
        {
            results.Add($"[{server}] [File: {fileName}] ERROR reading file: {ex.Message}");
        }
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
