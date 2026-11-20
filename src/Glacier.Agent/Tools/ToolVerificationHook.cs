namespace Glacier.Agent.Tools;

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Adapts an <see cref="ITool"/> or <see cref="IAgentTool"/> into an <see cref="IVerificationHook"/>
/// using AOT-safe JSON serialization.
/// </summary>
public sealed class ToolVerificationHook : IVerificationHook
{
    private readonly ITool? _tool;
    private readonly IAgentTool? _agentTool;

    public string Name { get; }

    public ToolVerificationHook(ITool tool, string? name = null)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        Name = string.IsNullOrWhiteSpace(name) ? tool.Name : name;
    }

    public ToolVerificationHook(IAgentTool tool, string? name = null)
    {
        _agentTool = tool ?? throw new ArgumentNullException(nameof(tool));
        Name = string.IsNullOrWhiteSpace(name) ? tool.Name : name;
    }

    public async ValueTask<VerificationResult> VerifyAsync(VerificationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string payload = BuildToolArgumentsJson(context);
        string rawResult = _agentTool != null
            ? await _agentTool.ExecuteAsync(payload, ct).ConfigureAwait(false)
            : await _tool!.ExecuteAsync(payload).ConfigureAwait(false);

        return VerificationResultParser.Parse(rawResult, Name);
    }

    internal static string BuildToolArgumentsJson(VerificationContext context)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("output", context.CandidateOutput);
            writer.WriteString("prompt", context.CurrentPrompt);
            writer.WriteString("originalPrompt", context.OriginalPrompt);
            writer.WriteNumber("turn", context.TurnOrStep);
            if (!string.IsNullOrEmpty(context.SessionId))
            {
                writer.WriteString("sessionId", context.SessionId);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
