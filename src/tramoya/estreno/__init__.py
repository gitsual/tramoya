"""The premiere («el estreno»): the finished take, staged in a theatre.

The take that ``assemble_marks`` leaves (``video.mp4``) is the bare recording. The premiere puts it on a stage:
the curtain opens on the title, each scene comes in with its headline and its cue, the cue sheet keeps count,
the boards light up as it goes, and the curtain falls with the house figures. ``sala.html`` draws every frame
as a pure function of ``seek(t)``; Playwright photographs it frame by frame and ffmpeg encodes it, so the same
take always gives the same film.

Two choices change the result:

* ``formato``: ``horizontal`` (1920x1080) or ``vertical`` (1080x1920, for a phone).
* ``duracion_max``: seconds at most. The take plays faster to fit (up to ``VEL_MAX``, the voice keeps its
  pitch); if even that is not enough, the film says so and stays as short as it can.
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import urllib.parse
from collections.abc import Callable
from dataclasses import dataclass
from datetime import date
from importlib import resources
from pathlib import Path

FORMATOS = {"horizontal": (1920, 1080), "vertical": (1080, 1920)}
FPS = 30
VEL_MAX = 1.5
INTRO_MS, OUTRO_MS = 3200, 4400
# A short film gets a shorter opening and close: they are part of the minute.
INTRO_CORTA_MS, OUTRO_CORTA_MS, CORTA_S = 2600, 3400, 45
ORIGEN = "http://tramoya.local"

PALABRAS = {
    "es": {"presenta": "Tramoya presenta", "cuenta": "Escena {i} de {n}", "hoja": "Hoja de cues",
           "sigue": "Sigue", "fin": "Fin", "funcion": "de la función", "escena": "escena", "escenas": "escenas",
           "duracion": "duración", "estreno": "estreno", "hecho": "Hecho con Tramoya"},
    "en": {"presenta": "Tramoya presents", "cuenta": "Scene {i} of {n}", "hoja": "Cue sheet",
           "sigue": "Next", "fin": "The end", "funcion": "of the show", "escena": "scene", "escenas": "scenes",
           "duracion": "running time", "estreno": "premiere", "hecho": "Made with Tramoya"},
}
MESES = {"es": "enero febrero marzo abril mayo junio julio agosto septiembre octubre noviembre diciembre".split(),
         "en": "January February March April May June July August September October November December".split()}


@dataclass
class Plan:
    """How the take fits in the film: its speed, the opening and close, and every scene on the film's clock."""

    vel: float
    intro_ms: int
    outro_ms: int
    escenas: list[dict]          # {"texto", "ini", "dur"}: ms of the film, counted from the end of the opening
    total_ms: int
    cabe: bool                   # False when even at VEL_MAX it runs longer than duracion_max


def plan(escenas: list[tuple[str, float]], video_s: float, duracion_max: float = 0) -> Plan:
    """``escenas``: (headline, seconds in the take) in order. ``video_s``: the length of the take."""
    corta = 0 < duracion_max <= CORTA_S
    intro, outro = (INTRO_CORTA_MS, OUTRO_CORTA_MS) if corta else (INTRO_MS, OUTRO_MS)
    video_ms = video_s * 1000
    vel, cabe = 1.0, True
    if duracion_max > 0:
        hueco = duracion_max * 1000 - intro - outro
        if hueco <= 0 or video_ms / hueco > VEL_MAX:
            vel, cabe = VEL_MAX, False
        elif video_ms > hueco:
            vel = video_ms / hueco
    salida, t = [], 0.0
    for texto, segundos in escenas:
        dur = segundos * 1000 / vel
        salida.append({"texto": texto, "ini": round(t), "dur": round(dur)})
        t += dur
    total = intro + video_ms / vel + outro
    return Plan(round(vel, 4), intro, outro, salida, round(total), cabe)


def _fecha(lang: str, hoy: date) -> tuple[str, str]:
    mes = MESES[lang][hoy.month - 1]
    larga = f"{hoy.day} de {mes} de {hoy.year}" if lang == "es" else f"{mes} {hoy.day}, {hoy.year}"
    return larga, f"{hoy.day} {mes[:3].upper()} {hoy.year}"


def datos(p: Plan, *, titulo: str, lugar: str, formato: str, frames: int, video: tuple[int, int],
          lang: str = "es", hoy: date | None = None) -> dict:
    """What sala.html reads (datos.json)."""
    lang = lang if lang in PALABRAS else "en"
    larga, corta = _fecha(lang, hoy or date.today())
    ancho, alto = FORMATOS[formato]
    return {"formato": formato, "ancho": ancho, "alto": alto, "titulo": titulo, "lugar": lugar,
            "fecha": larga, "fecha_corta": corta, "escenas": p.escenas, "frames": frames, "fps": FPS,
            "vel": p.vel, "intro": p.intro_ms, "outro": p.outro_ms, "video": {"w": video[0], "h": video[1]},
            "t": PALABRAS[lang]}


