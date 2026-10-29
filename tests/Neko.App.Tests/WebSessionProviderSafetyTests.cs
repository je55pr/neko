using System.IO;
using Neko;

namespace Neko.App.Tests;

public sealed class WebSessionProviderSafetyTests
{
    private static WebSessionConversationProvider.Settings Config(
        bool experimental=false,string url="https://provider.example/")=>
        new(new Uri(url),Path.Combine(Path.GetTempPath(),"NekoWebSessionTest"),
            "/conversation/","form [contenteditable='true']",
            "form button[type=submit]","[data-role=user]",
            "[data-role=assistant]",experimental);

    [Fact]
    public void OnlyHttpsOriginWithRootHomeAndAbsoluteProfileIsAllowed()
    {
        Assert.True(Config().Valid);
        Assert.False(Config(url:"http://provider.example/").Valid);
        Assert.False(Config(url:"https://provider.example/some-page").Valid);
        Assert.False((Config() with {ProfileDirectory="relative"}).Valid);
        Assert.False((Config() with {RoutePrefix="//other-host/"}).Valid);
        Assert.False((Config() with {RoutePrefix="/../"}).Valid);
        Assert.False((Config() with {SubmitSelector=""}).Valid);
        Assert.False((Config() with {ComposerSelector=new string('x',201)}).Valid);
    }

    [Fact]
    public async Task BrowserSendingIsOptInAndCannotCreateAWindowWhenDisabled()
    {
        using var provider=new WebSessionConversationProvider(Config());
        var first=await provider.CreateAsync("safe test",CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected,first.Disposition);
        Assert.Equal("send_not_enabled",first.Reason);

        var existing=await provider.SendAsync("conversation","hello",CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected,existing.Disposition);
        Assert.Equal("send_not_enabled",existing.Reason);

        var field=typeof(WebSessionConversationProvider)
            .GetField("_window",System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(field.GetValue(provider));
    }

    [Fact]
    public void RejectsCredentialedOrNonCanonicalConfiguredOrigins()
    {
        Assert.False(Config(url:"https://name:pass"+(char)64+"provider.example/").Valid);
        Assert.False(Config(url:"https://provider.example/?session=value").Valid);
        Assert.False(Config(url:"https://provider.example/#fragment").Valid);
        Assert.False(Config(url:"https://provider.example:8443/").Valid);
        Assert.False((Config() with {UserMessageSelector=string.Concat("user",(char)0,"message")}).Valid);
        Assert.False((Config() with {ActiveGenerationSelector=""}).Valid);
        Assert.False((Config() with
            {ActiveGenerationSelector=new string('a',201)}).Valid);
        Assert.True((Config() with
            {ActiveGenerationSelector="button[aria-label='Stop']"}).Valid);
    }

    [Theory]
    [InlineData(0,1,true,true)]
    [InlineData(2,3,true,true)]
    [InlineData(2,2,true,false)]
    [InlineData(2,3,false,false)]
    [InlineData(2,1,true,false)]
    public void OnlyANewMatchingUserTurnCanAcknowledgeDelivery(
        int oldCount,int newCount,bool latestMatches,bool expected)
    {
        Assert.Equal(expected,WebSessionConversationProvider.IsFreshUserTurn(
            oldCount,newCount,latestMatches));
    }

    [Theory]
    [InlineData(0,1,true)]
    [InlineData(5,6,true)]
    [InlineData(5,5,false)]
    [InlineData(5,4,false)]
    public void OldAssistantTurnsCannotBeReportedAsNewReplies(
        int oldCount,int newCount,bool expected)
    {
        Assert.Equal(expected,WebSessionConversationProvider.IsFreshAssistantTurn(
            oldCount,newCount));
    }

    [Fact]
    public void ExpectedPathChecksAreExact()
    {
        Assert.True(WebSessionConversationProvider.IsExpectedPath(
            "/conversation/","/conversation/"));
        Assert.False(WebSessionConversationProvider.IsExpectedPath(
            "/conversation/other","/conversation/"));
        Assert.False(WebSessionConversationProvider.IsExpectedPath(
            "/conversation/one","/conversation/two"));
        Assert.False(WebSessionConversationProvider.IsExpectedPath(
            null,"/conversation/"));
    }
}
