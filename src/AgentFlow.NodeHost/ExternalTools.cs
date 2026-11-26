using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SQLitePCL;

namespace AgentFlow.NodeHost;

internal sealed partial class ExternalTools(ToolConnections connections)
{
    private const int MaxOutputBytes = 60000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HttpClient Http = new(Transport()) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient McpHttp = new(new LimitMcpResponse(Transport())) { Timeout = Timeout.InfiniteTimeSpan };

    private static SocketsHttpHandler Transport() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, MaxResponseHeadersLength = 32,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    public ToolCatalogInfo Catalog() => connections.Catalog();
    public McpProfile[] McpProfiles() => connections.ListMcp();
    public McpProfile SaveMcp(McpProfile profile, bool createOnly) => connections.SaveMcp(profile, createOnly);
    public bool DeleteMcp(string id) => connections.DeleteMcp(id);
    public string Compatibility => connections.Compatibility;

    public Task<McpToolInfo[]> DiscoverAsync(string server, CancellationToken cancellationToken) =>
        TimedAsync(8, async token =>
        {
            var allowed = connections.Mcp(server);
            await using var client = await OpenMcpAsync(allowed.Url, token);
            var tools = await DiscoverAsync(client, allowed.Tools, token);
            return tools.Select(tool => new McpToolInfo(tool.Name, tool.Description, tool.InputSchema)).ToArray();
        }, cancellationToken);

    public Task<McpToolInfo[]> ProbeMcpAsync(string url, CancellationToken cancellationToken) =>
        TimedAsync(8, async token =>
        {
            await using var client = await OpenMcpAsync(ToolConnections.Endpoint(url), token);
            var tools = await DiscoverAsync(client, null, token);
            return tools.Select(tool => new McpToolInfo(tool.Name, tool.Description, tool.InputSchema)).ToArray();
        }, cancellationToken);

    public string Target(PlannedNode node) => node.Type switch
    {
        NodeTypes.HttpRead => node.Text("connection"),
        NodeTypes.FileRead => node.Text("path"),
        NodeTypes.SqliteQuery => node.Text("source"),
        _ => node.Text("server") + "/" + node.Text("tool")
    };

    public Task<string> ExecuteAsync(PlannedNode node, string input, CancellationToken cancellationToken) =>
        TimedAsync(node.Number("seconds"), token => node.Type switch
        {
            NodeTypes.HttpRead => ReadHttpAsync(connections.Http(node.Text("connection")), token),
            NodeTypes.FileRead => ReadFileAsync(node.Text("path"), token),
            NodeTypes.SqliteQuery => Task.Run(() => ReadSqlite(node, token), token),
            NodeTypes.McpTool => InvokeMcpAsync(node.Text("server"), node.Text("tool"),
                node.Parameters["useInput"].GetBoolean() ? FlowValues.Json(input, "InvalidToolArguments") : node.Parameters["arguments"], token),
            _ => throw new GraphError("ToolAccessDenied")
        }, cancellationToken);

    private static async Task<string> ReadHttpAsync(Uri uri, CancellationToken token)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new GraphError("ToolRedirectDenied");
        if (!response.IsSuccessStatusCode)
            throw new GraphError("ToolHttpStatus");
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media is null || !(media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw new GraphError("ToolContentType");
        if (response.Content.Headers.ContentLength > MaxOutputBytes)
            throw new GraphError("ToolOutputLimit");
        return await ReadTextAsync(await response.Content.ReadAsStreamAsync(token), token);
    }

