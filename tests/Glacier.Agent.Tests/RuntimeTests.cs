namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Runtime;
using Xunit;

public sealed class RuntimeTests
{
    private sealed class TestActor : AgentActor
    {
        public readonly List<AgentMessage> Received = new();
        public readonly TaskCompletionSource<bool> MessageReceivedSignal = new();

        public TestActor(string id) : base(id) { }

        protected override Task OnMessageAsync(AgentMessage msg, CancellationToken ct)
        {
            Received.Add(msg);
            MessageReceivedSignal.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AgentActor_MailboxMessagePassing_Succeeds()
    {
        using var actor = new TestActor("actor-1");
        actor.Start();

        var msg = AgentMessage.Create("sender-0", "actor-1", "ping", "hello world");
        await actor.SendAsync(msg);

        await actor.MessageReceivedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Single(actor.Received);
        Assert.Equal("ping", actor.Received[0].Type);
        Assert.Equal("hello world", actor.Received[0].Payload);
    }

    [Fact]
    public async Task MultiAgentSwarm_RoutesMessagesBetweenAgents()
    {
        using var swarm = new MultiAgentSwarm("test-swarm");
        var planner = new TestActor("planner");
        var coder = new TestActor("coder");

        swarm.RegisterActor(planner);
        swarm.RegisterActor(coder);

        var msg = AgentMessage.Create("planner", "coder", "task", "generate code");
        await swarm.SendToAsync("coder", msg);

        await coder.MessageReceivedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Single(coder.Received);
        Assert.Equal("task", coder.Received[0].Type);
    }

    [Fact]
    public async Task AgentExecutionEngine_ExecutesTool_AndPublishesEvents()
    {
        using var engine = new AgentExecutionEngine("agent-alpha");
        var publishedEvents = new List<AgentEvent>();
        engine.EventPublished += evt => publishedEvents.Add(evt);

        var calculatorTool = DelegateAgentTool.Create(
            name: "calc",
            description: "Evaluates simple math",
            parameterJsonSchema: """{"type": "object", "properties": {"expr": {"type": "string"}}}""",
            handler: args => "42");

        engine.RegisterTool(calculatorTool);

        var context = new AgentContext("session-01", "CALL calc: {\"expr\": \"6*7\"}", 1);
        AgentResponse response = await engine.ExecuteStepAsync(context);

        Assert.False(response.IsCompleted);
        Assert.Equal("calc", response.ToolCallName);
        Assert.Equal("42", response.Output);

        Assert.True(publishedEvents.Count >= 3);
        Assert.Contains(publishedEvents, e => e.EventType == "ToolInvoking" && e.Payload == "calc");
        Assert.Contains(publishedEvents, e => e.EventType == "ToolCompleted");
    }

    private sealed class MockChatModel : IAgentChatModel
    {
        public ValueTask<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            if (userPrompt.Contains("<|tool_result|>", StringComparison.Ordinal))
            {
                return ValueTask.FromResult("The answer is 42.");
            }
            return ValueTask.FromResult("""{"tool":"calc","arguments":{"expr":"6*7"}}""");
        }
    }

    [Fact]
    public async Task AgentExecutionEngine_WithChatModel_ExecutesMultiStepJsonToolLoop()
    {
        using var engine = new AgentExecutionEngine("agent-llm", new MockChatModel());
        engine.RegisterTool(DelegateAgentTool.Create("calc", "Math", "{}", _ => "42"));

        AgentResponse final = await engine.ExecuteLoopAsync("sess-loop", "What is 6*7?", maxSteps: 4);
        Assert.True(final.IsCompleted);
        Assert.Equal("The answer is 42.", final.Output);
    }

    [Fact]
    public async Task AgentExecutionEngine_WithEpisodicMemory_RecallsAndStoresTraces()
    {
        var memory = new Glacier.MemEx.MemExEngine();
        memory.StoreTrace(new Glacier.MemEx.Models.EpisodicTrace
        {
            Id = "mem-1",
            Content = "User prefers metric units.",
            BaseImportance = 4.0f
        });

        string? observedPrompt = null;
        using var engine = new AgentExecutionEngine("agent-mem", (ctx, _, _) =>
        {
            observedPrompt = ctx.Prompt;
            return ValueTask.FromResult(new AgentStepDecision("21C", IsCompleted: true));
        });
        engine.AttachEpisodicMemory(memory);

        var res = await engine.ExecuteLoopAsync("sess-mem", "What is the temperature?", maxSteps: 2);
        Assert.True(res.IsCompleted);
        Assert.NotNull(observedPrompt);
        Assert.Contains("[EPISODIC MEMORY]", observedPrompt);
        Assert.Contains("metric units", observedPrompt);
        Assert.Equal(2, memory.Count);
    }

    [Fact]
    public async Task AgentExecutionEngine_WithConformalRouter_EscalatesOrFastDispatches()
    {
        using var clavier = new Glacier.Clavier.Engine.ClavierSession(embeddingDim: 16, hiddenDim: 16, numActions: 2);
        using var engine = new AgentExecutionEngine("agent-clavier", new MockChatModel());
        engine.RegisterTool(DelegateAgentTool.Create("calc", "Math", "{}", _ => "42"));
        engine.RegisterTool(DelegateAgentTool.Create("search", "Search", "{}", _ => "found"));
        engine.AttachConformalRouter(clavier, conformalAlpha: 0.05f);

        var events = new List<string>();
        engine.EventPublished += ev => events.Add(ev.EventType);

        AgentResponse final = await engine.ExecuteLoopAsync("sess-clavier", "What is 6*7?", maxSteps: 4);
        Assert.True(final.IsCompleted);
        Assert.True(events.Contains("ConformalFastDispatch") || events.Contains("ConformalEscalated"));
    }
}
