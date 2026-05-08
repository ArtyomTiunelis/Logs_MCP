using Logs_MCP;

namespace Logs_MCP.IntegrationTests;

public class FederatedLogSearcherTests
{
    [Fact]
    public async Task SearchAsync_WhenNoMappedServerExists_ReturnsMappingError()
    {
        var options = new LogSearchOptions
        {
            AppServerMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["finweb-uat1"] = ["missing-server"]
            },
            LogShare = "missing-share"
        };

        var criteria = new LogSearchCriteria("NIA", null, null, null, null, false, false);

        string result = await FederatedLogSearcher.SearchAsync(
            "finweb-uat1",
            "potsplitter",
            criteria,
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
            15,
            0,
            15,
            options);

        Assert.DoesNotContain("ERROR: No server mapping", result);
    }

    [Fact]
    public async Task SearchAsync_WhenServerMappingIsMissing_ReturnsMappingError()
    {
        var options = new LogSearchOptions
        {
            AppServerMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["finweb-uat1"] = ["missing-server"]
            }
        };

        var criteria = new LogSearchCriteria("NIA", null, null, null, null, false, false);

        string result = await FederatedLogSearcher.SearchAsync(
            "unknown-server",
            "potsplitter",
            criteria,
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
            15,
            0,
            15,
            options);

        Assert.StartsWith("ERROR: No server mapping found for server_name 'unknown-server'.", result);
    }

    [Fact]
    public void GetCandidateAppFolders_WhenTargetAppMatches_ReturnsMatchingFoldersOnly()
    {
        string root = CreateTempDirectory();

        try
        {
            string expected = Directory.CreateDirectory(Path.Combine(root, "Next.Fin.PotSplitter.Web")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "Some.Other.App"));

            string[] result = FederatedLogSearcher.GetCandidateAppFolders(root, "potsplitter");

            Assert.Single(result);
            Assert.Equal(expected, result[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetCorrelationContextAsync_WhenServerMappingMissing_ReturnsMappingError()
    {
        string result = await FederatedLogSearcher.GetCorrelationContextAsync(
            "unknown-server",
            "potsplitter",
            "corr-123",
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
            5,
            2,
            2,
            new LogSearchOptions
            {
                AppServerMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["finweb-uat1"] = ["missing-server"]
                }
            });

        Assert.StartsWith("ERROR: No server mapping found for server_name 'unknown-server'.", result);
    }

    [Fact]
    public void GetCandidateAppFolders_WhenTargetAppDoesNotMatch_ReturnsNoFolders()
    {
        string root = CreateTempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Next.Fin.PotSplitter.Web"));

            string[] result = FederatedLogSearcher.GetCandidateAppFolders(root, "missing-app");

            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsMatch_WhenJsonFieldsMatchStructuredCriteria_ReturnsTrue()
    {
        ParsedLogEntry entry = FederatedLogSearcher.ParseLogEntry("{" +
            "\"timestamp\":\"2026-05-08T10:15:30Z\"," +
            "\"level\":\"Error\"," +
            "\"correlationId\":\"corr-123\"," +
            "\"exceptionType\":\"InvalidOperationException\"," +
            "\"message\":\"Value cannot be null\"}");

        var criteria = new LogSearchCriteria("Value cannot be null", null, "Error", "corr-123", "InvalidOperationException", true, true);

        Assert.True(FederatedLogSearcher.IsMatch(entry, criteria));
    }

    [Fact]
    public void IsMatch_WhenLogLevelDoesNotMatchJsonLevel_ReturnsFalseEvenIfMessageContainsError()
    {
        ParsedLogEntry entry = FederatedLogSearcher.ParseLogEntry("{" +
            "\"timestamp\":\"2026-05-08T10:15:30Z\"," +
            "\"level\":\"Information\"," +
            "\"message\":\"This informational event mentions an error string\"}");

        var criteria = new LogSearchCriteria("error", null, "Error", null, null, false, false);

        Assert.False(FederatedLogSearcher.IsMatch(entry, criteria));
    }

    [Fact]
    public void IsMatch_WhenRecentErrorsEnabledAndPlainTextLooksInformational_ReturnsFalse()
    {
        ParsedLogEntry entry = FederatedLogSearcher.ParseLogEntry("2026-05-08 10:15:30 INFO PotSplitter.Web started successfully");
        var criteria = new LogSearchCriteria(null, null, null, null, null, false, true);

        Assert.False(FederatedLogSearcher.IsMatch(entry, criteria));
    }

    [Fact]
    public void BuildSummary_WhenHitsExist_ReturnsSummaryBlock()
    {
        LogSearchHit[] hits =
        [
            new("hit-1", "SRV1", @"\\SRV1\logfiles$\LogFiles\app.log", "app.log", 10, "[SRV1] [File: app.log] first", DateTimeOffset.Parse("2026-05-08T10:00:00Z"), null, "Failure one", "InvalidOperationException"),
            new("hit-2", "SRV1", @"\\SRV1\logfiles$\LogFiles\app.log", "app.log", 20, "[SRV1] [File: app.log] second", DateTimeOffset.Parse("2026-05-08T10:05:00Z"), null, "Failure one", "InvalidOperationException"),
            new("hit-3", "SRV2", @"\\SRV2\logfiles$\LogFiles\other.log", "other.log", 30, "[SRV2] [File: other.log] third", DateTimeOffset.Parse("2026-05-08T10:10:00Z"), null, "Failure two", null)
        ];

        string summary = FederatedLogSearcher.BuildSummary(
            hits,
            new LogSearchCriteria("Failure", null, "Error", null, null, false, false),
            "potsplitter",
            "finweb-uat1",
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
            50,
            1,
            2,
            2);

        Assert.Contains("Summary:", summary);
        Assert.Contains("- matches_collected: 3", summary);
        Assert.Contains("- offset: 1", summary);
        Assert.Contains("- current_page: 1", summary);
        Assert.Contains("- matches_in_page: 2", summary);
        Assert.Contains("- next_offset: n/a", summary);
        Assert.Contains("- previous_offset: 0", summary);
        Assert.Contains("- first_seen: 2026-05-08T10:00:00.0000000+00:00", summary);
        Assert.Contains("- last_seen: 2026-05-08T10:10:00.0000000+00:00", summary);
        Assert.Contains("- servers_with_hits: SRV1, SRV2", summary);
        Assert.Contains("- top_messages: Failure one (2), Failure two (1)", summary);
    }

    [Fact]
    public void BuildSummary_WhenOnlyFallbackTimestampsExist_UsesFallbackTimestamps()
    {
        LogSearchHit[] hits =
        [
            new("hit-1", "SRV1", "a.log", "a.log", 1, "1", null, DateTimeOffset.Parse("2026-05-08T11:00:00Z"), "m1", null),
            new("hit-2", "SRV1", "a.log", "a.log", 2, "2", null, DateTimeOffset.Parse("2026-05-08T11:05:00Z"), "m2", null)
        ];

        string summary = FederatedLogSearcher.BuildSummary(
            hits,
            new LogSearchCriteria("m", null, null, null, null, false, false),
            "potsplitter",
            "finweb-uat1",
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
            50,
            0,
            10,
            2);

        Assert.Contains("- first_seen: 2026-05-08T11:00:00.0000000+00:00", summary);
        Assert.Contains("- last_seen: 2026-05-08T11:05:00.0000000+00:00", summary);
    }

    [Fact]
    public void ApplyPagination_WhenOffsetSkipsHits_ReturnsRequestedPage()
    {
        LogSearchHit[] hits =
        [
            new("hit-1", "SRV1", "a.log", "a.log", 1, "1", null, null, "m1", null),
            new("hit-2", "SRV1", "a.log", "a.log", 2, "2", null, null, "m2", null),
            new("hit-3", "SRV1", "a.log", "a.log", 3, "3", null, null, "m3", null)
        ];

        IReadOnlyList<LogSearchHit> page = FederatedLogSearcher.ApplyPagination(hits, 1, 2);

        Assert.Equal(2, page.Count);
        Assert.Equal("2", page[0].DisplayLine);
        Assert.Equal("3", page[1].DisplayLine);
    }

    [Fact]
    public void FilterFolderNames_WhenFilterProvided_ReturnsDistinctSortedMatches()
    {
        string[] folders = FederatedLogSearcher.FilterFolderNames(
            ["Next.Fin.PotSplitter.Web", "another.app", "next.fin.potsplitter.web", "Unrelated"],
            "potsplitter",
            10);

        Assert.Single(folders);
        Assert.Equal("Next.Fin.PotSplitter.Web", folders[0]);
    }

    [Fact]
    public void BuildNoMatchResponse_WhenAppFolderMissing_ListsAvailableFolders()
    {
        NodeSearchStatus[] statuses =
        [
            new()
            {
                Server = "SRV1",
                BasePath = @"\\SRV1\logfiles$\LogFiles",
                ShareReachable = true,
                AppFolderCount = 0,
                CandidateLogFileCount = 0
            }
        ];

        statuses[0].AvailableFolders.AddRange(["Next.Fin.PotSplitter.Web", "Another.App"]);

        string response = FederatedLogSearcher.BuildNoMatchResponse(
            "finweb-uat1",
            "missingapp",
            new LogSearchCriteria("Failure", null, null, null, null, false, false),
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
            statuses);

        Assert.Contains("Reason: no application folder matching 'missingapp' was found", response);
        Assert.Contains("Suggested folders:", response);
        Assert.Contains("Another.App", response);
        Assert.Contains("Next.Fin.PotSplitter.Web", response);
    }

    [Fact]
    public void IsMatch_WhenPhraseMatchesLine_ReturnsTrue()
    {
        ParsedLogEntry entry = FederatedLogSearcher.ParseLogEntry("2026-05-08 ERROR Value cannot be null for parameter foo");
        var criteria = new LogSearchCriteria(null, "Value cannot be null", null, null, null, false, false);

        Assert.True(FederatedLogSearcher.IsMatch(entry, criteria));
    }

    [Fact]
    public void IsMatch_WhenExactMatchDiffersOnlyByHtmlEncoding_ReturnsTrue()
    {
        ParsedLogEntry entry = FederatedLogSearcher.ParseLogEntry("2026-05-08 ERROR Value cannot be null. (Parameter &#39;source&#39;)");
        var criteria = new LogSearchCriteria("Value cannot be null. (Parameter 'source')", null, null, null, null, true, false);

        Assert.True(FederatedLogSearcher.IsMatch(entry, criteria));
    }

    [Fact]
    public void RankFolderSuggestions_WhenTargetProvided_PrioritizesClosestMatches()
    {
        IReadOnlyList<string> suggestions = FederatedLogSearcher.RankFolderSuggestions(
            ["Another.App", "Next.Fin.PotSplitter.Web", "PotSplitter.Service"],
            "potsplitter");

        Assert.Equal("Next.Fin.PotSplitter.Web", suggestions[0]);
        Assert.Equal("PotSplitter.Service", suggestions[1]);
    }

    [Fact]
    public void CreateHitId_WhenDecoded_RestoresFilePathAndLineNumber()
    {
        string hitId = FederatedLogSearcher.CreateHitId(@"C:\logs\app.log", 42);

        bool decoded = FederatedLogSearcher.TryDecodeHitId(hitId, out string filePath, out int lineNumber);

        Assert.True(decoded);
        Assert.Equal(@"C:\logs\app.log", filePath);
        Assert.Equal(42, lineNumber);
    }

    [Fact]
    public async Task GetLogContextAsync_WhenHitExists_ReturnsTargetAndSurroundingLines()
    {
        string root = CreateTempDirectory();

        try
        {
            string filePath = Path.Combine(root, "app.log");
            await File.WriteAllLinesAsync(filePath,
            [
                "line 1",
                "line 2",
                "target line",
                "line 4",
                "line 5"
            ]);

            string hitId = FederatedLogSearcher.CreateHitId(filePath, 3);
            string result = await FederatedLogSearcher.GetLogContextAsync(hitId, 1, 1, new LogSearchOptions());

            Assert.Contains("- target_lines: 3", result);
            Assert.Contains("  2: line 2", result);
            Assert.Contains("> 3: target line", result);
            Assert.Contains("  4: line 4", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"Logs_MCP_{Guid.NewGuid():N}");
        return Directory.CreateDirectory(path).FullName;
    }
}
