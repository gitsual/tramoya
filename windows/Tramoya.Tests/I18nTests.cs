using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>Port of <c>tests/test_i18n.py</c>.</summary>
public sealed class I18nTests
{
    [Fact]
    public void TReturnsTextForRequestedLang()
    {
        var messages = new Messages(new() { ["greet"] = new() { ["es"] = "Hola", ["en"] = "Hello" } });
        Assert.Equal("Hola", messages.T("greet", lang: "es"));
        Assert.Equal("Hello", messages.T("greet", lang: "en"));
    }

    [Fact]
    public void TUsesDefaultLangWhenNoneGiven()
    {
        var messages = new Messages(new() { ["greet"] = new() { ["es"] = "Hola", ["en"] = "Hello" } }, defaultLang: "es");
        Assert.Equal("Hola", messages.T("greet"));
    }

    [Fact]
    public void TFallsBackToDefaultLangWhenMissing()
    {
        var messages = new Messages(new() { ["greet"] = new() { ["es"] = "Hola" } }, defaultLang: "es");
        Assert.Equal("Hola", messages.T("greet", lang: "en"));
    }

    [Fact]
    public void TFallsBackToKeyWhenTotallyMissing()
    {
        var messages = new Messages(new(), defaultLang: "es");
        Assert.Equal("unknown.key", messages.T("unknown.key"));
    }

    [Fact]
    public void TFormatsWithKwargs()
    {
        var messages = new Messages(new() { ["pace"] = new() { ["en"] = "--pace {p} ~= {minutes:.1f} min" } });
        Assert.Equal("--pace 2 ~= 1.5 min", messages.T("pace", "en", ("p", 2), ("minutes", 1.5)));
        Assert.Equal("--pace 2 ~= 1.5 min",
            messages.T("pace", "en", new Dictionary<string, object?> { ["p"] = 2, ["minutes"] = 1.5 }));
    }

    [Fact]
    public void RegisterMergesTable()
    {
        var messages = new Messages(new() { ["a"] = new() { ["en"] = "A" } });
        messages.Register(new() { ["b"] = new() { ["en"] = "B" } });
        Assert.Equal("A", messages.T("a", lang: "en"));
        Assert.Equal("B", messages.T("b", lang: "en"));
    }

    [Fact]
    public void RegisterOverwritesExistingKey()
    {
        var messages = new Messages(new() { ["a"] = new() { ["en"] = "A" } });
        messages.Register(new() { ["a"] = new() { ["en"] = "A2" } });
        Assert.Equal("A2", messages.T("a", lang: "en"));
    }

    [Theory]
    [InlineData("es", "ES")]
    [InlineData("en", "EN")]
    public void DefaultLangIsConfigurable(string defaultLang, string expected)
    {
        var messages = new Messages(new() { ["k"] = new() { ["es"] = "ES", ["en"] = "EN" } }, defaultLang: defaultLang);
        Assert.Equal(expected, messages.T("k"));
    }

    [Fact]
    public void FormatMatchesPythonStrFormat()
    {
        var kw = new Dictionary<string, object?> { ["x"] = 1, ["f"] = 2.0, ["b"] = true, ["n"] = null, ["s"] = "ñ" };
        Assert.Equal("{lit} 1 2.0 True None ñ", Messages.Format("{{lit}} {x} {f} {b} {n} {s}", kw));
        Assert.Equal("2.000 1", Messages.Format("{f:.3f} {x:d}", kw));
        Assert.Throws<KeyNotFoundException>(() => Messages.Format("{missing}", kw));
    }
}
