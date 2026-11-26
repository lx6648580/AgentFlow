using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace AgentFlow.NodeHost;

internal sealed record HumanReply(FlowMessage Input, string Action, string? Value);
internal sealed record HumanBoundary(HumanWait Wait, CheckpointInfo Checkpoint);
internal sealed record HumanRecovery(HumanWait Wait, string SessionId, string CheckpointId, string Compatibility,
    BudgetState Budget, int WorkMilliseconds, HumanDecision? Decision = null);
internal sealed record ExecutionOutcome(string? Output, RunUsage? Usage, HumanBoundary? Boundary = null, string? ConversationReply = null);

internal sealed class HumanCheckpoints(Guid runId, RunArchive archive, HumanRecovery? recovery) : IDisposable
{
    private readonly FileSystemJsonCheckpointStore store = new(new DirectoryInfo(archive.CheckpointDirectory(runId)));
    public HumanRecovery? Recovery => recovery;
    public string SessionId => runId.ToString("N");
    public CheckpointManager Manager => CheckpointManager.CreateJson(new GuardedStore(store, archive), WorkflowDocument.Json);

    public void Dispose() => store.Dispose();

    public static string Compatibility(WorkflowPlan plan, ExternalTools tools)
    {
        var snapshot = JsonSerializer.Serialize(new
        {
            Engine = typeof(HumanCheckpoints).Assembly.ManifestModule.ModuleVersionId,
            NodeSdk = typeof(WorkflowDocument).Assembly.ManifestModule.ModuleVersionId,
            Framework = typeof(CheckpointManager).Assembly.ManifestModule.ModuleVersionId,
            plan.Definition, plan.Profiles, Plugins = plan.PluginVersions.Distinct().Order(), Tools = tools.Compatibility,
            Simulation = Environment.GetEnvironmentVariable("AGENTFLOW_SIMULATED_WRITES") == "1"
        }, WorkflowDocument.Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }

    private sealed class GuardedStore(ICheckpointStore<JsonElement> inner, RunArchive archive) : ICheckpointStore<JsonElement>
    {
        public ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
        {
            var text = value.GetRawText();
            if (text.Length > 1_000_000 || text != archive.Scrub(text))
                throw new GraphError("CheckpointDataDenied");
            return inner.CreateCheckpointAsync(sessionId, value, parent);
        }

        public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key) =>
            inner.RetrieveCheckpointAsync(sessionId, key);

        public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null) =>
            inner.RetrieveIndexAsync(sessionId, withParent);
    }
}
