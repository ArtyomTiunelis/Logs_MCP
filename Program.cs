using Logs_MCP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;

// ── Build host with stdio MCP transport ─────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Configuration
    .AddJsonFile(
        Path.Combine(AppContext.BaseDirectory, "appsettings.example.json"),
        optional: false,
        reloadOnChange: false)
    .AddJsonFile(
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        optional: true,
        reloadOnChange: false);

// Bind the LogSearch section from appsettings.json into LogSearchOptions
builder.Services
    .AddOptions<LogSearchOptions>()
    .Bind(builder.Configuration.GetSection(LogSearchOptions.SectionName))
    .Validate(options => options.AppServerMap.Count > 0,
        $"Configuration section '{LogSearchOptions.SectionName}:AppServerMap' must contain at least one mapping.")
    .ValidateOnStart();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<LogSearchTools>();

await builder.Build().RunAsync();


// ── MCP Tool class ───────────────────────────────────────────────────────────
[McpServerToolType]
internal sealed class LogSearchTools
{
    private readonly LogSearchOptions _options;

    public LogSearchTools(IOptions<LogSearchOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Searches application logs for <paramref name="targetApp"/> on the servers mapped to
    /// <paramref name="serverName"/>, filters by date range, and returns formatted matching lines.
    /// </summary>
    [McpServerTool(Name = "search_federated_logs")]
    [Description(
        "Searches application logs on the Windows servers mapped to a specific server_name/CNAME. " +
        "Provide a required server_name such as 'finweb-uat1', a required target_app such as 'potsplitter' to narrow the log folder, " +
        "and one or more content filters such as search_keyword, log_level, correlation_id, exception_type, or recent_errors. " +
        "Results are returned as a summary plus a page of matching lines.")]
    public async Task<string> SearchFederatedLogs(
        [Description("Server CNAME / environment mapping to search, e.g. 'finweb-uat1' or 'finintsvc-dev3'.")]
        string server_name,

        [Description("Application or folder name to search within the mapped server, e.g. 'potsplitter' or 'Next.Fin.PotSplitter.Web'.")]
        string target_app,

        [Description("Optional plain-text search term, Exception ID, or Correlation ID to search for inside log files.")]
        string? search_keyword = null,

        [Description("Optional exact phrase to search for inside log lines, e.g. 'Value cannot be null'.")]
        string? phrase = null,

        [Description("Optional structured log level filter, e.g. 'Error', 'Warning', or 'Information'.")]
        string? log_level = null,

        [Description("Optional Correlation ID filter.")]
        string? correlation_id = null,

        [Description("Optional exception type filter, e.g. 'NullReferenceException'.")]
        string? exception_type = null,

        [Description("If true, search_keyword must match exactly instead of using substring matching.")]
        bool exact_match = false,

        [Description("If true, return recent error-like entries even when no search_keyword is supplied.")]
        bool recent_errors = false,

        [Description("ISO 8601 start date (e.g. '2026-05-05'). Defaults to today if omitted.")]
        string? start_date = null,

        [Description("ISO 8601 end date (e.g. '2026-05-07'). Defaults to today if omitted.")]
        string? end_date = null,

        [Description("Maximum number of matching lines to collect before paging. Defaults to 50 to protect the AI context window.")]
        int max_results = 50,

        [Description("Maximum number of matching lines to include in this response page. Defaults to max_results.")]
        int? page_size = null,

        [Description("Zero-based offset into the collected matches for pagination. Defaults to 0.")]
        int offset = 0)
    {
        // ── Validate / default dates ─────────────────────────────────────────
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        if (!TryParseDate(start_date, today, out DateOnly startDate, out string? startErr))
            return $"ERROR: Invalid start_date — {startErr}";

        if (!TryParseDate(end_date, today, out DateOnly endDate, out string? endErr))
            return $"ERROR: Invalid end_date — {endErr}";

        if (startDate > endDate)
            return $"ERROR: start_date ({startDate:yyyy-MM-dd}) must not be after end_date ({endDate:yyyy-MM-dd}).";

        if (max_results <= 0)
            return "ERROR: max_results must be a positive integer.";

        if (page_size is <= 0)
            return "ERROR: page_size must be a positive integer when provided.";

        if (offset < 0)
            return "ERROR: offset must be zero or a positive integer.";

        if (string.IsNullOrWhiteSpace(server_name))
            return "ERROR: server_name is required and must not be empty.";

        if (string.IsNullOrWhiteSpace(target_app))
            return "ERROR: target_app is required and must not be empty.";

        var criteria = new LogSearchCriteria(
            search_keyword,
            phrase,
            log_level,
            correlation_id,
            exception_type,
            exact_match,
            recent_errors);

        if (!criteria.HasAnyContentFilter)
            return "ERROR: At least one content filter is required. Provide search_keyword, phrase, log_level, correlation_id, exception_type, or set recent_errors=true.";

        // ── Delegate to the searcher ─────────────────────────────────────────
        return await FederatedLogSearcher.SearchAsync(
            server_name.Trim(),
            target_app.Trim(),
            criteria,
            startDate,
            endDate,
            max_results,
            offset,
            page_size ?? max_results,
            _options);
    }

    [McpServerTool(Name = "list_app_folders")]
    [Description(
        "Lists application folder names visible on the Windows servers mapped to a specific server_name/CNAME. " +
        "Use this to discover valid target_app values before running a log search.")]
    public async Task<string> ListAppFolders(
        [Description("Server CNAME / environment mapping to inspect, e.g. 'finweb-uat1' or 'finintsvc-dev3'.")]
        string server_name,

        [Description("Optional case-insensitive substring filter applied to discovered folder names.")]
        string? filter = null,

        [Description("Maximum number of folder names to return. Defaults to 50.")]
        int max_results = 50)
    {
        if (string.IsNullOrWhiteSpace(server_name))
            return "ERROR: server_name is required and must not be empty.";

        if (max_results <= 0)
            return "ERROR: max_results must be a positive integer.";

        return await FederatedLogSearcher.ListAppFoldersAsync(
            server_name.Trim(),
            filter,
            max_results,
            _options);
    }

    [McpServerTool(Name = "get_log_context")]
    [Description(
        "Returns surrounding lines for a specific search hit. " +
        "Provide the hit_id returned by search_federated_logs and optionally adjust the number of lines before and after the hit.")]
    public async Task<string> GetLogContext(
        [Description("Stable hit identifier returned by search_federated_logs.")]
        string hit_id,

        [Description("Number of lines to include before the matching line. Defaults to 10.")]
        int before = 10,

        [Description("Number of lines to include after the matching line. Defaults to 10.")]
        int after = 10)
    {
        if (string.IsNullOrWhiteSpace(hit_id))
            return "ERROR: hit_id is required and must not be empty.";

        if (before < 0)
            return "ERROR: before must be zero or a positive integer.";

        if (after < 0)
            return "ERROR: after must be zero or a positive integer.";

        return await FederatedLogSearcher.GetLogContextAsync(hit_id.Trim(), before, after, _options);
    }

    [McpServerTool(Name = "get_correlation_context")]
    [Description(
        "Finds entries for a correlation_id and returns surrounding lines for each matching hit. " +
        "Provide the server_name and target_app routing information, the correlation_id to expand, and optional date and context settings.")]
    public async Task<string> GetCorrelationContext(
        [Description("Server CNAME / environment mapping to search, e.g. 'finweb-uat1' or 'finintsvc-dev3'.")]
        string server_name,

        [Description("Application or folder name to search within the mapped server, e.g. 'potsplitter' or 'Next.Fin.PotSplitter.Web'.")]
        string target_app,

        [Description("Correlation ID to expand into surrounding log context.")]
        string correlation_id,

        [Description("ISO 8601 start date (e.g. '2026-05-05'). Defaults to today if omitted.")]
        string? start_date = null,

        [Description("ISO 8601 end date (e.g. '2026-05-07'). Defaults to today if omitted.")]
        string? end_date = null,

        [Description("Maximum number of correlation hits to expand. Defaults to 10.")]
        int max_hits = 10,

        [Description("Number of lines to include before each matching line. Defaults to 10.")]
        int before = 10,

        [Description("Number of lines to include after each matching line. Defaults to 10.")]
        int after = 10)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        if (!TryParseDate(start_date, today, out DateOnly startDate, out string? startErr))
            return $"ERROR: Invalid start_date — {startErr}";

        if (!TryParseDate(end_date, today, out DateOnly endDate, out string? endErr))
            return $"ERROR: Invalid end_date — {endErr}";

        if (startDate > endDate)
            return $"ERROR: start_date ({startDate:yyyy-MM-dd}) must not be after end_date ({endDate:yyyy-MM-dd}).";

        if (string.IsNullOrWhiteSpace(server_name))
            return "ERROR: server_name is required and must not be empty.";

        if (string.IsNullOrWhiteSpace(target_app))
            return "ERROR: target_app is required and must not be empty.";

        if (string.IsNullOrWhiteSpace(correlation_id))
            return "ERROR: correlation_id is required and must not be empty.";

        if (max_hits <= 0)
            return "ERROR: max_hits must be a positive integer.";

        if (before < 0)
            return "ERROR: before must be zero or a positive integer.";

        if (after < 0)
            return "ERROR: after must be zero or a positive integer.";

        return await FederatedLogSearcher.GetCorrelationContextAsync(
            server_name.Trim(),
            target_app.Trim(),
            correlation_id.Trim(),
            startDate,
            endDate,
            max_hits,
            before,
            after,
            _options);
    }

