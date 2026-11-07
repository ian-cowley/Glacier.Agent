using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Glacier.Agent.Mcp;
using Glacier.Agent.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Glacier.Agent.Mcp;

public class McpService : IAsyncDisposable
{
    private const string SdkVersion = "1.1.0";
    private readonly ILogger<McpService>? _logger;
    private readonly List<McpClient> _clients = new();
    private readonly List<Process> _processes = new();

    public McpService(ILogger<McpService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers an existing McpClient directly (e.g. for testing with InMemoryMcpTransport).
    /// </summary>
    public async Task<List<ITool>> RegisterClientAsync(McpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients.Add(client);
        var tools = new List<ITool>();
        var mcpTools = await client.ListToolsAsync().ConfigureAwait(false);
        foreach (var tool in mcpTools)
        {
            _logger?.LogInformation("  Found tool: {ToolName}", tool.Name);
            tools.Add(new McpClientTool(client, tool));
        }
        return tools;
    }

    public async Task<List<ITool>> InitializeFromConfigAsync(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = BindMcpSettings(configuration);
        var allTools = new List<ITool>();

        if (settings.McpServers.Count == 0)
        {
            return allTools;
        }

        foreach (var server in settings.McpServers)
        {
            try
            {
                _logger?.LogInformation("Initializing MCP server: {ServerName}", server.Key);

                var psi = new ProcessStartInfo
                {
                    FileName = server.Value.Command,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var arg in server.Value.Args)
                {
                    psi.ArgumentList.Add(arg);
                }

                foreach (var env in server.Value.Env)
                {
                    psi.Environment[env.Key] = env.Value;
                }

                var process = Process.Start(psi);
                if (process == null)
                {
                    _logger?.LogError("Failed to start process for server: {ServerName}", server.Key);
                    continue;
                }

                _processes.Add(process);

                var transport = new StreamMcpTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
                var client = new McpClient(transport);
                await client.InitializeAsync("AgentDevKit.Adk", SdkVersion).ConfigureAwait(false);

                _clients.Add(client);

                var mcpTools = await client.ListToolsAsync().ConfigureAwait(false);
                foreach (var tool in mcpTools)
                {
                    _logger?.LogInformation("  Found tool: {ToolName}", tool.Name);
                    allTools.Add(new McpClientTool(client, tool));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize MCP server: {ServerName}", server.Key);
            }
        }

        return allTools;
    }

    private static McpSettings BindMcpSettings(IConfiguration configuration)
    {
        var settings = new McpSettings();
        var serversSection = configuration.GetSection("McpServers");
        if (!serversSection.Exists())
        {
            serversSection = configuration.GetSection("mcpServers");
        }

        if (!serversSection.Exists())
        {
            return settings;
        }

        foreach (var child in serversSection.GetChildren())
        {
            var serverConfig = new McpServerConfig
            {
                Command = child["Command"] ?? child["command"] ?? string.Empty
            };

            var argsSection = child.GetSection("Args");
            if (!argsSection.Exists())
            {
                argsSection = child.GetSection("args");
            }
            foreach (var argItem in argsSection.GetChildren())
            {
                if (argItem.Value is not null)
                {
                    serverConfig.Args.Add(argItem.Value);
                }
            }

            var envSection = child.GetSection("Env");
            if (!envSection.Exists())
            {
                envSection = child.GetSection("env");
            }
            foreach (var envItem in envSection.GetChildren())
            {
                if (envItem.Value is not null)
                {
                    serverConfig.Env[envItem.Key] = envItem.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(serverConfig.Command))
            {
                settings.McpServers[child.Key] = serverConfig;
            }
        }

        return settings;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _clients.Clear();

        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                process.Dispose();
            }
            catch
            {
            }
        }
        _processes.Clear();
    }
}
