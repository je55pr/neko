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
