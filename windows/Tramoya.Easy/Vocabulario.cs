using System.Text.RegularExpressions;

namespace Tramoya.Easy;

// El vocabulario compartido mantiene las descripciones y decisiones del motor Python.
public static class Vocabulario
{
    public static readonly Regex Guardan = new(@"^\s*(imputar|guardar|grabar|enviar|aceptar|confirmar|borrar|eliminar|firmar|validar|aprobar|copiar filas|save|submit|delete|ok)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Regex Cookies = new(@"cookie|aceptar tod|accept all|rechazar tod|reject all|permitir tod|consent|allow all", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Regex Clave = new(@"password|contrase|clave|passwd|pwd", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Dictionary<string, string> Roles = new()
    {
        ["button"] = "el botón", ["link"] = "el enlace", ["checkbox"] = "la casilla", ["textbox"] = "el campo",
        ["cell"] = "la celda", ["row"] = "la fila", ["tab"] = "la pestaña", ["combobox"] = "el desplegable",
        ["option"] = "la opción", ["menuitem"] = "la opción del menú", ["radio"] = "la opción",
        ["img"] = "la imagen", ["heading"] = "el título", ["gridcell"] = "la celda", ["listitem"] = "el elemento",
        ["spinbutton"] = "el campo", ["searchbox"] = "el buscador", ["switch"] = "el interruptor"
    };
    public static readonly Dictionary<string, string> Teclas = new()
    {
        ["Enter"] = "Intro", ["Tab"] = "Tabulador", ["Escape"] = "Esc", ["Backspace"] = "Borrar",
        ["ArrowDown"] = "flecha abajo", ["ArrowUp"] = "flecha arriba"
    };
    public static readonly (int Ancho, int Alto) Viewport = (1280, 800);
    public static readonly Dictionary<string, (int Ancho, int Alto)> Tamanos = new()
    { ["720p"] = (1280, 720), ["800p"] = (1280, 800), ["1080p"] = (1920, 1080) };
    public static readonly Dictionary<string, double> Ritmos = new()
    { ["tranquilo"] = 1.3, ["normal"] = 1.0, ["agil"] = 0.75 };
    public static readonly string[] SsoSiempre = ["*.microsoftonline.com", "*.windows.net"];
}