    private async Task<string> ReadFileAsync(string relative, CancellationToken token)
    {
        await using var file = new FileStream(connections.FilePath(relative), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        if (file.Length > MaxOutputBytes)
            throw new GraphError("ToolOutputLimit");
        return await ReadTextAsync(file, token);
    }

    private static async Task<string> ReadTextAsync(Stream source, CancellationToken token)
    {
        var buffer = new byte[MaxOutputBytes + 1];
        var length = await source.ReadAtLeastAsync(buffer, buffer.Length, false, token);
        if (length > MaxOutputBytes)
            throw new GraphError("ToolOutputLimit");
        string text;
        try { text = Utf8.GetString(buffer.AsSpan(0, length)); }
        catch (DecoderFallbackException) { throw new GraphError("ToolContentType"); }
        if (text.Contains('\0'))
            throw new GraphError("ToolContentType");
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    private string ReadSqlite(PlannedNode node, CancellationToken token)
    {
        var source = connections.Sqlite(node.Text("source"));
        var file = connections.FilePath(source.Path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            try
            {
                if ((File.GetAttributes(file + suffix) & (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != 0)
                    throw new GraphError("ToolPathDenied");
            }
            catch (FileNotFoundException) { }
        }
        if (new FileInfo(file).Length > 64L * 1024 * 1024)
            throw new GraphError("ToolOutputLimit");
        token.ThrowIfCancellationRequested();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = node.Number("seconds")
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA trusted_schema=OFF";
        cmd.ExecuteNonQuery();
        var handle = db.Handle!;
        raw.sqlite3_progress_handler(handle, 1000, static state => ((CancellationToken)state!).IsCancellationRequested ? 1 : 0, token);
        var authorized = raw.sqlite3_set_authorizer(handle, (strdelegate_authorizer)((_, action, table, _, database, _) =>
            action == raw.SQLITE_SELECT || (action == raw.SQLITE_READ && table == source.Table && database == "main")
                ? raw.SQLITE_OK : raw.SQLITE_DENY), null);
        if (authorized != raw.SQLITE_OK)
            throw new GraphError("ToolDatabaseError");
        try
        {
            var columns = Columns(node.Parameters["columns"]);
            var filters = Filters(node.Parameters["equals"]);
            var predicates = new List<string>();
            foreach (var item in filters)
            {
                if (item.Value.ValueKind == JsonValueKind.Null)
                    predicates.Add(Quote(item.Name) + " IS NULL");
                else
                {
                    var parameter = "$v" + predicates.Count;
                    predicates.Add(Quote(item.Name) + "=" + parameter);
                    cmd.Parameters.AddWithValue(parameter, SqlValue(item.Value));
                }
            }
            var where = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);
            cmd.CommandText = "SELECT " + string.Join(",", columns.Select(Quote)) + " FROM main." + Quote(source.Table) +
                where + " LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", node.Number("rows"));
            using var reader = cmd.ExecuteReader();
            var json = new StringBuilder("[");
            var bytes = 2;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < columns.Length; i++)
                {
                    var value = reader.GetValue(i);
                    if (value is byte[] || value is string text && text.Length > MaxOutputBytes)
                        throw new GraphError("ToolContentType");
                    row[columns[i]] = value is DBNull ? null : value;
                }
                var encoded = JsonSerializer.Serialize(row);
                bytes += Utf8.GetByteCount(encoded) + (json.Length > 1 ? 1 : 0);
                if (bytes > MaxOutputBytes)
                    throw new GraphError("ToolOutputLimit");
                if (json.Length > 1)
                    json.Append(',');
                json.Append(encoded);
            }
            return json.Append(']').ToString();
        }
        finally
        {
            raw.sqlite3_progress_handler(handle, 0, null, null);
            raw.sqlite3_set_authorizer(handle, (strdelegate_authorizer?)null, null);
        }
    }

    public static string[] Columns(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 16)
            throw new GraphError("InvalidSqliteQuery");
        var columns = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : "").ToArray();
        if (columns.Any(name => !ToolConnections.SqlName().IsMatch(name)) ||
            columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
            throw new GraphError("InvalidSqliteQuery");
        return columns;
    }

    public static JsonProperty[] Filters(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new GraphError("InvalidSqliteQuery");
        var fields = value.EnumerateObject().ToArray();
        if (fields.Length > 8 || fields.Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length ||
            fields.Any(field => !ToolConnections.SqlName().IsMatch(field.Name) || field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
            throw new GraphError("InvalidSqliteQuery");
        return fields;
    }

    private static string Quote(string id) => "\"" + id + "\"";

    private static object SqlValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => throw new GraphError("InvalidSqliteQuery")
    };

    private static async Task<T> TimedAsync<T>(int seconds, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { return await action(deadline.Token); }
        catch (Exception error) when (error is OperationCanceledException or SqliteException or IOException or
            HttpRequestException or McpException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new GraphError("ToolTimeout");
            throw new GraphError(error is SqliteException ? "ToolDatabaseError" : error is McpException or JsonException
                ? "ToolProtocolError" : "ToolUnavailable");
        }
    }

    private sealed class LimitMcpResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            try
            {
                if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                    throw new GraphError("ToolRedirectDenied");
                // 只支持有限的无状态请求响应。缓冲上限也覆盖供应商返回的 SSE 文本。
                await response.Content.LoadIntoBufferAsync(256 * 1024, cancellationToken);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}
