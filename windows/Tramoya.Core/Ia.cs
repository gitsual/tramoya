using System.Text.Json.Nodes;

namespace Tramoya.Core;

/// <summary>The model that answers, once flags, the window's settings and the environment have been weighed.</summary>
/// <param name="Backend">«ollama», «claude», «openai» (any server with the OpenAI chat API) or «off».</param>
/// <param name="Model">The model name; never empty.</param>
/// <param name="Url">Ollama's host or the OpenAI-compatible base URL (…/v1); empty for Claude.</param>
/// <param name="Key">The API key for Claude, or for an OpenAI-compatible server that needs one; empty otherwise.</param>
public sealed record IaElegida(string Backend, string Model, string Url, string Key)
{
    /// <summary>The key is never printed: only whether there is one.</summary>
    public override string ToString() => $"{Backend} · {Model}{(Url.Length != 0 ? " · " + Url : "")}{(Key.Length != 0 ? " · key set" : "")}";
}

/// <summary>
/// One place that decides which model the agent and the assistant use. The order is the same for
/// every setting: a command-line flag, then the window's settings (<c>ia_backend</c>, <c>ia_modelo</c>,
/// <c>ia_url</c>, <c>ia_clave</c> in <c>ajustes.json</c>), then the environment (<c>TRAMOYA_LLM</c>,
/// <c>TRAMOYA_LLM_MODEL</c>, <c>OLLAMA_HOST</c>, <c>OPENAI_BASE_URL</c>, <c>ANTHROPIC_API_KEY</c>,
/// <c>OPENAI_API_KEY</c>), then the defaults (a local Ollama with <c>qwen3-coder:30b</c>).
/// </summary>
public static class Ia
{
    public static readonly IReadOnlyList<string> Backends = ["ollama", "claude", "openai"];
    public const string DefaultOllamaModel = "qwen3-coder:30b";
    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultClaudeModel = "claude-sonnet-5-5";
    public const string DefaultOpenAiModel = "gpt-4o-mini";

    /// <summary>The settings keys, as the window stores them.</summary>
    public const string KBackend = "ia_backend", KModelo = "ia_modelo", KUrl = "ia_url", KClave = "ia_clave";

    public static string Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    public static IDictionary<string, string> Entorno() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.Ordinal);

    /// <summary>Whether the settings switch the AI off (the window then hides the «ask the AI» card).</summary>
    public static bool Apagada(JsonObject? ajustes) => Str(ajustes, KBackend) == "off";

    /// <summary>
    /// Weigh flag, settings, environment and defaults. A flag naming a backend other than the saved
    /// one does not inherit the saved model, URL or key, which belong to the saved backend.
    /// </summary>
    public static IaElegida Elegir(string? flagBackend, string? flagModel, JsonObject? ajustes, IDictionary<string, string>? env = null)
    {
        env ??= Entorno();
        string? Env(string k) => env.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var guardado = Str(ajustes, KBackend);
        if (guardado is "auto") guardado = "";
        var backend = First(flagBackend, guardado, Env("TRAMOYA_LLM"))
                      ?? (Env("ANTHROPIC_API_KEY") is not null ? "claude" : "ollama");
        backend = backend.ToLowerInvariant();
        if (backend == "off") return new IaElegida("off", "", "", "");
        if (!Backends.Contains(backend))
            throw new ArgumentException($"unknown AI backend «{backend}»: use ollama, claude or openai");
        // The saved model, URL and key go with the saved backend.
        var mismo = guardado.Length == 0 || guardado == backend;
        string? Guardado(string k) => mismo ? First(Str(ajustes, k)) : null;
        var model = First(flagModel, Guardado(KModelo), Env("TRAMOYA_LLM_MODEL"));
        return backend switch
        {
            "claude" => new IaElegida("claude", model ?? DefaultClaudeModel, "",
                First(Guardado(KClave), Env("ANTHROPIC_API_KEY")) ?? ""),
            "openai" => new IaElegida("openai", model ?? DefaultOpenAiModel,
                (First(Guardado(KUrl), Env("OPENAI_BASE_URL")) ?? "").TrimEnd('/'),
                First(Guardado(KClave), Env("OPENAI_API_KEY")) ?? ""),
            _ => new IaElegida("ollama", model ?? DefaultOllamaModel,
                UrlOllama(First(Guardado(KUrl), Env("OLLAMA_HOST"))), ""),
        };
    }

    /// <summary>
    /// <c>OLLAMA_HOST</c> is the server's own variable, so it often comes without a scheme
    /// («127.0.0.1:11434») or as a listening address («0.0.0.0:11434»): it becomes a URL that connects.
    /// </summary>
    public static string UrlOllama(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return DefaultOllamaUrl;
        var url = host.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        url = url.Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
                 .Replace("://[::]", "://localhost", StringComparison.Ordinal)
                 .Replace("://::", "://localhost", StringComparison.Ordinal);
        return url.TrimEnd('/');
    }

    /// <summary>The settings as the window may see them: the key replaced by whether there is one and its last characters.</summary>
    public static JsonObject Publicos(JsonObject ajustes)
    {
        var copia = ajustes.DeepClone().AsObject();
        var clave = Str(ajustes, KClave);
        copia[KClave] = "";
        copia["ia_clave_guardada"] = clave.Length != 0;
        copia["ia_clave_fin"] = clave.Length >= 8 ? clave[^4..] : "";
        return copia;
    }

    /// <summary>The settings without the key, for anything that is written to a project or passed on a command line.</summary>
    public static JsonObject SinClave(JsonObject ajustes)
    {
        var copia = ajustes.DeepClone().AsObject();
        copia.Remove(KClave);
        return copia;
    }
}
