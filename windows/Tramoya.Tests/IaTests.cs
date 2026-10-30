using System.Text.Json.Nodes;
using Tramoya.Agent;
using Tramoya.Core;
using Tramoya.Easy;
using Xunit;

namespace Tramoya.Tests;

public class IaTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    private static readonly Dictionary<string, string> SinEntorno = new();

    [Fact]
    public void WithNothingSetItIsALocalOllama()
    {
        var ia = Ia.Elegir(null, null, null, SinEntorno);
        Assert.Equal(new IaElegida("ollama", "qwen3-coder:30b", "http://localhost:11434", ""), ia);
    }

    [Fact]
    public void TheEnvironmentWeighsLessThanTheSettings()
    {
        var env = new Dictionary<string, string> { ["TRAMOYA_LLM"] = "ollama", ["TRAMOYA_LLM_MODEL"] = "env-model", ["OLLAMA_HOST"] = "0.0.0.0:9999" };
        Assert.Equal(new IaElegida("ollama", "env-model", "http://127.0.0.1:9999", ""), Ia.Elegir(null, null, null, env));
        var ajustes = Obj("""{"ia_backend":"ollama","ia_modelo":"qwen3:14b","ia_url":"http://gpu.lan:11434/"}""");
        Assert.Equal(new IaElegida("ollama", "qwen3:14b", "http://gpu.lan:11434", ""), Ia.Elegir(null, null, ajustes, env));
    }

    [Fact]
    public void AFlagWeighsMoreThanTheSettings()
    {
        var ajustes = Obj("""{"ia_backend":"ollama","ia_modelo":"qwen3:14b","ia_url":"http://gpu.lan:11434"}""");
        Assert.Equal("small", Ia.Elegir(null, "small", ajustes, SinEntorno).Model);
        // Another backend by flag does not inherit the saved backend's model or address.
        var claude = Ia.Elegir("claude", null, ajustes, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-env" });
        Assert.Equal(new IaElegida("claude", Ia.DefaultClaudeModel, "", "sk-env"), claude);
    }

    [Fact]
    public void TheSavedKeyWinsOverTheEnvironmentAndOpenAiNeedsItsAddress()
    {
        var env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-env", ["OPENAI_BASE_URL"] = "http://env/v1" };
        Assert.Equal("sk-saved", Ia.Elegir(null, null, Obj("""{"ia_backend":"claude","ia_clave":"sk-saved"}"""), env).Key);
        var openai = Ia.Elegir(null, null, Obj("""{"ia_backend":"openai","ia_url":"http://127.0.0.1:1234/v1/","ia_modelo":"local"}"""), env);
        Assert.Equal(new IaElegida("openai", "local", "http://127.0.0.1:1234/v1", ""), openai);
        Assert.Throws<ArgumentException>(() => Backends.Desde(Ia.Elegir(null, null, Obj("""{"ia_backend":"openai"}"""), SinEntorno)));
        Assert.IsType<OpenAiChatBackend>(Backends.Desde(openai));
        Assert.IsType<OpenAiBackend>(Assistant.PickBackend(null, env, ajustes: Obj("""{"ia_backend":"openai","ia_url":"http://x/v1"}""")));
    }

    [Fact]
    public void OffSwitchesTheAiOffUnlessAFlagAsks()
    {
        var off = Obj("""{"ia_backend":"off"}""");
        Assert.True(Ia.Apagada(off));
        Assert.Equal("off", Ia.Elegir(null, null, off, SinEntorno).Backend);
        Assert.Throws<ArgumentException>(() => Backends.Desde(Ia.Elegir(null, null, off, SinEntorno)));
        Assert.Throws<ArgumentException>(() => Assistant.PickBackend(null, SinEntorno, ajustes: off));
        Assert.Equal("ollama", Ia.Elegir("ollama", null, off, SinEntorno).Backend);
        Assert.Throws<ArgumentException>(() => Ia.Elegir("gemini", null, null, SinEntorno));
    }

    [Fact]
    public void TheKeyNeverGoesBackToTheWindowNorIntoAProject()
    {
        var a = Obj("""{"ia_backend":"claude","ia_clave":"sk-ant-abcdefgh1234","voz":"x"}""");
        var pub = Ia.Publicos(a);
        Assert.Equal("", pub["ia_clave"]!.GetValue<string>());
        Assert.True(pub["ia_clave_guardada"]!.GetValue<bool>());
        Assert.Equal("1234", pub["ia_clave_fin"]!.GetValue<string>());
        Assert.False(Ia.SinClave(a).ContainsKey("ia_clave"));
        Assert.DoesNotContain("sk-ant", Ia.Elegir(null, null, a, SinEntorno).ToString());
    }

    [Fact]
    public void SettingsRoundTripKeepsTheKeyWhenTheFieldIsEmpty()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "tramoya-ia-" + Guid.NewGuid().ToString("N"));
        try
        {
            var o = new ServerOptions { Datos = Path.Combine(raiz, "datos"), MisVideos = Path.Combine(raiz, "v"), Casa = raiz, Motor = ["x"] };
            var a = Proyectos.GuardarAjustes(o, Obj("""{"ia_backend":"openai","ia_url":"http://127.0.0.1:1234/v1","ia_modelo":"m","ia_clave":"sk-1234567890"}"""));
            Assert.Equal("sk-1234567890", a["ia_clave"]!.GetValue<string>());
            a = Proyectos.GuardarAjustes(o, Obj("""{"ia_modelo":"n","ia_clave":""}"""));
            Assert.Equal("sk-1234567890", Proyectos.LeerAjustes(o)["ia_clave"]!.GetValue<string>());
            Assert.Equal("n", Proyectos.LeerAjustes(o)["ia_modelo"]!.GetValue<string>());
            Proyectos.GuardarAjustes(o, Obj("""{"ia_clave_borrar":true}"""));
            Assert.Equal("", Proyectos.LeerAjustes(o)["ia_clave"]!.GetValue<string>());
            var ia = Ia.Elegir(null, null, Proyectos.LeerAjustes(o), SinEntorno);
            Assert.Equal(new IaElegida("openai", "n", "http://127.0.0.1:1234/v1", ""), ia);
        }
        finally
        {
            if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionSaysWhichModelAnswered()
    {
        string? url = null;
        PostJsonAsync post = (u, payload, headers, ct) =>
        {
            url = u;
            Assert.Equal("Bearer sk-x", headers["Authorization"]);
            return Task.FromResult<JsonNode>(Obj("""{"model":"served-model","choices":[{"message":{"content":" OK "}}]}"""));
        };
        var (modelo, respuesta, _) = await Backends.ProbarAsync(new IaElegida("openai", "asked", "http://srv/v1", "sk-x"), post);
        Assert.Equal("http://srv/v1/chat/completions", url);
        Assert.Equal("served-model", modelo);
        Assert.Equal("OK", respuesta);
    }
}
