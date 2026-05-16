using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Mcp;
using LspRange = RoslynMcpServer.Lsp.Range;

namespace RoslynMcpServer.Tests;

public sealed class PositionMapperTests
{
    [Fact]
    public void ToLspPosition_ConvertsOneBasedInputToZeroBasedPosition()
    {
        var position = PositionMapper.ToLspPosition(line: 12, column: 5);

        Assert.Equal(11, position.Line);
        Assert.Equal(4, position.Character);
    }

    [Fact]
    public void ToLspPosition_RejectsLessThanOne()
    {
        var ex = Assert.Throws<UserFacingException>(() => PositionMapper.ToLspPosition(line: 0, column: 1));

        Assert.Equal("invalid_position", ex.Code);
    }

    [Fact]
    public void ToMcpRange_ConvertsZeroBasedRangeToOneBasedOutput()
    {
        var range = PositionMapper.ToMcpRange(new LspRange(
            new Position(2, 3),
            new Position(4, 5)));

        Assert.Equal(3, range.StartLine);
        Assert.Equal(4, range.StartColumn);
        Assert.Equal(5, range.EndLine);
        Assert.Equal(6, range.EndColumn);
    }
}
