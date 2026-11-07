namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Sessions;
using Glacier.Agent.Llm;
using Xunit;

public class AdkSessionProviderTests
{
    [Fact]
    public async Task MemorySessionProvider_SavesAndRetrievesHistoryInOrder()
    {
        var provider = new MemorySessionProvider();
        string sessionId = "test-session-1";

        var historyEmpty = await provider.GetHistoryAsync(sessionId);
        Assert.Empty(historyEmpty);

        await provider.SaveMessageAsync(sessionId, LlmContent.User("Hello agent!"));
        await provider.SaveMessageAsync(sessionId, LlmContent.Model("Hello user, how can I assist?"));
        await provider.SaveMessageAsync(sessionId, LlmContent.User("Give me a quote."));

        var history = await provider.GetHistoryAsync(sessionId);
        Assert.Equal(3, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("Hello agent!", history[0].Parts[0].Text);
        Assert.Equal("model", history[1].Role);
        Assert.Equal("Hello user, how can I assist?", history[1].Parts[0].Text);
        Assert.Equal("user", history[2].Role);
        Assert.Equal("Give me a quote.", history[2].Parts[0].Text);
    }

    [Fact]
    public async Task MemorySessionProvider_MultiSessionIsolation()
    {
        var provider = new MemorySessionProvider();
        await provider.SaveMessageAsync("session-A", LlmContent.User("Message in A"));
        await provider.SaveMessageAsync("session-B", LlmContent.User("Message in B"));

        var historyA = await provider.GetHistoryAsync("session-A");
        var historyB = await provider.GetHistoryAsync("session-B");

        Assert.Single(historyA);
        Assert.Equal("Message in A", historyA[0].Parts[0].Text);

        Assert.Single(historyB);
        Assert.Equal("Message in B", historyB[0].Parts[0].Text);
    }

    [Fact]
    public async Task FileSessionProvider_PersistsAndLoadsHistoryFromDisk()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AgentDevKit_Tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FileSessionProvider(tempDir);
            string sessionId = "disk-session-xyz";

            var initial = await provider.GetHistoryAsync(sessionId);
            Assert.Empty(initial);

            await provider.SaveMessageAsync(sessionId, LlmContent.User("Save to disk"));
            await provider.SaveMessageAsync(sessionId, LlmContent.Model("Saved successfully"));

            // Create a second instance pointing to the same folder to verify disk persistence
            var provider2 = new FileSessionProvider(tempDir);
            var loaded = await provider2.GetHistoryAsync(sessionId);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("Save to disk", loaded[0].Parts[0].Text);
            Assert.Equal("Saved successfully", loaded[1].Parts[0].Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
