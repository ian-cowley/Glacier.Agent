namespace Glacier.Agent.Mcp;

public class McpServerConfig
{
    public string Command { get; set; } = string.Empty;
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
}

public class McpSettings
{
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
}
