using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Tramoya.Stage;

/// <summary>
/// The premiere («el estreno»): the assembled take, staged in a theatre. Port of <c>tramoya/estreno</c>.
/// <c>sala.html</c> (embedded, the same file the Python package ships) draws every frame as a pure function
/// of <c>seek(t)</c>: the curtain opens on the title, each scene comes in with its headline and its cue, the
/// cue sheet keeps count, the boards light up as it goes and the curtain falls with the house figures.
/// Playwright photographs it frame by frame and ffmpeg encodes it.
/// </summary>
public static class Estreno
{
    public static readonly Dictionary<string, (int Ancho, int Alto)> Formatos = new()
    { ["horizontal"] = (1920, 1080), ["vertical"] = (1080, 1920) };
    public const int Fps = 30;
    public const double VelMax = 1.5;
    public const int IntroMs = 3200, OutroMs = 4400;
    // A short film gets a shorter opening and close: they are part of the minute.
    public const int IntroCortaMs = 2600, OutroCortaMs = 3400;
    public const double CortaS = 45;
    private const string Origen = "http://tramoya.local";

    public static readonly Dictionary<string, Dictionary<string, string>> Palabras = new()
    {
        ["es"] = new()
        {
            ["presenta"] = "Tramoya presenta", ["cuenta"] = "Escena {i} de {n}", ["hoja"] = "Hoja de cues",
            ["sigue"] = "Sigue", ["fin"] = "Fin", ["funcion"] = "de la función", ["escena"] = "escena",
            ["escenas"] = "escenas", ["duracion"] = "duración", ["estreno"] = "estreno", ["hecho"] = "Hecho con Tramoya",
        },
        ["en"] = new()
        {
            ["presenta"] = "Tramoya presents", ["cuenta"] = "Scene {i} of {n}", ["hoja"] = "Cue sheet",
            ["sigue"] = "Next", ["fin"] = "The end", ["funcion"] = "of the show", ["escena"] = "scene",
            ["escenas"] = "scenes", ["duracion"] = "running time", ["estreno"] = "premiere", ["hecho"] = "Made with Tramoya",
        },
    };

    private static readonly Dictionary<string, string[]> Meses = new()
    {
        ["es"] = "enero febrero marzo abril mayo junio julio agosto septiembre octubre noviembre diciembre".Split(' '),
        ["en"] = "January February March April May June July August September October November December".Split(' '),
    };

    /// <summary>A scene on the film's clock: ms counted from the end of the opening.</summary>
    public sealed record Escena(string Texto, int Ini, int Dur);

    /// <summary>How the take fits in the film: its speed, the opening and close, and every scene.</summary>
    public sealed record Plan(double Vel, int IntroMs, int OutroMs, IReadOnlyList<Escena> Escenas, int TotalMs, bool Cabe);

    public static Plan PlanDe(IReadOnlyList<(string Texto, double Segundos)> escenas, double videoS, double duracionMax = 0)
    {
        var corta = duracionMax > 0 && duracionMax <= CortaS;
        var (intro, outro) = corta ? (IntroCortaMs, OutroCortaMs) : (IntroMs, OutroMs);
        var videoMs = videoS * 1000;
        double vel = 1.0;
        var cabe = true;
        if (duracionMax > 0)
        {
            var hueco = duracionMax * 1000 - intro - outro;
            if (hueco <= 0 || videoMs / hueco > VelMax) { vel = VelMax; cabe = false; }
            else if (videoMs > hueco) vel = videoMs / hueco;
        }
        var salida = new List<Escena>();
        double t = 0;
        foreach (var (texto, segundos) in escenas)
        {
            var dur = segundos * 1000 / vel;
            salida.Add(new Escena(texto, (int)Math.Round(t, MidpointRounding.ToEven), (int)Math.Round(dur, MidpointRounding.ToEven)));
            t += dur;
        }
        var total = intro + videoMs / vel + outro;
        return new Plan(Math.Round(vel, 4), intro, outro, salida, (int)Math.Round(total, MidpointRounding.ToEven), cabe);
    }

    private static (string Larga, string Corta) Fecha(string lang, DateTime hoy)
    {
        var mes = Meses[lang][hoy.Month - 1];
        var larga = lang == "es" ? $"{hoy.Day} de {mes} de {hoy.Year}" : $"{mes} {hoy.Day}, {hoy.Year}";
        return (larga, $"{hoy.Day} {mes[..3].ToUpperInvariant()} {hoy.Year}");
    }

