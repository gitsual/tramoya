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
  # Medallion: the emblem trimmed to its ring, everything outside the circle
  # replaced by the palette background (the generated image has its own).
  local medallion mask w h; medallion=$(mktemp --suffix=.png); mask=$(mktemp --suffix=.png)
  magick "$emblem" -fuzz 6% -trim +repage -bordercolor "#$COLOR_BG" -border 4 "$medallion"
  w=$(magick identify -format "%w" "$medallion"); h=$(magick identify -format "%h" "$medallion")
  magick -size "${w}x${h}" xc:black -fill white \
    -draw "circle $((w / 2)),$((h / 2)) $((w / 2)),4" "$mask"
  magick -size "${w}x${h}" xc:"#$COLOR_BG" "$medallion" "$mask" -compose Over -composite "$medallion"
  # Mark: the medallion alone, transparent outside the ring (no square corners).
  # (-border composes with the active operator, so reset it to Over first.)
  magick "$medallion" "$mask" -alpha off -compose CopyOpacity -composite -compose Over \
    -bordercolor none -border 6% -resize 360x360 assets/logo-mark.png
  # Logo: the mark beside the wordmark, one per README theme, transparent
  # background so it sits on GitHub's own page colour (README uses <picture>).
  local ring; ring=$(mktemp --suffix=.png)
  magick "$medallion" "$mask" -alpha off -compose CopyOpacity -composite -compose Over \
    -bordercolor none -border 6% -resize 300x300 "$ring"
  wordmark() {  # <fg> <accent> <out>
    magick "$ring" \
      \( -background none -fill "#$1" -font "$font" -pointsize 150 -kerning 6 label:tramoya \
         \( -background none -fill "#$2" -font "$font" -pointsize 30 -kerning 8 \
            label:"THE MACHINERY BEHIND THE TAKE" \) \
         -gravity west -append \) \
      -gravity center -background none +smush 32 \
      -bordercolor none -border 24x16 +repage ""
  }
  wordmark "$COLOR_FG" "$COLOR_ACCENT" assets/logo-dark.png
  wordmark "$COLOR_BG" "$COLOR_BORDER_INACTIVE" assets/logo-light.png
  rm -f "$ring"
  rm -f "$medallion" "$mask"
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
  # TRAMOYA_BIN overrides that with another venv's bin dir (one that has the tts extra).
  local runner="uv run --project $root"
  [[ -n ${TRAMOYA_BIN:-} ]] && runner="env PATH=$TRAMOYA_BIN:$PATH"
  (cd "${3:-.}" && asciinema rec --overwrite --cols 110 --rows 24 \
      -c "$runner bash $root/$1" "$cast" >/dev/null)
  agg --theme "$(agg_theme)" --font-family "JetBrainsMono Nerd Font" --font-size 15 \
      --idle-time-limit 3 "$cast" "$2" 2>/dev/null
  rm -f "$cast"
}

assistant_gif() {
  echo "-- assistant gif (real session against a local Ollama model)"
  record_cast scripts/assistant-cast.sh assets/gifs/assistant.gif examples/notes-app/out
}

card() {  # <text> <png>
  magick -size 1280x720 xc:"#$COLOR_BG" -gravity center -fill "#$COLOR_FG" \
    -font "$(font_pick)" -pointsize 64 -annotate +0-20 "$1" \
    -fill "#$COLOR_ACCENT" -pointsize 26 -annotate +0+60 "tramoya" "$2"
}

segment() {  # <input> <out.mp4> [<seconds>]  normalise anything to 1280x720 h264 + silent-or-real aac
  local loop=()
  [[ -n ${3:-} ]] && loop=(-loop 1 -t "$3")
  ffmpeg -loglevel error -y "${loop[@]}" -i "$1" -f lavfi -i anullsrc=r=48000:cl=stereo \
    -filter_complex "[0:v]scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:color=#$COLOR_BG,fps=30,format=yuv420p[v];[0:a]anull[a0]" \
    -map "[v]" -map "[a0]" -c:v libx264 -crf 22 -c:a aac -ar 48000 -ac 2 -shortest "$2" 2>/dev/null \
  || ffmpeg -loglevel error -y "${loop[@]}" -i "$1" -f lavfi -i anullsrc=r=48000:cl=stereo \
    -filter_complex "[0:v]scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:color=#$COLOR_BG,fps=30,format=yuv420p[v]" \
    -map "[v]" -map 1:a -c:v libx264 -crf 22 -c:a aac -ar 48000 -ac 2 -shortest "$2"
}

