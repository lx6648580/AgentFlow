using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace AgentFlow.NodeHost;

public sealed class RunHub : Hub
{
    public async Task Subscribe(Guid runId)
    {
        if (Context.Items.TryGetValue("run", out var value) && value is Guid previous && previous != runId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, previous.ToString("N"));
        Context.Items["run"] = runId;
        await Groups.AddToGroupAsync(Context.ConnectionId, runId.ToString("N"));
    }
}

internal sealed class RunQueue(NodeCatalog catalog, NodeRegistry registry, ModelProfiles profiles, ExternalTools tools, RunArchive archive,
    IHubContext<RunHub> hub, ILogger<RunQueue> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _history = [];
    private const int QueueLimit = 8;
    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(QueueLimit) { SingleReader = true });
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly ConversationStore _conversations = new(archive, logger);
    private readonly NodeFactory _nodeFactory = new(registry);

    public Guid Start(RunRequest request)
    {
        if (request.RequestId == Guid.Empty || request.Workflow is null)
            throw new GraphError("InvalidRunRequest");
        lock (_gate)
        {
            if (_history.ContainsKey(request.RequestId) || archive.Contains(request.RequestId))
                return request.RequestId;
            if (_queue.Reader.Count >= QueueLimit)
                throw new GraphError("RunQueueFull");
            if (request.ConversationId is null && (request.TurnInput is not null || request.ReplyOutputId is not null))
                throw new GraphError("InvalidConversationInput");
            var workflow = WorkflowPlan.Validate(request.Workflow, catalog, registry, profiles);
            if (workflow.Durable && _history.Values.Count(job => job.Workflow.Durable && !job.Finished) >= RunArchive.HistoryLimit)
                throw new GraphError("RunQueueFull");
            ConversationTurn? turn = null;
            if (request.ConversationId is { } conversationId)
            {
                if (_history.Values.Any(job => job.Conversation?.State.Snapshot.ConversationId == conversationId && !job.Finished))
                    throw new GraphError("ConversationBusy");
                turn = _conversations.Open(request, workflow, tools.Compatibility);
            }
            var (scope, breakAfter) = workflow.ConfigureDebug(request.Debug);
            var definition = archive.Definition(request.RequestId, workflow, request.Debug);
            if (turn is not null)
                definition = definition with { ConversationId = request.ConversationId, TurnInput = turn.Input,
                    ReplyOutputId = turn.State.Snapshot.ReplyOutputId };
            if (scope.Durable && System.Text.Json.JsonSerializer.Serialize(workflow.Definition, WorkflowDocument.Json) !=
                System.Text.Json.JsonSerializer.Serialize(definition.Workflow, WorkflowDocument.Json))
                throw new GraphError("CheckpointDataDenied");
            var job = new Job(request.RequestId, scope, definition, archive, request.Debug, breakAfter) { Conversation = turn };
            if (!Save(job))
                throw new GraphError("RunRecordingUnavailable");
            if (!_queue.Writer.TryWrite(job))
                throw new GraphError("RunQueueFull");
            if (_history.Count >= RunArchive.HistoryLimit && _history.Values.Where(item => item.Finished).MinBy(item => item.CreatedAt) is { } oldest)
                _history.Remove(oldest.Id);
            _history.Add(job.Id, job);
            return job.Id;
        }
    }

    public void RestorePending()
    {
        lock (_gate)
        {
            foreach (var (record, recovery) in archive.Pending())
            {
                try
                {
                    var plan = WorkflowPlan.Validate(record.Definition.Workflow, catalog, registry, profiles);
                    if (!plan.Durable || HumanCheckpoints.Compatibility(plan, tools) != recovery.Compatibility)
                        throw new GraphError("CheckpointIncompatible");
                    var job = new Job(record.Snapshot.RunId, plan, record.Definition, archive, record.Definition.Debug, null, record, recovery);
                    _history.Add(job.Id, job);
                }
                catch (GraphError)
                {
                    var end = DateTimeOffset.UtcNow;
                    var snapshot = record.Snapshot with { State = "Failed", Error = "CheckpointIncompatible",
                        FinishedAt = end, Waiting = null, Sequence = record.Snapshot.Sequence + 1 };
                    var entry = new RunEventItem(snapshot.Sequence, "Failed", null, snapshot.Error, end);
                    archive.Save(record with { Snapshot = snapshot, Events = [.. record.Events, entry] }, recovery: recovery);
                }
            }
            DispatchRecovered();
        }
    }

    public async Task<RunSnapshot?> DecideAsync(Guid id, HumanDecision request)
    {
        Job? job;
        lock (_gate)
        {
            job = _history.GetValueOrDefault(id);
            if (job is null)
            {
                if (archive.Read(id) is not { } completed)
                    return null;
                if (archive.Recovery(id)?.Decision == request)
                    return completed.Snapshot;
                throw new GraphError("HumanWaitChanged");
            }
            if (job.Recovery?.Decision != request && _queue.Reader.Count >= QueueLimit)
                throw new GraphError("RunQueueFull");
            if (job.Accept(request))
            {
                job.NeedsDispatch = !_queue.Writer.TryWrite(job);
            }
        }
        await NotifyAsync(id);
        return job.Snapshot();
    }

    public async Task WatchWaitsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                Job[] jobs;
                lock (_gate)
                {
                    DispatchRecovered();
                    jobs = _history.Values.ToArray();
                }
                foreach (var job in jobs)
                {
                    if (!job.Expire())
                        continue;
                    if (Save(job))
                        Remove(job);
                    await NotifyAsync(job.Id);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // 仅在持有队列锁时调用，恢复的决定和普通请求共用队列容量。
    private void DispatchRecovered()
    {
        foreach (var job in _history.Values.Where(job => job.NeedsDispatch))
        {
            if (!_queue.Writer.TryWrite(job))
                break;
            job.NeedsDispatch = false;
        }
    }

    public RunSnapshot? Snapshot(Guid id) => Find(id)?.Snapshot() ?? archive.Read(id)?.Snapshot;
    public ConversationSnapshot? Conversation(Guid id)
    {
        lock (_gate)
            return _conversations.Load(id)?.Snapshot;
    }

    public RunDefinition? Definition(Guid id) => Find(id)?.Definition ?? archive.Read(id)?.Definition;
    public RunEventItem[]? Events(Guid id, long after) => Find(id)?.Events(after) ??
        archive.Read(id)?.Events.Where(item => item.Sequence > after).ToArray();
    public RunRecording? Recording(Guid id) => Find(id)?.Recording() ?? archive.Read(id);
    public string? Artifact(Guid runId, Guid artifactId) => (Find(runId) is not null || archive.Contains(runId))
        ? archive.ReadArtifact(runId, artifactId) : null;

    public RunSummary[] List(string? state, string? query, bool queued)
    {
        RunSummary[] active;
        lock (_gate)
            active = _history.Values.Select(job => RunArchive.Summarize(job.Snapshot())).ToArray();
        return active.Concat(archive.Summaries()).DistinctBy(run => run.RunId)
            .Where(run => queued ? !RunArchive.Terminal(run.State) : RunArchive.Terminal(run.State))
            .Where(run => string.IsNullOrWhiteSpace(state) || run.State.Equals(state, StringComparison.OrdinalIgnoreCase))
            .Where(run => string.IsNullOrWhiteSpace(query) || run.WorkflowId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                run.RunId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(run => run.CreatedAt).Take(RunArchive.HistoryLimit).ToArray();
    }

    public RunDiagnosticReport? Report(Guid id)
    {
        if (Recording(id) is not { } recording)
            return null;
        var files = new Dictionary<Guid, string>();
        var artifactIds = recording.Events.Select(item => item.Value?.ArtifactId)
            .Concat((recording.Snapshot.Outputs ?? []).Select(output => output.Value.ArtifactId)).OfType<Guid>().Distinct();
        foreach (var artifactId in artifactIds)
        {
            if (archive.ReadArtifact(id, artifactId) is { } value)
                files[artifactId] = value;
        }
        return new(recording, files);
    }

    public async Task<RunSnapshot?> ControlAsync(Guid id, RunControlRequest request)
    {
        if (Find(id) is not { } job)
            return null;
        job.Release(request);
        await NotifyAsync(id);
        return job.Snapshot();
    }

    public async Task<RunSnapshot?> CancelAsync(Guid id)
    {
        var job = Find(id);
        if (job is null)
            return archive.Read(id)?.Snapshot;
        await job.CancelAsync();
        if (job.Finished && Save(job))
            Remove(job);
        await NotifyAsync(job.Id);
        return job.Snapshot();
    }

    private Job? Find(Guid id)
    {
        lock (_gate)
            return _history.GetValueOrDefault(id);
    }

    public async Task ProcessAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                await ExecuteAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ExecuteAsync(Job job, CancellationToken stoppingToken)
    {
        if (!job.TryBegin())
            return;

        var state = "Failed";
        string? code = null;
        string? output = null;
        string? reply = null;
        RunUsage? usage = null;
        var previous = job.Recovery;
        var work = previous?.WorkMilliseconds ?? job.Workflow.Limits.MaxSeconds * 1000;
        var started = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(work));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation, stoppingToken, timeout.Token);
        try
        {
            await NotifyAsync(job.Id);
            linked.Token.ThrowIfCancellationRequested();
            var budget = new ExecutionBudget(job.Workflow.Limits, previous?.Budget);
            using var checkpoints = job.Workflow.Durable ? new HumanCheckpoints(job.Id, archive, previous) : null;
            var execution = new WorkflowExecution(job.Workflow, profiles, tools, archive, _nodeFactory,
                job.Debug?.UsePureCache == true ? _cache : null,
                job.UsesLockstep ? (batch, nodes, token) => job.PauseIfRequestedAsync(batch, nodes, () => NotifyAsync(job.Id), token) : null,
                async entry =>
            {
                job.Record(entry);
                await NotifyAsync(job.Id);
            }, job.Id, budget, checkpoints, suppliedInput: job.Conversation?.Input, conversation: job.Conversation,
                nativeLockstep: job.Debug?.Interactive == true);
            var outcome = await execution.RunAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (outcome.Boundary is { } boundary)
            {
                var remaining = work - (int)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (remaining < 1)
                    throw new GraphError("WorkflowTimeout");
                job.Suspend(new(boundary.Wait, boundary.Checkpoint.SessionId!, boundary.Checkpoint.CheckpointId,
                    HumanCheckpoints.Compatibility(job.Workflow, tools), budget.Snapshot(), remaining));
                await NotifyAsync(job.Id);
                return;
            }
            (output, usage, reply) = (outcome.Output, outcome.Usage, outcome.ConversationReply);
            state = "Succeeded";
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested && !job.Cancellation.IsCancellationRequested && previous is not null)
        {
            // 已落盘的决定仍指向上一个人工边界，重启后可以重新调度。
            return;
        }
        catch (Exception) when (job.Cancellation.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
            state = "Cancelled";
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            state = "TimedOut";
            code = "WorkflowTimeout";
        }
        catch (GraphError error)
        {
            code = error.Code;
            if (code is "ModelTimeout" or "WorkflowTimeout")
                state = "TimedOut";
        }
        catch (Exception error)
        {
            code = "ExecutionFailed";
            logger.LogWarning("Run {RunId} raised {ErrorType}", job.Id, error.GetType().Name);
        }
        if (job.Conversation is { } turn && state == "Succeeded")
        {
            lock (_gate)
            {
                // 先准备会话，再写运行终态。重启时以运行归档决定是否发布准备好的轮次。
                try { _conversations.Stage(turn, job.Id, reply!); }
                catch (GraphError error) { (state, code) = ("Failed", error.Code); }
                job.Finish(state, code, output, usage);
                if (Save(job))
                {
                    _history.Remove(job.Id);
                    try { _conversations.Load(turn.State.Snapshot.ConversationId); }
                    catch (GraphError) { /* 已记录存储错误，后续读取会按运行终态重试发布。 */ }
                }
                else if (job.Snapshot().State == "Succeeded")
                    job.Finish("Failed", "RunRecordingUnavailable", null, null);
            }
        }
        else
        {
            job.Finish(state, code, output, usage);
            if (Save(job))
                Remove(job);
        }
        var final = job.Snapshot();
        logger.LogInformation("Run {RunId} finished in {State} ({ErrorCode})", job.Id, final.State, final.Error);
        await NotifyAsync(job.Id);
    }

    private bool Save(Job job)
    {
        try
        {
            archive.Save(job.Recording(), recovery: job.Recovery);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Run {RunId} recording could not be saved: {Type}", job.Id, error.GetType().Name);
            return false;
        }
    }

    private void Remove(Job job)
    {
        lock (_gate)
            _history.Remove(job.Id);
    }

    private async Task NotifyAsync(Guid id)
    {
        try
        {
            await hub.Clients.Group(id.ToString("N")).SendAsync("RunChanged", id);
        }
        catch (Exception error)
        {
            // HTTP 快照仍可读取，浏览器可在重连后恢复。
            logger.LogWarning("Run {RunId} notification failed: {ErrorType}", id, error.GetType().Name);
        }
    }

    private sealed class Job
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly Queue<RunEventItem> _events = new();
        private readonly HashSet<string> _started = [];
        private readonly StringBuilder _output = new();
        private readonly SortedDictionary<string, RunOutput> _outputs = new(StringComparer.Ordinal);
        private string _state = "Queued";
        private long _sequence;
        private RunUsage? _usage;
        private string? _error;
        private DateTimeOffset? _finishedAt;
        private readonly RunArchive _archive;
        private readonly string? _breakAfter;
        private TaskCompletionSource? _resume;
        private bool _stepBatch;
        private bool _stepNode;
        private bool _stepNodeCompleted;
        private bool _pauseRequested;
        private int _artifactBytes;
        private HumanRecovery? _recovery;

        public Job(Guid id, WorkflowPlan workflow, RunDefinition definition, RunArchive archive, RunDebugOptions? debug, string? breakAfter,
            RunRecording? restored = null, HumanRecovery? recovery = null)
        {
            Id = id;
            Workflow = workflow;
            Definition = definition;
            _archive = archive;
            Debug = debug;
            _breakAfter = breakAfter;
            _stepNode = debug?.StepAtStart == true && workflow.IsLinear;
            _stepBatch = debug?.StepAtStart == true && !workflow.IsLinear;
            if (restored is null)
            {
                AddEvent("Queued", null, null);
                return;
            }
            _recovery = recovery;
            _state = recovery?.Decision is null ? "Waiting" : "Queued";
            NeedsDispatch = _state == "Queued";
            CreatedAt = restored.Snapshot.CreatedAt;
            _sequence = restored.Snapshot.Sequence;
            _output.Append(restored.Snapshot.Output);
            foreach (var output in restored.Snapshot.Outputs ?? [])
                _outputs[output.NodeId] = output;
            foreach (var entry in restored.Events)
            {
                if (entry is { Kind: "NodeStarted", NodeId: { } node })
                    _started.Add(node);
                if (entry.Value?.ArtifactId is not null)
                    _artifactBytes += entry.Value.Length;
                Append(entry);
            }
        }

        public Guid Id { get; }
        public WorkflowPlan Workflow { get; }
        public RunDefinition Definition { get; }
        public ConversationTurn? Conversation { get; init; }
        public RunDebugOptions? Debug { get; }
        public bool UsesLockstep => Debug is { } debug && (debug.Interactive || debug.PauseAtStart || debug.BreakpointNode is not null);
        public CancellationToken Cancellation => _cancel.Token;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public HumanRecovery? Recovery { get { lock (_gate) return _recovery; } }
        public bool NeedsDispatch { get; set; }
        public bool Finished { get { lock (_gate) return FinishedUnsafe; } }
        private bool FinishedUnsafe => RunArchive.Terminal(_state);

        public bool TryBegin()
        {
            lock (_gate)
            {
                if (_state != "Queued")
                    return false;
                _state = "Running";
                AddEvent(_state, null, null);
                return true;
            }
        }

        public Task CancelAsync()
        {
            lock (_gate)
            {
                if (FinishedUnsafe)
                    return Task.CompletedTask;
                if (_state is "Queued" or "Waiting")
                {
                    _state = "Cancelled";
                    _finishedAt = DateTimeOffset.UtcNow;
                    AddEvent(_state, null, null);
                    return Task.CompletedTask;
                }
                if (_state != "Cancelling")
                {
                    _state = "Cancelling";
                    AddEvent(_state, null, null);
                }
                return _cancel.CancelAsync();
            }
        }

        public void Suspend(HumanRecovery recovery)
        {
            lock (_gate)
            {
                _cancel.Token.ThrowIfCancellationRequested();
                if (_state != "Running")
                    throw new GraphError("HumanWaitChanged");
                var entry = new RunEventItem(_sequence + 1, "HumanWaiting", recovery.Wait.NodeId, recovery.Wait.Kind, DateTimeOffset.UtcNow);
                Commit(recovery, entry, "Waiting", recovery.Wait);
            }
        }

        public bool Accept(HumanDecision request)
        {
            lock (_gate)
            {
                if (_recovery?.Decision == request)
                    return false;
                if (_state != "Waiting" || _recovery is not { } recovery || recovery.Wait.RequestId != request.RequestId)
                    throw new GraphError("HumanWaitChanged");
                if (DateTimeOffset.UtcNow >= recovery.Wait.ExpiresAt)
                    throw new GraphError("HumanWaitExpired");
                var actor = request.Actor?.Trim();
                if (actor is null || !string.Equals(actor, recovery.Wait.Assignee, StringComparison.Ordinal))
                    throw new GraphError("HumanAssigneeMismatch");
                if (request.Action != "reject" && request.Action != (recovery.Wait.Kind == "approval" ? "approve" : "submit") ||
                    (request.Action == "submit" ? string.IsNullOrWhiteSpace(request.Value) : request.Value is not null) ||
                    request.Value is { Length: > 4000 })
                    throw new GraphError("InvalidHumanDecision");
                if (request.Value is { } value && value != _archive.Scrub(value))
                    throw new GraphError("CheckpointDataDenied");
                var now = DateTimeOffset.UtcNow;
                var waiting = _events.LastOrDefault(item => item.Kind == "HumanWaiting" && item.NodeId == recovery.Wait.NodeId);
                var entry = new RunEventItem(_sequence + 1, "HumanDecided", recovery.Wait.NodeId, request.Action, now,
                    DurationMs: waiting is null ? null : Math.Max(0, (now - waiting.At).TotalMilliseconds), Value: new(actor, actor.Length));
                Commit(recovery with { Decision = request }, entry, "Queued", null);
                return true;
            }
        }

        private void Commit(HumanRecovery recovery, RunEventItem entry, string state, HumanWait? waiting)
        {
            var snapshot = Snapshot() with { State = state, Sequence = entry.Sequence, Waiting = waiting };
            try { _archive.Save(new(snapshot, Definition, [.. _events, entry]), recovery: recovery); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new GraphError("RunRecordingUnavailable"); }
            _recovery = recovery;
            _state = state;
            _sequence = entry.Sequence;
            Append(entry);
        }

        public bool Expire()
        {
            lock (_gate)
            {
                if (_state != "Waiting" || _recovery is not { } recovery || DateTimeOffset.UtcNow < recovery.Wait.ExpiresAt)
                    return false;
                AddEvent("NodeFailed", recovery.Wait.NodeId, "HumanWaitExpired");
                Finish("TimedOut", "HumanWaitExpired", null, null);
                return true;
            }
        }

        public async Task PauseIfRequestedAsync(int? batch, IEnumerable<string> nodes, Func<Task> notify, CancellationToken token)
        {
            Task paused;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                var breakpointHit = _breakAfter is not null && nodes.Contains(_breakAfter);
                // 原生 Agent 先激活适配器，再由输出适配器发出节点完成事件。
                var stepFinished = Debug?.Interactive == true ? _stepNodeCompleted : nodes.Any(Workflow.Nodes.ContainsKey);
                var shouldPause = batch is null
                    ? _pauseRequested || Debug?.PauseAtStart == true || Debug?.BreakpointNode == Workflow.Entry
                    : _pauseRequested || _stepBatch || _stepNode && stepFinished || breakpointHit;
                if (!shouldPause)
                    return;
                _stepBatch = false;
                _stepNode = false;
                _stepNodeCompleted = false;
                _pauseRequested = false;
                _state = "Paused";
                _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                AddEvent("Paused", batch is null ? Workflow.Entry : breakpointHit ? Debug?.BreakpointNode : null, null, batch: batch);
                paused = _resume.Task;
            }
            await notify();
            await paused.WaitAsync(token);
        }

        public void Release(RunControlRequest request)
        {
            lock (_gate)
            {
                if (request.Command == "pause")
                {
                    if (!UsesLockstep || _state is not ("Queued" or "Running"))
                        throw new GraphError("RunInteractiveUnavailable");
                    if (!_pauseRequested)
                    {
                        _pauseRequested = true;
                        AddEvent("PauseRequested", null, null);
                    }
                    return;
                }
                if (_state != "Paused" || _resume is null || request.ExpectedSequence != _sequence)
                    throw new GraphError("RunNotPaused");
                if (request.Command is not ("continue" or "stepBatch" or "stepNode") ||
                    (request.Command == "stepNode" && !Workflow.IsLinear))
                    throw new GraphError("InvalidRunControl");
                _stepBatch = request.Command == "stepBatch";
                _stepNode = request.Command == "stepNode";
                _state = "Running";
                AddEvent("Resumed", null, request.Command);
                var pause = _resume;
                _resume = null;
                pause.TrySetResult();
            }
        }

        public void Record(RunEventItem entry)
        {
            lock (_gate)
            {
                var text = entry.Text is null ? null : _archive.Scrub(entry.Text);
                if (entry.Kind == "OutputDelta" && Workflow.Outputs.Length == 1)
                    _output.Append(text);
                var value = entry.Value is { } body ? Capture(body.Preview) : null;
                var id = entry.NodeId is { } nodeId ? _archive.Scrub(nodeId) : null;
                var iteration = entry.IterationId is { } iterationId ? _archive.Scrub(iterationId) : null;
                if (entry.Kind == "NodeStarted" && id is not null)
                    _started.Add(id);
                if (_stepNode && Debug?.Interactive == true && entry is { Kind: "NodeCompleted", NodeId: { } completed } &&
                    Workflow.Nodes.ContainsKey(completed))
                    _stepNodeCompleted = true;
                if (entry.Kind == "NodeCompleted" && id is not null && value is not null &&
                    Workflow.Nodes.GetValueOrDefault(entry.NodeId!)?.Type == NodeTypes.Output)
                    _outputs.TryAdd(id, new(id, value, OutputContentType(entry.Value!.Preview)));
                var destination = entry.OutputNodeId is { } target ? _archive.Scrub(target) : null;
                Append(entry with { Sequence = ++_sequence, Text = text, Value = value, NodeId = id, IterationId = iteration, OutputNodeId = destination });
            }
        }

        public void Finish(string state, string? error, string? output, RunUsage? usage)
        {
            lock (_gate)
            {
                _state = _state == "Cancelling" ? "Cancelled" : state;
                if (_state == "Succeeded")
                {
                    _output.Clear();
                    _output.Append(_archive.Scrub(output ?? ""));
                    _usage = usage;
                }
                _error = _state == "Cancelled" ? null : error;
                _finishedAt = DateTimeOffset.UtcNow;
                var pauseNode = _resume is null ? null : _events.LastOrDefault(item => item.Kind == "Paused")?.NodeId;
                _resume = null;
                if (_state is "Cancelled" or "TimedOut")
                {
                    var kind = _state == "Cancelled" ? "NodeCancelled" : "NodeFailed";
                    if (pauseNode is not null)
                        AddEvent(kind, pauseNode, _error);
                    foreach (var trace in _events.Where(item => item.NodeId is not null && item.Kind.StartsWith("Node", StringComparison.Ordinal))
                                 .GroupBy(item => (item.NodeId, item.IterationId)).Select(group => group.Last()).Where(item => item.Kind == "NodeStarted").ToArray())
                        AddEvent(kind, trace.NodeId, _error, trace.IterationId);
                }
                if (_state == "Succeeded")
                {
                    foreach (var node in Definition.Workflow.Nodes.Where(node => !NodeTypes.IsResource(node.TypeId) &&
                                 node.TypeId != NodeTypes.RunTerminal && !_started.Contains(node.Id)))
                        AddEvent("NodeSkipped", node.Id, null);
                }
                AddEvent(_state, null, _error);
            }
        }

        public RunSnapshot Snapshot()
        {
            lock (_gate)
                return new(Id, Definition.Workflow.WorkflowId, Definition.Workflow.Revision,
                    string.Join(", ", Definition.ModelProfiles!.Select(profile => profile.Id)), _state, _sequence,
                    _output.ToString(), _usage, _error, CreatedAt, _finishedAt, _state == "Paused" && Workflow.IsLinear,
                    _state == "Waiting" ? _recovery?.Wait : null, _outputs.Values.ToArray())
                {
                    ConversationId = Definition.ConversationId,
                    CanPause = UsesLockstep && _state is "Queued" or "Running",
                    PauseRequested = _pauseRequested && _state is "Queued" or "Running"
                };
        }

        public RunRecording Recording()
        {
            lock (_gate) return new(Snapshot(), Definition, _events.ToArray());
        }

        public RunEventItem[] Events(long after)
        {
            lock (_gate)
                return _events.Where(item => item.Sequence > after).ToArray();
        }

        private RunValue Capture(string input)
        {
            var text = _archive.Scrub(input);
            const int inlineLimit = 1024;
            if (text.Length <= inlineLimit)
                return new(text, text.Length);
            var preview = text[..(char.IsHighSurrogate(text[inlineLimit - 1]) ? inlineLimit - 1 : inlineLimit)];
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (_artifactBytes + bytes > 2_000_000)
                return new(preview, text.Length, Truncated: true);
            try
            {
                var id = _archive.StoreArtifact(Id, text);
                _artifactBytes += bytes;
                return new(preview, text.Length, id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AddEvent("ArtifactUnavailable", null, error.GetType().Name);
                return new(preview, text.Length, Truncated: true);
            }
        }

        private static string OutputContentType(string text)
        {
            var trimmed = text.AsSpan().TrimStart();
            if (!trimmed.IsEmpty && trimmed[0] is '{' or '[')
            {
                try
                {
                    using var json = JsonDocument.Parse(text);
                    return "application/json";
                }
                catch (JsonException)
                {
                    // Output 也可接收恰好以大括号开头的普通文本。
                }
            }
            return "text/plain";
        }

        private void AddEvent(string kind, string? node, string? text, string? iteration = null, int? batch = null) =>
            Append(new(++_sequence, kind, node is null ? null : _archive.Scrub(node), text, DateTimeOffset.UtcNow, iteration, batch));

        private void Append(RunEventItem entry)
        {
            if (_events.Count == 4096)
                _events.Dequeue();
            _events.Enqueue(entry);
        }
    }
}
