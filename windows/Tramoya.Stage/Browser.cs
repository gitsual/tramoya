using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Playwright;
using Tramoya.Core;

namespace Tramoya.Stage;

/// <summary>
/// What the stage needs around the browser (port of the second half of
/// <c>tramoya/stage.py</c>): a readiness probe for the app under test, the app process
/// itself, and <see cref="OpenStageAsync"/>, the only place that launches Chromium.
/// </summary>
public static class Browser
{
    private static readonly HttpClient SharedClient = new();

    /// <summary>
    /// Poll <paramref name="url"/> until it answers or <paramref name="timeout"/> seconds
    /// pass. Any HTTP answer counts, even an error page: it proves the server is up, which
    /// is all a launch needs to know. Exceptions (refused, reset, timed out) mean "not yet".
    /// </summary>
    public static async Task<bool> HttpReadyAsync(string url, double timeout = 20.0, double poll = 0.25, HttpClient? client = null)
    {
        client ??= SharedClient;
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or SocketException)
            {
                var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
                if (remaining <= 0) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(poll, remaining))).ConfigureAwait(false);
            }
        }
        return false;
    }

    /// <summary>
    /// Open a browser, install the overlay before any app script runs, and return a
    /// session whose <see cref="StageSession.Stage"/> is bound to the resulting page.
    /// <paramref name="storageState"/> is a Playwright session file (cookies and local
    /// storage) saved by <c>tramoya login</c>, so a demo behind a sign-in never sees a
    /// password. The two tweaks let a caller add launch arguments or context options
    /// (the easy engine needs <c>--auth-server-allowlist</c>, ignored HTTPS errors and a
    /// locale) without this module knowing about them.
    /// </summary>
    public static async Task<StageSession> OpenStageAsync(
        string url,
        Pacing pacing,
        Marks marks,
        string? videoDir = null,
        (int Width, int Height)? viewport = null,
        bool headless = true,
        string? storageState = null,
        Action<BrowserTypeLaunchOptions>? launchTweak = null,
        Action<BrowserNewContextOptions>? contextTweak = null)
    {
        var size = viewport ?? Stage.DefaultViewport;
        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowser? browser = null;
        IBrowserContext? context = null;
        try
        {
            var launch = new BrowserTypeLaunchOptions { Headless = headless };
            launchTweak?.Invoke(launch);
            browser = await playwright.Chromium.LaunchAsync(launch).ConfigureAwait(false);

            var options = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = size.Width, Height = size.Height },
            };
            if (videoDir is not null)
            {
                options.RecordVideoDir = videoDir;
                options.RecordVideoSize = new RecordVideoSize { Width = size.Width, Height = size.Height };
            }
            if (storageState is not null) options.StorageStatePath = storageState;
            contextTweak?.Invoke(options);

            context = await browser.NewContextAsync(options).ConfigureAwait(false);
            // Before the first page: an init script only reaches pages created after it.
            await context.AddInitScriptAsync(Stage.OverlayJs).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync(url).ConfigureAwait(false);
            return new StageSession(playwright, browser, context, page, new Stage(page, pacing, marks, size));
        }
        catch
        {
            if (context is not null) await context.CloseAsync().ConfigureAwait(false);
            if (browser is not null) await browser.CloseAsync().ConfigureAwait(false);
            playwright.Dispose();
            throw;
        }
    }
}

/// <summary>What the director needs from an open stage: the hand, the take, and a way to close it.</summary>
public interface IStageSession : IAsyncDisposable
{
    IDirectedStage Stage { get; }

    /// <summary>The path of the video Playwright is recording, or null when not recording.</summary>
    Task<string?> VideoPathAsync();
}

/// <summary>
/// An open browser with a <see cref="Stage"/> on its page. Disposing closes the context
/// first (that is what finishes the video file) and then the browser, as the Python
/// <c>open_stage</c> context manager does on exit.
/// </summary>
public sealed class StageSession : IStageSession
{
    private readonly IPlaywright _playwright;
    private bool _closed;

    internal StageSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page, Stage stage)
    {
        _playwright = playwright;
        Browser = browser;
        Context = context;
        Page = page;
        Stage = stage;
    }

    public Stage Stage { get; }
    public IPage Page { get; }
    public IBrowserContext Context { get; }
    public IBrowser Browser { get; }

    IDirectedStage IStageSession.Stage => Stage;

    public async Task<string?> VideoPathAsync()
    {
        var video = Page.Video;
        return video is null ? null : await video.PathAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        await Context.CloseAsync().ConfigureAwait(false);
        await Browser.CloseAsync().ConfigureAwait(false);
        _playwright.Dispose();
    }
}

/// <summary>
/// What <see cref="AppProcess"/> and the director need from a launched child; a real
/// <see cref="Process"/> behind <see cref="ProcessChild"/>, a double in tests.
/// </summary>
public interface IChildProcess
{
    bool HasExited { get; }

