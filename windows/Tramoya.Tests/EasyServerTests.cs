using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Tramoya.Core;
using Tramoya.Easy;

namespace Tramoya.Tests;

/// <summary>
/// Tests de <c>Tramoya.Easy.Proyectos</c>, <c>Tareas</c> y <c>Server</c> (el port de
/// <c>tramoya/easy/app.py</c>). El <c>test_easy.py</c> de Python solo prueba el motor, así
/// que estos salen del comportamiento del módulo: una carpeta de datos temporal por test y,
/// para la API, un <c>Server</c> de verdad con un «motor» que es <c>cmd /c exit 0</c>: termina
/// al instante sin decir nada, que es justo el caso que recorre la máquina de estados sin
/// abrir navegadores ni montar vídeos.
/// </summary>
public class EasyServerTests : IDisposable
{
    private readonly string _datos;
    private readonly ServerOptions _o;
    private readonly Func<bool> _modeloAntes = Tareas.ModeloDisponible;

    public EasyServerTests()
    {
        // Ningún test sondea a un Ollama de verdad: sin modelo salvo que un test diga lo contrario.
        Tareas.ModeloDisponible = () => false;
        var raiz = Path.Combine(Path.GetTempPath(), "tramoya-easy-tests", Guid.NewGuid().ToString("N"));
        _datos = Path.Combine(raiz, "datos");
        Directory.CreateDirectory(_datos);
        _o = new ServerOptions
        {
            Datos = _datos,
            MisVideos = Path.Combine(raiz, "Mis vídeos"),
            Casa = raiz,
            Motor = ["cmd.exe", "/c", "exit", "0"],
            Entorno = new Dictionary<string, string> { ["TRAMOYA_PRUEBA"] = "1" },
            ExePath = null,
            Windows = true,
        };
    }