    /// <summary>What sala.html reads (datos.json).</summary>
    public static JsonObject Datos(Plan p, string titulo, string lugar, string formato, int frames,
        (int W, int H) video, string lang = "es", DateTime? hoy = null)
    {
        lang = Palabras.ContainsKey(lang) ? lang : "en";
        var (larga, corta) = Fecha(lang, hoy ?? DateTime.Today);
        var (ancho, alto) = Formatos[formato];
        var t = new JsonObject();
        foreach (var (k, v) in Palabras[lang]) t[k] = v;
        return new JsonObject
        {
            ["formato"] = formato, ["ancho"] = ancho, ["alto"] = alto, ["titulo"] = titulo, ["lugar"] = lugar,
            ["fecha"] = larga, ["fecha_corta"] = corta,
            ["escenas"] = new JsonArray(p.Escenas.Select(e =>
                (JsonNode)new JsonObject { ["texto"] = e.Texto, ["ini"] = e.Ini, ["dur"] = e.Dur }).ToArray()),
            ["frames"] = frames, ["fps"] = Fps, ["vel"] = p.Vel, ["intro"] = p.IntroMs, ["outro"] = p.OutroMs,
            ["video"] = new JsonObject { ["w"] = video.W, ["h"] = video.H }, ["t"] = t,
        };
    }

