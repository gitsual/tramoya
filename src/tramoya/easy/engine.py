"""The engine behind `tramoya easy`: clicks in, steps out; steps in, video out.

    python -m tramoya.easy.engine grabar <project dir> <url> <browser profile>
    python -m tramoya.easy.engine video <project dir> [--voz]
    python -m tramoya.easy.engine pasos <codegen.py>     steps of a codegen file (JSON)
    python -m tramoya.easy.engine voces                  Spanish SAPI voices (Windows)

`grabar` opens the page in a persistent browser profile (sessions survive from
one video to the next) and records nothing until it reads `empezar` on stdin,
so a sign-in is never part of the recording. Selectors come from Playwright's
own generator (`window.playwright.selector`, exposed by PWDEBUG=console): role
and accessible name first, never the generated ids that change on every load.

`video` turns the steps into tramoya scenes (a step with a sentence opens a
scene: the sentence is the caption and, with --voz, the narration read by the
Windows voice), replays them on stage and assembles the take. Each command
writes one JSON object per line: progress ({"fase", "msg", "p"}), then
{"ok": true, "video": ...} or {"error": ..., "paso": n}.

The user-facing text is Spanish; so are the names, which mirror that text.
"""
from __future__ import annotations

import ast
import contextlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path
from urllib.parse import urlparse

VIEWPORT = (1280, 800)
TAMANOS = {"720p": (1280, 720), "800p": (1280, 800), "1080p": (1920, 1080)}
RITMOS = {"tranquilo": 1.3, "normal": 1.0, "agil": 0.75}

# Botones que guardan algo: en el vídeo el cursor los señala pero no los pulsa.
GUARDAN = re.compile(
    r"^\s*(imputar|guardar|grabar|enviar|aceptar|confirmar|borrar|eliminar|firmar|"
    r"validar|aprobar|copiar filas|save|submit|delete|ok)\s*$", re.I)
# El aviso de cookies sale la primera vez y luego ya no: ese clic es opcional.
COOKIES = re.compile(r"cookie|aceptar tod|accept all|rechazar tod|reject all|permitir tod|"
                     r"consent|allow all", re.I)
CLAVE = re.compile(r"password|contrase|clave|passwd|pwd", re.I)

ROLES = {
    "button": "el botón", "link": "el enlace", "checkbox": "la casilla", "textbox": "el campo",
    "cell": "la celda", "row": "la fila", "tab": "la pestaña", "combobox": "el desplegable",
    "option": "la opción", "menuitem": "la opción del menú", "radio": "la opción",
    "img": "la imagen", "heading": "el título", "gridcell": "la celda", "listitem": "el elemento",
    "spinbutton": "el campo", "searchbox": "el buscador", "switch": "el interruptor",
}
TECLAS = {"Enter": "Intro", "Tab": "Tabulador", "Escape": "Esc", "Backspace": "Borrar",
          "ArrowDown": "flecha abajo", "ArrowUp": "flecha arriba"}


def emitir(**datos) -> None:
    print(json.dumps(datos, ensure_ascii=False), flush=True)


# --- grabación de playwright codegen -> pasos --------------------------------

def _texto(nodo) -> str | None:
    return nodo.value if isinstance(nodo, ast.Constant) and isinstance(nodo.value, str) else None


def _q(t: str) -> str:
    return json.dumps(t, ensure_ascii=False)


def _cadena(nodo) -> tuple[list[tuple[str, list, dict]], bool] | None:
    """page.a(..).b(..).c(..) -> ([(a, args, kwargs), ...], empieza_en_page)."""
    partes = []
    while True:
        if isinstance(nodo, ast.Call) and isinstance(nodo.func, ast.Attribute):
            kw = {k.arg: k.value for k in nodo.keywords}
            partes.append((nodo.func.attr, nodo.args, kw))
            nodo = nodo.func.value
        elif isinstance(nodo, ast.Attribute):
            partes.append((nodo.attr, [], {}))
            nodo = nodo.value
        elif isinstance(nodo, ast.Name):
            return list(reversed(partes)), nodo.id == "page"
        else:
            return None


def _raiz(nodo) -> str:
    while isinstance(nodo, (ast.Call, ast.Attribute)):
        nodo = nodo.func if isinstance(nodo, ast.Call) else nodo.value
    return nodo.id if isinstance(nodo, ast.Name) else ""


