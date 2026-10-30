using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Tramoya.Core;

namespace Tramoya.Agent;

/// <summary>Un mensaje de la conversación: <c>user</c> o <c>assistant</c>, con una captura PNG opcional.</summary>
public sealed record ChatMessage(string Role, string Text, byte[]? ImagePng = null);

/// <summary>Un modelo de lenguaje con el que se conversa por turnos; el agente no sabe cuál hay detrás.</summary>
public interface IChatBackend
{
    Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

/// <summary>La única llamada de red: POST de un JSON y respuesta JSON. Se inyecta en los tests.</summary>
public delegate Task<JsonNode> PostJsonAsync(string url, JsonNode payload, IDictionary<string, string> headers, CancellationToken ct);

/// <summary>
/// Un modelo servido por Ollama en este mismo PC, por su endpoint de chat con <c>format: json</c>
/// para que conteste un objeto JSON. Un modelo de 30B en una GPU de sobremesa tarda unos
/// segundos por turno; cargarlo puede llevar minutos, de ahí el tope de diez.
/// </summary>
public sealed class OllamaChatBackend : IChatBackend
{
    public string Model { get; }
    public string BaseUrl { get; }
    private readonly PostJsonAsync _post;

    public OllamaChatBackend(string model = Backends.DefaultOllamaModel, string baseUrl = Backends.DefaultOllamaUrl, PostJsonAsync? post = null)
    {
        Model = model;
        BaseUrl = baseUrl.TrimEnd('/');
        _post = post ?? Backends.HttpPostAsync;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var lista = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
            var mensaje = new JsonObject { ["role"] = m.Role, ["content"] = m.Text };
            if (m.ImagePng is not null) mensaje["images"] = new JsonArray(Convert.ToBase64String(m.ImagePng));
            lista.Add(mensaje);
        }
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = lista,
            ["stream"] = false,
            ["format"] = "json",
            ["options"] = new JsonObject { ["temperature"] = 0.1, ["num_ctx"] = 16384 },
            // Algunos modelos lo ignoran y razonan igual; el <think> se quita al leer la respuesta.
            ["think"] = false,
            // Que no se descargue entre un turno y el siguiente.
            ["keep_alive"] = "30m",
        };
        var reply = await _post($"{BaseUrl}/api/chat", payload, new Dictionary<string, string>(), ct).ConfigureAwait(false);
        if (reply is JsonObject o && o["error"] is JsonNode error)
            throw new InvalidOperationException($"Ollama: {error}");
        return reply?["message"]?["content"]?.GetValue<string>() ?? throw new InvalidOperationException("Ollama no ha devuelto message.content");
    }
}

/// <summary>La Messages API de Claude, con la captura de pantalla como imagen cuando la hay.</summary>
public sealed class ClaudeChatBackend : IChatBackend
{
    public string Model { get; }
    public string ApiKey { get; }
    private readonly PostJsonAsync _post;

    public ClaudeChatBackend(string apiKey, string model = Backends.DefaultClaudeModel, PostJsonAsync? post = null)
    {
        ApiKey = apiKey;
        Model = model;
        _post = post ?? Backends.HttpPostAsync;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var lista = new JsonArray();
        foreach (var m in messages)
        {
            if (m.ImagePng is null)
            {
                lista.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
                continue;
            }
            lista.Add(new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(m.ImagePng) },
                    },
                    new JsonObject { ["type"] = "text", ["text"] = m.Text },
                },
            });
        }
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 1024,
            ["system"] = system,
            ["messages"] = lista,
        };
        var reply = await _post(Assistant.ClaudeUrl, payload,
            new Dictionary<string, string> { ["x-api-key"] = ApiKey, ["anthropic-version"] = "2023-06-01" }, ct).ConfigureAwait(false);
        if (reply is JsonObject o && o["error"] is JsonObject error)
            throw new InvalidOperationException($"Claude: {error["message"] ?? error}");
        var sb = new StringBuilder();
        if (reply?["content"] is JsonArray bloques)
            foreach (var bloque in bloques)
                if (bloque is JsonObject b && b["text"] is JsonNode texto)
                    sb.Append(texto.GetValue<string>());
        return sb.ToString();
    }
}

