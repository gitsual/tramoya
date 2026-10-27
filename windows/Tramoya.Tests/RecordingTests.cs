using Tramoya.Core;
using Xunit;

namespace Tramoya.Tests;

public class RecordingTests
{
    private const string Devtools = """
        {"title":"notes","steps":[
          {"type":"setViewport","width":1280,"height":720},
          {"type":"navigate","url":"http://127.0.0.1:8000/index.html"},
          {"type":"click","selectors":[["aria/New note"],["#new"],["xpath///button[1]"]]},
          {"type":"change","value":"Water the plants","selectors":[["#title"],["xpath///input[1]"]]},
          {"type":"change","value":"home","selectors":[["aria/Tag"],["#tag"]]},
          {"type":"keyDown","key":"Enter"},{"type":"keyUp","key":"Enter"},
          {"type":"navigate","url":"http://127.0.0.1:8000/settings.html"},
          {"type":"click","selectors":[["text/Save"],["pierce/#save"]]},
          {"type":"scroll","y":400}]}
        """;
    private const string Codegen = """
        import re
        from playwright.sync_api import Playwright, sync_playwright, expect

        def run(playwright: Playwright) -> None:
            browser = playwright.chromium.launch(headless=False)
            context = browser.new_context()
            page = context.new_page()
            page.goto("http://127.0.0.1:8000/index.html")
            page.get_by_role("button", name="New note").click()
            page.locator("#title").click()
            page.locator("#title").fill("Water the plants")
            page.get_by_placeholder("Text").fill("Balcony first")
            page.locator("#tag").select_option("home")
            page.get_by_text("Work").click()
            page.get_by_label("Search").fill("re")
            page.locator("#title").press("Enter")
            page.goto("http://127.0.0.1:8000/about.html")
            page.get_by_role("link", name="Docs", exact=True).click()
            # ---------------------
            context.close()
            browser.close()

        with sync_playwright() as playwright:
            run(playwright)
        """;
    [Fact] public void DevtoolsExportBecomesOneScenePerNavigation()
    {
        var scenes = Recording.ScenesFromDevtools(Json.Parse(Devtools)!);
        Assert.Equal(new[] { "index", "settings" }, scenes.Select(s => s.Name));
        Assert.Equal(new[] { "click #new", "type #title | Water the plants", "select #tag | home", "press Enter", "say" }, scenes[0].Steps);
        Assert.Equal(new[] { "goto /settings.html", "click_text Save", "scroll 400", "say" }, scenes[1].Steps); Assert.Equal("settings", scenes[1].Label);
    }
    [Fact] public void DevtoolsPrefersCssThenTextThenAriaAndNeverXpathOrPierce()
    {
        var doc = Json.Parse("""
            {"steps":[{"type":"navigate","url":"http://a/"},
            {"type":"click","selectors":[["xpath///div"],["pierce/#x"]]},
            {"type":"click","selectors":[["xpath///div"],["aria/Go"],["text/Go now"]]}]}
            """)!;
        Assert.Equal(new[] { "click xpath=//div", "click_text Go now", "say" }, Recording.ScenesFromDevtools(doc)[0].Steps);
    }
    [Fact] public void CodegenPythonBecomesScenesWithStageVerbs()
    {
        var scenes = Recording.ScenesFromCodegen(Codegen);
        Assert.Equal(new[] { "index", "about" }, scenes.Select(s => s.Name));
        Assert.Equal(new[] { "click role=button[name=\"New note\"]", "click #title", "type #title | Water the plants", "type [placeholder=\"Text\"] | Balcony first", "select #tag | home", "click_text Work", "type role=textbox[name=\"Search\"] | re", "press Enter", "say" }, scenes[0].Steps);
        Assert.Equal(new[] { "goto /about.html", "click role=link[name=\"Docs\"]", "say" }, scenes[1].Steps);
    }
    [Fact] public void ScenesFromFilePicksTheFormatByContent()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(dir);
        try
        {
            string j = Path.Combine(dir, "flow.json"), p = Path.Combine(dir, "flow.py"), bad = Path.Combine(dir, "flow.txt");
            File.WriteAllText(j, Devtools); File.WriteAllText(p, Codegen); File.WriteAllText(bad, "hello");
            Assert.Equal(new[] { "index", "settings" }, Recording.ScenesFromFile(j).Select(s => s.Name));
            Assert.Equal(new[] { "index", "about" }, Recording.ScenesFromFile(p).Select(s => s.Name));
            Assert.Contains("neither", Assert.Throws<ArgumentException>(() => Recording.ScenesFromFile(bad)).Message);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact] public void ARecordingWithNoNavigationIsStillOneScene()
    {
        var scene = Assert.Single(Recording.ScenesFromDevtools(Json.Parse("""{"steps":[{"type":"click","selectors":[["#a"]]}]}""")!));
        Assert.Equal(new[] { "click #a", "say" }, scene.Steps);
    }
    [Theory]
    [InlineData("\"quote: \\\"; slash: \\\\; line: \\n; tab: \\t; unicode: \\u00e9\"", "quote: \"; slash: \\; line: \n; tab: \t; unicode: é")]
    [InlineData("'single: \\'; line: \\n'", "single: '; line: \n")]
    public void CodegenUnescapesPythonStringLiterals(string literal, string expected)
    {
        var scenes = Recording.ScenesFromCodegen("page.locator(\"#a\").fill(" + literal + ")");
        Assert.Equal("type #a | " + expected, scenes[0].Steps[0]);
    }
}
