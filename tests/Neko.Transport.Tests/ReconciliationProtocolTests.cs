using System.Security.Cryptography;
using System.Text;
using Neko;
using Neko.Transport;

namespace Neko.Transport.Tests;

public sealed class ReconciliationProtocolTests
{
    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static (ReconciliationProbe Probe, VisibleConversation History) Example()
    {
        var messages = new VisibleConversationMessage[]
        {
            new("u0", VisibleMessageRole.User, "previous prompt"),
            new("a0", VisibleMessageRole.Assistant, "previous answer"),
            new("u1", VisibleMessageRole.User, "second"),
            new("a1", VisibleMessageRole.Assistant, "second answer")
        };
        return (new("thread", Digest("second"), ["u0", "a0"],
            AcceptedSubmission: true),
            new VisibleConversation("thread", "Example", messages));
    }

    [Fact]
    public void ExactAcceptedTurnAndIndependentStreamCompletionAreRequired()
    {
        var (probe, history) = Example();
        var verified = ProjectProvider.VerifyCompletedTurn(
            probe, history, "a1", "finished_successfully",
            "second answer", "a1", "COMPLETE");
        Assert.True(verified.VerifiedComplete);
        Assert.Equal("u1", verified.UserMessageId);
        Assert.Equal("a1", verified.AssistantMessageId);

        Assert.False(ProjectProvider.VerifyCompletedTurn(
            probe, history, "a1", "finished_successfully",
            "second answer", "a1", "IN_PROGRESS").VerifiedComplete);
        Assert.False(ProjectProvider.VerifyCompletedTurn(
            probe, history, "a1", "finished_successfully",
            "second answer", "a0", "COMPLETE").VerifiedComplete);
        Assert.False(ProjectProvider.VerifyCompletedTurn(
            probe, history, "a1", "pending",
            "second answer", "a1", "COMPLETE").VerifiedComplete);
    }

    [Fact]
    public void UnmatchedHistoryOrMissingDispatchCorrelationCannotRestore()
    {
        var (probe, history) = Example();
        Assert.False(ProjectProvider.VerifyCompletedTurn(
            probe with { PromptSha256 = Digest("different") },
            history, "a1", "finished_successfully",
            "second answer", "a1", "COMPLETE").VerifiedComplete);
        Assert.False(ProjectProvider.VerifyCompletedTurn(
            probe with { BaselineMessageIds = ["u0", "unknown"] },
            history, "a1", "finished_successfully",
            "second answer", "a1", "COMPLETE").VerifiedComplete);
        Assert.Equal("missing_dispatch_correlation",
            ProjectProvider.VerifyCompletedTurn(
                probe with { AcceptedSubmission = false },
                history, "a1", "finished_successfully",
                "second answer", "a1", "COMPLETE").Reason);
        Assert.False(ProjectProvider.VerifyCompletedTurn(
            probe with { CorrelatedUserMessageId = "not-u1" },
            history, "a1", "finished_successfully",
            "second answer", "a1", "COMPLETE").VerifiedComplete);

        var extra = history with {
            Messages = [..history.Messages,
                new VisibleConversationMessage("u2", VisibleMessageRole.User, "external")]
        };
        Assert.Equal("ambiguous_remote_turn",
            ProjectProvider.VerifyCompletedTurn(
                probe, extra, "a1", "finished_successfully",
                "second answer", "a1", "COMPLETE").Reason);
    }

    [Fact]
    public void ServerCorrelatedCreationCanBeVerifiedWithoutAnAcceptedReceipt()
    {
        var messages = new VisibleConversationMessage[] {
            new("u0", VisibleMessageRole.User, "first"),
            new("a0", VisibleMessageRole.Assistant, "reply")
        };
        var probe = new ReconciliationProbe("thread", Digest("first"),
            [], CorrelatedCreation: true);
        Assert.True(ProjectProvider.VerifyCompletedTurn(
            probe, new VisibleConversation("thread", "Created", messages),
            "a0", "finished_successfully", "reply", "a0",
            "COMPLETE").VerifiedComplete);
    }

    [Fact]
    public void AuthoritativeRemoteFailureIsNotObserverTimeout()
    {
        var (probe, history) = Example();
        var rejected = ProjectProvider.VerifyCompletedTurn(probe, history,
            "a1", "failed", "partial", "a1", "FAILED");
        Assert.False(rejected.VerifiedComplete);
        Assert.Equal("remote_terminal_failure", rejected.Reason);
        Assert.Equal(PublicUpdateOperation.ObservationExpired,
            ProjectProvider.ObserverTerminalOperation(false, true));
        Assert.Equal(PublicUpdateOperation.Fail,
            ProjectProvider.ObserverTerminalOperation(false, false));
        Assert.Equal(PublicUpdateOperation.Complete,
            ProjectProvider.ObserverTerminalOperation(true, true));
    }
}
