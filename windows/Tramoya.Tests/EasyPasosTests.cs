using System.Text.Json.Nodes;
using Tramoya.Core;
using Tramoya.Easy;
using static Tramoya.Easy.Pasos;

namespace Tramoya.Tests;

public class EasyPasosTests
{
    [Fact]
    public void SenalarEsUnPointQueNoPulsa()
    {
        Assert.Equal(new[] { "point #guardar", "beat 1" }, LineasDePaso(new JsonObject { ["tipo"] = "señalar", ["selector"] = "#guardar" }));
    }

    // Engine.cs porta _paso_de_evento, dominios_de y argumentos_navegador:
    // test_recorded_events_name_what_was_touched y
    // test_windows_sign_in_is_allowed_for_the_site_its_domain_and_the_extra_ones.
    private static JsonObject Obj(string s) => Json.Parse(s)!.AsObject();
    private static List<JsonObject> Lista(string s) => Json.Parse(s)!.AsArray().Cast<JsonObject>().ToList();
    private const string Codegen = """
        from playwright.sync_api import Playwright, sync_playwright


        def run(playwright: Playwright) -> None:
            browser = playwright.chromium.launch(headless=False)
            context = browser.new_context()
            page = context.new_page()
            page.goto("https://apps.example.net/Horas/home.xhtml")
            page.get_by_role("button", name="Añadir Código Proyecto").click()
            page.locator(".rowNotSaved > .tdCod > input").click()
            page.locator(".rowNotSaved > .tdCod > input").fill("EV4I02")
            page.locator(".rowNotSaved > .tdCod > input").press("Tab")
            page.get_by_label("Lunes").fill("9")
            page.get_by_role("row", name="EV4I02 2003").get_by_role("checkbox").check()
            page.get_by_text("Copiar filas a", exact=True).click()
            page.keyboard.press("Enter")
            page.get_by_placeholder("Contraseña").fill("secreto")
            page.get_by_role("button", name="IMPUTAR").click()
            with page.expect_popup() as page1_info:
                page.get_by_role("link", name="Ayuda").click()
            page1 = page1_info.value
            page1.get_by_role("button", name="X").click()
        """;
    [Fact]
    public void NonliteralArgumentsPreserveChainsAndPythonWarnings()
    {
        var (pasos, avisos) = CodegenSteps.PasosDeGrabacion("""
            page.get_by_role("button", name=re.compile("Save")).click(timeout=5000)
            page.get_by_role("button", name=re.compile("A,(B)")).nth(1).click()
            page.get_by_label("Name").fill(values[choose("a,b", [1, 2])])
            page.locator(selectors[0]).click()
            page.get_by_text("Choice").select_option({"label": "A,B"})
            with page.expect_popup() as popup_info:
                page.get_by_text("Help").click()
            page1.get_by_role("button", name=re.compile("Close")).click()
            """);
        // Captured from pasos_de_grabacion under Python 3.12: the with header
        // is not an expression statement, but its body still contributes steps.
        var expected = Lista("""
            [{"tipo":"clic","selector":"internal:role=button","desc":"Pulsa el botón","guarda":false,"id":1,"texto":"","seguro":false},
             {"tipo":"clic","selector":"internal:role=button >> nth=1","desc":"Pulsa el botón","guarda":false,"id":2,"texto":"","seguro":false},
             {"tipo":"clic","selector":"internal:text=\"Help\"i","desc":"Pulsa «Help»","guarda":false,"id":3,"texto":"","seguro":false}]
            """);
        Assert.Equal(expected.Count, pasos.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(JsonNode.DeepEquals(expected[i], pasos[i]));
        Assert.Equal(new[] {
            "Se abrió otra ventana durante la grabación; lo que hiciste en ella no se puede repetir.",
            "Un paso no se ha podido entender y se ha quitado (click).",
            "Un paso no se ha podido entender y se ha quitado (fill).",
            "Un paso no se ha podido entender y se ha quitado (select_option)."
        }, avisos);
    }

    [Fact]
    public void CodegenBecomesReadableStepsInSourceOrder()
    {
        var (pasos, avisos) = CodegenSteps.PasosDeGrabacion(Codegen);
        Assert.Equal(new[] {
            "Pulsa el botón «Añadir Código Proyecto»", "Escribe «EV4I02»", "Pulsa la tecla Tabulador",
            "Escribe «9» en el campo «Lunes»", "Marca la casilla de la fila «EV4I02 2003»",
            "Pulsa «Copiar filas a»", "Pulsa la tecla Intro", "Pulsa el botón «IMPUTAR»", "Pulsa el enlace «Ayuda»"
        }, pasos.Select(p => Str(p, "desc")));
        Assert.Equal("internal:role=button[name=\"Añadir Código Proyecto\"i]", Str(pasos[0], "selector"));
        Assert.Equal("internal:role=row[name=\"EV4I02 2003\"i] >> internal:role=checkbox", Str(pasos[4], "selector"));
        Assert.Equal("internal:text=\"Copiar filas a\"s", Str(pasos[5], "selector"));
        Assert.Equal(Enumerable.Range(1, 9), pasos.Select(p => p["id"]!.GetValue<int>()));
        Assert.Contains(avisos, a => a.Contains("contraseña"));
        Assert.Contains(avisos, a => a.Contains("otra ventana"));
    }

    [Fact]
    public void OnlyTheLastSavingButtonIsPointedAtNotPressed()
    {
        var pasos = Limpiar(Lista("""
            [{"tipo":"clic","selector":"#a","desc":"Pulsa «Guardar»","guarda":true},
             {"tipo":"escribir","selector":"#b","valor":"1","desc":"Escribe «1»"},
             {"tipo":"clic","selector":"#c","desc":"Pulsa «Imputar»","guarda":true},
             {"tipo":"tecla","valor":"Enter","desc":"Pulsa la tecla Intro"}]
            """));
        Assert.Equal(new bool?[] { false, null, true, null }, pasos.Select(p => p["seguro"]?.GetValue<bool>()));
    }

    [Fact]
    public void AClickIntoAFieldThatIsThenTypedIntoIsOneStep()
    {
        var pasos = Limpiar(Lista("""
            [{"tipo":"clic","selector":"#t","desc":"Pulsa el campo «Title»"},
             {"tipo":"escribir","selector":"#t","valor":"Reg","desc":"Escribe «Reg»"},
             {"tipo":"escribir","selector":"#t","valor":"Regar","desc":"Escribe «Regar»"},
             {"tipo":"clic","selector":"#t","desc":"Pulsa el campo «Title»"}]
            """));
        Assert.Equal(new[] { "Escribe «Regar»" }, pasos.Select(p => Str(p, "desc")));
        Assert.Equal(new[] { "desc", "id", "selector", "texto", "tipo", "valor" }, pasos[0].Select(kv => kv.Key).Order());
    }

    [Fact]
    public void ASentenceOpensASceneAndUntoldStepsRideAlong()
    {
        var proyecto = Obj("""
            {"nombre":"Copiar filas","pasos":[
              {"id":1,"tipo":"clic","selector":"#a","texto":""},
              {"id":2,"tipo":"clic","selector":"#b","texto":"Pulsa aquí."},
              {"id":3,"tipo":"escribir","selector":"#c","valor":"9","texto":""},
              {"id":4,"tipo":"clic","selector":"#d","texto":"Y guarda.","quitado":true}]}
            """);
        var lista = Escenas(proyecto);
        Assert.Equal(new[] { "inicio", "paso-2" }, lista.Select(e => e.Name));
        Assert.Equal(new[] { "Copiar filas", "Pulsa aquí." }, lista.Select(e => e.Texto));
        Assert.Equal(new[] { 1 }, lista[0].Items.Select(p => p["id"]!.GetValue<int>()));
        Assert.Equal(new[] { 2, 3 }, lista[1].Items.Select(p => p["id"]!.GetValue<int>()));
        Assert.Same(proyecto["pasos"]![1], lista[1].Items[0]);
    }

    [Fact]
    public void EveryStepLineParses()
    {
        var pasos = Lista("""
            [{"tipo":"clic","selector":"#a"}, {"tipo":"clic","selector":"#a","seguro":true},
             {"tipo":"escribir","selector":"#a","valor":"a | b"}, {"tipo":"tecla","valor":"Tab"},
             {"tipo":"elegir","selector":"#s","valor":"home"}, {"tipo":"ir","valor":"https://example.net/"}]
            """);
        string[][] esperadas = [ ["click #a", "beat 0.7"], ["point #a", "beat 0.8"],
            ["clear #a", "type #a | a | b", "beat 0.3"], ["press Tab", "beat 0.5"],
            ["select #s | home", "beat 0.6"], ["goto https://example.net/", "beat 1"] ];
        // Steps.Parse pertenece a otro port aún ausente; se comparan todas las líneas
        // literalmente y se conserva el separador interno del valor al dividir una vez.
        for (var i = 0; i < pasos.Count; i++) Assert.Equal(esperadas[i], LineasDePaso(pasos[i]));
        Assert.Equal(new[] { "#a", "a | b" }, LineasDePaso(pasos[2])[1][5..].Split(" | ", 2));
    }

    [Fact]
    public void ReadingTime()
    {
        Assert.Equal(2.5, SegundosLectura("Hola"));
        Assert.Equal(11.2, SegundosLectura(string.Join(" ", Enumerable.Repeat("palabra", 26))));
        // Valores contrastados con Python 3.12, incluidos vacío y espacios múltiples.
        foreach (var (n, esperado) in new[] { (0, 2.5), (4, 2.7), (13, 6.2), (39, 16.2), (52, 21.2) })
            Assert.Equal(esperado, SegundosLectura(string.Join("\t \n", Enumerable.Repeat("palabra", n))));
    }

    [Fact]
    public void SubtitlesFollowTheAssembledSceneLengths()
    {
        Assert.Equal("1\n00:00:00,000 --> 00:00:02,500\nHola.\n\n2\n00:00:03,500 --> 00:00:06,750\nAdiós.\n",
            Srt([("Hola.", 2.5), ("", 1.0), ("Adiós.", 3.25)]));
        Assert.Equal("", Srt([]));
        Assert.Equal("1\n01:00:00,000 --> 01:00:00,002\nFin\n", Srt([(" ", 3600), (" Fin ", 0.0015)]));
    }

    [Fact]
    public void FrameLocatorAndContentFrameKeepTheWholeSelector()
    {
        var (pasos, avisos) = CodegenSteps.PasosDeGrabacion("""
            page.frame_locator("#panel").get_by_role("button", name="Abrir").click()
            page.locator("iframe").content_frame.get_by_test_id("fila").filter(has_text="Madrid").nth(0).first.last.click()
            """);
        Assert.Empty(avisos);
        Assert.Equal("#panel >> internal:control=enter-frame >> internal:role=button[name=\"Abrir\"i]", Str(pasos[0], "selector"));
        Assert.Equal("iframe >> internal:control=enter-frame >> internal:testid=[data-testid=\"fila\"s] >> internal:has-text=\"Madrid\"i >> nth=0 >> nth=0 >> nth=-1", Str(pasos[1], "selector"));
    }

    [Fact]
    public void ExactRoleAndPythonEscapesSurviveMultilineCalls()
    {
        var (pasos, avisos) = CodegenSteps.PasosDeGrabacion("""
            import re
            def run(playwright):
                page.get_by_role(
                    'button', name='A\u00f1adir "l\'été"', exact=True
                ).click()
                ignored = page.get_by_text("Asignado").click()
                expect(page.get_by_text("Ignorado")).to_be_visible()
                context.close()
            """);
        Assert.Empty(avisos);
        Assert.Single(pasos);
        Assert.Equal("internal:role=button[name=\"Añadir \\\"l'été\\\"\"s]", Str(pasos[0], "selector"));
    }

    [Fact]
    public void FillAfterClickKeepsOnlyTheFinalValueAndExactKeys()
    {
        var (pasos, avisos) = CodegenSteps.PasosDeGrabacion("""
            page.goto("https://example.org")
            page.get_by_label("Nombre").click()
            page.get_by_label("Nombre").fill("Ana")
            page.get_by_label("Nombre").fill("Ana María")
            page.get_by_label("Nombre").click()
            """);
        Assert.Empty(avisos);
        Assert.Single(pasos);
        Assert.True(JsonNode.DeepEquals(Obj("""
            {"tipo":"escribir","selector":"internal:label=\"Nombre\"i","valor":"Ana María",
             "desc":"Escribe «Ana María» en el campo «Nombre»","id":1,"texto":""}
            """), pasos[0]));
    }

    [Fact]
    public void PasswordFieldsAreRemovedAndWarningsAreUniqueAndSorted()
    {
        var (pasos, avisos) = CodegenSteps.PasosDeGrabacion("""
            page.get_by_label("Contraseña").fill("secreto")
            page.locator("input[type=password]").fill("otro")
            page1.get_by_text("Ayuda").click()
            page1.close()
            page.get_by_role("button", name="Guardar", exact=True).click()
            """);
        Assert.Single(pasos);
        Assert.True(Bool(pasos[0], "seguro"));
        Assert.Equal(new[] { "He quitado un paso donde se escribía una contraseña.",
            "Se abrió otra ventana durante la grabación; lo que hiciste en ella no se puede repetir." }, avisos);
        Assert.DoesNotContain("secreto", Json.Dump(pasos));
    }

    [Fact]
    public void KeyboardPressAndNavigationHaveNoInventedSelector()
    {
        var (pasos, avisos) = CodegenSteps.PasosDeGrabacion("""
            page.goto("https://example.org")
            page.keyboard.press("Enter")
            page.keyboard.press("Control+KeyA")
            page.goto("https://example.org/fin")
            page.press("body", "Escape")
            """);
        Assert.Empty(avisos);
        Assert.Equal(new[] { "Pulsa la tecla Intro", "Pulsa la tecla Control+KeyA", "Abre otra página" }, pasos.Select(p => Str(p, "desc")));
        Assert.All(pasos, p => Assert.False(p.ContainsKey("selector")));
    }
}
