"""Build the portable Windows kit of `tramoya easy`: one folder, double-click,
nothing to install, nothing downloaded on the target machine.

    python scripts/build_easy_kit.py            # on Windows, with internet
    -> dist/Tramoya-<version>-windows.zip       (~300 MB)

Inside: a relocatable CPython (python-build-standalone) with tramoya[easy]
installed from this checkout, Playwright's Chromium in app/navegador, an
ffmpeg in app/ffmpeg, the launcher «Abrir Tramoya.bat» and LÉEME.txt. On first
start the app adds a «Tramoya» shortcut with its icon next to the launcher.
Corporate laptops are the point: no admin rights, no installer, and no
download at run time for a proxy to cut.
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
PBS_TAG = "20261026"
PY_VERSION = "3.12.16"
PBS_URL = ("https://github.com/astral-sh/python-build-standalone/releases/download/"
           f"{PBS_TAG}/cpython-{PY_VERSION}%2B{PBS_TAG}-x86_64-pc-windows-msvc-install_only.tar.gz")

LANZADOR = '@echo off\r\nrem Tramoya: abre la app. No hace falta instalar nada.\r\n' \
           'start "" "%~dp0app\\python\\pythonw.exe" -m tramoya.easy\r\n'

LEEME = """TRAMOYA: vídeos de procesos, sin esfuerzo
=========================================

1. Descomprime esta carpeta donde quieras (por ejemplo, en Documentos).
2. Doble clic en «Abrir Tramoya». La primera vez aparece también un acceso
   directo «Tramoya» con su icono; desde la app puedes ponerlo en el escritorio.

No hay que instalar nada ni pedir permisos: todo va dentro de la carpeta.

Cómo se hace un vídeo
---------------------
1. «Nuevo vídeo»: le pones nombre y pegas la dirección de la web.
2. «Abrir la web»: entras como siempre y vas a donde empieza el proceso.
   Pulsas «Empezar a grabar», haces el proceso una vez y «Ya he terminado».
3. Escribes una frase en los pasos que quieras explicar. Puedes escucharla,
   cambiar lo que se escribe en cada campo o quitar los pasos que sobren.
4. «Hacer el vídeo»: Tramoya repite el proceso sola y graba el vídeo, con la
   voz de Windows si la activas. Mientras tanto, no toques esa ventana.

Los vídeos terminados (y sus subtítulos .srt) quedan en «Mis vídeos».
En «Ajustes»: la voz y su velocidad, el ritmo y el tamaño del vídeo.

Bueno saber
-----------
- Tu contraseña nunca se graba: no se apunta nada hasta que pulsas «Empezar a
  grabar». La web recuerda la sesión como un navegador normal.
- Al repetir el proceso, el último botón que guarda (Guardar, Enviar…) solo se
  señala con el cursor, sin pulsarlo, para no guardar dos veces. En cada botón
  puedes cambiarlo con «Solo señalar, sin pulsar».
- Si un paso falla (la web ha cambiado, por ejemplo), Tramoya te dice cuál.
  Puedes quitarlo o volver a grabar; las frases que ya escribiste se conservan.
"""


def run(*argv: str | Path, env: dict | None = None) -> None:
    print("+", " ".join(str(a) for a in argv), flush=True)
    subprocess.run([str(a) for a in argv], check=True, env=env)


def version() -> str:
    for line in (REPO / "pyproject.toml").read_text(encoding="utf-8").splitlines():
        if line.startswith("version = "):
            return line.split('"')[1]
    raise RuntimeError("no version in pyproject.toml")


def descargar(url: str, destino: Path) -> Path:
    if not destino.exists():
        print(f"downloading {url}", flush=True)
        tmp = destino.with_suffix(".part")
        with urllib.request.urlopen(url) as r, tmp.open("wb") as f:
            shutil.copyfileobj(r, f)
        tmp.replace(destino)
    return destino


def construir(out: Path) -> Path:
    if sys.platform != "win32":
        raise SystemExit("build the Windows kit on Windows (Chromium and SAPI are Windows builds)")
    cache = out / "cache"
    cache.mkdir(parents=True, exist_ok=True)
    kit = out / "Tramoya"
    shutil.rmtree(kit, ignore_errors=True)
    app = kit / "app"
    app.mkdir(parents=True)

    with tarfile.open(descargar(PBS_URL, cache / f"cpython-{PY_VERSION}-{PBS_TAG}.tar.gz")) as t:
        t.extractall(app, filter="data")
    py = app / "python" / "python.exe"
    run(py, "-m", "pip", "install", "--no-warn-script-location", "--disable-pip-version-check",
        f"{REPO}[easy]")

    env = dict(os.environ, PLAYWRIGHT_BROWSERS_PATH=str(app / "navegador"))
    run(py, "-m", "playwright", "install", "chromium", "--no-shell", env=env)
    run(py, "-m", "playwright", "install", "ffmpeg", env=env)  # the browser's own, for the take

    imgff = cache / "imageio-ffmpeg"
    if not imgff.exists():
        run(py, "-m", "pip", "install", "--target", imgff, "--disable-pip-version-check",
            "imageio-ffmpeg")
    exe = next((imgff / "imageio_ffmpeg" / "binaries").glob("ffmpeg-*.exe"))
    (app / "ffmpeg").mkdir()
    shutil.copyfile(exe, app / "ffmpeg" / "ffmpeg.exe")

    for basura in [*app.rglob("*.pdb"), *app.rglob("__pycache__")]:
        shutil.rmtree(basura) if basura.is_dir() else basura.unlink()
    (kit / "Abrir Tramoya.bat").write_bytes(LANZADOR.encode("ascii"))
    (kit / "LÉEME.txt").write_text(LEEME.replace("\n", "\r\n"), encoding="utf-8-sig",
                                   newline="")

    zip_path = out / f"Tramoya-{version()}-windows.zip"
    zip_path.unlink(missing_ok=True)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for f in sorted(kit.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(out).as_posix())
    print(f"kit: {kit}\nzip: {zip_path} ({zip_path.stat().st_size / 1e6:.0f} MB)")
    return zip_path


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", default=str(REPO / "dist"), help="build folder (default: dist/)")
    construir(Path(ap.parse_args().out).resolve())
    return 0


if __name__ == "__main__":
    sys.exit(main())
