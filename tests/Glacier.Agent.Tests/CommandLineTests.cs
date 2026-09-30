namespace Glacier.Agent.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Glacier.Agent.CommandLine;
using Xunit;

public sealed class CommandLineTests
{
    [Fact]
    public void ColdStartup_UnderFiveMilliseconds()
    {
        // Cold startup invocation benchmark
        var sw = Stopwatch.StartNew();
        var app = new CliApp("glacier", "Glacier Autonomous Agent CLI");
        app.AddCommand(new CliCommand("status", "Check agent status")
            .SetHandler(ctx => 0));

        using var swOut = new StringWriter();
        app.Output = swOut;
        int exitCode = app.Run(["status"]);
        sw.Stop();

        Assert.Equal(0, exitCode);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Startup took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Subcommand_OptionAndArgumentParsing_Succeeds()
    {
        var app = new CliApp("glacier", "Glacier Agent CLI");
        string? capturedModel = null;
        int capturedPort = 0;
        string? capturedPath = null;
        bool verboseFlag = false;

        var serveCmd = new CliCommand("serve", "Start agent server")
            .AddOption("model", "m", "Model identifier", hasValue: true, defaultValue: "gpt-4")
            .AddOption("port", "p", "Port to listen on", hasValue: true, defaultValue: "8080")
            .AddFlag("verbose", "v", "Enable verbose logging")
            .AddArgument("config", "Path to config file", isRequired: true)
            .SetHandler(ctx =>
            {
                capturedModel = ctx.GetOption("model");
                capturedPort = ctx.GetOptionInt("port");
                verboseFlag = ctx.HasFlag("-v");
                capturedPath = ctx.GetArgument(0);
                return 42;
            });

        app.AddCommand(serveCmd);

        string[] args = ["serve", "-m", "claude-3-5", "--port", "9090", "-v", "settings.json"];
        int result = app.Run(args);

        Assert.Equal(42, result);
        Assert.Equal("claude-3-5", capturedModel);
        Assert.Equal(9090, capturedPort);
        Assert.True(verboseFlag);
        Assert.Equal("settings.json", capturedPath);
    }

    [Fact]
    public void OptionEqualsSyntax_ParsesCorrectly()
    {
        var app = new CliApp("glacier");
        string? capturedKey = null;

        var runCmd = new CliCommand("run")
            .AddOption("key", null, "Key parameter")
            .SetHandler(ctx =>
            {
                capturedKey = ctx.GetOption("key");
                return 0;
            });

        app.AddCommand(runCmd);
        app.Run(["run", "--key=my-custom-value"]);

        Assert.Equal("my-custom-value", capturedKey);
    }

    [Fact]
    public void HelpAndVersionFlags_ReturnZeroExitCode()
    {
        var app = new CliApp("glacier", "Glacier autonomous CLI", "2.5.0");
        using var swOut = new StringWriter();
        app.Output = swOut;

        int helpResult = app.Run(["--help"]);
        Assert.Equal(0, helpResult);
        string helpText = swOut.ToString();
        Assert.Contains("Glacier autonomous CLI", helpText);

        using var swOut2 = new StringWriter();
        app.Output = swOut2;
        int verResult = app.Run(["--version"]);
        Assert.Equal(0, verResult);
        Assert.Contains("2.5.0", swOut2.ToString());
    }
}