def _selector(partes) -> tuple[str, str]:
    """Las llamadas de localización -> (selector de Playwright, nombre para personas)."""
    sel, nombre = [], ""
    for metodo, args, kw in partes:
        a0 = _texto(args[0]) if args else None
        exacto = isinstance(kw.get("exact"), ast.Constant) and kw["exact"].value is True
        flag = "s" if exacto else "i"
        if metodo == "locator" and a0:
            sel.append(a0)
            if ht := _texto(kw.get("has_text")):
                sel.append(f"internal:has-text={_q(ht)}i")
                nombre = ht
        elif metodo == "get_by_role" and a0:
            n = _texto(kw.get("name"))
            sel.append(f"internal:role={a0}" + (f"[name={_q(n)}{flag}]" if n else ""))
            rol = ROLES.get(a0, "el elemento")
            nombre = f"{rol} «{n}»" if n else (f"{rol} de {nombre}" if nombre else rol)
        elif metodo == "get_by_text" and a0:
            sel.append(f"internal:text={_q(a0)}{flag}")
            nombre = f"«{a0}»"
        elif metodo == "get_by_label" and a0:
            sel.append(f"internal:label={_q(a0)}{flag}")
            nombre = f"el campo «{a0}»"
        elif metodo in ("get_by_placeholder", "get_by_title", "get_by_alt_text") and a0:
            attr = {"get_by_placeholder": "placeholder", "get_by_title": "title",
                    "get_by_alt_text": "alt"}[metodo]
            sel.append(f"internal:attr=[{attr}={_q(a0)}{flag}]")
            nombre = f"«{a0}»"
        elif metodo == "get_by_test_id" and a0:
            sel.append(f"internal:testid=[data-testid={_q(a0)}s]")
        elif metodo == "filter":
            if ht := _texto(kw.get("has_text")):
                sel.append(f"internal:has-text={_q(ht)}i")
        elif metodo == "nth" and args and isinstance(args[0], ast.Constant):
            sel.append(f"nth={args[0].value}")
        elif metodo == "first":
            sel.append("nth=0")
        elif metodo == "last":
            sel.append("nth=-1")
        elif metodo == "frame_locator" and a0:
            sel += [a0, "internal:control=enter-frame"]
        elif metodo == "content_frame":
            sel.append("internal:control=enter-frame")
        else:
            raise ValueError(metodo)
    if not nombre:
        nombre = "este elemento"
    return " >> ".join(sel), nombre


def pasos_de_grabacion(fuente: str) -> tuple[list[dict], list[str]]:
    pasos, avisos = [], []
    arbol = ast.parse(fuente)
    exprs = sorted((n for n in ast.walk(arbol) if isinstance(n, ast.Expr)
                    and isinstance(n.value, ast.Call)), key=lambda n: (n.lineno, n.col_offset))
    for nodo in exprs:
        c = _cadena(nodo.value)
        if c and not c[1] and c[0] and _raiz(nodo.value).startswith("page"):
            avisos.append("Se abrió otra ventana durante la grabación; lo que hiciste en ella "
                          "no se puede repetir.")
            continue
        if not c or not c[1] or not c[0]:
            continue
        partes = c[0]
        if partes[0][0] == "keyboard":
            if partes[-1][0] == "press" and partes[-1][1] and _texto(partes[-1][1][0]):
                k = _texto(partes[-1][1][0])
                pasos.append({"tipo": "tecla", "valor": k,
                              "desc": f"Pulsa la tecla {TECLAS.get(k, k)}"})
            continue
        accion, args, _kw = partes[-1]
        a0 = _texto(args[0]) if args else None
        if accion == "goto" and len(partes) == 1:
            if pasos:
                pasos.append({"tipo": "ir", "valor": a0, "desc": "Abre otra página"})
            continue
        if accion == "press" and len(partes) == 1:  # page.keyboard.press no pasa por aquí
            continue
        try:
            sel, nombre = _selector(partes[:-1]) if len(partes) > 1 else ("", "")
        except ValueError:
            avisos.append(f"Un paso no se ha podido entender y se ha quitado ({accion}).")
            continue
        if not sel:
            continue
        if accion in ("click", "dblclick", "check", "uncheck"):
            verbo = {"check": "Marca", "uncheck": "Desmarca"}.get(accion, "Pulsa")
            if accion in ("check", "uncheck") and not nombre.startswith("la casilla"):
                nombre = "la casilla" + ("" if nombre == "este elemento" else f" de {nombre}")
            n_limpio = re.sub(r"^.*«(.*)»$", r"\1", nombre)
            pasos.append({"tipo": "clic", "selector": sel, "desc": f"{verbo} {nombre}",
                          "guarda": bool(GUARDAN.match(n_limpio))})
        elif accion in ("fill", "type", "press_sequentially") and a0 is not None:
            if CLAVE.search(sel) or CLAVE.search(nombre):
                avisos.append("He quitado un paso donde se escribía una contraseña.")
                continue
            donde = "" if nombre == "este elemento" else f" en {nombre}"
            pasos.append({"tipo": "escribir", "selector": sel, "valor": a0,
                          "desc": f"Escribe «{a0}»{donde}"})
        elif accion == "press" and a0:
            pasos.append({"tipo": "tecla", "valor": a0, "selector": sel,
                          "desc": f"Pulsa la tecla {TECLAS.get(a0, a0)}"})
        elif accion == "select_option" and a0:
            pasos.append({"tipo": "elegir", "selector": sel, "valor": a0,
                          "desc": f"Elige «{a0}» en {nombre}"})
        elif accion in ("close", "wait_for_timeout", "set_default_timeout"):
            continue
        else:
            avisos.append(f"Un paso no se ha podido entender y se ha quitado ({accion}).")
    return limpiar(pasos), sorted(set(avisos))


