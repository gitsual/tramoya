// Chromium provisioning. The Python kit ships Playwright's Chromium inside
// `navegador\`; the C# contract adds the installed layout, where the browser
// lives in %LOCALAPPDATA%\Tramoya\navegador and has to be downloaded the first
// time (ARQUITECTURA.md, «Ejecutable y distribución»).
//
// What Playwright needs on Windows to launch Chromium is three folders under
// PLAYWRIGHT_BROWSERS_PATH, each closed with an INSTALLATION_COMPLETE marker:
// the browser, its headless shell and winldd (the dependency checker). Their
// names carry the build numbers of the exact Playwright version compiled in,
// so they are read from the `browsers.json` the package copies next to the exe
// instead of being hardcoded: a package bump must never leave this class
// looking for the wrong folder.
//
// The download is done by the driver itself (`Program.Main(["install",
// "chromium"])`, the same thing publish.ps1 runs through playwright.ps1). It is
// known to hang after a finished download now and then, so a watchdog watches
// the files it writes (the zip in the temp folder, then the extracted tree) and
// kills the driver when nothing has moved for three minutes, retrying once.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Tramoya.Core;

namespace Tramoya.App;

internal static class Browsers
{
    /// <summary>Marker Playwright writes when a browser folder is complete.</summary>
    public const string Marker = "INSTALLATION_COMPLETE";

    /// <summary>The entries of browsers.json Chromium needs on Windows, in Playwright's naming.</summary>
    internal static readonly string[] Needed = { "chromium", "chromium-headless-shell", "winldd" };

    /// <summary>How long the driver may stay silent on disk before it is taken for hung.</summary>
    internal static TimeSpan Stall { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The `browsers.json` of the Microsoft.Playwright package compiled in, next to the exe.</summary>
    public static string BrowsersJson
    {
        get
        {
            // PLAYWRIGHT_DRIVER_SEARCH_PATH is how playwright.ps1 points the package at its
            // driver; honouring it keeps both ways of running the driver in step.
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH"),
                AppContext.BaseDirectory,
                Paths.ExeDir,
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                var candidate = Path.Combine(root, ".playwright", "package", "browsers.json");
                if (File.Exists(candidate))
                    return candidate;
            }
            return Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
        }
    }

