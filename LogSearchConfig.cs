namespace Logs_MCP;

/// <summary>
/// Strongly-typed representation of the <c>LogSearch</c> section in
/// <c>appsettings.json</c>.  Bound automatically by
/// <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/>;
/// add new CNAME ? server mappings in the JSON file without recompiling.
/// </summary>
internal sealed class LogSearchOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "LogSearch";

    /// <summary>
    /// Maps a CNAME / application name (case-insensitive) to the ordered
    /// list of load-balanced Windows server hostnames that host its logs.
    /// </summary>
    public Dictionary<string, string[]> AppServerMap { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UNC share segment appended to every server name.</summary>
    public string LogShare { get; init; } = @"logfiles$\LogFiles";

    /// <summary>Maximum line length before truncation / JSON summarisation.</summary>
    public int MaxLineLength { get; init; } = 500;

    /// <summary>Suffix appended to truncated lines.</summary>
    public string TruncationSuffix { get; init; } = "...[TRUNCATED FOR CONTEXT]";
}

