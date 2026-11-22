using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

public sealed class OpenAiModelsListResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiModelEntry>? Data { get; set; }
}

public sealed class OpenAiModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public sealed record OpenAiNpuDiscoveryResult(
    string BaseUrl,
    IReadOnlyList<string> Models,
    double LatencyMs)
{
    public string? PrimaryModel => Models.Count > 0 ? Models[0] : null;
}

public class OpenAiService : ILlmService, IDisposable
{
    private const string NpuEndpointEnvVar = "GLACIER_NPU_ENDPOINT";
    private const string DefaultFastFlowLmUrl = "http://127.0.0.1:52625";
    private const string DefaultLemonadeUrl = "http://127.0.0.1:8000";
    private const string DefaultLemonadeAltUrl = "http://127.0.0.1:13305";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _chatCompletionsUrl;
    private readonly string? _apiKey;
    private readonly TextWriter? _debugWriter;
    private int _disposed;

    public string BaseUrl => _baseUrl;

    public OpenAiService(
        string baseUrl = "https://api.openai.com/v1",
        string? apiKey = null,
        TimeSpan? timeout = null,
        TextWriter? debugWriter = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var (pathPart, suffix) = SplitPathAndSuffix(baseUrl);
        if (pathPart.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = pathPart[..^"/chat/completions".Length].TrimEnd('/');
            if (string.IsNullOrWhiteSpace(stripped))
                throw new ArgumentException("Base URL cannot be empty before '/chat/completions'.", nameof(baseUrl));

            _baseUrl = string.Concat(stripped, suffix);
            _chatCompletionsUrl = string.Concat(pathPart, suffix);
        }
        else
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
            _chatCompletionsUrl = AppendPathBeforeSuffix(_baseUrl, "/chat/completions");
        }