    [McpServerTool(Name = "search_recent_errors")]
    [Description(
        "Searches recent error-like log entries without requiring a search keyword. " +
        "Use this when the MCP client still enforces search_keyword on search_federated_logs.")]
    public async Task<string> SearchRecentErrors(
        [Description("Server CNAME / environment mapping to search, e.g. 'finweb-uat1' or 'finintsvc-dev3'.")]
        string server_name,

        [Description("Application or folder name to search within the mapped server, e.g. 'potsplitter' or 'Next.Fin.PotSplitter.Web'.")]
        string target_app,

        [Description("Optional structured log level filter. Defaults to 'Error'.")]
        string? log_level = "Error",

        [Description("ISO 8601 start date (e.g. '2026-05-05'). Defaults to today if omitted.")]
        string? start_date = null,

        [Description("ISO 8601 end date (e.g. '2026-05-07'). Defaults to today if omitted.")]
        string? end_date = null,

        [Description("Maximum number of matching lines to collect before paging. Defaults to 50.")]
        int max_results = 50,

        [Description("Maximum number of matching lines to include in this response page. Defaults to max_results.")]
        int? page_size = null,

        [Description("Zero-based offset into the collected matches for pagination. Defaults to 0.")]
        int offset = 0)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        if (!TryParseDate(start_date, today, out DateOnly startDate, out string? startErr))
            return $"ERROR: Invalid start_date — {startErr}";

