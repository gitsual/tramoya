using System.Text;
using Tramoya.Voice;

namespace Tramoya.Tests;

/// <summary>
/// Tests de <c>Tramoya.Voice.Sapi</c>. Lo que necesita voces instaladas se salta sin fallar
/// cuando la máquina no tiene ninguna; <c>Oir</c> no se prueba porque hace ruido.
/// </summary>
public class VoiceTests
{
    [Theory]
    [InlineData("Microsoft Helena Desktop - Spanish (Spain)", true)]
    [InlineData("Microsoft Sabina Desktop - Spanish (Mexico)", true)]
    [InlineData("Voz en Español", true)]
    [InlineData("voz en espanol (castellano)", true)]
    [InlineData("Microsoft Zira Desktop - English (United States)", false)]
    [InlineData("", false)]
    public void EsEspanol(string descripcion, bool esperado) =>
        Assert.Equal(esperado, Sapi.EsEspanol(descripcion));

    [Fact]
    public void VocesDevuelveListaConLasDeEspanolPrimero()
    {
        var voces = Sapi.Voces();
        Assert.NotNull(voces);
        // Una vez aparece la primera voz que no es de español, ya no queda ninguna de español.
        var primeraNoEspanola = voces.FindIndex(v => !Sapi.EsEspanol(v));
        if (primeraNoEspanola >= 0)
            Assert.DoesNotContain(voces.Skip(primeraNoEspanola), Sapi.EsEspanol);
        Assert.All(voces, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void HablarEscribeWav48kHzMono16Bit()
    {
        if (Sapi.Voces().Count == 0)
            return; // sin voces en esta máquina no hay nada que sintetizar

        var carpeta = Path.Combine(Path.GetTempPath(), "tramoya-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            // La subcarpeta no existe: Hablar tiene que crearla.
            var ruta = Path.Combine(carpeta, "voz", "prueba.wav");
            Sapi.Hablar(new Dictionary<string, string> { [ruta] = "Hola, esto es una prueba." });

            Assert.True(File.Exists(ruta));
            var (canales, frecuencia, bits, datos) = LeerCabeceraWav(ruta);
            Assert.Equal(1, canales);
            Assert.Equal(48000, frecuencia);
            Assert.Equal(16, bits);
            Assert.True(datos > 0, "el wav no tiene muestras");
        }
        finally
        {
            if (Directory.Exists(carpeta))
                Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public void HablarConVozPorNombre()
    {
        var voces = Sapi.Voces();
        if (voces.Count == 0)
            return;

        var carpeta = Path.Combine(Path.GetTempPath(), "tramoya-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ruta = Path.Combine(carpeta, "nombre.wav");
            // Con la última voz de la lista (la que no se elegiría por defecto si hay más de una)
            // y la velocidad fuera de rango: tiene que recortarla a 10 y no fallar.
            Sapi.Hablar(new Dictionary<string, string> { [ruta] = "Prueba." }, voces[^1], velocidad: 99);
            var (canales, frecuencia, bits, _) = LeerCabeceraWav(ruta);
            Assert.Equal((1, 48000, 16), (canales, frecuencia, bits));
        }
        finally
        {
            if (Directory.Exists(carpeta))
                Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public void EscritorioEsUnaCarpeta()
    {
        var escritorio = Sapi.Escritorio();
        if (escritorio is null)
            return;
        Assert.True(Path.IsPathRooted(escritorio));
    }

    [Fact]
    public void CrearAccesoDejaUnLnk()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "tramoya-lnk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(carpeta, "Tramoya.exe");
            var ruta = Sapi.CrearAcceso(carpeta, exe, carpeta, Path.Combine(carpeta, "no-existe.ico"));
            Assert.Equal(Path.Combine(carpeta, "Tramoya.lnk"), ruta);
            Assert.True(File.Exists(ruta));
            // Un .lnk empieza por el tamaño de cabecera 0x4C y el CLSID de ShellLink.
            var bytes = File.ReadAllBytes(ruta);
            Assert.True(bytes.Length > 0x4C);
            Assert.Equal(0x4C, BitConverter.ToInt32(bytes, 0));
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    /// <summary>Lee el chunk <c>fmt </c> de un RIFF/WAVE y el tamaño del chunk <c>data</c>.</summary>
    private static (int Canales, int Frecuencia, int Bits, int Datos) LeerCabeceraWav(string ruta)
    {
        using var f = File.OpenRead(ruta);
        using var r = new BinaryReader(f);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(r.ReadBytes(4)));
        r.ReadInt32(); // tamaño del RIFF
        Assert.Equal("WAVE", Encoding.ASCII.GetString(r.ReadBytes(4)));
        int canales = 0, frecuencia = 0, bits = 0, datos = -1;
        while (f.Position + 8 <= f.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            var tam = r.ReadInt32();
            var inicio = f.Position;
            if (id == "fmt ")
            {
                r.ReadInt16(); // formato (1 = PCM)
                canales = r.ReadInt16();
                frecuencia = r.ReadInt32();
                r.ReadInt32(); // bytes por segundo
                r.ReadInt16(); // alineación de bloque
                bits = r.ReadInt16();
            }
            else if (id == "data")
            {
                datos = tam;
                break;
            }
            f.Position = inicio + tam + (tam & 1);
        }
        Assert.True(datos >= 0, "el wav no tiene chunk data");
        return (canales, frecuencia, bits, datos);
    }
}
