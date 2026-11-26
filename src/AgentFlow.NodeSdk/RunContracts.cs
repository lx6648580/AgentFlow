namespace AgentFlow;

public sealed record ModelProfile(string Id, string BaseUrl, string ModelId, string? SecretRef = null,
    int TimeoutSeconds = 60, double Temperature = 0.2, int MaxTokens = 128, bool Streaming = true);

public sealed record RunRequest(WorkflowDocument Workflow, Guid RequestId, RunDebugOptions? Debug = null)
{
    public Guid? ConversationId { get; init; }
    public string? TurnInput { get; init; }
    public string? ReplyOutputId { get; init; }
}
public sealed record RunDebugOptions(bool PauseAtStart = false, string? BreakpointNode = null, string? StopAfterNode = null,
    bool UsePureCache = false)
{
    public bool Interactive { get; init; }
    public bool StepAtStart { get; init; }
}
public sealed record RunControlRequest(string Command, long ExpectedSequence);
public sealed record RunStarted(Guid RunId);
public sealed record WorkflowLimits(int MaxSteps = 256, int MaxSeconds = 600, int? MaxModelOutputTokens = null);
public sealed record RunDefinition(Guid RunId, WorkflowDocument Workflow, ModelProfile? ModelProfile,
    IReadOnlyList<ModelProfile>? ModelProfiles = null, RunDebugOptions? Debug = null)
{
    public Guid? ConversationId { get; init; }
    public string? TurnInput { get; init; }
    public string? ReplyOutputId { get; init; }
}
public sealed record RunUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);
public sealed record RunSnapshot(Guid RunId, string WorkflowId, int Revision, string ProfileId, string State,
    long Sequence, string Output, RunUsage? Usage, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null, bool CanStepNode = false, HumanWait? Waiting = null,
    RunOutput[]? Outputs = null)
{
    public Guid? ConversationId { get; init; }
    public bool CanPause { get; init; }
    public bool PauseRequested { get; init; }
}
public sealed record ConversationSnapshot(Guid ConversationId, string WorkflowId, int Revision, string ReplyOutputId,
    ConversationMessage[] Turns);
public sealed record ConversationMessage(Guid RunId, string Input, string Reply, DateTimeOffset At);
public sealed record RunOutput(string NodeId, RunValue Value, string ContentType);
public sealed record HumanWait(string RequestId, string NodeId, string Kind, string Assignee, string Prompt,
    string Preview, DateTimeOffset ExpiresAt);
public sealed record HumanDecision(string RequestId, string Actor, string Action, string? Value = null);
public sealed record RunSummary(Guid RunId, string WorkflowId, int Revision, string State, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);
public sealed record RunValue(string Preview, int Length, Guid? ArtifactId = null, bool Truncated = false);
public sealed record RunEventItem(long Sequence, string Kind, string? NodeId, string? Text, DateTimeOffset At,
    string? IterationId = null, int? Batch = null, double? DurationMs = null, RunValue? Value = null, RunUsage? Usage = null,
    string? OutputNodeId = null);
public sealed record RunRecording(RunSnapshot Snapshot, RunDefinition Definition, RunEventItem[] Events);
public sealed record RunDiagnosticReport(RunRecording Recording, Dictionary<Guid, string> Artifacts);
public sealed record ToolProbe(string Status);
