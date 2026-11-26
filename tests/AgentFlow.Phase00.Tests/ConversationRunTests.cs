using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgentFlow.Phase00.Tests;

public partial class ModelRunTests
{
    [Theory]
    [InlineData(NodeTypes.Agent)]
    [InlineData(NodeTypes.HarnessAgent)]
    [Trait("Category", "RunHost")]
    public async Task Conversation_RestoresOnlyCommittedAgentHistoryAfterHostRestart(string agentType)
    {
        using var fixture = new TcpListener(IPAddress.Loopback, 0);
        fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var profile = new ModelProfile("conversation-fixture", $"http://{fixture.LocalEndpoint}/v1", "fixture",
            Streaming: false);
        await VerifyConversationRestartAsync(agentType, profile, fixture, deadline.Token);
    }

    [Theory]
    [InlineData(NodeTypes.Agent)]
    [InlineData(NodeTypes.HarnessAgent)]
    [Trait("Category", "LiveModel")]
    public async Task Conversation_RealModelRemembersTheFirstTurnAfterHostRestart(string agentType)
    {
        var profile = new ModelProfile("conversation-live", RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL"),
            RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID"), RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF"),
            TimeoutSeconds: 90, Temperature: 0, MaxTokens: 1024);
        await VerifyConversationRestartAsync(agentType, profile, null, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task Conversation_CollaborationKeepsAgentHistoriesSeparateAcrossRoutingAndRestart()
    {
        using var fixture = new TcpListener(IPAddress.Loopback, 0);
        fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var profile = new ModelProfile("collaboration-fixture", $"http://{fixture.LocalEndpoint}/v1", "fixture", Streaming: false);
        await VerifyConversationCollaborationAsync(profile, fixture, deadline.Token);
    }

    [Fact]
    [Trait("Category", "LiveModel")]
    public async Task Conversation_RealCollaborationRecallsBothAgentsHistoryAfterSkippingAndRestarting()
    {
        var profile = new ModelProfile("collaboration-live", RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL"),
            RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID"), RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF"),
            TimeoutSeconds: 90, Temperature: 0, MaxTokens: 1024);
        await VerifyConversationCollaborationAsync(profile, null, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "LiveModel")]
    public async Task Conversation_RealHarnessUsesTheCurrentDeviceAfterRestartWithToolHistory()
    {
        var (options, client) = CreateConversationHost();
        using var api = client;
        const string profileId = "conversation-device-live";
        var graph = Example("phase07/harness-collaboration");
        SetText(graph, "model", profileId);
        var conversationId = Guid.NewGuid();
        var runs = new List<RunSnapshot>();
        var history = new List<RunEventItem[]>();

        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            await SaveAsync(api, new(profileId, RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL"),
                RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID"), RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF"),
                TimeoutSeconds: 90, Temperature: 0, MaxTokens: 1024));
            await RunAsync("pump-01", "warning");
        }
        finally { StopHost(host); }

        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            await RunAsync("pump-02", "normal");
            var transcript = await GetAsync<ConversationSnapshot>(api, $"api/conversations/{conversationId}");
            Assert.Equal(runs.Select(run => run.RunId), transcript.Turns.Select(turn => turn.RunId));
            await WorkflowTests.EvidenceAsync("phase07-conversation-mcp-qwen.json", new { runs, history, transcript });
        }
        finally { StopHost(host); }

        async Task RunAsync(string deviceId, string expectedState)
        {
            var input = JsonSerializer.Serialize(new { deviceId, needsTool = true });
            var runId = await StartAsync(api, new RunRequest(graph, Guid.NewGuid()) { ConversationId = conversationId, TurnInput = input });
            // 两个串行 Agent 各有 90 秒模型超时，轮询覆盖整个工作流。
            var run = await WaitAsync(api, runId, Terminal, 200);
            var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{runId}/events");
            runs.Add(run);
            history.Add(events);
            await WorkflowTests.EvidenceAsync($"phase07-conversation-mcp-{deviceId}.json", new { run, events });
            Assert.True(run.State == "Succeeded", $"{run.State} ({run.Error})");
            Assert.Equal(conversationId, run.ConversationId);
            Assert.Contains(deviceId, Assert.Single(run.Outputs!).Value.Preview, StringComparison.OrdinalIgnoreCase);
            var request = Assert.Single(events, item => item.Kind == "ToolRequest");
            Assert.Equal(("harness", "devices/read_device"), (request.NodeId, request.Text));
            using var arguments = JsonDocument.Parse(request.Value!.Preview);
            Assert.Equal(deviceId, arguments.RootElement.GetProperty("deviceId").GetString());
            var result = Assert.Single(events, item => item.Kind == "ToolResult");
            Assert.Contains(deviceId, result.Value!.Preview);
            Assert.Contains(expectedState, result.Value.Preview);
        }
    }

    private static async Task VerifyConversationCollaborationAsync(ModelProfile profile, TcpListener? fixture, CancellationToken token)
    {
        var (options, client) = CreateConversationHost();
        using var api = client;
        var graph = Example("phase07/harness-collaboration");
        graph.Edges.RemoveAll(edge => edge.Target == "harness" && edge.Kind == "resource" && edge.TargetPort != "model");
        SetText(graph, "model", profile.Id);
        SetText(graph, "triage", """
            Read the user's JSON. For action "remember", output only the exact value of the handoff field.
            For action "recall", look up the privateCode from the previous user message with action "remember".
            Output exactly RECALL followed by one space and that privateCode. Do not add quotes or explanation.
            """);
        var harness = Assert.Single(graph.Nodes, node => node.TypeId == NodeTypes.HarnessAgent);
        graph.Nodes[graph.Nodes.IndexOf(harness)] = harness with { Config = new("""
            If the current user message is a CEDAR code, remember that exact code and reply only ACK.
            If the current user message starts with RECALL, reply with the CEDAR code from your earlier
            user message. Ignore any ORCHID code in the RECALL message. Output only the requested text.
            """) };
        var conversationId = Guid.NewGuid();
        var privateCode = $"ORCHID-{Guid.NewGuid():N}"[..13].ToUpperInvariant();
        var handoffCode = $"CEDAR-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var firstInput = JsonSerializer.Serialize(new { needsTool = true, action = "remember", privateCode, handoff = handoffCode });
        var skippedInput = """{"needsTool":false,"action":"bypass"}""";
        var recallInput = """{"needsTool":true,"action":"recall"}""";
        var recallHandoff = $"RECALL {privateCode}";
        var requests = new[] { firstInput, skippedInput, recallInput }.Select(text => new RunRequest(graph, Guid.NewGuid())
        {
            ConversationId = conversationId, TurnInput = text
        }).ToArray();
        var runs = new List<RunSnapshot>();
        var history = new List<RunEventItem[]>();

        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            await SaveAsync(api, profile);
            await StartAsync(api, requests[0]);
            if (fixture is not null)
            {
                var triage = await ConversationModelReplyAsync(fixture, handoffCode, token);
                Assert.Equal(firstInput, Assert.Single(triage, message => message.Role == "user").Content);
                var dispatched = await ConversationModelReplyAsync(fixture, "ACK", token);
                Assert.Equal(handoffCode, Assert.Single(dispatched, message => message.Role == "user").Content);
                Assert.DoesNotContain(dispatched, message => message.Content?.Contains(privateCode, StringComparison.Ordinal) == true);
            }
            await RecordAsync(requests[0].RequestId);
            Assert.Equal("ACK", runs[0].Output.Trim());
            Assert.Equal(handoffCode, Handoff(0));

            await StartAsync(api, requests[1]);
            await RecordAsync(requests[1].RequestId);
            Assert.Equal("No live device check requested.", runs[1].Output);
            Assert.DoesNotContain(history[1], item => item.Kind == "ModelRequest");
            Assert.Contains(history[1], item => item is { Kind: "NodeSkipped", NodeId: "triage" });
            Assert.Contains(history[1], item => item is { Kind: "NodeSkipped", NodeId: "harness" });
        }
        finally { StopHost(host); }

        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            await StartAsync(api, requests[2]);
            if (fixture is not null)
            {
                var triage = await ConversationModelReplyAsync(fixture, recallHandoff, token);
                Assert.Equal(new[] { firstInput, recallInput }, triage.Where(item => item.Role == "user").Select(item => item.Content));
                Assert.Equal(handoffCode, Assert.Single(triage, item => item.Role == "assistant").Content);
                var dispatched = await ConversationModelReplyAsync(fixture, handoffCode, token);
                Assert.Equal(new[] { handoffCode, recallHandoff }, dispatched.Where(item => item.Role == "user").Select(item => item.Content));
                Assert.Equal("ACK", Assert.Single(dispatched, item => item.Role == "assistant").Content);
            }
            await RecordAsync(requests[2].RequestId);
            var transcript = await GetAsync<ConversationSnapshot>(api, $"api/conversations/{conversationId}");
            await WorkflowTests.EvidenceAsync(fixture is null ? "phase07-conversation-collaboration-qwen.json" :
                "phase07-conversation-collaboration-fixture.json", new { runs, history, transcript });
            Assert.Equal(recallHandoff, Handoff(2));
            Assert.Equal(handoffCode, runs[2].Output.Trim());
            Assert.Equal(requests.Select(request => request.RequestId), transcript.Turns.Select(turn => turn.RunId));
            Assert.Equal(requests.Select(request => request.TurnInput), transcript.Turns.Select(turn => turn.Input));

            if (fixture is not null)
            {
                var other = requests[2] with { RequestId = Guid.NewGuid(), ConversationId = Guid.NewGuid() };
                await StartAsync(api, other);
                var triage = await ConversationModelReplyAsync(fixture, "UNKNOWN", token);
                Assert.Equal(recallInput, Assert.Single(triage, item => item.Role == "user").Content);
                Assert.DoesNotContain(triage, item => item.Role == "assistant");
                var dispatched = await ConversationModelReplyAsync(fixture, "No history", token);
                Assert.Equal("UNKNOWN", Assert.Single(dispatched, item => item.Role == "user").Content);
                Assert.DoesNotContain(dispatched, item => item.Role == "assistant");
                var final = await WaitAsync(api, other.RequestId, Terminal);
                Assert.Equal("No history", final.Output);
            }
        }
        finally { StopHost(host); }

        async Task RecordAsync(Guid runId)
        {
            var run = await WaitAsync(api, runId, Terminal, 100);
            Assert.True(run.State == "Succeeded", $"{run.State} ({run.Error})");
            Assert.Equal(conversationId, run.ConversationId);
            runs.Add(run);
            history.Add(await GetAsync<RunEventItem[]>(api, $"api/runs/{runId}/events"));
        }

        string? Handoff(int turn) => Assert.Single(history[turn],
            item => item is { Kind: "NodeStarted", NodeId: "harness" }).Value?.Preview.Trim();
    }

    private static async Task VerifyConversationRestartAsync(string agentType, ModelProfile profile, TcpListener? fixture,
        CancellationToken token)
    {
        var (options, client) = CreateConversationHost();
        using var api = client;

        var selected = Graph("Template input", profile.Id);
        var agent = Assert.Single(selected.Nodes, node => node.TypeId == NodeTypes.Agent);
        selected.Nodes[selected.Nodes.IndexOf(agent)] = agent with { TypeId = agentType };
        selected.Nodes.Add(new("side-output", NodeTypes.Output, 1, new()));
        selected.Edges.Add(new("input-side-output", "input", "text", "side-output", "value", "message"));
        var conversationId = Guid.NewGuid();
        var codeword = $"MAPLE-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var first = new RunRequest(selected, Guid.NewGuid())
        {
            ConversationId = conversationId,
            TurnInput = $"Remember this exact codeword for my next turn: {codeword}. Reply only OK.",
            ReplyOutputId = "output"
        };
        var second = first with { RequestId = Guid.NewGuid(), TurnInput = "What exact codeword did I ask you to remember? Reply only with it." };
        var path = $"api/conversations/{conversationId}";
        RunSnapshot before;
        RunSnapshot after;
        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            await SaveAsync(api, profile);
            using var invalid = await api.PostAsJsonAsync("api/runs", first with { ReplyOutputId = null });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("ConversationReplyOutputRequired", await invalid.Content.ReadAsStringAsync());

            await StartAsync(api, first);
            if (fixture is not null)
            {
                try
                {
                    Assert.Equal(first.RequestId, await StartAsync(api, first));
                    using var busy = await api.PostAsJsonAsync("api/runs", second);
                    Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
                    Assert.Contains("ConversationBusy", await busy.Content.ReadAsStringAsync());
                }
                finally
                {
                    var received = await ConversationModelReplyAsync(fixture, "OK", token);
                    Assert.Equal(("user", first.TurnInput), Assert.Single(received, message => message.Role == "user"));
                }
            }
            before = await WaitAsync(api, first.RequestId, Terminal, 100);
            Assert.True(before.State == "Succeeded", $"{before.State} ({before.Error})");
            Assert.Equal(conversationId, before.ConversationId);
            Assert.Equal(first.TurnInput, Assert.Single(before.Outputs!, item => item.NodeId == "side-output").Value.Preview);
            Assert.Equal(first.RequestId, Assert.Single((await GetAsync<ConversationSnapshot>(api, path)).Turns).RunId);
        }
        finally { StopHost(host); }

        // 模拟进程在保存运行终态后、发布已准备轮次前退出。
        var conversationFile = Path.Combine(options.Environment["AGENTFLOW_RUN_DATA"]!, "conversations", $"{conversationId:N}.json");
        File.Move(conversationFile, Path.ChangeExtension(conversationFile, ".pending"));

        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            Assert.Equal(first.RequestId, Assert.Single((await GetAsync<ConversationSnapshot>(api, path)).Turns).RunId);
            using var drift = await api.PostAsJsonAsync("api/runs", second with
            {
                Workflow = selected with { Revision = selected.Revision + 1 }
            });
            Assert.Equal(HttpStatusCode.Conflict, drift.StatusCode);
            Assert.Contains("ConversationIncompatible", await drift.Content.ReadAsStringAsync());

            if (fixture is not null)
            {
                var failed = second with { RequestId = Guid.NewGuid(), TurnInput = "Discard this failed turn." };
                await StartAsync(api, failed);
                await ConversationModelReplyAsync(fixture, null, token);
                Assert.Equal("Failed", (await WaitAsync(api, failed.RequestId, Terminal)).State);
                var empty = second with { RequestId = Guid.NewGuid(), TurnInput = "Discard this empty reply." };
                await StartAsync(api, empty);
                await ConversationModelReplyAsync(fixture, " ", token);
                var emptyRun = await WaitAsync(api, empty.RequestId, Terminal);
                Assert.Equal("Failed", emptyRun.State);
                Assert.Equal("ConversationReplyMissing", emptyRun.Error);
                Assert.Single((await GetAsync<ConversationSnapshot>(api, path)).Turns);
            }
            await StartAsync(api, second);
            if (fixture is not null)
            {
                var received = await ConversationModelReplyAsync(fixture, codeword, token);
                Assert.Equal(new[] { first.TurnInput, second.TurnInput }, received.Where(message => message.Role == "user").Select(message => message.Content));
                Assert.Equal("OK", Assert.Single(received, message => message.Role == "assistant").Content);
            }
            after = await WaitAsync(api, second.RequestId, Terminal, 100);
            Assert.True(after.State == "Succeeded", $"{after.State} ({after.Error})");
            Assert.Equal(conversationId, after.ConversationId);
            Assert.NotEqual(before.RunId, after.RunId);
            var transcript = await GetAsync<ConversationSnapshot>(api, path);
            Assert.Equal("output", transcript.ReplyOutputId);
            Assert.Equal(new[] { first.RequestId, second.RequestId }, transcript.Turns.Select(turn => turn.RunId));
            Assert.Equal(new[] { first.TurnInput, second.TurnInput }, transcript.Turns.Select(turn => turn.Input));
            Assert.Equal(codeword, transcript.Turns[1].Reply.Trim());
            Assert.Equal(transcript.Turns[1].Reply, Assert.Single(after.Outputs!, item => item.NodeId == "output").Value.Preview);
            if (fixture is null)
                await WorkflowTests.EvidenceAsync($"phase07-conversation-{agentType}-qwen.json", new { before, after, transcript });
        }
        finally { StopHost(host); }

    }

    private static (ProcessStartInfo Options, HttpClient Api) CreateConversationHost()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AgentFlow.slnx")))
            root = root.Parent ?? throw new DirectoryNotFoundException("AgentFlow.slnx");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(root.FullName, "src", "AgentFlow.NodeHost", "bin", configuration, "net10.0", "AgentFlow.NodeHost.dll");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var address = $"http://{reservation.LocalEndpoint}";
        var options = new ProcessStartInfo("dotnet") { WorkingDirectory = root.FullName, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { dll, "--urls", address })
            options.ArgumentList.Add(argument);
        options.Environment["AGENTFLOW_RUN_DATA"] = Path.Combine(root.FullName, "work", "phase07-conversation-" + Guid.NewGuid().ToString("N"));
        return (options, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(8) });
    }

    private static Process StartConversationHost(ProcessStartInfo options)
    {
        var process = Process.Start(options) ?? throw new InvalidOperationException("Could not start conversation test host.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<(string Role, string? Content)[]> ConversationModelReplyAsync(TcpListener listener, string? answer,
        CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
        var length = 0;
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.True(length > 0);
        var payload = new char[length];
        Assert.Equal(length, await reader.ReadBlockAsync(payload, token));
        using var json = JsonDocument.Parse(Encoding.Latin1.GetBytes(payload));
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().Select(message =>
        {
            message.TryGetProperty("content", out var content);
            return (message.GetProperty("role").GetString()!, content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString())),
                _ => null
            });
        }).ToArray();
        var body = answer is null
            ? """{"error":{"message":"fixture failure","type":"fixture_failure"}}"""
            : JsonSerializer.Serialize(new
            {
                id = "conversation-completion", @object = "chat.completion", created = 1, model = "fixture",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = "stop" } }
            });
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(answer is null ? 400 : 200)} Test\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        return messages;
    }
}
