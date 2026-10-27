using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Tramoya.Core;
using Tramoya.Stage;

namespace Tramoya.Tests;

/// <summary>
/// Port of <c>tests/test_stage.py</c>: the overlay script, the Stage hand, the scene
/// registry, the readiness probe, the session teardown and the app-under-test process,
/// all without a browser except the one <see cref="BrowserFactAttribute"/> test.
/// The Python fake page records every call; here the page is an NSubstitute double and
/// the recording lists live on <see cref="FakeBoundPage"/>.
/// </summary>
public sealed class StageTests
{
    [Fact]
    public void OverlayJsInstallsTramoyaIdsAndKeepsCaptionNonInteractive()
    {
        Assert.Contains("#tramoya-cursor", Stage.Stage.OverlayJs);
        Assert.Contains("#tramoya-ripple", Stage.Stage.OverlayJs);
        Assert.Contains("#tramoya-caption", Stage.Stage.OverlayJs);
        Assert.Contains("window.__tramoyaCaption", Stage.Stage.OverlayJs);
        Assert.Contains("pointer-events: none", Stage.Stage.OverlayJs);
        Assert.Contains("mousemove", Stage.Stage.OverlayJs);
        Assert.Contains("mousedown", Stage.Stage.OverlayJs);
    }

    // --------------------------------------------------------------------- Stage

    /// <summary>
    /// A minimal stand-in for a Playwright page, recording every call a Stage makes so
    /// the tests can assert on behaviour without a browser. Locators are registered up
    /// front (the Python fake creates them on demand) because a substitute is configured
    /// outside the call it answers.
    /// </summary>
    private sealed class FakeBoundPage
    {
        public IPage Page { get; } = Substitute.For<IPage>();
        public IMouse Mouse { get; } = Substitute.For<IMouse>();
        public List<(string Script, object? Arg)> Evaluated { get; } = new();
        public List<string> Clicked { get; } = new();
        public List<(string Selector, string Text)> Typed { get; } = new();
        public List<(string Selector, string Value)> Selected { get; } = new();
        public Dictionary<string, ILocator> Locators { get; } = new();
        public Dictionary<string, ILocator> TextLocators { get; } = new();

        public FakeBoundPage()
        {
            Page.Mouse.Returns(Mouse);
            Page.EvaluateAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(ci =>
            {
                Evaluated.Add((ci.ArgAt<string>(0), ci.ArgAt<object?>(1)));
                return Task.FromResult<JsonElement?>(null);
            });
        }

        public ILocator Locator(string selector, bool visible = true)
        {
            var loc = NewLocator(selector, visible);
            Locators[selector] = loc;
            Page.Locator(selector).Returns(loc);
            return loc;
        }

        public ILocator TextLocator(string text, bool visible = true)
        {
            var loc = NewLocator(text, visible);
            TextLocators[text] = loc;
            Page.GetByText(text, Arg.Any<PageGetByTextOptions>()).Returns(loc);
            return loc;
        }

        private ILocator NewLocator(string selector, bool visible)
        {
            var loc = Substitute.For<ILocator>();
            // Playwright locators are chained with `.First`; the same double answers both.
            loc.First.Returns(loc);
            var box = visible ? new LocatorBoundingBoxResult { X = 10, Y = 20, Width = 100, Height = 40 } : null;
            loc.BoundingBoxAsync(Arg.Any<LocatorBoundingBoxOptions>()).Returns(Task.FromResult(box));
            loc.InnerTextAsync(Arg.Any<LocatorInnerTextOptions>()).Returns(Task.FromResult("some text"));
            loc.ClickAsync(Arg.Any<LocatorClickOptions>()).Returns(_ =>
            {
                Clicked.Add(selector);
                return Task.CompletedTask;
            });
            loc.PressSequentiallyAsync(Arg.Any<string>(), Arg.Any<LocatorPressSequentiallyOptions>()).Returns(ci =>
            {
                Typed.Add((selector, ci.ArgAt<string>(0)));
                return Task.CompletedTask;
            });
            loc.SelectOptionAsync(Arg.Any<string>(), Arg.Any<LocatorSelectOptionOptions>()).Returns(ci =>
            {
                Selected.Add((selector, ci.ArgAt<string>(0)));
                return Task.FromResult<IReadOnlyList<string>>(new[] { ci.ArgAt<string>(0) });
            });
            return loc;
        }
    }

