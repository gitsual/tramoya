using Tramoya.Core;
using Tramoya.Stage;

namespace Tramoya.Tests;

/// <summary>
/// The one test that opens a real Chromium through <see cref="Browser.OpenStageAsync"/>.
/// It proves the Playwright package, the downloaded browser and the overlay work end to
/// end on this machine (the Python suite's <c>test_overlay_installs_when_page_has_a_body</c>
/// lives here too). Skipped in the normal run: it needs
/// <c>playwright.ps1 install chromium</c> and <c>PLAYWRIGHT_BROWSERS_PATH</c> set first.
/// </summary>
public sealed class StageSmokeTests
{
    [Fact(Skip = "launches Chromium; run by hand")]
    public async Task OpenStageClicksAButtonAndRecordsMarks()
    {
        const string url = "data:text/html,<button id=b>hi</button>";
        var pacing = new Pacing(pace: 1.0, sleep: _ => Task.CompletedTask);
        var marks = new Marks();

        await using var session = await Browser.OpenStageAsync(url, pacing, marks, headless: true);

        // The overlay must be there before any app script runs (an init script once threw
        // `appendChild of null` because <html> did not exist yet).
        var errors = new List<string>();
        session.Page.PageError += (_, message) => errors.Add(message);
        await session.Stage.GotoAsync(url);
        Assert.Equal("function", await session.Page.EvaluateAsync<string>("typeof window.__tramoyaCaption"));
        await session.Stage.CaptionAsync("scene one");
        Assert.Equal("scene one", await session.Page.EvaluateAsync<string>("document.getElementById('tramoya-caption').textContent"));
        Assert.Empty(errors);

        using (marks.Scene("smoke", "Smoke"))
        {
            await session.Stage.ClickAsync("#b");
        }
        Assert.Equal("hi", await session.Stage.ReadAsync("#b"));
        Assert.Equal(new[] { "scene:smoke:start", "scene:smoke:end" }, marks.Rows.Select(m => m.Key));
        Assert.Null(await session.VideoPathAsync());
    }
}
