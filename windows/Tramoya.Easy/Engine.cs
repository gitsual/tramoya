using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Tramoya.Agent;
using Tramoya.Core;
using Tramoya.Stage;
using Tramoya.Voice;
using AgentRunner = Tramoya.Agent.Agent;

namespace Tramoya.Easy;

/// <summary>
/// El motor de «tramoya easy»: clics dentro, pasos fuera; pasos dentro, vídeo fuera.
/// Port de <c>tramoya/easy/engine.py</c> (la parte pura está en <see cref="Pasos"/>,
/// <see cref="CodegenSteps"/> y <see cref="Vocabulario"/>; la voz, en <see cref="Sapi"/>).
/// </summary>
/// <remarks>
/// <para>
/// <c>Tramoya.exe easy-engine grabar &lt;carpeta&gt; &lt;url&gt; &lt;perfil&gt; [ajustes]</c> abre la web en
/// un perfil de navegador que se conserva (las sesiones siguen abiertas de un vídeo a otro) y
/// no apunta nada hasta que lee <c>empezar</c> por la entrada estándar, así que el inicio de
/// sesión nunca forma parte de la grabación. Los selectores los genera el propio Playwright
/// (<c>window.playwright.selector</c>, que existe con <c>PWDEBUG=console</c>): por rol y nombre
/// visible, nunca los ids que cambian en cada carga.
/// </para>
/// <para>
/// <c>video &lt;carpeta&gt; [--voz]</c> convierte los pasos en escenas de tramoya (un paso con frase
/// abre una escena: la frase es el rótulo y, con <c>--voz</c>, la locución que lee la voz de
/// Windows), los reproduce en el escenario y monta la toma. Cada orden escribe un objeto JSON
/// por línea: progreso (<c>{"fase", "msg", "p"}</c>) y al final <c>{"ok": true, "video": ...}</c>
/// o <c>{"error": ..., "paso": n}</c>. El servidor lee esas líneas por la tubería.
/// </para>
/// <para>El texto que ve la persona está en español; los nombres también, como en el Python.</para>
/// </remarks>
public static class Engine
{
    private const string Ayuda = """
        El motor de «tramoya easy»: clics dentro, pasos fuera; pasos dentro, vídeo fuera.

            Tramoya.exe easy-engine grabar <carpeta> <url> <perfil> [ajustes-json]
            Tramoya.exe easy-engine agente <carpeta> <url> <perfil> <ajustes-json> <objetivo>
            Tramoya.exe easy-engine video <carpeta> [--voz]
            Tramoya.exe easy-engine pasos <codegen.py>     pasos de un fichero de codegen (JSON)
            Tramoya.exe easy-engine voces                  voces SAPI instaladas (español primero)
            Tramoya.exe easy-engine oir <json>             {"texto", "voz", "velocidad"} por los altavoces
        """;

    // --- salida por líneas ------------------------------------------------------------

    /// <summary>
    /// Un objeto JSON por línea, compacto, sin escapar los acentos (<c>ensure_ascii=False</c>)
    /// y vaciado al momento: el servidor lo lee por la tubería mientras el motor sigue.
    /// </summary>
    public static void Emitir(params (string Clave, object? Valor)[] datos)
    {
        var objeto = new JsonObject();
        foreach (var (clave, valor) in datos)
            objeto[clave] = valor switch
            {
                null => null,
                JsonNode nodo => nodo.DeepClone(),
                _ => JsonSerializer.SerializeToNode(valor, Json.Compact),
            };
        Emitir(objeto);
    }

    public static void Emitir(JsonObject datos)
    {
        var salida = Console.Out;
        salida.Write(Json.Dump(datos, indent: false));
        salida.Write('\n');
        salida.Flush();
    }

    // --- el grabador propio: una sola ventana, sirve para cualquier web ----------------
    //
    // Se abre la web en un perfil de navegador que se conserva (las sesiones siguen
    // abiertas de un vídeo a otro). La persona entra, va a donde empieza el proceso y
    // pulsa «Empezar» en la app: desde ese momento cada clic, texto o tecla se apunta
    // con los selectores que genera el propio Playwright (por rol y nombre visible,
    // no por los ids que cambian en JSF). El inicio de sesión nunca se graba.

