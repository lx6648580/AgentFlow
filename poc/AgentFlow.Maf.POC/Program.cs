using System.Text;
using System.Text.Json;
using AgentFlow;

namespace AgentFlow.Maf.POC;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args is not ["run", var path])
        {
            Console.Error.WriteLine("Usage: AgentFlow.Maf.POC run <workflow.json>");
            return 2;
        }

        try
        {
            var graph = WorkflowDocument.Read(await File.ReadAllTextAsync(path));
            var workflow = WorkflowRunner.Compile(graph);
            var result = await WorkflowRunner.RunAsync(workflow, string.Empty, item =>
                Console.Error.WriteLine(JsonSerializer.Serialize(item, WorkflowRunner.LogJson)));
            Console.WriteLine(JsonSerializer.Serialize(new { output = result }, WorkflowRunner.LogJson));
            return 0;
        }
        catch (GraphError error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error.Code, error.NodeId, error.PortId, error.ParameterId }, WorkflowRunner.LogJson));
            return 2;
        }
        catch (JsonException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { code = "InvalidJson", error.Path }, WorkflowRunner.LogJson));
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { code = "FileReadFailed", error.Message }, WorkflowRunner.LogJson));
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { code = "ExecutionFailed", error.Message }, WorkflowRunner.LogJson));
            return 3;
        }
    }
}
