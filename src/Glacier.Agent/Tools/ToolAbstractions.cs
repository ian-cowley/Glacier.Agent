namespace Glacier.Agent.Tools;

using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonNode GetParametersSchema();
    Task<string> ExecuteAsync(string arguments);
}

public interface IApprovalService
{
    Task<bool> ApproveAsync(string toolName, string arguments);
}

public class SensitiveTool : ITool
{
    public ITool InnerTool { get; }
    public IApprovalService? ApprovalService { get; }
    public string Name => InnerTool.Name;
    public string Description => InnerTool.Description;

    public SensitiveTool(ITool innerTool, IApprovalService? approvalService = null)
    {
        InnerTool = innerTool ?? throw new ArgumentNullException(nameof(innerTool));
        ApprovalService = approvalService;
    }

    public JsonNode GetParametersSchema() => InnerTool.GetParametersSchema();

    public async Task<string> ExecuteAsync(string arguments)
    {
        if (ApprovalService != null)
        {
            var approved = await ApprovalService.ApproveAsync(Name, arguments).ConfigureAwait(false);
            if (!approved)
            {
                return new JsonObject
                {
                    ["status"] = "error",
                    ["message"] = "Action rejected by user."
                }.ToJsonString();
            }
        }

        return await InnerTool.ExecuteAsync(arguments).ConfigureAwait(false);
    }

    internal Task<string> ExecuteApprovedAsync(string arguments) => InnerTool.ExecuteAsync(arguments);
}
