"""Render the brand assets of the README from HTML, with the fonts of the window.

    python scripts/render_brand.py logos               # assets/logo-{light,dark,mark}.png
    python scripts/render_brand.py pipeline            # assets/pipeline.png
    python scripts/render_brand.py filmstrip [take marks]   # assets/filmstrip.png, one frame per scene
    python scripts/render_brand.py card "One request" out.png  # a 1280x720 stage card

The look is the window's: Solnítido's palette staged as a theatre (curtains, a
spotlight, boards, a batten with its counterweight), the poster type is Archivo
condensed and the cues are IBM Plex Mono, both read from src/tramoya/easy/ui/fonts.
Needs Playwright's Chromium; filmstrip also needs ffmpeg. Runs on any system.
"""
from __future__ import annotations

import base64
import html
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FONTS = ROOT / "src" / "tramoya" / "easy" / "ui" / "fonts"
ASSETS = ROOT / "assets"

ESCENARIO, TELON, TELON_2, FOCO, LUZ = "#0E2A47", "#16334E", "#245680", "#E9A80E", "#F0CB66"
TINTA, TABLAS, CIELO = "#1F1E1C", "#333230", "#CFE0EA"


def _font(name: str) -> str:
    return base64.b64encode((FONTS / name).read_bytes()).decode()


BASE_CSS = f"""
@font-face {{ font-family: Archivo; src: url(data:font/woff2;base64,{_font("Archivo-tramoya.woff2")}) format("woff2");
  font-weight: 400 900; font-stretch: 62% 100%; }}
@font-face {{ font-family: "IBM Plex Mono"; src: url(data:font/woff2;base64,{_font("IBMPlexMono-Medium.woff2")}) format("woff2"); font-weight: 500; }}
* {{ box-sizing: border-box; margin: 0; }}
body {{ background: transparent; font-family: Archivo, sans-serif; }}
.cartel {{ font-stretch: 66%; font-weight: 850; text-transform: uppercase; line-height: .86; letter-spacing: -.005em; }}
.cue {{ font: 500 15px/1.2 "IBM Plex Mono", monospace; letter-spacing: .14em; text-transform: uppercase; }}
.pliegues {{ background: repeating-linear-gradient(90deg, {TELON} 0 20px, {ESCENARIO} 20px 27px, {TELON_2} 27px 30px, {TELON} 30px 44px); }}
.feston {{ background: radial-gradient(circle at 50% 0, {TELON} 69%, transparent 70%) 0 0 / 52px 30px repeat-x; }}
.tablas {{ background: repeating-linear-gradient(90deg, {TABLAS} 0 138px, {TINTA} 138px 140px); border-top: 6px solid {TINTA}; }}
"""

MARK_SVG = (f'<svg width="{{w}}" height="{{h}}" viewBox="0 0 22 46" aria-hidden="true">'
            f'<rect x="10" y="0" width="2" height="28" fill="{{cuerda}}"/>'
            f'<rect x="3" y="26" width="16" height="16" fill="{FOCO}"/></svg>')


def _shoot(pages: list[tuple[str, Path, str | None, tuple[int, int], bool]]) -> None:
    """(html, out, selector, viewport, transparent) -> one PNG each, at 2x."""
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        for doc, out, sel, (w, h), transparent in pages:
            page = browser.new_page(viewport={"width": w, "height": h}, device_scale_factor=2)
            page.set_content(f"<!doctype html><meta charset=utf-8><style>{BASE_CSS}</style>{doc}")
            page.evaluate("document.fonts.ready")
            page.wait_for_timeout(300)
            target = page.locator(sel) if sel else page
            target.screenshot(path=str(out), omit_background=transparent)
            page.close()
            print("wrote", out.relative_to(ROOT) if out.is_relative_to(ROOT) else out)
        browser.close()