/// <summary>
/// Any server with the OpenAI chat-completions API: LM Studio, llama.cpp, vLLM, LiteLLM, OpenAI itself.
/// <c>BaseUrl</c> is the part before <c>/chat/completions</c> (usually ends in <c>/v1</c>).
/// </summary>
public sealed class OpenAiChatBackend : IChatBackend
{
    public string Model { get; }
    public string BaseUrl { get; }
    public string ApiKey { get; }
    private readonly PostJsonAsync _post;

    public OpenAiChatBackend(string baseUrl, string model, string apiKey = "", PostJsonAsync? post = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Model = model;
        ApiKey = apiKey;
        _post = post ?? Backends.HttpPostAsync;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var lista = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
            if (m.ImagePng is null)
            {
                lista.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
                continue;
            }
            lista.Add(new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = m.Text },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(m.ImagePng) } },
                },
            });
        }
        var payload = new JsonObject { ["model"] = Model, ["messages"] = lista, ["temperature"] = 0.1 };
        var cabeceras = new Dictionary<string, string>();
        if (ApiKey.Length != 0) cabeceras["Authorization"] = "Bearer " + ApiKey;
        var reply = await _post($"{BaseUrl}/chat/completions", payload, cabeceras, ct).ConfigureAwait(false);
        if (reply is JsonObject o && o["error"] is JsonNode error)
            throw new InvalidOperationException($"OpenAI-compatible server: {error["message"] ?? error}");
        return reply?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException("the server returned no choices[0].message.content");
    }
}

public static class Backends
{
    public const string DefaultOllamaModel = "qwen3-coder:30b";
    public const string DefaultOllamaUrl = Assistant.DefaultOllamaUrl;
    public const string DefaultClaudeModel = Assistant.DefaultClaudeModel;

    /// <summary>
    /// <c>OLLAMA_HOST</c> es la variable del propio servidor, así que suele venir sin esquema
    /// («127.0.0.1:11434») o como dirección de escucha («0.0.0.0:11434»), que no vale para
    /// conectarse: se convierte en la url local que sí vale.
    /// </summary>
    public static string UrlOllama(string? host) => Ia.UrlOllama(host);

