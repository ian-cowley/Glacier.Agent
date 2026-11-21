using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Glacier.Agent.Sandbox;

namespace Glacier.Agent.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(List<LlmContent>))]
[JsonSerializable(typeof(LlmContent))]
[JsonSerializable(typeof(LlmPart))]
[JsonSerializable(typeof(LlmFunctionCall))]
[JsonSerializable(typeof(LlmFunctionResponse))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(VerificationStatus))]
[JsonSerializable(typeof(CircuitBreakerStatus))]
[JsonSerializable(typeof(VerificationIssue))]
[JsonSerializable(typeof(List<VerificationIssue>))]
[JsonSerializable(typeof(VerificationResult))]
[JsonSerializable(typeof(List<VerificationResult>))]
[JsonSerializable(typeof(VerificationContext))]
[JsonSerializable(typeof(VerificationCircuitBreakerState))]
[JsonSerializable(typeof(VerificationEventPayload))]
[JsonSerializable(typeof(ToolErrorPayload))]
[JsonSerializable(typeof(VerificationFeedbackPayload))]
[JsonSerializable(typeof(SelfVerificationToolArgs))]
[JsonSerializable(typeof(SandboxOptions))]
[JsonSerializable(typeof(SandboxExecutionResult))]
[JsonSerializable(typeof(List<string>))]
public partial class AdkJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(List<LlmContent>))]
[JsonSerializable(typeof(VerificationResult))]
[JsonSerializable(typeof(SandboxExecutionResult))]
public partial class AdkIndentedJsonContext : JsonSerializerContext
{
}
