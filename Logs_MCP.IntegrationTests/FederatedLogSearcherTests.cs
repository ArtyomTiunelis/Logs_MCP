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

        string result = await FederatedLogSearcher.SearchAsync(
            "finweb-uat1",
            "potsplitter",
            "NIA",
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
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

        string result = await FederatedLogSearcher.SearchAsync(
            "unknown-server",
            "potsplitter",
            "NIA",
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 8),
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

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"Logs_MCP_{Guid.NewGuid():N}");
        return Directory.CreateDirectory(path).FullName;
    }
}
