using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentFlow.NodeHost;

internal sealed class ConversationStore(RunArchive archive, ILogger logger)
{
    private static string DirectoryPath => Path.Combine(ModelProfiles.DataDirectory, "conversations");
    public static string PendingPath(Guid id) => PathFor(id, ".pending");

    // 由 RunQueue 的锁保护会话读取和提交。并行 Agent 只写当前轮的会话副本。
    public ConversationTurn Open(RunRequest request, WorkflowPlan plan, string toolPolicy)
    {
        var id = request.ConversationId!.Value;
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(request.TurnInput) || request.TurnInput.Length > 16000)
            throw new GraphError("InvalidConversationInput");
        if (request.TurnInput != archive.Scrub(request.TurnInput))
            throw new GraphError("ConversationDataDenied");
        // ponytail: 人工等待与反馈链有独立的恢复语义。子工作流中的 Agent 随调用实例结束。
        if (plan.Durable || !plan.Nodes.Values.Any(node => node.IsAgent) || plan.Nodes.Values.Any(node => node.IsController) ||
            request.Debug is { PauseAtStart: true } or { Interactive: true } or { BreakpointNode: not null } or { StopAfterNode: not null })
            throw new GraphError("ConversationUnsupportedGraph");
        var output = request.ReplyOutputId ?? (plan.Outputs.Length == 1 ? plan.Outputs[0] : null);
        if (output is null || !plan.Outputs.Contains(output, StringComparer.Ordinal))
            throw new GraphError("ConversationReplyOutputRequired");

        var identity = JsonSerializer.Serialize(new
        {
            Version = 1,
            Framework = typeof(Microsoft.Agents.AI.AIAgent).Assembly.GetName().Version?.ToString(),
            Harness = typeof(Microsoft.Agents.AI.HarnessAgent).Assembly.GetName().Version?.ToString(),
            plan.Definition, plan.Profiles, ToolPolicy = toolPolicy, ReplyOutputId = output
        }, WorkflowDocument.Json);
        var pluginVersions = plan.PluginVersions.Distinct().Order().ToArray();
        if (pluginVersions.Length > 0)
            identity += string.Join(",", pluginVersions);
        var compatibility = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        var state = Load(id) ?? new ConversationState(compatibility,
            new(id, plan.Definition.WorkflowId, plan.Definition.Revision, output, []), []);
        if (state.Compatibility != compatibility)
            throw new GraphError("ConversationIncompatible");
        return new(state, request.TurnInput);
    }

    public ConversationState? Load(Guid id)
    {
        try
        {
            var pendingPath = PendingPath(id);
            var pending = Read(pendingPath);
            if (pending is not null)
            {
                if (archive.Succeeded(pending.Snapshot.Turns[^1].RunId))
                {
                    File.Move(pendingPath, PathFor(id, ".json"), overwrite: true);
                    return pending;
                }
                File.Delete(pendingPath);
            }
            return Read(PathFor(id, ".json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw StorageError(id, error);
        }
    }

    public void Stage(ConversationTurn turn, Guid runId, string reply)
    {
        var id = turn.State.Snapshot.ConversationId;
        var snapshot = turn.State.Snapshot;
        var state = turn.State with
        {
            Snapshot = snapshot with { Turns = [.. snapshot.Turns, new(runId, turn.Input, archive.Scrub(reply), DateTimeOffset.UtcNow)] },
            Sessions = turn.Sessions.ToDictionary()
        };
        var json = JsonSerializer.Serialize(state, WorkflowDocument.Json);
        if (json != archive.Scrub(json))
            throw new GraphError("ConversationDataDenied");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = PendingPath(id);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw StorageError(id, error);
        }
    }

    private static string PathFor(Guid id, string extension) => Path.Combine(DirectoryPath, id.ToString("N") + extension);

    private static ConversationState? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ConversationState>(File.ReadAllText(path), WorkflowDocument.Json)
                is { Snapshot.Turns.Length: > 0, Sessions: not null } state ? state : throw new JsonException();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private GraphError StorageError(Guid id, Exception error)
    {
        logger.LogWarning("Conversation {ConversationId} state could not be saved or read: {Type}", id, error.GetType().Name);
        return new("ConversationStorageUnavailable");
    }
}

internal sealed record ConversationState(string Compatibility, ConversationSnapshot Snapshot, Dictionary<string, JsonElement> Sessions);

internal sealed class ConversationTurn(ConversationState state, string input)
{
    public ConversationState State => state;
    public string Input => input;
    public ConcurrentDictionary<string, JsonElement> Sessions { get; } = new(state.Sessions, StringComparer.Ordinal);
}
