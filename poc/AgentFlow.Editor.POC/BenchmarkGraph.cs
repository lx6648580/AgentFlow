namespace AgentFlow.Editor.POC;

internal static class BenchmarkGraph
{
    public static WorkflowDocument Create(int count, int linkCount)
    {
        var nodes = new List<WorkflowNode>(count);
        var edges = new List<WorkflowEdge>(linkCount);
        var layout = new Dictionary<string, NodePosition>();
        for (var index = 0; index < count; index++)
        {
            var id = $"bench-{index}";
            nodes.Add(index < count / 2
                ? new(id, NodeTypes.Input, 1, new("AgentFlow"))
                : new(id, NodeTypes.Probe, 1, new(Ports: 4)));
            layout.Add(id, new(45 + index % 10 * 250, 45 + index / 10 * 205));
        }
        // 布局和所有边都按固定规则生成，重复运行将使用同一张图。
        for (var index = 0; index < linkCount; index++)
        {
            var sources = count / 2;
            edges.Add(new($"bench-edge-{index}", nodes[(index * 13 + index / sources) % sources].Id, "text",
                nodes[sources + index % sources].Id, $"text-{1 + index / sources}", "message"));
        }
        return new("0.1", $"bench-{count}", 1, nodes, edges, layout);
    }
}
