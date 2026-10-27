// Where tramoya keeps its things on Windows: the port of the module-level
// constants of easy/app.py (_kit, KIT, CASA, DATOS, MIS_VIDEOS, AJUSTES,
// PERFIL_WEB) plus the two that only exist on this side (the WebView2 profile
// and the Chromium folder).
//
// Two layouts, like the Python app. Inside a portable kit (an `app\` or
// `navegador\` folder beside Tramoya.exe, or TRAMOYA_KIT pointing at one)
// everything stays in the kit folder, so the whole thing can live on a USB
// stick and leaves no trace. Otherwise the data goes to %LOCALAPPDATA%\Tramoya
// and the finished videos to the user's Videos folder («Mis vídeos»), which is
// where a Windows user looks for them.
//
// Everything is computed on each access instead of cached at startup: it is
// cheap, and it lets the tests switch layouts just by changing the environment.
using System.Drawing;

namespace Tramoya.App;

public static class Paths
{
    public const string KitVariable = "TRAMOYA_KIT";
    public const string BrowsersVariable = "PLAYWRIGHT_BROWSERS_PATH";

    /// <summary>The folder that holds Tramoya.exe (the test host's folder under tests).</summary>
    public static string ExeDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>The portable kit, or null when installed normally.</summary>
    public static string? Kit
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(KitVariable);
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);
            var exeDir = ExeDir;
            if (Directory.Exists(Path.Combine(exeDir, "app")) ||
                Directory.Exists(Path.Combine(exeDir, "navegador")))
                return exeDir;
            return null;
        }
    }

    /// <summary>Home of tramoya for the user: the kit, or %USERPROFILE%\Tramoya.</summary>
    public static string Casa => Kit ?? Path.Combine(UserProfile, "Tramoya");

    /// <summary>Projects, settings and browser profiles.</summary>
    public static string Datos => Kit is { } kit
        ? Path.Combine(kit, "app", "datos")
        : Path.Combine(LocalAppData, "Tramoya", "datos");

    /// <summary>Where the finished videos end up («Mis vídeos»).</summary>
    public static string MisVideos => Kit is { } kit
        ? Path.Combine(kit, "Mis vídeos")
        : Path.Combine(VideosFolder, "Tramoya");

    public static string Ajustes => Path.Combine(Datos, "ajustes.json");

    /// <summary>The persistent profile of the recorded webs (sessions), as in a normal browser.</summary>
    public static string PerfilWeb => Path.Combine(Datos, "_navegador");

    /// <summary>User data folder of the WebView2 window itself (`--user-data-dir` in Python).</summary>
    public static string Ventana => Path.Combine(Datos, "_ventana");

    /// <summary>Where Playwright keeps Chromium (PLAYWRIGHT_BROWSERS_PATH).</summary>
    public static string Navegador => Kit is { } kit
        ? Path.Combine(kit, "navegador")
        : Path.Combine(LocalAppData, "Tramoya", "navegador");

    /// <summary>ffmpeg beside the exe (`ffmpeg\ffmpeg.exe` or `ffmpeg.exe`), else the one in PATH.</summary>
    public static string Ffmpeg => Tool("ffmpeg");

    public static string Ffprobe => Tool("ffprobe");

    /// <summary>The icon lives inside the exe itself, so its path is the exe's.</summary>
    public static string? IconPath => Environment.ProcessPath;

    /// <summary>The exe's own icon, or null if it cannot be read (test hosts, odd launchers).</summary>
    public static Icon? Icono()
    {
        try
        {
            return IconPath is { } path ? Icon.ExtractAssociatedIcon(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What the Python app does to os.environ at import time: point Playwright at our
    /// Chromium and put the bundled ffmpeg first in PATH, so child processes (the
    /// `easy-engine` workers, Playwright's driver) find the same binaries the window uses.
    /// </summary>
    public static void ConfigureEnvironment()
    {
        Environment.SetEnvironmentVariable(BrowsersVariable, Navegador);
        var ffmpegDir = Path.Combine(ExeDir, "ffmpeg");
        if (Directory.Exists(ffmpegDir))
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(Path.PathSeparator).Contains(ffmpegDir, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", ffmpegDir + Path.PathSeparator + path);
        }
    }

    /// <summary>The folders `main()` creates before serving: data and «Mis vídeos».</summary>
    public static void EnsureFolders()
    {
        Directory.CreateDirectory(Datos);
        Directory.CreateDirectory(MisVideos);
    }

    private static string Tool(string name)
    {
        var exeDir = ExeDir;
        foreach (var candidate in new[]
                 {
                     Path.Combine(exeDir, "ffmpeg", name + ".exe"),
                     Path.Combine(exeDir, name + ".exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return name;
    }

    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // The known folder, not a literal %USERPROFILE%\Videos: users who redirected
    // «Vídeos» to another drive should find their videos there.
    private static string VideosFolder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return string.IsNullOrEmpty(videos) ? Path.Combine(UserProfile, "Videos") : videos;
        }
    }
}
