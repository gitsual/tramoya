using Microsoft.Playwright;
using Tramoya.Core;

namespace Tramoya.Stage;

/// <summary>
/// The stage as the director sees it (port of the duck-typed stage in
/// <c>tramoya/director.py</c>): the step verbs of <see cref="IStage"/> plus the caption
/// band and the accessibility snapshot. <see cref="Stage"/> implements it over a
/// Playwright page; the director tests use a double, like the Python ones.
/// </summary>
public interface IDirectedStage : IStage
{
    /// <summary>Show (or, with an empty string, hide) the caption band.</summary>
    Task CaptionAsync(string text);

    /// <summary>The page's accessibility tree as text; empty when the page cannot give one.</summary>
    Task<string> SnapshotAsync(string selector = "body");
}

/// <summary>
/// A hand over a browser page (port of <c>tramoya/stage.py</c>): move, point, click, type,
/// scroll and read, plus a caption band and a beat (pause) scaled by pacing.
///
/// Takes any <see cref="IPage"/>; tests pass a substitute, so nothing here needs a browser
/// to be exercised. Only <see cref="Browser.OpenStageAsync"/> launches one.
/// </summary>
public sealed class Stage : IDirectedStage
{
    /// <summary>
    /// Installed as an init script before any app script runs: a ring cursor that follows
    /// the mouse, a ripple on every click and a caption band at the bottom, all
    /// <c>pointer-events: none</c> so the overlay never steals a click from the app.
    /// </summary>
    public const string OverlayJs = """
// ids: #tramoya-cursor, #tramoya-ripple, #tramoya-caption
(() => {
  // Init scripts run before <html> exists; wait for it, then install once.
  const install = () => {
  const cursor = document.createElement("div");
  cursor.id = "tramoya-cursor";
  cursor.style.cssText = [
    "position: fixed", "z-index: 2147483647", "width: 18px", "height: 18px",
    "border: 2px solid #ff5f5f", "border-radius: 50%", "pointer-events: none",
    "transform: translate(-50%, -50%)", "left: -100px", "top: -100px",
    "transition: left 0.05s linear, top 0.05s linear",
  ].join(";");
  document.documentElement.appendChild(cursor);

  const caption = document.createElement("div");
  caption.id = "tramoya-caption";
  caption.style.cssText = [
    "position: fixed", "z-index: 2147483647", "left: 0", "right: 0", "bottom: 0",
    "pointer-events: none", "text-align: center", "padding: 18px",
    "font: 20px sans-serif", "color: #fff",
    "background: linear-gradient(transparent, rgba(0,0,0,0.6))",
    "opacity: 0", "transition: opacity 0.2s ease",
  ].join(";");
  document.documentElement.appendChild(caption);

  window.__tramoyaCaption = (text) => {
    caption.textContent = text || "";
    caption.style.opacity = text ? "1" : "0";
  };

  function ripple(x, y) {
    const ring = document.createElement("div");
    ring.id = "tramoya-ripple";
    ring.style.cssText = [
      "position: fixed", "z-index: 2147483647", "width: 10px", "height: 10px",
      "border: 2px solid #ff5f5f", "border-radius: 50%", "pointer-events: none",
      `left: ${x}px`, `top: ${y}px`, "transform: translate(-50%, -50%) scale(1)",
      "opacity: 1", "transition: transform 0.4s ease, opacity 0.4s ease",
    ].join(";");
    document.documentElement.appendChild(ring);
    requestAnimationFrame(() => {
      ring.style.transform = "translate(-50%, -50%) scale(3)";
      ring.style.opacity = "0";
    });
    setTimeout(() => ring.remove(), 400);
  }

  window.addEventListener("mousemove", (e) => {
    cursor.style.left = `${e.clientX}px`;
    cursor.style.top = `${e.clientY}px`;
  });
  window.addEventListener("mousedown", (e) => ripple(e.clientX, e.clientY));
  };
  if (document.documentElement) install();
  else document.addEventListener("DOMContentLoaded", install, { once: true });
})();
""";

    public static readonly (int Width, int Height) DefaultViewport = (1600, 900);

    public IPage Page { get; }
    public Pacing Pacing { get; }
    public Marks Marks { get; }
    public (int Width, int Height) Viewport { get; }

    /// <summary>Where the cursor was last moved to (starts at the viewport's centre).</summary>
    public double X { get; private set; }
    public double Y { get; private set; }

    public Stage(IPage page, Pacing pacing, Marks marks, (int Width, int Height)? viewport = null)
    {
        Page = page;
        Pacing = pacing;
        Marks = marks;
        Viewport = viewport ?? DefaultViewport;
        X = Viewport.Width / 2.0;
        Y = Viewport.Height / 2.0;
    }

    /// <summary>Pause for a beat, scaled by pacing (compressed when rehearsing).</summary>
    public async Task BeatAsync(double baseSeconds = 1.0) => await Pacing.SleepAsync(baseSeconds).ConfigureAwait(false);

    public Task CaptionAsync(string text) => Page.EvaluateAsync("(t) => window.__tramoyaCaption(t)", text);

    public async Task MoveToXyAsync(double x, double y, int steps = 15)
    {
        await Page.Mouse.MoveAsync((float)x, (float)y, new MouseMoveOptions { Steps = steps }).ConfigureAwait(false);
        X = x;
        Y = y;
    }

