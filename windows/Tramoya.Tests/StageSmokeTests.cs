using Tramoya.Core;
using Tramoya.Stage;

namespace Tramoya.Tests;

/// <summary>
/// The tests that open a real Chromium through <see cref="Browser.OpenStageAsync"/>.
/// They prove the Playwright package, the downloaded browser, the overlay and the video
/// recording work end to end on this machine. Opt-in through
/// <see cref="BrowserFactAttribute"/>: they need <c>playwright.ps1 install chromium</c>
/// and <c>PLAYWRIGHT_BROWSERS_PATH</c> set first.
/// </summary>
public sealed class StageSmokeTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "tramoya-smoke-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A <c>data:</c> page needs no server. Percent-encoding keeps <c>#</c> (a fragment
    /// otherwise) and spaces out of the URL itself.
    /// </summary>
    private static string DataUrl(string html) => "data:text/html," + Uri.EscapeDataString(html);

    [BrowserFact]
    public async Task OpenStageClicksAButtonAndRecordsMarks()
    {
        var url = DataUrl("<button id=b>hi</button>");
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

    /// <summary>
    /// The take itself: with <c>videoDir</c> set, Playwright records the context and only
    /// writes the <c>.webm</c> when the context closes, which is what disposing the session
    /// does first. The director copies that file as <c>take.webm</c>, so the path reported
    /// while the session is open has to be the file that exists after it is gone.
    /// </summary>
    [BrowserFact]
    public async Task OpenStageRecordsAVideoAndDrivesThePage()
    {
        var videoDir = Path.Combine(TempDir(), "raw");
        var url = DataUrl(
            "<button id=b onclick=\"document.getElementById('o').textContent='clicked'\">hi</button>" +
            "<input id=i aria-label=name><p id=o></p>");
        var pacing = new Pacing(pace: 1.0, sleep: _ => Task.CompletedTask);
        var marks = new Marks();

        string? videoPath;
        var session = await Browser.OpenStageAsync(url, pacing, marks, videoDir: videoDir, viewport: (640, 360), headless: true);
        try
        {
            await session.Stage.GotoAsync(url);
            Assert.Equal(1, await session.Page.Locator("#tramoya-cursor").CountAsync());

            using (marks.Scene("smoke", "Smoke"))
            {
                await session.Stage.ClickAsync("#b");
                await session.Stage.TypeIntoAsync("#i", "hola");
            }
            Assert.Equal("clicked", await session.Stage.ReadAsync("#o"));
            Assert.Equal("hola", await session.Page.InputValueAsync("#i"));
            Assert.Equal("hi", await session.Stage.ReadAsync("#b"));

            var tree = await session.Stage.SnapshotAsync();
            Assert.Contains("button", tree);
            Assert.Contains("textbox", tree);

            videoPath = await session.VideoPathAsync();
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.Equal(new[] { "scene:smoke:start", "scene:smoke:end" }, marks.Rows.Select(m => m.Key));
        Assert.NotNull(videoPath);
        Assert.Equal(".webm", Path.GetExtension(videoPath));
        Assert.True(File.Exists(videoPath), $"video not written: {videoPath}");
        Assert.True(new FileInfo(videoPath).Length > 0, "video file is empty");
        var written = Assert.Single(Directory.GetFiles(videoDir, "*.webm"));
        Assert.Equal(Path.GetFullPath(written), Path.GetFullPath(videoPath));
    }
}
