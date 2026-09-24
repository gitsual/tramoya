"""Plain language in, a plan of tramoya commands out.

`ask` sends a request such as "make the video with voice" to a language
model together with a description of what the command line can do and the
files in the working directory, and gets back a `Plan`: a one-line summary
(the high level) and numbered steps, each with a reason and the exact
`tramoya` command it runs (the low level).

Two backends, same contract: `OllamaBackend` for a local model and
`ClaudeBackend` for the Claude API. Both take an injectable `post` so tests
never open a socket. `run_plan` only ever executes `tramoya` commands.
"""

from __future__ import annotations

import json
import os
import re
import urllib.request
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol

from tramoya.tts import VOICES

Post = Callable[[str, dict, dict], dict]

DEFAULT_OLLAMA_MODEL = "qwen3-coder"
DEFAULT_OLLAMA_URL = "http://localhost:11434"
DEFAULT_CLAUDE_MODEL = "claude-sonnet-5"
CLAUDE_URL = "https://api.anthropic.com/v1/messages"

# Subcommands that accept --dry-run; `run_plan(dry_run=True)` appends it there.
DRY_RUN_CAPABLE = {"assemble", "deck", "captions", "music", "record"}

SYSTEM_PROMPT = """You are the assistant of `tramoya`, a command-line tool that turns a
screen recording of a software demo into a finished video. The user is not a
video engineer. Translate what they ask into a short plan of `tramoya`
commands. Never invent files: use only the files listed, or names the user
gave, or outputs of earlier steps.

Commands you may use (and nothing else):
  tramoya marks <marks.json>                      print the scene table of a marks file
  tramoya assemble --video <take.mp4> --marks <marks.json> --voice-dir <voices-dir>
                   --out <output> [--lang en]   cut the take per scene, fit the voice, join
  tramoya tts --script <script.json> --lang <en|es> --out-dir <dir>
                                                synthesize one voice clip per script key
  tramoya deck --slides <slides-dir> --voice-dir <voices-dir> --out <output>
               [--clip <video> --clip-duration <seconds>]
                                                slide PNGs + narration (+ a clip) -> video
  tramoya captions burn --video <input> --subtitles <file.srt> --out <output> [--trim 0-120,600-780]
  tramoya music bed --out bed.wav               synthesize a looping background bed
  tramoya music mix --video <input> --bed bed.wav --out <output> [--solo]
  tramoya cues marks --cues <cues-file> --out <marks.json> [--meta <meta-file> --duration <seconds>]
  tramoya cues ass --cues <cues-file> --duration <seconds> --width <w> --height <h> --out <file.ass>
  tramoya record --out <take.mp4> --geometry "<x>,<y> <w>x<h>" [--fps <n>]

Rules:
- `tts` needs a narration script JSON (key -> {lang: text}); `marks.json` is
  not a script. Voices exist for "en" and "es" only.
- Anything in <angle brackets> above is a placeholder: replace it with a real
  file name from the list, or a name the user gave, or an output of an
  earlier step. If a needed input is not listed, return no steps and say in
  the summary which file is missing.

Answer with JSON only, in this exact shape:
{"summary": "one sentence saying what will happen and why",
 "steps": [{"title": "short imperative title",
            "why": "one sentence a non-technical person understands",
            "command": ["tramoya", "assemble", "--video", "take.mp4", "..."]}]}
Every command is a list of strings starting with "tramoya". Prefer the fewest
steps that do the job. When the request is impossible with these commands,
return a plan with no steps and say why in the summary."""


@dataclass
class Step:
    title: str
    why: str
    command: list[str]


@dataclass
class Plan:
    summary: str
    steps: list[Step] = field(default_factory=list)


# --- transport ------------------------------------------------------------


def http_post(url: str, payload: dict, headers: dict) -> dict:
    """POST JSON, return the decoded JSON body. The only network call here."""
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url, data=data, headers={"Content-Type": "application/json", **headers}, method="POST"
    )
    with urllib.request.urlopen(request, timeout=300) as response:
        return json.loads(response.read().decode("utf-8"))


class Backend(Protocol):
    model: str

    def complete(self, system: str, user: str) -> str: ...


