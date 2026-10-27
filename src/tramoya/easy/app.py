"""`tramoya easy`: the point-and-click app, in Spanish, for people who never open
a terminal.

A stdlib HTTP server on 127.0.0.1 serves `ui/index.html` and opens it in an
app window of Playwright's Chromium; closing the window stops everything. The
heavy lifting runs in child processes of `tramoya.easy.engine` (record, render),
so a crash in a browser never takes the window down.

Two layouts. Installed with pip, data goes to ~/Tramoya. Inside the portable
Windows kit (`scripts/build-easy-kit.py`), the launcher sets TRAMOYA_KIT and
everything stays in the kit folder: Python, Chromium, ffmpeg, the sessions and
the finished videos in «Mis vídeos».
"""
from __future__ import annotations

import contextlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time
import unicodedata
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def _kit() -> Path | None:
    """The portable kit: <kit>/app/python/python(w).exe, or TRAMOYA_KIT."""
    if os.environ.get("TRAMOYA_KIT"):
        return Path(os.environ["TRAMOYA_KIT"]).resolve()
    exe = Path(sys.executable).resolve()
    if exe.parent.name == "python" and (exe.parent.parent / "navegador").is_dir():
        return exe.parent.parent.parent
    return None


KIT = _kit()
CASA = KIT or Path.home() / "Tramoya"
DATOS = (KIT / "app" / "datos") if KIT else CASA / ".datos"
MIS_VIDEOS = CASA / "Mis vídeos"
UI = Path(__file__).resolve().parent / "ui" / "index.html"
ICONO = Path(__file__).resolve().parent / "ui" / "tramoya.ico"
AJUSTES = DATOS / "ajustes.json"
AJUSTES_DEFECTO = {"voz": "", "velocidad": 0, "ritmo": "normal", "tamano": "800p", "srt": True,
                   "dominios": []}
# pythonw (the kit launcher, no console) for the window; python for the workers
PY = Path(sys.executable).with_name(Path(sys.executable).name.replace("pythonw", "python"))
MOTOR = [str(PY), "-m", "tramoya.easy.engine"]
SIN_VENTANA = getattr(subprocess, "CREATE_NO_WINDOW", 0)

ENTORNO = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
if KIT:
    ENTORNO["PLAYWRIGHT_BROWSERS_PATH"] = str(KIT / "app" / "navegador")
    ENTORNO["PATH"] = str(KIT / "app" / "ffmpeg") + os.pathsep + os.environ.get("PATH", "")
    os.environ.update(PLAYWRIGHT_BROWSERS_PATH=ENTORNO["PLAYWRIGHT_BROWSERS_PATH"],
                      PATH=ENTORNO["PATH"])


def abrir_archivo(ruta: Path) -> None:
    if sys.platform == "win32":
        os.startfile(ruta)
    else:
        subprocess.Popen(["open" if sys.platform == "darwin" else "xdg-open", str(ruta)])


def chromium() -> str:
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        return pw.chromium.executable_path

# id -> {"estado", "msg", "p", "proc", ...}; un trabajo a la vez por vídeo
TAREAS: dict[str, dict] = {}
CERROJO = threading.Lock()


def slug(texto: str) -> str:
    t = unicodedata.normalize("NFKD", texto).encode("ascii", "ignore").decode().lower()
    return re.sub(r"[^a-z0-9]+", "-", t).strip("-")[:40] or "video"


def leer(pid: str) -> dict:
    return json.loads((DATOS / pid / "proyecto.json").read_text(encoding="utf-8"))


def guardar(pid: str, datos: dict) -> None:
    ruta = DATOS / pid / "proyecto.json"
    tmp = ruta.with_suffix(".tmp")
    tmp.write_text(json.dumps(datos, ensure_ascii=False, indent=1), encoding="utf-8")
    tmp.replace(ruta)


