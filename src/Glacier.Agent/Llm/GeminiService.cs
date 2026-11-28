using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Serialization;

namespace Glacier.Agent.Llm;

public sealed class GeminiGenerateResponse
{
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }

    [JsonPropertyName("usageMetadata")]
    public GeminiUsageMetadata? UsageMetadata { get; set; }
}

public sealed class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContentDto? Content { get; set; }
}

public sealed class GeminiContentDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public List<GeminiPartDto>? Parts { get; set; }
}

public sealed class GeminiPartDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("functionCall")]
    public GeminiFunctionCallDto? FunctionCall { get; set; }
}

public sealed class GeminiFunctionCallDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }
}

public sealed class GeminiUsageMetadata
{
    [JsonPropertyName("promptTokenCount")]
    public int PromptTokenCount { get; set; }

    [JsonPropertyName("candidatesTokenCount")]
    public int CandidatesTokenCount { get; set; }
}

public class GeminiService : ILlmService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private int _disposed;

    public GeminiService(
        string apiKey,
        HttpClient? httpClient = null,
        string baseUrl = "https://generativelanguage.googleapis.com/v1beta")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
    }

    public Task<LlmResponse> GenerateContentAsync(LlmRequest request)
    {
        return GenerateContentAsync(request, CancellationToken.None);
    }

    public async Task<LlmResponse> GenerateContentAsync(LlmRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);

        var modelPath = NormalizeModelPath(request.Model);
        var url = $"{_baseUrl}/{modelPath}:generateContent";
        return await SendRequestAsync(url, request, ct).ConfigureAwait(false);
    }

    public IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
    {
        return StreamGenerateContentAsync(request, CancellationToken.None);
    }

    public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);

        var modelPath = NormalizeModelPath(request.Model);
        var url = $"{_baseUrl}/{modelPath}:streamGenerateContent?alt=sse";

        using var buffer = new PooledByteBufferWriter(1024);
        WritePayload(buffer, request);
        using var requestMessage = CreateHttpRequest(url, buffer.WrittenMemory);
        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Gemini API Error (StreamGenerateContent): {response.StatusCode} at {url}\nDetails: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        StringBuilder? nonSseBuffer = null;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (nonSseBuffer is null && trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = trimmed.AsSpan(5).Trim();
                if (data.Length == 0 || data.SequenceEqual("[DONE]"))
                {
                    continue;
                }

                GeminiGenerateResponse? parsed = JsonSerializer.Deserialize(data, AdkJsonContext.Default.GeminiGenerateResponse);
                if (parsed is not null)
                {
                    yield return MapResponse(parsed);
                }
            }
            else if (nonSseBuffer is not null || trimmed.StartsWith('[') || trimmed.StartsWith('{'))
            {
                // Fallback for mock/proxy endpoints returning a raw JSON array/object instead of SSE frames
                nonSseBuffer ??= new StringBuilder();
                nonSseBuffer.AppendLine(line);
            }
        }

        if (nonSseBuffer is not null && nonSseBuffer.Length > 0)
        {
            string rawJson = nonSseBuffer.ToString().TrimStart();
            if (rawJson.StartsWith('['))
            {
                List<GeminiGenerateResponse>? array = JsonSerializer.Deserialize(rawJson, AdkJsonContext.Default.ListGeminiGenerateResponse);
                if (array is not null)
                {
                    foreach (GeminiGenerateResponse item in array)
                    {
                        yield return MapResponse(item);
                    }
                }
            }
            else
            {
                GeminiGenerateResponse? single = JsonSerializer.Deserialize(rawJson, AdkJsonContext.Default.GeminiGenerateResponse);
                if (single is not null)
                {
                    yield return MapResponse(single);
                }
            }
        }
    }

    private static string NormalizeModelPath(string model)
    {
        return model.StartsWith("models/", StringComparison.Ordinal) ? model : $"models/{model}";
    }

    private HttpRequestMessage CreateHttpRequest(string url, ReadOnlyMemory<byte> utf8Payload)
    {
        var content = new ReadOnlyMemoryContent(utf8Payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        req.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        return req;
    }

    private static void WritePayload(IBufferWriter<byte> buffer, LlmRequest request)
    {
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();

        if (request.SystemInstruction is not null && HasWritableParts(request.SystemInstruction.Parts))
        {
            writer.WriteStartObject("system_instruction");
            writer.WriteStartArray("parts");
            WriteParts(writer, request.SystemInstruction.Parts);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteStartArray("contents");
        foreach (var content in request.Contents)
        {
            if (!HasWritableParts(content.Parts))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("role", content.Role ?? "user");
            writer.WriteStartArray("parts");
            WriteParts(writer, content.Parts);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (request.Tools.Count > 0)
        {
            writer.WriteStartArray("tools");
            writer.WriteStartObject();
            writer.WriteStartArray("function_declarations");
            foreach (var tool in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                JsonNode? schema = tool.GetParametersSchema();
                if (schema is not null)
                {
                    schema.WriteTo(writer);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static bool HasWritableParts(List<LlmPart> parts)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            LlmPart p = parts[i];
            if (p.Text is not null || p.FunctionCall is not null || p.FunctionResponse is not null)
            {
                return true;
            }
        }
        return false;
    }

    private static void WriteParts(Utf8JsonWriter writer, List<LlmPart> parts)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            LlmPart part = parts[i];
            if (part.Text is null && part.FunctionCall is null && part.FunctionResponse is null)
            {
                continue;
            }

            writer.WriteStartObject();
            if (part.Text is not null)
            {
                writer.WriteString("text", part.Text);
            }

            if (part.FunctionCall is not null)
            {
                writer.WriteStartObject("functionCall");
                writer.WriteString("name", part.FunctionCall.Name);
                writer.WritePropertyName("args");
                if (part.FunctionCall.Args is not null)
                {
                    part.FunctionCall.Args.WriteTo(writer);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            if (part.FunctionResponse is not null)
            {
                writer.WriteStartObject("functionResponse");
                writer.WriteString("name", part.FunctionResponse.Name);
                writer.WritePropertyName("response");
                if (part.FunctionResponse.Response is not null)
                {
                    part.FunctionResponse.Response.WriteTo(writer);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }
    }

    private async Task<LlmResponse> SendRequestAsync(string url, LlmRequest request, CancellationToken ct)
    {
        using var buffer = new PooledByteBufferWriter(1024);
        WritePayload(buffer, request);
        using var requestMessage = CreateHttpRequest(url, buffer.WrittenMemory);
        using var response = await _httpClient.SendAsync(requestMessage, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Gemini API Error (GenerateContent): {response.StatusCode} at {url}\nDetails: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        GeminiGenerateResponse? parsed = await JsonSerializer.DeserializeAsync(
            stream,
            AdkJsonContext.Default.GeminiGenerateResponse,
            ct).ConfigureAwait(false);
        return parsed is not null ? MapResponse(parsed) : new LlmResponse();
    }

    private static LlmResponse MapResponse(GeminiGenerateResponse doc)
    {
        var llmResponse = new LlmResponse();

        GeminiCandidate? candidate = doc.Candidates is { Count: > 0 } ? doc.Candidates[0] : null;
        List<GeminiPartDto>? parts = candidate?.Content?.Parts;

        if (parts != null)
        {
            StringBuilder? textBuilder = null;
            for (int i = 0; i < parts.Count; i++)
            {
                GeminiPartDto part = parts[i];
                if (part.Text is not null)
                {
                    textBuilder ??= new StringBuilder();
                    textBuilder.Append(part.Text);
                }

                if (part.FunctionCall is not null)
                {
                    llmResponse.FunctionCalls ??= new List<LlmFunctionCall>();
                    JsonNode? argsNode = null;
                    if (part.FunctionCall.Args is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } argsElem)
                    {
                        argsNode = JsonNode.Parse(argsElem.GetRawText());
                    }

                    llmResponse.FunctionCalls.Add(new LlmFunctionCall
                    {
                        Name = part.FunctionCall.Name ?? string.Empty,
                        Args = argsNode
                    });
                }
            }

            if (textBuilder is not null)
            {
                llmResponse.Content = textBuilder.ToString();
            }
        }

        if (doc.UsageMetadata is not null)
        {
            llmResponse.Usage = new LlmUsage
            {
                PromptTokens = doc.UsageMetadata.PromptTokenCount,
                CompletionTokens = doc.UsageMetadata.CandidatesTokenCount
            };

            if (llmResponse.Usage.TotalTokens > 0)
            {
                Telemetry.TokenCounter.Add(llmResponse.Usage.TotalTokens);
            }
        }

        return llmResponse;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
