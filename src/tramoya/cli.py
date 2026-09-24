"""The `tramoya` command line.

Every subcommand that would invoke ffmpeg or a recorder takes `--dry-run`
and prints the exact argv instead, so a whole pipeline can be checked
without paying a single encode.
"""

from __future__ import annotations

import argparse
import json
import shlex
import sys
import tempfile
from pathlib import Path

from tramoya import __version__
from tramoya.assembly import assemble_deck, assemble_marks, plan_segments
from tramoya.assistant import ask, pick_backend, preflight, render_plan, run_plan
from tramoya.audio import build_loop, mix_argv, render_wav
from tramoya.captions import burn_argv, trim_plan
from tramoya.cues import (
    cues_to_ass,
    cues_to_marks,
    offset_from_duration,
    parse_cues_with_total,
    parse_meta,
    shift,
)
from tramoya.ffmpeg import Runner
from tramoya.marks import load_marks, scene_bounds, wait_windows
from tramoya.narration import wav_seconds
from tramoya.record import Recorder, read_sidecar, wf_recorder_argv
from tramoya.tts import synthesize_script


def _print_calls(runner: Runner) -> None:
    for argv in runner.calls:
        print(shlex.join(argv))


def _parse_ranges(text: str) -> list[tuple[float, float]]:
    ranges: list[tuple[float, float]] = []
    for chunk in text.split(","):
        a, b = chunk.split("-", 1)
        ranges.append((float(a), float(b)))
    return ranges


# --- subcommands ----------------------------------------------------------


def cmd_marks(args: argparse.Namespace) -> int:
    marks = load_marks(Path(args.marks), args.offset)
    scenes = scene_bounds(marks)
    waits = wait_windows(marks)
    print(f"{len(scenes)} scenes, {len(waits)} waits, offset {args.offset:+.2f}s")
    for name, (start, end) in scenes.items():
        print(f"  {name:<20} {start:8.2f} -> {end:8.2f}  ({end - start:6.1f}s)")
    for start, end in waits:
        print(f"  {'(wait)':<20} {start:8.2f} -> {end:8.2f}  ({end - start:6.1f}s)")
    return 0


def _offset_for(video: Path, explicit: float | None) -> float:
    if explicit is not None:
        return explicit
    sidecar = video.with_name(video.name + ".offset.json")
    return read_sidecar(sidecar) if sidecar.exists() else 0.0


def cmd_assemble(args: argparse.Namespace) -> int:
    runner = Runner(dry_run=args.dry_run)
    video = Path(args.video)
    offset = _offset_for(video, args.offset)
    table = assemble_marks(
        runner, video, Path(args.marks), Path(args.voice_dir), Path(args.out),
        offset=offset, lang=args.lang,
    )
    if args.dry_run:
        _print_calls(runner)
    else:
        for name, raw, final in table:
            print(f"scene {name}: {raw:6.1f}s raw -> {final:6.1f}s final")
        print(f"assembled {args.out}")
    return 0


def cmd_deck(args: argparse.Namespace) -> int:
    slides = Path(args.slides)
    manifest = json.loads((slides / "index.json").read_text(encoding="utf-8"))
    pages: dict[str, str] = manifest["pages"]
    order = sorted(pages, key=lambda n: pages[n])
    voice_dir = Path(args.voice_dir)
    voices = {n: voice_dir / f"{n}.wav" for n in order if (voice_dir / f"{n}.wav").exists()}
    durations = {n: wav_seconds(p) for n, p in voices.items()}
    clip = Path(args.clip) if args.clip else None
    plan = plan_segments(
        pages, order, durations, slides, clip, args.clip_duration,
        voices=voices, clip_page=args.clip_page,
    )
    for seg in plan:
        print(f"  {seg.kind:<5} {seg.name:<20} {seg.duration:6.1f}s  {seg.source.name}")
    runner = Runner(dry_run=args.dry_run)
    with tempfile.TemporaryDirectory(prefix="tramoya-deck-") as tmp:
        assemble_deck(runner, plan, Path(tmp), Path(args.out))
    if args.dry_run:
        _print_calls(runner)
    else:
        print(f"assembled {args.out}")
    return 0


def cmd_captions_burn(args: argparse.Namespace) -> int:
    runner = Runner(dry_run=args.dry_run)
    video = Path(args.video)
    with tempfile.TemporaryDirectory(prefix="tramoya-cap-") as tmp:
        if args.trim:
            trimmed = Path(tmp) / "trimmed.mp4"
            for argv in trim_plan(_parse_ranges(args.trim), video, Path(tmp), trimmed):
                runner.run(argv)
            video = trimmed
        runner.run(burn_argv(video, Path(args.subtitles), Path(args.out)))
    if args.dry_run:
        _print_calls(runner)
    return 0


