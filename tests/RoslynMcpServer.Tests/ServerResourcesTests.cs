using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Mcp;

namespace RoslynMcpServer.Tests;

public sealed class ServerResourcesTests
{
    [Fact]
    public void Guide_DescribesReadOnlyBestEffortUsage()
    {
        var guide = ServerResources.Guide();

        Assert.Contains("best-effort, read-only C# context provider", guide);
        Assert.Contains("list_workspaces", guide);
        Assert.Contains("get_workspace_status", guide);
    }

    [Fact]
    public void GuidanceResourceUris_AdvertiseDirectResources()
    {
        Assert.Equal(
            [ServerResourceUris.Guide, ServerResourceUris.Capabilities],
            ServerResourceUris.GuidanceResources);
    }
}
