using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace AgentFlow.Editor.POC;

public sealed class CanvasNode(WorkflowNode definition, Point location) : NodeModel(definition.Id, location)
{
    public WorkflowNode Definition { get; set; } = definition;
    public bool Collapsed { get; set; }
    public bool Active { get; set; }
    public string? Mode { get; set; }
    public RunNodeFrame? RunFrame { get; set; }
    public Guid? RunId { get; set; }
    public string? RunState => RunFrame?.State;
    public bool Inspected { get; set; }
    public double? OutputWidth { get; set; }
    public double? OutputHeight { get; set; }
}

public sealed class CanvasPort(CanvasNode parent, PortDefinition spec, Func<CanvasPort, CanvasPort, bool> validate)
    : PortModel(spec.Id, parent, spec.Output ? PortAlignment.Right : PortAlignment.Left)
{
    public PortDefinition Spec { get; } = spec;

    public override bool CanAttachTo(ILinkable other) =>
        other is CanvasPort { Spec.Output: false } target && Spec.Output && validate(this, target);
}
