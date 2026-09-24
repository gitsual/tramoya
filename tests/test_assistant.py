"""The assistant turns a plain-language request into a plan of tramoya
commands, through either a local Ollama model or the Claude API. Both
backends are exercised with a fake HTTP `post`; nothing talks to a server."""

from __future__ import annotations

import json

import pytest

from tramoya import cli
from tramoya.assistant import (
    ClaudeBackend,
    OllamaBackend,
    Plan,
    Step,
    ask,
    parse_plan,
    pick_backend,
    render_plan,
    run_plan,
)

PLAN_JSON = json.dumps({
    "summary": "Cut the take by its marks and add the voice.",
    "steps": [
        {"title": "Check the scene table", "why": "See what the marks say before cutting.",
         "command": ["tramoya", "marks", "marks.json"]},
        {"title": "Assemble the video", "why": "One clip per scene, voice fitted.",
         "command": ["tramoya", "assemble", "--video", "take.mp4", "--marks", "marks.json",
                     "--voice-dir", "voices/en", "--out", "demo.mp4"]},
    ],
})


class FakePost:
    def __init__(self, reply: str) -> None:
        self.reply = reply
        self.calls: list[tuple[str, dict, dict]] = []

    def __call__(self, url: str, payload: dict, headers: dict) -> dict:
        self.calls.append((url, payload, headers))
        return json.loads(self.reply)


# --- parsing --------------------------------------------------------------


def test_parse_plan_accepts_fenced_json() -> None:
    plan = parse_plan("Here you go:\n```json\n" + PLAN_JSON + "\n```")
    assert plan.summary.startswith("Cut the take")
    assert [s.title for s in plan.steps] == ["Check the scene table", "Assemble the video"]
    assert plan.steps[1].command[:2] == ["tramoya", "assemble"]


def test_parse_plan_rejects_garbage() -> None:
    with pytest.raises(ValueError, match="not a plan"):
        parse_plan("I cannot help with that.")


def test_parse_plan_rejects_missing_steps() -> None:
    with pytest.raises(ValueError, match="steps"):
        parse_plan('{"summary": "x"}')


# --- backends -------------------------------------------------------------


def test_ollama_backend_posts_a_json_chat() -> None:
    post = FakePost(json.dumps({"message": {"role": "assistant", "content": PLAN_JSON}}))
    backend = OllamaBackend(model="qwen3-coder", base_url="http://localhost:11434", post=post)
    text = backend.complete("SYSTEM", "USER")
    url, payload, _headers = post.calls[0]
    assert url == "http://localhost:11434/api/chat"
    assert payload["model"] == "qwen3-coder"
    assert payload["format"] == "json"
    assert payload["stream"] is False
    assert payload["messages"][0] == {"role": "system", "content": "SYSTEM"}
    assert payload["messages"][1] == {"role": "user", "content": "USER"}
    assert json.loads(text)["summary"].startswith("Cut")


def test_claude_backend_posts_a_messages_call() -> None:
    post = FakePost(json.dumps({"content": [{"type": "text", "text": PLAN_JSON}]}))
    backend = ClaudeBackend(model="claude-sonnet-5", api_key="sk-test", post=post)
    text = backend.complete("SYSTEM", "USER")
    url, payload, headers = post.calls[0]
    assert url == "https://api.anthropic.com/v1/messages"
    assert headers["x-api-key"] == "sk-test"
    assert headers["anthropic-version"]
    assert payload["model"] == "claude-sonnet-5"
    assert payload["system"] == "SYSTEM"
    assert payload["messages"] == [{"role": "user", "content": "USER"}]
    assert payload["max_tokens"] >= 1024
    assert json.loads(text)["summary"].startswith("Cut")


def test_pick_backend_prefers_the_explicit_name_then_env_then_key() -> None:
    assert isinstance(pick_backend("ollama", {"ANTHROPIC_API_KEY": "k"}), OllamaBackend)
    assert isinstance(pick_backend(None, {"TRAMOYA_LLM": "claude", "ANTHROPIC_API_KEY": "k"}),
                      ClaudeBackend)
    assert isinstance(pick_backend(None, {"ANTHROPIC_API_KEY": "k"}), ClaudeBackend)
    assert isinstance(pick_backend(None, {}), OllamaBackend)


def test_pick_backend_claude_without_key_is_an_error() -> None:
    with pytest.raises(ValueError, match="ANTHROPIC_API_KEY"):
        pick_backend("claude", {})


def test_pick_backend_honours_model_overrides() -> None:
    backend = pick_backend("ollama", {"TRAMOYA_LLM_MODEL": "llama3.2:3b"})
    assert backend.model == "llama3.2:3b"
    backend = pick_backend("ollama", {}, model="gpt-oss:20b")
    assert backend.model == "gpt-oss:20b"


