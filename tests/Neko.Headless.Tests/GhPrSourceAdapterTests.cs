using System.Text.Json;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class GhPrSourceAdapterTests
{
    [Fact]
    public void UnassociatedAndNullReviewIdsDoNotInvalidateSnapshot()
    {
        using var document = JsonDocument.Parse("""
            [
              {"body":"standalone","pull_request_review_id":null},
              {"body":"without id"},
              {"body":"malformed","pull_request_review_id":"7"},
              {"body":"","pull_request_review_id":99},
              {"body":"review comment","pull_request_review_id":7},
              {"body":"review comment again","pull_request_review_id":7}
            ]
            """);
        var ids = GhPrSourceAdapter.AssociatedReviewIds(document.RootElement);
        Assert.Single(ids);
        Assert.Contains(7, ids);
    }

    [Fact]
    public void DeletedReviewAuthorsRemainDistinctAndCannotMimicAccounts()
    {
        using var reviews = JsonDocument.Parse("""
            [
              {"id":1001,"user":null},
              {"id":1002},
              {"id":1003,"user":{"login":"real-reviewer"}}
            ]
            """);
        var identities = reviews.RootElement.EnumerateArray()
            .Select(GhPrSourceAdapter.ReviewerIdentity).ToArray();
        Assert.Equal("deleted-review:1001", identities[0]);
        Assert.Equal("deleted-review:1002", identities[1]);
        Assert.NotEqual(identities[0], identities[1]);
        Assert.Equal("real-reviewer", identities[2]);
    }

    [Fact]
    public void DeletedPullRequestAuthorKeepsSourceReadable()
    {
        using var missingAuthor = JsonDocument.Parse("""
            {"id":12345,"user":null}
            """);
        Assert.Equal("deleted-pr-author:12345",
            GhPrSourceAdapter.PullAuthorIdentity(missingAuthor.RootElement));
        using var author = JsonDocument.Parse("""
            {"id":12345,"user":{"login":"repository-author"}}
            """);
        Assert.Equal("repository-author",
            GhPrSourceAdapter.PullAuthorIdentity(author.RootElement));
    }

    [Fact]
    public void AutomatedReviewNeedsConfiguredVerifiedNoFindingsLine()
    {
        const string marker = "Actionable comments posted: 0";
        Assert.True(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 0**\n", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 3**\n", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "Status: Actionable comments posted: 0 (old)\n", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "> Actionable comments posted: 0\n", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "```\nActionable comments posted: 0\n```\n", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "~~~\nActionable comments posted: 0\n~~~\n", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "    Actionable comments posted: 0\n", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 2**\n<!--\nActionable comments posted: 0\n-->",
            marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 3**\n**Actionable comments posted: 0**",
            marker));
        Assert.True(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "<!-- automated-review-metadata -->\n**Actionable comments posted: 0**\n",
            marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 0**\n**Actionable comments posted: 3**",
            marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 0**\n<!-- Actionable comments posted: 2 -->",
            marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "<!--\nActionable comments posted: 0\n-->\n**Actionable comments posted: 2**",
            marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 0**", null));
    }

    [Fact]
    public void WorkflowRunIdentityRequiresTrustedHostAndExactRepository()
    {
        using var valid = JsonDocument.Parse("""
            {"app":{"slug":"github-actions"},
             "details_url":"https://github.com/Sample/Project/actions/runs/123/job/456"}
            """);
        Assert.Equal(123, GhPrSourceAdapter.GitHubActionsRunId(
            valid.RootElement, "sample/project"));
        Assert.Null(GhPrSourceAdapter.GitHubActionsRunId(
            valid.RootElement, "sample/another-project"));

        using var forged = JsonDocument.Parse("""
            {"app":{"slug":"github-actions"},
             "details_url":"https://attacker.example/sample/project/actions/runs/123/job/456"}
            """);
        Assert.Null(GhPrSourceAdapter.GitHubActionsRunId(
            forged.RootElement, "sample/project"));

        using var otherApp = JsonDocument.Parse("""
            {"app":{"slug":"independent-checker"},
             "details_url":"https://github.com/sample/project/actions/runs/123/job/456"}
            """);
        Assert.Null(GhPrSourceAdapter.GitHubActionsRunId(
            otherApp.RootElement, "sample/project"));
    }

    [Fact]
    public async Task ExternalPrOperationsNeverAcceptUnlistedRepositories()
    {
        var policy = new Neko.PrReviewPolicy(["build"], "automated-check",
            "automated-reviewer", AllowedRepositories: ["Sample/Project"]);
        var adapter = new GhPrSourceAdapter(null, policy);
        Assert.Equal("Sample/Project", adapter.AuthorizedRepository("sample/project"));
        Assert.Equal("Sample/Project", adapter.AuthorizedRepository("SAMPLE/PROJECT"));
        Assert.Throws<ArgumentException>(() => adapter.AuthorizedRepository("other/project"));
        Assert.Throws<ArgumentException>(() => adapter.AuthorizedRepository("../project"));
        Assert.Throws<ArgumentException>(() => adapter.AuthorizedRepository("sample/project/other"));
        Assert.Throws<ArgumentException>(() =>
            new GhPrSourceAdapter(null).AuthorizedRepository("sample/project"));

        const string head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.ReadAsync("untrusted/project", 7, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.PostReviewAsync("untrusted/project", 7, head,
                "Substantive test review content.", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.MergeAsync("untrusted/project", 7, head, CancellationToken.None));
    }

    [Fact]
    public async Task RelativeOrMissingExecutableCannotRunExternalCommands()
    {
        Assert.Throws<ArgumentException>(() => new GhPrSourceAdapter("gh"));
        Assert.Throws<ArgumentException>(() =>
            new GhPrSourceAdapter(Path.Combine(Path.GetTempPath(),
                "missing-executable-" + Guid.NewGuid().ToString("N"))));

        var policy = new Neko.PrReviewPolicy(["build"], "automated-check",
            "automated-reviewer", AllowedRepositories: ["sample/project"]);
        var disabled = new GhPrSourceAdapter(null, policy);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            disabled.ReadAsync("sample/project", 1, CancellationToken.None));
    }
}
