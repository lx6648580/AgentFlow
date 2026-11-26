using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace AgentFlow.Maf.POC;

public static class WorkflowRunner
{
    public static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web);

    public static Workflow Compile(WorkflowDocument graph)
    {
        var (input, normalize, output) = graph.GetExecutableChain();
        var bindings = new Dictionary<string, ExecutorBinding>
        {
            [input.Id] = Bind(input.Id, _ => input.Config.Text ?? string.Empty),
            [normalize.Id] = Bind(normalize.Id, text => text.Trim()),
            [output.Id] = Bind(output.Id, text => text)
        };
        var builder = new WorkflowBuilder(bindings[input.Id]);
        foreach (var edge in graph.Edges)
            builder.AddEdge(bindings[edge.Source], bindings[edge.Target]);
        return builder.WithOutputFrom(bindings[output.Id]).Build();
    }

    public static ExecutorBinding Bind(string id, Func<string, string> handler) => handler.BindAsExecutor(id);

    public static async Task<object> RunAsync<T>(Workflow workflow, T input, Action<RunEvent> record,
        CancellationToken cancellationToken = default) where T : notnull
    {
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: cancellationToken);
        return await ObserveAsync(run, record, cancellationToken);
    }

    public static async Task<object> ObserveAsync(StreamingRun run, Action<RunEvent> record,
        CancellationToken cancellationToken = default)
    {
        object? output = null;
        Exception? failure = null;
        var sequence = 0;
        var superstep = 0;
        // 事件类型和 Executor ID 来自 MAF，Sequence 和 Superstep 是本次读取的观测序号。
        await foreach (var message in run.WatchStreamAsync(cancellationToken))
        {
            if (message is SuperStepStartedEvent)
                superstep++;
            var executor = message as ExecutorEvent;
            record(new(++sequence, superstep, message.GetType().Name, executor?.ExecutorId,
                message is ExecutorFailedEvent failed ? failed.Data?.ToString() : message.ToString()));
            if (message is WorkflowOutputEvent result)
                output = result.Data;
            if (message is WorkflowErrorEvent error)
                failure = error.Exception;
            if (message is ExecutorFailedEvent executorFailed)
                failure ??= executorFailed.Data ?? new InvalidOperationException($"Executor {executorFailed.ExecutorId} failed.");
        }
        if (failure is not null)
            throw new InvalidOperationException(failure.Message, failure);
        return output ?? throw new InvalidOperationException("Workflow produced no output.");
    }
}

public sealed record RunEvent(int Sequence, int Superstep, string EventType, string? NodeId, string? Detail);
