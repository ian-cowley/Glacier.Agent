namespace Glacier.Agent.Llm;

using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Glacier.Agent.Tools;

public interface ILlmService
{
    Task<LlmResponse> GenerateContentAsync(LlmRequest request);
    IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request);
}

public class LlmRequest
{
    public string Model { get; set; } = string.Empty;
    public LlmContent? SystemInstruction { get; set; }
    public List<LlmContent> Contents { get; set; } = new();
    public List<ITool> Tools { get; set; } = new();
}

public class LlmContent
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public List<LlmPart> Parts { get; set; } = new();

    public static LlmContent User(string text) => new() { Role = "user", Parts = { new LlmPart { Text = text } } };
    public static LlmContent Model(string text) => new() { Role = "model", Parts = { new LlmPart { Text = text } } };
}

public class LlmPart
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("functionCall")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmFunctionCall? FunctionCall { get; set; }

    [JsonPropertyName("functionResponse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmFunctionResponse? FunctionResponse { get; set; }
}

public class LlmFunctionCall
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public JsonNode? Args { get; set; }
}

public class LlmFunctionResponse
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("response")]
    public JsonNode? Response { get; set; }
}

public class LlmUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}

public class LlmResponse
{
    public string? Content { get; set; }
    public List<LlmFunctionCall>? FunctionCalls { get; set; }
    public LlmUsage? Usage { get; set; }
}