def logos() -> None:
    def lockup(fg: str, cuerda: str, tag: str) -> str:
        return f"""<div id=l style="display:inline-flex;align-items:flex-start;gap:26px;padding:24px 30px 26px">
          {MARK_SVG.format(w=58, h=122, cuerda=cuerda)}
          <div style="display:flex;flex-direction:column;gap:14px;padding-top:30px">
            <span class=cartel style="font-size:150px;color:{fg}">Tramoya</span>
            <span class=cue style="font-size:19px;letter-spacing:.32em;color:{tag}">The machinery behind the take</span>
          </div></div>"""
    mark = f"""<div id=m style="width:180px;height:180px;background:{TINTA};position:relative;overflow:hidden">
      <div style="position:absolute;left:0;right:0;top:0;height:12px;background:{TELON}"></div>
      <div class=feston style="position:absolute;left:0;right:0;top:12px;height:18px;background-size:30px 18px"></div>
      <div style="position:absolute;left:89px;top:0;width:3px;height:92px;background:#A5A4A0"></div>
      <div style="position:absolute;left:66px;top:88px;width:48px;height:48px;background:{FOCO}"></div>
      <div class=tablas style="position:absolute;left:0;right:0;bottom:0;height:26px;border-top-width:4px;background-size:auto"></div>
    </div>"""
    _shoot([
        (lockup(TINTA, "#8A8985", "#9E6E06"), ASSETS / "logo-light.png", "#l", (1100, 320), True),
        (lockup("#FFFFFF", "#A5A4A0", LUZ), ASSETS / "logo-dark.png", "#l", (1100, 320), True),
        (mark, ASSETS / "logo-mark.png", "#m", (180, 180), False),
    ])


def _caja(nombre: str, linea1: str, linea2: str, foco: bool = False) -> str:
    borde = FOCO if foco else "#54809C"
    fondo = "rgba(233,168,14,.12)" if foco else TELON
    return f"""<div style="flex:1;min-width:0;border:2px solid {borde};background:{fondo};padding:16px 16px 14px;display:flex;flex-direction:column;gap:6px">
      <span class=cartel style="font-size:44px;color:#fff">{nombre}</span>
      <span style="font-size:15px;color:{CIELO}">{linea1}</span>
      <span class=cue style="font-size:12px;letter-spacing:.06em;text-transform:none;color:#8FB5CC">{linea2}</span></div>"""


FLECHA = f'<span style="flex:none;align-self:center;color:{FOCO};font:700 28px Archivo">&rarr;</span>'


def pipeline() -> None:
    escena = "".join([
        _caja("easy · agent", "a person, or a model", "an easy project: steps + sentences", True), FLECHA,
        _caja("direct", "the profile on stage", "take.webm · marks.json · snapshots"), FLECHA,
        _caja("record", "or the screen itself", "take.mp4 + offset sidecar"), FLECHA,
        _caja("tts", "the script, voiced", "voices/&lt;lang&gt;/&lt;scene&gt;.wav"),
    ])
    cabina = "".join([
        _caja("assemble", "cut per scene, waits sped up", "demo.mp4 · the voice fitted", True), FLECHA,
        _caja("deck", "slides + narration", "deck.mp4"), FLECHA,
        _caja("captions", "SRT, ASS, burnt in", ".srt · .ass · video"), FLECHA,
        _caja("music", "a bed under the voice", "ducked mix"),
    ])
    doc = f"""<div id=p style="width:1500px;background:{ESCENARIO};position:relative;overflow:hidden;font-family:Archivo">
      <div style="position:absolute;left:0;right:0;top:0;height:18px;background:{TELON}"></div>
      <div class=feston style="position:absolute;left:0;right:0;top:18px;height:30px"></div>
      <div class=pliegues style="position:absolute;left:0;top:0;bottom:250px;width:70px;clip-path:polygon(0 0,100% 0,100% 62%,40% 100%,0 100%)"></div>
      <div class=pliegues style="position:absolute;right:0;top:0;bottom:250px;width:70px;clip-path:polygon(0 0,100% 0,100% 100%,60% 100%,0 62%)"></div>
      <div style="position:absolute;inset:0 0 250px 0;background:{FOCO};opacity:.10;clip-path:polygon(47% 0,53% 0,70% 100%,30% 100%)"></div>
      <div style="position:relative;padding:76px 100px 34px;display:flex;flex-direction:column;gap:18px">
        <span class=cue style="color:{LUZ}">On stage · something plays in a browser and is recorded</span>
        <div style="display:flex;gap:12px">{escena}</div></div>
      <div class=tablas style="position:relative;height:34px"></div>
      <div style="position:relative;background:{TINTA};padding:30px 100px 40px;display:flex;flex-direction:column;gap:18px">
        <span class=cue style="color:{LUZ}">In the booth · files in, files out; every ffmpeg call printable with --dry-run</span>
        <div style="display:flex;gap:12px">{cabina}</div></div></div>"""
    _shoot([(doc, ASSETS / "pipeline.png", "#p", (1500, 700), False)])