    /// <summary>Scroll an element into view and move the cursor to its centre.</summary>
    public async Task PointAtAsync(string selector)
    {
        var locator = Page.Locator(selector).First;
        await locator.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        var box = await locator.BoundingBoxAsync().ConfigureAwait(false);
        if (box is null) throw new InvalidOperationException($"element not visible: {selector}");
        await MoveToXyAsync(box.X + box.Width / 2, box.Y + box.Height / 2).ConfigureAwait(false);
    }

    public async Task ClickAsync(string selector)
    {
        await PointAtAsync(selector).ConfigureAwait(false);
        await Page.Locator(selector).First.ClickAsync().ConfigureAwait(false);
    }

    public Task ClickTextAsync(string text) => ClickTextAsync(text, exact: true);

    public async Task ClickTextAsync(string text, bool exact)
    {
        var locator = Page.GetByText(text, new PageGetByTextOptions { Exact = exact }).First;
        await locator.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        var box = await locator.BoundingBoxAsync().ConfigureAwait(false);
        if (box is null) throw new InvalidOperationException($"text not visible: {text}");
        await MoveToXyAsync(box.X + box.Width / 2, box.Y + box.Height / 2).ConfigureAwait(false);
        await locator.ClickAsync().ConfigureAwait(false);
    }

    public Task TypeIntoAsync(string selector, string text) => TypeIntoAsync(selector, text, delay: 40);

    /// <summary>Click the field, then type key by key so the take shows the text appearing.</summary>
    public async Task TypeIntoAsync(string selector, string text, int delay)
    {
        await ClickAsync(selector).ConfigureAwait(false);
        await Page.Locator(selector).First
            .PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Delay = delay })
            .ConfigureAwait(false);
    }

    public Task SelectAsync(string selector, string value) => Page.Locator(selector).First.SelectOptionAsync(value);

    /// <summary><c>fill("")</c> alone does not wake every framework; the <c>input</c> event does.</summary>
    public async Task ClearAsync(string selector)
    {
        await Page.FillAsync(selector, "").ConfigureAwait(false);
        await Page.DispatchEventAsync(selector, "input").ConfigureAwait(false);
    }

    public Task ScrollAsync(int dy) => ScrollAsync(dy, dx: 0);

    public Task ScrollAsync(int dy, int dx) => Page.Mouse.WheelAsync(dx, dy);

    public Task PressAsync(string key) => Page.Keyboard.PressAsync(key);

    public Task GotoAsync(string url) => Page.GotoAsync(url);

    /// <summary>
    /// Wait for something slow (a model thinking, a build) inside a wait window: the marks
    /// get <c>wait:start</c>/<c>wait:end</c>, and <c>assemble</c> speeds any window longer
    /// than 20 s up to a few seconds with a caption. Not finding the element is not an
    /// error here: the next step will fail with a clearer message if it mattered.
    /// </summary>
    public async Task WaitForAsync(string selector, double seconds)
    {
        using (Marks.Wait(selector))
        {
            try
            {
                await Page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = (float)(seconds * 1000),
                }).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // the window closes anyway: the marks must stay balanced
            }
        }
    }

    /// <summary>
    /// The page's accessibility tree as text (roles and names, no pixels): what
    /// <c>tramoya snapshots diff</c> compares between two runs. A page that cannot give
    /// one (a double, an old browser) yields an empty snapshot rather than an error.
    /// </summary>
    public async Task<string> SnapshotAsync(string selector = "body")
    {
        try
        {
            return await Page.Locator(selector).AriaSnapshotAsync().ConfigureAwait(false) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Read an element's text, or an empty string if it never appears.</summary>
    public async Task<string> ReadAsync(string selector, int timeout = 5000)
    {
        var locator = Page.Locator(selector).First;
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeout })
                .ConfigureAwait(false);
            return await locator.InnerTextAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Any failure here is expected (the element never showed up), not a bug.
            return "";
        }
    }
}

/// <summary>
/// An ordered collection of scenes, each wrapped so its start/end marks and its caption
/// on/off are never forgotten by the scene itself. The Python version registers scenes
/// with a decorator; here <see cref="Scene"/> takes the callback and hands it back.
/// </summary>
public sealed class SceneRegistry
{
    private readonly Dictionary<string, (string Label, Func<Stage, object?[], Task> Fn)> _scenes = new();

    public List<string> Order { get; } = new();

    public Func<Stage, object?[], Task> Scene(string name, string label, Func<Stage, object?[], Task> fn)
    {
        Order.Add(name);
        _scenes[name] = (label, fn);
        return fn;
    }

    public List<string> Names() => new(Order);

    public async Task RunAllAsync(Stage stage, params object?[] args)
    {
        if (Order.Count == 0) throw new InvalidOperationException("no scenes registered");
        foreach (var name in Order)
        {
            var (label, fn) = _scenes[name];
            using (stage.Marks.Scene(name, label))
            {
                await stage.CaptionAsync(label).ConfigureAwait(false);
                try
                {
                    await fn(stage, args).ConfigureAwait(false);
                }
                finally
                {
                    await stage.CaptionAsync("").ConfigureAwait(false);
                }
            }
        }
    }
}
