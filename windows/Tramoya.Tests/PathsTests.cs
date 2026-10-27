// Tests of App/Paths.cs: the two layouts of easy/app.py (portable kit vs
// installed) chosen through TRAMOYA_KIT. The variable is restored after each
// test so the suite stays hermetic whatever the developer's machine has set.
using Tramoya.App;

namespace Tramoya.Tests;

public class PathsTests
{
    /// <summary>Sets an environment variable for the duration of a test and puts it back afterwards.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _before;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _before = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _before);
    }

    private static string TempKit()
    {
        var kit = Path.Combine(Path.GetTempPath(), "tramoya-kit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(kit, "app"));
        return kit;
    }

    [Fact]
    public void KitLayoutKeepsEverythingInsideTheKit()
    {
        var kit = TempKit();
        try
        {
            using var _ = new EnvScope(Paths.KitVariable, kit);
            Assert.Equal(kit, Paths.Kit);
            Assert.Equal(kit, Paths.Casa);
            Assert.Equal(Path.Combine(kit, "app", "datos"), Paths.Datos);
            Assert.Equal(Path.Combine(kit, "Mis vídeos"), Paths.MisVideos);
            Assert.Equal(Path.Combine(kit, "app", "datos", "ajustes.json"), Paths.Ajustes);
            Assert.Equal(Path.Combine(kit, "app", "datos", "_navegador"), Paths.PerfilWeb);
            Assert.Equal(Path.Combine(kit, "app", "datos", "_ventana"), Paths.Ventana);
            Assert.Equal(Path.Combine(kit, "navegador"), Paths.Navegador);
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    [Fact]
    public void InstalledLayoutUsesLocalAppDataAndVideos()
    {
        using var _ = new EnvScope(Paths.KitVariable, null);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
            videos = Path.Combine(perfil, "Videos");

        Assert.Null(Paths.Kit);
        Assert.Equal(Path.Combine(perfil, "Tramoya"), Paths.Casa);
        Assert.Equal(Path.Combine(local, "Tramoya", "datos"), Paths.Datos);
        Assert.Equal(Path.Combine(videos, "Tramoya"), Paths.MisVideos);
        Assert.Equal(Path.Combine(local, "Tramoya", "datos", "ajustes.json"), Paths.Ajustes);
        Assert.Equal(Path.Combine(local, "Tramoya", "datos", "_navegador"), Paths.PerfilWeb);
        Assert.Equal(Path.Combine(local, "Tramoya", "datos", "_ventana"), Paths.Ventana);
        Assert.Equal(Path.Combine(local, "Tramoya", "navegador"), Paths.Navegador);
    }

    [Fact]
    public void KitVariableIsNormalisedToAFullPath()
    {
        var kit = TempKit();
        try
        {
            // A relative or trailing-slash value must not leak into the paths.
            using var _ = new EnvScope(Paths.KitVariable, kit + Path.DirectorySeparatorChar);
            Assert.Equal(Path.GetFullPath(kit + Path.DirectorySeparatorChar), Paths.Kit);
            Assert.EndsWith(Path.Combine("app", "datos"), Paths.Datos);
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    [Fact]
    public void FfmpegFallsBackToTheOneInPath()
    {
        // The test host has no ffmpeg beside it, so the bare names must come back
        // and Process.Start resolves them through PATH.
        Assert.Equal("ffmpeg", Paths.Ffmpeg);
        Assert.Equal("ffprobe", Paths.Ffprobe);
    }

    [Fact]
    public void ConfigureEnvironmentPointsPlaywrightAtNavegador()
    {
        var kit = TempKit();
        try
        {
            using var _ = new EnvScope(Paths.KitVariable, kit);
            using var __ = new EnvScope(Paths.BrowsersVariable, null);
            Paths.ConfigureEnvironment();
            Assert.Equal(Path.Combine(kit, "navegador"),
                Environment.GetEnvironmentVariable(Paths.BrowsersVariable));
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    [Fact]
    public void EnsureFoldersCreatesDatosAndMisVideos()
    {
        var kit = TempKit();
        try
        {
            using var _ = new EnvScope(Paths.KitVariable, kit);
            Paths.EnsureFolders();
            Assert.True(Directory.Exists(Path.Combine(kit, "app", "datos")));
            Assert.True(Directory.Exists(Path.Combine(kit, "Mis vídeos")));
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    // --- Browsers: what Playwright needs under navegador\ ------------------------------

    [Fact]
    public void BrowserFoldersComeFromThePackagesBrowsersJson()
    {
        // The package copies its browsers.json next to the test host; the folder names
        // follow the driver's convention (dashes to underscores, then the revision).
        Assert.True(File.Exists(Browsers.BrowsersJson), Browsers.BrowsersJson);
        var folders = Browsers.Folders();
        Assert.Equal(4, folders.Count);
        Assert.Matches(@"^chromium-\d+$", folders[0]);
        Assert.Matches(@"^chromium_headless_shell-\d+$", folders[1]);
        Assert.Matches(@"^winldd-\d+$", folders[2]);
        Assert.Matches(@"^ffmpeg-\d+$", folders[3]); // the driver's own encoder for the recorded video
        Assert.Equal(folders[0].Split('-')[1], folders[1].Split('-')[1]); // same Chromium build
    }

    [Fact]
    public void DriverLivesNextToTheBrowsersJson()
    {
        // The installer runs the driver as a child process: node and cli.js must be where
        // the package put them, in the same `.playwright` folder as browsers.json.
        Assert.Equal(Path.Combine(Browsers.DriverDir, "package", "browsers.json"), Browsers.BrowsersJson);
        Assert.True(File.Exists(Path.Combine(Browsers.DriverDir, "node", "win32_x64", "node.exe")));
        Assert.True(File.Exists(Path.Combine(Browsers.DriverDir, "package", "cli.js")));
    }

    /// <summary>A complete install under <paramref name="navegador"/>: every folder with its marker and its executable (empty files are enough).</summary>
    private static void FakeInstall(string navegador)
    {
        foreach (var (folder, executables) in Browsers.Layout())
        {
            var exe = Path.Combine(navegador, folder, executables[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(Path.Combine(navegador, folder, Browsers.Marker), "");
            File.WriteAllText(exe, "");
        }
    }

    [Fact]
    public void InstalledNeedsEveryMarkerAndExecutable()
    {
        var kit = TempKit();
        try
        {
            using var _ = new EnvScope(Paths.KitVariable, kit);
            Assert.False(Browsers.Installed()); // no navegador\ at all
            var navegador = Path.Combine(kit, "navegador");
            var layout = Browsers.Layout();
            foreach (var (folder, _) in layout)
                Directory.CreateDirectory(Path.Combine(navegador, folder));
            Assert.False(Browsers.Installed()); // folders without the marker: a download that died
            foreach (var (folder, _) in layout)
                File.WriteAllText(Path.Combine(navegador, folder, Browsers.Marker), "");
            Assert.False(Browsers.Installed()); // markers without executables: a folder an antivirus emptied
            FakeInstall(navegador);
            Assert.True(Browsers.Installed());
            // Any of the known layouts of the browser folder will do, but not none of them.
            var (chromium, chromiumExes) = layout[0];
            File.Delete(Path.Combine(navegador, chromium, chromiumExes[0]));
            Assert.False(Browsers.Installed());
            var other = Path.Combine(navegador, chromium, chromiumExes[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(other, "");
            Assert.True(Browsers.Installed());
            // Playwright's ffmpeg is what encodes the recording: without it the install is not usable.
            var (ffmpeg, ffmpegExes) = layout[3];
            File.Delete(Path.Combine(navegador, ffmpeg, ffmpegExes[0]));
            Assert.False(Browsers.Installed());
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsyncDoesNothingWhenInstalled()
    {
        var kit = TempKit();
        try
        {
            using var _ = new EnvScope(Paths.KitVariable, kit);
            FakeInstall(Path.Combine(kit, "navegador"));
            var lines = new List<string>();
            var progress = new SyncProgress(lines.Add);
            Assert.Null(await Browsers.EnsureAsync(progress, CancellationToken.None));
            Assert.Empty(lines);
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    /// <summary>An IProgress that reports inline (Progress&lt;T&gt; posts to a context the tests do not have).</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _report;
        public SyncProgress(Action<string> report) => _report = report;
        public void Report(string value) => _report(value);
    }
}
