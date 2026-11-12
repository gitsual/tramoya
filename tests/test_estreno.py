"""The premiere: the take on a stage. The plan fits it in a length, the page data carries every word it shows,
and the easy settings choose the finish, the format and the length."""

from __future__ import annotations

from datetime import date

import pytest

from tramoya import estreno
from tramoya.easy.engine import VERTICAL, de_estreno, duracion_maxima, srt, viewport_de

ESCENAS = [("Open the list", 8.0), ("Write a note", 12.0), ("Mark it done", 10.0)]


def test_without_a_length_the_take_plays_as_it_is():
    p = estreno.plan(ESCENAS, 30.0)
    assert p.vel == 1 and p.cabe
    assert [e["ini"] for e in p.escenas] == [0, 8000, 20000]
    assert p.total_ms == estreno.INTRO_MS + 30_000 + estreno.OUTRO_MS


def test_a_length_speeds_the_take_up_to_fit():
    p = estreno.plan(ESCENAS, 30.0, duracion_max=30)
    assert p.cabe and 1 < p.vel <= estreno.VEL_MAX
    assert (p.intro_ms, p.outro_ms) == (estreno.INTRO_CORTA_MS, estreno.OUTRO_CORTA_MS)   # short film, short cards
    assert abs(p.total_ms - 30_000) <= 1
    assert abs(sum(e["dur"] for e in p.escenas) - 30_000 / p.vel) <= 3


def test_too_short_says_so_and_stays_at_the_top_speed():
    p = estreno.plan(ESCENAS, 30.0, duracion_max=15)
    assert not p.cabe and p.vel == estreno.VEL_MAX
    assert p.total_ms > 15_000


def test_a_length_longer_than_the_take_changes_nothing():
    assert estreno.plan(ESCENAS, 30.0, duracion_max=120).vel == 1


def test_the_page_data_carries_the_words_and_the_canvas():
    p = estreno.plan(ESCENAS, 30.0)
    d = estreno.datos(p, titulo="Notes", lugar="notes.local", formato="vertical", frames=900,
                      video=(768, 960), lang="es", hoy=date(2026, 11, 12))
    assert (d["ancho"], d["alto"], d["video"]) == (1080, 1920, {"w": 768, "h": 960})
    assert d["fecha"] == "12 de noviembre de 2026" and d["fecha_corta"] == "12 NOV 2026"
    assert d["t"]["presenta"] == "Tramoya presenta"
    en = estreno.datos(p, titulo="Notes", lugar="", formato="horizontal", frames=900, video=(1280, 800),
                       lang="en", hoy=date(2026, 11, 12))
    assert en["fecha"] == "November 12, 2026" and en["t"]["fin"] == "The end"


def test_the_subtitles_follow_the_film_clock():
    p = estreno.plan(ESCENAS, 30.0)
    texto = srt(estreno.subtitulos(p))
    # The first headline starts when the curtain has opened.
    assert "00:00:03,200 --> 00:00:11,200\nOpen the list" in texto


def test_the_player_ships_with_its_typefaces(tmp_path):
    estreno._sala(tmp_path)
    assert "window.seek = seek" in (tmp_path / "sala.html").read_text(encoding="utf-8")
    assert (tmp_path / "fonts" / "Archivo-tramoya.woff2").stat().st_size > 10_000


def test_an_unknown_format_is_refused(tmp_path):
    with pytest.raises(ValueError):
        estreno.estrenar(tmp_path / "x.mp4", ESCENAS, tmp_path / "y.mp4", titulo="x", formato="square")


def test_easy_settings():
    assert de_estreno({}) and not de_estreno({"acabado": "bruto"})
    assert viewport_de({"formato": "vertical", "tamano": "1080p"}) == VERTICAL
    assert viewport_de({"tamano": "720p"}) == (1280, 720)
    assert duracion_maxima({"duracion": "60"}) == 60 and duracion_maxima({"duracion": "x"}) == 0


def test_the_sound_graph():
    p = estreno.plan(ESCENAS, 30.0)
    assert estreno.filtro_sonido(p, voz=False, musica=False) == ""
    solo_voz = estreno.filtro_sonido(p, voz=True, musica=False)
    assert solo_voz.startswith("[1:a]aresample=48000") and "adelay=3200" in solo_voz and "[2:a]" not in solo_voz
    con_todo = estreno.filtro_sonido(p, voz=True, musica=True)
    assert "sidechaincompress" in con_todo and "amix=inputs=2" in con_todo and f"volume={estreno.MUSICA_VOLUMEN}" in con_todo
    assert f"volume={estreno.MUSICA_SOLA}" in estreno.filtro_sonido(p, voz=False, musica=True)


def test_the_lobby_loop_ships_with_tramoya():
    assert estreno.ascensor().stat().st_size > 500_000


def test_the_music_setting(tmp_path):
    from tramoya.easy.engine import musica_de, pistas
    (tmp_path / "Café Bossa Nova A.mp3").write_bytes(b"x")
    assert list(pistas(tmp_path)) == ["Café Bossa Nova A"]
    assert musica_de({}, tmp_path) == estreno.ascensor()
    assert musica_de({"musica": "ninguna"}, tmp_path) is None
    assert musica_de({"musica": "Café Bossa Nova A"}, tmp_path) == tmp_path / "Café Bossa Nova A.mp3"
    assert musica_de({"musica": "borrada"}, tmp_path) == estreno.ascensor()


def test_the_video_speaks_the_language_of_its_sentences():
    from tramoya.easy.engine import idioma_de
    assert idioma_de(["Esta es la lista de notas de casa.", "El buscador filtra mientras escribes."]) == "es"
    assert idioma_de(["This is the list of notes.", "The search box filters as you type."]) == "en"
    assert idioma_de(["¿Dónde está?"]) == "es"
    assert idioma_de(["OK"]) is None and idioma_de([]) is None
