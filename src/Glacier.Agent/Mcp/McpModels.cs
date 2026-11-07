namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record McpImplementation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record McpCapabilityFlag();

public sealed record McpServerCapabilities(
    [property: JsonPropertyName("tools")] McpCapabilityFlag? Tools = null,
    [property: JsonPropertyName("resources")] McpCapabilityFlag? Resources = null,
    [property: JsonPropertyName("prompts")] McpCapabilityFlag? Prompts = null,
    [property: JsonPropertyName("logging")] McpCapabilityFlag? Logging = null);

public sealed record McpInitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] McpServerCapabilities Capabilities,
    [property: JsonPropertyName("serverInfo")] McpImplementation ServerInfo);

public sealed record McpTool(
    string Name,
    string Description,
    object? InputSchema = null)
{
    public string GetInputSchemaJson()
    {
        if (InputSchema is JsonElement je && je.ValueKind != JsonValueKind.Undefined)
        {
            return je.GetRawText();
        }

        if (InputSchema is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        return """{"type":"object"}""";
    }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("name", Name);
        writer.WriteString("description", Description);
        writer.WritePropertyName("inputSchema");
        if (InputSchema is JsonElement je && je.ValueKind != JsonValueKind.Undefined)
        {
            je.WriteTo(writer);
        }
        else if (InputSchema is string s && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                doc.RootElement.WriteTo(writer);
            }
            catch
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WriteEndObject();
            }
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    internal static McpTool FromJsonElement(JsonElement elem)
    {
        string name = elem.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        string desc = elem.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "";
        object? schema = elem.TryGetProperty("inputSchema", out var s) ? s.Clone() : null;
        return new McpTool(name, desc, schema);
    }
}

public sealed record McpContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null);

public sealed record McpToolCallResult(
    [property: JsonPropertyName("content")] List<McpContent> Content,
    [property: JsonPropertyName("isError")] bool IsError = false)
{
    public static McpToolCallResult Success(string text)
        => new(new List<McpContent> { new("text", text) }, false);

    public static McpToolCallResult Error(string error)
        => new(new List<McpContent> { new("text", error) }, true);
}

public sealed record McpResource(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("mimeType")] string? MimeType = null);

public sealed record McpResourceContent(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("mimeType")] string? MimeType = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("blob")] string? Blob = null);

public sealed record McpPromptArgument(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("required")] bool Required = false);

public sealed record McpPrompt(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("arguments")] List<McpPromptArgument>? Arguments = null);

public sealed record McpPromptMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] McpContent Content);

public sealed record McpError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpImplementation))]
[JsonSerializable(typeof(McpCapabilityFlag))]
[JsonSerializable(typeof(McpServerCapabilities))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpContent))]
[JsonSerializable(typeof(List<McpContent>))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSerializable(typeof(McpResource))]
[JsonSerializable(typeof(List<McpResource>))]
[JsonSerializable(typeof(McpResourceContent))]
[JsonSerializable(typeof(McpResourceContent[]))]
[JsonSerializable(typeof(McpPromptArgument))]
[JsonSerializable(typeof(List<McpPromptArgument>))]
[JsonSerializable(typeof(McpPrompt))]
[JsonSerializable(typeof(List<McpPrompt>))]
[JsonSerializable(typeof(McpPromptMessage))]
[JsonSerializable(typeof(List<McpPromptMessage>))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AgentMcpJsonContext : JsonSerializerContext
{
}
