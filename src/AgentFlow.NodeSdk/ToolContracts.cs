using System.Text.Json;

namespace AgentFlow;

public sealed record ToolCatalogInfo(string[] Http, string[] Mcp, string[] Sqlite);
public sealed record McpToolInfo(string Name, string? Description, JsonElement InputSchema);
public sealed record McpProfile(string Id, string Url, string[] Tools);
public sealed record McpEndpointRequest(string Url);