def limpiar(pasos: list[dict]) -> list[dict]:
    """Junta lo repetido (clic + escribir en el mismo campo, dos escribir seguidos)
    y deja «solo señalar» marcado de entrada solo en el último botón que guarda:
    si se marca uno de en medio, los pasos de después no tendrían en qué apoyarse."""
    limpios: list[dict] = []
    for p in pasos:
        prev = limpios[-1] if limpios else None
        mismo = prev is not None and prev.get("selector") == p.get("selector")
        if mismo and p["tipo"] == "escribir" and prev["tipo"] in ("escribir", "clic"):
            limpios[-1] = p
        elif mismo and p["tipo"] == "clic" and prev["tipo"] == "escribir":
            continue
        else:
            limpios.append(p)
    ultimo_que_cuenta = max((i for i, p in enumerate(limpios) if p["tipo"] != "tecla"), default=-1)
    for i, p in enumerate(limpios):
        p["id"] = i + 1
        p.setdefault("texto", "")
        if p["tipo"] == "clic":
            p["seguro"] = bool(p.get("guarda")) and i == ultimo_que_cuenta
    return limpios


# --- el grabador propio: una sola ventana, sirve para cualquier web -------------
#
# Se abre la web en un perfil de navegador que se conserva (las sesiones siguen
# abiertas de un vídeo a otro). La persona entra, va a donde empieza el proceso y
# pulsa «Empezar» en la app: desde ese momento cada clic, texto o tecla se apunta
# con los selectores que genera el propio Playwright (por rol y nombre visible,
# no por los ids que cambian en JSF). El inicio de sesión nunca se graba.

# Inicio de sesión integrado de Windows (Kerberos/NTLM): la web, su dominio y los
# que se añadan en Ajustes. El de Microsoft va siempre porque muchas webs de empresa
# entran a través de él.
SSO_SIEMPRE = ["*.microsoftonline.com", "*.windows.net"]

ESCUCHA_JS = (Path(__file__).resolve().parent / "ui" / "escucha.js").read_text(encoding="utf-8")


def _paso_de_evento(d: dict, prefijo: str) -> dict | None:
    tipo = d.get("tipo")
    nombre = d.get("nombre") or ""
    que = d.get("que") or ""
    cosa = (f"{que} «{nombre}»" if que else f"«{nombre}»") if nombre else (que or "este elemento")
    if tipo == "tecla":
        return {"tipo": "tecla", "valor": d["valor"],
                "desc": f"Pulsa la tecla {TECLAS.get(d['valor'], d['valor'])}"}
    if not d.get("selector"):
        return None
    selector = prefijo + d["selector"]
    if tipo == "clic":
        verbo = {"on": "Marca", "off": "Desmarca"}.get(d.get("marca"), "Pulsa")
        if COOKIES.search(nombre) or COOKIES.search(d["selector"]):
            return {"tipo": "clic", "selector": selector, "opcional": True,
                    "desc": f"{verbo} {cosa} (aviso de cookies: si no sale, se salta)"}
        return {"tipo": "clic", "selector": selector, "desc": f"{verbo} {cosa}",
                "guarda": bool(GUARDAN.match(nombre))}
    if tipo == "escribir":
        donde = f" en el campo «{nombre}»" if nombre else ""
        return {"tipo": "escribir", "selector": selector, "valor": d["valor"],
                "desc": f"Escribe «{d['valor']}»{donde}"}
    if tipo == "elegir":
        donde = f" en «{nombre}»" if nombre and len(nombre) < 40 else ""
        return {"tipo": "elegir", "selector": selector, "valor": d["valor"],
                "desc": f"Elige «{d.get('texto_opcion') or d['valor']}»{donde}"}
    return None