# --- ask / render / run ---------------------------------------------------


class FakeBackend:
    def __init__(self, reply: str) -> None:
        self.reply = reply
        self.prompts: list[tuple[str, str]] = []

    def complete(self, system: str, user: str) -> str:
        self.prompts.append((system, user))
        return self.reply


def test_ask_sends_the_request_and_the_files_it_can_see() -> None:
    backend = FakeBackend(PLAN_JSON)
    plan = ask(backend, "make the video with voice", files=["take.mp4", "marks.json"])
    system, user = backend.prompts[0]
    assert "tramoya assemble" in system  # the model is told what the tool can do
    assert "make the video with voice" in user
    assert "take.mp4" in user and "marks.json" in user
    assert isinstance(plan, Plan)


def test_render_plan_is_readable_at_both_levels() -> None:
    plan = parse_plan(PLAN_JSON)
    text = render_plan(plan)
    assert "Cut the take by its marks" in text
    assert "1. Check the scene table" in text
    assert "why: See what the marks say" in text
    assert "$ tramoya marks marks.json" in text


def test_run_plan_calls_each_tramoya_command_in_order() -> None:
    plan = parse_plan(PLAN_JSON)
    seen: list[list[str]] = []
    codes = run_plan(plan, runner=lambda argv: (seen.append(argv), 0)[1])
    assert seen == [["marks", "marks.json"],
                    ["assemble", "--video", "take.mp4", "--marks", "marks.json",
                     "--voice-dir", "voices/en", "--out", "demo.mp4"]]
    assert codes == [0, 0]


def test_run_plan_stops_at_the_first_failure() -> None:
    plan = parse_plan(PLAN_JSON)
    codes = run_plan(plan, runner=lambda argv: 3)
    assert codes == [3]


def test_run_plan_refuses_anything_that_is_not_tramoya() -> None:
    plan = Plan(summary="x", steps=[Step("rm", "no", ["rm", "-rf", "/"])])
    with pytest.raises(ValueError, match="only tramoya commands"):
        run_plan(plan, runner=lambda argv: 0)


def test_run_plan_dry_run_adds_the_flag_where_it_exists() -> None:
    plan = parse_plan(PLAN_JSON)
    seen: list[list[str]] = []
    run_plan(plan, runner=lambda argv: (seen.append(argv), 0)[1], dry_run=True)
    assert seen[0] == ["marks", "marks.json"]  # `marks` has no --dry-run
    assert seen[1][-1] == "--dry-run"


# --- cli ------------------------------------------------------------------


def test_cli_ask_prints_the_plan(monkeypatch, capsys, tmp_path) -> None:
    monkeypatch.setattr(cli, "pick_backend", lambda *a, **k: FakeBackend(PLAN_JSON))
    monkeypatch.chdir(tmp_path)
    assert cli.main(["ask", "make the video"]) == 0
    out = capsys.readouterr().out
    assert "1. Check the scene table" in out
    assert "$ tramoya assemble" in out


def test_cli_do_runs_the_plan_with_yes(monkeypatch, capsys, tmp_path) -> None:
    monkeypatch.setattr(cli, "pick_backend", lambda *a, **k: FakeBackend(PLAN_JSON))
    monkeypatch.chdir(tmp_path)
    for name in ("take.mp4", "marks.json"):
        (tmp_path / name).write_text("")
    (tmp_path / "voices" / "en").mkdir(parents=True)
    ran: list[list[str]] = []
    monkeypatch.setattr(cli, "_run_step", lambda argv: (ran.append(argv), 0)[1])
    assert cli.main(["do", "make the video", "--yes"]) == 0
    assert [r[0] for r in ran] == ["marks", "assemble"]


def test_cli_do_without_yes_asks_and_a_no_runs_nothing(monkeypatch, capsys, tmp_path) -> None:
    monkeypatch.setattr(cli, "pick_backend", lambda *a, **k: FakeBackend(PLAN_JSON))
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr("builtins.input", lambda prompt="": "n")
    ran: list[list[str]] = []
    monkeypatch.setattr(cli, "_run_step", lambda argv: (ran.append(argv), 0)[1])
    assert cli.main(["do", "make the video"]) == 1
    assert ran == []


# --- preflight ------------------------------------------------------------

from tramoya.assistant import preflight  # noqa: E402


def test_preflight_reports_inputs_that_do_not_exist() -> None:
    plan = parse_plan(PLAN_JSON)
    problems = preflight(plan, exists=lambda p: p in {"take.mp4", "marks.json"})
    assert problems == ["step 2 needs voices/en, which does not exist"]


def test_preflight_accepts_outputs_of_earlier_steps() -> None:
    plan = Plan(summary="", steps=[
        Step("bed", "", ["tramoya", "music", "bed", "--out", "bed.wav"]),
        Step("mix", "", ["tramoya", "music", "mix", "--video", "demo.mp4", "--bed", "bed.wav",
                         "--out", "final.mp4"]),
    ])
    assert preflight(plan, exists=lambda p: p == "demo.mp4") == []