def card(texto: str, out: Path) -> None:
    doc = f"""<div id=c style="width:1280px;height:720px;background:{ESCENARIO};position:relative;overflow:hidden">
      <div style="position:absolute;left:0;right:0;top:0;height:24px;background:{TELON}"></div>
      <div class=feston style="position:absolute;left:0;right:0;top:24px;height:30px"></div>
      <div class=pliegues style="position:absolute;left:0;top:0;bottom:110px;width:15%;clip-path:polygon(0 0,100% 0,100% 58%,38% 100%,0 100%)"></div>
      <div class=pliegues style="position:absolute;right:0;top:0;bottom:110px;width:15%;clip-path:polygon(0 0,100% 0,100% 100%,62% 100%,0 58%)"></div>
      <div style="position:absolute;inset:0 0 110px 0;background:{FOCO};opacity:.16;clip-path:polygon(46% 0,54% 0,80% 100%,20% 100%)"></div>
      <div class=tablas style="position:absolute;left:0;right:0;bottom:0;height:110px"></div>
      <div style="position:absolute;left:50%;bottom:48px;transform:translateX(-50%);width:60%;height:64px;border-radius:50%;background:{LUZ};opacity:.5"></div>
      <div style="position:absolute;inset:0 0 110px 0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:20px">
        <span class=cartel style="font-size:120px;color:#fff">{html.escape(texto)}</span>
        <span class=cue style="color:{LUZ}">tramoya</span></div></div>"""
    _shoot([(doc, out, "#c", (1280, 720), False)])


def filmstrip(take: Path, marks_path: Path) -> None:
    ffmpeg = shutil.which("ffmpeg") or sys.exit("ffmpeg is not on PATH")
    marks = json.loads(marks_path.read_text(encoding="utf-8"))
    starts = {m["key"].split(":")[1]: (m["t"], m["label"]) for m in marks
              if m["key"].startswith("scene:") and m["key"].endswith(":start")}
    ends = {m["key"].split(":")[1]: m["t"] for m in marks
            if m["key"].startswith("scene:") and m["key"].endswith(":end")}
    celdas = []
    with tempfile.TemporaryDirectory() as tmp:
        for i, (name, (t0, label)) in enumerate(starts.items()):
            png = Path(tmp) / f"f{i}.png"
            subprocess.run([ffmpeg, "-loglevel", "error", "-y", "-ss", f"{(t0 + ends[name]) / 2:.2f}",
                            "-i", str(take), "-frames:v", "1", "-vf", "scale=640:-1", str(png)], check=True)
            data = base64.b64encode(png.read_bytes()).decode()
            celdas.append(f"""<figure style="display:flex;flex-direction:column;background:{TINTA}">
              <img src="data:image/png;base64,{data}" style="display:block;width:100%">
              <figcaption style="display:flex;align-items:baseline;gap:14px;padding:12px 14px;border-top:4px solid {FOCO}">
                <span class=cartel style="font-size:34px;color:{FOCO}">{i + 1:02d}</span>
                <span style="display:flex;flex-direction:column;gap:3px">
                  <span class=cue style="font-size:12px;color:#8FB5CC">{html.escape(name)}</span>
                  <span style="font-size:18px;font-weight:600;color:#fff">{html.escape(label)}</span></span>
              </figcaption></figure>""")
        doc = f"""<div id=f style="width:2040px;background:{ESCENARIO};padding:24px;display:grid;grid-template-columns:repeat(3,1fr);gap:20px">
          {"".join(celdas)}</div>"""
        _shoot([(doc, ASSETS / "filmstrip.png", "#f", (2040, 1000), False)])


def main(argv: list[str]) -> int:
    what = argv[0] if argv else "logos"
    if what == "logos":
        logos()
    elif what == "pipeline":
        pipeline()
    elif what == "card":
        card(argv[1], Path(argv[2]))
    elif what == "filmstrip":
        demo = ROOT / "examples" / "notes-app" / "demo"
        take = Path(argv[1]) if len(argv) > 1 else demo / "take.webm"
        marks = Path(argv[2]) if len(argv) > 2 else demo / "marks.json"
        filmstrip(take, marks)
    elif what == "all":
        logos()
        pipeline()
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