def _prefijo_marco(frame) -> str:
    """Las webs con marcos (muy típicas en aplicaciones de empresa): el camino hasta él."""
    partes = []
    while frame.parent_frame is not None:
        elemento = frame.frame_element()
        partes.insert(0, elemento.evaluate("e => window.playwright.selector(e)"))
        frame = frame.parent_frame
    return "".join(f"{p} >> internal:control=enter-frame >> " for p in partes)


def dominios_de(url: str, extra: list[str] | tuple[str, ...] = ()) -> list[str]:
    """La web, su dominio («app.empresa.com» -> «*.empresa.com») y los de Ajustes."""
    host = urlparse(url).hostname or ""
    salida = [host] if host else []
    trozos = host.split(".")
    if len(trozos) > 2 and not host.replace(".", "").isdigit():
        salida.append("*." + ".".join(trozos[-2:]))
    for d in extra:
        d = d.strip()
        if d and d not in salida:
            salida.append(d)
    return salida + SSO_SIEMPRE


def argumentos_navegador(url: str, extra: list[str] | tuple[str, ...] = ()) -> list[str]:
    dominios = ",".join(dominios_de(url, extra))
    # TRAMOYA_CDP: un puerto de depuración, para que las pruebas hagan clics de verdad
    cdp = os.environ.get("TRAMOYA_CDP")
    extra = [f"--remote-debugging-port={cdp}"] if cdp else []
    return [f"--auth-server-allowlist={dominios}", "--disable-features=Translate",
            "--lang=es-ES", *extra]


def grabar(carpeta: Path, url: str, perfil: Path, ajustes: dict | None = None) -> int:
    import os
    import queue
    import threading

    os.environ["PWDEBUG"] = "console"  # da window.playwright.selector() en cada página
    from playwright.sync_api import sync_playwright

    ajustes = ajustes or {}
    ancho, alto = TAMANOS.get(ajustes.get("tamano"), VIEWPORT)
    ordenes: queue.Queue[str] = queue.Queue()
    threading.Thread(target=lambda: [ordenes.put(x.strip()) for x in sys.stdin],
                     daemon=True).start()
    eventos: list[tuple] = []
    pasos: list[dict] = []
    avisos: set[str] = set()
    inicio = None
    titulo = ""
    with sync_playwright() as pw:
        ctx = pw.chromium.launch_persistent_context(
            str(perfil), headless=False, viewport={"width": ancho, "height": alto},
            ignore_https_errors=True, locale="es-ES",
            args=argumentos_navegador(url, ajustes.get("dominios", [])))
        ctx.set_default_navigation_timeout(60000)
        principal = ctx.pages[0] if ctx.pages else ctx.new_page()
        ctx.expose_binding("__tramoyaPaso", lambda fuente, d: eventos.append((fuente, d)))
        ctx.add_init_script(ESCUCHA_JS)
        with contextlib.suppress(Exception):  # un inicio de sesión lento no es un fallo
            principal.goto(url)
        emitir(estado="preparado")
        while True:
            try:
                principal.wait_for_timeout(150)
            except Exception:  # noqa: BLE001 - han cerrado la ventana
                break
            if principal.is_closed():
                break
            try:
                orden = ordenes.get_nowait()
            except queue.Empty:
                orden = ""
            if orden == "terminar":
                break
            if orden == "empezar" and inicio is None:
                inicio = principal.url
                try:
                    titulo = principal.title().strip()[:60]
                except Exception:  # noqa: BLE001
                    titulo = ""
                eventos.clear()
                emitir(estado="grabando", inicio=inicio)
            while eventos:
                fuente, d = eventos.pop(0)
                if inicio is None:
                    continue
                if fuente["page"] != principal:
                    avisos.add("Se abrió otra pestaña o ventana; lo que se hizo en ella no se "
                               "puede repetir.")
                    continue
                if d.get("tipo") == "clave":
                    avisos.add("No se ha apuntado ninguna contraseña.")
                    continue
                try:
                    prefijo = _prefijo_marco(fuente["frame"])
                except Exception:  # noqa: BLE001
                    prefijo = ""
                paso = _paso_de_evento(d, prefijo)
                if paso:
                    pasos.append(paso)
                    emitir(paso=paso["desc"], n=len(limpiar([dict(p) for p in pasos])))
        with contextlib.suppress(Exception):
            ctx.storage_state(path=str(carpeta / "auth.json"))
        with contextlib.suppress(Exception):
            ctx.close()
    limpios = limpiar(pasos)
    (carpeta / "grabacion.json").write_text(json.dumps(
        {"inicio": inicio, "titulo": titulo, "pasos": limpios, "avisos": sorted(avisos)},
        ensure_ascii=False,
        indent=1), encoding="utf-8")
    emitir(fin=True, n=len(limpios), inicio=inicio)
    return 0