    /// <summary>
    /// The folder names this build's Playwright expects under <see cref="Paths.Navegador"/>:
    /// <c>chromium-1248</c>, <c>chromium_headless_shell-1248</c>, <c>winldd-1007</c>
    /// (the driver swaps the dashes of the name for underscores and appends the revision).
    /// </summary>
    public static IReadOnlyList<string> Folders()
    {
        var json = Json.LoadNode(BrowsersJson) as JsonObject
                   ?? throw new InvalidOperationException($"No puedo leer {BrowsersJson}");
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in json["browsers"]?.AsArray() ?? new JsonArray())
        {
            if (entry is JsonObject o && o["name"]?.GetValue<string>() is { } name
                                      && o["revision"]?.GetValue<string>() is { } revision)
                revisions[name] = revision;
        }
        var folders = new List<string>();
        foreach (var name in Needed)
        {
            if (!revisions.TryGetValue(name, out var revision))
                throw new InvalidOperationException($"{BrowsersJson} no trae la revisión de {name}");
            folders.Add(name.Replace('-', '_') + "-" + revision);
        }
        return folders;
    }

    /// <summary>True when every folder Chromium needs is complete under <see cref="Paths.Navegador"/>.</summary>
    public static bool Installed()
    {
        try
        {
            var root = Paths.Navegador;
            return Folders().All(folder => File.Exists(Path.Combine(root, folder, Marker)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            // No browsers.json, unreadable folder: Playwright could not launch either.
            return false;
        }
    }

    /// <summary>
    /// Make sure Chromium is installed, downloading it when it is not. Returns null when it is
    /// there, else the message to show the user (Spanish). Progress lines go to
    /// <paramref name="progress"/>; cancelling kills the download and throws.
    /// </summary>
    public static async Task<string?> EnsureAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        if (Installed())
            return null;
        try
        {
            Directory.CreateDirectory(Paths.Navegador);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"No puedo crear la carpeta del navegador {Paths.Navegador}: {e.Message}";
        }

        string? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            progress?.Report(attempt == 1
                ? $"Descargando Chromium en {Paths.Navegador}…"
                : "La descarga se quedó parada; lo intento otra vez…");
            var (code, hung, error) = await InstallOnceAsync(progress, cancel).ConfigureAwait(false);
            if (Installed())
                return null;
            last = hung
                ? "La descarga se quedó parada dos veces"
                : error ?? (code == 0
                    ? $"Playwright terminó sin dejar en {Paths.Navegador} las carpetas {string.Join(", ", Folders())}"
                    : $"Playwright terminó con el código {code}");
            if (!hung)
                break;
        }
        return "No he podido descargar el navegador Chromium: " + last + ".";
    }

    /// <summary>One run of the driver under the watchdog: exit code, whether it was killed for hanging, and a message when it could not even start.</summary>
    private static async Task<(int Code, bool Hung, string? Error)> InstallOnceAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? error = null;
        // Program.Main blocks until the driver exits; a dedicated thread keeps the caller's
        // thread (the dialog's message loop under Task.Run, or the CLI) free of it.
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
            }
            catch (Exception e)
            {
                error = $"no he podido arrancar el instalador de Playwright ({e.Message})";
                done.SetResult(-1);
            }
        }) { IsBackground = true, Name = "playwright-install" };
        thread.Start();

        var hung = false;
        var lastChange = DateTime.UtcNow;
        var lastSeen = Snapshot();
        while (true)
        {
            var finished = await Task.WhenAny(done.Task, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
            if (finished == done.Task)
                break;
            if (cancel.IsCancellationRequested)
            {
                KillDriver();
                await done.Task.ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            var seen = Snapshot();
            if (seen != lastSeen)
            {
                lastSeen = seen;
                lastChange = DateTime.UtcNow;
                progress?.Report(Describe(seen));
            }
            else if (DateTime.UtcNow - lastChange > Stall)
            {
                // Nothing written for too long: the driver is stuck (it happens after the
                // download finishes). Kill it; the caller retries once.
                hung = true;
                KillDriver();
                await done.Task.ConfigureAwait(false);
                break;
            }
        }
        return (await done.Task.ConfigureAwait(false), hung, error);
    }

    /// <summary>What the driver has written so far: the zip growing in the temp folder and the files extracted under Navegador.</summary>
    private readonly record struct Activity(long DownloadBytes, int Files, long Bytes);

    private static Activity Snapshot()
    {
        long download = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "playwright-download-*"))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                    download += SafeLength(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var files = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Paths.Navegador, "*", SearchOption.AllDirectories))
            {
                files++;
                bytes += SafeLength(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new Activity(download, files, bytes);
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string Describe(Activity a)
    {
        if (a.DownloadBytes > 0)
            return $"Descargando Chromium… {a.DownloadBytes / (1024 * 1024)} MB";
        return $"Instalando… {a.Files} ficheros ({a.Bytes / (1024 * 1024)} MB)";
    }

    /// <summary>Kill the driver (the node.exe children of this process) and everything they started.</summary>
    internal static void KillDriver()
    {
        foreach (var pid in ChildProcesses("node.exe"))
        {
            try
            {
                using var child = Process.GetProcessById(pid);
                child.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to kill: nothing else to do.
            }
        }
    }

    /// <summary>Process ids of the direct children of this process with that image name.</summary>
    private static List<int> ChildProcesses(string imageName)
    {
        var mine = Environment.ProcessId;
        var found = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return found;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
                return found;
            do
            {
                if (entry.th32ParentProcessID == mine
                    && string.Equals(entry.szExeFile, imageName, StringComparison.OrdinalIgnoreCase))
                    found.Add((int)entry.th32ProcessID);
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return found;
    }

    // --- Win32 (Toolhelp) --------------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
