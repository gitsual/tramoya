// Entry point of Tramoya.exe. Without arguments it is the app: start the local
// server of «tramoya easy» and open the window over it (what `python -m
// tramoya.easy` does). With arguments it is the CLI. A WinExe has no console,
// so the CLI first attaches to the one that launched it; that way the same exe
// serves both the double-click and `Tramoya.exe grabar ...` from a terminal.
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tramoya.App;

internal static class Program
{
    // TODO(integration): Tramoya.Easy.Server.Start() -> URL of the HttpListener on 127.0.0.1
    private static readonly Func<string> StartServer = () => "about:blank";

    // TODO(integration): Tramoya.Easy.Server.Stop() (cancel the tasks, close the listener)
    private static readonly Action StopServer = () => { };

    [STAThread]
    private static int Main(string[] args)
    {
        Paths.ConfigureEnvironment();
        return args.Length > 0 ? RunCli(args) : RunWindow();
    }

    // --- app window ----------------------------------------------------------------

    private static int RunWindow()
    {
        // One window per session: a second double-click just raises the first one.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Tramoya", out var primera);
        if (!primera)
        {
            ActivarVentanaExistente();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Paths.EnsureFolders();

        string url;
        try
        {
            url = StartServer();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No he podido arrancar Tramoya.\n\n" + ex.Message, "Tramoya",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        try
        {
            Application.Run(new MainWindow(url));
        }
        finally
        {
            // Closing the window stops everything, like `servidor.shutdown()` in Python.
            StopServer();
        }
        return 0;
    }

    private static void ActivarVentanaExistente()
    {
        var ventana = FindWindow(null, "Tramoya");
        if (ventana == IntPtr.Zero)
            return;
        ShowWindow(ventana, SW_RESTORE);
        SetForegroundWindow(ventana);
    }

    // --- CLI -----------------------------------------------------------------------

    private static int RunCli(string[] args)
    {
        // Handles inherited from the parent (pipes, files) survive AttachConsole;
        // keep them so `Tramoya.exe ... > out.txt` and the `easy-engine` workers,
        // which the server talks to through pipes, keep working.
        var salida = StreamHeredado(STD_OUTPUT_HANDLE);
        var errores = StreamHeredado(STD_ERROR_HANDLE);

        if (!AttachConsole(ATTACH_PARENT_PROCESS) && (salida is null || errores is null)
            && !string.Equals(args[0], "easy-engine", StringComparison.Ordinal))
        {
            // Launched without a console (Run dialog, a shortcut): give it one so
            // the user sees the output. The engine workers never need it.
            AllocConsole();
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // No console at all: the writers below are UTF-8 anyway.
        }
        Console.SetOut(new StreamWriter(salida ?? Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(errores ?? Console.OpenStandardError()) { AutoFlush = true });

        return Cli.Run(args);
    }

    /// <summary>A stream over the inherited std handle when it is a pipe or a file; null when it is a console or nothing.</summary>
    private static Stream? StreamHeredado(int which)
    {
        var handle = GetStdHandle(which);
        if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
            return null;
        var tipo = GetFileType(handle);
        if (tipo != FILE_TYPE_PIPE && tipo != FILE_TYPE_DISK)
            return null;
        try
        {
            return new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- Win32 ---------------------------------------------------------------------

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint FILE_TYPE_DISK = 0x0001;
    private const uint FILE_TYPE_PIPE = 0x0003;
    private const int SW_RESTORE = 9;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
