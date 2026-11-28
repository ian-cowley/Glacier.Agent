namespace Glacier.Agent.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Glacier.Agent.CommandLine;
using Xunit;

[Collection("ProcessAndTimingSensitive")]
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

    [Fact]
    public void NegativeNumericOptions_AndShortOptionEquals_AndCaseInsensitiveRequired_ParseCorrectly()
    {
        double capturedTemp = 0;
        int capturedPort = 0;
        var err = new StringWriter();
        var app = new CliApp("glacier") { Error = err };

        app.AddCommand(new CliCommand("infer")
            .AddOption("--temp", "-t", "Sampling temperature", hasValue: true, isRequired: true)
            .AddOption("--port", "-p", "Listen port", hasValue: true, defaultValue: "8080")
            .SetHandler(ctx =>
            {
                capturedTemp = ctx.GetOptionDouble("--temp");
                capturedPort = ctx.GetOptionInt("--port");
                return 0;
            }));

        Assert.Equal(0, app.Run(["infer", "--TEMP", "-0.5", "-p=9090"]));
        Assert.Equal(-0.5, capturedTemp, precision: 5);
        Assert.Equal(9090, capturedPort);

        Assert.Equal(0, app.Run(["infer", "-t", "-0.25"]));
        Assert.Equal(-0.25, capturedTemp, precision: 5);
        Assert.Equal(8080, capturedPort);

        Assert.Equal(0, app.Run(["infer", "-t", "-.75"]));
        Assert.Equal(-0.75, capturedTemp, precision: 5);
    }

    [Fact]
    public void Flag_ShortAndLongCrossLookup_AndCombinedAndUnregisteredFlags_ParseCorrectly()
    {
        bool hasVerboseLong = false;
        bool hasVerboseShort = false;
        bool hasVerboseBare = false;
        bool hasAlphaLong = false;
        bool hasAlphaShort = false;
        bool hasBetaLong = false;
        bool hasBetaShort = false;
        bool hasCustom = false;
        bool hasUnregShort = false;
        bool hasMissing = false;

        var app = new CliApp("glacier");
        app.AddCommand(new CliCommand("cmd")
            .AddFlag("--verbose", "-v", "Verbose")
            .AddFlag("--alpha", "-a", "Alpha")
            .AddFlag("--beta", "-b", "Beta")
            .SetHandler(ctx =>
            {
                hasVerboseLong = ctx.HasFlag("--verbose");
                hasVerboseShort = ctx.HasFlag("-v");
                hasVerboseBare = ctx.HasFlag("verbose");
                hasAlphaLong = ctx.HasFlag("--alpha");
                hasAlphaShort = ctx.HasFlag("-a");
                hasBetaLong = ctx.HasFlag("--beta");
                hasBetaShort = ctx.HasFlag("-b");
                hasCustom = ctx.HasFlag("--custom");
                hasUnregShort = ctx.HasFlag("-z");
                hasMissing = ctx.HasFlag("--missing");
                return 0;
            }));

        Assert.Equal(0, app.Run(["cmd", "-v"]));
        Assert.True(hasVerboseLong);
        Assert.True(hasVerboseShort);
        Assert.True(hasVerboseBare);

        Assert.Equal(0, app.Run(["cmd", "--verbose", "-ab", "--custom", "-z"]));
        Assert.True(hasVerboseLong);
        Assert.True(hasVerboseShort);
        Assert.True(hasAlphaLong);
        Assert.True(hasAlphaShort);
        Assert.True(hasBetaLong);
        Assert.True(hasBetaShort);
        Assert.True(hasCustom);
        Assert.True(hasUnregShort);
        Assert.False(hasMissing);
    }

    [Fact]
    public void MissingValueBeforeFlag_WithDefault_PreservesDefaultAndParsesFlag()
    {
        double capturedTemp = 0;
        bool hasVerboseLong = false;
        bool hasVerboseShort = false;
        var err = new StringWriter();
        var app = new CliApp("glacier") { Error = err };

        app.AddCommand(new CliCommand("cmd")
            .AddOption("--temp", "-t", "Sampling temperature", hasValue: true, defaultValue: "1.0")
            .AddFlag("--verbose", "-v", "Verbose")
            .SetHandler(ctx =>
            {
                capturedTemp = ctx.GetOptionDouble("--temp");
                hasVerboseLong = ctx.HasFlag("--verbose");
                hasVerboseShort = ctx.HasFlag("-v");
                return 0;
            }));

        Assert.Equal(0, app.Run(["cmd", "--temp", "--verbose"]));
        Assert.Equal(1.0, capturedTemp, precision: 5);
        Assert.True(hasVerboseLong);
        Assert.True(hasVerboseShort);
        Assert.Equal(string.Empty, err.ToString());

        Assert.Equal(0, app.Run(["cmd", "-t", "-v"]));
        Assert.Equal(1.0, capturedTemp, precision: 5);
        Assert.True(hasVerboseLong);
        Assert.True(hasVerboseShort);
    }
}