# --- pasos -> escenas de tramoya ----------------------------------------------

def lineas_de_paso(p: dict) -> list[str]:
    t, s = p["tipo"], p.get("selector", "")
    if t == "clic":
        return [f"point {s}", "beat 0.8"] if p.get("seguro") else [f"click {s}", "beat 0.7"]
    if t == "señalar":
        return [f"point {s}", "beat 1"]
    if t == "escribir":
        return [f"clear {s}", f"type {s} | {p['valor']}", "beat 0.3"]
    if t == "tecla":
        return [f"press {p['valor']}", "beat 0.5"]
    if t == "elegir":
        return [f"select {s} | {p['valor']}", "beat 0.6"]
    if t == "ir":
        return [f"goto {p['valor']}", "beat 1"]
    return []


def segundos_lectura(texto: str) -> float:
    return round(max(2.5, len(texto.split()) / 2.6 + 1.2), 1)


def escenas(proyecto: dict) -> list[dict]:
    """Cada paso con explicación abre una escena; el texto sale en pantalla y es la voz."""
    lista: list[dict] = []
    pasos = [p for p in proyecto["pasos"] if not p.get("quitado")]
    if not pasos or not pasos[0].get("texto", "").strip():
        lista.append({"name": "inicio", "texto": proyecto["nombre"], "items": []})
    for p in pasos:
        texto = p.get("texto", "").strip()
        if texto or not lista:
            lista.append({"name": f"paso-{p['id']}", "texto": texto, "items": []})
        lista[-1]["items"].append(p)
    return lista


# --- la voz de Windows (SAPI) ---------------------------------------------------

def _es_espanol(descripcion: str) -> bool:
    d = descripcion.lower()
    return "spanish" in d or "español" in d or "espanol" in d


def voces() -> list[str]:
    """Las voces de Windows instaladas, las de español primero."""
    try:
        import comtypes.client as cc
        todas = cc.CreateObject("SAPI.SpVoice").GetVoices()
        nombres = [todas.Item(i).GetDescription() for i in range(todas.Count)]
    except Exception:  # noqa: BLE001 - sin Windows o sin SAPI: no hay voz
        return []
    return sorted(nombres, key=lambda n: not _es_espanol(n))


def _voz(nombre: str | None, velocidad: int = 0):
    import comtypes.client as cc
    voz = cc.CreateObject("SAPI.SpVoice")
    todas = voz.GetVoices()
    elegida = None
    for i in range(todas.Count):
        d = todas.Item(i).GetDescription()
        if d == nombre or (elegida is None and not nombre and _es_espanol(d)):
            elegida = todas.Item(i)
    if elegida is not None:
        voz.Voice = elegida
    voz.Rate = max(-10, min(10, int(velocidad)))
    return voz


def hablar(textos: dict[Path, str], nombre: str | None = None, velocidad: int = 0) -> None:
    import comtypes.client as cc
    voz = _voz(nombre, velocidad)
    for ruta, texto in textos.items():
        ruta.parent.mkdir(parents=True, exist_ok=True)
        flujo = cc.CreateObject("SAPI.SpFileStream")
        formato = cc.CreateObject("SAPI.SpAudioFormat")
        formato.Type = 39  # SAFT48kHz16BitMono
        flujo.Format = formato
        flujo.Open(str(ruta), 3)
        voz.AudioOutputStream = flujo
        voz.Speak(texto)
        flujo.Close()


