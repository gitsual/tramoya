# tramoya para Windows en C# — contrato de arquitectura

Versión Windows de **tramoya** reescrita entera en C#/.NET 9. La versión de Linux y macOS
sigue siendo el paquete Python de `../src/tramoya` (v0.5.0). **El código Python es la
especificación**: cada módulo de C# es el port de un módulo de Python y los tests de
`../tests/test_*.py` son los tests que hay que portar. Ambas versiones tienen que leer y
escribir los mismos ficheros.

## Decisiones ya tomadas (no se discuten)

- **Monolito modular**: un solo ejecutable `Tramoya.exe` (WinExe) con proyectos separados.
  Sin servicios de red, sin microservicios.
- **.NET 9**, C# `latest`, nullable activado, `ImplicitUsings` activado.
- **Ventana con WebView2** (WinForms host) que reutiliza la interfaz web actual
  `../src/tramoya/easy/ui/index.html` y `escucha.js` **sin modificarlas** (van como
  recursos incrustados con `LogicalName` `ui/index.html`, `ui/escucha.js`, `ui/tramoya.ico`).
- **Playwright para .NET** (`Microsoft.Playwright`) para grabar y reproducir; navegador
  Chromium. Selectores del grabador con `window.playwright.selector` bajo `PWDEBUG=console`.
- **Voz**: `System.Speech` (SAPI). Kokoro/ONNX queda para más adelante.
- **Montaje**: ffmpeg como programa externo (en `ffmpeg/` junto al exe, o en el PATH).
- **Formatos compatibles byte a byte en lo que importa** con la versión Python:
  `tramoya.toml`, `marks.json`, `script.json`, `proyecto.json`, `grabacion.json`,
  `auth.json` (storage_state de Playwright), `ajustes.json`, el lenguaje de pasos, los `.srt`
  y `.ass`, y los argv de ffmpeg (los tests los comparan literalmente).
- Mismo repositorio, carpeta `windows/`. Rama `feat/windows-csharp`.
- **Nada de `Co-Authored-By`** ni menciones a IA en commits, código o documentación.

## Proyectos y quién porta qué

```
windows/
  Tramoya.sln
  Directory.Build.props          net9.0, nullable, versión 0.6.0
  Tramoya.Core/      (net9.0)    todo lo puro: sin navegador, sin COM, sin ventana
  Tramoya.Stage/     (net9.0)    Playwright: Stage, OpenStage, Director
  Tramoya.Voice/     (net9.0-windows) SAPI: voces, hablar a wav, oír
  Tramoya.Easy/      (net9.0-windows) motor y servidor HTTP de «tramoya easy»
  Tramoya.App/       (WinExe)    Program: ventana WebView2 + CLI; AssemblyName = Tramoya
  Tramoya.Tests/     (xunit)     un fichero por módulo de Python: <Modulo>Tests.cs
```

