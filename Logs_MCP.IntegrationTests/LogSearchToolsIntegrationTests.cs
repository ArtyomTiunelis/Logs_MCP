using Logs_MCP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Logs_MCP.IntegrationTests;

public class LogSearchToolsIntegrationTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
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
    public async Task SearchFederatedLogs_WhenSearchKeywordMissing_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            " ");

        Assert.Equal("ERROR: search_keyword is required and must not be empty.", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenDateRangeIsInvalid_ReturnsValidationError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            "hub1",
            "2026-05-09",
            "2026-05-08",
            15);

        Assert.Equal("ERROR: start_date (2026-05-09) must not be after end_date (2026-05-08).", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenMappedServerProvided_DoesNotReturnMappingError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "finapihub-uat1",
            "hub",
            "hub1",
            "2026-05-08",
            "2026-05-08",
            15);

        Assert.DoesNotContain("ERROR: No server mapping found", result);
    }

    [Fact]
    public async Task SearchFederatedLogs_WhenUnknownServerProvided_ReturnsMappingError()
    {
        var tool = CreateTool();

        string result = await tool.SearchFederatedLogs(
            "unknown-server",
            "hub",
            "hub1",
            "2026-05-08",
            "2026-05-08",
            15);

        Assert.StartsWith("ERROR: No server mapping found for server_name 'unknown-server'.", result);
    }
}