    public void Dispose()
    {
        Tareas.Reiniciar();
        Tareas.ModeloDisponible = _modeloAntes;
        try { Directory.Delete(Path.GetDirectoryName(_datos)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Las mismas opciones con otro «motor»; <c>Motor</c> es de solo lectura.</summary>
    private ServerOptions Opciones(string[] motor) => new()
    {
        Datos = _o.Datos,
        MisVideos = _o.MisVideos,
        Casa = _o.Casa,
        Motor = motor,
        Entorno = _o.Entorno,
        ExePath = null,
        Windows = true,
    };

    /// <summary>
    /// Un «motor» que ignora sus argumentos y escribe las líneas dadas por su salida, una por
    /// <c>echo</c> de un <c>.cmd</c>: es la forma más corta de fingir lo que dice el de verdad.
    /// </summary>
    private string[] MotorQueDice(params string[] lineas)
    {
        var ruta = Path.Combine(Path.GetDirectoryName(_datos)!, "motor.cmd");
        File.WriteAllText(ruta, "@echo off\r\n" + string.Concat(lineas.Select(l => $"echo {l}\r\n")), new UTF8Encoding(false));
        return ["cmd.exe", "/c", ruta];
    }

    /// <summary>Deja <c>proyecto.json</c> como una carpeta: ni se puede leer ni se puede sustituir.</summary>
    private void RomperProyecto(string id)
    {
        var ruta = Path.Combine(_datos, id, "proyecto.json");
        File.Delete(ruta);
        Directory.CreateDirectory(ruta);
    }

    private static JsonObject Obj(string s) => Json.Parse(s)!.AsObject();

    private static void Espera(Func<bool> listo, int ms = 15000)
    {
        var fin = DateTime.UtcNow.AddMilliseconds(ms);
        while (!listo())
        {
            if (DateTime.UtcNow > fin) throw new TimeoutException("la tarea no ha terminado a tiempo");
            Thread.Sleep(50);
        }
    }

    // --- Proyectos -----------------------------------------------------------------------

    [Theory]
    [InlineData("Añadir Código Proyecto", "anadir-codigo-proyecto")]
    [InlineData("  Hola, mundo!!  ", "hola-mundo")]
    [InlineData("Mi vídeo", "mi-video")]
    [InlineData("¡¡¡", "video")]
    [InlineData("", "video")]
    [InlineData("Ñandú & Co.", "nandu-co")]
    [InlineData("año", "ano")]
    [InlineData("ＡＢＣ", "abc")] // compatibilidad NFKD: anchura completa → ASCII
    [InlineData("ﬃ", "ffi")] // ligadura → tres letras
    [InlineData("Straße", "strae")] // ß no se descompone: se va, como en el Python
    public void SlugQuitaAcentosYSignos(string texto, string esperado) =>
        Assert.Equal(esperado, Proyectos.Slug(texto));

    [Fact]
    public void SlugRecortaACuarentaCaracteres()
    {
        var largo = string.Join(" ", Enumerable.Repeat("palabra", 20));
        var s = Proyectos.Slug(largo);
        Assert.Equal(40, s.Length);
        Assert.StartsWith("palabra-palabra", s);
    }

    [Fact]
    public void AjustesSinFicheroSonLosDeFabrica()
    {
        var a = Proyectos.LeerAjustes(_o);
        Assert.Equal("""{"voz":"","velocidad":0,"ritmo":"normal","tamano":"800p","srt":true,"dominios":[]}""",
            Json.Dump(a, indent: false));
    }

    [Fact]
    public void GuardarAjustesFiltraClavesYConvierteDominios()
    {
        var a = Proyectos.GuardarAjustes(_o, Obj("""{"voz":"Helena","velocidad":2,"dominios":"a.com, b.net  c.org,","intruso":1}"""));
        Assert.False(a.ContainsKey("intruso"));
        Assert.Equal("Helena", a["voz"]!.GetValue<string>());
        Assert.Equal(2, a["velocidad"]!.GetValue<int>());
        Assert.Equal("""["a.com","b.net","c.org"]""", Json.Dump(a["dominios"], indent: false));
        // Lo guardado se lee igual, y lo que no se mandó sigue siendo lo de fábrica.
        var otra = Proyectos.LeerAjustes(_o);
        Assert.Equal(Json.Dump(a, indent: false), Json.Dump(otra, indent: false));
        Assert.Equal("800p", otra["tamano"]!.GetValue<string>());
        // Una segunda llamada solo cambia lo que trae.
        var tercera = Proyectos.GuardarAjustes(_o, Obj("""{"ritmo":"agil"}"""));
        Assert.Equal("Helena", tercera["voz"]!.GetValue<string>());
        Assert.Equal("agil", tercera["ritmo"]!.GetValue<string>());
    }

    [Fact]
    public void AjustesRotosOConClavesAjenasSonLosDeFabricaMasLoConocido()
    {
        File.WriteAllText(Path.Combine(_datos, "ajustes.json"), "{esto no es json", Encoding.UTF8);
        Assert.Equal("normal", Proyectos.LeerAjustes(_o)["ritmo"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(_datos, "ajustes.json"), """{"ritmo":"tranquilo","otra":true}""", Encoding.UTF8);
        var a = Proyectos.LeerAjustes(_o);
        Assert.Equal("tranquilo", a["ritmo"]!.GetValue<string>());
        Assert.False(a.ContainsKey("otra"));
    }

    [Fact]
    public void CrearPoneNombrePorDefectoEsquemaEIdLibre()
    {
        var antes = Proyectos.Ahora();
        var id = Proyectos.Crear(_o, "  ", "apps.example.net/x");
        Assert.Equal("mi-video", id);
        var p = Proyectos.Leer(_o, id);
        Assert.Equal(["id", "nombre", "url", "pasos", "fase", "creado"], p.Select(kv => kv.Key).ToArray());
        Assert.Equal("Mi vídeo", p["nombre"]!.GetValue<string>());
        Assert.Equal("https://apps.example.net/x", p["url"]!.GetValue<string>());
        Assert.Empty(p["pasos"]!.AsArray());
        Assert.Equal("grabar", p["fase"]!.GetValue<string>());
        Assert.InRange(p["creado"]!.GetValue<double>(), antes, Proyectos.Ahora());
        // El mismo nombre otra vez: «-2», «-3»; una dirección con esquema se respeta.
        Assert.Equal("mi-video-2", Proyectos.Crear(_o, "Mi vídeo", "http://a.b/"));
        Assert.Equal("mi-video-3", Proyectos.Crear(_o, "MI VÍDEO", "http://a.b/"));
        Assert.Equal("http://a.b/", Proyectos.Leer(_o, "mi-video-2")["url"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_datos, "mi-video", "proyecto.tmp")));
    }

    [Fact]
    public void CrearSinDireccionFalla()
    {
        var e = Assert.Throws<ArgumentException>(() => Proyectos.Crear(_o, "Algo", "  "));
        Assert.Equal("Falta la dirección de la web", e.Message);
        Assert.Empty(Directory.GetDirectories(_datos));
    }

    [Fact]
    public void GuardarEsAtomicoYRespetaElContenido()
    {
        var id = Proyectos.Crear(_o, "Uno", "https://x.y/");
        var p = Proyectos.Leer(_o, id);
        p["pasos"] = new JsonArray(Obj("""{"id":1,"tipo":"clic","selector":"#a","desc":"Pulsa «Añadir»","texto":"Hola, ¿qué tal?"}"""));
        Proyectos.Guardar(_o, id, p);
        var texto = File.ReadAllText(Path.Combine(_datos, id, "proyecto.json"), Encoding.UTF8);
        Assert.Contains("Pulsa «Añadir»", texto); // sin \uXXXX, como ensure_ascii=False
        Assert.DoesNotContain("\\u00", texto);
        Assert.False(File.Exists(Path.Combine(_datos, id, "proyecto.tmp")));
        Assert.Equal("Pulsa «Añadir»", Proyectos.Leer(_o, id)["pasos"]![0]!["desc"]!.GetValue<string>());
    }

    [Fact]
    public void ListaOrdenaPorFechaYDiceLaTarea()
    {
        var a = Proyectos.Crear(_o, "Antiguo", "https://a.b/");
        var pa = Proyectos.Leer(_o, a);
        pa["creado"] = 1000.0;
        pa["video"] = @"C:\no\existe.mp4";
        Proyectos.Guardar(_o, a, pa);
        var b = Proyectos.Crear(_o, "Nuevo", "https://a.b/");
        Directory.CreateDirectory(Path.Combine(_datos, "_navegador")); // sin proyecto.json: no cuenta
        Directory.CreateDirectory(Path.Combine(_datos, "roto"));
        File.WriteAllText(Path.Combine(_datos, "roto", "proyecto.json"), "{", Encoding.UTF8);
        var lista = Proyectos.Lista(_o);
        Assert.Equal([b, a], lista.Select(x => x!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal(["id", "nombre", "fase", "creado", "video", "tarea"], lista[0]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.False(lista[0]!["video"]!.GetValue<bool>());
        Assert.True(lista[1]!["video"]!.GetValue<bool>());
        Assert.Equal("quieto", lista[0]!["tarea"]!.GetValue<string>());
        Assert.Equal(1000.0, lista[1]!["creado"]!.GetValue<double>());
    }

    [Fact]
    public void ListaToleraUnProyectoAMedioEscribirOIlegible()
    {
        var bueno = Proyectos.Crear(_o, "Bueno", "https://a.b/");
        // Guardar escribe proyecto.tmp y lo renombra encima: si se corta a mitad, queda un
        // proyecto.json vacío o a medias y un .tmp al lado. Ninguno de los dos cuenta.
        Directory.CreateDirectory(Path.Combine(_datos, "a-medias"));
        File.WriteAllText(Path.Combine(_datos, "a-medias", "proyecto.json"), "", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_datos, "a-medias", "proyecto.tmp"), """{"id":"a-medias","nombre":"x","creado":5}""", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_datos, "truncado"));
        File.WriteAllText(Path.Combine(_datos, "truncado", "proyecto.json"), """{"id":"truncado","nombre":"x","cre""", Encoding.UTF8);
        var ilegible = Proyectos.Crear(_o, "Ilegible", "https://a.b/");
        RomperProyecto(ilegible);
        Assert.Equal([bueno], Proyectos.Lista(_o).Select(x => x!["id"]!.GetValue<string>()).ToArray());
        Assert.Single(Proyectos.Webs(_o));
        Assert.False(Proyectos.Existe(_o, ilegible));
        // Y guardar encima de uno bueno no deja rastro del .tmp aunque se guarde varias veces.
        var p = Proyectos.Leer(_o, bueno);
        p["nombre"] = "Otro";
        Proyectos.Guardar(_o, bueno, p);
        Proyectos.Guardar(_o, bueno, p);
        Assert.Equal(["proyecto.json"], Directory.GetFiles(Path.Combine(_datos, bueno)).Select(f => Path.GetFileName(f)).ToArray());
        Assert.Equal("Otro", Proyectos.Leer(_o, bueno)["nombre"]!.GetValue<string>());
    }

    [Fact]
    public void WebsSinRepetirLaMasRecientePrimeroYComoMuchoSeis()
    {
        for (var i = 0; i < 8; i++)
        {
            var id = Proyectos.Crear(_o, $"P{i}", $"https://www.web{i}.example.org/ruta");
            var p = Proyectos.Leer(_o, id);
            p["creado"] = (double)i;
            Proyectos.Guardar(_o, id, p);
        }
        var repe = Proyectos.Crear(_o, "Repetida", "https://www.web7.example.org/ruta");
        var pr = Proyectos.Leer(_o, repe);
        pr["creado"] = 100.0;
        pr["titulo_web"] = "La web siete";
        Proyectos.Guardar(_o, repe, pr);
        var webs = Proyectos.Webs(_o);
        Assert.Equal(6, webs.Count);
        Assert.Equal("https://www.web7.example.org/ruta", webs[0]!["url"]!.GetValue<string>());
        Assert.Equal("La web siete", webs[0]!["nombre"]!.GetValue<string>());
        Assert.Equal("web6.example.org", webs[1]!["nombre"]!.GetValue<string>());
        Assert.Equal("web2.example.org", webs[5]!["nombre"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("https://www.example.org/x?y=1", "example.org")]
    [InlineData("http://intranet:8080/app", "intranet")]
    [InlineData("http://127.0.0.1:8770/", "127.0.0.1")]
    [InlineData("sin-esquema/ruta", "sin-esquema/ruta")]
    public void NombreWebEsElHostSinWww(string url, string esperado) =>
        Assert.Equal(esperado, Proyectos.NombreWeb(url));

    [Fact]
    public void DuplicarCopiaSinSalidasNiVideoYVuelveAExplicar()
    {
        var id = Proyectos.Crear(_o, "Original", "https://a.b/");
        var p = Proyectos.Leer(_o, id);
        p["pasos"] = new JsonArray(Obj("""{"id":1,"tipo":"clic","selector":"#a","desc":"Pulsa","texto":"t"}"""));
        p["fase"] = "video";
        p["video"] = @"C:\v.mp4";
        p["srt"] = @"C:\v.srt";
        Proyectos.Guardar(_o, id, p);
        var carpeta = Path.Combine(_datos, id);
        Directory.CreateDirectory(Path.Combine(carpeta, "out"));
        File.WriteAllText(Path.Combine(carpeta, "out", "x.mp4"), "x");
        File.WriteAllText(Path.Combine(carpeta, "ultimo-error.txt"), "x");
        File.WriteAllText(Path.Combine(carpeta, "grabacion.json"), "{}");
        Directory.CreateDirectory(Path.Combine(carpeta, "voz"));
        File.WriteAllText(Path.Combine(carpeta, "voz", "1.wav"), "x");

        var nuevo = Proyectos.Duplicar(_o, id);
        Assert.Equal("original-copia", nuevo);
        var q = Proyectos.Leer(_o, nuevo);
        Assert.Equal(nuevo, q["id"]!.GetValue<string>());
        Assert.Equal("Original (copia)", q["nombre"]!.GetValue<string>());
        Assert.Equal("explicar", q["fase"]!.GetValue<string>());
        Assert.Null(q["video"]);
        Assert.Null(q["srt"]);
        Assert.True(q.ContainsKey("video") && q.ContainsKey("srt"));
        Assert.Equal("t", q["pasos"]![0]!["texto"]!.GetValue<string>());
        Assert.True(q["creado"]!.GetValue<double>() > p["creado"]!.GetValue<double>());
        var copia = Path.Combine(_datos, nuevo);
        Assert.False(Directory.Exists(Path.Combine(copia, "out")));
        Assert.False(File.Exists(Path.Combine(copia, "ultimo-error.txt")));
        Assert.True(File.Exists(Path.Combine(copia, "grabacion.json")));
        Assert.True(File.Exists(Path.Combine(copia, "voz", "1.wav")));
        // Otra copia del original: «-2». Un proyecto sin pasos vuelve a «grabar».
        Assert.Equal("original-copia-2", Proyectos.Duplicar(_o, id));
        var vacio = Proyectos.Crear(_o, "Vacío", "https://a.b/");
        Assert.Equal("grabar", Proyectos.Leer(_o, Proyectos.Duplicar(_o, vacio))["fase"]!.GetValue<string>());
    }

    // --- Tareas --------------------------------------------------------------------------

    [Fact]
    public void ConvertirConservaLosTextosDeLosPasosIguales()
    {
        var id = Proyectos.Crear(_o, "Regrabado", "https://a.b/");
        var p = Proyectos.Leer(_o, id);
        p["pasos"] = new JsonArray(Obj("""{"id":1,"tipo":"clic","selector":"#a","desc":"Pulsa «A»","texto":"Primero, A."}"""));
        p["titulo_web"] = "Antes";
        Proyectos.Guardar(_o, id, p);
        File.WriteAllText(Path.Combine(_datos, id, "grabacion.json"), """
            {"pasos": [{"id": 1, "tipo": "clic", "selector": "#b", "desc": "Pulsa «B»"},
                       {"id": 2, "tipo": "clic", "selector": "#a", "desc": "Pulsa «A»"}],
             "avisos": ["ojo"], "inicio": 12.5, "titulo": "Mi web"}
            """, Encoding.UTF8);
        Tareas.Convertir(_o, id);
        var q = Proyectos.Leer(_o, id);
        Assert.Equal(["", "Primero, A."], q["pasos"]!.AsArray().Select(x => x!["texto"]!.GetValue<string>()).ToArray());
        Assert.Equal("explicar", q["fase"]!.GetValue<string>());
        Assert.Equal("Mi web", q["titulo_web"]!.GetValue<string>());
        Assert.Equal(12.5, q["inicio"]!.GetValue<double>());
        Assert.Equal("ojo", q["avisos"]![0]!.GetValue<string>());
        Assert.True(q.ContainsKey("video") && q["video"] is null);
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
        // Sin título nuevo se conserva el de antes.
        File.WriteAllText(Path.Combine(_datos, id, "grabacion.json"),
            """{"pasos": [{"id": 1, "tipo": "clic", "selector": "#b", "desc": "Pulsa «B»"}], "avisos": [], "inicio": 1}""", Encoding.UTF8);
        Tareas.Convertir(_o, id);
        Assert.Equal("Mi web", Proyectos.Leer(_o, id)["titulo_web"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertirPrefiereElTextoQueTraeLaGrabacion()
    {
        // La IA escribe la frase de cada paso: ese texto manda; donde viene vacío, se recupera el
        // del paso igual de antes, como al regrabar a mano.
        var id = Proyectos.Crear(_o, "Con IA", "https://a.b/");
        var p = Proyectos.Leer(_o, id);
        p["pasos"] = new JsonArray(
            Obj("""{"id":1,"tipo":"clic","selector":"#a","desc":"Pulsa «A»","texto":"Lo de antes."}"""),
            Obj("""{"id":2,"tipo":"clic","selector":"#b","desc":"Pulsa «B»","texto":"B de antes."}"""));
        Proyectos.Guardar(_o, id, p);
        File.WriteAllText(Path.Combine(_datos, id, "grabacion.json"), """
            {"pasos": [{"id": 1, "tipo": "clic", "selector": "#a", "desc": "Pulsa «A»", "texto": "Abrimos A."},
                       {"id": 2, "tipo": "clic", "selector": "#b", "desc": "Pulsa «B»", "texto": "  "},
                       {"id": 3, "tipo": "clic", "selector": "#c", "desc": "Pulsa «C»"}],
             "avisos": [], "inicio": "https://a.b/", "titulo": "A", "resumen": "Hecho: A y B.", "ok": true}
            """, Encoding.UTF8);
        Tareas.Convertir(_o, id);
        var q = Proyectos.Leer(_o, id);
        Assert.Equal(["Abrimos A.", "B de antes.", ""], q["pasos"]!.AsArray().Select(x => x!["texto"]!.GetValue<string>()).ToArray());
        Assert.Equal("explicar", q["fase"]!.GetValue<string>());
        Assert.Equal("Hecho: A y B.", q["resumen"]!.GetValue<string>());
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    [Fact]
    public void HayModeloSeInyectaYSeRecuerdaUnMinuto()
    {
        var veces = 0;
        Tareas.ModeloDisponible = () => { veces++; return true; };
        Assert.True(Tareas.HayModelo(_o));
        Assert.True(Tareas.HayModelo(_o));
        Assert.Equal(1, veces); // la segunda vez sale de la caché
        Tareas.Reiniciar();
        Tareas.ModeloDisponible = () => false;
        Assert.False(Tareas.HayModelo(_o));
    }

    [Fact]
    public void ConvertirAvisaSiNoHayPasosOGrabacion()
    {
        var id = Proyectos.Crear(_o, "Nada", "https://a.b/");
        Tareas.Convertir(_o, id);
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.Equal("No he podido leer la grabación.", t["msg"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(_datos, id, "grabacion.json"), """{"pasos": [], "avisos": [], "inicio": 1}""", Encoding.UTF8);
        Tareas.Convertir(_o, id);
        t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.Equal("No se ha apuntado ningún paso. Pulsa «Empezar a grabar» y luego haz los clics en la ventana de la web.",
            t["msg"]!.GetValue<string>());
        Assert.Equal("grabar", Proyectos.Leer(_o, id)["fase"]!.GetValue<string>());
        Tareas.Cancelar(id);
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertirConUnProyectoIlegibleDejaElErrorYNoLanza()
    {
        var id = Proyectos.Crear(_o, "Roto", "https://a.b/");
        File.WriteAllText(Path.Combine(_datos, id, "grabacion.json"),
            """{"pasos": [{"id": 1, "tipo": "clic", "selector": "#b", "desc": "Pulsa «B»"}], "avisos": [], "inicio": 1}""", Encoding.UTF8);
        RomperProyecto(id);
        Tareas.Convertir(_o, id);
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.StartsWith("No he podido guardar la grabación en el proyecto (", t["msg"]!.GetValue<string>());
        Assert.EndsWith(").", t["msg"]!.GetValue<string>());
        Assert.Contains("proyecto.json", File.ReadAllText(Path.Combine(_datos, id, "ultimo-error.txt"), Encoding.UTF8));
    }

    [Fact]
    public void VocesConUnMotorMudoEsUnaListaVaciaYSeCachea()
    {
        Assert.Empty(Tareas.Voces(_o));
        Assert.Same(Tareas.Voces(_o), Tareas.Voces(_o));
    }

    // --- HTTP ----------------------------------------------------------------------------

    private static HttpClient Cliente(Server s) => new() { BaseAddress = new Uri(s.Url), Timeout = TimeSpan.FromSeconds(20) };

    private static async Task<(HttpStatusCode Code, JsonObject Json)> Post(HttpClient c, string ruta, string? cuerpo = "{}")
    {
        using var r = await c.PostAsync(ruta, cuerpo is null ? null : new StringContent(cuerpo, Encoding.UTF8, "application/json"));
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        return (r.StatusCode, Obj(await r.Content.ReadAsStringAsync()));
    }

    private static async Task<(HttpStatusCode Code, JsonObject Json)> Get(HttpClient c, string ruta)
    {
        using var r = await c.GetAsync(ruta);
        return (r.StatusCode, Obj(await r.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task ServidorEnLoopbackSirveLaInterfazYElInicio()
    {
        using var s = Server.Start(_o);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/$", s.Url);
        Assert.True(Directory.Exists(_o.MisVideos));
        using var c = Cliente(s);
        foreach (var ruta in new[] { "/", "/index.html", "/?v=1" })
        {
            using var r = await c.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("text/html; charset=utf-8", r.Content.Headers.ContentType?.ToString());
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
            Assert.Contains("<title>Tramoya</title>", await r.Content.ReadAsStringAsync());
        }
        using var otra = await c.GetAsync("/otra-cosa");
        Assert.Equal(HttpStatusCode.NotFound, otra.StatusCode);

        var (code, inicio) = await Get(c, "/api/inicio");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(["proyectos", "voces", "webs", "ajustes", "windows", "carpeta", "acceso", "agente"], inicio.Select(kv => kv.Key).ToArray());
        Assert.False(inicio["agente"]!.GetValue<bool>()); // nadie contesta: la tarjeta de la IA no se enseña
        Assert.Empty(inicio["proyectos"]!.AsArray());
        Assert.Empty(inicio["voces"]!.AsArray());
        Assert.Empty(inicio["webs"]!.AsArray());
        Assert.Equal("800p", inicio["ajustes"]!["tamano"]!.GetValue<string>());
        Assert.True(inicio["windows"]!.GetValue<bool>());
        Assert.Equal(_o.MisVideos, inicio["carpeta"]!.GetValue<string>());
        Assert.True(inicio["acceso"]!.AsValue().TryGetValue<bool>(out _)); // depende del escritorio real: solo el tipo
    }

    [Fact]
    public async Task ProyectoInexistenteYAccionDesconocida()
    {
        using var s = Server.Start(_o);
        using var c = Cliente(s);
        var (code, j) = await Get(c, "/api/p/no-existe");
        Assert.Equal(HttpStatusCode.NotFound, code);
        Assert.Equal("""{"error":"no existe"}""", Json.Dump(j, indent: false));
        (code, j) = await Post(c, "/api/p/no-existe/guardar", """{"nombre":"x"}""");
        Assert.Equal(HttpStatusCode.NotFound, code);
        Assert.Equal("no existe", j["error"]!.GetValue<string>());
        (code, _) = await Post(c, "/api/p/Mayus/guardar");
        Assert.Equal(HttpStatusCode.NotFound, code);

        (code, j) = await Post(c, "/api/crear", """{"nombre":"Prueba","url":"a.b"}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("prueba", j["id"]!.GetValue<string>());
        (code, j) = await Post(c, "/api/p/prueba/volar");
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("acción desconocida", j["error"]!.GetValue<string>());
        (code, j) = await Post(c, "/api/crear", """{"nombre":"Sin web"}""");
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("Falta la dirección de la web", j["error"]!.GetValue<string>());
        using var mp4 = await c.GetAsync("/video/prueba.mp4");
        Assert.Equal(HttpStatusCode.NotFound, mp4.StatusCode);
    }

    [Fact]
    public async Task CrearGuardarDuplicarYBorrarPorHttp()
    {
        using var s = Server.Start(_o);
        using var c = Cliente(s);
        var (_, creado) = await Post(c, "/api/crear", """{"nombre":"Mi vídeo","url":"https://apps.example.net/"}""");
        var id = creado["id"]!.GetValue<string>();
        Assert.Equal("mi-video", id);

        var (code, p) = await Get(c, $"/api/p/{id}");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Mi vídeo", p["nombre"]!.GetValue<string>());
        Assert.Equal("""{"estado":"quieto"}""", Json.Dump(p["tarea"], indent: false));

        (code, var ok) = await Post(c, $"/api/p/{id}/guardar",
            """{"nombre":"Otro nombre","pasos":[{"id":1,"tipo":"clic","selector":"#a","desc":"Pulsa","texto":"¡Hola!"}],"fase":"explicar","ajeno":1}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("""{"ok":true}""", Json.Dump(ok, indent: false));
        var q = Proyectos.Leer(_o, id);
        Assert.Equal("Otro nombre", q["nombre"]!.GetValue<string>());
        Assert.Equal("explicar", q["fase"]!.GetValue<string>());
        Assert.Equal("¡Hola!", q["pasos"]![0]!["texto"]!.GetValue<string>());
        Assert.False(q.ContainsKey("ajeno"));

        (_, var inicio) = await Get(c, "/api/inicio");
        Assert.Equal("Otro nombre", inicio["proyectos"]![0]!["nombre"]!.GetValue<string>());
        Assert.Equal("apps.example.net", inicio["webs"]![0]!["nombre"]!.GetValue<string>());

        (code, var dup) = await Post(c, $"/api/p/{id}/duplicar");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(dup["ok"]!.GetValue<bool>());
        Assert.Equal("otro-nombre-copia", dup["id"]!.GetValue<string>());
        Assert.True(Proyectos.Existe(_o, "otro-nombre-copia"));

        (code, ok) = await Post(c, $"/api/p/{id}/borrar");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.False(Directory.Exists(Path.Combine(_datos, id)));
        (code, _) = await Get(c, $"/api/p/{id}");
        Assert.Equal(HttpStatusCode.NotFound, code);
        (_, inicio) = await Get(c, "/api/inicio");
        Assert.Single(inicio["proyectos"]!.AsArray());
    }

    [Fact]
    public async Task AjustesYOirPorHttp()
    {
        using var s = Server.Start(_o);
        using var c = Cliente(s);
        var (code, a) = await Post(c, "/api/ajustes", """{"voz":"Helena","velocidad":-2,"dominios":"x.com y.com","nada":1}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("""{"voz":"Helena","velocidad":-2,"ritmo":"normal","tamano":"800p","srt":true,"dominios":["x.com","y.com"]}""",
            Json.Dump(a, indent: false));
        Assert.True(File.Exists(Path.Combine(_datos, "ajustes.json")));
        (code, var ok) = await Post(c, "/api/oir", """{"texto":"  Hola.  "}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("""{"ok":true}""", Json.Dump(ok, indent: false));
        (code, ok) = await Post(c, "/api/oir", """{"texto":""}""");
        Assert.Equal(HttpStatusCode.OK, code);
        (code, ok) = await Post(c, "/api/oir", null); // sin cuerpo: {}
        Assert.Equal(HttpStatusCode.OK, code);
    }

    [Fact]
    public async Task ElVideoSeSirveEnteroYPorRangos()
    {
        var id = Proyectos.Crear(_o, "Con vídeo", "https://a.b/");
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        var mp4 = Path.Combine(Path.GetDirectoryName(_datos)!, "falso.mp4");
        File.WriteAllBytes(mp4, bytes);
        var p = Proyectos.Leer(_o, id);
        p["video"] = mp4;
        Proyectos.Guardar(_o, id, p);

        using var s = Server.Start(_o);
        using var c = Cliente(s);
        using (var r = await c.GetAsync($"/video/{id}.mp4?123"))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("video/mp4", r.Content.Headers.ContentType?.ToString());
            Assert.Equal("bytes", r.Headers.AcceptRanges.Single());
            Assert.Equal(bytes, await r.Content.ReadAsByteArrayAsync());
        }
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"/video/{id}.mp4") { Headers = { Range = new RangeHeaderValue(10, 19) } })
        using (var r = await c.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.Equal("bytes 10-19/1000", r.Content.Headers.ContentRange?.ToString());
            Assert.Equal(10, r.Content.Headers.ContentLength);
            Assert.Equal(bytes[10..20], await r.Content.ReadAsByteArrayAsync());
        }
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"/video/{id}.mp4") { Headers = { Range = new RangeHeaderValue(990, null) } })
        using (var r = await c.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.Equal("bytes 990-999/1000", r.Content.Headers.ContentRange?.ToString());
            Assert.Equal(bytes[990..], await r.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task AbrirWebConUnMotorQueSeCierraSinEmpezarVuelveAQuieto()
    {
        using var s = Server.Start(_o);
        using var c = Cliente(s);
        var (_, creado) = await Post(c, "/api/crear", """{"nombre":"Grabar","url":"https://a.b/"}""");
        var id = creado["id"]!.GetValue<string>();
        var (code, ok) = await Post(c, $"/api/p/{id}/abrir-web");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(ok["ok"]!.GetValue<bool>());
        // El «motor» sale al instante sin decir «fin» ni «inicio»: como cerrar la ventana sin empezar.
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() == "quieto");
        (_, var p) = await Get(c, $"/api/p/{id}");
        Assert.Equal("grabar", p["fase"]!.GetValue<string>());
        Assert.Equal("quieto", p["tarea"]!["estado"]!.GetValue<string>());
        // Mandar órdenes sin grabador vivo no hace nada ni falla.
        (code, _) = await Post(c, $"/api/p/{id}/empezar");
        Assert.Equal(HttpStatusCode.OK, code);
        (code, _) = await Post(c, $"/api/p/{id}/terminar");
        Assert.Equal(HttpStatusCode.OK, code);
        (code, _) = await Post(c, $"/api/p/{id}/cancelar");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    [Fact]
    public async Task ElInicioDiceQueHayIaCuandoElModeloContesta()
    {
        Tareas.ModeloDisponible = () => true;
        using var s = Server.Start(_o);
        using var c = Cliente(s);
        var (_, inicio) = await Get(c, "/api/inicio");
        Assert.True(inicio["agente"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AgentePorHttpRechazaUnObjetivoVacioYConUnoArranca()
    {
        using var s = Server.Start(_o);
        using var c = Cliente(s);
        var (_, creado) = await Post(c, "/api/crear", """{"nombre":"Con IA","url":"https://a.b/"}""");
        var id = creado["id"]!.GetValue<string>();
        var (code, j) = await Post(c, $"/api/p/{id}/agente", """{"objetivo":"   "}""");
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("Escribe qué quieres que haga la IA.", j["error"]!.GetValue<string>());
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
        (code, j) = await Post(c, $"/api/p/{id}/agente", """{"objetivo":" Entra en Pedidos y abre el 1234 "}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(j["ok"]!.GetValue<bool>());
        // El objetivo se queda en el proyecto, recortado.
        Assert.Equal("Entra en Pedidos y abre el 1234", Proyectos.Leer(_o, id)["objetivo"]!.GetValue<string>());
        // El «motor» sale al instante sin decir ni «fin» ni «error»: la IA se ha ido sin terminar.
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() is "error" or "quieto");
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.Equal("La IA se ha cerrado sin terminar.", t["msg"]!.GetValue<string>());
        Assert.Equal("grabar", Proyectos.Leer(_o, id)["fase"]!.GetValue<string>());
        (code, _) = await Post(c, $"/api/p/{id}/cancelar");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    [Fact]
    public void AgenteConUnMotorQueTerminaPasaLosPasosYSusFrasesAlProyecto()
    {
        // El motor va contando lo que piensa y los pasos que apunta, y al final dice «fin»; la
        // grabación que deja (aquí escrita de antemano) pasa al proyecto con sus frases.
        var o = Opciones(MotorQueDice(
            """{"fase": "agente", "msg": "El boton 3 abre Pedidos", "p": 0}""",
            """{"fase": "agente", "msg": "Paso 1: Pulsa el boton Pedidos", "p": 0.04}""",
            """{"paso": "Pulsa el boton Pedidos", "n": 1}""",
            """{"fin": true, "n": 1, "inicio": "https://a.b/"}"""));
        var id = Proyectos.Crear(o, "Con IA", "https://a.b/");
        File.WriteAllText(Path.Combine(_datos, id, "grabacion.json"), """
            {"pasos": [{"id": 1, "tipo": "clic", "selector": "#p", "desc": "Pulsa el botón «Pedidos»", "texto": "Entramos en Pedidos."}],
             "avisos": [], "inicio": "https://a.b/", "titulo": "Pedidos", "resumen": "Pedidos abierto.", "ok": true}
            """, Encoding.UTF8);
        lock (Tareas.Cerrojo)
        {
            Tareas.Agente(o, id, "Entra en Pedidos");
            var t = Tareas.Tarea(id);
            Assert.Equal("pensando", t["estado"]!.GetValue<string>());
            Assert.Equal("Pensando…", t["msg"]!.GetValue<string>());
            Assert.Equal(0.0, t["p"]!.GetValue<double>());
        }
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() is "error" or "quieto");
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
        var p = Proyectos.Leer(o, id);
        Assert.Equal("explicar", p["fase"]!.GetValue<string>());
        Assert.Equal("Entra en Pedidos", p["objetivo"]!.GetValue<string>());
        Assert.Equal("Entramos en Pedidos.", p["pasos"]![0]!["texto"]!.GetValue<string>());
        Assert.Equal("Pedidos", p["titulo_web"]!.GetValue<string>());
        Assert.Equal("Pedidos abierto.", p["resumen"]!.GetValue<string>());
    }

    [Fact]
    public void AgenteConUnMotorQueFallaEnsenaLoQueDijoElModelo()
    {
        var o = Opciones(MotorQueDice(
            """{"fase": "agente", "msg": "Paso 1: Pulsa Entrar", "p": 0.04}""",
            """{"paso": "Pulsa Entrar", "n": 1}""",
            """{"error": "La IA no ha podido hacerlo: no encuentro Pedidos en esta web."}"""));
        var id = Proyectos.Crear(o, "Con IA", "https://a.b/");
        Tareas.Agente(o, id, "Entra en Pedidos");
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() is "error" or "quieto");
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.Equal("La IA no ha podido hacerlo: no encuentro Pedidos en esta web.", t["msg"]!.GetValue<string>());
        Assert.Equal("grabar", Proyectos.Leer(o, id)["fase"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_datos, id, "ultimo-error.txt")));
    }

    [Fact]
    public async Task HacerVideoConUnMotorQueFallaDejaElErrorYSuRastro()
    {
        using var s = Server.Start(_o);
        using var c = Cliente(s);
        var (_, creado) = await Post(c, "/api/crear", """{"nombre":"Montar","url":"https://a.b/"}""");
        var id = creado["id"]!.GetValue<string>();
        var (code, ok) = await Post(c, $"/api/p/{id}/video", """{"voz":true}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(ok["ok"]!.GetValue<bool>());
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() is "error" or "listo");
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.Equal("El vídeo no se ha podido hacer.", t["msg"]!.GetValue<string>());
        Assert.True(t.ContainsKey("paso") && t["paso"] is null);
        Assert.True(File.Exists(Path.Combine(_datos, id, "ultimo-error.txt")));
        // Antes de lanzar el motor, el proyecto se dejó con lo que el motor necesita.
        var p = Proyectos.Leer(_o, id);
        Assert.True(p["voz"]!.GetValue<bool>());
        Assert.Equal(_o.MisVideos, p["mis_videos"]!.GetValue<string>());
        Assert.Equal("800p", p["ajustes"]!["tamano"]!.GetValue<string>());
        Assert.Equal("grabar", p["fase"]!.GetValue<string>());
        // La interfaz ve el error por la API y lo quita al cancelar.
        (_, var q) = await Get(c, $"/api/p/{id}");
        Assert.Equal("error", q["tarea"]!["estado"]!.GetValue<string>());
        await Post(c, $"/api/p/{id}/cancelar");
        (_, q) = await Get(c, $"/api/p/{id}");
        Assert.Equal("quieto", q["tarea"]!["estado"]!.GetValue<string>());
    }

    [Fact]
    public async Task HacerVideoConUnMotorQueEscribeBasuraAcabaEnErrorYElServidorSigue()
    {
        // Un motor que no habla JSON: nada de lo que dice se entiende, no hay «ok» ni «error»,
        // y lo que dijo queda como rastro.
        var o = Opciones(["cmd.exe", "/c", "echo", "not json"]);
        using var s = Server.Start(o);
        using var c = Cliente(s);
        var (_, creado) = await Post(c, "/api/crear", """{"nombre":"Basura","url":"https://a.b/"}""");
        var id = creado["id"]!.GetValue<string>();
        var (code, _) = await Post(c, $"/api/p/{id}/video");
        Assert.Equal(HttpStatusCode.OK, code);
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() is "error" or "listo");
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.Equal("El vídeo no se ha podido hacer.", t["msg"]!.GetValue<string>());
        Assert.Contains("not json", File.ReadAllText(Path.Combine(_datos, id, "ultimo-error.txt"), Encoding.UTF8));
        Assert.Equal("grabar", Proyectos.Leer(_o, id)["fase"]!.GetValue<string>());
        (code, var inicio) = await Get(c, "/api/inicio");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("error", inicio["proyectos"]![0]!["tarea"]!.GetValue<string>());
        (code, _) = await Post(c, $"/api/p/{id}/cancelar");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    [Fact]
    public async Task HacerVideoConElProyectoIlegibleAlTerminarAcabaEnErrorYElServidorSigue()
    {
        // El motor dice que el vídeo está hecho, pero al ir a apuntarlo proyecto.json ya no se
        // puede leer ni sustituir. Eso pasa en el hilo que escucha al motor, sin nadie encima:
        // tiene que acabar en «error» con rastro, no tumbar la app. Se rompe el proyecto con
        // el cerrojo cogido para que el hilo del motor no llegue antes.
        var o = Opciones(MotorQueDice("""{"msg": "Montando", "p": 0.5}""", """{"ok": true, "video": "C:\\no\\existe.mp4"}"""));
        var id = Proyectos.Crear(o, "Montar", "https://a.b/");
        using var s = Server.Start(o);
        using var c = Cliente(s);
        lock (Tareas.Cerrojo)
        {
            Tareas.HacerVideo(o, id, voz: false);
            RomperProyecto(id);
        }
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() is "error" or "listo");
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.StartsWith("No he podido terminar el vídeo (", t["msg"]!.GetValue<string>());
        Assert.EndsWith(").", t["msg"]!.GetValue<string>());
        Assert.Contains("proyecto.json", File.ReadAllText(Path.Combine(_datos, id, "ultimo-error.txt"), Encoding.UTF8));
        // El servidor sigue contestando: la portada no cuenta el proyecto roto y el detalle es un 404.
        var (code, inicio) = await Get(c, "/api/inicio");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Empty(inicio["proyectos"]!.AsArray());
        (code, _) = await Get(c, $"/api/p/{id}");
        Assert.Equal(HttpStatusCode.NotFound, code);
        Tareas.Cancelar(id);
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    [Fact]
    public async Task AbrirWebConElProyectoIlegibleAlCerrarAcabaEnErrorYElServidorSigue()
    {
        // El grabador llega a empezar y, al cerrarse, la grabación hay que pasarla al proyecto;
        // si proyecto.json no se deja, «error» con el motivo en vez de un hilo muerto.
        var o = Opciones(MotorQueDice("""{"estado": "grabando"}""", """{"fin": true, "inicio": 1.5}"""));
        var id = Proyectos.Crear(o, "Grabar", "https://a.b/");
        File.WriteAllText(Path.Combine(_datos, id, "grabacion.json"),
            """{"pasos": [{"id": 1, "tipo": "clic", "selector": "#b", "desc": "Pulsa «B»"}], "avisos": [], "inicio": 1.5}""", Encoding.UTF8);
        using var s = Server.Start(o);
        using var c = Cliente(s);
        lock (Tareas.Cerrojo)
        {
            Tareas.AbrirWeb(o, id);
            RomperProyecto(id);
        }
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() is "error" or "quieto");
        var t = Tareas.Tarea(id);
        Assert.Equal("error", t["estado"]!.GetValue<string>());
        Assert.StartsWith("No he podido guardar la grabación en el proyecto (", t["msg"]!.GetValue<string>());
        var (code, inicio) = await Get(c, "/api/inicio");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Empty(inicio["proyectos"]!.AsArray());
        // Mandar órdenes a un grabador que ya se fue no hace nada ni falla.
        Tareas.Orden(id, "terminar");
        Assert.Equal("error", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    /// <summary>Si un proceso con ese id ya no existe o ha terminado.</summary>
    private static bool Terminado(int id)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(id);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private static HashSet<int> Pings()
    {
        var ids = new HashSet<int>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("PING"))
        {
            ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    [Fact]
    public void UnFalloDelLectorNoSueltaElMotorYCancelarLoMata()
    {
        // Un motor que dice una línea que el lector no digiere (una clave repetida: el
        // JsonObject revienta al tocarla) y luego se queda medio minuto vivo, como un
        // grabador con la ventana abierta. La tarea acaba en «error», pero tiene que seguir
        // siendo dueña del proceso: cancelar lo mata, con el ping que lo mantiene vivo.
        var ruta = Path.Combine(Path.GetDirectoryName(_datos)!, "lento.cmd");
        File.WriteAllText(ruta,
            "@echo off\r\necho {\"estado\": \"grabando\", \"estado\": 1}\r\nping 127.0.0.1 -n 30 >nul\r\n",
            new UTF8Encoding(false));
        var o = Opciones(["cmd.exe", "/c", ruta]);
        var id = Proyectos.Crear(o, "Lento", "https://a.b/");
        var pingsAntes = Pings();
        Tareas.AbrirWeb(o, id);
        var motor = Tareas.ProcesoDe(id);
        Assert.NotNull(motor);
        Espera(() => Tareas.Tarea(id)["estado"]!.GetValue<string>() == "error");
        Assert.StartsWith("No he podido guardar la grabación en el proyecto (", Tareas.Tarea(id)["msg"]!.GetValue<string>());
        // El motor sigue vivo y sigue siendo de la tarea.
        Assert.False(Terminado(motor.Value));
        Assert.Equal(motor, Tareas.ProcesoDe(id));
        Espera(() => Pings().Except(pingsAntes).Any());
        var pings = Pings().Except(pingsAntes).ToList();
        Tareas.Cancelar(id);
        Espera(() => Terminado(motor.Value) && pings.All(Terminado));
        Assert.Equal("quieto", Tareas.Tarea(id)["estado"]!.GetValue<string>());
    }

    [Fact]
    public async Task StopCierraElPuerto()
    {
        var s = Server.Start(_o);
        var url = s.Url;
        using (var c = Cliente(s))
            Assert.Equal(HttpStatusCode.OK, (await Get(c, "/api/inicio")).Code);
        s.Stop();
        s.Dispose(); // idempotente
        using var c2 = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => c2.GetAsync("/api/inicio"));
    }
}