def cmd_music_bed(args: argparse.Namespace) -> int:
    render_wav(build_loop(), Path(args.out))
    print(f"bed written to {args.out}")
    return 0


def cmd_music_mix(args: argparse.Namespace) -> int:
    runner = Runner(dry_run=args.dry_run)
    runner.run(mix_argv(Path(args.video), Path(args.bed), Path(args.out), duck=not args.solo))
    if args.dry_run:
        _print_calls(runner)
    return 0


def _load_cues(args: argparse.Namespace) -> tuple[list, float]:
    cues, total = parse_cues_with_total(Path(args.cues).read_text(encoding="utf-8"))
    offset = 0.0
    if args.meta and args.duration is not None:
        meta = parse_meta(Path(args.meta).read_text(encoding="utf-8"))
        offset = offset_from_duration(args.duration, float(meta["elapsed"]))
    return shift(cues, offset), total + offset


def cmd_cues_marks(args: argparse.Namespace) -> int:
    cues, _total = _load_cues(args)
    rows = [{"t": m.t, "key": m.key, "label": m.label} for m in cues_to_marks(cues)]
    Path(args.out).write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n")
    print(f"{len(rows)} marks written to {args.out}")
    return 0


def cmd_cues_ass(args: argparse.Namespace) -> int:
    cues, total = _load_cues(args)
    text = cues_to_ass(cues, total, args.width, args.height, font=args.font)
    Path(args.out).write_text(text, encoding="utf-8")
    print(f"{len(cues)} cues written to {args.out}")
    return 0


def cmd_record(args: argparse.Namespace) -> int:
    if args.dry_run:
        argv = wf_recorder_argv(Path(args.out), args.geometry, fps=args.fps,
                                audio_sink=args.audio_sink)
        print(shlex.join(argv))
        return 0
    recorder = Recorder(args.out, args.geometry, fps=args.fps, audio_sink=args.audio_sink)
    recorder.start()
    print(f"recording to {args.out}; press Enter to stop", flush=True)
    try:
        sys.stdin.readline()
    finally:
        recorder.stop()
    print("stopped")
    return 0


def cmd_tts(args: argparse.Namespace) -> int:
    script = json.loads(Path(args.script).read_text(encoding="utf-8"))
    try:
        paths = synthesize_script(script, args.lang, args.out_dir)
    except ImportError as exc:
        print(f"text-to-speech needs the optional extra: pip install 'tramoya[tts]' ({exc})",
              file=sys.stderr)
        return 2
    for p in paths:
        print(p)
    return 0


def _visible_files(limit: int = 60) -> list[str]:
    files = sorted(
        str(p) for p in Path().rglob("*")
        if p.is_file() and not any(part.startswith(".") for part in p.parts)
    )
    return files[:limit]


def _run_step(argv: list[str]) -> int:
    print(f"$ tramoya {shlex.join(argv)}", flush=True)
    return main(argv)


def _plan_for(args: argparse.Namespace):
    backend = pick_backend(args.backend, model=args.model)
    return ask(backend, args.request, _visible_files())


def cmd_ask(args: argparse.Namespace) -> int:
    print(render_plan(_plan_for(args)))
    return 0


def cmd_do(args: argparse.Namespace) -> int:
    plan = _plan_for(args)
    print(render_plan(plan))
    if not plan.steps:
        return 1
    problems = preflight(plan)
    if problems:
        print("\nnot running:")
        for problem in problems:
            print(f"  {problem}")
        return 1
    if not args.yes:
        answer = input(f"\nRun these {len(plan.steps)} steps? [y/N] ").strip().lower()
        if answer not in ("y", "yes"):
            print("nothing run")
            return 1
    print()
    codes = run_plan(plan, runner=_run_step, dry_run=args.dry_run)
    return codes[-1] if codes else 0


# --- parser ---------------------------------------------------------------


