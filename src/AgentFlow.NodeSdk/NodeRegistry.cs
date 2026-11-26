using System.Collections.ObjectModel;
using System.Text.Json;

namespace AgentFlow;

public sealed record NodeContext(string RunId, string NodeId, IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyDictionary<string, JsonElement> Parameters);
public sealed record NodeResult(IReadOnlyDictionary<string, JsonElement> Outputs);

public interface INodeExecutor
{
    ValueTask<NodeResult> ExecuteAsync(NodeContext context, CancellationToken cancellationToken);
}

public interface INodePlugin
{
    void Register(NodeRegistry registry);
}

public sealed class NodeRegistry
{
    private readonly Dictionary<(string, int), (NodeDefinition Definition, Func<INodeExecutor>? Factory)> nodes = [];

    // 注册发生在启动阶段。服务运行时使用冻结后的目录，不自动加载 DLL。
    public void Register(NodeDefinition definition, Func<INodeExecutor>? factory = null)
    {
        NodeCatalog.CheckDefinition(definition);
        if (!nodes.TryAdd((definition.TypeId, definition.Version), (definition, factory)))
            throw new GraphError("DuplicateNodeVersion", definition.TypeId);
    }

    public NodeCatalog CreateCatalog() => new(nodes.Values.Select(entry => entry.Definition));

    public Guid? ExecutorVersion(string typeId, int version) =>
        nodes.TryGetValue((typeId, version), out var node) ? node.Factory?.Method.Module.ModuleVersionId : null;

    internal Func<INodeExecutor> GetFactory(string typeId, int version) =>
        nodes.TryGetValue((typeId, version), out var node) && node.Factory is { } factory
            ? factory : throw new GraphError("NodeNotExecutable", typeId);
}

public sealed class NodeFactory(NodeRegistry registry, INodeValidator? validator = null)
{
    private readonly NodeCatalog catalog = registry.CreateCatalog();
    private readonly INodeValidator parameterValidator = validator ?? new NodeValidator();

    public INodeExecutor Create(string typeId, int version)
    {
        catalog.Get(typeId, version);
        return registry.GetFactory(typeId, version)();
    }

    public async ValueTask<NodeResult> ExecuteAsync(string runId, WorkflowNode node,
        IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = catalog.Get(node.TypeId, node.Version, node.Id);
        var parameters = parameterValidator.Validate(definition, node.Config);
        if (parameters.Issues.FirstOrDefault() is { } issue)
            throw new GraphError(issue.Code, node.Id, parameterId: issue.Field);
        ValidatePorts(node.Id, definition.Ports.Where(port => !port.Output), inputs);
        var context = new NodeContext(runId, node.Id, Freeze(inputs), Freeze(parameters.Values));
        var result = await Create(node.TypeId, node.Version).ExecuteAsync(context, cancellationToken);
        ValidatePorts(node.Id, definition.Ports.Where(port => port.Output), result.Outputs);
        return new(Freeze(result.Outputs));
    }

    private static IReadOnlyDictionary<string, JsonElement> Freeze(IReadOnlyDictionary<string, JsonElement> source) =>
        new ReadOnlyDictionary<string, JsonElement>(source.ToDictionary(item => item.Key, item => item.Value.Clone()));

    private static void ValidatePorts(string nodeId, IEnumerable<PortDefinition> ports,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var declared = ports.ToDictionary(port => port.Id);
        foreach (var (id, value) in values)
        {
            if (!declared.TryGetValue(id, out var port))
                throw new GraphError("UnknownPort", nodeId, id);
            if (!NodeValidator.PortAccepts(port.Type, value))
                throw new GraphError("PortTypeMismatch", nodeId, id);
        }
        foreach (var port in declared.Values.Where(port => port.Required && !values.ContainsKey(port.Id)))
            throw new GraphError("MissingPortValue", nodeId, port.Id);
    }
}
