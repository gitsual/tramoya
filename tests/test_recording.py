"""Recordings become scenes: a Chrome DevTools Recorder JSON export or a
Playwright codegen Python file is read into step lines, one scene per
navigation, so nobody types a selector by hand."""

from __future__ import annotations

import json

import pytest

from tramoya.recording import (
    scenes_from_codegen,
    scenes_from_devtools,
    scenes_from_file,
)

DEVTOOLS = {
    "title": "notes",
    "steps": [
        {"type": "setViewport", "width": 1280, "height": 720},
        {"type": "navigate", "url": "http://127.0.0.1:8000/index.html"},
        {"type": "click", "selectors": [["aria/New note"], ["#new"], ["xpath///button[1]"]]},
        {"type": "change", "value": "Water the plants",
         "selectors": [["#title"], ["xpath///input[1]"]]},
        {"type": "change", "value": "home", "selectors": [["aria/Tag"], ["#tag"]]},
        {"type": "keyDown", "key": "Enter"},
        {"type": "keyUp", "key": "Enter"},
        {"type": "navigate", "url": "http://127.0.0.1:8000/settings.html"},
        {"type": "click", "selectors": [["text/Save"], ["pierce/#save"]]},
        {"type": "scroll", "y": 400},
    ],
}


def test_devtools_export_becomes_one_scene_per_navigation() -> None:
    scenes = scenes_from_devtools(DEVTOOLS)
    assert [s.name for s in scenes] == ["index", "settings"]
    assert scenes[0].steps == [
        "click #new",
        "type #title | Water the plants",
        "select #tag | home",
        "press Enter",
        "say",
    ]
    assert scenes[1].steps == ["goto /settings.html", "click_text Save", "scroll 400", "say"]
    assert scenes[1].label == "settings"


def test_devtools_prefers_css_then_text_then_aria_and_never_xpath_or_pierce() -> None:
    doc = {"steps": [
        {"type": "navigate", "url": "http://a/"},
        {"type": "click", "selectors": [["xpath///div"], ["pierce/#x"]]},
        {"type": "click", "selectors": [["xpath///div"], ["aria/Go"], ["text/Go now"]]},
    ]}
    scenes = scenes_from_devtools(doc)
    assert scenes[0].steps == ["click xpath=//div", "click_text Go now", "say"]


CODEGEN = '''
import re
from playwright.sync_api import Playwright, sync_playwright, expect


def run(playwright: Playwright) -> None:
    browser = playwright.chromium.launch(headless=False)
    context = browser.new_context()
    page = context.new_page()
    page.goto("http://127.0.0.1:8000/index.html")
    page.get_by_role("button", name="New note").click()
    page.locator("#title").click()
    page.locator("#title").fill("Water the plants")
    page.get_by_placeholder("Text").fill("Balcony first")
    page.locator("#tag").select_option("home")
    page.get_by_text("Work").click()
    page.get_by_label("Search").fill("re")
    page.locator("#title").press("Enter")
    page.goto("http://127.0.0.1:8000/about.html")
    page.get_by_role("link", name="Docs", exact=True).click()

    # ---------------------
    context.close()
    browser.close()


with sync_playwright() as playwright:
    run(playwright)
'''


def test_codegen_python_becomes_scenes_with_stage_verbs() -> None:
    scenes = scenes_from_codegen(CODEGEN)
    assert [s.name for s in scenes] == ["index", "about"]
    assert scenes[0].steps == [
        'click role=button[name="New note"]',
        "click #title",
        "type #title | Water the plants",
        'type [placeholder="Text"] | Balcony first',
        "select #tag | home",
        "click_text Work",
        'type role=textbox[name="Search"] | re',
        "press Enter",
        "say",
    ]
    assert scenes[1].steps == ["goto /about.html", 'click role=link[name="Docs"]', "say"]


def test_scenes_from_file_picks_the_format_by_content(tmp_path) -> None:
    j = tmp_path / "flow.json"
    j.write_text(json.dumps(DEVTOOLS))
    p = tmp_path / "flow.py"
    p.write_text(CODEGEN)
    assert [s.name for s in scenes_from_file(j)] == ["index", "settings"]
    assert [s.name for s in scenes_from_file(p)] == ["index", "about"]
    bad = tmp_path / "flow.txt"
    bad.write_text("hello")
    with pytest.raises(ValueError, match="neither"):
        scenes_from_file(bad)


def test_a_recording_with_no_navigation_is_still_one_scene() -> None:
    scenes = scenes_from_devtools({"steps": [{"type": "click", "selectors": [["#a"]]}]})
    assert len(scenes) == 1 and scenes[0].steps == ["click #a", "say"]