        _apiKey = apiKey;
        _debugWriter = debugWriter;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };
    }

    public static string NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var (pathPart, suffix) = SplitPathAndSuffix(baseUrl);
        if (pathPart.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = pathPart[..^"/chat/completions".Length].TrimEnd('/');
            if (string.IsNullOrWhiteSpace(stripped))
                throw new ArgumentException("Base URL cannot be empty before '/chat/completions'.", nameof(baseUrl));
            return string.Concat(stripped, suffix);
        }

        if (pathPart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            if (pathPart.Length == 3)
                throw new ArgumentException("Base URL cannot be empty before '/v1'.", nameof(baseUrl));
            return string.Concat(pathPart.AsSpan(0, pathPart.Length - 3), "/v1", suffix);
        }

        if (string.IsNullOrWhiteSpace(pathPart))
            throw new ArgumentException("Base URL cannot be empty.", nameof(baseUrl));

        return string.Concat(pathPart, "/v1", suffix);
    }

    private static (string PathPart, string Suffix) SplitPathAndSuffix(string url)
    {
        string trimmed = url.Trim();
        int suffixIdx = trimmed.IndexOfAny(['?', '#']);
        string pathPart = (suffixIdx >= 0 ? trimmed[..suffixIdx] : trimmed).TrimEnd('/');
        string suffix = suffixIdx >= 0 ? trimmed[suffixIdx..] : string.Empty;
        return (pathPart, suffix);
    }

    private static string AppendPathBeforeSuffix(string baseUrl, string pathSegment)
    {
        var (pathPart, suffix) = SplitPathAndSuffix(baseUrl);
        return string.Concat(pathPart, pathSegment, suffix);
    }

    public static async Task<OpenAiNpuDiscoveryResult?> DiscoverNpuAsync(
        HttpClient? httpClient = null,
        IReadOnlyList<string>? candidateEndpoints = null,
        TimeSpan? perEndpointTimeout = null,
        string? explicitEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targets = BuildCandidateRoots(candidateEndpoints, explicitEndpoint);
        if (targets.Count == 0)
            return null;

        bool ownsClient = httpClient == null;
        HttpClient client = httpClient ?? new HttpClient();
        TimeSpan timeout = perEndpointTimeout ?? TimeSpan.FromMilliseconds(350);

        if (targets.Count == 1)
        {
            try
            {
                return await ProbeNpuRootAsync(client, targets[0], timeout, cancellationToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsClient)
                    client.Dispose();
            }
        }

        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new Task<OpenAiNpuDiscoveryResult?>[targets.Count];
        for (int i = 0; i < targets.Count; i++)
        {
            tasks[i] = ProbeNpuRootAsync(client, targets[i], timeout, cancellationToken, raceCts.Token);
        }

        try
        {
            for (int i = 0; i < tasks.Length; i++)
            {
                OpenAiNpuDiscoveryResult? result = await tasks[i].ConfigureAwait(false);
                if (result != null)
                {
                    raceCts.Cancel();
                    return result;
                }
            }

            return null;
        }
        finally
        {
            _ = Task.WhenAll(tasks).ContinueWith(
                static (t, state) =>
                {
                    _ = t.Exception;
                    var (ownedClient, cts) = ((HttpClient?, CancellationTokenSource))state!;
                    cts.Dispose();
                    ownedClient?.Dispose();
                },
                (ownsClient ? client : null, raceCts),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public static async Task<(OpenAiService Service, string? PrimaryModel)?> CreateNpuServiceAsync(
        string? apiKey = null,
        TimeSpan? timeout = null,
        TextWriter? debugWriter = null,
        HttpClient? httpClient = null,
        IReadOnlyList<string>? candidateEndpoints = null,
        TimeSpan? perEndpointTimeout = null,
        string? explicitEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        var found = await DiscoverNpuAsync(
            httpClient,
            candidateEndpoints,
            perEndpointTimeout,
            explicitEndpoint,
            cancellationToken).ConfigureAwait(false);

        if (found == null)
            return null;

        var service = new OpenAiService(found.BaseUrl, apiKey, timeout, debugWriter, httpClient);
        return (service, found.PrimaryModel);
    }

    private static List<string> BuildCandidateRoots(IReadOnlyList<string>? candidateEndpoints, string? explicitEndpoint)
    {
        var list = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;
            string root = StripTrailingV1(raw);
            if (!string.IsNullOrWhiteSpace(root) && seen.Add(root))
                list.Add(root);
        }

        TryAdd(explicitEndpoint);
        if (candidateEndpoints != null)
        {
            foreach (var endpoint in candidateEndpoints)
                TryAdd(endpoint);
        }
        else
        {
            TryAdd(Environment.GetEnvironmentVariable(NpuEndpointEnvVar));
            TryAdd(DefaultFastFlowLmUrl);
            TryAdd(DefaultLemonadeUrl);
            TryAdd(DefaultLemonadeAltUrl);
        }

        return list;
    }

    private static string StripTrailingV1(string url)
    {
        var (pathPart, suffix) = SplitPathAndSuffix(url);
        if (pathPart.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            pathPart = pathPart[..^"/chat/completions".Length].TrimEnd('/');
        if (pathPart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            pathPart = pathPart[..^3].TrimEnd('/');
        return string.Concat(pathPart, suffix);
    }

    private static async Task<OpenAiNpuDiscoveryResult?> ProbeNpuRootAsync(
        HttpClient httpClient,
        string rootUrl,
        TimeSpan timeout,
        CancellationToken callerCancellationToken,
        CancellationToken raceToken)
    {
        string modelsUrl = AppendPathBeforeSuffix(rootUrl, "/v1/models");
        if (Uri.TryCreate(modelsUrl, UriKind.Absolute, out Uri? absoluteProbeUri))
        {
            if (!string.Equals(absoluteProbeUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(absoluteProbeUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        else if (httpClient.BaseAddress == null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(raceToken);
        cts.CancelAfter(timeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await httpClient.GetAsync(modelsUrl, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            sw.Stop();

            var parsed = JsonSerializer.Deserialize(body, AdkJsonContext.Default.OpenAiModelsListResponse);
            if (parsed?.Data == null)
                return null;

            var models = new List<string>(parsed.Data.Count);
            foreach (var entry in parsed.Data)
            {
                if (!string.IsNullOrWhiteSpace(entry?.Id))
                    models.Add(entry.Id.Trim());
            }

            return new OpenAiNpuDiscoveryResult(
                NormalizeBaseUrl(rootUrl),
                models.AsReadOnly(),
                sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UriFormatException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public Task<LlmResponse> GenerateContentAsync(LlmRequest request)
        => GenerateContentAsync(request, CancellationToken.None);

    public async Task<LlmResponse> GenerateContentAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = MapToOpenAiRequest(request, stream: false);
        var json = payload.ToJsonString();

        using var requestMessage = CreateHttpRequest(_chatCompletionsUrl, json);
        using var response = await _httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    public IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
        => StreamGenerateContentAsync(request, CancellationToken.None);

    public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = MapToOpenAiRequest(request, stream: true);
        var json = payload.ToJsonString();

        using var requestMessage = CreateHttpRequest(_chatCompletionsUrl, json);
        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolCallAccumulators = new SortedDictionary<int, ToolCallAccumulator>();
        LlmUsage? finalUsage = null;
        bool hadStreamParseError = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
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

            if (doc is not JsonObject rootObj)
            {
                continue;
            }

            if (rootObj["usage"] is JsonObject usageObj)
            {
                finalUsage = new LlmUsage
                {
                    PromptTokens = TryReadInt(usageObj["prompt_tokens"]),
                    CompletionTokens = TryReadInt(usageObj["completion_tokens"])
                };
            }

            var delta = rootObj["choices"] is JsonArray { Count: > 0 } streamChoices &&
                        streamChoices[0] is JsonObject firstChoice
                ? firstChoice["delta"] as JsonObject
                : null;
            if (delta is null)
            {
                continue;
            }

            string? contentDelta = TryReadString(delta["content"]);
            if (!string.IsNullOrEmpty(contentDelta))
            {
                yield return new LlmResponse { Content = contentDelta };
            }

            if (delta["tool_calls"] is JsonArray toolCallDeltas)
            {
                int fallbackIndex = 0;
                foreach (var callDeltaNode in toolCallDeltas)
                {
                    if (callDeltaNode is not JsonObject callDelta)
                    {
                        fallbackIndex++;
                        continue;
                    }

                    int index = TryReadInt(callDelta["index"], fallbackIndex);
                    if (!toolCallAccumulators.TryGetValue(index, out var accumulator))
                    {
                        accumulator = new ToolCallAccumulator();
                        toolCallAccumulators[index] = accumulator;
                    }

                    var idChunk = TryReadString(callDelta["id"]);
                    if (!string.IsNullOrEmpty(idChunk))
                    {
                        accumulator.Id = idChunk;
                    }

                    var fnNode = callDelta["function"] as JsonObject;
                    var nameChunk = TryReadString(fnNode?["name"]);
                    if (!string.IsNullOrEmpty(nameChunk))
                    {
                        accumulator.NameBuilder.Append(nameChunk);
                    }

                    var argsChunk = TryReadString(fnNode?["arguments"]);
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

        if (!string.IsNullOrEmpty(_apiKey))
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

        if (request.Temperature.HasValue)
        {
            payload["temperature"] = request.Temperature.Value;
        }

        if (request.MaxTokens.HasValue)
        {
            payload["max_tokens"] = request.MaxTokens.Value;
        }

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
        if (doc is not JsonObject rootObj)
        {
            return CreateMalformedJsonErrorResponse("The server returned an unexpected non-object JSON payload.");
        }

        var message = rootObj["choices"] is JsonArray { Count: > 0 } choices &&
                      choices[0] is JsonObject firstChoice
            ? firstChoice["message"] as JsonObject
            : null;
        var content = TryReadString(message?["content"]) ?? message?["content"]?.ToJsonString();
        if (message?["content"] is null)
            content = null;
        var toolCalls = message?["tool_calls"] as JsonArray;

        var response = new LlmResponse { Content = content };

        if (toolCalls != null)
        {
            response.FunctionCalls = new List<LlmFunctionCall>(toolCalls.Count);
            foreach (var callNode in toolCalls)
            {
                if (callNode is not JsonObject call)
                    continue;
                var id = TryReadString(call["id"]);
                var fnObj = call["function"] as JsonObject;
                var name = TryReadString(fnObj?["name"]) ?? string.Empty;
                var rawArgs = TryReadString(fnObj?["arguments"]) ?? fnObj?["arguments"]?.ToJsonString() ?? "{}";

                response.FunctionCalls.Add(new LlmFunctionCall
                {
                    Id = string.IsNullOrEmpty(id) ? null : id,
                    Name = name,
                    Args = TryExtractJson(rawArgs) ?? CreateMalformedArgsNode()
                });
            }
        }

        if (rootObj["usage"] is JsonObject usageObj)
        {
            response.Usage = new LlmUsage
            {
                PromptTokens = TryReadInt(usageObj["prompt_tokens"]),
                CompletionTokens = TryReadInt(usageObj["completion_tokens"])
            };

            if (response.Usage.TotalTokens > 0)
            {
                Telemetry.TokenCounter.Add(response.Usage.TotalTokens);
            }
        }

        return response;
    }

    private static int TryReadInt(JsonNode? node, int defaultValue = 0)
    {
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out int i))
                return i;
            if (jv.TryGetValue<long>(out long l) && l >= int.MinValue && l <= int.MaxValue)
                return (int)l;
            if (jv.TryGetValue<string>(out string? s) && int.TryParse(s, out int parsed))
                return parsed;
        }
        return defaultValue;
    }

    private static string? TryReadString(JsonNode? node)
    {
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out string? s))
                return s;
        }
        return null;
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
