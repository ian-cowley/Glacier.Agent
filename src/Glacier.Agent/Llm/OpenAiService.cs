using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Glacier.Agent.Llm;

public class OpenAiService : ILlmService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly TextWriter? _debugWriter;

    public OpenAiService(
        string baseUrl = "https://api.openai.com/v1",
        string? apiKey = null,
        TimeSpan? timeout = null,
        TextWriter? debugWriter = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _debugWriter = debugWriter;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };

        if (!string.IsNullOrEmpty(_apiKey) && _httpClient.DefaultRequestHeaders.Authorization is null)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    public async Task<LlmResponse> GenerateContentAsync(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{_baseUrl}/chat/completions";
        var payload = MapToOpenAiRequest(request, stream: false);
        var json = payload.ToJsonString();

        using var requestMessage = CreateHttpRequest(url, json);
        using var response = await _httpClient.SendAsync(requestMessage).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        _debugWriter?.WriteLine($"[DEBUG] Raw API response: {responseJson}");

        try
        {
            var doc = JsonNode.Parse(responseJson);
            return MapFromOpenAiResponse(doc);
        }
        catch (JsonException)
        {
            return CreateMalformedJsonErrorResponse("The server returned malformed JSON. Please try again.");
        }
    }

    public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{_baseUrl}/chat/completions";
        var payload = MapToOpenAiRequest(request, stream: true);
        var json = payload.ToJsonString();

        using var requestMessage = CreateHttpRequest(url, json);
        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolCallAccumulators = new SortedDictionary<int, ToolCallAccumulator>();
        LlmUsage? finalUsage = null;
        bool hadStreamParseError = false;

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = trimmed.Substring(5).Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                break;
            }

            JsonNode? doc = null;
            try
            {
                doc = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                hadStreamParseError = true;
            }

            if (hadStreamParseError)
            {
                yield return CreateMalformedJsonErrorResponse("The server returned malformed JSON stream. Please try again.");
                yield break;
            }

            var usageNode = doc?["usage"];
            if (usageNode is not null)
            {
                finalUsage = new LlmUsage
                {
                    PromptTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0,
                    CompletionTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? 0
                };
            }

            var delta = doc?["choices"]?[0]?["delta"];
            if (delta is null)
            {
                continue;
            }

            var contentDelta = delta["content"]?.GetValue<string>() ?? delta["content"]?.ToString();
            if (!string.IsNullOrEmpty(contentDelta))
            {
                yield return new LlmResponse { Content = contentDelta };
            }

            if (delta["tool_calls"] is JsonArray toolCallDeltas)
            {
                int fallbackIndex = 0;
                foreach (var callDelta in toolCallDeltas)
                {
                    if (callDelta is null)
                    {
                        fallbackIndex++;
                        continue;
                    }

                    int index = callDelta["index"]?.GetValue<int>() ?? fallbackIndex;
                    if (!toolCallAccumulators.TryGetValue(index, out var accumulator))
                    {
                        accumulator = new ToolCallAccumulator();
                        toolCallAccumulators[index] = accumulator;
                    }

                    var idChunk = callDelta["id"]?.ToString();
                    if (!string.IsNullOrEmpty(idChunk))
                    {
                        accumulator.Id = idChunk;
                    }

                    var fnNode = callDelta["function"];
                    var nameChunk = fnNode?["name"]?.ToString();
                    if (!string.IsNullOrEmpty(nameChunk))
                    {
                        accumulator.NameBuilder.Append(nameChunk);
                    }

                    var argsChunk = fnNode?["arguments"]?.ToString();
                    if (!string.IsNullOrEmpty(argsChunk))
                    {
                        accumulator.ArgumentsBuilder.Append(argsChunk);
                    }

                    fallbackIndex++;
                }
            }
        }

        if (finalUsage is not null && finalUsage.TotalTokens > 0)
        {
            Telemetry.TokenCounter.Add(finalUsage.TotalTokens);
        }

        if (!hadStreamParseError && toolCallAccumulators.Count > 0)
        {
            var assembledCalls = new List<LlmFunctionCall>(toolCallAccumulators.Count);
            foreach (var (_, acc) in toolCallAccumulators)
            {
                var name = acc.NameBuilder.ToString();
                var rawArgs = acc.ArgumentsBuilder.Length == 0 ? "{}" : acc.ArgumentsBuilder.ToString();
                var parsedArgs = TryExtractJson(rawArgs) ?? CreateMalformedArgsNode();

                assembledCalls.Add(new LlmFunctionCall
                {
                    Id = acc.Id,
                    Name = name,
                    Args = parsedArgs
                });
            }

            yield return new LlmResponse
            {
                FunctionCalls = assembledCalls,
                Usage = finalUsage
            };
        }
        else if (!hadStreamParseError && finalUsage is not null)
        {
            yield return new LlmResponse { Usage = finalUsage };
        }
    }

    private HttpRequestMessage CreateHttpRequest(string url, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrEmpty(_apiKey) && _httpClient.DefaultRequestHeaders.Authorization is null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return req;
    }

    private static JsonObject MapToOpenAiRequest(LlmRequest request, bool stream)
    {
        var messages = new JsonArray();

        if (request.SystemInstruction is not null)
        {
            var sysText = GetTextContent(request.SystemInstruction);
            if (!string.IsNullOrEmpty(sysText))
            {
                messages.Add((JsonNode)new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = sysText
                });
            }
        }

        foreach (var msg in request.Contents)
        {
            var role = msg.Role switch
            {
                "user" => "user",
                "model" => "assistant",
                "function" => "tool",
                _ => "user"
            };

            if (role == "tool")
            {
                foreach (var part in msg.Parts)
                {
                    if (part.FunctionResponse is null)
                    {
                        continue;
                    }

                    messages.Add((JsonNode)new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = string.IsNullOrEmpty(part.FunctionResponse.Id)
                            ? part.FunctionResponse.Name
                            : part.FunctionResponse.Id,
                        ["content"] = part.FunctionResponse.Response?.ToJsonString() ?? string.Empty
                    });
                }
            }
            else
            {
                var content = GetTextContent(msg);
                var toolCalls = GetToolCalls(msg);

                var msgObj = new JsonObject
                {
                    ["role"] = role,
                    ["content"] = string.IsNullOrEmpty(content) ? null : JsonValue.Create(content)
                };

                if (toolCalls is not null && toolCalls.Count > 0)
                {
                    msgObj["tool_calls"] = toolCalls;
                }

                messages.Add((JsonNode)msgObj);
            }
        }

        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream
        };

        if (request.Tools.Count > 0)
        {
            var toolsArray = new JsonArray();
            foreach (var tool in request.Tools)
            {
                toolsArray.Add((JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.GetParametersSchema().DeepClone()
                    }
                });
            }

            payload["tools"] = toolsArray;
            payload["tool_choice"] = "auto";
        }

        return payload;
    }

    private static string? GetTextContent(LlmContent content)
    {
        var texts = content.Parts.Where(p => p.Text != null).Select(p => p.Text!);
        using var enumerator = texts.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var first = enumerator.Current;
        if (!enumerator.MoveNext())
        {
            return first;
        }

        var sb = new StringBuilder(first);
        do
        {
            sb.Append('\n').Append(enumerator.Current);
        } while (enumerator.MoveNext());

        return sb.ToString();
    }

    private static JsonArray? GetToolCalls(LlmContent content)
    {
        JsonArray? calls = null;
        foreach (var part in content.Parts)
        {
            if (part.FunctionCall is null)
            {
                continue;
            }

            calls ??= new JsonArray();
            var callId = string.IsNullOrEmpty(part.FunctionCall.Id)
                ? part.FunctionCall.Name
                : part.FunctionCall.Id;

            calls.Add((JsonNode)new JsonObject
            {
                ["id"] = callId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = part.FunctionCall.Name,
                    ["arguments"] = part.FunctionCall.Args?.ToJsonString() ?? "{}"
                }
            });
        }

        return calls;
    }

    private static LlmResponse MapFromOpenAiResponse(JsonNode? doc)
    {
        var message = doc?["choices"]?[0]?["message"];
        var content = message?["content"]?.ToString();
        var toolCalls = message?["tool_calls"]?.AsArray();

        var response = new LlmResponse { Content = content };

        if (toolCalls != null)
        {
            response.FunctionCalls = new List<LlmFunctionCall>(toolCalls.Count);
            foreach (var call in toolCalls)
            {
                var id = call?["id"]?.ToString();
                var name = call?["function"]?["name"]?.ToString() ?? string.Empty;
                var rawArgs = call?["function"]?["arguments"]?.ToString() ?? "{}";

                response.FunctionCalls.Add(new LlmFunctionCall
                {
                    Id = string.IsNullOrEmpty(id) ? null : id,
                    Name = name,
                    Args = TryExtractJson(rawArgs) ?? CreateMalformedArgsNode()
                });
            }
        }

        var usage = doc?["usage"];
        if (usage != null)
        {
            response.Usage = new LlmUsage
            {
                PromptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? 0,
                CompletionTokens = usage["completion_tokens"]?.GetValue<int>() ?? 0
            };

            if (response.Usage.TotalTokens > 0)
            {
                Telemetry.TokenCounter.Add(response.Usage.TotalTokens);
            }
        }

        return response;
    }

    private static LlmResponse CreateMalformedJsonErrorResponse(string errorMessage)
    {
        return new LlmResponse
        {
            FunctionCalls = new List<LlmFunctionCall>
            {
                new LlmFunctionCall
                {
                    Name = "error",
                    Args = new JsonObject { ["error"] = errorMessage }
                }
            }
        };
    }

    private static JsonObject CreateMalformedArgsNode()
    {
        return new JsonObject
        {
            ["error"] = "Malformed tool arguments. Please fix the JSON format."
        };
    }

    private static JsonNode? TryExtractJson(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        try
        {
            int start = input.IndexOf('{');
            int end = input.LastIndexOf('}');

            if (start != -1 && end != -1 && end >= start)
            {
                string json = input.Substring(start, end - start + 1);
                return JsonNode.Parse(json);
            }

            return JsonNode.Parse(input);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public StringBuilder NameBuilder { get; } = new();
        public StringBuilder ArgumentsBuilder { get; } = new();
    }
}
