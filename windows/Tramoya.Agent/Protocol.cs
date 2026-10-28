using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tramoya.Agent;

/// <summary>Lo que el modelo decide en un turno, ya leído del JSON que devuelve.</summary>
/// <param name="Pensamiento">Por qué hace esto (va al registro, no al vídeo).</param>
/// <param name="Accion">clic, señalar, escribir, elegir, tecla, ir, esperar, terminar o fallo.</param>
/// <param name="Ref">El número del elemento, cuando la acción lo necesita.</param>
/// <param name="Valor">El texto que escribe, la opción que elige, la tecla o la url.</param>
/// <param name="Frase">Lo que dirá el narrador en el vídeo en este paso; vacío si no hay nada que decir.</param>
/// <param name="Resumen">Al terminar (o al rendirse): qué se ha conseguido.</param>
public sealed record Decision(string Pensamiento, string Accion, int? Ref, string Valor, string Frase, string Resumen);

/// <summary>
/// La conversación con el modelo: qué se le pide, cómo se le enseña la página y cómo se lee lo
/// que contesta. Todo en español porque las frases que escribe van al vídeo tal cual.
/// </summary>
public static class Protocol
{
    public static readonly IReadOnlySet<string> Acciones =
        new HashSet<string>(StringComparer.Ordinal) { "clic", "señalar", "escribir", "elegir", "tecla", "ir", "esperar", "terminar", "fallo" };

    /// <summary>Las acciones que necesitan un elemento numerado.</summary>
    public static readonly IReadOnlySet<string> ConRef = new HashSet<string>(StringComparer.Ordinal) { "clic", "señalar", "escribir", "elegir" };

