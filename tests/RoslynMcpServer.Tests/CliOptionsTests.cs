using RoslynMcpServer.Cli;

namespace RoslynMcpServer.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_UsesLargeRepoFriendlyDefaults()
    {
        using var root = TestRoot.Create();

        var options = CliOptions.Parse(["--root", root.Path]);

        Assert.Null(options.LoadSolutionPath);
        Assert.Equal(CliOptions.DefaultMaxSolutionCandidates, options.MaxSolutionCandidates);
        Assert.Equal(100, options.MaxSolutionCandidates);
        Assert.Equal(CliOptions.DefaultMaxProjectCandidates, options.MaxProjectCandidates);
        Assert.Equal(1000, options.MaxProjectCandidates);
    }

    [Fact]
    public void Parse_AcceptsLoadSolutionPath()
    {
        using var root = TestRoot.Create();

        var options = CliOptions.Parse(["--root", root.Path, "--load-solution", "Foo.sln"]);

        Assert.Equal("Foo.sln", options.LoadSolutionPath);
    }

    [Fact]
    public void Parse_AcceptsLoadSolutionSlnxPath()
    {
        using var root = TestRoot.Create();

        var options = CliOptions.Parse(["--root", root.Path, "--load-solution", "Foo.slnx"]);

        Assert.Equal("Foo.slnx", options.LoadSolutionPath);
    }

    [Fact]
    public void Parse_RejectsDuplicateLoadSolution()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["--load-solution", "One.sln", "--load-solution", "Two.sln"]));

        Assert.Contains("--load-solution can only be specified once", ex.Message);
    }

    [Fact]
    public void Parse_RejectsMissingLoadSolutionValue()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["--load-solution"]));

        Assert.Contains("Missing value for --load-solution", ex.Message);
    }

    [Fact]
    public void Parse_AcceptsLargeRepoTuningOptions()
    {
        using var root = TestRoot.Create();

        var options = CliOptions.Parse(
        [
            "--root", root.Path,
            "--scan-max-depth", "3",
            "--scan-timeout", "1.5",
            "--max-solution-candidates", "7",
            "--max-project-candidates", "11",
            "--max-in-flight-lsp-requests", "5"
        ]);

        Assert.Equal(3, options.ScanMaxDepth);
        Assert.Equal(TimeSpan.FromSeconds(1.5), options.ScanTimeout);
        Assert.Equal(7, options.MaxSolutionCandidates);
        Assert.Equal(11, options.MaxProjectCandidates);
        Assert.Equal(5, options.MaxInFlightLspRequests);
    }

    [Fact]
    public void Parse_RejectsInvalidLargeRepoTuningOption()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["--max-in-flight-lsp-requests", "0"]));

        Assert.Contains("--max-in-flight-lsp-requests must be a positive integer", ex.Message);
    }
}
