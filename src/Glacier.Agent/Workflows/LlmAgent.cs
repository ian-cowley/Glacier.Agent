using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Glacier.Agent.Workflows;

public class LlmAgent : IAgent
{
    public string Name { get; }
    public string Model { get; }
    public string Description { get; }
    public string Instruction { get; }
    public List<ITool> Tools { get; }
    public List<LlmContent> History { get; } = new();

    public IApprovalService? ApprovalService { get; set; }
    public Func<ITool, string, Task<string>>? BeforeToolCall { get; set; }
    public int MaxRetries { get; set; } = 3;

    private int _currentRetryCount;
    private readonly ISessionProvider? _sessionProvider;

    public LlmAgent(
        string name,
        string model,
        string description,
        string instruction,
        List<ITool>? tools = null,
        ISessionProvider? sessionProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        Name = name;
        Model = model;
        Description = description ?? string.Empty;
        Instruction = instruction ?? string.Empty;
        Tools = tools ?? new List<ITool>();
        _sessionProvider = sessionProvider;
    }

    public virtual Task OnBeforeModelInvokeAsync(LlmRequest request) => Task.CompletedTask;
    public virtual Task OnAfterModelInvokeAsync(LlmResponse response) => Task.CompletedTask;
    public virtual Task OnBeforeToolInvokeAsync(ITool tool, string arguments) => Task.CompletedTask;
    public virtual Task OnAfterToolInvokeAsync(ITool tool, string result) => Task.CompletedTask;

    public async Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(llmService);

        using var activity = Telemetry.ActivitySource.StartActivity($"Run {Name}");
        activity?.SetTag("agent.name", Name);
        activity?.SetTag("agent.model", Model);
        activity?.SetTag("session.id", sessionId);

        Telemetry.AgentRunsCounter.Add(1, new KeyValuePair<string, object?>("agent", Name));

        await LoadHistoryAsync(sessionId).ConfigureAwait(false);

        var userMessage = LlmContent.User(prompt);
        History.Add(userMessage);
        _currentRetryCount = 0;
        await SaveMessageAsync(sessionId, userMessage).ConfigureAwait(false);

        while (true)
        {
            var request = CreateRequest();
            await OnBeforeModelInvokeAsync(request).ConfigureAwait(false);

            var response = await llmService.GenerateContentAsync(request).ConfigureAwait(false);

            await OnAfterModelInvokeAsync(response).ConfigureAwait(false);

            if (response.FunctionCalls?.Count > 0)
            {
                var errorCall = response.FunctionCalls.FirstOrDefault(c => c.Args?["error"] != null);
                if (errorCall != null && _currentRetryCount < MaxRetries)
                {
                    _currentRetryCount++;
                    var errorMsg = errorCall.Args?["error"]?.ToString();
                    var feedback = LlmContent.User($"System: Your last tool call was malformed. Error: {errorMsg}. Please try again and provide ONLY valid JSON arguments.");
                    History.Add(feedback);
                    await SaveMessageAsync(sessionId, feedback).ConfigureAwait(false);
                    continue;
                }

                await HandleToolCallsAsync(response.FunctionCalls, sessionId).ConfigureAwait(false);
                continue;
            }

            if (!string.IsNullOrEmpty(response.Content))
            {
                var modelMessage = LlmContent.Model(response.Content);
                History.Add(modelMessage);
                await SaveMessageAsync(sessionId, modelMessage).ConfigureAwait(false);
                return response.Content;
            }

            break;
        }

        return "No response generated.";
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(llmService);

        using var activity = Telemetry.ActivitySource.StartActivity($"Stream {Name}");
        activity?.SetTag("agent.name", Name);
        activity?.SetTag("session.id", sessionId);

        Telemetry.AgentRunsCounter.Add(1, new KeyValuePair<string, object?>("agent", Name));

        await LoadHistoryAsync(sessionId).ConfigureAwait(false);

        var userMessage = LlmContent.User(prompt);
        History.Add(userMessage);
        _currentRetryCount = 0;
        await SaveMessageAsync(sessionId, userMessage).ConfigureAwait(false);

        while (true)
        {
            var request = CreateRequest();
            await OnBeforeModelInvokeAsync(request).ConfigureAwait(false);

            var fullContentBuilder = new StringBuilder();
            List<LlmFunctionCall>? toolCalls = null;

            await foreach (var chunk in llmService.StreamGenerateContentAsync(request).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    fullContentBuilder.Append(chunk.Content);
                    yield return chunk.Content;
                }

                if (chunk.FunctionCalls?.Count > 0)
                {
                    toolCalls ??= new List<LlmFunctionCall>();
                    toolCalls.AddRange(chunk.FunctionCalls);
                }
            }

            var fullContent = fullContentBuilder.ToString();
            await OnAfterModelInvokeAsync(new LlmResponse { Content = fullContent, FunctionCalls = toolCalls }).ConfigureAwait(false);

            if (toolCalls?.Count > 0)
            {
                var errorCall = toolCalls.FirstOrDefault(c => c.Args?["error"] != null);
                if (errorCall != null && _currentRetryCount < MaxRetries)
                {
                    _currentRetryCount++;
                    var errorMsg = errorCall.Args?["error"]?.ToString();
                    var feedback = LlmContent.User($"System: Your last tool call was malformed. Error: {errorMsg}. Please try again and provide ONLY valid JSON arguments.");
                    History.Add(feedback);
                    await SaveMessageAsync(sessionId, feedback).ConfigureAwait(false);
                    continue;
                }

                await HandleToolCallsAsync(toolCalls, sessionId).ConfigureAwait(false);
                continue;
            }

