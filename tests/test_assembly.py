"""Scene/deck planning and the ffmpeg pipelines that render them.

Everything here drives a `Runner(dry_run=True)`: no real ffmpeg process ever
starts, and every assertion reads back `runner.calls` or the returned plan.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from tramoya.assembly import (
    MIN_SCENE,
    PAGE_TAIL,
    SILENT_PAGE,
    WAIT_TARGET,
    WAIT_THRESHOLD,
    Segment,
    assemble_deck,
    assemble_marks,
    build_scene_clip,
    fit_and_mux,
    humanize_wait,
    plan_segments,
    render_clip,
    render_slide,
)
from tramoya.ffmpeg import Runner

# --------------------------------------------------------------------------
# Segment / plan_segments
# --------------------------------------------------------------------------


def test_segment_is_a_frozen_value_object() -> None:
    seg = Segment(kind="slide", name="cover", source=Path("01.png"), duration=4.0)
    assert seg.voice is None
    with pytest.raises(AttributeError):
        seg.duration = 5.0  # type: ignore[misc]


def test_the_recording_is_played_on_the_page_that_announces_it(tmp_path: Path) -> None:
    plan = plan_segments(
        pages={"portada": "01", "video": "02", "cierre": "03"},
        order=["portada", "video", "cierre"],
        durations={"portada": 4.0, "video": 3.0, "cierre": 5.0},
        slides_dir=tmp_path,
        clip=tmp_path / "demo.mp4",
        clip_duration=145.0,
    )
    kinds = [segment.kind for segment in plan]
    assert kinds == ["slide", "slide", "clip", "slide"]
    assert plan[2].source == tmp_path / "demo.mp4"
    assert plan[2].duration == pytest.approx(145.0)


def test_every_slide_segment_lasts_as_long_as_its_narration(tmp_path: Path) -> None:
    plan = plan_segments(
        pages={"portada": "01", "cierre": "02"},
        order=["portada", "cierre"],
        durations={"portada": 4.5, "cierre": 9.25},
        slides_dir=tmp_path,
        clip=None,
        clip_duration=0.0,
    )
    assert [s.duration for s in plan] == pytest.approx([4.5 + PAGE_TAIL, 9.25 + PAGE_TAIL])
    assert [s.source.name for s in plan] == ["01.png", "02.png"]


def test_a_page_without_a_voice_clip_still_gets_time_on_screen(tmp_path: Path) -> None:
    plan = plan_segments(
        pages={"seccion_1": "01"},
        order=["seccion_1"],
        durations={},
        slides_dir=tmp_path,
        clip=None,
        clip_duration=0.0,
    )
    assert plan[0].duration == pytest.approx(SILENT_PAGE)
    assert plan[0].voice is None


def test_clip_page_is_configurable(tmp_path: Path) -> None:
    plan = plan_segments(
        pages={"intro": "01", "showcase": "02"},
        order=["intro", "showcase"],
        durations={"intro": 2.0, "showcase": 2.0},
        slides_dir=tmp_path,
        clip=tmp_path / "demo.mp4",
        clip_duration=10.0,
        clip_page="showcase",
    )
    assert [s.kind for s in plan] == ["slide", "slide", "clip"]


# --------------------------------------------------------------------------
# humanize_wait
# --------------------------------------------------------------------------


def test_humanize_wait_in_spanish() -> None:
    assert humanize_wait(3.0, "es") == "unos segundos despues"
    assert humanize_wait(60.0, "es") == "1 minuto despues"
    assert humanize_wait(240.0, "es") == "4 minutos despues"


def test_humanize_wait_in_english() -> None:
    assert humanize_wait(3.0, "en") == "a few seconds later"
    assert humanize_wait(60.0, "en") == "1 minute later"
    assert humanize_wait(240.0, "en") == "4 minutes later"


# --------------------------------------------------------------------------
# build_scene_clip
# --------------------------------------------------------------------------


def test_build_scene_clip_cuts_a_plain_window_with_no_waits(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    out = build_scene_clip(runner, Path("in.mp4"), tmp_path, "intro", 10.0, 20.0, [], "en")
    assert len(runner.calls) == 1
    argv = runner.calls[0]
    assert argv[0] == "ffmpeg"
    assert "-ss" in argv and argv[argv.index("-ss") + 1] == "10.0"
    assert "-to" in argv and argv[argv.index("-to") + 1] == "20.0"
    assert "-an" in argv
    assert out == tmp_path / "scene-intro-video.mp4"


def test_build_scene_clip_clamps_a_near_zero_scene(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    build_scene_clip(runner, Path("in.mp4"), tmp_path, "s", 5.0, 5.0, [], "en")
    argv = runner.calls[0]
    assert argv[argv.index("-to") + 1] == str(5.0 + MIN_SCENE)


def test_build_scene_clip_ignores_waits_at_or_under_the_threshold(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    build_scene_clip(
        runner, Path("in.mp4"), tmp_path, "s", 0.0, 30.0,
        [(5.0, 5.0 + WAIT_THRESHOLD)], "en",
    )
    assert len(runner.calls) == 1, "a wait no longer than the threshold is left alone"


def test_build_scene_clip_compresses_a_long_wait_and_concats(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    out = build_scene_clip(
        runner, Path("in.mp4"), tmp_path, "s", 0.0, 40.0,
        [(5.0, 5.0 + WAIT_THRESHOLD + 10.0)], "en",
    )
    # pre-wait cut, the sped-up wait itself, the post-wait cut, then concat.
    assert len(runner.calls) == 4
    pre, fast, post, concat = runner.calls
    assert "-vf" not in pre
    assert "setpts=PTS/" in fast[fast.index("-vf") + 1]
    assert "drawtext=text='" in fast[fast.index("-vf") + 1]
    assert "-vf" not in post
    assert concat[:2] == ["ffmpeg", "-loglevel"]
    assert "concat" in concat
    assert out == tmp_path / "scene-s-video.mp4"
    list_files = list(tmp_path.glob("scene-s-concat.txt"))
    assert len(list_files) == 1


def test_build_scene_clip_emits_no_empty_post_cut_when_the_wait_reaches_the_end(
    tmp_path: Path,
) -> None:
    runner = Runner(dry_run=True)
    build_scene_clip(
        runner, Path("in.mp4"), tmp_path, "s", 0.0, 40.0,
        [(5.0, 40.0)], "en",
    )
    # pre-wait cut, the sped-up wait, concat: a zero-length "post" cut would
    # make ffmpeg fail on a real take.
    assert len(runner.calls) == 3
    assert not any(str(tmp_path / "scene-s-post.mp4") in c for c in runner.calls)


def test_build_scene_clip_speeds_up_by_the_target_ratio(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    wait_dur = 40.0
    build_scene_clip(runner, Path("in.mp4"), tmp_path, "s", 0.0, wait_dur, [(0.0, wait_dur)], "en")
    fast = runner.calls[0]
    factor = wait_dur / WAIT_TARGET
    assert f"setpts=PTS/{factor}," in fast[fast.index("-vf") + 1]


# --------------------------------------------------------------------------
# fit_and_mux
# --------------------------------------------------------------------------


def test_fit_and_mux_pads_video_when_the_voice_runs_longer(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    video = tmp_path / "clip.mp4"
    wav = tmp_path / "voice.wav"
    runner.fake_durations[video] = 3.0
    runner.fake_durations[wav] = 5.0
    out, final_dur = fit_and_mux(runner, tmp_path, "s", video, wav)
    assert final_dur == pytest.approx(5.0)
    assert len(runner.calls) == 2
    pad_call, mux_call = runner.calls
    assert "tpad=stop_mode=clone" in pad_call[pad_call.index("-vf") + 1]
    assert "-filter_complex" in mux_call
    assert "apad=whole_dur=5.000" in mux_call[mux_call.index("-filter_complex") + 1]
    assert out == tmp_path / "scene-s-final.mp4"


def test_fit_and_mux_skips_padding_when_video_already_covers_the_voice(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    video = tmp_path / "clip.mp4"
    wav = tmp_path / "voice.wav"
    runner.fake_durations[video] = 10.0
    runner.fake_durations[wav] = 2.0
    fit_and_mux(runner, tmp_path, "s", video, wav)
    assert len(runner.calls) == 1, "video already covers the voice; no tpad call"


def test_fit_and_mux_uses_silence_when_there_is_no_voice(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    video = tmp_path / "clip.mp4"
    runner.fake_durations[video] = 4.0
    fit_and_mux(runner, tmp_path, "s", video, None)
    mux_call = runner.calls[-1]
    assert "anullsrc=channel_layout=mono:sample_rate=48000" in " ".join(mux_call)
    assert "-filter_complex" not in mux_call


# --------------------------------------------------------------------------
# assemble_marks
# --------------------------------------------------------------------------


def _write_marks(path: Path) -> None:
    path.write_text(json.dumps([
        {"t": 0.0, "key": "scene:intro:start"},
        {"t": 10.0, "key": "scene:intro:end"},
        {"t": 10.0, "key": "scene:outro:start"},
        {"t": 15.0, "key": "scene:outro:end"},
    ]), encoding="utf-8")


def test_assemble_marks_renders_every_scene_and_concats(tmp_path: Path) -> None:
    marks_path = tmp_path / "marks.json"
    _write_marks(marks_path)
    voice_dir = tmp_path / "voice"
    voice_dir.mkdir()
    (voice_dir / "scene-intro.wav").write_bytes(b"")

    runner = Runner(dry_run=True)
    out = tmp_path / "out.mp4"
    table = assemble_marks(runner, Path("in.mp4"), marks_path, voice_dir, out)

    assert [row[0] for row in table] == ["intro", "outro"]
    assert table[0][1] == pytest.approx(10.0)  # raw duration
    last_call = runner.calls[-1]
    assert "concat" in last_call and str(out) == last_call[-1]


def test_assemble_marks_falls_back_to_the_legacy_voice_filename(tmp_path: Path) -> None:
    marks_path = tmp_path / "marks.json"
    _write_marks(marks_path)
    voice_dir = tmp_path / "voice"
    voice_dir.mkdir()
    (voice_dir / "escena-intro.wav").write_bytes(b"")

    runner = Runner(dry_run=True)
    assemble_marks(runner, Path("in.mp4"), marks_path, voice_dir, tmp_path / "out.mp4")

    mux_calls = [c for c in runner.calls if "-filter_complex" in c]
    assert any("escena-intro.wav" in " ".join(c) for c in mux_calls)


def test_assemble_marks_raises_without_any_scene_marks(tmp_path: Path) -> None:
    marks_path = tmp_path / "marks.json"
    marks_path.write_text("[]", encoding="utf-8")
    runner = Runner(dry_run=True)
    with pytest.raises(RuntimeError):
        assemble_marks(runner, Path("in.mp4"), marks_path, tmp_path, tmp_path / "out.mp4")


# --------------------------------------------------------------------------
# render_slide / render_clip / assemble_deck
# --------------------------------------------------------------------------


def test_render_slide_mutes_and_loops_a_still_page_with_no_voice(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    seg = Segment(kind="slide", name="cover", source=Path("01.png"), duration=4.0)
    render_slide(runner, seg, tmp_path / "000.mp4")
    argv = runner.calls[0]
    assert "-loop" in argv
    assert "anullsrc=channel_layout=stereo:sample_rate=48000" in " ".join(argv)
    assert "scale=1920:1080" in " ".join(argv)


def test_render_slide_mixes_in_the_voice_clip_when_present(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    seg = Segment(
        kind="slide", name="cover", source=Path("01.png"), duration=4.0,
        voice=Path("cover.wav"),
    )
    render_slide(runner, seg, tmp_path / "000.mp4")
    argv = runner.calls[0]
    assert "cover.wav" in argv
    assert "-filter_complex" in argv
    assert "loudnorm" in argv[argv.index("-filter_complex") + 1]


def test_render_clip_fits_the_recording_to_the_slide_grid(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    seg = Segment(kind="clip", name="demo", source=Path("demo.mp4"), duration=100.0)
    render_clip(runner, seg, tmp_path / "001.mp4")
    argv = runner.calls[0]
    assert "scale=1920:1080" in " ".join(argv)
    assert "demo.mp4" in argv


def test_assemble_deck_renders_every_segment_then_concats(tmp_path: Path) -> None:
    runner = Runner(dry_run=True)
    plan = [
        Segment(kind="slide", name="cover", source=Path("01.png"), duration=4.0),
        Segment(kind="clip", name="demo", source=Path("demo.mp4"), duration=10.0),
        Segment(kind="slide", name="cierre", source=Path("02.png"), duration=3.0),
    ]
    parts = assemble_deck(runner, plan, tmp_path, tmp_path / "out.mp4")
    assert len(parts) == 3
    assert len(runner.calls) == 4  # 3 renders + 1 concat
    assert "concat" in runner.calls[-1]
