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

        // Register custom tool
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
}
