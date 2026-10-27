using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Tramoya.Agent;

/// <summary>Lo que se le pide al agente.</summary>
/// <param name="Goal">El objetivo en lenguaje natural («Busca "Madrid" y abre el artículo»).</param>
/// <param name="Lang">El idioma de las frases (por ahora solo español).</param>
/// <param name="MaxPasos">Tope de pasos apuntados; las esperas y las acciones fallidas no cuentan.</param>
/// <param name="Capturas">Mandar también una captura de pantalla en cada turno (solo la aprovechan los modelos con visión).</param>
public sealed record AgentOptions(string Goal, string Lang = "es", int MaxPasos = 25, bool Capturas = false);

/// <summary>El resultado: los pasos en el formato de <c>easy</c> (con <c>texto</c> = la frase del narrador) y cómo acabó.</summary>
public sealed class AgentResult
{
    /// <summary>Solo cuando el modelo ha dicho «terminar».</summary>
    public bool Ok { get; set; }
    public string Resumen { get; set; } = "";
    /// <summary><c>tipo</c>, <c>selector</c>, <c>valor</c>, <c>desc</c>, <c>guarda</c>, <c>opcional</c> y <c>texto</c>, listos para <c>Pasos.Limpiar</c>.</summary>
    public List<JsonObject> Pasos { get; set; } = [];
    public List<string> Avisos { get; set; } = [];
    /// <summary>La url en la que empezó: el <c>inicio</c> del proyecto.</summary>
    public string Inicio { get; set; } = "";
    /// <summary>El título de la web al empezar, recortado como hace el grabador.</summary>
    public string Titulo { get; set; } = "";
}

/// <summary>
/// El agente: mira la página, pregunta al modelo, hace lo que dice y apunta el paso con el
/// selector duradero capturado ANTES de actuar (después el elemento puede haber desaparecido).
/// Explora en vivo: los botones que guardan se pulsan igual; el modo «solo señalar» es cosa del
/// vídeo, y para eso se apuntan con <c>guarda=true</c>.
/// </summary>
public static class Agent
{
    /// <summary>Cuántos turnos completos (observación + respuesta) se le enseñan al modelo.</summary>
    public const int Historial = 6;
    /// <summary>De esos, cuántos llevan la observación entera; los demás, solo un resumen (el contexto del modelo es finito).</summary>
    public const int HistorialCompleto = 2;

    public static async Task<AgentResult> RunAsync(IPage page, AgentOptions options, IChatBackend backend, IProgress<string>? log, CancellationToken ct)
    {
        var resultado = new AgentResult { Inicio = page.Url ?? "" };
        try { resultado.Titulo = Observer.Recortar((await page.TitleAsync().ConfigureAwait(false) ?? "").Trim(), 60); }
        catch (Exception) { resultado.Titulo = ""; }
        var system = Protocol.SystemPrompt(options.Goal);
        var historial = new List<(string Usuario, string Resumen, string Respuesta)>();
        var hechos = new List<string>();
        var ultimo = "acabamos de abrir la página.";
        var turnos = 0;
        var fallosSeguidos = 0;
        var miradasFallidas = 0;
        // Un modelo que no acierta nunca podría dar vueltas sin apuntar pasos: hay un tope de turnos también.
        var maxTurnos = options.MaxPasos * 2 + 4;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                resultado.Avisos.Add("Se ha cancelado antes de terminar.");
                break;
            }
            if (resultado.Pasos.Count >= options.MaxPasos)
            {
                resultado.Avisos.Add($"Se ha llegado al tope de {options.MaxPasos} pasos sin terminar.");
                break;
            }
            if (++turnos > maxTurnos)
            {
                resultado.Avisos.Add("El modelo no ha conseguido avanzar; se deja aquí.");
                break;
            }