def leer_ajustes() -> dict:
    try:
        guardados = json.loads(AJUSTES.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        guardados = {}
    return {**AJUSTES_DEFECTO, **{k: v for k, v in guardados.items() if k in AJUSTES_DEFECTO}}


def guardar_ajustes(nuevos: dict) -> dict:
    a = leer_ajustes()
    for k, v in nuevos.items():
        if k in AJUSTES_DEFECTO:
            a[k] = v
    if isinstance(a["dominios"], str):
        a["dominios"] = [d.strip() for d in re.split(r"[,\s]+", a["dominios"]) if d.strip()]
    AJUSTES.write_text(json.dumps(a, ensure_ascii=False, indent=1), encoding="utf-8")
    return a


def tarea(pid: str) -> dict:
    t = TAREAS.get(pid) or {"estado": "quieto"}
    return {k: v for k, v in t.items() if k != "proc"}


def lanzar(pid: str, argv: list[str], **kw) -> subprocess.Popen:
    return subprocess.Popen(argv, cwd=DATOS / pid, env=ENTORNO, creationflags=SIN_VENTANA,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            encoding="utf-8", errors="replace", **kw)


# --- grabar: una ventana con la web; se apunta desde que se pulsa «Empezar» ------

PERFIL_WEB = DATOS / "_navegador"  # sesiones de las webs, como en un navegador normal


def abrir_web(pid: str) -> None:
    p = leer(pid)
    argv = [*MOTOR, "grabar", str(DATOS / pid), p["url"], str(PERFIL_WEB),
            json.dumps(leer_ajustes())]
    proc = lanzar(pid, argv, stdin=subprocess.PIPE)
    TAREAS[pid] = {"estado": "abriendo", "proc": proc}

    def leer_salida() -> None:
        fin: dict = {}
        for linea in proc.stdout:
            try:
                d = json.loads(linea)
            except json.JSONDecodeError:
                continue
            t = TAREAS.get(pid)
            if not t or t.get("proc") is not proc:
                continue
            if d.get("estado") in ("preparado", "grabando"):
                t["estado"] = d["estado"]
            if "paso" in d:
                t.update(ultimo=d["paso"], n=d["n"])
            if d.get("fin"):
                fin = d
        proc.wait()
        if TAREAS.get(pid, {}).get("proc") is not proc:
            return
        if fin.get("inicio") is None:
            TAREAS[pid] = {"estado": "quieto"}  # cerró la ventana sin empezar
            return
        convertir(pid)
    threading.Thread(target=leer_salida, daemon=True).start()


def orden(pid: str, texto: str) -> None:
    t = TAREAS.get(pid, {})
    proc = t.get("proc")
    if proc is None or proc.poll() is not None:
        return
    if texto == "terminar":
        t["estado"] = "convirtiendo"
    try:
        proc.stdin.write(texto + "\n")
        proc.stdin.flush()
    except OSError:
        pass


def convertir(pid: str) -> None:
    ruta = DATOS / pid / "grabacion.json"
    try:
        res = json.loads(ruta.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        TAREAS[pid] = {"estado": "error", "msg": "No he podido leer la grabación."}
        return
    if not res["pasos"]:
        TAREAS[pid] = {"estado": "error", "msg": "No se ha apuntado ningún paso. Pulsa «Empezar "
                       "a grabar» y luego haz los clics en la ventana de la web."}
        return
    p = leer(pid)
    viejos = {x["desc"]: x.get("texto", "") for x in p.get("pasos", [])}
    for x in res["pasos"]:  # al regrabar, se conservan los textos de los pasos iguales
        x["texto"] = viejos.get(x["desc"], "")
    p.update(pasos=res["pasos"], avisos=res["avisos"], inicio=res["inicio"], fase="explicar",
             video=None, titulo_web=res.get("titulo") or p.get("titulo_web"))
    guardar(pid, p)
    TAREAS[pid] = {"estado": "quieto"}


# --- hacer el vídeo -----------------------------------------------------------

def hacer_video(pid: str, voz: bool) -> None:
    p = leer(pid)
    p["voz"] = voz
    p["mis_videos"] = str(MIS_VIDEOS)
    p["ajustes"] = leer_ajustes()
    guardar(pid, p)
    argv = MOTOR + ["video", str(DATOS / pid)] + (["--voz"] if voz else [])
    proc = lanzar(pid, argv)
    TAREAS[pid] = {"estado": "haciendo", "msg": "Empezando…", "p": 0.02, "proc": proc}

    def leer_salida() -> None:
        fin: dict = {}
        cola: list[str] = []
        for linea in proc.stdout:
            linea = linea.strip()
            try:
                d = json.loads(linea)
            except json.JSONDecodeError:
                cola = ([*cola, linea])[-20:]
                continue
            if "msg" in d:
                TAREAS[pid].update(msg=d["msg"], p=d.get("p", 0))
            if "ok" in d or "error" in d:
                fin = d
        proc.wait()
        if TAREAS.get(pid, {}).get("proc") is not proc:
            return
        if fin.get("ok"):
            q = leer(pid)
            q.update(video=fin["video"], srt=fin.get("srt"), fase="video",
                     hecho=time.strftime("%d-%m-%Y %H:%M"))
            guardar(pid, q)
            TAREAS[pid] = {"estado": "listo"}
        else:
            (DATOS / pid / "ultimo-error.txt").write_text("\n".join(cola), encoding="utf-8")
            TAREAS[pid] = {"estado": "error", "paso": fin.get("paso"),
                           "msg": fin.get("error") or "El vídeo no se ha podido hacer."}
    threading.Thread(target=leer_salida, daemon=True).start()


def cancelar(pid: str) -> None:
    t = TAREAS.pop(pid, None)
    if t and t.get("proc") and t["proc"].poll() is None:
        t["proc"].kill()


OYENDO: list[subprocess.Popen] = []


def oir(texto: str) -> None:
    for proc in OYENDO:
        if proc.poll() is None:
            proc.kill()
    OYENDO.clear()
    a = leer_ajustes()
    OYENDO.append(subprocess.Popen(
        [*MOTOR, "oir", json.dumps({"texto": texto, "voz": a["voz"], "velocidad": a["velocidad"]})],
        env=ENTORNO, creationflags=SIN_VENTANA))


def duplicar(pid: str) -> str:
    p = leer(pid)
    nombre = f"{p['nombre']} (copia)"
    base, n = slug(nombre), 2
    nuevo = base
    while (DATOS / nuevo).exists():
        nuevo, n = f"{base}-{n}", n + 1
    shutil.copytree(DATOS / pid, DATOS / nuevo, ignore=shutil.ignore_patterns("out", "*.txt"))
    p.update(id=nuevo, nombre=nombre, creado=time.time(), video=None, srt=None,
             fase="explicar" if p.get("pasos") else "grabar")
    guardar(nuevo, p)
    return nuevo


def escritorio() -> Path | None:
    if sys.platform != "win32":
        return None
    try:
        import comtypes.client as cc
        return Path(cc.CreateObject("WScript.Shell", dynamic=True).SpecialFolders("Desktop"))
    except Exception:  # noqa: BLE001
        return Path.home() / "Desktop"


def crear_acceso(carpeta: Path) -> Path:
    """Un acceso directo «Tramoya» con su icono, que abre la app sin consola."""
    import comtypes.client as cc
    pythonw = PY.with_name("pythonw.exe")
    destino = carpeta / "Tramoya.lnk"
    lnk = cc.CreateObject("WScript.Shell", dynamic=True).CreateShortcut(str(destino))
    lnk.TargetPath = str(pythonw if pythonw.exists() else PY)
    lnk.Arguments = "-m tramoya.easy"
    lnk.WorkingDirectory = str(CASA)
    lnk.Description = "Tramoya: vídeos de procesos, sin esfuerzo"
    if ICONO.exists():
        lnk.IconLocation = f"{ICONO},0"
    lnk.Save()
    return destino


# --- HTTP -----------------------------------------------------------------------

VOCES: list[str] | None = None


def voces() -> list[str]:
    global VOCES
    if VOCES is None:
        r = subprocess.run([*MOTOR, "voces"], env=ENTORNO,
                           capture_output=True, text=True, encoding="utf-8",
                           creationflags=SIN_VENTANA)
        try:
            VOCES = json.loads(r.stdout.strip().splitlines()[-1])["voces"]
        except (IndexError, json.JSONDecodeError, KeyError):
            VOCES = []
    return VOCES


def lista() -> list[dict]:
    salida = []
    for d in DATOS.glob("*/proyecto.json"):
        try:
            p = json.loads(d.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        salida.append({"id": p["id"], "nombre": p["nombre"], "fase": p.get("fase"),
                       "creado": p.get("creado", 0), "video": bool(p.get("video")),
                       "tarea": tarea(p["id"])["estado"]})
    return sorted(salida, key=lambda x: -x["creado"])


def webs() -> list[dict]:
    """Las webs de los vídeos anteriores, la más reciente primero."""
    vistas, salida = set(), []
    proyectos = []
    for d in DATOS.glob("*/proyecto.json"):
        try:
            proyectos.append(json.loads(d.read_text(encoding="utf-8")))
        except (OSError, json.JSONDecodeError):
            continue
    for p in sorted(proyectos, key=lambda x: -x.get("creado", 0)):
        url = p["url"]
        if url in vistas:
            continue
        vistas.add(url)
        salida.append({"url": url, "nombre": p.get("titulo_web") or nombre_web(url)})
    return salida[:6]


def nombre_web(url: str) -> str:
    from urllib.parse import urlparse
    u = urlparse(url)
    return (u.hostname or url).removeprefix("www.")


class H(BaseHTTPRequestHandler):
    def responder(self, datos, code: int = 200) -> None:
        cuerpo = json.dumps(datos, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(cuerpo)

    def do_GET(self) -> None:
        ruta = self.path.split("?", 1)[0]
        if ruta in ("/", "/index.html"):
            cuerpo = UI.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(cuerpo)
            return
        if ruta == "/api/inicio":
            esc = escritorio()
            return self.responder({
                "proyectos": lista(), "voces": voces(), "webs": webs(), "ajustes": leer_ajustes(),
                "windows": sys.platform == "win32", "carpeta": str(MIS_VIDEOS),
                "acceso": bool(esc and (esc / "Tramoya.lnk").exists()),
                # Pedírselo a la IA solo existe en la versión de Windows: aquí se oculta.
                "agente": False})
        if m := re.fullmatch(r"/api/p/([a-z0-9-]+)", ruta):
            if not (DATOS / m[1] / "proyecto.json").exists():
                return self.responder({"error": "no existe"}, 404)
            p = leer(m[1])
            p["tarea"] = tarea(m[1])
            return self.responder(p)
        if m := re.fullmatch(r"/video/([a-z0-9-]+)\.mp4", ruta):
            return self.video(m[1])
        self.send_error(404)

    def video(self, pid: str) -> None:
        p = leer(pid)
        ruta = Path(p.get("video") or "")
        if not ruta.is_file():
            return self.send_error(404)
        total = ruta.stat().st_size
        ini, fin = 0, total - 1
        rango = self.headers.get("Range")
        if rango and (m := re.match(r"bytes=(\d*)-(\d*)", rango)):
            ini = int(m[1] or 0)
            fin = int(m[2]) if m[2] else total - 1
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {ini}-{fin}/{total}")
        else:
            self.send_response(200)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(fin - ini + 1))
        self.end_headers()
        with ruta.open("rb") as f:
            f.seek(ini)
            quedan = fin - ini + 1
            try:
                while quedan > 0:
                    trozo = f.read(min(1 << 16, quedan))
                    if not trozo:
                        break
                    self.wfile.write(trozo)
                    quedan -= len(trozo)
            except (BrokenPipeError, ConnectionResetError):
                pass

    def do_POST(self) -> None:
        largo = int(self.headers.get("Content-Length") or 0)
        cuerpo = json.loads(self.rfile.read(largo) or b"{}")
        ruta = self.path
        if ruta == "/api/ajustes":
            return self.responder(guardar_ajustes(cuerpo))
        if ruta == "/api/oir":
            texto = (cuerpo.get("texto") or "").strip()
            if texto:
                oir(texto[:500])
            return self.responder({"ok": True})
        if ruta == "/api/acceso":
            try:
                return self.responder({"ok": True, "ruta": str(crear_acceso(escritorio()))})
            except Exception as exc:  # noqa: BLE001
                return self.responder({"error": f"No he podido crear el acceso directo ({exc})"})
        if ruta == "/api/crear":
            nombre = (cuerpo.get("nombre") or "").strip() or "Mi vídeo"
            url = (cuerpo.get("url") or "").strip()
            if not url:
                return self.responder({"error": "Falta la dirección de la web"}, 400)
            if not re.match(r"https?://", url):
                url = "https://" + url
            base, n = slug(nombre), 2
            pid = base
            while (DATOS / pid).exists():
                pid, n = f"{base}-{n}", n + 1
            (DATOS / pid).mkdir(parents=True)
            guardar(pid, {"id": pid, "nombre": nombre, "url": url, "pasos": [],
                          "fase": "grabar", "creado": time.time()})
            return self.responder({"id": pid})
        if ruta == "/api/carpeta":
            MIS_VIDEOS.mkdir(exist_ok=True)
            abrir_archivo(MIS_VIDEOS)
            return self.responder({"ok": True})
        m = re.fullmatch(r"/api/p/([a-z0-9-]+)/([a-z-]+)", ruta)
        if not m or not (DATOS / m[1] / "proyecto.json").exists():
            return self.responder({"error": "no existe"}, 404)
        pid, accion = m[1], m[2]
        with CERROJO:
            if accion == "abrir-web":
                cancelar(pid)
                abrir_web(pid)
            elif accion in ("empezar", "terminar"):
                orden(pid, accion)
            elif accion == "cancelar":
                cancelar(pid)
            elif accion == "guardar":
                p = leer(pid)
                for clave in ("nombre", "url", "pasos", "fase"):
                    if clave in cuerpo:
                        p[clave] = cuerpo[clave]
                guardar(pid, p)
            elif accion == "video":
                cancelar(pid)
                hacer_video(pid, bool(cuerpo.get("voz")))
            elif accion == "abrir":
                p = leer(pid)
                if cuerpo.get("que") == "carpeta" or not p.get("video"):
                    MIS_VIDEOS.mkdir(exist_ok=True)
                    if p.get("video") and Path(p["video"]).exists() and sys.platform == "win32":
                        subprocess.Popen(["explorer", "/select,", p["video"]])
                    else:
                        abrir_archivo(MIS_VIDEOS)
                else:
                    abrir_archivo(Path(p["video"]))
            elif accion == "duplicar":
                return self.responder({"ok": True, "id": duplicar(pid)})
            elif accion == "borrar":
                cancelar(pid)
                shutil.rmtree(DATOS / pid, ignore_errors=True)
            else:
                return self.responder({"error": "acción desconocida"}, 400)
        return self.responder({"ok": True})

    def log_message(self, *a) -> None:
        pass


def puerto_libre() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    DATOS.mkdir(parents=True, exist_ok=True)
    MIS_VIDEOS.mkdir(parents=True, exist_ok=True)
    puerto = puerto_libre()
    servidor = ThreadingHTTPServer(("127.0.0.1", puerto), H)
    threading.Thread(target=servidor.serve_forever, daemon=True).start()
    threading.Thread(target=voces, daemon=True).start()
    if KIT and sys.platform == "win32" and not (KIT / "Tramoya.lnk").exists():
        with contextlib.suppress(Exception):  # en el kit, un acceso directo con icono
            crear_acceso(KIT)
    url = f"http://127.0.0.1:{puerto}/"
    if "--no-window" in argv or "--sin-ventana" in argv:
        print(url, flush=True)
        threading.Event().wait()
    try:
        navegador = chromium()
    except Exception as exc:  # noqa: BLE001
        print(f"tramoya easy needs Playwright's Chromium: python -m playwright install chromium "
              f"({exc})", file=sys.stderr)
        return 2
    ventana = subprocess.Popen([
        navegador, f"--app={url}", f"--user-data-dir={DATOS / '_ventana'}",
        "--window-size=1180,820", "--no-first-run", "--no-default-browser-check",
        "--disable-features=Translate", "--lang=es",
    ])
    ventana.wait()
    for pid in list(TAREAS):
        cancelar(pid)
    servidor.shutdown()
    return 0


if __name__ == "__main__":
    sys.exit(main())
