"""Subtitle cue derivation, SRT/ASS rendering and the ffmpeg burn/trim argv."""

from __future__ import annotations

from itertools import pairwise
from pathlib import Path

import pytest

from tramoya.captions import (
    Cue,
    build_cues,
    burn_argv,
    fits,
    page_cues,
    render_ass,
    render_srt,
    split_long,
    split_sentences,
    timestamp,
    trim_plan,
    wrap,
)


class _Seg:
    def __init__(self, kind: str, name: str, duration: float) -> None:
        self.kind = kind
        self.name = name
        self.duration = duration


def test_split_sentences_keeps_terminal_punctuation() -> None:
    assert split_sentences("Uno. Dos! Tres?") == ["Uno.", "Dos!", "Tres?"]


def test_split_long_keeps_every_word() -> None:
    sentence = (
        "The risk is not in using artificial intelligence, it is in wiring it "
        "to Jira, Git and Bitbucket without clear gates and without audit."
    )
    pieces = split_long(sentence)
    assert len(pieces) > 1
    assert " ".join(pieces).split() == sentence.split()


def test_split_long_leaves_no_one_word_tail() -> None:
    sentence = ("a " * 60 + "final.").strip()
    pieces = split_long(sentence)
    assert len(pieces[-1]) > 84 // 4


def test_wrap_returns_text_unchanged_when_it_fits() -> None:
    assert wrap("short line") == "short line"


def test_wrap_breaks_at_the_most_even_word_boundary() -> None:
    text = "one two three four five six seven eight nine ten eleven twelve"
    wrapped = wrap(text)
    lines = wrapped.split("\n")
    assert len(lines) == 2
    assert fits(text) or max(len(line) for line in lines) <= 42


def test_page_cues_lose_no_narration() -> None:
    text = "This is the first sentence. This is the second, a bit longer one."
    cues = page_cues(text, 0.0, 10.0)
    rendered = " ".join(c.text.replace("\n", " ") for c in cues)
    assert rendered.split() == text.split()


def test_page_cues_cover_the_whole_window_without_overlap() -> None:
    text = "First sentence here. Second sentence follows. Third one closes it."
    cues = page_cues(text, 10.0, 40.0)
    assert cues[0].start == 10.0
    assert cues[-1].end == 40.0
    assert all(b.start == pytest.approx(a.end) for a, b in pairwise(cues))


def test_page_cues_returns_nothing_for_blank_text() -> None:
    assert page_cues("   ", 0.0, 5.0) == []


def test_build_cues_scales_to_the_measured_runtime() -> None:
    segments = [_Seg("slide", "cover", 10.0), _Seg("slide", "agenda", 10.0)]
    script = [("cover", "One sentence."), ("agenda", "Another sentence.")]
    cues = build_cues(script, segments, clip_cue=None, scale=0.5)
    assert cues[0].start == 0.0
    assert cues[-1].end == pytest.approx(10.0)


def test_build_cues_gives_the_recorded_clip_its_own_cue() -> None:
    segments = [_Seg("slide", "video", 10.0), _Seg("clip", "demo", 145.0)]
    script = [("video", "One sentence.")]
    cues = build_cues(script, segments, clip_cue="Recorded demo, real system.")
    clip_cues = [c for c in cues if "Recorded demo" in c.text.replace("\n", " ")]
    assert len(clip_cues) == 1
    assert clip_cues[0].start == pytest.approx(10.0)
    assert clip_cues[0].end - clip_cues[0].start <= 4.0
    assert cues[-1].end <= 155.0


def test_build_cues_skips_pages_the_script_does_not_mention() -> None:
    segments = [_Seg("slide", "cover", 5.0)]
    cues = build_cues([], segments, clip_cue=None)
    assert cues == []


def test_timestamp_is_srt_shaped() -> None:
    assert timestamp(0) == "00:00:00,000"
    assert timestamp(673.808) == "00:11:13,808"


def test_render_srt_numbers_cues_from_one() -> None:
    out = render_srt([Cue(0.0, 1.0, "one"), Cue(1.0, 2.0, "two")])
    assert out.startswith("1\n00:00:00,000 --> 00:00:01,000\none")
    assert "\n2\n" in out


def test_render_ass_has_the_vivac_plate_style() -> None:
    ass = render_ass([Cue(0.0, 2.0, "hello")], width=1920, height=1080)
    assert "[Script Info]" in ass
    assert "[V4+ Styles]" in ass
    assert "[Events]" in ass
    style_line = next(line for line in ass.splitlines() if line.startswith("Style: Narration"))
    fields = style_line.split(",")
    assert fields[1] == "JetBrainsMono Nerd Font"
    assert fields[2] == "36"  # max(20, round(1080/30))
    assert fields[15:19] == ["3", "6", "0", "2"]  # BorderStyle, Outline, Shadow, Alignment
    assert fields[19:22] == ["68", "68", "68"]  # margins = round(1080/16)


def test_render_ass_colours_are_aabbggrr_with_alpha_first() -> None:
    ass = render_ass([Cue(0.0, 1.0, "x")], 1920, 1080, fg="D9E2DD", bg="0E1513")
    assert "&H00DDE2D9" in ass  # fg
    assert "&H0013150E" in ass  # bg


def test_render_ass_dialogue_fades_and_trims_the_tail() -> None:
    ass = render_ass([Cue(1.0, 3.0, "hi")], 1920, 1080)
    dialogue = next(line for line in ass.splitlines() if line.startswith("Dialogue:"))
    assert dialogue == "Dialogue: 0,0:00:01.00,0:00:02.85,Narration,,0,0,0,,{\\fad(300,300)}hi"


def test_burn_argv_matches_the_recorded_dry_run() -> None:
    argv = burn_argv(Path("demo/demo-cci-ai-v9-es-mudo.mp4"), Path("showcase/captions/v9-es.srt"), Path("demo/demo-cci-ai-v9-es-captions.mp4"))
    assert argv == [
        "ffmpeg", "-y", "-i", "demo/demo-cci-ai-v9-es-mudo.mp4",
        "-vf", "subtitles=showcase/captions/v9-es.srt",
        "-c:a", "copy", "demo/demo-cci-ai-v9-es-captions.mp4",
    ]


def test_trim_plan_cuts_each_range_then_concats(tmp_path: Path) -> None:
    argvs = trim_plan(
        [(0, 120), (600, 780)], Path("in.mp4"), tmp_path, Path("out.mp4"),
    )
    assert len(argvs) == 3
    assert argvs[0][0] == "ffmpeg"
    assert "-ss 0 -to 120" in " ".join(argvs[0])
    assert "-ss 600 -to 780" in " ".join(argvs[1])
    assert "-f" in argvs[2] and "concat" in argvs[2]


def test_trim_plan_writes_a_concat_list_of_the_trimmed_parts(tmp_path: Path) -> None:
    trim_plan([(0, 10), (10, 20)], Path("in.mp4"), tmp_path, Path("out.mp4"))
    list_files = list(tmp_path.glob("*concat*.txt"))
    assert len(list_files) == 1
    content = list_files[0].read_text(encoding="utf-8")
    assert content.count("file '") == 2