| Python (`src/tramoya/`) | C# (proyecto / fichero) | Tests |
|---|---|---|
| `marks.py` | Core/`Marks.cs` (**hecho**: `Mark`, `MarkKeys`, `Marks`) | `MarksTests.cs` |
| `pacing.py` | Core/`Pacing.cs` (**hecho**) | `PacingTests.cs` |
| `ffmpeg.py` | Core/`Ffmpeg.cs` (**hecho**: `Runner`, `Ffmpeg`, `Fmt`, `Shell`) | `FfmpegTests.cs` |
| `profile.py` (dataclasses) | Core/`Profiles.cs` (**hecho**: `Scene`, `Profile`) | — |
| `profile.py` (load/dump) | Core/`ProfileFile.cs`: `ProfileFile.Load(path)`, `ProfileFile.Dump(profile)` | `ProfileTests.cs` |
| `profile.py` (pasos) | Core/`Steps.cs`: `Step`, `StepError`, `Steps.Verbs`, `Steps.Parse(line)`, `Steps.RunAsync(IStage, steps, Func<double,Task> say, baseUrl)` | `ProfileTests.cs` |
| `cues.py` | Core/`Cues.cs`: `Cue(T, Text)`, `Cues.ParseWithTotal`, `Parse`, `ParseMeta`, `OffsetFromDuration`, `Shift`, `ToMarks`, `ToAss` | `CuesTests.cs` |
| `narration.py` | Core/`Narration.cs`: `Script`, `DeckScript`, `Narration.WavSeconds(path)`, `Narrator` (async; `player` inyectable que devuelve algo con `WaitAsync()`) | `NarrationTests.cs` |
| `i18n.py` | Core/`I18n.cs`: `Messages` | `I18nTests.cs` |
| `snapshots.py` | Core/`Snapshots.cs`: `WriteSnapshot`, `ReadSnapshots`, `DiffSnapshots` | `SnapshotsTests.cs` (si hay tests en Python; si no, unos mínimos) |
| `assembly.py` | Core/`Assembly.cs`: `Segment`, `Assembly.HumanizeWait`, `PlanSegments`, `BuildSceneClip`, `FitAndMux`, `AssembleMarks`, `RenderSlide`, `RenderClip`, `AssembleDeck` | `AssemblyTests.cs` |
| `captions.py` | Core/`Captions.cs`: `Cue(Start, End, Text)` → llamarlo `CaptionCue` para no chocar con `Cues.Cue`; `SplitSentences`, `Wrap`, `Fits`, `SplitLong`, `PageCues`, `BuildCues`, `Timestamp`, `RenderSrt`, `RenderAss`, `BurnArgv`, `TrimPlan` | `CaptionsTests.cs` |
| `audio.py` | Core/`Audio.cs`: `Quantize`, `BuildLoop`, `RenderWav`, `DuckFilter`, `SoloFilter`, `MixArgv` | `AudioTests.cs` |
| `recording.py` | Core/`Recording.cs`: `ScenesFromDevtools(JsonNode)`, `ScenesFromCodegen(string)`, `ScenesFromFile(path)` | `RecordingTests.cs` |
| `wizard.py` | Core/`Wizard.cs`: `IAnswers`, `ScriptedAnswers`, `FlagAnswers`, `TtyAnswers`, `MissingAnswer`, `Wizard.Slug`, `Interview`, `Scaffold`, `SessionFile` | `WizardTests.cs` |
| `assistant.py` | Core/`Assistant.cs`: `Step`, `Plan`, `IBackend`, `OllamaBackend`, `ClaudeBackend`, `PickBackend`, `ParsePlan`, `Ask`, `RenderPlan`, `Preflight`, `RunPlan` (HTTP con `HttpClient`, inyectable) | `AssistantTests.cs` |
| `record.py` | **no se porta** (wf-recorder/PulseAudio son de Linux). Solo `ReadSidecar`/`WriteSidecar`/`OffsetSidecar` en Core/`Sidecar.cs` porque el CLI los usa. | — |
| `tts.py` | **no se porta** (Kokoro). El CLI `tts` en Windows usa la voz SAPI de `Tramoya.Voice`. | — |
| `console.py` | **no se porta** (no lo usa ningún comando). | — |
| `stage.py` | Stage/`Stage.cs` (`Stage : IStage` + `CaptionAsync`, `MoveToXyAsync`, `SnapshotAsync`, `ReadAsync`, `OverlayJs`), Stage/`Browser.cs` (`OpenStageAsync` → `StageSession : IAsyncDisposable` con `.Stage`, `.Page`, `.VideoPathAsync()`; `HttpReadyAsync`; `AppProcess`) | `StageTests.cs` |
| `director.py` | Stage/`Director.cs`: `Describe(profile)`, `RunProfileAsync(...)`, `Result`, `SilentClip` | `DirectorTests.cs` |
| `easy/engine.py` | Easy/`Engine.cs` (nombres en español como el Python: `Limpiar`, `PasoDeEvento`, `DominiosDe`, `ArgumentosNavegador`, `GrabarAsync`, `LineasDePaso`, `SegundosLectura`, `Escenas`, `Srt`, `HacerVideoAsync`, `ExplicarFallo`, `Emitir`) y Easy/`CodegenSteps.cs` (`PasosDeGrabacion` de un `.py` de codegen) | `EasyTests.cs` |
| `easy/app.py` | Easy/`Server.cs` (HttpListener en 127.0.0.1, mismas rutas `/api/...`, mismo JSON), Easy/`Projects.cs` (leer/guardar `proyecto.json`, ajustes, `Slug`, `Duplicar`, `Lista`, `Webs`), Easy/`Tasks.cs` (procesos hijos `Tramoya.exe easy-engine ...`) | `EasyTests.cs` |
| `easy/engine.py` voz SAPI | Voice/`Sapi.cs`: `Voces()`, `Hablar(dict<ruta,texto>, voz, velocidad)`, `Oir(texto, voz, velocidad)` (wav 48 kHz 16 bit mono) | `VoiceTests.cs` (solo lo que no necesite voces instaladas) |
| `cli.py` | App/`Cli.cs` (todos los subcomandos, misma salida de texto), App/`Program.cs`, App/`MainWindow.cs` (WebView2) | `CliTests.cs` |

## Convenciones

- **Un fichero C# por módulo Python**, nombres PascalCase, misma semántica y mismo orden de
  funciones. Comentarios de cabecera que expliquen el porqué, como en el Python. Lo que en
  el Python es español (el módulo `easy`, los textos de la interfaz y los mensajes al
  usuario) se queda en español tal cual; lo demás, en inglés.
- **Rutas como `string`** (no `FileInfo`). Fechas y números con `CultureInfo.InvariantCulture`.
  Para reproducir `str(float)` de Python usa `Fmt.Py`; para `f"{x:.3f}"`, `Fmt.F(x, 3)`.
- **JSON** siempre con `Tramoya.Core.Json` (sin escapar no-ASCII, indent 2). El Python usa
  `indent=1` en `proyecto.json`/`grabacion.json`: ahí da igual la sangría, no el contenido.
