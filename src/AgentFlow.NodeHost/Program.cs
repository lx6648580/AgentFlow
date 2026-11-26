using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace AgentFlow.NodeHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // 运行查询可包含业务输入，使用运行 ID 日志，避免默认请求日志记录查询值。
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://127.0.0.1:5188");
        var origins = (Environment.GetEnvironmentVariable("AGENTFLOW_EDITOR_ORIGINS") ??
            "http://127.0.0.1:5187,http://localhost:5187").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(origins).WithMethods("GET", "POST", "DELETE").AllowAnyHeader().AllowCredentials()));
        builder.Services.AddSignalR();
        var app = builder.Build();

        var registry = new NodeRegistry();
        StandardNodes.Register(registry);
        try
        {
            var configured = Environment.GetEnvironmentVariable("AGENTFLOW_NODE_PLUGINS") ?? "";
            foreach (var path in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                RegisterPlugin(path, registry, app.Logger);
        }
        catch (Exception error)
        {
            app.Logger.LogError(error, "Node catalog initialization failed");
            return 2;
        }
        var catalog = registry.CreateCatalog();
        var profiles = new ModelProfiles();
        var tools = new ExternalTools(new ToolConnections());
        var archive = new RunArchive(app.Logger);
        var runs = new RunQueue(catalog, registry, profiles, tools, archive, app.Services.GetRequiredService<IHubContext<RunHub>>(),
            app.Services.GetRequiredService<ILogger<RunQueue>>());
        runs.RestorePending();
        app.Logger.LogInformation("Node catalog ready: {Count} definitions", catalog.Definitions.Count());
        app.UseCors();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            // 跨站表单和 WebSocket 也能发送请求，运行相关接口校验实际来源。
            if ((path.StartsWithSegments("/api/models") || path.StartsWithSegments("/api/runs") ||
                 path.StartsWithSegments("/api/tools") || path.StartsWithSegments("/api/conversations") ||
                 path.StartsWithSegments("/hubs/runs")) &&
                context.Request.Headers.TryGetValue("Origin", out var origin) &&
                !origins.Contains(origin.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.MapGet("/api/nodes", () => Results.Json(catalog.Definitions, WorkflowDocument.Json));
        app.MapPost("/api/nodes/validate", (WorkflowNode? node) =>
        {
            if (node?.Config is null)
                return Results.BadRequest(new { code = "InvalidDocument" });
            try
            {
                var validation = NodeValidator.Resolve(catalog.Get(node.TypeId, node.Version, node.Id), node.Config);
                return validation.Issues.Count == 0 ? Results.Ok(validation) : Results.BadRequest(validation);
            }
            catch (GraphError error)
            {
                return Results.BadRequest(new { error.Code, error.NodeId, error.ParameterId });
            }
        });
        app.MapGet("/api/models", () => Results.Ok(profiles.List()));
        app.MapGet("/api/tools", () => Results.Ok(tools.Catalog()));
        app.MapGet("/api/tools/mcp-connections", () => Results.Ok(tools.McpProfiles()));
        app.MapPost("/api/tools/mcp-connections", (McpProfile profile, bool? createOnly) =>
        {
            try { return Results.Ok(tools.SaveMcp(profile, createOnly == true)); }
            catch (GraphError error) { return Invalid(error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                app.Logger.LogWarning("Saving MCP profiles failed: {ErrorType}", error.GetType().Name);
                return Results.Json(new { code = "McpProfileSaveFailed" }, statusCode: 500);
            }
        });
        app.MapDelete("/api/tools/mcp-connections/{id}", (string id) =>
        {
            try { return tools.DeleteMcp(id) ? Results.NoContent() : Results.NotFound(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                app.Logger.LogWarning("Deleting MCP profile failed: {ErrorType}", error.GetType().Name);
                return Results.Json(new { code = "McpProfileSaveFailed" }, statusCode: 500);
            }
        });
        app.MapPost("/api/tools/mcp-connections/discover", async (McpEndpointRequest request, CancellationToken token) =>
        {
            try { return Results.Ok(await tools.ProbeMcpAsync(request.Url, token)); }
            catch (GraphError error) { return Invalid(error); }
        });
        app.MapGet("/api/tools/mcp/{server}", async (string server, CancellationToken token) =>
        {
            try { return Results.Ok(await tools.DiscoverAsync(server, token)); }
            catch (GraphError error) { return Invalid(error); }
        });
        app.MapPost("/api/models", (ModelProfile profile, bool? createOnly) =>
        {
            try
            {
                profiles.Save(profile, createOnly == true);
                return Results.Ok(profile);
            }
            catch (GraphError error)
            {
                return error.Code == "ModelProfileExists" ? Results.Conflict(new { error.Code }) : Invalid(error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                app.Logger.LogWarning("Saving model profiles failed: {ErrorType}", error.GetType().Name);
                return Results.Json(new { code = "ModelProfileSaveFailed" }, statusCode: 500);
            }
        });
        app.MapPost("/api/models/{id}/tools", async (string id, CancellationToken token) =>
        {
            try
            {
                return Results.Ok(await profiles.ProbeAsync(id, token));
            }
            catch (GraphError error)
            {
                return Invalid(error);
            }
        });
        app.MapPost("/api/runs", (RunRequest request) =>
        {
            try
            {
                var id = runs.Start(request);
                return Results.Accepted($"/api/runs/{id}", new RunStarted(id));
            }
            catch (GraphError error)
            {
                return Invalid(error);
            }
        });
        app.MapGet("/api/runs/{id:guid}", (Guid id) =>
            runs.Snapshot(id) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
        app.MapGet("/api/conversations/{id:guid}", (Guid id) =>
        {
            try { return runs.Conversation(id) is { } conversation ? Results.Ok(conversation) : Results.NotFound(); }
            catch (GraphError error) { return Invalid(error); }
        });
        app.MapGet("/api/runs/{id:guid}/definition", (Guid id) =>
            runs.Definition(id) is { } definition ? Results.Ok(definition) : Results.NotFound());
        app.MapGet("/api/runs/{id:guid}/events", (Guid id, long? after) =>
            runs.Events(id, after ?? 0) is { } events ? Results.Ok(events) : Results.NotFound());
        app.MapGet("/api/runs", (string? state, string? q, bool? queue) => Results.Ok(runs.List(state, q, queue == true)));
        app.MapGet("/api/runs/{id:guid}/recording", (Guid id) =>
            runs.Recording(id) is { } recording ? Results.Ok(recording) : Results.NotFound());
        app.MapGet("/api/runs/{id:guid}/artifacts/{artifact:guid}", (Guid id, Guid artifact) =>
            runs.Artifact(id, artifact) is { } value ? Results.Text(value, "text/plain") : Results.NotFound());
        app.MapGet("/api/runs/{id:guid}/report", (Guid id) =>
            runs.Report(id) is { } report ? Results.Ok(report) : Results.NotFound());
        app.MapPost("/api/runs/{id:guid}/control", async (Guid id, RunControlRequest request) =>
        {
            try
            {
                return await runs.ControlAsync(id, request) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound();
            }
            catch (GraphError error)
            {
                return Invalid(error);
            }
        });
        app.MapPost("/api/runs/{id:guid}/cancel", async (Guid id) =>
            await runs.CancelAsync(id) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
        app.MapPost("/api/runs/{id:guid}/decision", async (Guid id, HumanDecision request) =>
        {
            try
            {
                return await runs.DecideAsync(id, request) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound();
            }
            catch (GraphError error) { return Invalid(error); }
        });
        app.MapHub<RunHub>("/hubs/runs");

        using var workerStop = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
        var workers = Task.WhenAll(runs.ProcessAsync(workerStop.Token), runs.WatchWaitsAsync(workerStop.Token));
        try
        {
            await app.RunAsync();
        }
        finally
        {
            await workerStop.CancelAsync();
            await workers;
        }
        return 0;
    }

    private static IResult Invalid(GraphError error) => Results.Json(new { error.Code, error.NodeId, error.PortId, error.ParameterId },
        statusCode: error.Code switch
        {
            "RunQueueFull" => 429,
            "RunRecordingUnavailable" or "ConversationStorageUnavailable" => 503,
            "HumanAssigneeMismatch" => 403,
            "RunNotPaused" or "HumanWaitChanged" or "HumanWaitExpired" or "ConversationBusy" or "ConversationIncompatible" or "McpProfileExists" => 409,
            _ => 400
        });

    private static void RegisterPlugin(string path, NodeRegistry registry, ILogger logger)
    {
        var fullPath = Path.GetFullPath(path, AppContext.BaseDirectory);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The configured plugin DLL was not found.", fullPath);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        var types = assembly.GetExportedTypes().Where(type => !type.IsAbstract && typeof(INodePlugin).IsAssignableFrom(type)).ToArray();
        if (types.Length == 0)
            throw new InvalidOperationException($"Plugin has no public INodePlugin: {fullPath}");
        foreach (var type in types)
        {
            ((INodePlugin)Activator.CreateInstance(type)!).Register(registry);
            logger.LogInformation("Registered plugin {Plugin} from {Path}", type.FullName, fullPath);
        }
    }
}