            Observation obs;
            try { obs = await Observer.ObserveAsync(page).ConfigureAwait(false); }
            catch (Exception e)
            {
                // La página está navegando o se ha cerrado: se espera un poco y se vuelve a mirar (sin gastar turno).
                log?.Report($"No he podido mirar la página ({Breve(e)}); espero.");
                turnos--;
                if (++miradasFallidas > 5)
                {
                    resultado.Avisos.Add($"No se puede mirar la página: {Breve(e)}");
                    resultado.Resumen = resultado.Avisos[^1];
                    break;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }
            miradasFallidas = 0;
            var usuario = Protocol.Turno(options.Goal, Protocol.Observacion(obs), ultimo, hechos, options.MaxPasos - resultado.Pasos.Count);
            byte[]? captura = null;
            if (options.Capturas)
            {
                try { captura = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png }).ConfigureAwait(false); }
                catch (Exception) { captura = null; }
            }
            var mensajes = Mensajes(historial, usuario, captura);

            string respuesta;
            Decision decision;
            try
            {
                respuesta = await backend.CompleteAsync(system, mensajes, ct).ConfigureAwait(false);
                try
                {
                    decision = Protocol.ParseDecision(respuesta);
                }
                catch (ArgumentException e)
                {
                    // Un JSON torcido no es un fallo: se le enseña el error y se le pide otra vez, una sola.
                    log?.Report($"La respuesta del modelo no vale ({e.Message}); se lo pido otra vez.");
                    var reintento = new List<ChatMessage>(mensajes)
                    {
                        new("assistant", respuesta),
                        new("user", $"Tu respuesta no vale: {e.Message}. Responde SOLO con el objeto JSON de la siguiente acción."),
                    };
                    respuesta = await backend.CompleteAsync(system, reintento, ct).ConfigureAwait(false);
                    decision = Protocol.ParseDecision(respuesta);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                resultado.Avisos.Add("Se ha cancelado antes de terminar.");
                break;
            }
            catch (Exception e)
            {
                resultado.Avisos.Add($"El modelo no ha contestado bien: {Breve(e)}");
                resultado.Resumen = resultado.Avisos[^1];
                log?.Report(resultado.Resumen);
                break;
            }
            var resumenTurno = $"{decision.Accion}{(decision.Ref is int r ? $" {r}" : "")}{(decision.Valor.Length != 0 ? $" «{decision.Valor}»" : "")}";
            historial.Add((usuario, $"Página: {obs.Title} ({obs.Url})\nResultado de la última acción: {ultimo}", respuesta));
            if (historial.Count > Historial) historial.RemoveAt(0);
            log?.Report($"Turno {turnos}: {resumenTurno} — {decision.Pensamiento}");

            if (decision.Accion == "terminar")
            {
                resultado.Ok = true;
                resultado.Resumen = decision.Resumen.Length != 0 ? decision.Resumen : decision.Pensamiento;
                log?.Report($"Terminado: {resultado.Resumen}");
                break;
            }
            if (decision.Accion == "fallo")
            {
                resultado.Resumen = decision.Resumen.Length != 0 ? decision.Resumen : decision.Pensamiento;
                resultado.Avisos.Add($"El agente se ha rendido: {resultado.Resumen}");
                log?.Report($"No he podido: {resultado.Resumen}");
                break;
            }
            if (decision.Accion == "esperar")
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                await AsentarAsync(page).ConfigureAwait(false);
                ultimo = "he esperado un segundo.";
                continue;
            }

            JsonObject? paso;
            try
            {
                paso = await ActuarAsync(page, obs, decision, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                resultado.Avisos.Add("Se ha cancelado antes de terminar.");
                break;
            }
            catch (Exception e)
            {
                ultimo = $"la acción «{resumenTurno}» ha fallado: {Explicar(e)}";
                log?.Report($"Falló: {ultimo}");
                if (++fallosSeguidos >= 4)
                {
                    resultado.Avisos.Add("Cuatro acciones seguidas han fallado; se deja aquí.");
                    resultado.Resumen = resultado.Avisos[^1];
                    break;
                }
                continue;
            }
            fallosSeguidos = 0;
            if (paso is null)
            {
                ultimo = $"no existe el elemento {decision.Ref}; usa solo los números de la lista actual.";
                log?.Report($"El modelo ha pedido un elemento que no existe ({decision.Ref}).");
                continue;
            }
            await AsentarAsync(page).ConfigureAwait(false);
            paso["texto"] = decision.Frase;
            resultado.Pasos.Add(paso);
            var desc = paso["desc"]?.ToString() ?? "";
            hechos.Add(desc);
            ultimo = $"hecho: {desc}.";
            log?.Report($"Paso {resultado.Pasos.Count}: {desc} — porque {decision.Pensamiento}");
            if (decision.Frase.Length != 0) log?.Report($"   «{decision.Frase}»");
        }
        return resultado;
    }

    /// <summary>La conversación para el modelo: los últimos turnos (los antiguos, resumidos) y el de ahora.</summary>
    private static List<ChatMessage> Mensajes(List<(string Usuario, string Resumen, string Respuesta)> historial, string usuario, byte[]? captura)
    {
        var lista = new List<ChatMessage>();
        for (var i = 0; i < historial.Count; i++)
        {
            var completo = i >= historial.Count - HistorialCompleto;
            lista.Add(new ChatMessage("user", completo ? historial[i].Usuario : historial[i].Resumen));
            lista.Add(new ChatMessage("assistant", historial[i].Respuesta));
        }
        lista.Add(new ChatMessage("user", usuario, captura));
        return lista;
    }

    /// <summary>
    /// Hace la acción en la página y devuelve el paso apuntado; null si el elemento no existe.
    /// Una acción que falla lanza: quien llama se lo cuenta al modelo.
    /// </summary>
    private static async Task<JsonObject?> ActuarAsync(IPage page, Observation obs, Decision d, CancellationToken ct)
    {
        if (d.Accion == "tecla")
        {
            var tecla = Palabras.Tecla(d.Valor);
            await page.Keyboard.PressAsync(tecla).ConfigureAwait(false);
            return new JsonObject { ["tipo"] = "tecla", ["valor"] = tecla, ["desc"] = $"Pulsa la tecla {Palabras.Teclas.GetValueOrDefault(tecla, tecla)}" };
        }
        if (d.Accion == "ir")
        {
            var url = d.Valor.Trim();
            await page.GotoAsync(url).ConfigureAwait(false);
            return new JsonObject { ["tipo"] = "ir", ["valor"] = url, ["desc"] = $"Ve a «{url}»" };
        }
        var el = d.Ref is int n ? obs.Elemento(n) : null;
        if (el is null) return null;
        // El selector, antes de actuar: después del clic el elemento puede no estar.
        string selector;
        try { selector = await Observer.SelectorCompletoAsync(page, obs, el).ConfigureAwait(false); }
        catch (Exception) { selector = ""; }
        if (selector.Length == 0) selector = $"[data-tramoya-ref=\"{el.Ref}\"]";
        var locator = Observer.LocatorDe(page, obs, el);
        var nombre = el.Nombre;
        var cosa = nombre.Length != 0 ? $"{el.Tipo} «{nombre}»" : el.Tipo;
        switch (d.Accion)
        {
            case "clic":
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 10000 }).ConfigureAwait(false);
                var verbo = el.Tipo == "la casilla" ? (el.Estado.Contains("marcado") ? "Desmarca" : "Marca") : "Pulsa";
                // El aviso de cookies sale la primera vez y luego ya no: ese clic es opcional.
                if (Palabras.Cookies.IsMatch(nombre) || Palabras.Cookies.IsMatch(selector))
                    return new JsonObject { ["tipo"] = "clic", ["selector"] = selector, ["opcional"] = true, ["desc"] = $"{verbo} {cosa} (aviso de cookies: si no sale, se salta)" };
                return new JsonObject { ["tipo"] = "clic", ["selector"] = selector, ["desc"] = $"{verbo} {cosa}", ["guarda"] = Palabras.Guardan.IsMatch(nombre) };
            }
            case "escribir":
            {
                await locator.FillAsync(d.Valor, new LocatorFillOptions { Timeout = 10000 }).ConfigureAwait(false);
                // fill() solo no despierta a todos los frameworks; el evento input sí.
                try { await locator.DispatchEventAsync("input").ConfigureAwait(false); } catch (Exception) { }
                var donde = nombre.Length != 0 ? $" en el campo «{nombre}»" : "";
                return new JsonObject { ["tipo"] = "escribir", ["selector"] = selector, ["valor"] = d.Valor, ["desc"] = $"Escribe «{d.Valor}»{donde}" };
            }
            case "elegir":
            {
                // El modelo ve el texto de las opciones; el paso guarda lo que Playwright acepte en «select».
                string elegido;
                try
                {
                    await locator.SelectOptionAsync(new SelectOptionValue { Label = d.Valor }, new LocatorSelectOptionOptions { Timeout = 10000 }).ConfigureAwait(false);
                    elegido = d.Valor;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    var valores = await locator.SelectOptionAsync(d.Valor, new LocatorSelectOptionOptions { Timeout = 10000 }).ConfigureAwait(false);
                    elegido = valores.Count != 0 ? valores[0] : d.Valor;
                }
                var donde = nombre.Length != 0 && nombre.Length < 40 ? $" en «{nombre}»" : "";
                return new JsonObject { ["tipo"] = "elegir", ["selector"] = selector, ["valor"] = elegido, ["desc"] = $"Elige «{d.Valor}»{donde}" };
            }
            default:
                throw new ArgumentException($"acción desconocida «{d.Accion}»");
        }
    }

    /// <summary>
    /// Deja que la página se asiente tras actuar: un respiro para que arranque la navegación que
    /// provoca un Intro o un clic (si se mira antes, el contexto se destruye a medias), luego la
    /// carga y la red en calma, cada una con tope, y otro respiro para lo que pinta después.
    /// </summary>
    private static async Task AsentarAsync(IPage page)
    {
        await Task.Delay(300).ConfigureAwait(false);
        try { await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = 5000 }).ConfigureAwait(false); }
        catch (Exception) { /* sigue cargando: se mira igual */ }
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 }).ConfigureAwait(false); }
        catch (Exception) { /* una web que nunca se calla no es un fallo */ }
        await Task.Delay(300).ConfigureAwait(false);
    }

    /// <summary>Lo que ha fallado, dicho para el modelo (y para el registro) sin la pila de Playwright.</summary>
    private static string Explicar(Exception e)
    {
        var t = e.Message ?? "";
        if (t.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || t.Contains("not visible", StringComparison.Ordinal))
            return "el elemento no se ha podido pulsar (no visible, tapado o ha tardado demasiado)";
        if (t.Contains("strict mode", StringComparison.Ordinal))
            return "hay varios elementos iguales";
        if (t.Contains("intercepts pointer events", StringComparison.Ordinal))
            return "otro elemento lo tapa (un aviso o un menú abierto)";
        return Breve(e);
    }

    private static string Breve(Exception e) => Observer.Recortar((e.Message ?? e.GetType().Name).Split('\n', 2)[0].TrimEnd('\r'), 200);
}

