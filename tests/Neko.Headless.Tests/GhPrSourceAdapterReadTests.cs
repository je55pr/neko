using System.Text.Json;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class GhPrSourceAdapterReadTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OldHead = "cccccccccccccccccccccccccccccccccccccccc";

    [Theory]
    [InlineData("COMMENTED", true, "", true, true)]
    [InlineData("APPROVED", true, "", true, false)]
    [InlineData("COMMENTED", false, null, true, false)]
    [InlineData("COMMENTED", true, null, true, false)]
    [InlineData("COMMENTED", true, " ", true, false)]
    [InlineData("COMMENTED", true, "P1: defect", true, false)]
    [InlineData("COMMENTED", true, "", false, false)]
    public async Task ReadAsyncClassifiesOnlyExplicitCurrentHeadCommentedEmptyReview(
        string verdict, bool hasBody, string? body, bool onCurrentHead,
        bool expectedClear)
    {
        var adapter = FakeSource(verdict, hasBody, body, onCurrentHead);
        var status = await adapter.ReadAsync("sample/project", 7,
            CancellationToken.None);
        var review = Assert.Single(status.Reviews);
        Assert.Equal(expectedClear, review.AutomatedNoFindings);
        Assert.Equal(expectedClear ||
            !string.IsNullOrWhiteSpace(body), review.Substantive);
        Assert.Equal(onCurrentHead ? Head : OldHead, review.HeadSha);
        Assert.Equal("sample/project", status.Repository);
        Assert.Equal(Head, status.HeadSha);
        Assert.Empty(status.InlineFindings!);
        Assert.Equal(0, status.UnresolvedThreads);
    }

    [Fact]
    public async Task ReadAsyncPreservesUnlinkedReanchoredCurrentHeadBotFinding()
    {
        var adapter = FakeSource("COMMENTED", true, "", true,
            commentOriginalHead: Head);
        var status = await adapter.ReadAsync("sample/project", 7,
            CancellationToken.None);
        var review = Assert.Single(status.Reviews);
        Assert.False(review.AutomatedNoFindings);
        Assert.False(review.Substantive);
        var finding = Assert.Single(status.InlineFindings!);
        Assert.Equal(99, finding.CommentId);
        Assert.Equal(Head, finding.OriginalHeadSha);
        Assert.Equal("review-bot", finding.Author);
    }

    [Fact]
    public async Task ReadAsyncDoesNotTreatReanchoredOldHeadFindingAsNew()
    {
        var adapter = FakeSource("COMMENTED", true, "", true,
            commentOriginalHead: OldHead);
        var status = await adapter.ReadAsync("sample/project", 7,
            CancellationToken.None);
        Assert.True(Assert.Single(status.Reviews).AutomatedNoFindings);
        Assert.Empty(status.InlineFindings!);
    }

    private static GhPrSourceAdapter FakeSource(string verdict,
        bool hasBody, string? body, bool onCurrentHead,
        string? commentOriginalHead = null)
    {
        var policy = new PrReviewPolicy([], "review-check", "review-bot",
            AllowedRepositories: ["sample/project"],
            AllowVerifiedEmptyAutomatedReview: true);
        object review = hasBody
            ? new {
                id = 55, state = verdict, body,
                commit_id = onCurrentHead ? Head : OldHead,
                submitted_at = "2026-10-31T17:40:00Z",
                html_url = "https://github.com/sample/project/pull/7#review-55",
                user = new { login = "review-bot" }
            }
            : new {
                id = 55, state = verdict,
                commit_id = onCurrentHead ? Head : OldHead,
                submitted_at = "2026-10-31T17:40:00Z",
                html_url = "https://github.com/sample/project/pull/7#review-55",
                user = new { login = "review-bot" }
            };
        object[] comments = commentOriginalHead is null ? [] : [
            new {
                id = 99, body = "P1: current-head finding",
                commit_id = Head,
                original_commit_id = commentOriginalHead,
                pull_request_review_id = (long?)null,
                html_url = "https://github.com/sample/project/pull/7#comment-99",
                user = new { login = "review-bot" }
            }
        ];

        Task<JsonDocument> Respond(CancellationToken cancellation, string[] args)
        {
            cancellation.ThrowIfCancellationRequested();
            object response = args[0] switch {
                "repos/sample/project/pulls/7" => new {
                    head = new { sha = Head },
                    @base = new { sha = Base, repo = new { full_name = "sample/project" } },
                    state = "open", merged = false, mergeable = true,
                    id = 7, user = new { login = "author" }
                },
                "repos/sample/project/commits/" + Head +
                    "/check-runs?per_page=100&filter=all" => new {
                        total_count = 0, check_runs = Array.Empty<object>()
                    },
                "repos/sample/project/commits/" + Head +
                    "/statuses?per_page=100" => Array.Empty<object>(),
                "repos/sample/project/pulls/7/reviews?per_page=100" =>
                    new[] { review },
                "repos/sample/project/pulls/7/comments?per_page=100" =>
                    comments,
                "graphql" => new {
                    data = new { repository = new {
                        pullRequest = new { reviewThreads = new {
                            nodes = Array.Empty<object>(),
                            pageInfo = new { hasNextPage = false }
                        } }
                    } }
                },
                _ => throw new InvalidOperationException(
                    "Unexpected synthetic source call: " + args[0])
            };
            return Task.FromResult(
                JsonDocument.Parse(JsonSerializer.Serialize(response)));
        }

        return new GhPrSourceAdapter(policy, Respond);
    }
}
