namespace Glacier.Agent.Tools;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Sandbox;

/// <summary>
/// AOT-safe self-verification tool implementing both <see cref="ITool"/> (Workflows) and
/// <see cref="IVerificationTool"/> / <see cref="IAgentTool"/> (Runtime).
/// </summary>
public sealed class SelfVerificationTool : ITool, IVerificationTool
{
    public const string DefaultToolName = "self_verify";

    private static readonly string CachedSchemaJson = CreateParametersSchema().ToJsonString();

    private readonly IVerificationHook? _hook;
    private readonly VerificationCircuitBreaker? _circuitBreaker;
    private readonly bool _treatHookExceptionAsFailure;

    public string Name { get; }
    public string Description { get; }
    public string ParameterJsonSchema => CachedSchemaJson;
    public VerificationCircuitBreaker? CircuitBreaker => _circuitBreaker;
    public ISandboxExecutor? SandboxExecutor => (_hook as SandboxVerificationHook)?.SandboxExecutor;

    public SelfVerificationTool(
        IVerificationHook? hook = null,
        VerificationCircuitBreaker? circuitBreaker = null,
        string name = DefaultToolName,
        string? description = null,
        bool treatHookExceptionAsFailure = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = string.IsNullOrWhiteSpace(description)
            ? "Verifies a candidate agent output against structural or custom verification checks and returns a structured VerificationResult JSON object."
            : description;
        _hook = hook;
        _circuitBreaker = circuitBreaker;
        _treatHookExceptionAsFailure = treatHookExceptionAsFailure;
    }

    public SelfVerificationTool(
        ISandboxExecutor sandboxExecutor,
        Func<VerificationContext, SandboxOptions> sandboxOptionsFactory,
        Func<SandboxExecutionResult, VerificationContext, VerificationResult>? sandboxResultEvaluator = null,
        VerificationCircuitBreaker? circuitBreaker = null,
        string name = DefaultToolName,
        string? description = null,
        bool treatHookExceptionAsFailure = true)
        : this(
            new SandboxVerificationHook(sandboxExecutor, sandboxOptionsFactory, sandboxResultEvaluator),
            circuitBreaker,
            name,
            description,
            treatHookExceptionAsFailure)
    {
    }

    public static SelfVerificationTool CreateSandboxed(
        Func<VerificationContext, SandboxOptions> sandboxOptionsFactory,
        ISandboxExecutor? sandboxExecutor = null,
        Func<SandboxExecutionResult, VerificationContext, VerificationResult>? sandboxResultEvaluator = null,
        VerificationCircuitBreaker? circuitBreaker = null,
        string name = DefaultToolName,
        string? description = null,
        bool treatHookExceptionAsFailure = true)
        => new(
            sandboxExecutor ?? new OsProcessSandbox(),
            sandboxOptionsFactory,
            sandboxResultEvaluator,
            circuitBreaker,
            name,
            description,
            treatHookExceptionAsFailure);

    public JsonNode GetParametersSchema() => CreateParametersSchema();

    Task<string> ITool.ExecuteAsync(string arguments)
        => ExecuteInternalAsync(arguments, CancellationToken.None).AsTask();

    ValueTask<string> IAgentTool.ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ExecuteInternalAsync(argumentsJson, ct);

    public async ValueTask<VerificationResult> VerifyArgumentsAsync(
        string argumentsJson,
        CancellationToken ct = default)
    {
        if (_circuitBreaker is { IsTripped: true })
        {
            return VerificationResult.Tripped(
                _circuitBreaker.LastFailureReason ?? "Verification circuit breaker is open.",
                _circuitBreaker.ConsecutiveFailures,
                _circuitBreaker.LastResult,
                Name);
        }

        SelfVerificationToolArgs? args;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            args = new SelfVerificationToolArgs();
        }
        else
        {
            try
            {
                args = JsonSerializer.Deserialize(argumentsJson, AdkJsonContext.Default.SelfVerificationToolArgs)
                    ?? new SelfVerificationToolArgs();
            }
            catch (JsonException ex)
            {
                var parseFailure = VerificationResult.Fail(
                    $"Invalid verification tool arguments JSON: {ex.Message}",
                    new[] { new VerificationIssue("invalid_arguments", ex.Message, "arguments") },
                    "Provide a valid JSON object containing an 'output' string.",
                    Name);
                return ApplyCircuitBreaker(parseFailure);
            }
        }

