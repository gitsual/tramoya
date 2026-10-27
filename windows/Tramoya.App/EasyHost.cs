// The «tramoya easy» server as this exe runs it: the data folders come from Paths, the
// worker is this same exe with the hidden `easy-engine` subcommand (a browser that dies
// in a child process never takes the window down), and the children inherit where the
// Chromium and ffmpeg of the kit live.
using Tramoya.Easy;

namespace Tramoya.App;

internal static class EasyHost
{
    private static Server? _server;

    public static ServerOptions Options()
    {
        var exe = Environment.ProcessPath ?? "Tramoya.exe";
        var entorno = new Dictionary<string, string>
        {
            ["PLAYWRIGHT_BROWSERS_PATH"] = Paths.Navegador,
            ["PYTHONUTF8"] = "1",
        };
        var ffmpegDir = Path.GetDirectoryName(Paths.Ffmpeg);
        if (!string.IsNullOrEmpty(ffmpegDir) && Path.IsPathRooted(ffmpegDir))
            entorno["PATH"] = ffmpegDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        return new ServerOptions
        {
            Datos = Paths.Datos,
            MisVideos = Paths.MisVideos,
            Casa = Paths.Casa,
            Motor = new[] { exe, "easy-engine" },
            Entorno = entorno,
            ExePath = exe,
            Windows = OperatingSystem.IsWindows(),
            Kit = Paths.Kit is not null,
        };
    }

    /// <summary>Start the local server and return its URL (what the window opens).</summary>
    public static string Start()
    {
        _server = Server.Start(Options());
        return _server.Url;
    }

    public static void Stop()
    {
        _server?.Stop();
        _server = null;
    }
}