def medidas(video: Path, ffmpeg: str = "ffmpeg") -> tuple[tuple[int, int], float, bool]:
    """(width, height), seconds and whether it has sound, read from ffmpeg's own banner (the kit has no ffprobe)."""
    texto = subprocess.run([ffmpeg, "-nostdin", "-hide_banner", "-i", str(video)], capture_output=True, text=True,
                           encoding="utf-8", errors="replace").stderr
    tam = re.search(r"Video:.*?(\d{2,5})x(\d{2,5})", texto)
    dur = re.search(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", texto)
    if not tam or not dur:
        raise RuntimeError(f"ffmpeg cannot read {video}")
    h, m, s = dur.groups()
    return (int(tam[1]), int(tam[2])), int(h) * 3600 + int(m) * 60 + float(s), "Audio:" in texto


def _sala(destino: Path) -> None:
    """sala.html and the window's typefaces (the same Archivo and Plex Mono), next to datos.json."""
    paquete = resources.files("tramoya")
    (destino / "sala.html").write_text(paquete.joinpath("estreno", "sala.html").read_text(encoding="utf-8"),
                                       encoding="utf-8")
    (destino / "fonts").mkdir(exist_ok=True)
    for nombre in ("Archivo-tramoya.woff2", "IBMPlexMono-Medium.woff2"):
        (destino / "fonts" / nombre).write_bytes(paquete.joinpath("easy", "ui", "fonts", nombre).read_bytes())


def _servir_desde_disco(page, raiz: Path) -> None:
    """The page reads its files straight from disk: no local server for an antivirus to slow down."""
    raiz = raiz.resolve()

    def servir(route):
        ruta = urllib.parse.unquote(urllib.parse.urlsplit(route.request.url).path).lstrip("/")
        fichero = (raiz / ruta).resolve()
        if fichero.is_file() and fichero.is_relative_to(raiz):
            route.fulfill(path=str(fichero))
        else:
            route.fulfill(status=404, body="")

    page.route(f"{ORIGEN}/**", servir)


def estrenar(video: Path, escenas: list[tuple[str, float]], salida: Path, *, titulo: str, lugar: str = "",
             formato: str = "horizontal", duracion_max: float = 0, lang: str = "es", ffmpeg: str = "ffmpeg",
             progreso: Callable[[float], None] | None = None) -> Plan:
    """Stage ``video`` (the assembled take) and write the film to ``salida``. Returns the plan: the scenes on
    the film's clock (for the subtitles), the speed and whether it fitted in ``duracion_max``."""
    from playwright.sync_api import sync_playwright

    if formato not in FORMATOS:
        raise ValueError(f"unknown format: {formato} ({', '.join(FORMATOS)})")
    trabajo = salida.parent / "estreno"
    shutil.rmtree(trabajo, ignore_errors=True)
    (trabajo / "frames").mkdir(parents=True)
    tam, segundos, con_sonido = medidas(video, ffmpeg)
    subprocess.run([ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-i", str(video), "-vf", f"fps={FPS}", "-q:v", "3",
                    str(trabajo / "frames" / "%05d.jpg")], check=True)
    frames = len(list((trabajo / "frames").glob("*.jpg")))
    p = plan(escenas, frames / FPS, duracion_max)
    (trabajo / "datos.json").write_text(json.dumps(
        datos(p, titulo=titulo, lugar=lugar, formato=formato, frames=frames, video=tam, lang=lang),
        ensure_ascii=False, indent=1), encoding="utf-8")
    _sala(trabajo)

    ancho, alto = FORMATOS[formato]
    mudo = trabajo / "mudo.mp4"
    with sync_playwright() as pw:
        navegador = pw.chromium.launch()
        try:
            page = navegador.new_page(viewport={"width": ancho, "height": alto})
            _servir_desde_disco(page, trabajo)
            page.goto(f"{ORIGEN}/sala.html")
            page.wait_for_function("window.ready === true", timeout=120_000)
            total = int(page.evaluate("window.duration") / 1000 * FPS)
            ff = subprocess.Popen([ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "image2pipe", "-framerate", str(FPS),
                                   "-i", "-", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                                   "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(mudo)],
                                  stdin=subprocess.PIPE)
            for f in range(total):
                page.evaluate(f"seek({f * 1000 / FPS})")
                ff.stdin.write(page.screenshot(type="jpeg", quality=92))
                if progreso and f % FPS == 0:
                    progreso(f / total)
            ff.stdin.close()
            if ff.wait():
                raise RuntimeError("ffmpeg failed while encoding the premiere")
        finally:
            navegador.close()

    salida.parent.mkdir(parents=True, exist_ok=True)
    if con_sonido:
        # The voice follows the picture: as fast as the take and after the opening, silence under the curtain.
        # No afade (the voice is over by then), and stereo: some ffmpeg builds never finish a mono AAC at 192k.
        fin = f"{p.total_ms / 1000:.3f}"
        filtro = (f"[1:a]aresample=48000,{f'atempo={p.vel:.4f},' if p.vel != 1 else ''}adelay={p.intro_ms}:all=1,"
                  f"apad=whole_dur={fin}[a]")
        subprocess.run([ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-i", str(mudo), "-i", str(video),
                        "-filter_complex", filtro, "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac",
                        "-ac", "2", "-b:a", "192k", "-t", fin, "-movflags", "+faststart", str(salida)],
                       check=True)
    else:
        shutil.copyfile(mudo, salida)
    shutil.rmtree(trabajo / "frames", ignore_errors=True)
    return p


def subtitulos(p: Plan) -> list[tuple[str, float]]:
    """(text, seconds) for every stretch of the film, the opening and close as silent stretches, for an .srt."""
    filas = [("", p.intro_ms / 1000)] + [(e["texto"], e["dur"] / 1000) for e in p.escenas]
    return [*filas, ("", p.outro_ms / 1000)]
