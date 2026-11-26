using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentFlow.NodeHost;

internal sealed class RunArchive
{
    public const int HistoryLimit = 64;
    private readonly string directory;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly Dictionary<Guid, RunSummary> index = [];
    private sealed record StoredRun(RunSnapshot Snapshot, RunDefinition Definition, RunEventItem[] Events, HumanRecovery? Recovery);
    private readonly string[] secrets = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .Where(item => item.Key.ToString()!.StartsWith("AGENTFLOW_MODEL_KEY_", StringComparison.OrdinalIgnoreCase))
        .Select(item => item.Value?.ToString()).OfType<string>().Where(item => item.Length > 0)
        .SelectMany(item => new[] { item, JsonSerializer.Serialize(item)[1..^1] })
        .Distinct(StringComparer.Ordinal).OrderByDescending(item => item.Length).ToArray();

    public RunArchive(ILogger logger)
    {
        this.logger = logger;
        directory = Path.Combine(ModelProfiles.DataDirectory, "runs");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) || ReadStored(id) is not { } record)
                continue;
            if (!Terminal(record.Snapshot.State) && record.Recovery is null)
            {
                var now = DateTimeOffset.UtcNow;
                var final = record.Snapshot with { State = "Failed", Sequence = record.Snapshot.Sequence + 1,
                    Error = "RunInterrupted", FinishedAt = now, CanStepNode = false };
                var events = record.Events.Append(new RunEventItem(final.Sequence, final.State, null, final.Error, now)).ToArray();
                Save(new(final, record.Definition, events), prune: false);
            }
            else
                index[id] = Summarize(record.Snapshot);
        }
        Prune();
    }

    public static bool Terminal(string state) => state is "Succeeded" or "Failed" or "Cancelled" or "TimedOut";

    public static RunSummary Summarize(RunSnapshot snapshot) =>
        new(snapshot.RunId, snapshot.WorkflowId, snapshot.Revision, snapshot.State, snapshot.Error, snapshot.CreatedAt, snapshot.FinishedAt);

    public bool Contains(Guid id)
    {
        lock (gate) return index.ContainsKey(id);
    }

    public bool Succeeded(Guid id)
    {
        lock (gate) return index.GetValueOrDefault(id)?.State == "Succeeded";
    }

    public RunSummary[] Summaries()
    {
        lock (gate) return index.Values.ToArray();
    }

    public RunRecording? Read(Guid id) => ReadStored(id) is { } run ? new(run.Snapshot, run.Definition, run.Events) : null;
    public HumanRecovery? Recovery(Guid id) => ReadStored(id)?.Recovery;

    public (RunRecording Recording, HumanRecovery Recovery)[] Pending() => Summaries().Where(run => !Terminal(run.State))
        .Select(run => ReadStored(run.RunId)).OfType<StoredRun>().Where(run => run.Recovery is not null)
        .Select(run => (new RunRecording(run.Snapshot, run.Definition, run.Events), run.Recovery!)).ToArray();

    private StoredRun? ReadStored(Guid id)
    {
        try
        {
            var result = JsonSerializer.Deserialize<StoredRun>(File.ReadAllText(RecordingPath(id)), WorkflowDocument.Json);
            return result is { Snapshot: { } snapshot, Definition: { } definition, Events: not null } &&
                snapshot.RunId == id && definition.RunId == id ? result : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Run {RunId} archive could not be read: {Type}", id, error.GetType().Name);
            return null;
        }
    }

    public void Save(RunRecording recording, bool prune = true, HumanRecovery? recovery = null)
    {
        var id = recording.Snapshot.RunId;
        lock (gate)
        {
            var path = RecordingPath(id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new StoredRun(recording.Snapshot, recording.Definition, recording.Events, recovery),
                WorkflowDocument.Json));
            File.Move(temp, path, overwrite: true);
            index[id] = Summarize(recording.Snapshot);
            if (prune)
                Prune();
        }
    }

    public string CheckpointDirectory(Guid runId)
    {
        var path = Path.Combine(directory, runId.ToString("N") + ".checkpoints");
        Directory.CreateDirectory(path);
        return path;
    }

    public string SimulatedWrite(Guid runId, string nodeId, string input)
    {
        if (Environment.GetEnvironmentVariable("AGENTFLOW_SIMULATED_WRITES") != "1")
            throw new GraphError("ToolAccessDenied");
        var root = Path.Combine(directory, "simulated-effects");
        Directory.CreateDirectory(root);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{runId:N}/{nodeId}")));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
        var outcome = JsonSerializer.Serialize(new { idempotencyKey = key, inputHash = hash, written = true });
        var path = Path.Combine(root, key + ".json");
        if (!File.Exists(path))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, outcome);
                File.Move(temp, path);
            }
            catch (IOException) when (File.Exists(path)) { }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        if (File.ReadAllText(path) != outcome)
            throw new GraphError("IdempotencyConflict");
        return outcome;
    }

    public Guid StoreArtifact(Guid runId, string text)
    {
        var id = Guid.NewGuid();
        File.WriteAllText(ArtifactPath(runId, id), text);
        return id;
    }

    public string? ReadArtifact(Guid runId, Guid id)
    {
        try { return File.ReadAllText(ArtifactPath(runId, id)); }
        catch (FileNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Run {RunId} artifact could not be read: {Type}", runId, error.GetType().Name);
            return null;
        }
    }

    public string Scrub(string text)
    {
        foreach (var secret in secrets)
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }

    // 保留有可能跨下一个流片段的密钥前缀，直到可以完整替换它。
    public string ScrubDelta(ref string pending, string delta, bool final = false)
    {
        var current = Scrub(pending + delta);
        var tail = 0;
        if (!final)
        {
            foreach (var secret in secrets)
            {
                for (var length = Math.Min(secret.Length - 1, current.Length); length > tail; length--)
                {
                    if (current.AsSpan(current.Length - length).SequenceEqual(secret.AsSpan(0, length)))
                    {
                        tail = length;
                        break;
                    }
                }
            }
        }
        pending = tail == 0 ? "" : current[^tail..];
        return current[..(current.Length - tail)];
    }

    public RunDefinition Definition(Guid id, WorkflowPlan plan, RunDebugOptions? debug)
    {
        var graph = plan.Definition;
        if (secrets.Length > 0)
        {
            var json = JsonSerializer.SerializeToNode(graph, WorkflowDocument.Json)!;
            ScrubJson(json);
            graph = json.Deserialize<WorkflowDocument>(WorkflowDocument.Json)!;
        }
        var profiles = plan.Profiles.Select(profile => profile with
        {
            BaseUrl = Scrub(profile.BaseUrl),
            ModelId = Scrub(profile.ModelId)
        }).ToArray();
        return new(id, graph, profiles.Length == 1 ? profiles[0] : null, profiles, debug is null ? null : debug with
        {
            BreakpointNode = debug.BreakpointNode is { } breakpoint ? Scrub(breakpoint) : null,
            StopAfterNode = debug.StopAfterNode is { } stop ? Scrub(stop) : null
        });
    }

    private void ScrubJson(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    obj[key] = Scrub(text);
                else if (value is not null)
                    ScrubJson(value);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    array[i] = Scrub(text);
                else if (array[i] is { } child)
                    ScrubJson(child);
            }
        }
    }

    private string RecordingPath(Guid id) => Path.Combine(directory, $"{id:N}.json");
    private string ArtifactPath(Guid runId, Guid id) => Path.Combine(directory, $"{runId:N}-{id:N}.txt");

    private void Prune()
    {
        foreach (var run in index.Values.Where(run => Terminal(run.State)).OrderByDescending(run => run.CreatedAt)
                     .ThenByDescending(run => run.RunId).Skip(HistoryLimit).ToArray())
        {
            try
            {
                // 运行终态是准备中会话的提交依据，发布成功前保留归档。
                if (ReadStored(run.RunId)?.Snapshot.ConversationId is { } conversationId &&
                    File.Exists(ConversationStore.PendingPath(conversationId)))
                    continue;
                var checkpoints = Path.Combine(directory, $"{run.RunId:N}.checkpoints");
                if (Directory.Exists(checkpoints))
                    Directory.Delete(checkpoints, recursive: true);
                File.Delete(RecordingPath(run.RunId));
                index.Remove(run.RunId);
                foreach (var artifact in Directory.EnumerateFiles(directory, $"{run.RunId:N}-*.txt"))
                    File.Delete(artifact);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Run {RunId} archive could not be pruned: {Type}", run.RunId, error.GetType().Name);
            }
        }
    }
}