    /// <summary>Lo que apunta <c>escucha.js</c> en la página, convertido en un paso legible; null si no vale.</summary>
    public static JsonObject? PasoDeEvento(JsonObject d, string prefijo)
    {
        var tipo = Pasos.Str(d, "tipo");
        var nombre = Pasos.Str(d, "nombre");
        var que = Pasos.Str(d, "que");
        var cosa = nombre.Length != 0
            ? (que.Length != 0 ? $"{que} «{nombre}»" : $"«{nombre}»")
            : (que.Length != 0 ? que : "este elemento");
        var valor = Pasos.Str(d, "valor");
        if (tipo == "tecla")
            return new() { ["tipo"] = "tecla", ["valor"] = valor, ["desc"] = $"Pulsa la tecla {Vocabulario.Teclas.GetValueOrDefault(valor, valor)}" };
        var selectorCrudo = Pasos.Str(d, "selector");
        if (selectorCrudo.Length == 0) return null;
        var selector = prefijo + selectorCrudo;
        switch (tipo)
        {
            case "clic":
            {
                var verbo = Pasos.Str(d, "marca") switch { "on" => "Marca", "off" => "Desmarca", _ => "Pulsa" };
                // El aviso de cookies sale la primera vez y luego ya no: ese clic es opcional.
                if (Vocabulario.Cookies.IsMatch(nombre) || Vocabulario.Cookies.IsMatch(selectorCrudo))
                    return new() { ["tipo"] = "clic", ["selector"] = selector, ["opcional"] = true, ["desc"] = $"{verbo} {cosa} (aviso de cookies: si no sale, se salta)" };
                return new() { ["tipo"] = "clic", ["selector"] = selector, ["desc"] = $"{verbo} {cosa}", ["guarda"] = Vocabulario.Guardan.IsMatch(nombre) };
            }
            case "escribir":
            {
                var donde = nombre.Length != 0 ? $" en el campo «{nombre}»" : "";
                return new() { ["tipo"] = "escribir", ["selector"] = selector, ["valor"] = valor, ["desc"] = $"Escribe «{valor}»{donde}" };
            }
            case "elegir":
            {
                var donde = nombre.Length != 0 && nombre.Length < 40 ? $" en «{nombre}»" : "";
                var textoOpcion = Pasos.Str(d, "texto_opcion");
                var visible = textoOpcion.Length != 0 ? textoOpcion : valor;
                return new() { ["tipo"] = "elegir", ["selector"] = selector, ["valor"] = valor, ["desc"] = $"Elige «{visible}»{donde}" };
            }
            default:
                return null;
        }
    }

    /// <summary>Las webs con marcos (muy típicas en aplicaciones de empresa): el camino hasta él.</summary>
    public static async Task<string> PrefijoMarcoAsync(IFrame frame)
    {
        var partes = new List<string>();
        while (frame.ParentFrame is not null)
        {
            var elemento = await frame.FrameElementAsync().ConfigureAwait(false);
            partes.Insert(0, await elemento.EvaluateAsync<string>("e => window.playwright.selector(e)").ConfigureAwait(false));
            frame = frame.ParentFrame;
        }
        return string.Concat(partes.Select(p => $"{p} >> internal:control=enter-frame >> "));
    }

    /// <summary>La web, su dominio («app.empresa.com» -> «*.empresa.com») y los de Ajustes.</summary>
    public static List<string> DominiosDe(string url, IEnumerable<string>? extra = null)
    {
        var host = HostDe(url);
        var salida = new List<string>();
        if (host.Length != 0) salida.Add(host);
        var trozos = host.Split('.');
        if (trozos.Length > 2 && !host.Replace(".", "").All(char.IsAsciiDigit))
            salida.Add("*." + string.Join(".", trozos[^2..]));
        foreach (var crudo in extra ?? [])
        {
            var d = crudo.Trim();
            if (d.Length != 0 && !salida.Contains(d)) salida.Add(d);
        }
        // Inicio de sesión integrado de Windows (Kerberos/NTLM): la web, su dominio y los que
        // se añadan en Ajustes. El de Microsoft va siempre porque muchas webs de empresa entran
        // a través de él.
        salida.AddRange(Vocabulario.SsoSiempre);
        return salida;
    }

    public static List<string> ArgumentosNavegador(string url, IEnumerable<string>? extra = null)
    {
        var dominios = string.Join(",", DominiosDe(url, extra));
        var lista = new List<string> { $"--auth-server-allowlist={dominios}", "--disable-features=Translate", "--lang=es-ES" };
        // TRAMOYA_CDP: un puerto de depuración, para que las pruebas hagan clics de verdad.
        var cdp = Environment.GetEnvironmentVariable("TRAMOYA_CDP");
        if (!string.IsNullOrEmpty(cdp)) lista.Add($"--remote-debugging-port={cdp}");
        return lista;
    }