class OllamaBackend:
    """A model served by Ollama, asked through its native chat endpoint with
    `format: json` so the reply is a JSON object."""

    def __init__(
        self,
        model: str = DEFAULT_OLLAMA_MODEL,
        base_url: str = DEFAULT_OLLAMA_URL,
        post: Post = http_post,
    ) -> None:
        self.model = model
        self.base_url = base_url.rstrip("/")
        self._post = post

    def complete(self, system: str, user: str) -> str:
        reply = self._post(
            f"{self.base_url}/api/chat",
            {
                "model": self.model,
                "format": "json",
                "stream": False,
                "options": {"temperature": 0},
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": user},
                ],
            },
            {},
        )
        return reply["message"]["content"]


class ClaudeBackend:
    """The Claude Messages API."""

    def __init__(
        self,
        api_key: str,
        model: str = DEFAULT_CLAUDE_MODEL,
        post: Post = http_post,
    ) -> None:
        self.model = model
        self.api_key = api_key
        self._post = post

    def complete(self, system: str, user: str) -> str:
        reply = self._post(
            CLAUDE_URL,
            {
                "model": self.model,
                "max_tokens": 2048,
                "system": system,
                "messages": [{"role": "user", "content": user}],
            },
            {"x-api-key": self.api_key, "anthropic-version": "2023-06-01"},
        )
        return "".join(block.get("text", "") for block in reply["content"])


def pick_backend(
    name: str | None,
    env: Mapping[str, str] | None = None,
    model: str | None = None,
) -> Backend:
    """Choose a backend: the explicit `name`, else `$TRAMOYA_LLM`, else Claude
    when `$ANTHROPIC_API_KEY` is set, else a local Ollama model.

    The model comes from `model`, else `$TRAMOYA_LLM_MODEL`, else a default.
    """
    env = os.environ if env is None else env
    fallback = "claude" if env.get("ANTHROPIC_API_KEY") else "ollama"
    chosen = name or env.get("TRAMOYA_LLM") or fallback
    model = model or env.get("TRAMOYA_LLM_MODEL") or None
    if chosen == "claude":
        key = env.get("ANTHROPIC_API_KEY", "")
        if not key:
            raise ValueError("the claude backend needs ANTHROPIC_API_KEY in the environment")
        return ClaudeBackend(api_key=key, model=model or DEFAULT_CLAUDE_MODEL)
    if chosen == "ollama":
        return OllamaBackend(
            model=model or DEFAULT_OLLAMA_MODEL,
            base_url=env.get("OLLAMA_HOST", DEFAULT_OLLAMA_URL),
        )
    raise ValueError(f"unknown backend {chosen!r}: use 'ollama' or 'claude'")


# --- plans ----------------------------------------------------------------

_FENCE = re.compile(r"```(?:json)?\s*(.*?)```", re.DOTALL)


def parse_plan(text: str) -> Plan:
    """Read a `Plan` out of a model reply, with or without a code fence."""
    match = _FENCE.search(text)
    body = match.group(1) if match else text
    start, end = body.find("{"), body.rfind("}")
    if start < 0 or end < 0:
        raise ValueError(f"the reply is not a plan: {text[:80]!r}")
    try:
        raw: Any = json.loads(body[start : end + 1])
    except json.JSONDecodeError as exc:
        raise ValueError(f"the reply is not a plan: {exc}") from exc
    if not isinstance(raw, dict) or "steps" not in raw or not isinstance(raw["steps"], list):
        raise ValueError("the reply has no steps")
    steps = [
        Step(
            title=str(s.get("title", "")),
            why=str(s.get("why", "")),
            command=[str(part) for part in s.get("command", [])],
        )
        for s in raw["steps"]
    ]
    return Plan(summary=str(raw.get("summary", "")), steps=steps)


def describe_files(files: list[str]) -> str:
    if not files:
        return "The working directory is empty."
    return "Files in the working directory:\n" + "\n".join(f"  {f}" for f in files)


