"""Tests for tramoya.cues: parsing recorded cue files and rendering ASS subtitles."""

from __future__ import annotations

from tramoya.cues import (
    Cue,
    cues_to_ass,
    cues_to_marks,
    offset_from_duration,
    parse_cues,
    parse_cues_with_total,
    parse_meta,
    shift,
)

CUES_TEXT = "\n".join(
    [
        "0.000|opening line",
        "4.120|second line",
        "9.500|",
    ]
)


def test_parse_cues_ignores_the_empty_text_sentinel():
    cues = parse_cues(CUES_TEXT)
    assert cues == [Cue(t=0.0, text="opening line"), Cue(t=4.12, text="second line")]


def test_parse_cues_with_total_returns_the_sentinel_timestamp():
    cues, total = parse_cues_with_total(CUES_TEXT)
    assert len(cues) == 2
    assert total == 9.5


def test_parse_cues_with_total_defaults_total_when_no_sentinel():
    cues, total = parse_cues_with_total("0.000|only line")
    assert len(cues) == 1
    assert total == 0.0


def test_parse_cues_skips_blank_lines():
    cues = parse_cues("0.000|a\n\n1.000|b\n")
    assert [c.text for c in cues] == ["a", "b"]


def test_parse_meta_reads_key_value_pairs():
    meta = parse_meta("resolution=1920x1080\nelapsed=42.5\n")
    assert meta == {"resolution": "1920x1080", "elapsed": "42.5"}


def test_offset_from_duration_is_the_gap_between_duration_and_elapsed():
    assert offset_from_duration(duration=50.0, elapsed=42.5) == 7.5


def test_offset_from_duration_never_goes_negative():
    assert offset_from_duration(duration=10.0, elapsed=42.5) == 0.0


def test_shift_adds_the_offset_to_every_cue():
    cues = [Cue(t=0.0, text="a"), Cue(t=1.0, text="b")]
    shifted = shift(cues, 2.5)
    assert shifted == [Cue(t=2.5, text="a"), Cue(t=3.5, text="b")]
    # original list is untouched
    assert cues == [Cue(t=0.0, text="a"), Cue(t=1.0, text="b")]


def test_cues_to_marks_turns_each_cue_into_a_scene_with_the_next_cues_start_as_its_end():
    cues = [Cue(t=0.0, text="a"), Cue(t=1.0, text="b"), Cue(t=3.0, text="c")]
    marks = cues_to_marks(cues)
    keys = [m.key for m in marks]
    assert keys == [
        "scene:0:start",
        "scene:0:end",
        "scene:1:start",
        "scene:1:end",
        "scene:2:start",
    ]
    assert marks[0].t == 0.0
    assert marks[1].t == 1.0
    assert marks[0].label == "a"
    assert marks[1].label == "a"


def test_cues_to_marks_of_empty_list_is_empty():
    assert cues_to_marks([]) == []


def test_cues_to_ass_contains_style_and_dialogue_lines():
    cues = [Cue(t=0.0, text="hello"), Cue(t=5.0, text="world")]
    ass = cues_to_ass(cues, total=10.0, width=1920, height=1080)
    assert "[Script Info]" in ass
    assert "PlayResX: 1920" in ass
    assert "PlayResY: 1080" in ass
    assert "BorderStyle=3" not in ass  # style values are positional, not key=value
    assert "\\fad(300,300)" in ass
    assert "hello" in ass
    assert "world" in ass


def test_cues_to_ass_ends_each_line_before_the_next_cue_starts():
    cues = [Cue(t=0.0, text="hello"), Cue(t=5.0, text="world")]
    ass = cues_to_ass(cues, total=10.0, width=1920, height=1080)
    dialogue = [line for line in ass.splitlines() if line.startswith("Dialogue:")]
    assert len(dialogue) == 2
    # "hello" ends 0.15s before "world" starts at 0:00:05.00
    assert "0:00:04.85" in dialogue[0]


def test_cues_to_ass_last_line_ends_at_total():
    cues = [Cue(t=0.0, text="hello")]
    ass = cues_to_ass(cues, total=5.0, width=1920, height=1080)
    dialogue = [line for line in ass.splitlines() if line.startswith("Dialogue:")]
    assert len(dialogue) == 1
    assert "0:00:04.85" in dialogue[0]


def test_cues_to_ass_skips_empty_text_cues():
    cues = [Cue(t=0.0, text="hello"), Cue(t=5.0, text="")]
    ass = cues_to_ass(cues, total=5.0, width=1920, height=1080)
    dialogue = [line for line in ass.splitlines() if line.startswith("Dialogue:")]
    assert len(dialogue) == 1


def test_cues_to_ass_scales_font_size_and_margin_to_height():
    cues = [Cue(t=0.0, text="hi")]
    ass = cues_to_ass(cues, total=1.0, width=1920, height=300)
    # size = max(20, height // 30) = 20; margin = height // 16 = 18
    style_line = next(line for line in ass.splitlines() if line.startswith("Style:"))
    fields = style_line.split(",")
    assert fields[2] == "20"
