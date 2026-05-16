using RoslynMcpServer.Cli;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Lsp;

public sealed class RoslynWorkspaceLoader(
    CliOptions options,
    RoslynLanguageServerProcess process)
    : IRoslynWorkspaceLoader
{
    public async Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var connection = process.Start(target.WorkspaceDirectory);
        var handle = new RoslynWorkspaceHandle(target, connection);

        try
        {
            var rootUri = ToFileUri(target.WorkspaceDirectory);
            await handle.Client.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
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
            }, options.StartupTimeout, cancellationToken).ConfigureAwait(false);

            await handle.Client.NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
