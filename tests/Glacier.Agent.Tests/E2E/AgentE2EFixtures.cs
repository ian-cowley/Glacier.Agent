using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Tools;
using Glacier.Agent.Workflows;

namespace Glacier.Agent.Tests.E2E;

internal sealed class StubLlmService : ILlmService, IDisposable
{
    private readonly Queue<LlmResponse> _responses;
    private readonly Func<LlmRequest, int, LlmResponse>? _factory;

    public List<LlmRequest> RecordedRequests { get; } = new();
    public int CallCount { get; private set; }

    public StubLlmService(params LlmResponse[] responses)
    {
        _responses = new Queue<LlmResponse>(responses);
    }

    public StubLlmService(Func<LlmRequest, int, LlmResponse> factory)
    {
        _responses = new Queue<LlmResponse>();
        _factory = factory;
    }

    public Task<LlmResponse> GenerateContentAsync(LlmRequest request)
    {
        CallCount++;
        RecordedRequests.Add(request);

        if (_factory != null)
        {
            return Task.FromResult(_factory(request, CallCount));
        }

        if (_responses.Count > 0)
        {
            return Task.FromResult(_responses.Dequeue());
        }

        return Task.FromResult(new LlmResponse { Content = $"fallback-response-{CallCount}" });
    }

    public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
    {
        var response = await GenerateContentAsync(request).ConfigureAwait(false);
        yield return response;
    }

    public void Dispose()
    {
        _responses.Clear();
    }
}

internal sealed class ScriptedWorkflowAgent : IAgent
{
    private readonly Func<string, int, string> _turnHandler;

    public string Name { get; }
    public string Description => "Scripted deterministic workflow member for E2E verification tests.";
    public List<string> ReceivedPrompts { get; } = new();
    public int InvocationCount { get; private set; }

    public ScriptedWorkflowAgent(string name, Func<string, int, string> turnHandler)
    {
        Name = name;
        _turnHandler = turnHandler;
    }

    public Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        InvocationCount++;
        ReceivedPrompts.Add(prompt);
        return Task.FromResult(_turnHandler(prompt, InvocationCount));
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        var output = await RunAsync(prompt, llmService, sessionId).ConfigureAwait(false);
        yield return output;
    }
}

internal sealed class ScriptedVerificationHook : IVerificationHook
{
    private readonly Func<VerificationContext, int, VerificationResult> _handler;

    public string Name { get; }
    public List<VerificationContext> Contexts { get; } = new();
    public int CallCount { get; private set; }

    public ScriptedVerificationHook(string name, Func<VerificationContext, int, VerificationResult> handler)
    {
        Name = name;
        _handler = handler;
    }

    public ValueTask<VerificationResult> VerifyAsync(VerificationContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CallCount++;
        Contexts.Add(context);
        return ValueTask.FromResult(_handler(context, CallCount));
    }
}

internal sealed class ThrowingVerificationTool : IVerificationTool
{
    private readonly Func<int, Exception> _exceptionFactory;

    public string Name { get; }
    public string Description => "Verification tool fixture that throws a configured exception.";
    public string ParameterJsonSchema => "{\"type\":\"object\",\"properties\":{\"output\":{\"type\":\"string\"}}}";
    public int CallCount { get; private set; }

    public ThrowingVerificationTool(string name = "throwing_verifier", Func<int, Exception>? exceptionFactory = null)
    {
        Name = name;
        _exceptionFactory = exceptionFactory ?? (attempt => new InvalidOperationException($"Simulated verification tool failure on attempt {attempt}."));
    }

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CallCount++;
        throw _exceptionFactory(CallCount);
    }
}

internal sealed class ScriptedVerificationAgentTool : IVerificationTool
{
    private readonly Func<string, int, string> _handler;

    public string Name { get; }
    public string Description => "Deterministic IVerificationTool fixture returning structured VerificationResult JSON.";
    public string ParameterJsonSchema => "{\"type\":\"object\",\"properties\":{\"output\":{\"type\":\"string\"}}}";
    public List<string> ReceivedArguments { get; } = new();
    public int CallCount { get; private set; }

    public ScriptedVerificationAgentTool(string name, Func<string, int, string> handler)
    {
        Name = name;
        _handler = handler;
    }

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CallCount++;
        ReceivedArguments.Add(argumentsJson);
        return ValueTask.FromResult(_handler(argumentsJson, CallCount));
    }
}

internal sealed class EngineBridgeWorkflowAgent : IAgent
{
    private readonly Glacier.Agent.Runtime.AgentExecutionEngine _engine;
    private int _step;

    public string Name => _engine.AgentId;
    public string Description => "Bridges AgentExecutionEngine into LoopAgent workflows.";
    public List<AgentResponse> StepResponses { get; } = new();
    public AgentResponse? LastResponse => StepResponses.Count > 0 ? StepResponses[^1] : null;

    public EngineBridgeWorkflowAgent(Glacier.Agent.Runtime.AgentExecutionEngine engine)
    {
        _engine = engine;
    }

    public async Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        string sid = sessionId ?? "bridge-session";
        _step++;
        var response = await _engine.ExecuteStepAsync(new AgentContext(sid, prompt, _step)).ConfigureAwait(false);
        StepResponses.Add(response);
        return response.Output;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        yield return await RunAsync(prompt, llmService, sessionId).ConfigureAwait(false);
    }
}
