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

builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
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
        "a search keyword (plain text, Exception ID, or Correlation ID), and an optional date range. " +
        "Results are prefixed with the server name and source file for easy triage.")]
    public async Task<string> SearchFederatedLogs(
        [Description("Server CNAME / environment mapping to search, e.g. 'finweb-uat1' or 'finintsvc-dev3'.")]
        string server_name,

        [Description("Application or folder name to search within the mapped server, e.g. 'potsplitter' or 'Next.Fin.PotSplitter.Web'.")]
        string target_app,

        [Description("String, Exception ID, or Correlation ID to search for inside log files.")]
        string search_keyword,

        [Description("ISO 8601 start date (e.g. '2026-05-05'). Defaults to today if omitted.")]
        string? start_date = null,

        [Description("ISO 8601 end date (e.g. '2026-05-07'). Defaults to today if omitted.")]
        string? end_date = null,

        [Description("Maximum number of matching lines to return. Defaults to 50 to protect the AI context window.")]
        int max_results = 50)
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

        if (string.IsNullOrWhiteSpace(server_name))
            return "ERROR: server_name is required and must not be empty.";

        if (string.IsNullOrWhiteSpace(target_app))
            return "ERROR: target_app is required and must not be empty.";

        if (string.IsNullOrWhiteSpace(search_keyword))
            return "ERROR: search_keyword is required and must not be empty.";

        // ── Delegate to the searcher ─────────────────────────────────────────
        return await FederatedLogSearcher.SearchAsync(
            server_name.Trim(),
            target_app.Trim(),
            search_keyword.Trim(),
            startDate,
            endDate,
            max_results,
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

