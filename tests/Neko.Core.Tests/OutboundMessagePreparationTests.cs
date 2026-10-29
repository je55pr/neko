using Neko;

namespace Neko.Core.Tests;

public sealed class OutboundMessagePreparationTests
{
    [Fact]
    public void ExistingChatSubmissionKeepsExactUserTextAndTarget()
    {
        var id=Guid.NewGuid().ToString("D");
        const string text="  leading\r\nsecond line 😀  \n";
        var result=OutboundMessagePreparation.ForExisting(id,text);

        Assert.True(result.IsReady);
        var outgoing=Assert.IsType<PreparedOutboundMessage>(result.Message);
        Assert.Equal(text,outgoing.Text);
        Assert.Equal(id,outgoing.ConversationId);
        Assert.False(outgoing.CreatesConversation);
        Assert.True(Guid.TryParseExact(outgoing.ClientMessageId,"D",out _));
        Assert.Equal(TimeSpan.Zero,outgoing.PreparedAtUtc.Offset);
        Assert.True(OutboundMessagePreparation.IsValid(outgoing));
    }

    [Fact]
    public void NewMessagesHaveStableDistinctIdsAndNoConversationTarget()
    {
        var first=OutboundMessagePreparation.ForNew("same prompt").Message!;
        var second=OutboundMessagePreparation.ForNew("same prompt").Message!;
        Assert.True(first.CreatesConversation);
        Assert.Null(first.ConversationId);
        Assert.NotEqual(first.ClientMessageId,second.ClientMessageId);
        Assert.Equal(first.ClientMessageId,(first with { }).ClientMessageId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    [InlineData("a\0b")]
    public void InvalidContentCannotBePrepared(string text)
    {
        Assert.False(OutboundMessagePreparation.ForNew(text).IsReady);
    }

    [Fact]
    public void InvalidSurrogateCharactersCannotBePrepared()
    {
        Assert.False(OutboundMessagePreparation.ForNew(new string((char)0xD800,1)).IsReady);
        Assert.False(OutboundMessagePreparation.ForNew(new string((char)0xDC00,1)).IsReady);
    }

    [Fact]
    public void OversizeAndInvalidTargetAreRejected()
    {
        Assert.False(OutboundMessagePreparation.ForNew(
            new string('x',OutboundMessagePreparation.MaxTextLength+1)).IsReady);
        Assert.False(OutboundMessagePreparation.ForExisting("","hello").IsReady);
        Assert.False(OutboundMessagePreparation.ForExisting(
            new string('x',257),"hello").IsReady);
        Assert.False(OutboundMessagePreparation.ForExisting(null,"hello").IsReady);
    }

    [Fact]
    public void ForgedOrMalformedPreparedSubmissionIsNotValid()
    {
        var submission=OutboundMessagePreparation.ForNew("hello").Message!;
        Assert.False(OutboundMessagePreparation.IsValid(
            submission with {ClientMessageId="fake"}));
        Assert.False(OutboundMessagePreparation.IsValid(
            submission with {Text=new string((char)0xD800,1)}));
        Assert.False(OutboundMessagePreparation.IsValid(
            submission with {ConversationId="  "}));
    }

    private sealed class RecordingProvider : IConversationProvider, IPreparedConversationProvider
    {
        public PreparedOutboundMessage? LastPrepared;
        public int OldMethodCalls;
        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        public Task<VisibleConversation?> ReadAsync(string id,CancellationToken ct)=>
            Task.FromResult<VisibleConversation?>(null);
        public Task<StopReceipt> StopAsync(string id,CancellationToken ct)=>
            Task.FromResult(new StopReceipt(StopDisposition.NotActive));
        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id,[System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct){await Task.CompletedTask;yield break;}
        public Task<SendReceipt> CreateAsync(string text,CancellationToken ct)
        {
            OldMethodCalls++;return Task.FromResult(new SendReceipt(SendDisposition.Rejected));
        }
        public Task<SendReceipt> SendAsync(string id,string text,CancellationToken ct)
        {
            OldMethodCalls++;return Task.FromResult(new SendReceipt(SendDisposition.Rejected));
        }
        public Task<SendReceipt> CreatePreparedAsync(
            PreparedOutboundMessage prepared,CancellationToken ct)
        {
            LastPrepared=prepared;
            return Task.FromResult(new SendReceipt(SendDisposition.Accepted,
                ConversationId:Guid.NewGuid().ToString("D")));
        }
        public Task<SendReceipt> SendPreparedAsync(
            PreparedOutboundMessage prepared,CancellationToken ct)
        {
            LastPrepared=prepared;
            return Task.FromResult(new SendReceipt(SendDisposition.Accepted));
        }
    }

    [Fact]
    public async Task CoordinatorPassesPreparedIdentityWithoutRecreatingIt()
    {
        var provider=new RecordingProvider();
        var coordinator=new ConversationCommandCoordinator(provider);
        var prepared=OutboundMessagePreparation.ForExisting("conversation","  exact text\n").Message!;
        var result=await coordinator.SendAsync(prepared);

        Assert.Equal(SendDisposition.Accepted,result.Disposition);
        Assert.Same(prepared,provider.LastPrepared);
        Assert.Equal(0,provider.OldMethodCalls);

        var creation=OutboundMessagePreparation.ForNew("new conversation").Message!;
        await coordinator.CreateAsync(creation);
        Assert.Same(creation,provider.LastPrepared);
    }

    [Fact]
    public async Task InvalidPreparedMessageNeverCallsProvider()
    {
        var provider=new RecordingProvider();
        var coordinator=new ConversationCommandCoordinator(provider);
        var bad=OutboundMessagePreparation.ForExisting("id","hello").Message!
            with {ClientMessageId="not a guid"};
        Assert.Equal(SendDisposition.Rejected,
            (await coordinator.SendAsync(bad)).Disposition);
        Assert.Equal(0,provider.OldMethodCalls);
        Assert.Null(provider.LastPrepared);
    }
}