    /// <summary>
    /// The polite request to quit. Python sends SIGINT/SIGTERM; a Windows process has no
    /// such signal, so the nearest thing is closing its main window, which only reaches
    /// apps that have one. Returns whether the request was delivered.
    /// </summary>
    bool Interrupt();

    bool WaitForExit(int milliseconds);

    /// <summary>The whole tree: a launcher's children would otherwise keep the port.</summary>
    void Kill();
}

public sealed class ProcessChild(Process process) : IChildProcess
{
    public Process Process => process;

    public bool HasExited
    {
        get
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public bool Interrupt()
    {
        try { return process.CloseMainWindow(); }
        catch (InvalidOperationException) { return false; }
    }

    public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);

    public void Kill()
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
    }
}

/// <summary>Starting and stopping child processes the way the Python code does.</summary>
public static class ChildProcess
{
    /// <summary>Run to completion and return the exit code with stderr (<c>subprocess.run(capture_output=True)</c>).</summary>
    public static (int ExitCode, string Stderr) Run(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {argv[0]}");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        stdout.GetAwaiter().GetResult();
        return (process.ExitCode, stderr.GetAwaiter().GetResult());
    }

    /// <summary>Start and return (<c>subprocess.Popen(argv, cwd=cwd)</c>).</summary>
    public static IChildProcess Start(IReadOnlyList<string> argv, string cwd)
    {
        var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false, WorkingDirectory = cwd };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {argv[0]}");
        return new ProcessChild(process);
    }

    /// <summary>
    /// Ask the child to quit and wait; a child that ignores the request (or cannot
    /// receive it) is killed, not reported, as in the Python <c>stop</c>/<c>_stop</c>.
    /// </summary>
    public static void Stop(IChildProcess proc, double timeout = 10.0)
    {
        var ms = (int)(timeout * 1000);
        bool exited;
        try
        {
            exited = proc.Interrupt() && proc.WaitForExit(ms);
        }
        catch (Exception)
        {
            exited = false;
        }
        if (!exited)
        {
            proc.Kill();
            proc.WaitForExit(ms);
        }
    }
}

/// <summary>
/// The app under test: cloned into a scratch directory and launched as a child process,
/// torn down politely with a fallback kill. The Python tests patch <c>subprocess.run</c>,
/// <c>subprocess.Popen</c> and <c>tempfile.mkdtemp</c>; here those are the three
/// optional delegates of the constructor.
/// </summary>
public sealed class AppProcess : IDisposable
{
    public delegate (int ExitCode, string Stderr) RunCommand(IReadOnlyList<string> argv);
    public delegate IChildProcess StartCommand(IReadOnlyList<string> argv, string cwd);

    private readonly string? _workdir;
    private readonly RunCommand _run;
    private readonly StartCommand _popen;
    private readonly Func<string> _mkdtemp;

    public string CloneUrl { get; }
    public string Branch { get; }
    public int Port { get; }
    public string Python { get; }
    public string Entry { get; }
    public bool GitSslVerify { get; }

    /// <summary>Where the clone landed, once <see cref="Start"/> has run.</summary>
    public string? Workdir { get; private set; }
    public IChildProcess? Proc { get; private set; }

    public AppProcess(
        string cloneUrl,
        string branch,
        int port,
        string? workdir = null,
        string python = "python",
        string entry = "app.py",
        bool gitSslVerify = true,
        RunCommand? run = null,
        StartCommand? popen = null,
        Func<string>? mkdtemp = null)
    {
        CloneUrl = cloneUrl;
        Branch = branch;
        Port = port;
        Python = python;
        Entry = entry;
        GitSslVerify = gitSslVerify;
        _workdir = workdir;
        _run = run ?? ChildProcess.Run;
        _popen = popen ?? ChildProcess.Start;
        _mkdtemp = mkdtemp ?? (() => Directory.CreateTempSubdirectory("tramoya-app-").FullName);
    }

    private string Clone()
    {
        var workdir = _workdir ?? _mkdtemp();
        var argv = new List<string>
        {
            "git", "-c", $"http.sslVerify={(GitSslVerify ? "true" : "false")}",
            "clone", "--branch", Branch, "--depth", "1", CloneUrl, workdir,
        };
        var (exitCode, stderr) = _run(argv);
        if (exitCode != 0) throw new InvalidOperationException($"failed to clone {CloneUrl}: {stderr}");
        return workdir;
    }

    public AppProcess Start()
    {
        Workdir = Clone();
        var entryPath = Path.Combine(Workdir, Entry);
        var argv = new List<string> { Python, entryPath, "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        Proc = _popen(argv, Workdir);
        return this;
    }

    public void Stop(double timeout = 10.0)
    {
        if (Proc is null) return;
        var proc = Proc;
        Proc = null;
        ChildProcess.Stop(proc, timeout);
    }

    /// <summary>The Python <c>with app:</c> block stops the app on exit; so does <c>using</c>.</summary>
    public void Dispose() => Stop();
}