def oir(texto: str, nombre: str | None = None, velocidad: int = 0) -> None:
    """Lee una frase por los altavoces, para probarla antes de hacer el vídeo."""
    _voz(nombre, velocidad).Speak(texto)


# --- subtítulos -----------------------------------------------------------------

def srt(tramos: list[tuple[str, float]]) -> str:
    """(texto, duración) por escena, en orden -> un .srt; las escenas sin texto no salen."""
    def marca(t: float) -> str:
        ms = round(t * 1000)
        return f"{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d},{ms % 1000:03d}"
    bloques, t, n = [], 0.0, 0
    for texto, dur in tramos:
        if texto.strip():
            n += 1
            bloques.append(f"{n}\n{marca(t)} --> {marca(t + dur)}\n{texto.strip()}\n")
        t += dur
    return "\n".join(bloques)


# --- grabar y montar ------------------------------------------------------------

def _duracion(_self, ruta: Path) -> float:
    salida = subprocess.run(["ffmpeg", "-hide_banner", "-i", str(ruta)], capture_output=True,
                            text=True, errors="replace").stderr
    m = re.search(r"Duration: (\d+):(\d+):([\d.]+)", salida)
    if not m:
        raise RuntimeError(f"no sé cuánto dura {ruta}")
    h, mi, s = m.groups()
    return int(h) * 3600 + int(mi) * 60 + float(s)


class SesionCaducada(Exception):
    pass


class FalloPaso(Exception):
    def __init__(self, paso: dict, causa: Exception) -> None:
        super().__init__(str(causa))
        self.paso, self.causa = paso, causa


def explicar_fallo(e: Exception) -> str:
    t = str(e)
    if "Timeout" in t or "timeout" in t or "not visible" in t:
        return "no lo he encontrado en la página (puede que la web haya cambiado o tarde más)"
    if "strict mode" in t:
        return "hay varios iguales en la página y no sé cuál es"
    return t.splitlines()[0][:200]


