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
}