        VerificationResult result;
        if (_hook != null)
        {
            var context = new VerificationContext(
                AgentName: Name,
                OriginalPrompt: args.OriginalPrompt ?? args.Prompt ?? string.Empty,
                CurrentPrompt: args.Prompt ?? string.Empty,
                CandidateOutput: args.Output ?? string.Empty,
                TurnOrStep: args.Turn ?? 1,
                SessionId: args.SessionId,
                ToolCallName: Name,
                ConsecutiveFailures: _circuitBreaker?.ConsecutiveFailures ?? 0);

            try
            {
                result = await _hook.VerifyAsync(context, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.VerifierName))
                {
                    result = result with { VerifierName = _hook.Name };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (!_treatHookExceptionAsFailure)
                {
                    throw;
                }
                result = VerificationResult.Error(ex.Message, _hook.Name);
            }
        }
        else
        {
            result = RunBuiltInChecks(args);
        }

        return ApplyCircuitBreaker(result);
    }

    private async ValueTask<string> ExecuteInternalAsync(string argumentsJson, CancellationToken ct)
    {
        var result = await VerifyArgumentsAsync(argumentsJson, ct).ConfigureAwait(false);
        return result.ToJson();
    }

    private VerificationResult ApplyCircuitBreaker(VerificationResult result)
    {
        if (_circuitBreaker == null)
        {
            return result;
        }

        var state = result.Passed
            ? _circuitBreaker.RecordSuccess(result)
            : _circuitBreaker.RecordFailure(result);
        return state.LastResult ?? result;
    }

    private VerificationResult RunBuiltInChecks(SelfVerificationToolArgs args)
    {
        var issues = new List<VerificationIssue>();
        string output = args.Output ?? string.Empty;

        if (string.IsNullOrWhiteSpace(output))
        {
            issues.Add(new VerificationIssue(
                "empty_output",
                "Candidate output is empty or whitespace.",
                "output",
                "Provide a non-empty response in the 'output' field."));
        }
        else
        {
            bool parseJson = args.RequireValidJson == true || (args.RequiredFields is { Count: > 0 } && args.RequireValidJson != false);
            if (parseJson)
            {
                try
                {
                    using var doc = JsonDocument.Parse(output);
                    if (args.RequiredFields is { Count: > 0 })
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            issues.Add(new VerificationIssue(
                                "expected_json_object",
                                "Output must be a JSON object when requiredFields are specified.",
                                "output",
                                "Return a JSON object containing all required fields."));
                        }
                        else
                        {
                            foreach (var field in args.RequiredFields)
                            {
                                if (!string.IsNullOrWhiteSpace(field) && !doc.RootElement.TryGetProperty(field, out _))
                                {
                                    issues.Add(new VerificationIssue(
                                        "missing_field",
                                        $"Required JSON field '{field}' is missing.",
                                        field,
                                        $"Include '{field}' in the JSON output."));
                                }
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    issues.Add(new VerificationIssue(
                        "invalid_json",
                        $"Output is not valid JSON: {ex.Message}",
                        "output",
                        "Format the output as valid JSON."));
                }
            }

            if (args.RequiredSubstrings is { Count: > 0 })
            {
                foreach (var substring in args.RequiredSubstrings)
                {
                    if (!string.IsNullOrEmpty(substring) && !output.Contains(substring, StringComparison.Ordinal))
                    {
                        issues.Add(new VerificationIssue(
                            "missing_substring",
                            $"Output is missing required text '{substring}'.",
                            "output",
                            $"Ensure the output contains '{substring}'."));
                    }
                }
            }
        }

        if (issues.Count > 0)
        {
            return VerificationResult.Fail(
                issues[0].Message,
                issues,
                issues[0].SuggestedFix,
                Name);
        }

        return VerificationResult.Pass("Self-verification structural checks passed.", Name);
    }

    private static JsonObject CreateParametersSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["output"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Candidate output text or JSON to verify."
                },
                ["prompt"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Current prompt context."
                },
                ["originalPrompt"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Original user prompt."
                },
                ["turn"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Current turn or step index."
                },
                ["sessionId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional session identifier."
                },
                ["requireValidJson"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "When true, verifies that 'output' is valid JSON."
                },
                ["requiredSubstrings"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Substrings that must appear in 'output'."
                },
                ["requiredFields"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "JSON object fields required in 'output'."
                }
            },
            ["required"] = new JsonArray("output")
        };
    }
}
