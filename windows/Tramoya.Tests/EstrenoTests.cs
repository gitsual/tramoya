using System.Text.Json.Nodes;
using Tramoya.Core;
using Tramoya.Easy;
using Tramoya.Stage;

namespace Tramoya.Tests;

// tests/test_estreno.py: the plan fits the take in a length, the page data carries every word it shows,
// and the easy settings choose the finish, the format and the length.
public class EstrenoTests
{
    private static readonly (string, double)[] Escenas = [("Open the list", 8.0), ("Write a note", 12.0), ("Mark it done", 10.0)];

    private static JsonObject Obj(string s) => Json.Parse(s)!.AsObject();

    [Fact]
    public void WithoutALengthTheTakePlaysAsItIs()
    {
        var p = Estreno.PlanDe(Escenas, 30.0);
        Assert.Equal(1.0, p.Vel);
        Assert.True(p.Cabe);
        Assert.Equal([0, 8000, 20000], p.Escenas.Select(e => e.Ini));
        Assert.Equal(Estreno.IntroMs + 30_000 + Estreno.OutroMs, p.TotalMs);
    }

    [Fact]
    public void ALengthSpeedsTheTakeUpToFit()
    {
        var p = Estreno.PlanDe(Escenas, 30.0, duracionMax: 30);
        Assert.True(p.Cabe);
        Assert.InRange(p.Vel, 1.0001, Estreno.VelMax);
        Assert.Equal((Estreno.IntroCortaMs, Estreno.OutroCortaMs), (p.IntroMs, p.OutroMs));
        Assert.InRange(p.TotalMs, 29_999, 30_001);
    }

    [Fact]
    public void TooShortSaysSoAndStaysAtTheTopSpeed()
    {
        var p = Estreno.PlanDe(Escenas, 30.0, duracionMax: 15);
        Assert.False(p.Cabe);
        Assert.Equal(Estreno.VelMax, p.Vel);
        Assert.True(p.TotalMs > 15_000);
        Assert.Equal(1.0, Estreno.PlanDe(Escenas, 30.0, duracionMax: 120).Vel);
    }

    [Fact]
    public void ThePageDataCarriesTheWordsAndTheCanvas()
    {
        var p = Estreno.PlanDe(Escenas, 30.0);
        var d = Estreno.Datos(p, "Notes", "notes.local", "vertical", 900, (768, 960), "es", new DateTime(2026, 11, 12));
        Assert.Equal(1080, (int)d["ancho"]!);
        Assert.Equal(1920, (int)d["alto"]!);
        Assert.Equal("12 de noviembre de 2026", (string)d["fecha"]!);
        Assert.Equal("12 NOV 2026", (string)d["fecha_corta"]!);
        Assert.Equal("Tramoya presenta", (string)d["t"]!["presenta"]!);
        var en = Estreno.Datos(p, "Notes", "", "horizontal", 900, (1280, 800), "en", new DateTime(2026, 11, 12));
        Assert.Equal("November 12, 2026", (string)en["fecha"]!);
        Assert.Equal("The end", (string)en["t"]!["fin"]!);
    }

    [Fact]
    public void TheSubtitlesFollowTheFilmClock()
    {
        var texto = Pasos.Srt(Estreno.Subtitulos(Estreno.PlanDe(Escenas, 30.0)));
        Assert.Contains("00:00:03,200 --> 00:00:11,200\nOpen the list", texto.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ThePlayerShipsWithItsTypefaces()
    {
        var dir = Directory.CreateTempSubdirectory("tramoya-sala-").FullName;
        try
        {
            Estreno.Sala(dir);
            Assert.Contains("window.seek = seek", File.ReadAllText(Path.Combine(dir, "sala.html")));
            Assert.True(new FileInfo(Path.Combine(dir, "fonts", "Archivo-tramoya.woff2")).Length > 10_000);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AnUnknownFormatIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Estreno.EstrenarAsync("x.mp4", Escenas, "y.mp4", "x", formato: "square"));
    }

    [BrowserFact]
    public async Task APremiereFromASyntheticTake()
    {
        var dir = Directory.CreateTempSubdirectory("tramoya-estreno-").FullName;
        try
        {
            var toma = Path.Combine(dir, "video.mp4");
            new Runner().Run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=768x960:rate=30:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", toma]);
            var salida = Path.Combine(dir, "estreno.mp4");
            var p = await Estreno.EstrenarAsync(toma, [("One", 2.0), ("Two", 2.0)], salida, "Test", formato: "vertical", duracionMax: 10);
            Assert.True(p.Cabe);
            var (tam, segundos, conSonido) = Estreno.Medidas(salida);
            Assert.Equal((1080, 1920), tam);
            Assert.InRange(segundos, 9.5, 10.2);
            Assert.True(conSonido);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EasySettings()
    {
        Assert.True(Engine.DeEstreno(Obj("{}")));
        Assert.False(Engine.DeEstreno(Obj("""{"acabado": "bruto"}""")));
        Assert.Equal(Vocabulario.Vertical, Engine.ViewportDe(Obj("""{"formato": "vertical", "tamano": "1080p"}""")));
        Assert.Equal((1280, 720), Engine.ViewportDe(Obj("""{"tamano": "720p"}""")));
        Assert.Equal(60, Engine.DuracionMaxima(Obj("""{"duracion": "60"}""")));
        Assert.Equal(60, Engine.DuracionMaxima(Obj("""{"duracion": 60}""")));
        Assert.Equal(0, Engine.DuracionMaxima(Obj("""{"duracion": "x"}""")));
    }
}
