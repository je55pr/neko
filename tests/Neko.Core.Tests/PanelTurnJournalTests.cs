using System.IO;
using System.Text.Json;
using Neko;

namespace Neko.Core.Tests;

public sealed class PanelTurnJournalTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;

    private static string ChatId()=>Guid.NewGuid().ToString("D");

    private static string TemporaryCachePath()=>
        Path.Combine(Path.GetTempPath(),"Neko-Journal-Test-"+
            Guid.NewGuid().ToString("N"),"journal.json");

    [Fact]
    public void AcceptedTurnSurvivesRestartWithoutClaimingServerConfirmation()
    {
        var file=TemporaryCachePath();
        var id=ChatId();
        try
        {
            var journal=new PanelTurnJournal();
            journal.OpenLocalCache(file);
            journal.Accepted(id,"Question");
            Assert.True(journal.StorageHealthy);
            Assert.True(journal.HasLocalDisplay(id));
            Assert.Equal(1,journal.Version(id));
            Assert.Equal("local_user",Assert.Single(journal.Merge(id,[])).Kind);

            var reopened=new PanelTurnJournal();
            reopened.OpenLocalCache(file);
            var pending=Assert.Single(reopened.Merge(id,[]));
            Assert.StartsWith("neko-local:",pending.Id);
            Assert.Equal("Question",pending.Text);
            Assert.Equal("local_user",pending.Kind);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!,true); }
    }

    [Fact]
    public void CompletedReplyIsRecordedOnceAndRejectedWhenUnconfirmed()
    {
        var id=ChatId();
        var journal=new PanelTurnJournal();
        journal.Accepted(id,"Question","previous-reply");

        var before=new PanelTurnJournal.ConfirmedReply(
            id,"reply-new","Answer",Now,false);
        Assert.False(journal.Observe(before));
        Assert.False(journal.Observe(before with {Completed=true,HasError=true}));
        Assert.False(journal.Observe(before with {
            Completed=true,MessageId="previous-reply"}));
        Assert.False(journal.Observe(before with {
            Completed=true,Text=new string('x',250_001)}));

        var finished=before with {Completed=true};
        Assert.True(journal.Observe(finished));
        Assert.False(journal.Observe(finished));
        var merged=journal.Merge(id,[]);
        Assert.Equal(2,merged.Length);
        Assert.Equal("user",merged[0].Role);
        Assert.Equal("assistant",merged[1].Role);
        Assert.Equal("Answer",merged[1].Text);
    }

    [Fact]
    public void DiskHistoryContainsOnlyUserVisibleMessages()
    {
        var id=ChatId();
        var file=TemporaryCachePath();
        try
        {
            var journal=new PanelTurnJournal();
            journal.OpenLocalCache(file);
            journal.RememberHistory(id,[
                new ConversationMessage{Id="user",Role="user",Text="Hello"},
                new ConversationMessage{Id="reason",Role="assistant",
                    Channel="analysis",Text="private-reasoning-fixture"},
                new ConversationMessage{Id="tool",Role="tool",
                    Text="private-tool-arguments-fixture"},
                new ConversationMessage{Id="final",Role="assistant",
                    Channel="final",Text="Visible answer"},
                new ConversationMessage{Id="progress",Role="assistant",
                    Channel="commentary",Kind="progress",Text="Working"},
                new ConversationMessage{Id="hidden-comment",Role="assistant",
                    Channel="commentary",Kind="message",Text="not-a-final"}
            ]);

            var stored=File.ReadAllText(file);
            Assert.DoesNotContain("private-reasoning-fixture",stored);
            Assert.DoesNotContain("private-tool-arguments-fixture",stored);
            Assert.DoesNotContain("not-a-final",stored);
            var reopened=new PanelTurnJournal();
            reopened.OpenLocalCache(file);
            var messages=reopened.Merge(id,[]);
            Assert.Equal(3,messages.Length);
            Assert.Contains(messages,m=>m.Text=="Visible answer");
            Assert.Contains(messages,m=>m.Text=="Working");
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!,true); }
    }

    [Fact]
    public void ForgetConversationPurgesPersistentDisplayCache()
    {
        var id=ChatId();
        var file=TemporaryCachePath();
        try
        {
            var journal=new PanelTurnJournal();
            journal.OpenLocalCache(file);
            journal.Accepted(id,"Will be erased");
            journal.RememberHistory(id,[
                new ConversationMessage{Id="old-user",Role="user",
                    Text="Saved before deletion"}
            ]);
            journal.ForgetChat(id);
            Assert.False(journal.HasLocalDisplay(id));
            Assert.Empty(journal.Merge(id,[]));

            var reopened=new PanelTurnJournal();
            reopened.OpenLocalCache(file);
            Assert.True(reopened.StorageHealthy);
            Assert.False(reopened.HasLocalDisplay(id));
            Assert.DoesNotContain("Will be erased",File.ReadAllText(file));
            Assert.DoesNotContain("Saved before deletion",File.ReadAllText(file));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!,true); }
    }

    [Fact]
    public void MalformedCacheIsRejectedWithoutInventingData()
    {
        var file=TemporaryCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            File.WriteAllText(file,"{not valid json");
            var journal=new PanelTurnJournal();
            journal.OpenLocalCache(file);
            Assert.False(journal.StorageHealthy);
            Assert.Empty(journal.Merge(ChatId(),[]));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!,true); }
    }

    [Fact]
    public void AuthoritativeHistoryAvoidsDuplicateLocalPrompt()
    {
        var id=ChatId();
        var journal=new PanelTurnJournal();
        journal.Accepted(id,"Hello");
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;
        var saved=new[]{
            new ConversationMessage{Id="server-user",Role="user",
                Text="Hello",Created=now},
            new ConversationMessage{Id="saved-reply",Role="assistant",
                Channel="final",Text="Answer",ParentId="server-user",
                Created=now-3}
        };
        var merged=journal.Merge(id,saved);
        Assert.Equal(2,merged.Length);
        Assert.Equal(new[]{"server-user","saved-reply"},
            merged.Select(m=>m.Id));
    }
}