def ask(
    backend: Backend,
    request: str,
    files: list[str],
    exists: Callable[[str], bool] = os.path.exists,
    repairs: int = 1,
) -> Plan:
    """Turn `request` into a `Plan`, telling the model which files exist.

    When the plan would fail `preflight`, the problems are sent back to the
    model once (`repairs` times) so it can fix its own mistakes, typically
    a placeholder file name. The caller still runs `preflight` before running.
    """
    user = f"{describe_files(files)}\n\nRequest: {request}"
    plan = parse_plan(backend.complete(SYSTEM_PROMPT, user))
    for _ in range(repairs):
        problems = preflight(plan, exists)
        if not problems:
            break
        user += (
            "\n\nYour previous plan cannot run:\n"
            + "\n".join(f"  {p}" for p in problems)
            + "\nReturn a corrected plan using only the files listed above, "
            "or no steps if the request cannot be met."
        )
        plan = parse_plan(backend.complete(SYSTEM_PROMPT, user))
    return plan


def render_plan(plan: Plan) -> str:
    """The plan for a person: what will happen, then each step at both
    levels (why in words, the exact command underneath)."""
    lines = [plan.summary, ""]
    if not plan.steps:
        lines.append("(nothing to run)")
    for i, step in enumerate(plan.steps, 1):
        lines.append(f"{i}. {step.title}")
        lines.append(f"   why: {step.why}")
        lines.append(f"   $ {' '.join(step.command)}")
    return "\n".join(lines)


# Flags whose value must be a file or directory that already exists.
_INPUT_FLAGS = {"--video", "--marks", "--voice-dir", "--script", "--slides", "--clip",
                "--subtitles", "--bed", "--cues", "--meta"}
_OUTPUT_FLAGS = {"--out", "--out-dir"}


def _inputs_of(argv: list[str]) -> list[str]:
    inputs = [argv[i + 1] for i, a in enumerate(argv[:-1]) if a in _INPUT_FLAGS]
    if argv[:1] == ["marks"] and len(argv) > 1:
        inputs.append(argv[1])
    return inputs


def _flag_value(argv: list[str], flag: str) -> str | None:
    return next((argv[i + 1] for i, a in enumerate(argv[:-1]) if a == flag), None)


def _is_script(path: str) -> bool:
    try:
        raw = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False
    return isinstance(raw, dict) and all(isinstance(v, dict) for v in raw.values())


def preflight(plan: Plan, exists: Callable[[str], bool] = os.path.exists) -> list[str]:
    """Say, in plain words, what would make a step fail before anything runs:
    an input that does not exist, a language with no voice, a file passed as
    a narration script that is not one. An input produced by an earlier
    step's `--out`/`--out-dir` counts as present."""
    produced: set[str] = set()
    problems: list[str] = []
    for i, step in enumerate(plan.steps, 1):
        argv = step.command[1:]
        for path in _inputs_of(argv):
            if path not in produced and not exists(path):
                problems.append(f"step {i} needs {path}, which does not exist")
        if argv[:1] == ["tts"]:
            lang = _flag_value(argv, "--lang")
            if lang and lang not in VOICES:
                langs = ", ".join(sorted(VOICES))
                problems.append(
                    f"step {i} asks for language {lang}, but voices exist only for {langs}"
                )
            script = _flag_value(argv, "--script")
            if script and exists(script) and not _is_script(script):
                problems.append(
                    f"step {i} uses {script} as a narration script, but it is not one "
                    "(a script maps each scene key to its text per language)"
                )
        produced.update(argv[j + 1] for j, a in enumerate(argv[:-1]) if a in _OUTPUT_FLAGS)
    return problems


def _argv_for(step: Step, dry_run: bool) -> list[str]:
    if not step.command or step.command[0] != "tramoya":
        raise ValueError(f"only tramoya commands can run, not {step.command!r}")
    argv = step.command[1:]
    if dry_run and argv and argv[0] in DRY_RUN_CAPABLE and "--dry-run" not in argv:
        argv = [*argv, "--dry-run"]
    return argv


def run_plan(
    plan: Plan,
    runner: Callable[[list[str]], int],
    dry_run: bool = False,
) -> list[int]:
    """Run each step through `runner` (the CLI's `main`), stopping at the
    first non-zero exit. Refuses any command that is not `tramoya`."""
    argvs = [_argv_for(step, dry_run) for step in plan.steps]  # validate all before running any
    codes: list[int] = []
    for argv in argvs:
        code = runner(argv)
        codes.append(code)
        if code != 0:
            break
    return codes
