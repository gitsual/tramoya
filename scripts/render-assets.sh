#!/usr/bin/env bash
# Regenerate every rendered asset under assets/ from its source and the palette
# in assets/theme.conf.
#
#   scripts/render-assets.sh            # logos + pipeline diagram + CLI gif
#   scripts/render-assets.sh logos      # only the logos
#
# The mark is assets/brand/emblem-flux.png, an emblem generated with a local
# diffusion model from the palette's own colours; everything around it (padding,
# wordmark, diagram, terminal theme) is derived from theme.conf at render time.
# Needs: magick, rsvg-convert, asciinema, agg, uv (for the CLI gif).
set -euo pipefail
cd "$(dirname "$0")/.."

# shellcheck disable=SC1091
source assets/theme.conf
what=${1:-all}

font_pick() {
  for f in Noto-Serif-Display-Regular Noto-Serif-Regular Liberation-Serif; do
    if magick -list font 2>/dev/null | grep -q "Font: $f\$"; then echo "$f"; return; fi
  done
  echo Liberation-Serif
}

logos() {
  echo "-- logos"
  local emblem=assets/brand/emblem-flux.png font
  font=$(font_pick)
  # Mark: the emblem trimmed to its medallion, padded on the palette background.
  magick "$emblem" -fuzz 6% -trim +repage -bordercolor "#$COLOR_BG" -border 14% \
    -resize 360x360 assets/logo-mark.png
  # Logo: the mark beside the wordmark, one line, on the palette background.
  magick "$emblem" -fuzz 6% -trim +repage -bordercolor "#$COLOR_BG" -border 12% -resize 300x300 \
    \( -background "#$COLOR_BG" -fill "#$COLOR_FG" -font "$font" -pointsize 150 \
       -kerning 6 label:tramoya \
       \( -background "#$COLOR_BG" -fill "#$COLOR_ACCENT" -font "$font" -pointsize 30 \
          -kerning 8 label:"THE MACHINERY BEHIND THE TAKE" \) \
       -gravity west -append \) \
    -gravity center -background "#$COLOR_BG" +smush 32 \
    -bordercolor "#$COLOR_BG" -border 40x28 assets/logo.png
}

pipeline() {
  echo "-- pipeline diagram"
  local svg; svg=$(mktemp --suffix=.svg)
  sed -e "s/@COLOR_BG@/#$COLOR_BG/g" -e "s/@COLOR_SURFACE@/#$COLOR_SURFACE/g" \
      -e "s/@COLOR_FG@/#$COLOR_FG/g" -e "s/@COLOR_ACCENT@/#$COLOR_ACCENT/g" \
      -e "s/@COLOR_MUTED@/#$COLOR_MUTED/g" \
      -e "s/@COLOR_BORDER_INACTIVE@/#$COLOR_BORDER_INACTIVE/g" \
      templates/pipeline.svg.in > "$svg"
  rsvg-convert -w 1800 "$svg" -o assets/pipeline.png
  rm -f "$svg"
}

agg_theme() {
  echo "$TERMINAL_BG,$TERMINAL_FG,$COLOR_SURFACE,$COLOR_URGENT,$COLOR_OK,$COLOR_ACCENT,$COLOR_INFO,$COLOR_BORDER_INACTIVE,$COLOR_INFO,$COLOR_FG_DIM,$COLOR_SURFACE_ALT,$COLOR_URGENT,$COLOR_OK,$COLOR_ACCENT,$COLOR_INFO,$COLOR_BORDER_INACTIVE,$COLOR_INFO,$COLOR_FG"
}

record_cast() {  # <script> <gif> [<dir>]
  local cast root
  cast=$(mktemp --suffix=.cast)
  root=$PWD
  # `uv run` puts the project venv on PATH, so `tramoya` inside the script resolves.
  (cd "${3:-.}" && asciinema rec --overwrite --cols 110 --rows 24 \
      -c "uv run --project $root bash $root/$1" "$cast" >/dev/null)
  agg --theme "$(agg_theme)" --font-family "JetBrainsMono Nerd Font" --font-size 15 \
      --idle-time-limit 3 "$cast" "$2" 2>/dev/null
  rm -f "$cast"
}

assistant_gif() {
  echo "-- assistant gif (real session against a local Ollama model)"
  record_cast scripts/assistant-cast.sh assets/gifs/assistant.gif examples/notes-app/out
}

cli_gif() {
  echo "-- cli gif (real recording of the installed command)"
  record_cast scripts/cli-cast.sh assets/gifs/cli.gif
}


case "$what" in
  logos) logos ;;
  pipeline) pipeline ;;
  gif) cli_gif ;;
  assistant) assistant_gif ;;
  all) logos; pipeline; cli_gif; assistant_gif ;;
  *) echo "usage: $0 [logos|pipeline|gif|assistant|all]" >&2; exit 2 ;;
esac
echo "-- done"
du -h assets/*.png assets/gifs/*.gif 2>/dev/null