- **TOML** con Tomlyn para leer; escribir a mano como hace `dump_profile` (misma salida).
- **Excepciones**: `ValueError` → `ArgumentException` (y `StepError : ArgumentException`),
  `RuntimeError` → `InvalidOperationException`, `FileNotFoundError` → `FileNotFoundException`,
  `KeyError` → `KeyNotFoundException`.
- **Async solo donde hay E/S o navegador**: `Pacing.SleepAsync`, `Narrator.SayAsync`,
  `IStage`, `Stage`, `Director`, motor de `easy`. Lo puro (cues, captions, audio, assembly,
  marks...) es síncrono. `Runner.Run` es síncrono a propósito.
- **Inyección como en el Python**: lo que allí se parchea en tests (`open_stage`,
  `http_ready`, `launch_app`, `player`, `pipeline_factory`, `exists`, `http_post`) aquí es
  un delegado opcional en el constructor o un parámetro con valor por defecto.
- **Tests**: xunit, un `<Modulo>Tests.cs` por `test_<modulo>.py`, una clase por fichero,
  un método por test de Python con el mismo nombre en PascalCase. Portar **todos** los tests
  del fichero Python; si uno no aplica en Windows (wf-recorder, PulseAudio, kokoro) se deja
  un `[Fact(Skip = "...")]` con el motivo. Los fixtures se leen de `fixtures/` (copiados
  desde `../tests/fixtures` en la compilación). Los tests no deben necesitar ffmpeg, voces
  ni navegador: usan `Runner(dryRun: true)` y dobles, como los de Python.
- **No se tocan los `.csproj`** ni `Directory.Build.props`. Si hace falta un paquete,
  se dice en el informe final y lo añade quien integra.
- **No se editan ficheros de otro agente.** Cada uno escribe solo los ficheros de su fila
  de la tabla. El proyecto de tests es compartido: se añaden ficheros nuevos, nunca se
  modifican los de otros.
- **Compilar y probar**: `dotnet build Tramoya.sln` y
  `dotnet test Tramoya.Tests --filter "FullyQualifiedName~Tramoya.Tests.<Clase>"`.
  `dotnet` está en `C:\Program Files\dotnet`. Antes de terminar, la solución entera tiene
  que compilar sin errores y los tests propios pasar.
- **Git**: cada agente trabaja en su propio worktree y rama `wip/<nombre>`; al terminar
  hace **un commit** con sus ficheros (mensaje en inglés, estilo del repo:
  `feat(windows): port cues, narration and assembly to C#`), sin trailers de ningún tipo.

## Ejecutable y distribución (para el agente de App)

- `Tramoya.exe` sin argumentos abre la ventana (WebView2 sobre el servidor local de
  `Tramoya.Easy`). Con argumentos es el CLI: hace `AttachConsole(-1)` y despacha a `Cli`.
- Subcomando interno `easy-engine grabar|video|voces|oir|pasos` con los mismos argumentos y
  la misma salida JSON por líneas que `python -m tramoya.easy.engine`; el servidor lo lanza
  como proceso hijo (`Environment.ProcessPath`) para que un navegador que se cae no tire la
  ventana.
- Datos: `%LOCALAPPDATA%\Tramoya\datos` (perfil `_navegador`, proyectos, `ajustes.json`),
  vídeos en `%USERPROFILE%\Videos\Tramoya` («Mis vídeos»). Si existe `app\` junto al exe
  (kit portátil), todo se queda junto al exe como en el kit de Python.
- Chromium: `PLAYWRIGHT_BROWSERS_PATH` apunta a `navegador\` junto al exe si existe; si no,
  a `%LOCALAPPDATA%\Tramoya\navegador`, y si falta se instala con
  `Microsoft.Playwright.Program.Main(["install", "chromium"])` avisando en la ventana.
- `publish.ps1`: `dotnet publish Tramoya.App -c Release` (win-x64, self-contained,
  single-file) y deja en `dist/` el exe, la carpeta `.playwright` que necesita el paquete,
  `ffmpeg\ffmpeg.exe` y `navegador\` con Chromium; y un zip `Tramoya-<v>-windows.zip`.
  La firma de código se hace fuera (signtool), no en este script.

## Lecciones del kit de Python en los portátiles de Indra (27-10-2026)

- Las políticas corporativas de Chromium (proxy, extensiones forzadas) se aplican también al
  Chromium de Playwright del kit y **mandan sobre las opciones de línea de comandos**: con
  `--no-proxy-server` la ventana de `127.0.0.1` seguía en blanco; Edge sí llegaba. Por eso la
  ventana del `.exe` es WebView2 (motor de Edge) y arranca con `--no-proxy-server`; si aun así
  WebView2 heredara una política de proxy, el plan B es abrir la interfaz con `msedge.exe --app=`.
- El Chromium de grabación y reproducción sí debe seguir con el proxy corporativo: es el que
  abre las webs de la empresa (inicio de sesión integrado, certificados internos).
- El proxy de Indra corta descargas grandes: la descarga de Chromium por Playwright puede
  fallar allí; por eso el kit lleva `navegador\` dentro y el `.exe` solo descarga si falta.
