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
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "No actionable findings\nP1: A critical source bug survives.",
            "No actionable findings"));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 0**\nP1: Source defect still open.",
            marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable comments posted: 0**\nBLOCKERS: Unresolved safety concern",
            marker));
    }

    [Fact]
    public void BotClearMarkerRejectsMarkdownBracketedSeverityFindings()
    {
        const string marker = "Actionable comments posted: 0";
        foreach (var finding in new[] {
            "### [P1] Unsafe merge path",
            "- **[P2]** Unverified review provenance",
            "> [P0] Critical authentication bypass",
            "### [p3]: Missing test for restart",
            "### **P1**: Claim hides a finding",
            "P2. Broken adapter path",
            "P3, Another unresolved defect"
        })
            Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
                "**Actionable comments posted: 0**\n" + finding, marker));
    }

    [Fact]
    public void BotZeroBlockerHeaderDoesNotMaskLaterInlineFindings()
    {
        const string marker = "No blockers: 0";
        Assert.True(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**No blockers: 0**\nEverything checked clean.", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**No blockers: 0**\nA late note reports P1: Unsafe merge.", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**No blockers: 0**\nBLOCKERS: Another defect.", marker));
        Assert.False(GhPrSourceAdapter.VerifiedNoFindingsLine(
            "**Actionable findings: 0**\n1. P1: Unsafe merge path remains.",
            "Actionable findings: 0"));
    }

    [Fact]
    public void CheckRunSnapshotRequestsAllSameHeadRuns()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.Equal("repos/sample/project/commits/" + sha +
            "/check-runs?per_page=100&filter=all",
            GhPrSourceAdapter.CompleteCheckRunsRoute("sample/project", sha));
    }

    [Fact]
    public void IndividualCommitStatusesKeepAllPublishersAndFailClosedOnFullFirstPage()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.Equal("repos/sample/project/commits/" + sha +
            "/statuses?per_page=100",
            GhPrSourceAdapter.IndividualCommitStatusesRoute("sample/project", sha));

        // The combined endpoint hides an earlier trusted status when a
        // different publisher posts a later status in the same context.
        using var complete = JsonDocument.Parse("""
            [{"id":22,"context":"build","creator":{"id":999}},
             {"id":21,"context":"build","creator":{"id":100}}]
            """);
        var statuses = GhPrSourceAdapter.VerifiedIndividualStatusPage(
            complete.RootElement);
        Assert.Equal(2, statuses.GetArrayLength());
        Assert.Equal(100, statuses[1].GetProperty("creator").GetProperty("id").GetInt32());

        using var empty = JsonDocument.Parse("[]");
        Assert.Equal(0, GhPrSourceAdapter.VerifiedIndividualStatusPage(
            empty.RootElement).GetArrayLength());
        using var invalid = JsonDocument.Parse("{}");
        Assert.Throws<InvalidOperationException>(() =>
            GhPrSourceAdapter.VerifiedIndividualStatusPage(invalid.RootElement));
        using var malformed = JsonDocument.Parse("null");
        Assert.Throws<InvalidOperationException>(() =>
            GhPrSourceAdapter.VerifiedIndividualStatusPage(malformed.RootElement));
        using var atLimit = JsonDocument.Parse(JsonSerializer.Serialize(
            Enumerable.Range(0, 100).Select(id => new { id }).ToArray()));
        Assert.Throws<InvalidOperationException>(() =>
            GhPrSourceAdapter.VerifiedIndividualStatusPage(atLimit.RootElement));
    }

    [Fact]
    public void SameHeadCheckRunSnapshotsCannotHideEarlierRuns()
    {
        using var allRuns = JsonDocument.Parse("""
            {"total_count":2,"check_runs":[{"id":11},{"id":12}]}
            """);
        Assert.Equal(2, GhPrSourceAdapter.VerifiedCompletePage(
            allRuns.RootElement, "check_runs").GetArrayLength());
        using var lostRun = JsonDocument.Parse("""
            {"total_count":2,"check_runs":[{"id":12}]}
            """);
        Assert.Throws<InvalidOperationException>(() =>
            GhPrSourceAdapter.VerifiedCompletePage(lostRun.RootElement, "check_runs"));
    }

    [Fact]
    public void InlineReviewCommentsOverrideAnyNoFindingsSummary()
    {
        var linked = new HashSet<long> { 77 };
        var body = "**Actionable comments posted: 0**\nVerified checks and review.";
        const string marker = "Actionable comments posted: 0";
        Assert.False(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "automated-account", "automated-account", 77, linked, body, marker));
        Assert.True(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "automated-account", "automated-account", 78, linked, body, marker));
        Assert.False(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "another-account", "automated-account", 78, linked, body, marker));
    }

    [Fact]
    public void StandaloneBotCommentsOnOriginalHeadRemainFindingEvidence()
    {
        const string head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var comments = JsonDocument.Parse("""
            [
              {"id":15,"user":{"login":"review-bot"},
               "original_commit_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
               "commit_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
               "pull_request_review_id":null,
               "html_url":"https://github.com/example/pr#comment-15"},
              {"id":16,"user":{"login":"review-bot"},
               "original_commit_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
               "pull_request_review_id":100,
               "html_url":"https://github.com/example/pr#comment-16"},
              {"id":17,"user":{"login":"review-bot"},
               "original_commit_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
               "pull_request_review_id":null,
               "html_url":"https://github.com/example/pr#comment-17"},
              {"id":18,"user":{"login":"human"},
               "original_commit_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
               "pull_request_review_id":null,
               "html_url":"https://github.com/example/pr#comment-18"}
            ]
            """);
        var found = GhPrSourceAdapter.StandaloneAutomatedFindings(
            comments.RootElement, head, "review-bot");
        Assert.Single(found);
        Assert.Equal(15, found[0].CommentId);
        Assert.Equal(head, found[0].OriginalHeadSha);
        Assert.Contains("comment-15", found[0].Url);
        using var missingOriginal = JsonDocument.Parse("""
            [{"id":20,"user":{"login":"review-bot"},
              "html_url":"https://github.com/example/pr#comment-20"}]
            """);
        Assert.Throws<InvalidOperationException>(() =>
            GhPrSourceAdapter.StandaloneAutomatedFindings(
                missingOriginal.RootElement, head, "review-bot"));
    }

    [Theory]
    [InlineData("{\"body\":\"\"}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"body\":null}", false)]
    [InlineData("{\"body\":7}", false)]
    [InlineData("{\"body\":[]}", false)]
    [InlineData("{\"body\":\" \"}", false)]
    [InlineData("{\"body\":\"finding\"}", false)]
    public void OnlyExplicitJsonEmptyStringCanBeAZeroFindingReview(
        string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(valid, GhPrSourceAdapter.ExplicitEmptyReviewBody(
            document.RootElement));
    }

    [Fact]
    public void VerifiedEmptyAutomatedReviewRequiresOptInAndExhaustiveCommentEvidence()
    {
        var linked = new HashSet<long>();
        Assert.False(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "review-bot", "review-bot", 9, linked, "", null));
        Assert.True(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "review-bot", "review-bot", 9, linked, "", null,
            allowVerifiedEmptyReview: true, noAutomatedCommentsOnHead: true));
        Assert.False(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "review-bot", "review-bot", 9, linked, "", null,
            allowVerifiedEmptyReview: true, noAutomatedCommentsOnHead: false));
        Assert.False(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "review-bot", "other-bot", 9, linked, "", null,
            allowVerifiedEmptyReview: true, noAutomatedCommentsOnHead: true));
        Assert.False(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "review-bot", "review-bot", 9, new HashSet<long> { 9 }, "", null,
            allowVerifiedEmptyReview: true, noAutomatedCommentsOnHead: true));
        Assert.False(GhPrSourceAdapter.SourceAutomatedNoFindings(
            "review-bot", "review-bot", 9, linked, "Some prose", null,
            allowVerifiedEmptyReview: true, noAutomatedCommentsOnHead: true));

        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var olderBotComment = JsonDocument.Parse("""
            [{"user":{"login":"review-bot"},"commit_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","original_commit_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]
            """);
        Assert.True(GhPrSourceAdapter.NoAutomatedInlineCommentsOnHead(
            olderBotComment.RootElement, sha, "review-bot"));
        using var currentBotComment = JsonDocument.Parse("""
            [{"user":{"login":"review-bot"},"commit_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","original_commit_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]
            """);
        Assert.False(GhPrSourceAdapter.NoAutomatedInlineCommentsOnHead(
            currentBotComment.RootElement, sha, "review-bot"));
        using var unknownAuthor = JsonDocument.Parse("""
            [{"user":null,"original_commit_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]
            """);
        Assert.False(GhPrSourceAdapter.NoAutomatedInlineCommentsOnHead(
            unknownAuthor.RootElement, sha, "review-bot"));
        using var unknownRevision = JsonDocument.Parse("""
            [{"user":{"login":"other"}}]
            """);
        Assert.False(GhPrSourceAdapter.NoAutomatedInlineCommentsOnHead(
            unknownRevision.RootElement, sha, "review-bot"));
        using var malformedRevision = JsonDocument.Parse("""
            [{"user":{"login":"review-bot"},"original_commit_id":"bogus"}]
            """);
        Assert.False(GhPrSourceAdapter.NoAutomatedInlineCommentsOnHead(
            malformedRevision.RootElement, sha, "review-bot"));
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
