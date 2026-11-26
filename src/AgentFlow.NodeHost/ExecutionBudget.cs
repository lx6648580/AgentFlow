using Microsoft.Extensions.AI;

namespace AgentFlow.NodeHost;

internal sealed record BudgetState(int Steps, int? RemainingOutputTokens, bool UnknownUsage, RunUsage? Usage);

internal sealed class ExecutionBudget(WorkflowLimits limits, BudgetState? restored = null)
{
    private readonly object gate = new();
    private int steps = restored?.Steps ?? 0;
    private int? remaining = restored is null ? limits.MaxModelOutputTokens : restored.RemainingOutputTokens;
    private bool unknown = restored?.UnknownUsage ?? false;
    private RunUsage? usage = restored?.Usage;

    public void Step()
    {
        if (Interlocked.Increment(ref steps) > limits.MaxSteps)
            throw new GraphError("StepBudgetExceeded");
    }

    public int Reserve(int maximum)
    {
        lock (gate)
        {
            if (remaining is not { } available)
                return maximum;
            if (available <= 0)
                throw new GraphError("TokenBudgetExceeded");
            var reserved = Math.Min(available, maximum);
            remaining = available - reserved;
            return reserved;
        }
    }

    public void Count(UsageDetails? observed, int reserved)
    {
        lock (gate)
        {
            if (remaining is not null && observed?.OutputTokenCount is { } spent)
            {
                if (spent > reserved)
                    throw new GraphError("TokenBudgetExceeded");
                remaining += reserved - (int)Math.Max(0, spent);
            }
            if (observed is null)
                unknown = true;
            else
                usage = usage is null ? new(observed.InputTokenCount, observed.OutputTokenCount, observed.TotalTokenCount)
                    : new(usage.InputTokens + observed.InputTokenCount, usage.OutputTokens + observed.OutputTokenCount,
                        usage.TotalTokens + observed.TotalTokenCount);
        }
    }

    public BudgetState Snapshot()
    {
        lock (gate) return new(steps, remaining, unknown, usage);
    }
}
