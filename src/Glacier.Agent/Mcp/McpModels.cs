namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed record McpImplementation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record McpServerCapabilities(
    [property: JsonPropertyName("tools")] object? Tools = null,
    [property: JsonPropertyName("resources")] object? Resources = null,
    [property: JsonPropertyName("prompts")] object? Prompts = null,
    [property: JsonPropertyName("logging")] object? Logging = null);

public sealed record McpInitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] McpServerCapabilities Capabilities,
    [property: JsonPropertyName("serverInfo")] McpImplementation ServerInfo);

public sealed record McpTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] object InputSchema);

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
    [property: JsonPropertyName("data")] object? Data = null);
