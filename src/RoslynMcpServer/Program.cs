using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(options.LogLevel);
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

if (!string.IsNullOrWhiteSpace(options.LogFile))
{
    builder.Logging.AddProvider(new FileLoggerProvider(options.LogFile, options.LogLevel));
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new PathGuard(options.Root));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<DocumentPathMapper>();
builder.Services.AddSingleton<DocumentStateManager>();
builder.Services.AddSingleton<DiagnosticStore>();
builder.Services.AddSingleton<IGitWorkspaceScanner, GitWorkspaceScanner>();
builder.Services.AddSingleton<WorkspaceScanner>();
builder.Services.AddSingleton<RoslynLanguageServerLocator>();
builder.Services.AddSingleton<IRoslynLanguageServerProcess, RoslynLanguageServerProcess>();
builder.Services.AddSingleton<IRoslynWorkspaceLoader, RoslynWorkspaceLoader>();
builder.Services.AddSingleton<WorkspaceSession>();
builder.Services.AddHostedService<StartupSolutionLoader>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
