namespace RoslynMcpServer.Infrastructure;

public static class ServerResourceUris
{
    public const string Guide = "roslyn://server/guide";
    public const string Capabilities = "roslyn://server/capabilities";

    private static readonly IReadOnlyList<string> guidanceResources = Array.AsReadOnly(
        new[]
        {
            Guide,
            Capabilities
        });

    public static IReadOnlyList<string> GuidanceResources => guidanceResources;
}
