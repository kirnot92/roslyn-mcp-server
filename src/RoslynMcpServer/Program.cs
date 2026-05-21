using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (CliUsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = args.Any(arg => arg is "--help" or "-h") ? 0 : 2;
    return;
}

var workspaceRoot = new WorkspaceRoot(options.Root);
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(options.LogLevel);
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

if (!string.IsNullOrWhiteSpace(options.LogFile))
{
    builder.Logging.AddProvider(new FileLoggerProvider(options.LogFile, options.LogLevel));
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(workspaceRoot);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<DocumentStateManager>();
builder.Services.AddSingleton<DiagnosticStore>();
builder.Services.AddSingleton<IRoslynWorkspaceLoader>(serviceProvider =>
{
    return RoslynWorkspaceLoader.CreateForServer(
        serviceProvider.GetRequiredService<CliOptions>(),
        serviceProvider.GetRequiredService<ILogger<RoslynLanguageServerProcess>>(),
        serviceProvider.GetRequiredService<ILoggerFactory>());
});
builder.Services.AddSingleton(serviceProvider =>
{
    return WorkspaceSession.CreateForServer(
        serviceProvider.GetRequiredService<WorkspaceRoot>(),
        serviceProvider.GetRequiredService<IRoslynWorkspaceLoader>(),
        serviceProvider.GetRequiredService<CliOptions>(),
        serviceProvider.GetRequiredService<DocumentStateManager>(),
        serviceProvider.GetRequiredService<DiagnosticStore>());
});
builder.Services.AddHostedService<StartupSolutionLoader>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