/// <summary>
/// Las palabras del grabador de <c>easy</c> que el agente también necesita (<c>Vocabulario</c> vive en
/// <c>Tramoya.Easy</c>, que es solo de Windows, y esta biblioteca no lo referencia). Si cambian
/// allí, cambian aquí.
/// </summary>
internal static class Palabras
{
    public static readonly Regex Guardan = new(@"^\s*(imputar|guardar|grabar|enviar|aceptar|confirmar|borrar|eliminar|firmar|validar|aprobar|copiar filas|save|submit|delete|ok)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Regex Cookies = new(@"cookie|aceptar tod|accept all|rechazar tod|reject all|permitir tod|consent|allow all", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Dictionary<string, string> Teclas = new()
    {
        ["Enter"] = "Intro", ["Tab"] = "Tabulador", ["Escape"] = "Esc", ["Backspace"] = "Borrar",
        ["ArrowDown"] = "flecha abajo", ["ArrowUp"] = "flecha arriba",
    };

    /// <summary>La tecla como la entiende Playwright, venga como la diga el modelo («Intro», «enter», «Esc»).</summary>
    public static string Tecla(string valor)
    {
        var v = valor.Trim();
        return v.ToLowerInvariant() switch
        {
            "enter" or "intro" or "return" or "\n" => "Enter",
            "tab" or "tabulador" => "Tab",
            "escape" or "esc" => "Escape",
            "backspace" or "borrar" => "Backspace",
            "arrowdown" or "down" or "abajo" or "flecha abajo" => "ArrowDown",
            "arrowup" or "up" or "arriba" or "flecha arriba" => "ArrowUp",
            "pagedown" or "avpág" or "avpag" => "PageDown",
            "pageup" or "repág" or "repag" => "PageUp",
            "space" or "espacio" => "Space",
            _ => v,
        };
    }
}