    private static (Stage.Stage Stage, FakeBoundPage Page) MakeStage(FakeBoundPage? page = null, double pace = 1.0)
    {
        page ??= new FakeBoundPage();
        var pacing = new Pacing(pace: pace, sleep: _ => Task.CompletedTask);
        var marks = new Marks(t0: 0.0, clock: () => 0.0);
        return (new Stage.Stage(page.Page, pacing, marks, viewport: (1600, 900)), page);
    }

    [Fact]
    public async Task StageBeatSleepsScaledByPacing()
    {
        var slept = new List<double>();
        var pacing = new Pacing(pace: 1.0, sleep: s => { slept.Add(s); return Task.CompletedTask; });
        var marks = new Marks(t0: 0.0, clock: () => 0.0);
        var stage = new Stage.Stage(new FakeBoundPage().Page, pacing, marks, viewport: (1600, 900));
        await stage.BeatAsync(2.0);
        Assert.Equal(new[] { 2.0 }, slept); // pace=1.0
    }

    [Fact]
    public async Task StageCaptionCallsTheOverlayHook()
    {
        var (stage, page) = MakeStage();
        await stage.CaptionAsync("hello");
        Assert.Equal("hello", page.Evaluated[^1].Arg);
    }

    [Fact]
    public async Task StageMoveToXyMovesTheMouseAndTracksPosition()
    {
        var (stage, page) = MakeStage();
        await stage.MoveToXyAsync(50, 60);
        await page.Mouse.Received().MoveAsync(50f, 60f, Arg.Any<MouseMoveOptions>());
        Assert.Equal(50, stage.X);
        Assert.Equal(60, stage.Y);
    }

    [Fact]
    public async Task StagePointAtLocatesScrollsAndMovesToTheBoxCenter()
    {
        var (stage, page) = MakeStage();
        var loc = page.Locator("#thing");
        await stage.PointAtAsync("#thing");
        await loc.Received().ScrollIntoViewIfNeededAsync();
        await loc.Received().BoundingBoxAsync();
        // box: x=10,y=20,w=100,h=40 -> center (60, 40)
        await page.Mouse.Received().MoveAsync(60f, 40f, Arg.Any<MouseMoveOptions>());
    }