def hacer_video(carpeta: Path, con_voz: bool) -> int:
    from tramoya.assembly import assemble_marks
    from tramoya.director import _SilentClip
    from tramoya.ffmpeg import Runner
    from tramoya.marks import Marks
    from tramoya.narration import Narrator, wav_seconds
    from tramoya.pacing import Pacing
    from tramoya.profile import run_steps
    from tramoya.stage import open_stage

    Runner.probe_duration = _duracion
    proyecto = json.loads((carpeta / "proyecto.json").read_text(encoding="utf-8"))
    url = proyecto.get("inicio") or proyecto["url"]
    ajustes = proyecto.get("ajustes") or {}
    viewport = TAMANOS.get(ajustes.get("tamano"), VIEWPORT)
    # La reproducción, con los mismos permisos que la grabación: inicio de sesión de
    # Windows en las webs de la empresa y sus certificados internos.
    from playwright.sync_api import Browser, BrowserType
    lanzar, contexto = BrowserType.launch, Browser.new_context
    BrowserType.launch = lambda self, **kw: lanzar(
        self, **{**kw, "args": argumentos_navegador(url, ajustes.get("dominios", []))})
    Browser.new_context = lambda self, **kw: contexto(
        self, **{**kw, "ignore_https_errors": True, "locale": "es-ES"})
    lista = escenas(proyecto)
    out = carpeta / "out"
    shutil.rmtree(out, ignore_errors=True)
    voces = out / "voices" / "es"
    voces.mkdir(parents=True)

    if con_voz:
        emitir(fase="voz", msg="Preparando la voz…", p=0.05)
        hablar({voces / f"scene-{e['name']}.wav": e["texto"] for e in lista if e["texto"]},
               ajustes.get("voz"), ajustes.get("velocidad", 0))

    emitir(fase="grabar", msg="Grabando. No toques la ventana del navegador.", p=0.15)
    pacing = Pacing(pace=RITMOS.get(ajustes.get("ritmo"), 1.0), lang="es")
    narrador = Narrator(out / "voices", pacing,
                        player=lambda clip: _SilentClip(wav_seconds(clip), pacing))
    marks = Marks(path=out / "marks.json")
    auth = carpeta / "auth.json"
    total = sum(len(e["items"]) for e in lista) or 1
    hechos = 0
    with open_stage(url, pacing, marks, video_dir=out / "raw", viewport=viewport,
                    headless=False, storage_state=auth if auth.exists() else None) as stage:
        stage.page.set_default_timeout(15000)
        marks.t0 = time.time()
        take = Path(stage.page.video.path())
        with contextlib.suppress(Exception):
            stage.page.wait_for_load_state("networkidle", timeout=15000)
        stage.beat(1.0)
        if urlparse(stage.page.url).hostname != urlparse(url).hostname:
            raise SesionCaducada(stage.page.url)
        for e in lista:
            def say(fallback: float, key: str = f"scene-{e['name']}") -> None:
                narrador.say(key, fallback=fallback)

            with marks.scene(e["name"], e["texto"]):
                stage.caption(e["texto"])
                if e["texto"]:
                    run_steps(stage, [f"say {segundos_lectura(e['texto'])}"], say)
                for p in e["items"]:
                    try:
                        if p.get("opcional"):
                            stage.page.set_default_timeout(3000)
                        run_steps(stage, lineas_de_paso(p), say, base_url=url)
                    except Exception as exc:
                        if not p.get("opcional"):
                            raise FalloPaso(p, exc) from exc
                    finally:
                        stage.page.set_default_timeout(15000)
                    hechos += 1
                    emitir(fase="grabar", msg=f"Grabando paso {hechos} de {total}…",
                           p=0.15 + 0.6 * hechos / total)
                if e is lista[-1]:
                    stage.beat(2.0)
                stage.caption("")
    shutil.copyfile(take, out / "take.webm")

    emitir(fase="montar", msg="Montando el vídeo…", p=0.8)
    final = out / "video.mp4"
    tabla = assemble_marks(Runner(), out / "take.webm", out / "marks.json", voces, final,
                           lang="es")
    nombre = re.sub(r'[<>:"/\\|?*]+', "", proyecto["nombre"]).strip() or "vídeo"
    destino = Path(proyecto["mis_videos"]) / f"{nombre}.mp4"
    destino.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(final, destino)
    subtitulos = None
    if ajustes.get("srt", True):
        textos = {e["name"]: e["texto"] for e in lista}
        subtitulos = destino.with_suffix(".srt")
        subtitulos.write_text(srt([(textos.get(n, ""), dur) for n, _raw, dur in tabla]),
                              encoding="utf-8-sig")
    emitir(ok=True, video=str(destino), srt=str(subtitulos) if subtitulos else None, p=1.0)
    return 0


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    if sys.argv[1] == "pasos":
        pasos, avisos = pasos_de_grabacion(Path(sys.argv[2]).read_text(encoding="utf-8"))
        emitir(pasos=pasos, avisos=avisos)
        return 0
    if sys.argv[1] == "grabar":
        ajustes = json.loads(sys.argv[5]) if len(sys.argv) > 5 else {}
        return grabar(Path(sys.argv[2]), sys.argv[3], Path(sys.argv[4]), ajustes)
    if sys.argv[1] == "voces":
        emitir(voces=voces())
        return 0
    if sys.argv[1] == "oir":
        d = json.loads(sys.argv[2])
        oir(d["texto"], d.get("voz"), d.get("velocidad", 0))
        return 0
    try:
        return hacer_video(Path(sys.argv[2]), "--voz" in sys.argv)
    except FalloPaso as f:
        pista = ""
        try:
            ruta = Path(sys.argv[2]) / "proyecto.json"
            pasos = json.loads(ruta.read_text(encoding="utf-8"))["pasos"]
            antes = [p for p in pasos
                     if p["id"] < f.paso["id"] and p.get("seguro") and not p.get("quitado")]
            if antes:
                pista = (f" Puede ser porque el paso {antes[-1]['id']} está en «solo señalar» y, "
                         "al no pulsarse, la web no ha avanzado.")
        except (OSError, KeyError, json.JSONDecodeError):
            pass
        emitir(error=f"No he podido hacer el paso {f.paso['id']} ({f.paso['desc']}): "
                     f"{explicar_fallo(f.causa)}.{pista}", paso=f.paso["id"])
    except SesionCaducada:
        emitir(error="La web pide volver a entrar (la sesión ha caducado). Ve a «Grabar», "
                     "pulsa «Abrir la web», entra con tu usuario y cierra la ventana sin grabar; "
                     "luego vuelve aquí y haz el vídeo.", sesion=True)
    except Exception as exc:  # noqa: BLE001
        emitir(error=f"Algo ha fallado: {explicar_fallo(exc)}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