    /// <summary>El <c>hostname</c> de <c>urlparse</c>: en minúsculas, sin puerto ni corchetes; "" si no hay.</summary>
    private static string HostDe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown)
            return "";
        return uri.DnsSafeHost.ToLowerInvariant();
    }

    public static async Task<int> GrabarAsync(string carpeta, string url, string perfil, JsonObject? ajustes = null)
    {
        // Da window.playwright.selector() en cada página; tiene que estar puesto antes de que
        // arranque el proceso de Playwright, que es quien lo hereda.
        Environment.SetEnvironmentVariable("PWDEBUG", "console");

        ajustes ??= [];
        var (ancho, alto) = Vocabulario.Tamanos.GetValueOrDefault(Pasos.Str(ajustes, "tamano"), Vocabulario.Viewport);
        var ordenes = new ConcurrentQueue<string>();
        var lector = new Thread(() =>
        {
            string? linea;
            while ((linea = Console.In.ReadLine()) is not null)
                ordenes.Enqueue(linea.Trim());
        }) { IsBackground = true, Name = "ordenes" };
        lector.Start();
        var eventos = new ConcurrentQueue<(IPage Page, IFrame Frame, JsonElement Dato)>();
        var pasos = new List<JsonObject>();
        var avisos = new SortedSet<string>(StringComparer.Ordinal);
        string? inicio = null;
        var titulo = "";

        using (var pw = await Playwright.CreateAsync().ConfigureAwait(false))
        {
            var ctx = await pw.Chromium.LaunchPersistentContextAsync(perfil, new()
            {
                Headless = SinVentana,
                ViewportSize = new ViewportSize { Width = ancho, Height = alto },
                IgnoreHTTPSErrors = true,
                Locale = "es-ES",
                Args = ArgumentosNavegador(url, Dominios(ajustes)),
            }).ConfigureAwait(false);
            ctx.SetDefaultNavigationTimeout(60000);
            var principal = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync().ConfigureAwait(false);
            await ctx.ExposeBindingAsync<JsonElement>("__tramoyaPaso",
                (BindingSource fuente, JsonElement d) => eventos.Enqueue((fuente.Page, fuente.Frame, d))).ConfigureAwait(false);
            await ctx.AddInitScriptAsync(EscuchaJs()).ConfigureAwait(false);
            try { await principal.GotoAsync(url).ConfigureAwait(false); }
            catch (Exception) { /* un inicio de sesión lento no es un fallo */ }
            Emitir(("estado", "preparado"));
            while (true)
            {
                try { await principal.WaitForTimeoutAsync(150).ConfigureAwait(false); }
                catch (Exception) { break; } // han cerrado la ventana
                if (principal.IsClosed) break;
                if (!ordenes.TryDequeue(out var orden)) orden = "";
                if (orden == "terminar") break;
                if (orden == "empezar" && inicio is null)
                {
                    inicio = principal.Url;
                    try { titulo = Recortar((await principal.TitleAsync().ConfigureAwait(false)).Trim(), 60); }
                    catch (Exception) { titulo = ""; }
                    eventos.Clear();
                    Emitir(("estado", "grabando"), ("inicio", inicio));
                }
                while (eventos.TryDequeue(out var evento))
                {
                    var (pagina, marco, dato) = evento;
                    if (inicio is null) continue;
                    if (!ReferenceEquals(pagina, principal))
                    {
                        avisos.Add("Se abrió otra pestaña o ventana; lo que se hizo en ella no se puede repetir.");
                        continue;
                    }
                    if (dato.ValueKind != JsonValueKind.Object) continue;
                    var d = JsonObject.Create(dato)!;
                    if (Pasos.Str(d, "tipo") == "clave")
                    {
                        avisos.Add("No se ha apuntado ninguna contraseña.");
                        continue;
                    }
                    string prefijo;
                    try { prefijo = await PrefijoMarcoAsync(marco).ConfigureAwait(false); }
                    catch (Exception) { prefijo = ""; }
                    var paso = PasoDeEvento(d, prefijo);
                    if (paso is not null)
                    {
                        pasos.Add(paso);
                        // Limpiar numera y funde pasos sobre copias: la lista cruda sigue creciendo.
                        var n = Pasos.Limpiar(pasos.Select(p => (JsonObject)p.DeepClone()).ToList()).Count;
                        Emitir(("paso", Pasos.Str(paso, "desc")), ("n", n));
                    }
                }
            }
            try { await ctx.StorageStateAsync(new() { Path = Path.Combine(carpeta, "auth.json") }).ConfigureAwait(false); }
            catch (Exception) { /* sin sesión que guardar */ }
            try { await ctx.CloseAsync().ConfigureAwait(false); }
            catch (Exception) { /* ya estaba cerrado */ }
        }
        var limpios = Pasos.Limpiar(pasos);
        var grabacion = new JsonObject
        {
            ["inicio"] = inicio,
            ["titulo"] = titulo,
            ["pasos"] = new JsonArray(limpios.Select(p => (JsonNode)p.DeepClone()).ToArray()),
            ["avisos"] = new JsonArray(avisos.Select(a => (JsonNode)a).ToArray()),
        };
        Json.Write(Path.Combine(carpeta, "grabacion.json"), grabacion, trailingNewline: false);
        Emitir(("fin", true), ("n", limpios.Count), ("inicio", inicio));
        return 0;
    }

    /// <summary>El oído de la página, incrustado desde <c>src/tramoya/easy/ui/escucha.js</c> (una sola fuente).</summary>
    internal static string EscuchaJs()
    {
        using var flujo = typeof(Engine).Assembly.GetManifestResourceStream("ui/escucha.js")
            ?? throw new InvalidOperationException("falta el recurso ui/escucha.js");
        using var lector = new StreamReader(flujo, Encoding.UTF8);
        return lector.ReadToEnd();
    }

    private static List<string> Dominios(JsonObject ajustes) =>
        ajustes["dominios"] is JsonArray lista ? lista.Select(x => x?.ToString() ?? "").ToList() : [];

    private static string Recortar(string s, int n) => s.Length <= n ? s : s[..n];

    // --- la IA hace el proceso --------------------------------------------------------------
    //
    // La misma ventana del grabador (el perfil que conserva las sesiones, los selectores que
    // genera Playwright), pero quien hace los clics es un modelo de lenguaje a partir de lo que
    // la persona describe. Sale el mismo grabacion.json que al grabar a mano, con la frase de
    // cada paso ya escrita, y el servidor lo pasa al proyecto igual que una grabación.

    /// <summary>
    /// Con <c>TRAMOYA_HEADLESS=1</c> el agente y el vídeo van sin ventana: para grabar la ventana de
    /// Tramoya con la propia tramoya sin que el navegador asome por el escritorio.
    internal static bool SinVentana => Environment.GetEnvironmentVariable("TRAMOYA_HEADLESS") == "1";

    /// Abre la web en el perfil de <c>easy</c>, deja que el agente la recorra y guarda la sesión
    /// en <paramref name="auth"/>. Es la única implementación: la usan la ventana (por el
    /// subcomando <c>agente</c>) y el CLI (<c>tramoya agent</c>).
    /// </summary>
    public static async Task<AgentResult> CorrerAgenteAsync(string url, string perfil, AgentOptions opciones, JsonObject ajustes,
        string auth, IChatBackend backend, IProgress<string>? log, CancellationToken ct)
    {
        // Da window.playwright.selector() en cada página (los selectores duraderos del agente);
        // tiene que estar puesto antes de que arranque el proceso de Playwright, que lo hereda.
        Environment.SetEnvironmentVariable("PWDEBUG", "console");
        var (ancho, alto) = Vocabulario.Tamanos.GetValueOrDefault(Pasos.Str(ajustes, "tamano"), Vocabulario.Viewport);
        using var pw = await Playwright.CreateAsync().ConfigureAwait(false);
        var ctx = await pw.Chromium.LaunchPersistentContextAsync(perfil, new()
        {
            Headless = false,
            ViewportSize = new ViewportSize { Width = ancho, Height = alto },
            IgnoreHTTPSErrors = true,
            Locale = "es-ES",
            Args = ArgumentosNavegador(url, Dominios(ajustes)),
        }).ConfigureAwait(false);
        try
        {
            ctx.SetDefaultNavigationTimeout(60000);
            var page = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync().ConfigureAwait(false);
            try { await page.GotoAsync(url).ConfigureAwait(false); }
            catch (Exception) { /* un inicio de sesión lento no es un fallo */ }
            var resultado = await AgentRunner.RunAsync(page, opciones, backend, log, ct).ConfigureAwait(false);
            try { await ctx.StorageStateAsync(new() { Path = auth }).ConfigureAwait(false); }
            catch (Exception) { /* sin sesión que guardar */ }
            return resultado;
        }
        finally
        {
            try { await ctx.CloseAsync().ConfigureAwait(false); }
            catch (Exception) { /* ya estaba cerrado */ }
        }
    }

    /// <summary>Tope de pasos cuando se le pide desde la ventana; el CLI tiene su <c>--max-pasos</c>.</summary>
    public const int MaxPasosAgente = 25;

    /// <summary>
    /// <c>agente &lt;carpeta&gt; &lt;url&gt; &lt;perfil&gt; &lt;ajustes-json&gt; &lt;objetivo&gt;</c>: la IA hace el
    /// proceso y deja <c>auth.json</c> y <c>grabacion.json</c> en la carpeta, como el grabador.
    /// Habla por líneas: <c>{"fase":"agente","msg","p"}</c> con cada avance, <c>{"paso","n"}</c> con
    /// cada paso apuntado y al final <c>{"fin":true,"n","inicio"}</c> o <c>{"error"}</c>. El modelo
    /// sale de los ajustes (<c>ia_backend</c>, <c>ia_modelo</c>, opcionales) o del entorno, como en
    /// <see cref="Backends.Pick"/>.
    /// </summary>
    public static async Task<int> AgenteAsync(string carpeta, string url, string perfil, JsonObject? ajustes, string objetivo)
    {
        ajustes ??= [];
        try
        {
            // Lo que puede fallar sin abrir nada se comprueba antes: sin carpeta no hay dónde dejar
            // el resultado y sin modelo no hay a quién preguntar.
            if (!Directory.Exists(carpeta)) throw new DirectoryNotFoundException($"no existe la carpeta {carpeta}");
            if (objetivo.Trim().Length == 0) throw new ArgumentException("no me has dicho qué tiene que hacer");
            var backend = Backends.Pick(Vacio(Pasos.Str(ajustes, "ia_backend")), Vacio(Pasos.Str(ajustes, "ia_modelo")));
            var opciones = new AgentOptions(objetivo.Trim(), "es", MaxPasosAgente);
            var r = await CorrerAgenteAsync(url, perfil, opciones, ajustes, Path.Combine(carpeta, "auth.json"),
                backend, new ProgresoAgente(MaxPasosAgente), CancellationToken.None).ConfigureAwait(false);
            var limpios = Pasos.Limpiar(r.Pasos);
            if (limpios.Count == 0)
            {
                // Sin pasos no hay nada que revisar: el motivo es lo que el modelo (o el agente) ha dicho.
                var motivo = r.Resumen.Length != 0 ? r.Resumen : r.Avisos.LastOrDefault() ?? "no ha hecho ningún paso";
                throw new InvalidOperationException(motivo);
            }
            var inicio = r.Inicio.Length != 0 ? r.Inicio : url;
            var grabacion = new JsonObject
            {
                ["inicio"] = inicio,
                ["titulo"] = r.Titulo,
                ["pasos"] = new JsonArray(limpios.Select(p => (JsonNode)p.DeepClone()).ToArray()),
                ["avisos"] = new JsonArray(r.Avisos.Select(a => (JsonNode)a).ToArray()),
                ["resumen"] = r.Resumen,
                ["ok"] = r.Ok,
            };
            Json.Write(Path.Combine(carpeta, "grabacion.json"), grabacion, trailingNewline: false);
            Emitir(("fin", true), ("n", limpios.Count), ("inicio", inicio));
            return 0;
        }
        catch (Exception e)
        {
            var motivo = Recortar((e.Message ?? e.GetType().Name).Split('\n', 2)[0].TrimEnd('\r').Trim().TrimEnd('.'), 200);
            Emitir(("error", $"La IA no ha podido hacerlo: {motivo}."));
            return 1;
        }
    }

    private static string? Vacio(string s) => s.Length == 0 ? null : s;

    /// <summary>
    /// El registro del agente convertido en las líneas que lee el servidor. El agente solo da
    /// texto, así que se reconoce por su forma: «Paso N: …» es un paso apuntado (y va también
    /// como <c>{"paso","n"}</c>, para el contador de la ventana), la frase que escribe justo después
    /// se pega a ese mensaje para que se vea qué se va a decir, y de «Turno N: acción — porqué» se
    /// enseña solo el porqué, que es lo que una persona entiende. El avance es pasos / tope.
    /// </summary>
    private sealed class ProgresoAgente(int maxPasos) : IProgress<string>
    {
        private static readonly Regex Paso = new(@"^Paso (\d+): (.*?) — porque ", RegexOptions.CultureInvariant);
        private static readonly Regex Turno = new(@"^Turno \d+: .*? — (.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Frase = new(@"^\s+«(.*)»$", RegexOptions.CultureInvariant);
        private int _n;
        private string _ultimo = "";

        public void Report(string linea)
        {
            if (Paso.Match(linea) is { Success: true } m)
            {
                _n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                _ultimo = $"Paso {_n}: {m.Groups[2].Value}";
                Avance(_ultimo);
                Emitir(("paso", m.Groups[2].Value), ("n", _n));
                return;
            }
            if (_ultimo.Length != 0 && Frase.Match(linea) is { Success: true } f)
            {
                Avance($"{_ultimo} — {f.Groups[1].Value}");
                return;
            }
            if (Turno.Match(linea) is { Success: true } t)
            {
                Avance(t.Groups[1].Value.Trim());
                return;
            }
            Avance(linea.Trim());
        }

        private void Avance(string msg) =>
            Emitir(("fase", "agente"), ("msg", msg), ("p", Math.Min(1.0, (double)_n / maxPasos)));
    }

    // --- grabar y montar ----------------------------------------------------------------

    /// <summary>La web ha redirigido a otro sitio al abrirse: hay que volver a entrar.</summary>
    public sealed class SesionCaducada(string url) : Exception(url);

    /// <summary>Un paso del proyecto que no se ha podido reproducir, con su causa.</summary>
    public sealed class FalloPaso(JsonObject paso, Exception causa) : Exception(causa.Message, causa)
    {
        public JsonObject Paso { get; } = paso;
        public Exception Causa { get; } = causa;
    }

    /// <summary>Lo que ha fallado, dicho para una persona y no para quien depura.</summary>
    public static string ExplicarFallo(Exception e)
    {
        var t = e.Message ?? "";
        if (t.Contains("Timeout", StringComparison.Ordinal) || t.Contains("timeout", StringComparison.Ordinal)
            || t.Contains("not visible", StringComparison.Ordinal))
            return "no lo he encontrado en la página (puede que la web haya cambiado o tarde más)";
        if (t.Contains("strict mode", StringComparison.Ordinal))
            return "hay varios iguales en la página y no sé cuál es";
        var primera = t.Split('\n', 2)[0].TrimEnd('\r');
        return Recortar(primera, 200);
    }

    public static async Task<int> HacerVideoAsync(string carpeta, bool conVoz)
    {
        // El Python sustituye probe_duration por una sonda con ffmpeg porque el kit no lleva
        // ffprobe; aquí Runner.ProbeDuration ya cae a ffmpeg por sí solo.
        var proyecto = Json.LoadNode(Path.Combine(carpeta, "proyecto.json"))!.AsObject();
        var url = Pasos.Str(proyecto, "inicio");
        if (url.Length == 0) url = proyecto["url"]?.ToString() ?? throw new KeyNotFoundException("url");
        var ajustes = proyecto["ajustes"] as JsonObject ?? [];
        var viewport = Vocabulario.Tamanos.GetValueOrDefault(Pasos.Str(ajustes, "tamano"), Vocabulario.Viewport);
        var dominios = Dominios(ajustes);
        var lista = Pasos.Escenas(proyecto);
        var salida = Path.Combine(carpeta, "out");
        try { if (Directory.Exists(salida)) Directory.Delete(salida, recursive: true); }
        catch (Exception) { /* como rmtree(ignore_errors=True) */ }
        var voces = Path.Combine(salida, "voices", "es");
        Directory.CreateDirectory(voces);

        if (conVoz)
        {
            Emitir(("fase", "voz"), ("msg", "Preparando la voz…"), ("p", 0.05));
            var textos = new Dictionary<string, string>();
            foreach (var e in lista.Where(e => e.Texto.Length != 0))
                textos[Path.Combine(voces, $"scene-{e.Name}.wav")] = e.Texto;
            var voz = Pasos.Str(ajustes, "voz");
            Sapi.Hablar(textos, voz.Length != 0 ? voz : null, Entero(ajustes["velocidad"]));
        }

        Emitir(("fase", "grabar"), ("msg", "Grabando. No toques la ventana del navegador."), ("p", 0.15));
        var pacing = new Pacing(pace: Vocabulario.Ritmos.GetValueOrDefault(Pasos.Str(ajustes, "ritmo"), 1.0), lang: "es");
        // La voz ya está en los wav: en el escenario solo hay que esperar lo que duran.
        var narrador = new Narrator(Path.Combine(salida, "voices"), pacing,
            clip => new SilentClip(Narration.WavSeconds(clip), pacing));
        var marks = new Marks(path: Path.Combine(salida, "marks.json"));
        var auth = Path.Combine(carpeta, "auth.json");
        var total = Math.Max(1, lista.Sum(e => e.Items.Count));
        var hechos = 0;
        string take;
        // La reproducción, con los mismos permisos que la grabación: inicio de sesión de
        // Windows en las webs de la empresa y sus certificados internos.
        await using (var sesion = await Browser.OpenStageAsync(url, pacing, marks,
                         videoDir: Path.Combine(salida, "raw"), viewport: viewport, headless: SinVentana,
                         storageState: File.Exists(auth) ? auth : null,
                         launchTweak: o => o.Args = ArgumentosNavegador(url, dominios),
                         contextTweak: o => { o.IgnoreHTTPSErrors = true; o.Locale = "es-ES"; }).ConfigureAwait(false))
        {
            var stage = sesion.Stage;
            var page = sesion.Page;
            page.SetDefaultTimeout(15000);
            marks.T0 = Marks.Now();
            take = await sesion.VideoPathAsync().ConfigureAwait(false)
                   ?? throw new InvalidOperationException("el navegador no está grabando");
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
            catch (Exception) { /* una web que nunca se calla no es un fallo */ }
            await stage.BeatAsync(1.0).ConfigureAwait(false);
            if (HostDe(page.Url) != HostDe(url))
                throw new SesionCaducada(page.Url);
            foreach (var e in lista)
            {
                var clave = $"scene-{e.Name}";
                Func<double, Task> say = async fallback => await narrador.SayAsync(clave, fallback).ConfigureAwait(false);
                using (marks.Scene(e.Name, e.Texto))
                {
                    await stage.CaptionAsync(e.Texto).ConfigureAwait(false);
                    if (e.Texto.Length != 0)
                        await Steps.RunAsync(stage, [$"say {Fmt.Py(Pasos.SegundosLectura(e.Texto))}"], say).ConfigureAwait(false);
                    foreach (var p in e.Items)
                    {
                        var opcional = Pasos.Bool(p, "opcional");
                        try
                        {
                            if (opcional) page.SetDefaultTimeout(3000);
                            await Steps.RunAsync(stage, Pasos.LineasDePaso(p), say, baseUrl: url).ConfigureAwait(false);
                        }
                        catch (Exception exc) when (!opcional)
                        {
                            throw new FalloPaso(p, exc);
                        }
                        catch (Exception)
                        {
                            // Un paso opcional (el aviso de cookies) que no sale se salta.
                        }
                        finally
                        {
                            page.SetDefaultTimeout(15000);
                        }
                        hechos++;
                        Emitir(("fase", "grabar"), ("msg", $"Grabando paso {hechos} de {total}…"), ("p", 0.15 + 0.6 * hechos / total));
                    }
                    if (ReferenceEquals(e, lista[^1])) await stage.BeatAsync(2.0).ConfigureAwait(false);
                    await stage.CaptionAsync("").ConfigureAwait(false);
                }
            }
        }
        // Al cerrar el contexto Playwright termina de escribir el webm; solo entonces se copia.
        var toma = Path.Combine(salida, "take.webm");
        File.Copy(take, toma, overwrite: true);

        Emitir(("fase", "montar"), ("msg", "Montando el vídeo…"), ("p", 0.8));
        var final = Path.Combine(salida, "video.mp4");
        var tabla = Core.Assembly.AssembleMarks(new Runner(), toma, Path.Combine(salida, "marks.json"), voces, final, lang: "es");
        var nombre = Regex.Replace(Pasos.Str(proyecto, "nombre"), "[<>:\"/\\\\|?*]+", "").Trim();
        if (nombre.Length == 0) nombre = "vídeo";
        var destino = Path.Combine(Pasos.Str(proyecto, "mis_videos"), nombre + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destino))!);
        File.Copy(final, destino, overwrite: true);
        string? subtitulos = null;
        if (!ajustes.ContainsKey("srt") || Pasos.Bool(ajustes, "srt"))
        {
            var textos = lista.ToDictionary(e => e.Name, e => e.Texto);
            subtitulos = Path.ChangeExtension(destino, ".srt");
            File.WriteAllText(subtitulos,
                Pasos.Srt(tabla.Select(fila => (textos.GetValueOrDefault(fila.Name, ""), fila.Final))),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        Emitir(("ok", true), ("video", destino), ("srt", subtitulos), ("p", 1.0));
        return 0;
    }

    /// <summary>Un número de los ajustes (<c>velocidad</c>), venga como entero, como decimal o como texto.</summary>
    private static int Entero(JsonNode? nodo)
    {
        if (nodo is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
        return 0;
    }

    // --- punto de entrada -----------------------------------------------------------------

    /// <summary>Los argumentos que siguen a <c>easy-engine</c>; devuelve el código de salida del proceso.</summary>
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Out.WriteLine(Ayuda);
            return 2;
        }
        if (args[0] == "pasos")
        {
            var (pasos, avisos) = CodegenSteps.PasosDeGrabacion(File.ReadAllText(args[1], Encoding.UTF8));
            Emitir(("pasos", pasos), ("avisos", avisos));
            return 0;
        }
        if (args[0] == "grabar")
        {
            var ajustes = args.Length > 4 ? Json.Parse(args[4]) as JsonObject : null;
            return GrabarAsync(args[1], args[2], args[3], ajustes).GetAwaiter().GetResult();
        }
        if (args[0] == "agente")
        {
            if (args.Length < 6)
            {
                Emitir(("error", "La IA no ha podido hacerlo: faltan datos (carpeta, web, perfil, ajustes y objetivo)."));
                return 1;
            }
            JsonObject? ajustes;
            try { ajustes = Json.Parse(args[4]) as JsonObject; }
            catch (JsonException) { ajustes = null; } // unos ajustes ilegibles son los de fábrica
            return AgenteAsync(args[1], args[2], args[3], ajustes, args[5]).GetAwaiter().GetResult();
        }
        if (args[0] == "voces")
        {
            Emitir(("voces", Sapi.Voces()));
            return 0;
        }
        if (args[0] == "oir")
        {
            var d = Json.Parse(args[1])!.AsObject();
            var voz = Pasos.Str(d, "voz");
            Sapi.Oir(Pasos.Str(d, "texto"), voz.Length != 0 ? voz : null, Entero(d["velocidad"]));
            return 0;
        }
        try
        {
            return HacerVideoAsync(args[1], args.Contains("--voz")).GetAwaiter().GetResult();
        }
        catch (FalloPaso f)
        {
            var pista = "";
            try
            {
                // Si antes hubo un botón en «solo señalar», la web no avanzó y el paso de
                // después no tiene en qué apoyarse: es la causa más común y se dice.
                var pasos = Json.LoadNode(Path.Combine(args[1], "proyecto.json"))!["pasos"]!.AsArray().Cast<JsonObject>();
                var id = Entero(f.Paso["id"]);
                var antes = pasos.Where(p => Entero(p["id"]) < id && Pasos.Bool(p, "seguro") && !Pasos.Bool(p, "quitado")).ToList();
                if (antes.Count > 0)
                    pista = $" Puede ser porque el paso {Entero(antes[^1]["id"])} está en «solo señalar» y, al no pulsarse, la web no ha avanzado.";
            }
            catch (Exception) { /* sin proyecto legible no hay pista */ }
            Emitir(("error", $"No he podido hacer el paso {Pasos.Str(f.Paso, "id")} ({Pasos.Str(f.Paso, "desc")}): {ExplicarFallo(f.Causa)}.{pista}"),
                ("paso", f.Paso["id"]));
        }
        catch (SesionCaducada)
        {
            Emitir(("error", "La web pide volver a entrar (la sesión ha caducado). Ve a «Grabar», pulsa «Abrir la web», "
                             + "entra con tu usuario y cierra la ventana sin grabar; luego vuelve aquí y haz el vídeo."),
                ("sesion", true));
        }
        catch (Exception exc)
        {
            Emitir(("error", $"Algo ha fallado: {ExplicarFallo(exc)}"));
        }
        return 1;
    }
}