    private static readonly Regex Pregunta = new(@"^\s*(¿|d[oó]nde\b|c[oó]mo\b|qu[eé]\b|cu[aá]l(es)?\b|hay\b|existe\b)|\?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Si el objetivo es una pregunta («¿dónde está…?», «cómo se cambia…») en vez de un encargo: entonces
    /// el agente no hace el proceso, lo enseña, y la respuesta se dice al final del vídeo.
    /// </summary>
    public static bool EsPregunta(string objetivo) => Pregunta.IsMatch(objetivo ?? "");

    /// <summary>
    /// Las reglas del juego, en español y cortas: un modelo local las lee en cada turno. El
    /// objetivo va dentro para que no se pierda cuando el historial se recorta.
    /// </summary>
    public static string SystemPrompt(string objetivo, bool pregunta = false) => $$"""
        Manejas un navegador para grabar un vídeo que enseña cómo se hace algo en una web. En cada turno ves la página (título, url, texto y una lista numerada de elementos) y decides UNA sola acción. Al final el vídeo se reproduce con tus pasos y un narrador lee tus frases.

        OBJETIVO: {{objetivo}}
        {{(pregunta ? ModoPregunta : "")}}
        Acciones (el campo "accion"):
        - "clic": pulsar el elemento "ref".
        - "señalar": señalar el elemento "ref" con el cursor sin pulsarlo: para enseñar dónde está algo.
        - "escribir": escribir "valor" en el campo "ref" (sustituye lo que hubiera).
        - "elegir": elegir la opción "valor" (su texto visible) en el desplegable "ref".
        - "tecla": pulsar la tecla "valor": Enter, Tab, Escape, ArrowDown, ArrowUp.
        - "ir": abrir la url "valor".
        - "esperar": esperar un segundo a que la página cargue.
        - "terminar": el objetivo ya está conseguido; di qué se ha hecho en "resumen".
        - "fallo": solo si de verdad no hay manera de seguir; explica por qué en "resumen".

        Reglas:
        1. Una sola acción por turno. Usa SOLO números "ref" de la lista del turno actual: cambian en cada turno.
        2. Para buscar algo: escribe en el campo de búsqueda y luego pulsa la tecla Enter (o el botón de buscar).
        3. Mira el resultado de tu última acción y la página actual antes de decidir; no repitas una acción que ya funcionó. Si una acción falló, prueba otra forma.
        4. Prefiere los elementos con nombre visible. Si la página aún está cargando o vacía, "esperar".
        5. Si la lista está recortada, lo que buscas puede no estar: haz scroll con la tecla ArrowDown o usa un enlace que lleve hacia ello.
        6. Cuando la página ya muestra lo que pide el objetivo, "terminar" en ese mismo turno. No hagas pasos de más.
        7. "frase": lo que dirá el narrador en el vídeo en ese paso, para la persona que lo ve: una frase corta, natural, en español, en presente y sin tecnicismos ("Escribimos el nombre del cliente", "Abrimos su ficha", "Pulsamos Intro para buscar"). Nada de "ref", "clic", "elemento", "Enter" ni números. En el primer paso, presenta además lo que se va a ver ("Vamos a dar de alta un cliente: empezamos por su nombre"). Deja "frase" vacía en los pasos que no aportan nada (cerrar un aviso, esperar, cerrar un menú).
        8. "pensamiento": una línea con el porqué. No es para el vídeo.

        Responde SOLO con un objeto JSON, sin texto alrededor, con esta forma exacta:
        {"pensamiento":"…","accion":"clic|escribir|elegir|tecla|ir|esperar|terminar|fallo","ref":N,"valor":"…","frase":"…","resumen":"…"}
        Ejemplo: {"pensamiento":"El campo 7 es el buscador del catálogo","accion":"escribir","ref":7,"valor":"impresora láser","frase":"Vamos a localizar una impresora en el catálogo: escribimos lo que buscamos","resumen":""}
        """.ReplaceLineEndings("\n");

    /// <summary>Lo que cambia cuando el objetivo es una pregunta: enseñar en vez de hacer, y contestar al final.</summary>
    public const string ModoPregunta = """

        MODO PREGUNTA: la persona no te encarga un proceso, te pregunta dónde está algo o cómo se hace. Tu trabajo es llevarla hasta allí y enseñárselo: navega lo justo (menús, enlaces, pestañas), y cuando tengas delante lo que pregunta, usa "señalar" sobre el elemento exacto y di en "frase" qué es, dónde está y para qué sirve. Si pregunta «cómo se hace», hazlo de verdad paso a paso, explicando cada paso. No escribas en campos ni cambies datos si la pregunta no lo pide. La respuesta suele estar escrita en la propia página: lee el "Texto visible" (lo que hay en pantalla) antes de moverte, y cítalo. Quédate en esta web: no abras enlaces externos ni los que llevan fuera (Figma, repositorio, "↗", otra pestaña). Señala el elemento exacto una vez; no señales varios iguales ni repitas la misma explicación con otro. En las "frase", habla de lo que la persona ve, como si estuvieras a su lado ("Aquí está…", "Este número de la esquina es…", "Con esto se copia…"): nunca "hacer clic", "señalar", "pulsar sobre el elemento" ni "vamos a ver cómo"; nombres como se leen en pantalla, sin variables, códigos ni números de elemento salvo que sean la respuesta. Al "terminar", escribe en "resumen" la respuesta a la pregunta en una o dos frases, como se la dirías a esa persona: el narrador la leerá al final del vídeo.
        """;

    /// <summary>La página en texto compacto: <c>N | tipo | nombre | valor | estado</c> por línea (las columnas vacías del final se omiten).</summary>
    public static string Observacion(Observation obs)
    {
        var sb = new StringBuilder();
        sb.Append("Título: ").Append(obs.Title.Length != 0 ? obs.Title : "(sin título)").Append('\n');
        sb.Append("Url: ").Append(obs.Url).Append('\n');
        var texto = obs.Text.Trim();
        if (texto.Length != 0)
            sb.Append("Texto visible: ").Append(texto.Replace("\r", "")).Append('\n');
        sb.Append("Elementos (número | tipo | nombre | valor | estado):\n");
        if (obs.Elements.Count == 0)
            sb.Append("(ninguno: la página está vacía o cargando)\n");
        string? marco = null;
        foreach (var e in obs.Elements)
        {
            if (e.Frame != marco)
            {
                marco = e.Frame;
                if (marco.Length != 0) sb.Append("[marco: ").Append(marco).Append("]\n");
            }
            sb.Append(Linea(e)).Append('\n');
        }
        if (obs.Total > obs.Elements.Count)
            sb.Append($"(lista recortada: hay {obs.Total} elementos y solo se muestran {obs.Elements.Count}; los que no se ven en pantalla van al final)\n");
        return sb.ToString();
    }

    public static string Linea(ObservedElement e)
    {
        var columnas = new List<string> { e.Ref.ToString(), e.Tipo, Limpio(e.Nombre), Limpio(e.Valor), e.Estado };
        while (columnas.Count > 3 && columnas[^1].Length == 0) columnas.RemoveAt(columnas.Count - 1);
        // Una columna vacía en medio queda como «| |», no como un hueco doble.
        return string.Join(" | ", columnas).Replace(" |  | ", " | | ");
    }

    private static string Limpio(string s) => s.Replace("|", "/").Replace('\n', ' ').Trim();

    /// <summary>
    /// El mensaje de un turno: qué pasó con la última acción, qué se ha hecho ya (resumido) y la
    /// página de ahora. El objetivo se repite al final: es lo último que lee antes de contestar.
    /// </summary>
    public static string Turno(string objetivo, string observacion, string resultadoAnterior, IReadOnlyList<string> hechos, int pasosRestantes)
    {
        var sb = new StringBuilder();
        if (hechos.Count != 0)
        {
            sb.Append("Pasos hechos hasta ahora:\n");
            for (var i = 0; i < hechos.Count; i++) sb.Append($"{i + 1}. {hechos[i]}\n");
        }
        else sb.Append("Todavía no se ha hecho ningún paso.\n");
        if (resultadoAnterior.Length != 0) sb.Append("Resultado de la última acción: ").Append(resultadoAnterior).Append('\n');
        sb.Append($"Pasos que quedan como máximo: {pasosRestantes}.\n\n");
        sb.Append("PÁGINA ACTUAL\n").Append(observacion);
        sb.Append("\nOBJETIVO: ").Append(objetivo).Append("\nResponde solo con el JSON de la siguiente acción.");
        return sb.ToString();
    }

    private static readonly Regex Think = new("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Fence = new("```(?:json)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Lee la decisión del texto que devuelve el modelo: tolera vallas <c>```json</c>, texto
    /// alrededor y el razonamiento <c>&lt;think&gt;</c> de algunos modelos. Lo que no es una
    /// decisión es <see cref="ArgumentException"/>, con el motivo para contárselo al modelo.
    /// </summary>
    public static Decision ParseDecision(string texto)
    {
        var limpio = Think.Replace(texto ?? "", "");
        var valla = Fence.Match(limpio);
        var cuerpo = valla.Success ? valla.Groups[1].Value : limpio;
        int inicio = cuerpo.IndexOf('{'), fin = cuerpo.LastIndexOf('}');
        if (inicio < 0 || fin <= inicio)
            throw new ArgumentException("la respuesta no es un objeto JSON");
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(cuerpo[inicio..(fin + 1)]) as JsonObject ?? throw new ArgumentException("la respuesta no es un objeto JSON");
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"JSON inválido: {e.Message}", e);
        }
        var accion = Observer.Str(obj, "accion").Trim().ToLowerInvariant();
        if (accion == "click") accion = "clic";
        if (accion is "senalar" or "señala" or "senala" or "apuntar" or "point" or "hover") accion = "señalar";
        if (!Acciones.Contains(accion))
            throw new ArgumentException($"acción desconocida «{accion}»: usa clic, señalar, escribir, elegir, tecla, ir, esperar, terminar o fallo");
        int? @ref = obj["ref"] is JsonNode n && n is not null && !(n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length == 0)
            ? Observer.Entero(n)
            : null;
        if (ConRef.Contains(accion) && (@ref is null || @ref <= 0))
            throw new ArgumentException($"la acción «{accion}» necesita un \"ref\" numérico de la lista");
        var valor = Observer.Str(obj, "valor");
        if (accion is "escribir" or "tecla" or "ir" && valor.Trim().Length == 0)
            throw new ArgumentException($"la acción «{accion}» necesita un \"valor\"");
        return new Decision(
            Observer.Str(obj, "pensamiento").Trim(),
            accion,
            @ref,
            valor,
            Observer.Str(obj, "frase").Trim(),
            Observer.Str(obj, "resumen").Trim());
    }
}
