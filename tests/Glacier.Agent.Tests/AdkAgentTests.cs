namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Workflows;
using Glacier.Agent.Tools;
using Glacier.Agent.Llm;
using Glacier.Agent.Tests.Mocks;
using Xunit;

public class AdkAgentTests
{
    [Fact]
    public async Task LlmAgent_SingleTurn_ReturnsExpectedContent()
    {
        var mock = new TestLlmService("Hello from mock LLM!");
        var agent = new LlmAgent("TestAgent", "test-model", "Test agent description", "You are a test assistant.", new List<ITool>());

        var response = await agent.RunAsync("Hello!", mock);

        Assert.Equal("Hello from mock LLM!", response);
        Assert.Single(mock.CapturedRequests);
        var req = mock.CapturedRequests[0];
        Assert.Equal("test-model", req.Model);
        Assert.Equal("You are a test assistant.", req.SystemInstruction?.Parts[0].Text);
    }

    [Fact]
    public async Task LlmAgent_ToolExecutionLoop_ExecutesFunctionAndReturnsAnswer()
    {
        int callCount = 0;
        var mock = new TestLlmService(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                // First turn: requests function call
                return new LlmResponse
                {
                    FunctionCalls = new List<LlmFunctionCall>
                    {
                        new LlmFunctionCall
                        {
                            Name = "get_weather",
                            Args = JsonNode.Parse("{\"city\": \"Seattle\"}")
                        }
                    }
                };
            }

            // Second turn: returns final text
            return new LlmResponse { Content = "The weather in Seattle is sunny and 72F." };
        });

        var tool = new FunctionTool<WeatherArgs, string>(
            "get_weather",
            "Gets current weather",
            args => $"Sunny, 72F in {args.City}");

        var agent = new LlmAgent("WeatherAgent", "test-model", "Weather bot", "Assist with weather", new List<ITool> { tool });

        var result = await agent.RunAsync("What's the weather in Seattle?", mock);

        Assert.Equal("The weather in Seattle is sunny and 72F.", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task SequentialAgent_ExecutesMultiStagePipeline()
    {
        var mock = new TestLlmService(req =>
        {
            var prompt = req.Contents.Last().Parts[0].Text ?? "";
            return new LlmResponse { Content = $"[{prompt} -> Processed]" };
        });

        var agent1 = new LlmAgent("Stage1", "m1", "First stage", "Instruction 1", new List<ITool>());
        var agent2 = new LlmAgent("Stage2", "m2", "Second stage", "Instruction 2", new List<ITool>());

        var seq = new SequentialAgent("Pipeline", "Sequential pipeline", new[] { agent1, agent2 });

        var output = await seq.RunAsync("InitialData", mock);

        Assert.Equal("[[InitialData -> Processed] -> Processed]", output);
    }

    [Fact]
    public async Task ParallelAgent_RunsSubAgentsConcurrently()
    {
        var mock = new TestLlmService(req =>
        {
            var agentName = req.SystemInstruction?.Parts[0].Text ?? "Unknown";
            return new LlmResponse { Content = $"Output from {agentName}" };
        });

        var agentA = new LlmAgent("AgentA", "mA", "Worker A", "Instruction A", new List<ITool>());
        var agentB = new LlmAgent("AgentB", "mB", "Worker B", "Instruction B", new List<ITool>());

        var parallel = new ParallelAgent("ParallelTeam", "Runs A and B", new[] { agentA, agentB });

        var output = await parallel.RunAsync("Task", mock);

        Assert.Contains("Result from AgentA:", output);
        Assert.Contains("Output from Instruction A", output);
        Assert.Contains("Result from AgentB:", output);
        Assert.Contains("Output from Instruction B", output);
    }

    [Fact]
    public async Task LoopAgent_IteratesUntilTerminationOrMaxSteps()
    {
        int count = 0;
        var mock = new TestLlmService(req =>
        {
            count++;
            return new LlmResponse { Content = count >= 3 ? "DONE: Finished work" : $"Step {count}" };
        });

        var innerAgent = new LlmAgent("LoopWorker", "mW", "Worker", "Iterate", new List<ITool>());
        var loop = new LoopAgent("RefinementLoop", "Loops until DONE", innerAgent, maxTurns: 5, stopCondition: s => s.Contains("DONE"));

        var output = await loop.RunAsync("Start", mock);

        Assert.Equal("DONE: Finished work", output);
        Assert.Equal(3, count);
    }

    public class WeatherArgs
    {
        public string City { get; set; } = string.Empty;
    }
}
