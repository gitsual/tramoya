using Tramoya.Core;
using Xunit;

namespace Tramoya.Tests;

public class LenguaTests
{
    [Theory]
    [InlineData("en", "Click the “Save” button", "Check the checkbox", "Type “Milk” in the “Title” field", "Choose “Blue” in “Colour”", "Press Enter", "Point at the link")]
    [InlineData("es", "Pulsa el botón «Save»", "Marca la casilla", "Escribe «Milk» en el campo «Title»", "Elige «Blue» en «Colour»", "Pulsa la tecla Intro", "Señala el enlace")]
    public void StepsAreWrittenInTheChosenLanguage(string lang, string clic, string casilla, string escribe, string elige, string tecla, string senala)
    {
        Assert.Equal(clic, Lengua.Clic(Lengua.Cosa("el botón", "Save", lang), "", lang));
        Assert.Equal(casilla, Lengua.Clic(Lengua.Cosa("la casilla", "", lang), "on", lang));
        Assert.Equal(escribe, Lengua.Escribe("Milk", "Title", lang));
        Assert.Equal(elige, Lengua.Elige("Blue", "Colour", lang));
        Assert.Equal(tecla, Lengua.Tecla("Enter", lang));
        Assert.Equal(senala, Lengua.Senala(Lengua.Cosa("el enlace", "", lang), lang));
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("fr", "en")]
    [InlineData("ES", "es")]
    [InlineData("es-ES", "es")]
    [InlineData("en-GB", "en")]
    public void UnknownLanguagesFallBackToEnglish(string? lang, string esperado) => Assert.Equal(esperado, Lengua.Normalizar(lang));

    [Fact]
    public void TheAgentWritesItsSentencesInTheLanguage()
    {
        Assert.Contains("en English", Tramoya.Agent.Protocol.SystemPrompt("Add a note", lang: "en"));
        Assert.Contains("en español", Tramoya.Agent.Protocol.SystemPrompt("Apunta una nota", lang: "es"));
        Assert.True(Tramoya.Agent.Protocol.EsPregunta("Where do I see the notes I have finished?"));
        Assert.True(Tramoya.Agent.Protocol.EsPregunta("how is the colour changed"));
        Assert.True(Tramoya.Agent.Protocol.EsPregunta("¿Dónde veo las notas terminadas?"));
        Assert.False(Tramoya.Agent.Protocol.EsPregunta("Add a note called Shopping"));
    }
}
