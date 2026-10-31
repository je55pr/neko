using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class ConversationUpdateFeedTests
{
    private static PublicConversationUpdate Append(string text) =>
        new("reply", PublicUpdateKind.Final, PublicUpdateOperation.Append, text);

    private static PublicConversationUpdate Complete() =>
        new("reply", PublicUpdateKind.Final, PublicUpdateOperation.Complete);

    [Fact]
    public async Task LateSubscribersReplayTheCompletedTurnIndependently()
    {
        var feed = new ConversationUpdateFeed();
        feed.Publish(Append("one"));
        feed.Publish(Append(" two"));
        feed.Publish(Complete());

        var a = await ReadAll(feed);
        var b = await ReadAll(feed);
        Assert.Equal(new[] { "one", " two", "" }, a.Select(x => x.Text));
        Assert.Equal(a, b);
        Assert.Equal(PublicUpdateOperation.Complete, a[^1].Operation);
    }

    [Fact]
    public async Task ConcurrentSubscribersDoNotStealEachOthersEvents()
    {
        var feed = new ConversationUpdateFeed();
        var first = ReadAll(feed);
        var second = ReadAll(feed);
        feed.Publish(Append("first"));
        feed.Publish(Append("second"));
        feed.Publish(Complete());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var results = await Task.WhenAll(first.WaitAsync(timeout.Token), second.WaitAsync(timeout.Token));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(3, results[0].Count);
    }

    [Fact]
    public async Task CompletedFeedIgnoresLateEvents()
    {
        var feed = new ConversationUpdateFeed();
        feed.Publish(Complete());
        feed.Publish(Append("should never arrive"));
        var result = await ReadAll(feed);
        Assert.Single(result);
    }

    [Fact]
    public async Task ConcurrentPublishingAndStatusSnapshotsRemainConsistent()
    {
        var feed = new ConversationUpdateFeed();
        var writers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
                feed.Publish(Append("payload-does-not-enter-metadata"));
        })).ToArray();
        var reader = Task.Run(async () =>
        {
            while (writers.Any(writer => !writer.IsCompleted))
            {
                var state = feed.Snapshot();
                Assert.InRange(state.PublicEventCount, 0, 800);
                Assert.Equal(state.PublicEventCount == 0,
                    state.LastPublicUpdateType is null);
                Assert.False(state.IsTerminal);
                await Task.Yield();
            }
        });
        await Task.WhenAll(writers);
        await reader;
        var beforeTerminal = feed.Snapshot();
        Assert.Equal(800, beforeTerminal.PublicEventCount);
        Assert.Equal("Final.Append", beforeTerminal.LastPublicUpdateType);
        Assert.Null(beforeTerminal.TerminalOperation);

        feed.Publish(Complete());
        Parallel.For(0, 100, _ => feed.Publish(Append("ignored")));
        var afterTerminal = feed.Snapshot();
        Assert.Equal(801, afterTerminal.PublicEventCount);
        Assert.True(afterTerminal.IsTerminal);
        Assert.Equal("Complete", afterTerminal.TerminalOperation);
        Assert.Equal("Final.Complete", afterTerminal.LastPublicUpdateType);
    }

    private static async Task<List<PublicConversationUpdate>> ReadAll(ConversationUpdateFeed feed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = new List<PublicConversationUpdate>();
        await foreach (var update in feed.WatchAsync(timeout.Token))
            result.Add(update);
        return result;
    }
}
