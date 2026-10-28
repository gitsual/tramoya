"""`tramoya easy`: the recorded clicks become steps a person can read, the steps
become scenes and step lines, and nothing a password was typed into survives."""

from __future__ import annotations

from tramoya.easy.engine import (
    SSO_SIEMPRE,
    _paso_de_evento,
    argumentos_navegador,
    dominios_de,
    escenas,
    limpiar,
    lineas_de_paso,
    pasos_de_grabacion,
    segundos_lectura,
    srt,
)
from tramoya.profile import parse_step

CODEGEN = '''
from playwright.sync_api import Playwright, sync_playwright


def run(playwright: Playwright) -> None:
    browser = playwright.chromium.launch(headless=False)
    context = browser.new_context()
    page = context.new_page()
    page.goto("https://apps.example.net/Horas/home.xhtml")
    page.get_by_role("button", name="Añadir Código Proyecto").click()
    page.locator(".rowNotSaved > .tdCod > input").click()
    page.locator(".rowNotSaved > .tdCod > input").fill("EV4I02")
    page.locator(".rowNotSaved > .tdCod > input").press("Tab")
    page.get_by_label("Lunes").fill("9")
    page.get_by_role("row", name="EV4I02 2003").get_by_role("checkbox").check()
    page.get_by_text("Copiar filas a", exact=True).click()
    page.keyboard.press("Enter")
    page.get_by_placeholder("Contraseña").fill("secreto")
    page.get_by_role("button", name="IMPUTAR").click()
    with page.expect_popup() as page1_info:
        page.get_by_role("link", name="Ayuda").click()
    page1 = page1_info.value
    page1.get_by_role("button", name="X").click()
'''


def test_codegen_becomes_readable_steps_in_source_order() -> None:
    pasos, avisos = pasos_de_grabacion(CODEGEN)
    assert [p["desc"] for p in pasos] == [
        "Pulsa el botón «Añadir Código Proyecto»",
        "Escribe «EV4I02»",
        "Pulsa la tecla Tabulador",
        "Escribe «9» en el campo «Lunes»",
        "Marca la casilla de la fila «EV4I02 2003»",
        "Pulsa «Copiar filas a»",
        "Pulsa la tecla Intro",
        "Pulsa el botón «IMPUTAR»",
        "Pulsa el enlace «Ayuda»",
    ]
    assert pasos[0]["selector"] == 'internal:role=button[name="Añadir Código Proyecto"i]'
    assert pasos[4]["selector"] == 'internal:role=row[name="EV4I02 2003"i] >> internal:role=checkbox'
    assert pasos[5]["selector"] == 'internal:text="Copiar filas a"s'
    assert [p["id"] for p in pasos] == list(range(1, 10))
    assert any("contraseña" in a for a in avisos)
    assert any("otra ventana" in a for a in avisos)


def test_only_the_last_saving_button_is_pointed_at_not_pressed() -> None:
    pasos = limpiar([
        {"tipo": "clic", "selector": "#a", "desc": "Pulsa «Guardar»", "guarda": True},
        {"tipo": "escribir", "selector": "#b", "valor": "1", "desc": "Escribe «1»"},
        {"tipo": "clic", "selector": "#c", "desc": "Pulsa «Imputar»", "guarda": True},
        {"tipo": "tecla", "valor": "Enter", "desc": "Pulsa la tecla Intro"},
    ])
    assert [p.get("seguro") for p in pasos] == [False, None, True, None]


def test_a_click_into_a_field_that_is_then_typed_into_is_one_step() -> None:
    pasos = limpiar([
        {"tipo": "clic", "selector": "#t", "desc": "Pulsa el campo «Title»"},
        {"tipo": "escribir", "selector": "#t", "valor": "Reg", "desc": "Escribe «Reg»"},
        {"tipo": "escribir", "selector": "#t", "valor": "Regar", "desc": "Escribe «Regar»"},
        {"tipo": "clic", "selector": "#t", "desc": "Pulsa el campo «Title»"},
    ])
    assert [p["desc"] for p in pasos] == ["Escribe «Regar»"]