    // Ten minutes: a 30B model loaded from a slow disk, or evicted by another program using the same
    // GPU, takes two or three minutes before its first word; the agent waits rather than giving up.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>POST de un JSON y respuesta JSON; un error HTTP lleva el cuerpo en el mensaje (Ollama explica ahí qué falta).</summary>
    public static async Task<JsonNode> HttpPostAsync(string url, JsonNode payload, IDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(Json.Compact), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in headers)
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content.Headers.TryAddWithoutValidation(name, value);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var cuerpo = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{url} ha contestado {(int)response.StatusCode}: {Observer.Recortar(cuerpo, 300)}");
        return JsonNode.Parse(cuerpo) ?? throw new InvalidOperationException("respuesta JSON vacía");
    }

    /// <summary>
    /// Elige el modelo como <c>Assistant.PickBackend</c>: el nombre explícito, si no <c>$TRAMOYA_LLM</c>,
    /// si no Claude cuando hay <c>$ANTHROPIC_API_KEY</c> y, si no, Ollama en local. El modelo:
    /// <paramref name="model"/>, si no <c>$TRAMOYA_LLM_MODEL</c>, si no el de cada casa.
    /// </summary>
    public static IChatBackend Pick(string? name, string? model, IDictionary<string, string>? env = null, JsonObject? ajustes = null) =>
        Desde(Ia.Elegir(name, model, ajustes, env));

    /// <summary>The chat backend of a decided choice (<see cref="Ia.Elegir"/>).</summary>
    public static IChatBackend Desde(IaElegida ia) => ia.Backend switch
    {
        "off" => throw new ArgumentException(Lengua.T("la IA está apagada en Ajustes", "the AI is switched off in Settings")),
        "claude" when ia.Key.Length == 0 => throw new ArgumentException(Lengua.T(
            "Claude necesita una clave de API: ponla en Ajustes → IA o en ANTHROPIC_API_KEY",
            "Claude needs an API key: set it in Settings → AI or in ANTHROPIC_API_KEY")),
        "claude" => new ClaudeChatBackend(ia.Key, ia.Model),
        "openai" when ia.Url.Length == 0 => throw new ArgumentException(Lengua.T(
            "falta la dirección del servidor compatible con OpenAI (Ajustes → IA u OPENAI_BASE_URL)",
            "the OpenAI-compatible server needs its address (Settings → AI or OPENAI_BASE_URL)")),
        "openai" => new OpenAiChatBackend(ia.Url, ia.Model, ia.Key),
        _ => new OllamaChatBackend(ia.Model, ia.Url),
    };

    private static readonly HttpClient Sonda = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>The models a server offers, for the window's dropdown: Ollama's /api/tags, an OpenAI server's /models, Claude's /v1/models.</summary>
    public static async Task<List<string>> ModelosAsync(IaElegida ia, CancellationToken ct = default)
    {
        using var peticion = ia.Backend switch
        {
            "ollama" => new HttpRequestMessage(HttpMethod.Get, $"{ia.Url}/api/tags"),
            "openai" => new HttpRequestMessage(HttpMethod.Get, $"{ia.Url}/models"),
            "claude" => new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100"),
            _ => throw new ArgumentException("no backend"),
        };
        if (ia.Backend == "claude")
        {
            peticion.Headers.TryAddWithoutValidation("x-api-key", ia.Key);
            peticion.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (ia.Backend == "openai" && ia.Key.Length != 0)
            peticion.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ia.Key);
        using var r = await Sonda.SendAsync(peticion, ct).ConfigureAwait(false);
        var cuerpo = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)r.StatusCode}: {Observer.Recortar(cuerpo, 200)}");
        var json = JsonNode.Parse(cuerpo);
        var lista = ia.Backend == "ollama" ? json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>())
                                           : json?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>());
        return (lista ?? []).OfType<string>().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>One short question to the chosen model: who answered and how long it took. For «Test connection».</summary>
    public static async Task<(string Modelo, string Respuesta, long Ms)> ProbarAsync(IaElegida ia, PostJsonAsync? post = null, CancellationToken ct = default)
    {
        post ??= HttpPostAsync;
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        const string pregunta = "Reply with the single word OK.";
        JsonNode? reply;
        string texto;
        switch (ia.Backend)
        {
            case "claude":
                if (ia.Key.Length == 0) throw new ArgumentException(Lengua.T("falta la clave de API", "the API key is missing"));
                reply = await post(Assistant.ClaudeUrl, new JsonObject
                {
                    ["model"] = ia.Model, ["max_tokens"] = 16,
                    ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = pregunta } },
                }, new Dictionary<string, string> { ["x-api-key"] = ia.Key, ["anthropic-version"] = "2023-06-01" }, ct).ConfigureAwait(false);
                texto = reply?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
                break;
            case "openai":
                var cab = new Dictionary<string, string>();
                if (ia.Key.Length != 0) cab["Authorization"] = "Bearer " + ia.Key;
                reply = await post($"{ia.Url}/chat/completions", new JsonObject
                {
                    ["model"] = ia.Model, ["max_tokens"] = 16,
                    ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = pregunta } },
                }, cab, ct).ConfigureAwait(false);
                texto = reply?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
                break;
            case "ollama":
                reply = await post($"{ia.Url}/api/chat", new JsonObject
                {
                    ["model"] = ia.Model, ["stream"] = false, ["think"] = false,
                    ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = pregunta } },
                }, new Dictionary<string, string>(), ct).ConfigureAwait(false);
                texto = reply?["message"]?["content"]?.GetValue<string>() ?? "";
                break;
            default:
                throw new ArgumentException(Lengua.T("la IA está apagada", "the AI is switched off"));
        }
        var modelo = reply?["model"]?.GetValue<string>() ?? ia.Model;
        return (modelo, Observer.Recortar(texto.Trim(), 80), reloj.ElapsedMilliseconds);
    }
}
