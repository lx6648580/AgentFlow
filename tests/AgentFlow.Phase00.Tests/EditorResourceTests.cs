using AgentFlow.Editor.POC;

namespace AgentFlow.Phase00.Tests;

public class EditorResourceTests
{
    [Fact]
    public async Task EditorExample_ValidatesOriginalNodesAndResourceConnection()
    {
        var graph = WorkflowDocument.Read(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "review.workflow.json")));
        Assert.Equal(NodeTypes.Standard.Take(9).Order(), graph.Nodes.Select(node => node.TypeId).Order());
        Assert.Single(graph.Groups!);
        Assert.Single(graph.Edges, edge => edge.Kind == "resource");
        graph.Groups!.Add(new("duplicate", null, ["input"]));
        var json = System.Text.Json.JsonSerializer.Serialize(graph, WorkflowDocument.Json);
        Assert.Equal("InvalidGroup", Assert.Throws<GraphError>(() => WorkflowDocument.Read(json)).Code);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void BuiltInNodesAndEditorResources_HaveTranslation(string culture)
    {
        foreach (var key in UiText.Keys)
        {
            var text = UiText.Get(key, culture);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
        foreach (var definition in StandardNodes.Catalog.Definitions)
        {
            Assert.NotEqual(definition.TypeId, definition.Name!.Get(culture, definition.TypeId));
            Assert.All(definition.Parameters, parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.Label!.Get(culture, ""))));
        }
    }
}