def test_preflight_flags_placeholders_in_capitals() -> None:
    plan = Plan(summary="", steps=[Step("m", "", ["tramoya", "marks", "MARKS.json"])])
    problems = preflight(plan, exists=lambda p: False)
    assert problems == ["step 1 needs MARKS.json, which does not exist"]


def test_cli_do_refuses_a_plan_that_fails_preflight(monkeypatch, capsys, tmp_path) -> None:
    monkeypatch.setattr(cli, "pick_backend", lambda *a, **k: FakeBackend(PLAN_JSON))
    monkeypatch.chdir(tmp_path)  # empty: take.mp4 and marks.json are missing
    ran: list[list[str]] = []
    monkeypatch.setattr(cli, "_run_step", lambda argv: (ran.append(argv), 0)[1])
    assert cli.main(["do", "make the video", "--yes"]) == 1
    assert ran == []
    assert "does not exist" in capsys.readouterr().out


def test_preflight_flags_a_language_without_a_voice(tmp_path) -> None:
    script = tmp_path / "script.json"
    script.write_text('{"scene-a": {"en": "hi"}}')
    plan = Plan(summary="", steps=[
        Step("tts", "", ["tramoya", "tts", "--script", str(script), "--lang", "fr",
                         "--out-dir", "voices/fr"]),
    ])
    assert preflight(plan) == ["step 1 asks for language fr, but voices exist only for en, es"]


def test_preflight_flags_a_script_that_is_not_a_script(tmp_path) -> None:
    marks = tmp_path / "marks.json"
    marks.write_text('[{"t": 0, "key": "scene:a:start", "label": ""}]')
    plan = Plan(summary="", steps=[
        Step("tts", "", ["tramoya", "tts", "--script", str(marks), "--lang", "en",
                         "--out-dir", "voices/en"]),
    ])
    assert preflight(plan) == [
        f"step 1 uses {marks} as a narration script, but it is not one "
        "(a script maps each scene key to its text per language)"
    ]


def test_ask_repairs_a_plan_once_when_preflight_complains() -> None:
    bad = PLAN_JSON.replace("marks.json", "MARKS.json")
    backend = FakeBackend(bad)
    replies = iter([bad, PLAN_JSON])
    backend.complete = lambda system, user: (backend.prompts.append((system, user)),
                                             next(replies))[1]
    plan = ask(backend, "make the video", files=["take.mp4", "marks.json", "voices/en"],
               exists=lambda p: p in {"take.mp4", "marks.json", "voices/en"})
    assert len(backend.prompts) == 2
    assert "MARKS.json, which does not exist" in backend.prompts[1][1]
    assert plan.steps[0].command == ["tramoya", "marks", "marks.json"]


def test_preflight_knows_what_direct_produces(tmp_path, monkeypatch) -> None:
    monkeypatch.chdir(tmp_path)
    (tmp_path / "script.json").write_text('{"scene-welcome": {"en": "Hello"}}')
    plan = Plan(summary="", steps=[
        Step("direct", "", ["tramoya", "direct", "director.py", "--out", "out"]),
        Step("voice", "", ["tramoya", "tts", "--script", "script.json", "--lang", "en",
                          "--out-dir", "out/voices/en"]),
        Step("build", "", ["tramoya", "assemble", "--video", "out/take.webm", "--marks",
                          "out/marks.json", "--voice-dir", "out/voices/en", "--out", "demo.mp4"]),
    ])
    assert preflight(plan, exists=lambda p: p in {"director.py", "script.json"}) == []


def test_preflight_reports_a_missing_director_script() -> None:
    plan = Plan(summary="", steps=[
        Step("direct", "", ["tramoya", "direct", "director.py", "--out", "out"]),
    ])
    assert preflight(plan, exists=lambda p: False) == [
        "step 1 needs director.py, which does not exist"
    ]


def test_run_plan_dry_run_reaches_direct() -> None:
    plan = Plan(summary="", steps=[
        Step("direct", "", ["tramoya", "direct", "director.py", "--out", "out"]),
    ])
    seen: list[list[str]] = []
    run_plan(plan, runner=lambda argv: (seen.append(argv), 0)[1], dry_run=True)
    assert seen == [["direct", "director.py", "--out", "out", "--dry-run"]]


def test_preflight_reports_direct_without_a_script() -> None:
    plan = Plan(summary="", steps=[
        Step("direct", "", ["tramoya", "direct", "--out", "out", "--lang", "en"]),
    ])
    assert preflight(plan, exists=lambda p: True) == [
        "step 1 does not say which director script to run (tramoya direct <director.py> ...)"
    ]