walkthrough() {
  echo "-- walkthrough video (one request, the real run, the result)"
  local app=examples/notes-app tmp; tmp=$(mktemp -d)
  # Start from the three sources only, so the assistant sees a clean folder.
  fd -I --min-depth 1 . "$app" -E index.html -E script.json -E director.py -X rm -rf
  record_cast scripts/walkthrough-cast.sh "$tmp/terminal.gif" "$app"
  local result; result=$(fd -I -e mp4 . "$app" -x stat -c '%Y %n' | sort -n | tail -1 | cut -d' ' -f2-)
  [[ -n $result ]] || { echo "no video came out of the run" >&2; return 1; }
  card "One request" "$tmp/card1.png"; card "The result" "$tmp/card2.png"
  segment "$tmp/card1.png" "$tmp/s1.mp4" 2.5
  segment "$tmp/terminal.gif" "$tmp/s2.mp4"
  segment "$tmp/card2.png" "$tmp/s3.mp4" 2.5
  segment "$result" "$tmp/s4.mp4"
  printf "file '%s/s%d.mp4'\n" "$tmp" 1 "$tmp" 2 "$tmp" 3 "$tmp" 4 > "$tmp/list.txt"
  ffmpeg -loglevel error -y -f concat -safe 0 -i "$tmp/list.txt" -c copy assets/video/walkthrough.mp4
  ffmpeg -loglevel error -y -i assets/video/walkthrough.mp4 \
    -vf "fps=6,scale=720:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer" \
    assets/gifs/walkthrough.gif
  rm -rf "$tmp"
}

filmstrip() {
  echo "-- filmstrip (one real frame per scene of the demo take, from its marks)"
  local app=examples/notes-app tmp; tmp=$(mktemp -d)
  local take=$app/demo/take.webm marks=$app/demo/marks.json font; font=$(font_pick)
  [[ -f $take && -f $marks ]] || { echo "run the walkthrough first: no take or marks under $app/demo" >&2; return 1; }
  local i=0
  while IFS=$'\t' read -r name label mid; do
    ffmpeg -loglevel error -y -ss "$mid" -i "$take" -frames:v 1 -vf scale=640:-1 "$tmp/f$i.png"
    magick "$tmp/f$i.png" -bordercolor "#$COLOR_BORDER_INACTIVE" -border 2 \
      -background "#$COLOR_BG" -fill "#$COLOR_ACCENT" -font "$font" -pointsize 26 -gravity center \
      \( -size 644x48 xc:"#$COLOR_BG" -fill "#$COLOR_FG" -annotate +0+0 "$label" \
         -fill "#$COLOR_ACCENT" -pointsize 18 -gravity west -annotate +8+0 "$name" \) -append "$tmp/c$i.png"
    i=$((i + 1))
  done < <(python3 - "$marks" <<'PY'
import json, sys
marks = json.load(open(sys.argv[1]))
starts = {m["key"].split(":")[1]: (m["t"], m["label"]) for m in marks if m["key"].endswith(":start") and m["key"].startswith("scene:")}
ends = {m["key"].split(":")[1]: m["t"] for m in marks if m["key"].endswith(":end") and m["key"].startswith("scene:")}
for name, (t0, label) in starts.items():
    print(f"{name}\t{label}\t{(t0 + ends[name]) / 2:.2f}")
PY
)
  magick montage "$tmp"/c*.png -tile 3x -geometry +16+16 -background "#$COLOR_BG" assets/filmstrip.png
  rm -rf "$tmp"
}

cli_gif() {
  echo "-- cli gif (real recording of the installed command)"
  record_cast scripts/cli-cast.sh assets/gifs/cli.gif
}


case "$what" in
  logos) logos ;;
  pipeline) pipeline ;;
  filmstrip) filmstrip ;;
  gif) cli_gif ;;
  assistant) assistant_gif ;;
  walkthrough) walkthrough ;;
  all) logos; pipeline; filmstrip; cli_gif; assistant_gif; walkthrough ;;
  *) echo "usage: $0 [logos|pipeline|filmstrip|gif|assistant|walkthrough|all]" >&2; exit 2 ;;
esac
echo "-- done"
du -h assets/*.png assets/gifs/*.gif 2>/dev/null
