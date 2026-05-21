using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Lsp;

public sealed class RoslynWorkspaceLoader : IRoslynWorkspaceLoader
{
    private const string LanguageServerLocale = "en-US";
    private readonly CliOptions options;
    private readonly IRoslynLanguageServerStarter languageServer;

    public static RoslynWorkspaceLoader CreateForServer(
        CliOptions options,
        ILogger<RoslynLanguageServerProcess> processLogger,
        ILoggerFactory loggerFactory)
    {
        var languageServer = new RoslynLanguageServerProcess(options, processLogger, loggerFactory);
        return new RoslynWorkspaceLoader(options, languageServer);
    }

    public static RoslynWorkspaceLoader CreateForTest(
        CliOptions options,
        IRoslynLanguageServerStarter languageServer)
    {
        return new RoslynWorkspaceLoader(options, languageServer);
    }

    private RoslynWorkspaceLoader(
        CliOptions options,
        IRoslynLanguageServerStarter languageServer)
    {
        this.options = options;
        this.languageServer = languageServer;
    }

    public async Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var handle = this.languageServer.Start(target);

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
                    version = "0.2.0"
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
        if (target.Kind is WorkspaceKind.Project)
        {
            return client.NotifyAsync("project/open", new { projects = new[] { targetUri } }, cancellationToken);
        }

        return client.NotifyAsync("solution/open", new { solution = targetUri }, cancellationToken);
    }

    private static string ToFileUri(string path)
    {
        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }
}