    [Fact]
    public async Task StagePointAtRaisesWhenLocatorHasNoBox()
    {
        var page = new FakeBoundPage();
        page.Locator("#ghost", visible: false);
        var (stage, _) = MakeStage(page);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.PointAtAsync("#ghost"));
    }

    [Fact]
    public async Task StageClickClicksThePointedAtElement()
    {
        var (stage, page) = MakeStage();
        page.Locator("#button");
        await stage.ClickAsync("#button");
        Assert.Equal(new[] { "#button" }, page.Clicked);
    }

    [Fact]
    public async Task StageClickTextClicksMatchingText()
    {
        var (stage, page) = MakeStage();
        page.TextLocator("Submit");
        await stage.ClickTextAsync("Submit");
        Assert.Equal(new[] { "Submit" }, page.Clicked);
    }

    [Fact]
    public async Task StageClickTextRaisesWhenNoBox()
    {
        var page = new FakeBoundPage();
        page.TextLocator("Ghost", visible: false);
        var (stage, _) = MakeStage(page);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ClickTextAsync("Ghost"));
    }

    [Fact]
    public async Task StageTypeIntoClicksThenTypes()
    {
        var (stage, page) = MakeStage();
        page.Locator("#field");
        await stage.TypeIntoAsync("#field", "hi");
        Assert.Equal(new[] { "#field" }, page.Clicked);
        Assert.Equal(new[] { ("#field", "hi") }, page.Typed);
    }

    [Fact]
    public async Task StageSelectChoosesTheOption()
    {
        var (stage, page) = MakeStage();
        page.Locator("#dropdown");
        await stage.SelectAsync("#dropdown", "value-a");
        Assert.Equal(new[] { ("#dropdown", "value-a") }, page.Selected);
    }

    [Fact]
    public async Task StageScrollWheelsThePage()
    {
        var (stage, page) = MakeStage();
        await stage.ScrollAsync(300);
        await page.Mouse.Received(1).WheelAsync(0f, 300f);
    }

    [Fact]
    public async Task StageReadReturnsInnerText()
    {
        var (stage, page) = MakeStage();
        page.Locator("#thing");
        Assert.Equal("some text", await stage.ReadAsync("#thing"));
    }

    [Fact]
    public async Task StageReadReturnsEmptyStringWhenItRaises()
    {
        var page = new FakeBoundPage();
        var loc = page.Locator("#missing");
        loc.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).ThrowsAsync(new TimeoutException("nope"));
        var (stage, _) = MakeStage(page);
        Assert.Equal("", await stage.ReadAsync("#missing"));
    }

    // ------------------------------------------------------------- SceneRegistry

    [Fact]
    public async Task SceneRegistryRunsScenesInRegistrationOrder()
    {
        var registry = new SceneRegistry();
        var order = new List<string>();
        registry.Scene("intro", "Intro", (stage, args) => { order.Add("intro"); return Task.CompletedTask; });
        registry.Scene("outro", "Outro", (stage, args) => { order.Add("outro"); return Task.CompletedTask; });

        var (stage, _) = MakeStage();
        await registry.RunAllAsync(stage, "http://base");
        Assert.Equal(new[] { "intro", "outro" }, order);
    }

    [Fact]
    public void SceneRegistryNamesAndOrder()
    {
        var registry = new SceneRegistry();
        registry.Scene("a", "A", (stage, args) => Task.CompletedTask);
        registry.Scene("b", "B", (stage, args) => Task.CompletedTask);
        Assert.Equal(new[] { "a", "b" }, registry.Names());
        Assert.Equal(new[] { "a", "b" }, registry.Order);
    }

    [Fact]
    public async Task SceneRegistryWrapsMarksAndCaptionsAroundTheScene()
    {
        var registry = new SceneRegistry();
        registry.Scene("only", "Only scene", (stage, args) => Task.CompletedTask);

        var (stage, page) = MakeStage();
        await registry.RunAllAsync(stage, "http://base");
        var keys = stage.Marks.Rows.Select(m => m.Key).ToList();
        Assert.Equal(new[] { "scene:only:start", "scene:only:end" }, keys);
        // caption turned on then off
        var captions = page.Evaluated.Select(e => e.Arg).ToList();
        Assert.Equal(new object?[] { "Only scene", "" }, captions);
    }

    [Fact]
    public async Task SceneRegistryRaisesOnEmptyRegistryWhenRun()
    {
        var registry = new SceneRegistry();
        var (stage, _) = MakeStage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RunAllAsync(stage, "http://base"));
    }

    // ------------------------------------------------------------------ http_ready

    /// <summary>Stands in for the server; the Python tests patch <c>urllib.request.urlopen</c>.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task HttpReadyTrueWhenUrlopenSucceeds()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(await Browser.HttpReadyAsync("http://127.0.0.1:9/", timeout: 1.0, client: client));
    }

    [Fact]
    public async Task HttpReadyFalseWhenItNeverAnswers()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("refused")));
        Assert.False(await Browser.HttpReadyAsync("http://127.0.0.1:9/", timeout: 0.01, client: client));
    }

    /// <summary>
    /// <c>urlopen</c> raises <c>HTTPError</c> for anything outside 2xx, and the Python
    /// probe treats that like a refused connection; an error page is not a ready app.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpReadyFalseWhenTheServerOnlyAnswersWithErrors(HttpStatusCode status)
    {
        var hits = 0;
        using var client = new HttpClient(new StubHandler(_ => { hits++; return new HttpResponseMessage(status); }));
        Assert.False(await Browser.HttpReadyAsync("http://127.0.0.1:9/", timeout: 0.05, poll: 0.01, client: client));
        Assert.True(hits > 1); // it kept polling instead of taking the error for an answer
    }

    [Fact]
    public async Task HttpReadyTrueOnceAnErrorPageTurnsIntoASuccess()
    {
        var hits = 0;
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(++hits < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        Assert.True(await Browser.HttpReadyAsync("http://127.0.0.1:9/", timeout: 1.0, poll: 0.01, client: client));
        Assert.Equal(3, hits);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task HttpReadyTrueForAnySuccessStatus(HttpStatusCode status)
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));
        Assert.True(await Browser.HttpReadyAsync("http://127.0.0.1:9/", timeout: 1.0, client: client));
    }

    // ------------------------------------------------------------------ AppProcess

    private sealed class FakeChild(IReadOnlyList<string> argv, string cwd) : IChildProcess
    {
        public IReadOnlyList<string> Argv { get; } = argv;
        public string Cwd { get; } = cwd;
        public List<string> Sent { get; } = new();
        public bool Waited { get; private set; }
        public bool HasExited => false;

        public bool Interrupt()
        {
            Sent.Add("interrupt");
            return true;
        }

        public bool WaitForExit(int milliseconds)
        {
            Waited = true;
            return true;
        }

        public void Kill() => Sent.Add("kill");
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "tramoya-stage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void AppProcessStartClonesAndLaunches()
    {
        var tmp = TempDir();
        var calls = new List<List<string>>();
        var app = new AppProcess("https://forge/repo.git", "main", 8765, python: "python3", entry: "app.py",
            run: argv => { calls.Add(argv.ToList()); return (0, ""); },
            popen: (argv, cwd) => new FakeChild(argv, cwd),
            mkdtemp: () => tmp);
        app.Start();

        Assert.Equal(new[] { "git", "-c" }, calls[0].Take(2));
        var proc = Assert.IsType<FakeChild>(app.Proc);
        Assert.Equal("python3", proc.Argv[0]);
        Assert.Contains(tmp, proc.Argv[1]);
    }

    [Fact]
    public void AppProcessStartRaisesWhenCloneFails()
    {
        var tmp = TempDir();
        var app = new AppProcess("https://forge/repo.git", "main", 8765,
            run: _ => (1, "fatal: not found"),
            popen: (argv, cwd) => new FakeChild(argv, cwd),
            mkdtemp: () => tmp);
        Assert.Throws<InvalidOperationException>(() => app.Start());
    }

    [Fact]
    public void AppProcessStopSendsSigintAndWaits()
    {
        var tmp = TempDir();
        var app = new AppProcess("https://forge/repo.git", "main", 8765,
            run: _ => (0, ""),
            popen: (argv, cwd) => new FakeChild(argv, cwd),
            mkdtemp: () => tmp);
        app.Start();
        var proc = (FakeChild)app.Proc!;
        app.Stop();
        Assert.True(proc.Waited);
        Assert.NotEmpty(proc.Sent);
    }

    [Fact]
    public void AppProcessContextManagerStopsOnExit()
    {
        var tmp = TempDir();
        var app = new AppProcess("https://forge/repo.git", "main", 8765,
            run: _ => (0, ""),
            popen: (argv, cwd) => new FakeChild(argv, cwd),
            mkdtemp: () => tmp);
        FakeChild proc;
        using (app)
        {
            app.Start();
            proc = (FakeChild)app.Proc!;
        }
        Assert.True(proc.Waited);
    }

    // ------------------------------------------------------------------- open_stage

    [Fact(Skip = "Playwright is a compiled dependency of Tramoya.Stage; there is no import that can fail")]
    public void OpenStageRaisesClearErrorWithoutPlaywright()
    {
    }

    /// <summary>
    /// Init scripts run before <c>document.documentElement</c> exists; a real browser once
    /// threw <c>appendChild of null</c> and the caption hook was never defined. The overlay
    /// must defer until the document is there. Like the Python test, this uses Playwright
    /// directly rather than <see cref="Browser.OpenStageAsync"/>: it is the script under
    /// test, not the session.
    /// </summary>
    [BrowserFact]
    public async Task OverlayInstallsWhenPageHasABody()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var context = await browser.NewContextAsync();
        await context.AddInitScriptAsync(Stage.Stage.OverlayJs);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, message) => errors.Add(message);
        await page.GotoAsync("data:text/html,<h1>hello</h1>");
        Assert.Equal("function", await page.EvaluateAsync<string>("typeof window.__tramoyaCaption"));
        await page.EvaluateAsync("window.__tramoyaCaption('scene one')");
        Assert.Equal("scene one", await page.EvaluateAsync<string>("document.getElementById('tramoya-caption').textContent"));
        Assert.Equal(1, await page.Locator("#tramoya-cursor").CountAsync());
        Assert.Equal(1, await page.Locator("#tramoya-caption").CountAsync());
        Assert.Empty(errors);
    }

    // ---------------------------------------------------------------- StageSession

    private static (StageSession Session, IPlaywright Playwright, IBrowser Browser, IBrowserContext Context) MakeSession()
    {
        var playwright = Substitute.For<IPlaywright>();
        var browser = Substitute.For<IBrowser>();
        var context = Substitute.For<IBrowserContext>();
        var page = Substitute.For<IPage>();
        var stage = new Stage.Stage(page, new Pacing(pace: 1.0, sleep: _ => Task.CompletedTask), new Marks(t0: 0.0, clock: () => 0.0));
        return (new StageSession(playwright, browser, context, page, stage), playwright, browser, context);
    }

    [Fact]
    public async Task DisposeClosesContextThenBrowserThenDriver()
    {
        var (session, playwright, browser, context) = MakeSession();
        var order = new List<string>();
        context.CloseAsync(Arg.Any<BrowserContextCloseOptions>()).Returns(_ => { order.Add("context"); return Task.CompletedTask; });
        browser.CloseAsync(Arg.Any<BrowserCloseOptions>()).Returns(_ => { order.Add("browser"); return Task.CompletedTask; });
        playwright.When(p => p.Dispose()).Do(_ => order.Add("driver"));

        await session.DisposeAsync();
        await session.DisposeAsync(); // idempotent, as a context manager's exit is

        Assert.Equal(new[] { "context", "browser", "driver" }, order);
    }

    /// <summary>
    /// The Python <c>with sync_playwright()</c> block stops the driver whatever the inner
    /// <c>finally</c> raised; a context that cannot close (page crashed, target gone) must
    /// not leave a Chromium and a node process behind, and the failure still surfaces.
    /// </summary>
    [Fact]
    public async Task DisposeStillClosesBrowserAndDriverWhenTheContextFailsToClose()
    {
        var (session, playwright, browser, context) = MakeSession();
        context.CloseAsync(Arg.Any<BrowserContextCloseOptions>()).ThrowsAsync(new InvalidOperationException("Target closed"));

        var exc = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());

        Assert.Equal("Target closed", exc.Message);
        await browser.Received(1).CloseAsync(Arg.Any<BrowserCloseOptions>());
        playwright.Received(1).Dispose();
    }

    [Fact]
    public async Task DisposeStillDisposesTheDriverWhenTheBrowserFailsToClose()
    {
        var (session, playwright, browser, context) = MakeSession();
        browser.CloseAsync(Arg.Any<BrowserCloseOptions>()).ThrowsAsync(new InvalidOperationException("Browser gone"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());

        await context.Received(1).CloseAsync(Arg.Any<BrowserContextCloseOptions>());
        playwright.Received(1).Dispose();
    }

    /// <summary>The launch failure path has no context or browser yet; only the driver needs disposing.</summary>
    [Fact]
    public async Task TearDownWithoutBrowserOrContextOnlyDisposesTheDriver()
    {
        var playwright = Substitute.For<IPlaywright>();
        await StageSession.TearDownAsync(playwright, browser: null, context: null);
        playwright.Received(1).Dispose();
    }

    [Fact]
    public async Task SnapshotReadsTheAriaTreeAndToleratesDoublesWithoutIt()
    {
        var page = Substitute.For<IPage>();
        var loc = Substitute.For<ILocator>();
        loc.AriaSnapshotAsync(Arg.Any<LocatorAriaSnapshotOptions>()).Returns(Task.FromResult("- heading \"Notes\""));
        page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(loc);
        var stage = new Stage.Stage(page, new Pacing(pace: 1.0, rehearse: true, lang: "en"), new Marks());
        Assert.Equal("- heading \"Notes\"", await stage.SnapshotAsync());

        var bare = Substitute.For<IPage>();
        bare.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Throws(new MissingMethodException("no locator"));
        Assert.Equal("", await new Stage.Stage(bare, new Pacing(pace: 1.0, rehearse: true, lang: "en"), new Marks()).SnapshotAsync());
    }
}
