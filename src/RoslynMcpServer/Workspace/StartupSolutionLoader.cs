using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Workspace;

public sealed class StartupSolutionLoader(
    CliOptions options,
    WorkspaceSession session,
    ILogger<StartupSolutionLoader> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.LoadSolutionPath))
        {
            session.MarkStartupLoadPending();
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.LoadSolutionPath))
        {
            return;
        }

        try
        {
            await session.LoadSolutionAsync(options.LoadSolutionPath, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (UserFacingException ex)
        {
            await session.MarkStartupLoadFailedAsync(ex);
            logger.LogWarning(
                ex,
                "Startup solution load failed with user-facing error {ErrorCode}: {Message}",
                ex.Code,
                ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup solution load failed unexpectedly.");
        }
    }
}
