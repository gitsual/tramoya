using System.Runtime.InteropServices;

namespace Tramoya.Voice;

/// <summary>
/// La voz de Windows (SAPI), port de la sección «la voz de Windows (SAPI)» de
/// <c>tramoya/easy/engine.py</c> y del acceso directo de <c>tramoya/easy/app.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Se habla con SAPI por COM tardío (<c>SAPI.SpVoice</c> con <c>dynamic</c>), igual que el
/// Python con <c>comtypes</c>, y no con <c>System.Speech</c>: la solución compila con
/// <c>InvariantGlobalization</c> y en ese modo <c>System.Speech</c> deja <c>VoiceInfo.Culture</c>
/// a null y el sintetizador revienta con <c>NullReferenceException</c> en cuanto se le pide
/// hablar, elija voz o no. SAPI por COM no depende de las culturas de .NET y además devuelve
/// la misma descripción de voz que guarda la versión Python en <c>ajustes.json</c>
/// («Microsoft Helena Desktop - Spanish (Spain)»), así que un proyecto se lee igual con las dos.
/// </para>
/// <para>Los wav salen a 48 kHz, 16 bits, mono, que es lo que espera el montaje con ffmpeg.</para>
/// </remarks>
public static class Sapi
{
    // Constantes de SAPI 5 (sapi.h / SpeechLib): el formato de audio y el modo de apertura.
    private const int SAFT48kHz16BitMono = 38; // ojo: 39 es el estéreo (el Python usa 39 y escribe estéreo aunque su comentario diga mono)
    private const int SSFMCreateForWrite = 3;

    public static bool EsEspanol(string descripcion)
    {
        var d = descripcion.ToLowerInvariant();
        return d.Contains("spanish") || d.Contains("español") || d.Contains("espanol");
    }

    /// <summary>Las voces de Windows instaladas, las de español primero.</summary>
    public static List<string> Voces()
    {
        List<string> nombres;
        try
        {
            dynamic voz = Com("SAPI.SpVoice");
            try
            {
                nombres = Descripciones(voz);
            }
            finally
            {
                Marshal.FinalReleaseComObject(voz);
            }
        }
        catch (Exception)
        {
            // Sin SAPI (o sin ninguna voz) no hay voz: lista vacía, nunca una excepción.
            return [];
        }
        // Orden estable, como el `sorted` de Python: las de español delante y el resto en el
        // orden en que las da Windows.
        return nombres.OrderBy(n => EsEspanol(n) ? 0 : 1).ToList();
    }

    /// <summary>
    /// Sintetiza cada texto a su ruta (wav de 48 kHz, 16 bits, mono) con la voz pedida; si no
    /// se pide ninguna, la primera voz en español. La velocidad va de -10 a 10, como en SAPI.
    /// </summary>
    public static void Hablar(IDictionary<string, string> textos, string? nombre = null, int velocidad = 0)
    {
        dynamic voz = Voz(nombre, velocidad);
        try
        {
            foreach (var (ruta, texto) in textos)
            {
                var carpeta = Path.GetDirectoryName(Path.GetFullPath(ruta));
                if (!string.IsNullOrEmpty(carpeta))
                    Directory.CreateDirectory(carpeta);
                dynamic flujo = Com("SAPI.SpFileStream");
                dynamic formato = Com("SAPI.SpAudioFormat");
                try
                {
                    formato.Type = SAFT48kHz16BitMono;
                    flujo.Format = formato;
                    flujo.Open(Path.GetFullPath(ruta), SSFMCreateForWrite);
                    voz.AudioOutputStream = flujo;
                    voz.Speak(texto);
                }
                finally
                {
                    // Cerrar el fichero ya, no cuando el recolector quiera: el montaje lo lee
                    // acto seguido.
                    voz.AudioOutputStream = null;
                    flujo.Close();
                    Marshal.FinalReleaseComObject(formato);
                    Marshal.FinalReleaseComObject(flujo);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(voz);
        }
    }

    /// <summary>Lee una frase por los altavoces, para probarla antes de hacer el vídeo.</summary>
    public static void Oir(string texto, string? nombre = null, int velocidad = 0)
    {
        dynamic voz = Voz(nombre, velocidad);
        try
        {
            voz.Speak(texto); // síncrono: SVSFDefault
        }
        finally
        {
            Marshal.FinalReleaseComObject(voz);
        }
    }

    /// <summary>La carpeta del escritorio del usuario, o null si Windows no la tiene.</summary>
    public static string? Escritorio()
    {
        var ruta = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrEmpty(ruta) ? null : ruta;
    }

    /// <summary>
    /// Un acceso directo «Tramoya» con su icono en <paramref name="carpeta"/>, que abre la
    /// app sin consola. Devuelve la ruta del <c>.lnk</c>.
    /// </summary>
    /// <remarks>
    /// <c>WScript.Shell</c> por COM tardío, como el Python, para no depender de una interop
    /// generada ni de la Shell API; el destino es el exe, sin argumentos.
    /// </remarks>
    public static string CrearAcceso(string carpeta, string targetExe, string workingDir, string iconPath)
    {
        var destino = Path.Combine(carpeta, "Tramoya.lnk");
        dynamic shell = Com("WScript.Shell");
        try
        {
            dynamic lnk = shell.CreateShortcut(destino);
            try
            {
                lnk.TargetPath = targetExe;
                lnk.WorkingDirectory = workingDir;
                lnk.Description = "Tramoya: vídeos de procesos, sin esfuerzo";
                if (File.Exists(iconPath))
                    lnk.IconLocation = $"{iconPath},0";
                lnk.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(lnk);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
        return destino;
    }

    // --- interno --------------------------------------------------------------------

    /// <summary>Crea un objeto COM por su ProgID; falla claro si Windows no lo tiene.</summary>
    private static object Com(string progId)
    {
        var tipo = Type.GetTypeFromProgID(progId)
                   ?? throw new InvalidOperationException($"No está disponible {progId}");
        return Activator.CreateInstance(tipo)
               ?? throw new InvalidOperationException($"No he podido crear {progId}");
    }

    /// <summary>Las descripciones de los tokens de voz, en el orden en que las da SAPI.</summary>
    private static List<string> Descripciones(dynamic voz)
    {
        dynamic todas = voz.GetVoices();
        try
        {
            var nombres = new List<string>();
            int n = todas.Count;
            for (var i = 0; i < n; i++)
                nombres.Add((string)todas.Item(i).GetDescription());
            return nombres;
        }
        finally
        {
            Marshal.FinalReleaseComObject(todas);
        }
    }

    /// <summary>
    /// Un <c>SpVoice</c> con la voz elegida: la que tenga exactamente esa descripción; si no se
    /// pide ninguna, la primera en español; si no hay ninguna, la que Windows tenga por defecto.
    /// </summary>
    private static object Voz(string? nombre, int velocidad)
    {
        dynamic voz = Com("SAPI.SpVoice");
        try
        {
            dynamic todas = voz.GetVoices();
            try
            {
                dynamic? elegida = null;
                int n = todas.Count;
                for (var i = 0; i < n; i++)
                {
                    dynamic token = todas.Item(i);
                    string d = token.GetDescription();
                    if (d == nombre || (elegida is null && string.IsNullOrEmpty(nombre) && EsEspanol(d)))
                        elegida = token;
                }
                if (elegida is not null)
                    voz.Voice = elegida;
            }
            finally
            {
                Marshal.FinalReleaseComObject(todas);
            }
            voz.Rate = Math.Clamp(velocidad, -10, 10);
            return voz;
        }
        catch
        {
            Marshal.FinalReleaseComObject(voz);
            throw;
        }
    }
}
