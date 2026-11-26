using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFlow.NodeHost;

internal sealed partial class ToolConnections
{
    private sealed record Settings(string? Root = null, Dictionary<string, string>? Http = null,
        Dictionary<string, McpSource>? Mcp = null, Dictionary<string, DatabaseSource>? Sqlite = null);

    internal sealed record McpSource(string Url, string[] Tools);
    internal sealed record DatabaseSource(string Path, string Table);
    private readonly Dictionary<string, Uri> http = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Uri Url, HashSet<string> Tools)> mcp = new(StringComparer.Ordinal);
    private readonly object mcpGate = new();
    private readonly string mcpPath = Path.Combine(ModelProfiles.DataDirectory, "mcp-profiles.json");
    private readonly Dictionary<string, DatabaseSource> sqlite = new(StringComparer.Ordinal);
    private readonly string root;

    public ToolConnections()
    {
        var configured = Environment.GetEnvironmentVariable("AGENTFLOW_TOOLS_CONFIG");
        var file = string.IsNullOrWhiteSpace(configured) ? Path.Combine(ModelProfiles.DataDirectory, "tools.json") : Path.GetFullPath(configured);
        var directory = Path.GetDirectoryName(file)!;
        var config = string.IsNullOrWhiteSpace(configured) && !File.Exists(file)
            ? new Settings()
            : JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), WorkflowDocument.Json) ?? throw new GraphError("InvalidToolPolicy");
        var dataRoot = Environment.GetEnvironmentVariable("AGENTFLOW_TOOL_ROOT") ?? config.Root;
        root = Path.GetFullPath(string.IsNullOrWhiteSpace(dataRoot) ? "tool-files" : dataRoot, directory);
        foreach (var (name, url) in config.Http ?? [])
            http.Add(RequireName(name), Endpoint(url));
        var connections = File.Exists(mcpPath)
            ? JsonSerializer.Deserialize<McpProfile[]>(File.ReadAllText(mcpPath), WorkflowDocument.Json) ?? []
            : (config.Mcp ?? []).Select(item => item.Value is { } source
                ? new McpProfile(item.Key, source.Url, source.Tools) : throw new GraphError("InvalidToolPolicy")).ToArray();
        foreach (var profile in connections)
        {
            mcp.Add(RequireName(profile.Id), McpSettings(profile));
        }
        foreach (var (name, source) in config.Sqlite ?? [])
        {
            if (source is null || !SqlName().IsMatch(source.Table ?? "") || !IsRelative(source.Path))
                throw new GraphError("InvalidToolPolicy");
            sqlite.Add(RequireName(name), source);
        }
    }

    public ToolCatalogInfo Catalog() => new(http.Keys.Order(StringComparer.Ordinal).ToArray(),
        ListMcp().Select(item => item.Id).ToArray(), sqlite.Keys.Order(StringComparer.Ordinal).ToArray());

    public string Compatibility => JsonSerializer.Serialize(new
    {
        root, Http = http.OrderBy(item => item.Key, StringComparer.Ordinal),
        Mcp = ListMcp().Select(item => new { Key = item.Id, Url = new Uri(item.Url), Tools = item.Tools }),
        Sqlite = sqlite.OrderBy(item => item.Key, StringComparer.Ordinal)
    });

    public Uri Http(string name) => http.TryGetValue(name, out var uri) ? uri : throw new GraphError("ToolAccessDenied");
    public (Uri Url, HashSet<string> Tools) Mcp(string name)
    {
        lock (mcpGate)
            return mcp.TryGetValue(name, out var connection) ? connection : throw new GraphError("ToolAccessDenied");
    }

    public McpProfile[] ListMcp()
    {
        lock (mcpGate)
            return mcp.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
                new McpProfile(item.Key, item.Value.Url.AbsoluteUri, item.Value.Tools.Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    public McpProfile SaveMcp(McpProfile profile, bool createOnly)
    {
        var id = RequireName(profile.Id);
        var connection = McpSettings(profile);
        var saved = new McpProfile(id, connection.Url.AbsoluteUri, connection.Tools.Order(StringComparer.Ordinal).ToArray());
        lock (mcpGate)
        {
            var current = ListMcp();
            var exists = mcp.ContainsKey(id);
            if (exists && createOnly)
                throw new GraphError("McpProfileExists");
            if (!exists && current.Length >= 64)
                throw new GraphError("McpProfileLimit");
            SaveMcpProfiles(current.Where(item => item.Id != id).Append(saved));
            mcp[id] = connection;
        }
        return saved;
    }

    public bool DeleteMcp(string id)
    {
        lock (mcpGate)
        {
            if (!mcp.ContainsKey(id))
                return false;
            SaveMcpProfiles(ListMcp().Where(item => item.Id != id));
            return mcp.Remove(id);
        }
    }

    private void SaveMcpProfiles(IEnumerable<McpProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(mcpPath)!);
        var temp = mcpPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles, WorkflowDocument.Json));
        File.Move(temp, mcpPath, overwrite: true);
    }

    private static (Uri Url, HashSet<string> Tools) McpSettings(McpProfile profile)
    {
        if (profile.Tools is not { Length: <= 32 })
            throw new GraphError("InvalidToolPolicy");
        return (Endpoint(profile.Url), profile.Tools.Select(RequireName).ToHashSet(StringComparer.Ordinal));
    }
    public DatabaseSource Sqlite(string name) => sqlite.TryGetValue(name, out var source) ? source : throw new GraphError("ToolAccessDenied");

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,40}\z")]
    public static partial Regex Name();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z")]
    public static partial Regex SqlName();

    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    private static string RequireName(string? name) => name is not null && Name().IsMatch(name)
        ? name : throw new GraphError("InvalidToolPolicy");

    internal static Uri Endpoint(string? url)
    {
        if (url is null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !IPAddress.TryParse(uri.IdnHost, out var address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            throw new GraphError("InvalidToolPolicy");
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var octets = address.GetAddressBytes();
        var privateV4 = octets.Length == 4 && (octets[0] == 10 || (octets[0] == 172 && octets[1] is >= 16 and <= 31) ||
            (octets[0] == 192 && octets[1] == 168));
        if ((octets.Length == 4 && ((octets[0] == 169 && octets[1] == 254) || octets[0] is 0 or >= 224)) ||
            (uri.Scheme == "http" && !IPAddress.IsLoopback(address) && !privateV4))
            throw new GraphError("InvalidToolPolicy");
        return uri;
    }

    public static bool IsRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 350 || Path.IsPathRooted(path) || path.Contains(':'))
            return false;
        return path.Replace('\\', '/').Split('/').All(part => part.Length > 0 && !part.EndsWith('.') && !part.EndsWith(' ') &&
            !part.Any(char.IsControl) && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            (!OperatingSystem.IsWindows() || !DeviceName().IsMatch(part.Split('.')[0].TrimEnd())));
    }

    public string FilePath(string relative)
    {
        if (!IsRelative(relative))
            throw new GraphError("ToolPathDenied");
        var path = Path.GetFullPath(relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), root);
        var inside = Path.GetRelativePath(root, path);
        if (!IsRelative(inside))
            throw new GraphError("ToolPathDenied");
        var current = Path.GetPathRoot(path)!;
        var parts = path[current.Length..].Split(Path.DirectorySeparatorChar);
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                    ((attributes & FileAttributes.Directory) != 0) == (i == parts.Length - 1))
                    throw new GraphError("ToolPathDenied");
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new GraphError("ToolFileNotFound");
        }
        catch (UnauthorizedAccessException) { throw new GraphError("ToolPathDenied"); }
        return path;
    }
}
