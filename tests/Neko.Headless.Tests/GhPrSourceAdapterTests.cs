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
    public async Task RelativeOrMissingExecutableCannotRunExternalCommands()
    {
        Assert.Throws<ArgumentException>(() => new GhPrSourceAdapter("gh"));
        Assert.Throws<ArgumentException>(() =>
            new GhPrSourceAdapter(Path.Combine(Path.GetTempPath(),
                "missing-executable-" + Guid.NewGuid().ToString("N"))));

        var disabled = new GhPrSourceAdapter(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            disabled.ReadAsync("sample/project", 1, CancellationToken.None));
    }
}