def test_recorded_events_name_what_was_touched() -> None:
    clic = _paso_de_evento({"tipo": "clic", "selector": "internal:role=button[name=\"IMPUTAR\"i]",
                            "nombre": "IMPUTAR", "que": "el botón"}, "")
    assert clic["desc"] == "Pulsa el botón «IMPUTAR»" and clic["guarda"]
    marco = _paso_de_evento({"tipo": "clic", "selector": "#x", "nombre": "", "que": "la casilla",
                             "marca": "on"}, "iframe >> internal:control=enter-frame >> ")
    assert marco["desc"] == "Marca la casilla"
    assert marco["selector"] == "iframe >> internal:control=enter-frame >> #x"
    cookies = _paso_de_evento({"tipo": "clic", "selector": "#ok", "nombre": "Aceptar todas",
                               "que": "el botón"}, "")
    assert cookies["opcional"] and "cookies" in cookies["desc"]
    assert _paso_de_evento({"tipo": "clic", "selector": "", "nombre": "x"}, "") is None


def test_a_sentence_opens_a_scene_and_untold_steps_ride_along() -> None:
    proyecto = {"nombre": "Copiar filas", "pasos": [
        {"id": 1, "tipo": "clic", "selector": "#a", "texto": ""},
        {"id": 2, "tipo": "clic", "selector": "#b", "texto": "Pulsa aquí."},
        {"id": 3, "tipo": "escribir", "selector": "#c", "valor": "9", "texto": ""},
        {"id": 4, "tipo": "clic", "selector": "#d", "texto": "Y guarda.", "quitado": True},
    ]}
    lista = escenas(proyecto)
    assert [(e["name"], e["texto"], [p["id"] for p in e["items"]]) for e in lista] == [
        ("inicio", "Copiar filas", [1]),
        ("paso-2", "Pulsa aquí.", [2, 3]),
    ]


def test_every_step_line_parses() -> None:
    for paso in (
        {"tipo": "clic", "selector": "#a"},
        {"tipo": "clic", "selector": "#a", "seguro": True},
        {"tipo": "escribir", "selector": "#a", "valor": "a | b"},
        {"tipo": "tecla", "valor": "Tab"},
        {"tipo": "elegir", "selector": "#s", "valor": "home"},
        {"tipo": "ir", "valor": "https://example.net/"},
    ):
        lines = lineas_de_paso(paso)
        assert lines
        for line in lines:
            parse_step(line)
    assert lineas_de_paso({"tipo": "clic", "selector": "#a", "seguro": True})[0] == "point #a"
    assert lineas_de_paso({"tipo": "señalar", "selector": "#a"}) == ["point #a", "beat 1"]
    assert parse_step(lineas_de_paso({"tipo": "escribir", "selector": "#a",
                                      "valor": "a | b"})[1]).args == ["#a", "a | b"]


def test_reading_time() -> None:
    assert segundos_lectura("Hola") == 2.5
    assert segundos_lectura(" ".join(["palabra"] * 26)) == 11.2


def test_windows_sign_in_is_allowed_for_the_site_its_domain_and_the_extra_ones() -> None:
    assert dominios_de("https://apps.example.org/x", ["*.example.net"]) == [
        "apps.example.org", "*.example.org", "*.example.net", *SSO_SIEMPRE]
    assert dominios_de("http://127.0.0.1:8770/") == ["127.0.0.1", *SSO_SIEMPRE]
    args = argumentos_navegador("https://intranet.example.org/app")
    allow = next(a for a in args if a.startswith("--auth-server-allowlist="))
    assert allow.split("=", 1)[1] == ",".join(dominios_de("https://intranet.example.org/"))


def test_subtitles_follow_the_assembled_scene_lengths() -> None:
    assert srt([("Hola.", 2.5), ("", 1.0), ("Adiós.", 3.25)]) == (
        "1\n00:00:00,000 --> 00:00:02,500\nHola.\n\n"
        "2\n00:00:03,500 --> 00:00:06,750\nAdiós.\n")