def _add_dry_run(p: argparse.ArgumentParser) -> None:
    p.add_argument("--dry-run", action="store_true",
                   help="print the commands instead of running them")


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="tramoya", description=__doc__)
    ap.add_argument("--version", action="version", version=f"tramoya {__version__}")
    sub = ap.add_subparsers(dest="command", required=True)

    p = sub.add_parser("marks", help="print the scene table of a marks file")
    p.add_argument("marks")
    p.add_argument("--offset", type=float, default=0.0)
    p.set_defaults(fn=cmd_marks)

    p = sub.add_parser("assemble", help="marks + recording + voice clips -> one video")
    p.add_argument("--video", required=True)
    p.add_argument("--marks", required=True)
    p.add_argument("--voice-dir", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--offset", type=float, default=None,
                   help="director-vs-recorder offset; defaults to <video>.offset.json")
    p.add_argument("--lang", default="en")
    _add_dry_run(p)
    p.set_defaults(fn=cmd_assemble)

    p = sub.add_parser("deck", help="slide PNGs + narration (+ a clip) -> one video")
    p.add_argument("--slides", required=True, help="directory with index.json and PNGs")
    p.add_argument("--voice-dir", required=True)
    p.add_argument("--clip", default=None)
    p.add_argument("--clip-duration", type=float, default=0.0)
    p.add_argument("--clip-page", default="video")
    p.add_argument("--out", required=True)
    _add_dry_run(p)
    p.set_defaults(fn=cmd_deck)

    p = sub.add_parser("captions", help="subtitle tools")
    cs = p.add_subparsers(dest="captions_command", required=True)
    b = cs.add_parser("burn", help="burn an .srt/.ass into a video")
    b.add_argument("--video", required=True)
    b.add_argument("--subtitles", required=True)
    b.add_argument("--out", required=True)
    b.add_argument("--trim", default=None, help="keep only these ranges: 0-120,600-780")
    _add_dry_run(b)
    b.set_defaults(fn=cmd_captions_burn)

    p = sub.add_parser("music", help="background bed tools")
    ms = p.add_subparsers(dest="music_command", required=True)
    bed = ms.add_parser("bed", help="synthesize the looping background bed")
    bed.add_argument("--out", required=True)
    bed.set_defaults(fn=cmd_music_bed)
    mix = ms.add_parser("mix", help="mix the bed into a video, ducked under the voice")
    mix.add_argument("--video", required=True)
    mix.add_argument("--bed", required=True)
    mix.add_argument("--out", required=True)
    mix.add_argument("--solo", action="store_true", help="flat level, no ducking")
    _add_dry_run(mix)
    mix.set_defaults(fn=cmd_music_mix)

    p = sub.add_parser("cues", help="single-take cue files (timestamp|text)")
    cu = p.add_subparsers(dest="cues_command", required=True)
    for name, fn in (("marks", cmd_cues_marks), ("ass", cmd_cues_ass)):
        c = cu.add_parser(name)
        c.add_argument("--cues", required=True)
        c.add_argument("--meta", default=None)
        c.add_argument("--duration", type=float, default=None,
                       help="ffprobe duration of the take, to recover the pre-roll")
        c.add_argument("--out", required=True)
        if name == "ass":
            c.add_argument("--width", type=int, required=True)
            c.add_argument("--height", type=int, required=True)
            c.add_argument("--font", default="JetBrainsMono Nerd Font")
        c.set_defaults(fn=fn)

    p = sub.add_parser("record", help="record the screen with wf-recorder")
    p.add_argument("--out", required=True)
    p.add_argument("--geometry", required=True, help='"X,Y WxH"')
    p.add_argument("--fps", type=int, default=10)
    p.add_argument("--audio-sink", default=None)
    _add_dry_run(p)
    p.set_defaults(fn=cmd_record)

    for name, fn, help_text in (
        ("ask", cmd_ask, "describe what you want; get the plan of commands, run nothing"),
        ("do", cmd_do, "describe what you want; confirm the plan, then run it"),
    ):
        p = sub.add_parser(name, help=help_text)
        p.add_argument("request", help='e.g. "make the video from take.mp4 with the English voice"')
        p.add_argument("--backend", choices=("ollama", "claude"), default=None,
                       help="default: $TRAMOYA_LLM, else claude when $ANTHROPIC_API_KEY is set, "
                            "else ollama")
        p.add_argument("--model", default=None, help="model name; default: $TRAMOYA_LLM_MODEL")
        if name == "do":
            p.add_argument("--yes", action="store_true", help="skip the confirmation")
            _add_dry_run(p)
        p.set_defaults(fn=fn)

    p = sub.add_parser("tts", help="synthesize a narration script with Kokoro")
    p.add_argument("--script", required=True, help="JSON: key -> {lang: text}")
    p.add_argument("--lang", required=True)
    p.add_argument("--out-dir", required=True)
    p.set_defaults(fn=cmd_tts)
    return ap


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return int(args.fn(args))


if __name__ == "__main__":
    sys.exit(main())