    /// <summary>(width, height), seconds and whether it has sound, from ffmpeg's banner (the kit has no ffprobe).</summary>
    public static ((int W, int H) Tam, double Segundos, bool ConSonido) Medidas(string video, string ffmpeg = "ffmpeg")
    {
        var texto = Ejecutar(ffmpeg, ["-nostdin", "-hide_banner", "-i", video], comprobar: false);
        var tam = Regex.Match(texto, @"Video:.*?(\d{2,5})x(\d{2,5})");
        var dur = Regex.Match(texto, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!tam.Success || !dur.Success) throw new InvalidOperationException($"ffmpeg cannot read {video}");
        var s = int.Parse(dur.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(dur.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture);
        return ((int.Parse(tam.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(tam.Groups[2].Value, CultureInfo.InvariantCulture)),
            s, texto.Contains("Audio:", StringComparison.Ordinal));
    }

    /// <summary>(text, seconds) for every stretch of the film, the opening and close silent, for an .srt.</summary>
    public static List<(string Texto, double Dur)> Subtitulos(Plan p)
    {
        var filas = new List<(string, double)> { ("", p.IntroMs / 1000.0) };
        filas.AddRange(p.Escenas.Select(e => (e.Texto, e.Dur / 1000.0)));
        filas.Add(("", p.OutroMs / 1000.0));
        return filas;
    }

    /// <summary>sala.html and the window's typefaces, next to datos.json.</summary>
    public static void Sala(string destino)
    {
        Directory.CreateDirectory(Path.Combine(destino, "fonts"));
        Copiar("estreno/sala.html", Path.Combine(destino, "sala.html"));
        foreach (var nombre in new[] { "Archivo-tramoya.woff2", "IBMPlexMono-Medium.woff2" })
            Copiar($"estreno/fonts/{nombre}", Path.Combine(destino, "fonts", nombre));
    }

    private static void Copiar(string recurso, string destino)
    {
        using var s = typeof(Estreno).Assembly.GetManifestResourceStream(recurso)
                      ?? throw new InvalidOperationException($"missing resource {recurso}");
        using var f = File.Create(destino);
        s.CopyTo(f);
    }

    /// <summary>
    /// Stage <paramref name="video"/> (the assembled take) and write the film to <paramref name="salida"/>.
    /// Returns the plan: the scenes on the film's clock, the speed and whether it fitted.
    /// </summary>
    public static async Task<Plan> EstrenarAsync(string video, IReadOnlyList<(string Texto, double Segundos)> escenas,
        string salida, string titulo, string lugar = "", string formato = "horizontal", double duracionMax = 0,
        string lang = "es", string ffmpeg = "ffmpeg", Action<double>? progreso = null)
    {
        if (!Formatos.TryGetValue(formato, out var lienzo))
            throw new ArgumentException($"unknown format: {formato} (horizontal, vertical)", nameof(formato));
        var trabajo = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(salida))!, "estreno");
        if (Directory.Exists(trabajo)) Directory.Delete(trabajo, recursive: true);
        Directory.CreateDirectory(Path.Combine(trabajo, "frames"));
        var (tam, _, conSonido) = Medidas(video, ffmpeg);
        Ejecutar(ffmpeg, ["-nostdin", "-loglevel", "error", "-y", "-i", video, "-vf", $"fps={Fps}", "-q:v", "3",
            Path.Combine(trabajo, "frames", "%05d.jpg")]);
        var frames = Directory.GetFiles(Path.Combine(trabajo, "frames"), "*.jpg").Length;
        var p = PlanDe(escenas, (double)frames / Fps, duracionMax);
        File.WriteAllText(Path.Combine(trabajo, "datos.json"),
            Datos(p, titulo, lugar, formato, frames, tam, lang).ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
        Sala(trabajo);

        var mudo = Path.Combine(trabajo, "mudo.mp4");
        using (var pw = await Playwright.CreateAsync().ConfigureAwait(false))
        {
            await using var navegador = await pw.Chromium.LaunchAsync(new() { Headless = true }).ConfigureAwait(false);
            var page = await navegador.NewPageAsync(new() { ViewportSize = new() { Width = lienzo.Ancho, Height = lienzo.Alto } })
                .ConfigureAwait(false);
            await ServirDesdeDisco(page, trabajo).ConfigureAwait(false);
            await page.GotoAsync($"{Origen}/sala.html").ConfigureAwait(false);
            await page.WaitForFunctionAsync("window.ready === true", null, new() { Timeout = 120_000 }).ConfigureAwait(false);
            var duracion = await page.EvaluateAsync<double>("window.duration").ConfigureAwait(false);
            var total = (int)(duracion / 1000 * Fps);
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            foreach (var a in new[] { "-nostdin", "-loglevel", "error", "-y", "-f", "image2pipe", "-framerate", Fps.ToString(CultureInfo.InvariantCulture),
                         "-i", "-", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                         "-movflags", "+faststart", mudo })
                psi.ArgumentList.Add(a);
            using var ff = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {ffmpeg}");
            var tubo = ff.StandardInput.BaseStream;
            for (var f = 0; f < total; f++)
            {
                var ms = (f * 1000.0 / Fps).ToString("R", CultureInfo.InvariantCulture);
                await page.EvaluateAsync($"seek({ms})").ConfigureAwait(false);
                var foto = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 92 }).ConfigureAwait(false);
                await tubo.WriteAsync(foto).ConfigureAwait(false);
                if (progreso is not null && f % Fps == 0) progreso((double)f / total);
            }
            tubo.Close();
            await ff.WaitForExitAsync().ConfigureAwait(false);
            if (ff.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed while encoding the premiere");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(salida))!);
        if (conSonido)
        {
            // The voice follows the picture: as fast as the take and after the opening, silence under the curtain.
            // No afade (the voice is over by then), and stereo: some ffmpeg builds never finish a mono AAC at 192k.
            var inv = CultureInfo.InvariantCulture;
            var tempo = p.Vel != 1 ? $"atempo={p.Vel.ToString("0.0000", inv)}," : "";
            var fin = (p.TotalMs / 1000.0).ToString("0.000", inv);
            var filtro = $"[1:a]aresample=48000,{tempo}adelay={p.IntroMs}:all=1,apad=whole_dur={fin}[a]";
            Ejecutar(ffmpeg, ["-nostdin", "-loglevel", "error", "-y", "-i", mudo, "-i", video, "-filter_complex", filtro,
                "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-ac", "2", "-b:a", "192k",
                "-t", fin, "-movflags", "+faststart", salida]);
        }
        else File.Copy(mudo, salida, overwrite: true);
        try { Directory.Delete(Path.Combine(trabajo, "frames"), recursive: true); }
        catch (IOException) { /* a frame still open by an antivirus scan: it goes with the next premiere */ }
        return p;
    }

    /// <summary>The page reads its files straight from disk: no local server for an antivirus to slow down.</summary>
    private static Task ServirDesdeDisco(IPage page, string raiz)
    {
        var baseDir = Path.GetFullPath(raiz).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return page.RouteAsync($"{Origen}/**", async route =>
        {
            var ruta = Uri.UnescapeDataString(new Uri(route.Request.Url).AbsolutePath).TrimStart('/');
            var fichero = Path.GetFullPath(Path.Combine(baseDir, ruta));
            if (fichero.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && File.Exists(fichero))
                await route.FulfillAsync(new() { Path = fichero }).ConfigureAwait(false);
            else
                await route.FulfillAsync(new() { Status = 404, Body = "" }).ConfigureAwait(false);
        });
    }

    private static string Ejecutar(string exe, IEnumerable<string> argv, bool comprobar = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (comprobar && p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} failed ({p.ExitCode}): {err.Trim()[^Math.Min(400, err.Trim().Length)..]}");
        return err;
    }
}
