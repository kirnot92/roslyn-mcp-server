using RoslynMcpServer.Cli;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Lsp;

public sealed class RoslynWorkspaceLoader(
    CliOptions options,
    IRoslynLanguageServerProcess process)
    : IRoslynWorkspaceLoader
{
    private const string LanguageServerLocale = "en-US";

    public async Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var handle = process.Start(target);

        try
        {
            var rootUri = ToFileUri(target.WorkspaceDirectory);
            await handle.Client.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                locale = LanguageServerLocale,
                rootUri,
                workspaceFolders = new[]
                {
                    new
                    {
                        uri = rootUri,
                        name = Path.GetFileName(target.WorkspaceDirectory)
                    }
                },
                clientInfo = new
                {
                    name = "roslyn-mcp-server",
                    version = "0.1.0"
                },
                capabilities = new
                {
                    workspace = new
                    {
                        workspaceFolders = true
                    },
                    textDocument = new
                    {
                        diagnostic = new
                        {
                            dynamicRegistration = true
                        }
                    }
                }
            }, options.StartupTimeout, cancellationToken);

            await handle.Client.NotifyAsync("initialized", new { }, cancellationToken);
            await NotifyOpenTargetAsync(handle.Client, target, cancellationToken);
            return handle;
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }
    }

    private static Task NotifyOpenTargetAsync(ILspClient client, WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var targetUri = ToFileUri(target.FullPath);
        return target.Kind is WorkspaceKind.Project
            ? client.NotifyAsync("project/open", new { projects = new[] { targetUri } }, cancellationToken)
            : client.NotifyAsync("solution/open", new { solution = targetUri }, cancellationToken);
    }

    private static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