        if (!TryParseDate(end_date, today, out DateOnly endDate, out string? endErr))
            return $"ERROR: Invalid end_date — {endErr}";

        if (startDate > endDate)
            return $"ERROR: start_date ({startDate:yyyy-MM-dd}) must not be after end_date ({endDate:yyyy-MM-dd}).";

        if (string.IsNullOrWhiteSpace(server_name))
            return "ERROR: server_name is required and must not be empty.";

        if (string.IsNullOrWhiteSpace(target_app))
            return "ERROR: target_app is required and must not be empty.";

        if (max_results <= 0)
            return "ERROR: max_results must be a positive integer.";

        if (page_size is <= 0)
            return "ERROR: page_size must be a positive integer when provided.";

        if (offset < 0)
            return "ERROR: offset must be zero or a positive integer.";

        var criteria = new LogSearchCriteria(
            searchKeyword: null,
            phrase: null,
            logLevel: log_level,
            correlationId: null,
            exceptionType: null,
            exactMatch: false,
            recentErrors: true);

        return await FederatedLogSearcher.SearchAsync(
            server_name.Trim(),
            target_app.Trim(),
            criteria,
            startDate,
            endDate,
            max_results,
            offset,
            page_size ?? max_results,
            _options);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static bool TryParseDate(
        string? input,
        DateOnly fallback,
        out DateOnly result,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            result = fallback;
            error  = null;
            return true;
        }

        if (DateOnly.TryParseExact(input.Trim(), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result))
        {
            error = null;
            return true;
        }

        result = fallback;
        error  = $"'{input}' is not a valid ISO 8601 date (expected format: yyyy-MM-dd).";
        return false;
    }
}

