using Logs_MCP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Logs_MCP.IntegrationTests;

public class LogSearchToolsIntegrationTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.example.json", optional: false)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();

    private static readonly LogSearchOptions SearchOptions =
        Configuration.GetSection(LogSearchOptions.SectionName).Get<LogSearchOptions>()
        ?? new LogSearchOptions();

    private static global::LogSearchTools CreateTool() =>
        new(Options.Create(SearchOptions));

    [Fact]
    public void AppSettings_LoadsExpectedMappings()
    {
        Assert.True(SearchOptions.AppServerMap.Count > 0);
        Assert.True(SearchOptions.AppServerMap.ContainsKey("finweb-uat1"));
        Assert.True(SearchOptions.AppServerMap.ContainsKey("finapihub-uat1"));
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenServerNameMissing_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            " ",
            "potsplitter",
            "hub1");

        Assert.Equal("ERROR: server_name is required and must not be empty.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenTargetAppMissing_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            " ",
            "hub1");

        Assert.Equal("ERROR: target_app is required and must not be empty.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenNoContentFiltersProvided_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub");

        Assert.Equal("ERROR: At least one content filter is required. Provide search_keyword, phrase, log_level, correlation_id, exception_type, or set recent_errors=true.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenPhraseProvided_AllowsMissingSearchKeyword()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            phrase: "Value cannot be null");

        Assert.DoesNotContain("ERROR: At least one content filter is required.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenRecentErrorsEnabled_AllowsMissingSearchKeyword()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            recent_errors: true);

        Assert.DoesNotContain("ERROR: At least one content filter is required.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenDateRangeIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            search_keyword: "hub1",
            start_date: "2026-05-09",
            end_date: "2026-05-08",
            max_results: 15);

        Assert.Equal("ERROR: start_date (2026-05-09) must not be after end_date (2026-05-08).", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenPageSizeIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            search_keyword: "hub1",
            page_size: 0);

        Assert.Equal("ERROR: page_size must be a positive integer when provided.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenOffsetIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            search_keyword: "hub1",
            offset: -1);

        Assert.Equal("ERROR: offset must be zero or a positive integer.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenMappedServerProvided_DoesNotReturnMappingError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            search_keyword: "hub1",
            start_date: "2026-05-08",
            end_date: "2026-05-08",
            max_results: 15);

        Assert.DoesNotContain("ERROR: No server mapping found", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenUnknownServerProvided_ReturnsMappingError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "unknown-server",
            "hub",
            search_keyword: "hub1",
            start_date: "2026-05-08",
            end_date: "2026-05-08",
            max_results: 15);

        Assert.StartsWith("ERROR: No server mapping found for server_name 'unknown-server'.", result);
    }

    [Fact]
    public async Task ListAppFolders_WhenServerNameMissing_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.ListAppFolders(" ");

        Assert.Equal("ERROR: server_name is required and must not be empty.", result);
    }

    [Fact]
    public async Task ListAppFolders_WhenUnknownServerProvided_ReturnsMappingError()
    {
        var tool = CreateTool();

        string result = await tool.ListAppFolders("unknown-server");

        Assert.StartsWith("ERROR: No server mapping found for server_name 'unknown-server'.", result);
    }

    [Fact]
    public async Task ListAppFolders_WhenMappedServerProvided_ReturnsDiscoveryResponse()
    {
        var tool = CreateTool();

        string result = await tool.ListAppFolders("finweb-uat1", "pot", 10);

        Assert.DoesNotContain("ERROR: No server mapping found", result);
    }

    [Fact]
    public async Task GetLogContext_WhenHitIdMissing_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.GetLogContext(" ");

        Assert.Equal("ERROR: hit_id is required and must not be empty.", result);
    }

    [Fact]
    public async Task GetLogContext_WhenBeforeIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.GetLogContext("abc", before: -1);

        Assert.Equal("ERROR: before must be zero or a positive integer.", result);
    }

    [Fact]
    public async Task GetLogContext_WhenAfterIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.GetLogContext("abc", after: -1);

        Assert.Equal("ERROR: after must be zero or a positive integer.", result);
    }

    [Fact]
    public async Task GetCorrelationContext_WhenCorrelationIdMissing_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.GetCorrelationContext("finweb-uat1", "potsplitter", " ");

        Assert.Equal("ERROR: correlation_id is required and must not be empty.", result);
    }

    [Fact]
    public async Task GetCorrelationContext_WhenMaxHitsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.GetCorrelationContext("finweb-uat1", "potsplitter", "corr-123", max_hits: 0);

        Assert.Equal("ERROR: max_hits must be a positive integer.", result);
    }

    [Fact]
    public async Task GetCorrelationContext_WhenBeforeIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.GetCorrelationContext("finweb-uat1", "potsplitter", "corr-123", before: -1);

        Assert.Equal("ERROR: before must be zero or a positive integer.", result);
    }

    [Fact]
    public async Task GetCorrelationContext_WhenAfterIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.GetCorrelationContext("finweb-uat1", "potsplitter", "corr-123", after: -1);

        Assert.Equal("ERROR: after must be zero or a positive integer.", result);
    }

    [Fact]
    public async Task SearchRecentErrors_WhenTargetAppMissing_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchRecentErrors("finweb-uat1", " ");

        Assert.Equal("ERROR: target_app is required and must not be empty.", result);
    }

    [Fact]
    public async Task SearchRecentErrors_WhenMappedServerProvided_DoesNotReturnMappingError()
    {
        var tool = CreateTool();

        string result = await tool.SearchRecentErrors("finapihub-uat1", "hub");

        Assert.DoesNotContain("ERROR: No server mapping found", result);
    }
}
