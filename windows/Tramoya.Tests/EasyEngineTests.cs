using System.Text.Json.Nodes;
using Tramoya.Core;
using Tramoya.Easy;

namespace Tramoya.Tests;

// La parte del motor de «tramoya easy» que no necesita navegador, voz ni ffmpeg: los
// dominios del inicio de sesión de Windows, los eventos grabados convertidos en pasos, la
// explicación de los fallos y el despacho de `Main`. El resto de test_easy.py está en
// EasyPasosTests.cs.
public class EasyEngineTests
{
    private static JsonObject Obj(string s) => Json.Parse(s)!.AsObject();

    private static readonly object Consola = new();

    /// <summary>Corre <see cref="Engine.Main"/> con la salida estándar capturada; devuelve (código, líneas).</summary>
    private static (int Codigo, List<JsonObject> Lineas) Correr(params string[] args)
    {
        lock (Consola)
        {
            var antes = Console.Out;
            var captura = new StringWriter();
            Console.SetOut(captura);
            try
            {
                var codigo = Engine.Main(args);
                var lineas = captura.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.TrimStart().StartsWith('{')).Select(l => Json.Parse(l)!.AsObject()).ToList();
                return (codigo, lineas);
            }
            finally
            {
                Console.SetOut(antes);
            }
        }
    }

    [Fact]
    public void RecordedEventsNameWhatWasTouched()
    {
        var clic = Engine.PasoDeEvento(Obj("""{"tipo": "clic", "selector": "internal:role=button[name=\"IMPUTAR\"i]", "nombre": "IMPUTAR", "que": "el botón"}"""), "")!;
        Assert.Equal("Pulsa el botón «IMPUTAR»", Pasos.Str(clic, "desc"));
        Assert.True(Pasos.Bool(clic, "guarda"));
        var marco = Engine.PasoDeEvento(Obj("""{"tipo": "clic", "selector": "#x", "nombre": "", "que": "la casilla", "marca": "on"}"""), "iframe >> internal:control=enter-frame >> ")!;
        Assert.Equal("Marca la casilla", Pasos.Str(marco, "desc"));
        Assert.Equal("iframe >> internal:control=enter-frame >> #x", Pasos.Str(marco, "selector"));
        var cookies = Engine.PasoDeEvento(Obj("""{"tipo": "clic", "selector": "#ok", "nombre": "Aceptar todas", "que": "el botón"}"""), "")!;
        Assert.True(Pasos.Bool(cookies, "opcional"));
        Assert.Contains("cookies", Pasos.Str(cookies, "desc"));
        Assert.Null(Engine.PasoDeEvento(Obj("""{"tipo": "clic", "selector": "", "nombre": "x"}"""), ""));
    }

    [Fact]
    public void RecordedKeysFieldsAndChoicesAreDescribed()
    {
        var tecla = Engine.PasoDeEvento(Obj("""{"tipo": "tecla", "valor": "Enter"}"""), "")!;
        Assert.Equal("Pulsa la tecla Intro", Pasos.Str(tecla, "desc"));
        var campo = Engine.PasoDeEvento(Obj("""{"tipo": "escribir", "selector": "#t", "valor": "Regar", "nombre": "Título"}"""), "")!;
        Assert.Equal("Escribe «Regar» en el campo «Título»", Pasos.Str(campo, "desc"));
        var elegir = Engine.PasoDeEvento(Obj("""{"tipo": "elegir", "selector": "#s", "valor": "2", "texto_opcion": "Febrero", "nombre": "Mes"}"""), "")!;
        Assert.Equal("Elige «Febrero» en «Mes»", Pasos.Str(elegir, "desc"));
        Assert.Equal("2", Pasos.Str(elegir, "valor"));
        Assert.Null(Engine.PasoDeEvento(Obj("""{"tipo": "otro", "selector": "#s"}"""), ""));
    }

    [Fact]
    public void WindowsSignInIsAllowedForTheSiteItsDomainAndTheExtraOnes()
    {
        Assert.Equal(["apps.example.org", "*.example.org", "*.example.net", .. Vocabulario.SsoSiempre],
            Engine.DominiosDe("https://apps.example.org/x", ["*.example.net"]));
        Assert.Equal(["127.0.0.1", .. Vocabulario.SsoSiempre], Engine.DominiosDe("http://127.0.0.1:8770/"));
        var args = Engine.ArgumentosNavegador("https://intranet.example.org/app");
        var allow = args.First(a => a.StartsWith("--auth-server-allowlist=", StringComparison.Ordinal));
        Assert.Equal(string.Join(",", Engine.DominiosDe("https://intranet.example.org/")), allow.Split('=', 2)[1]);
        Assert.Contains("--lang=es-ES", args);
    }

    [Fact]
    public void ExtraDomainsAreTrimmedAndNotRepeated()
    {
        Assert.Equal(["app.example.org", "*.example.org", .. Vocabulario.SsoSiempre],
            Engine.DominiosDe("https://app.example.org/", [" *.example.org ", "", "app.example.org"]));
        Assert.Equal(Vocabulario.SsoSiempre.ToList(), Engine.DominiosDe("no es una url"));
    }

    [Fact]
    public void ADebuggingPortIsAddedOnlyWhenAsked()
    {
        var antes = Environment.GetEnvironmentVariable("TRAMOYA_CDP");
        try
        {
            Environment.SetEnvironmentVariable("TRAMOYA_CDP", null);
            Assert.DoesNotContain(Engine.ArgumentosNavegador("https://example.org/"), a => a.StartsWith("--remote-debugging-port=", StringComparison.Ordinal));
            Environment.SetEnvironmentVariable("TRAMOYA_CDP", "9222");
            Assert.Equal("--remote-debugging-port=9222", Engine.ArgumentosNavegador("https://example.org/").Last());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRAMOYA_CDP", antes);
        }
    }

    [Fact]
    public void FailuresAreExplainedForPeople()
    {
        Assert.Equal("no lo he encontrado en la página (puede que la web haya cambiado o tarde más)",
            Engine.ExplicarFallo(new TimeoutException("Timeout 15000ms exceeded.\nCall log:\n  - waiting for locator")));
        Assert.Equal("no lo he encontrado en la página (puede que la web haya cambiado o tarde más)",
            Engine.ExplicarFallo(new InvalidOperationException("element is not visible")));
        Assert.Equal("hay varios iguales en la página y no sé cuál es",
            Engine.ExplicarFallo(new InvalidOperationException("strict mode violation: locator resolved to 2 elements")));
        Assert.Equal("primera línea", Engine.ExplicarFallo(new InvalidOperationException("primera línea\nsegunda línea")));
        Assert.Equal(200, Engine.ExplicarFallo(new InvalidOperationException(new string('x', 300))).Length);
        var fallo = new Engine.FalloPaso(Obj("""{"id": 3, "desc": "Pulsa «Guardar»"}"""), new TimeoutException("Timeout"));
        Assert.Equal(3, fallo.Paso["id"]!.GetValue<int>());
        Assert.Equal("Timeout", fallo.Message);
    }

    [Fact]
    public void MainWithoutArgumentsPrintsTheUsage()
    {
        var (codigo, lineas) = Correr();
        Assert.Equal(2, codigo);
        Assert.Empty(lineas);
    }

    [Fact]
    public void MainPasosEmitsTheStepsOfACodegenFile()
    {
        var fichero = Path.Combine(Path.GetTempPath(), $"tramoya-codegen-{Guid.NewGuid():N}.py");
        File.WriteAllText(fichero, """
            from playwright.sync_api import Playwright, sync_playwright


            def run(playwright: Playwright) -> None:
                browser = playwright.chromium.launch(headless=False)
                context = browser.new_context()
                page = context.new_page()
                page.goto("https://apps.example.net/home.xhtml")
                page.get_by_role("button", name="Añadir Código Proyecto").click()
                page.get_by_placeholder("Contraseña").fill("secreto")
                page.keyboard.press("Enter")
            """);
        try
        {
            var (codigo, lineas) = Correr("pasos", fichero);
            Assert.Equal(0, codigo);
            var salida = Assert.Single(lineas);
            var pasos = salida["pasos"]!.AsArray();
            Assert.Equal(["Pulsa el botón «Añadir Código Proyecto»", "Pulsa la tecla Intro"],
                pasos.Select(p => Pasos.Str(p!.AsObject(), "desc")).ToList());
            Assert.Equal([1, 2], pasos.Select(p => p!["id"]!.GetValue<int>()).ToList());
            Assert.Contains(salida["avisos"]!.AsArray(), a => a!.ToString().Contains("contraseña"));
        }
        finally
        {
            File.Delete(fichero);
        }
    }

    [Fact]
    public void MainVocesEmitsAListEvenWithoutVoices()
    {
        var (codigo, lineas) = Correr("voces");
        Assert.Equal(0, codigo);
        var salida = Assert.Single(lineas);
        var voces = Assert.IsType<JsonArray>(salida["voces"]);
        Assert.All(voces, v => Assert.IsType<string>(v!.GetValue<string>()));
    }

    [Fact]
    public void MainVideoWithoutAProjectReportsTheFailureInOneLine()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), $"tramoya-sin-proyecto-{Guid.NewGuid():N}");
        var (codigo, lineas) = Correr("video", carpeta);
        Assert.Equal(1, codigo);
        var salida = Assert.Single(lineas);
        Assert.StartsWith("Algo ha fallado: ", Pasos.Str(salida, "error"));
        Assert.False(salida.ContainsKey("paso"));
    }

    [Fact]
    public void MainAgenteWithoutAFolderReportsTheFailureInOneLine()
    {
        // Antes de abrir ningún navegador ni preguntar a ningún modelo: sin carpeta no hay dónde
        // dejar el resultado, y se dice en la misma línea de error que lee el servidor.
        var carpeta = Path.Combine(Path.GetTempPath(), $"tramoya-sin-carpeta-{Guid.NewGuid():N}");
        var perfil = Path.Combine(carpeta, "_navegador");
        var (codigo, lineas) = Correr("agente", carpeta, "https://a.b/", perfil, "{}", "Entra en Pedidos");
        Assert.Equal(1, codigo);
        var salida = Assert.Single(lineas);
        Assert.StartsWith("La IA no ha podido hacerlo: ", Pasos.Str(salida, "error"));
        Assert.Contains("no existe la carpeta", Pasos.Str(salida, "error"));
        Assert.EndsWith(".", Pasos.Str(salida, "error"));
        Assert.False(Directory.Exists(carpeta));
    }

    [Fact]
    public void MainAgenteWithMissingArgumentsReportsTheFailureInOneLine()
    {
        var (codigo, lineas) = Correr("agente", Path.GetTempPath(), "https://a.b/");
        Assert.Equal(1, codigo);
        var salida = Assert.Single(lineas);
        Assert.StartsWith("La IA no ha podido hacerlo: faltan datos", Pasos.Str(salida, "error"));
    }

    [Fact]
    public void EmitirWritesOneCompactUnescapedLine()
    {
        lock (Consola)
        {
            var antes = Console.Out;
            var captura = new StringWriter();
            Console.SetOut(captura);
            try
            {
                Engine.Emitir(("paso", "Pulsa «Añadir»"), ("n", 2), ("inicio", null));
            }
            finally
            {
                Console.SetOut(antes);
            }
            Assert.Equal("{\"paso\":\"Pulsa «Añadir»\",\"n\":2,\"inicio\":null}\n", captura.ToString());
        }
    }

    [Fact]
    public void TheListenerScriptIsEmbedded()
    {
        var js = Engine.EscuchaJs();
        Assert.False(string.IsNullOrWhiteSpace(js));
        Assert.Contains("__tramoyaPaso", js);
    }
}
