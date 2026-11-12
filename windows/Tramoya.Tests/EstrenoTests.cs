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
    public void TheSoundGraph()
    {
        var p = Estreno.PlanDe(Escenas, 30.0);
        Assert.Equal("", Estreno.FiltroSonido(p, voz: false, musica: false));
        var soloVoz = Estreno.FiltroSonido(p, voz: true, musica: false);
        Assert.StartsWith("[1:a]aresample=48000", soloVoz);
        Assert.DoesNotContain("[2:a]", soloVoz);
        var conTodo = Estreno.FiltroSonido(p, voz: true, musica: true);
        Assert.Contains("sidechaincompress", conTodo);
        Assert.Contains("volume=0.28", conTodo);
        Assert.Contains("volume=0.5", Estreno.FiltroSonido(p, voz: false, musica: true));
    }

    [Fact]
    public void TheMusicSetting()
    {
        var dir = Directory.CreateTempSubdirectory("tramoya-pistas-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Café Bossa Nova A.mp3"), [1]);
            Assert.Equal(["Café Bossa Nova A"], Engine.Pistas(dir).Keys);
            var trabajo = Path.Combine(dir, "out");
            Assert.EndsWith("ascensor.ogg", Engine.MusicaDe(Obj("{}"), dir, trabajo));
            Assert.True(new FileInfo(Path.Combine(trabajo, "ascensor.ogg")).Length > 500_000);
            Assert.Null(Engine.MusicaDe(Obj("""{"musica": "ninguna"}"""), dir, trabajo));
            Assert.EndsWith("Café Bossa Nova A.mp3", Engine.MusicaDe(Obj("""{"musica": "Café Bossa Nova A"}"""), dir, trabajo));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheVideoSpeaksTheLanguageOfItsSentences()
    {
        Assert.Equal("es", Pasos.IdiomaDe(["Esta es la lista de notas de casa.", "El buscador filtra mientras escribes."]));
        Assert.Equal("en", Pasos.IdiomaDe(["This is the list of notes.", "The search box filters as you type."]));
        Assert.Equal("es", Pasos.IdiomaDe(["¿Dónde está?"]));
        Assert.Null(Pasos.IdiomaDe(["OK"]));
        Assert.Null(Pasos.IdiomaDe([]));
    }

    [Fact]
    public void RandomMusicAndTheSeamlessLoop()
    {
        var dir = Directory.CreateTempSubdirectory("tramoya-bucle-").FullName;
        try
        {
            foreach (var n in new[] { "A", "B" }) File.WriteAllBytes(Path.Combine(dir, n + ".mp3"), [1]);
            File.WriteAllText(Path.Combine(dir, "A.json"), """{"negra": 0.5, "uno": 0.4, "bucle": [27.25, 45.0]}""");
            var trabajo = Path.Combine(dir, "out");
            var elegidas = Enumerable.Range(0, 60).Select(_ => Path.GetFileName(Engine.MusicaDe(Obj("""{"musica": "aleatoria"}"""), dir, trabajo)!)).ToHashSet();
            Assert.Equal(new HashSet<string> { "A.mp3", "B.mp3", "ascensor.ogg" }, elegidas);
            Assert.Equal((27.25, 45.0), Engine.BucleDe(Path.Combine(dir, "A.mp3")));
            Assert.Null(Engine.BucleDe(Path.Combine(dir, "B.mp3")));
            var p = Estreno.PlanDe(Escenas, 30.0);
            Assert.Contains("aloop=loop=-1:size=852000:start=1308000", Estreno.FiltroSonido(p, true, true, (27.25, 45.0)));
            Assert.DoesNotContain("aloop", Estreno.FiltroSonido(p, true, true));
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
