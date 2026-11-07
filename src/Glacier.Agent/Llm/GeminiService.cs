using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Glacier.Agent.Llm;

public class GeminiService : ILlmService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public GeminiService(
        string apiKey,
        HttpClient? httpClient = null,
        string baseUrl = "https://generativelanguage.googleapis.com/v1beta")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<LlmResponse> GenerateContentAsync(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelPath = NormalizeModelPath(request.Model);
        var url = $"{_baseUrl}/{modelPath}:generateContent";
        return await SendRequestAsync(url, request).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelPath = NormalizeModelPath(request.Model);
        var url = $"{_baseUrl}/{modelPath}:streamGenerateContent?alt=sse";

        var payload = CreatePayload(request);
        using var requestMessage = CreateHttpRequest(url, payload.ToJsonString());
        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException($"Gemini API Error (StreamGenerateContent): {response.StatusCode} at {url}\nDetails: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        StringBuilder? nonSseBuffer = null;

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (nonSseBuffer is null && trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = trimmed.Substring(5).Trim();
                if (data.Length == 0 || data == "[DONE]")
                {
                    continue;
                }

                var node = JsonNode.Parse(data);
                if (node is not null)
                {
                    yield return ParseResponse(node);
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
            var doc = JsonNode.Parse(nonSseBuffer.ToString());
            if (doc is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        yield return ParseResponse(item);
                    }
                }
            }
            else if (doc is not null)
            {
                yield return ParseResponse(doc);
            }
        }
    }

    private static string NormalizeModelPath(string model)
    {
        return model.StartsWith("models/", StringComparison.Ordinal) ? model : $"models/{model}";
    }

    private HttpRequestMessage CreateHttpRequest(string url, string jsonPayload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        return req;
    }

    private static JsonObject CreatePayload(LlmRequest request)
    {
        var payload = new JsonObject();

        if (request.SystemInstruction is not null && request.SystemInstruction.Parts.Count > 0)
        {
            var sysParts = MapParts(request.SystemInstruction.Parts);
            if (sysParts.Count > 0)
            {
                payload["system_instruction"] = new JsonObject
                {
                    ["parts"] = sysParts
                };
            }
        }

        var contentsArray = new JsonArray();
        foreach (var content in request.Contents)
        {
            var partsArray = MapParts(content.Parts);
            if (partsArray.Count == 0)
            {
                continue;
            }

            contentsArray.Add((JsonNode)new JsonObject
            {
                ["role"] = content.Role ?? "user",
                ["parts"] = partsArray
            });
        }
        payload["contents"] = contentsArray;

        if (request.Tools.Count > 0)
        {
            var declarations = new JsonArray();
            foreach (var tool in request.Tools)
            {
                declarations.Add((JsonNode)new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.GetParametersSchema().DeepClone()
                });
            }

            var toolsWrapper = new JsonArray();
            toolsWrapper.Add((JsonNode)new JsonObject
            {
                ["function_declarations"] = declarations
            });
            payload["tools"] = toolsWrapper;
        }

        return payload;
    }

    private static JsonArray MapParts(List<LlmPart> parts)
    {
        var array = new JsonArray();
        foreach (var part in parts)
        {
            var partObj = new JsonObject();
            bool hasField = false;

            if (part.Text is not null)
            {
                partObj["text"] = part.Text;
                hasField = true;
            }

            if (part.FunctionCall is not null)
            {
                partObj["functionCall"] = new JsonObject
                {
                    ["name"] = part.FunctionCall.Name,
                    ["args"] = part.FunctionCall.Args?.DeepClone() ?? new JsonObject()
                };
                hasField = true;
            }

            if (part.FunctionResponse is not null)
            {
                partObj["functionResponse"] = new JsonObject
                {
                    ["name"] = part.FunctionResponse.Name,
                    ["response"] = part.FunctionResponse.Response?.DeepClone() ?? new JsonObject()
                };
                hasField = true;
            }

            if (hasField)
            {
                array.Add((JsonNode)partObj);
            }
        }

        return array;
    }

    private async Task<LlmResponse> SendRequestAsync(string url, LlmRequest request)
    {
        var payload = CreatePayload(request);
        using var requestMessage = CreateHttpRequest(url, payload.ToJsonString());
        using var response = await _httpClient.SendAsync(requestMessage).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException($"Gemini API Error (GenerateContent): {response.StatusCode} at {url}\nDetails: {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var doc = JsonNode.Parse(json);
        return ParseResponse(doc);
    }

    private static LlmResponse ParseResponse(JsonNode? doc)
    {
        var candidate = doc?["candidates"]?[0];
        var content = candidate?["content"];
        var parts = content?["parts"]?.AsArray();

        var llmResponse = new LlmResponse();

        if (parts != null)
        {
            StringBuilder? textBuilder = null;
            foreach (var part in parts)
            {
                var textNode = part?["text"];
                if (textNode != null)
                {
                    textBuilder ??= new StringBuilder();
                    textBuilder.Append(textNode.ToString());
                }

                var fnCall = part?["functionCall"];
                if (fnCall != null)
                {
                    llmResponse.FunctionCalls ??= new List<LlmFunctionCall>();
                    llmResponse.FunctionCalls.Add(new LlmFunctionCall
                    {
                        Name = fnCall["name"]?.ToString() ?? string.Empty,
                        Args = fnCall["args"]?.DeepClone()
                    });
                }
            }

            if (textBuilder is not null)
            {
                llmResponse.Content = textBuilder.ToString();
            }
        }

        var usage = doc?["usageMetadata"];
        if (usage != null)
        {
            llmResponse.Usage = new LlmUsage
            {
                PromptTokens = usage["promptTokenCount"]?.GetValue<int>() ?? 0,
                CompletionTokens = usage["candidatesTokenCount"]?.GetValue<int>() ?? 0
            };

            if (llmResponse.Usage.TotalTokens > 0)
            {
                Telemetry.TokenCounter.Add(llmResponse.Usage.TotalTokens);
            }
        }

        return llmResponse;
    }
}