            if (!string.IsNullOrEmpty(fullContent))
            {
                var modelMessage = LlmContent.Model(fullContent);
                History.Add(modelMessage);
                await SaveMessageAsync(sessionId, modelMessage).ConfigureAwait(false);
            }

            break;
        }
    }

    private LlmRequest CreateRequest()
    {
        return new LlmRequest
        {
            Model = Model,
            SystemInstruction = string.IsNullOrEmpty(Instruction) ? null : LlmContent.Model(Instruction),
            Tools = Tools,
            Contents = History
        };
    }

    private async Task HandleToolCallsAsync(List<LlmFunctionCall> toolCalls, string? sessionId)
    {
        using var toolActivity = Telemetry.ActivitySource.StartActivity($"ToolCalls {Name}");

        var responseParts = new List<LlmPart>(toolCalls.Count);
        var toolResultParts = new List<LlmPart>(toolCalls.Count);

        foreach (var call in toolCalls)
        {
            responseParts.Add(new LlmPart { FunctionCall = call });

            var tool = Tools.FirstOrDefault(t => t.Name == call.Name);
            string result;

            if (tool is null)
            {
                result = BuildErrorJson($"Unknown tool '{call.Name}'.");
            }
            else
            {
                using var callActivity = Telemetry.ActivitySource.StartActivity($"Invoke {tool.Name}");
                Telemetry.ToolExecutionCounter.Add(1, new KeyValuePair<string, object?>("tool", tool.Name));

                try
                {
                    var arguments = call.Args?.ToJsonString() ?? "{}";

                    // 1. Interceptor / Guardrail
                    if (BeforeToolCall != null)
                    {
                        arguments = await BeforeToolCall(tool, arguments).ConfigureAwait(false);
                    }

                    // 2. Fail-Closed HITL Approval Gate for SensitiveTool
                    if (tool is SensitiveTool sensitiveTool)
                    {
                        var approvalSvc = ApprovalService ?? sensitiveTool.ApprovalService;
                        if (approvalSvc is null)
                        {
                            callActivity?.SetTag("status", "denied");
                            result = BuildErrorJson($"Sensitive tool '{tool.Name}' requires a configured IApprovalService; execution denied.");
                        }
                        else
                        {
                            var approved = await approvalSvc.ApproveAsync(tool.Name, arguments).ConfigureAwait(false);
                            if (!approved)
                            {
                                callActivity?.SetTag("status", "rejected");
                                result = BuildErrorJson("Action rejected by user.");
                            }
                            else
                            {
                                await OnBeforeToolInvokeAsync(tool, arguments).ConfigureAwait(false);
                                result = await sensitiveTool.ExecuteApprovedAsync(arguments).ConfigureAwait(false);
                                await OnAfterToolInvokeAsync(tool, result).ConfigureAwait(false);
                                callActivity?.SetTag("status", "success");
                            }
                        }
                    }
                    else
                    {
                        await OnBeforeToolInvokeAsync(tool, arguments).ConfigureAwait(false);
                        result = await tool.ExecuteAsync(arguments).ConfigureAwait(false);
                        await OnAfterToolInvokeAsync(tool, result).ConfigureAwait(false);
                        callActivity?.SetTag("status", "success");
                    }
                }
                catch (Exception ex)
                {
                    callActivity?.SetTag("status", "error");
                    callActivity?.SetTag("error", ex.Message);
                    result = BuildErrorJson(ex.Message);
                }
            }

            toolResultParts.Add(new LlmPart
            {
                FunctionResponse = new LlmFunctionResponse
                {
                    Id = call.Id,
                    Name = call.Name,
                    Response = TryParseOrWrap(result)
                }
            });
        }

        var modelTurn = new LlmContent { Role = "model", Parts = responseParts };
        var functionTurn = new LlmContent { Role = "function", Parts = toolResultParts };

        History.Add(modelTurn);
        History.Add(functionTurn);

        await SaveMessageAsync(sessionId, modelTurn).ConfigureAwait(false);
        await SaveMessageAsync(sessionId, functionTurn).ConfigureAwait(false);
    }

    private async Task LoadHistoryAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || _sessionProvider == null) return;
        if (History.Count > 0) return; // Already loaded or manually set

        var history = await _sessionProvider.GetHistoryAsync(sessionId).ConfigureAwait(false);
        History.AddRange(history);
    }

    private async Task SaveMessageAsync(string? sessionId, LlmContent message)
    {
        if (string.IsNullOrEmpty(sessionId) || _sessionProvider == null) return;
        await _sessionProvider.SaveMessageAsync(sessionId, message).ConfigureAwait(false);
    }

    private static string BuildErrorJson(string message)
    {
        return new JsonObject
        {
            ["status"] = "error",
            ["message"] = message
        }.ToJsonString();
    }

    private static JsonNode TryParseOrWrap(string? result)
    {
        if (!string.IsNullOrEmpty(result))
        {
            try
            {
                var parsed = JsonNode.Parse(result);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch
            {
                // Not valid JSON; wrap as string property below.
            }
        }

        return new JsonObject
        {
            ["result"] = result ?? string.Empty
        };
    }
}
