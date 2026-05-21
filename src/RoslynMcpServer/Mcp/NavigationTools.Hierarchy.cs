using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using static RoslynMcpServer.Mcp.NavigationToolLimits;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    [McpServerTool(Name = "get_call_hierarchy")]
    [Description("Use when you have an exact C# callable position, such as a method, constructor, property accessor, event, or operator, and need direct depth-1 incoming callers, outgoing callees, or both. This is not recursive; kindFilter and includePathPrefixes reduce returned edges after Roslyn LS responds, not request cost.")]
    public async Task<object> GetCallHierarchy(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Call hierarchy direction: incoming callers, outgoing callees, or both; defaults to incoming.")]
        string direction = "incoming",
        [Description("Positive call hierarchy edge cap; defaults to 200 and is capped by the server.")]
        int? maxResults = null,
        [Description(CallHierarchyKindFilterParameterDescription)]
        string[]? kindFilter = null,
        [Description("Optional root-relative path prefixes used to keep only edges whose direction-specific counterpart is at or under those paths. This is MCP-side filtering after Roslyn LS responds.")]
        string[]? includePathPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedDirection = NavigationToolOptions.ParseCallHierarchyDirection(direction);
            var effectiveMaxResults = NavigationToolOptions.NormalizeCallHierarchyMaxResults(maxResults);
            var parsedKindFilter = NavigationToolOptions.ParseCallHierarchyKindFilter(kindFilter);
            var parsedIncludePathPrefixes = NavigationToolOptions.ParseIncludePathPrefixes(this.workspaceRoot, includePathPrefixes);
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var prepareResponse = await request.Context.Handle.Client.RequestAsync(
                "textDocument/prepareCallHierarchy",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                CallHierarchyTimeout,
                cancellationToken);

            var roots = CallHierarchyMapper.MapPreparedItems(this.workspaceRoot, prepareResponse);
            if (roots.Count == 0)
            {
                var emptyMetadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.CallHierarchy, truncated: false);
                return new CallHierarchyResult(
                    [],
                    [],
                    TotalKnown: 0,
                    TotalUnfilteredKnown: 0,
                    Returned: 0,
                    emptyMetadata.WorkspaceState,
                    emptyMetadata.Completeness,
                    emptyMetadata.Reason,
                    emptyMetadata.RetryAfterMs,
                    emptyMetadata.Truncated);
            }

            var edges = new List<CallHierarchyEdge>();
            var totalUnfilteredKnown = 0;
            var totalKnown = 0;
            var returned = 0;
            var callSitesTruncated = false;
            foreach (var root in roots)
            {
                if (parsedDirection is CallHierarchyDirection.Incoming or CallHierarchyDirection.Both)
                {
                    var incomingResponse = await request.Context.Handle.Client.RequestAsync(
                        "callHierarchy/incomingCalls",
                        new { item = root.OriginalItem },
                        CallHierarchyTimeout,
                        cancellationToken,
                        isExpensive: true);
                    CallHierarchyMapper.AddEdges(
                        this.workspaceRoot,
                        incomingResponse,
                        root.Symbol,
                        CallHierarchyDirection.Incoming,
                        parsedKindFilter,
                        parsedIncludePathPrefixes,
                        effectiveMaxResults,
                        edges,
                        ref totalUnfilteredKnown,
                        ref totalKnown,
                        ref returned,
                        ref callSitesTruncated);
                }

                if (parsedDirection is CallHierarchyDirection.Outgoing or CallHierarchyDirection.Both)
                {
                    var outgoingResponse = await request.Context.Handle.Client.RequestAsync(
                        "callHierarchy/outgoingCalls",
                        new { item = root.OriginalItem },
                        CallHierarchyTimeout,
                        cancellationToken,
                        isExpensive: true);
                    CallHierarchyMapper.AddEdges(
                        this.workspaceRoot,
                        outgoingResponse,
                        root.Symbol,
                        CallHierarchyDirection.Outgoing,
                        parsedKindFilter,
                        parsedIncludePathPrefixes,
                        effectiveMaxResults,
                        edges,
                        ref totalUnfilteredKnown,
                        ref totalKnown,
                        ref returned,
                        ref callSitesTruncated);
                }
            }

            var truncated = totalKnown > returned || callSitesTruncated;
            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.CallHierarchy, truncated);
            return new CallHierarchyResult(
                roots.Select(root => root.Symbol).ToArray(),
                edges,
                totalKnown,
                totalUnfilteredKnown,
                returned,
                metadata.WorkspaceState,
                metadata.Completeness,
                metadata.Reason,
                metadata.RetryAfterMs,
                metadata.Truncated);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    [McpServerTool(Name = "get_type_hierarchy")]
    [Description("Use when you have an exact C# type position and need base type, derived type, or interface implementation hierarchy. Traverses LSP type hierarchy edges breadth-first up to maxDepth. includePathPrefixes narrows returned and traversed follow-up types after Roslyn LS responds.")]
    public async Task<object> GetTypeHierarchy(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Type hierarchy direction: supertypes, subtypes, or both; defaults to supertypes.")]
        string direction = "supertypes",
        [Description("Positive BFS traversal depth; defaults to 2 and is capped at 5.")]
        int? maxDepth = null,
        [Description("Positive type hierarchy edge cap; defaults to 200 and is capped by the server.")]
        int? maxResults = null,
        [Description("Optional root-relative path prefixes used to keep only follow-up type hierarchy edges whose discovered type is at or under those paths. Excluded follow-up types are not traversed further.")]
        string[]? includePathPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedDirection = NavigationToolOptions.ParseTypeHierarchyDirection(direction);
            var effectiveMaxDepth = NavigationToolOptions.NormalizeTypeHierarchyMaxDepth(maxDepth);
            var effectiveMaxResults = NavigationToolOptions.NormalizeTypeHierarchyMaxResults(maxResults);
            var parsedIncludePathPrefixes = NavigationToolOptions.ParseIncludePathPrefixes(this.workspaceRoot, includePathPrefixes);
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var prepareResponse = await request.Context.Handle.Client.RequestAsync(
                "textDocument/prepareTypeHierarchy",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                TypeHierarchyTimeout,
                cancellationToken);

            var roots = TypeHierarchyMapper.MapPreparedItems(this.workspaceRoot, prepareResponse);
            if (roots.Count == 0)
            {
                var emptyMetadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.TypeHierarchy, truncated: false);
                return new TypeHierarchyResult(
                    [],
                    [],
                    TotalKnown: 0,
                    TotalUnfilteredKnown: 0,
                    Returned: 0,
                    emptyMetadata.WorkspaceState,
                    emptyMetadata.Completeness,
                    emptyMetadata.Reason,
                    emptyMetadata.RetryAfterMs,
                    emptyMetadata.Truncated);
            }

            var edges = new List<TypeHierarchyEdge>();
            var traversalState = new TypeHierarchyTraversalState();
            var visitedEdges = new HashSet<string>(StringComparer.Ordinal);
            var followUpDirections = NavigationToolOptions.GetTypeHierarchyTraversalDirections(parsedDirection);
            foreach (var traversalDirection in followUpDirections)
            {
                foreach (var root in roots)
                {
                    await TypeHierarchyMapper.TraverseAsync(
                        this.workspaceRoot,
                        request.Context.Handle.Client,
                        root,
                        traversalDirection,
                        effectiveMaxDepth,
                        effectiveMaxResults,
                        parsedIncludePathPrefixes,
                        edges,
                        visitedEdges,
                        traversalState,
                        cancellationToken);
                }
            }

            var truncated = traversalState.TotalKnown > traversalState.Returned || traversalState.HitResultLimit;
            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.TypeHierarchy, truncated);
            return new TypeHierarchyResult(
                roots.Select(root => root.Symbol).ToArray(),
                edges,
                traversalState.TotalKnown,
                traversalState.TotalUnfilteredKnown,
                traversalState.Returned,
                metadata.WorkspaceState,
                metadata.Completeness,
                metadata.Reason,
                metadata.RetryAfterMs,
                metadata.Truncated);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }
}
