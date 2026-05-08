using Microsoft.Extensions.Configuration;
using Xunit;

namespace Logs_MCP.IntegrationTests;

public class LogShareReachabilityTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.example.json", optional: false)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();

    public static IEnumerable<object[]> ServerShareData()
    {
        var appServerMap = Configuration
            .GetSection("LogSearch:AppServerMap")
            .Get<Dictionary<string, string[]>>()
            ?? [];

        var logShare = Configuration["LogSearch:LogShare"]
            ?? @"logfiles$\LogFiles";

        foreach (var (app, servers) in appServerMap)
        {
            foreach (var server in servers)
            {
                var uncPath = $@"\\{server}\{logShare}";
                yield return [app, server, uncPath];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ServerShareData))]
    public void LogSharePathIsWellFormed(string app, string server, string uncPath)
    {
        Assert.False(string.IsNullOrWhiteSpace(app));
        Assert.False(string.IsNullOrWhiteSpace(server));
        Assert.StartsWith(@"\\", uncPath);
        Assert.Contains(server, uncPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logfiles$", uncPath, StringComparison.OrdinalIgnoreCase);
    }
}