#!/usr/bin/env bash
# Regenerate every rendered asset under assets/ from its source and the palette
# in assets/theme.conf.
#
#   scripts/render-assets.sh            # logos + pipeline diagram + CLI gif
#   scripts/render-assets.sh logos      # only the logos
#
# The logos, the diagram, the cards and the filmstrip are HTML rendered by
# scripts/render_brand.py with the window's fonts (Archivo, IBM Plex Mono);
# the terminal theme of the GIFs comes from theme.conf.
# Needs: python with playwright, ffmpeg, asciinema, agg, IBM Plex Mono (or AGG_FONT_DIR), and uv or TRAMOYA_BIN.
set -euo pipefail
cd "$(dirname "$0")/.."

# shellcheck disable=SC1091
source assets/theme.conf
what=${1:-all}

PY=${PYTHON:-python3}

logos() {
  echo "-- logos"
  "$PY" scripts/render_brand.py logos
}

pipeline() {
  echo "-- pipeline diagram"
  "$PY" scripts/render_brand.py pipeline
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
  agg --theme "$(agg_theme)" --font-family "IBM Plex Mono" ${AGG_FONT_DIR:+--font-dir "$AGG_FONT_DIR"} --font-size 15 \
      --idle-time-limit 3 "$cast" "$2" 2>/dev/null
  rm -f "$cast"
}

assistant_gif() {
  echo "-- assistant gif (real session against a local Ollama model)"
  local demo=${NOTES_APP:-examples/notes-app}/demo
  [[ -f $demo/notes-demo.mp4 ]] || { echo "run the demo first: no video under $demo" >&2; return 1; }
  cp "$demo/notes-demo.mp4" "$demo/demo.mp4"
  record_cast scripts/assistant-cast.sh assets/gifs/assistant.gif "$demo"
}

card() {  # <text> <png>
  "$PY" scripts/render_brand.py card "$1" "$2"
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
  local app=${NOTES_APP:-examples/notes-app} tmp; tmp=$(mktemp -d)
  # Start from the three sources only, so the assistant sees a clean folder.
  find "$app" -mindepth 1 -maxdepth 1 ! -name index.html ! -name script.json ! -name director.py \
    ! -name tramoya.toml ! -name snapshots -exec rm -rf {} +
  record_cast scripts/walkthrough-cast.sh "$tmp/terminal.gif" "$app"
  local result; result=$(find "$app" -name '*.mp4' -printf '%T@ %p\n' | sort -n | tail -1 | cut -d' ' -f2-)
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

demo() {
  echo "-- demo video (the profile directed, voiced and assembled by the package itself)"
  local app=${NOTES_APP:-examples/notes-app} runner="uv run --project $PWD"
  [[ -n ${TRAMOYA_BIN:-} ]] && runner="env PATH=$TRAMOYA_BIN:$PATH"
  rm -rf "$app/demo"
  (cd "$app" && $runner tramoya direct tramoya.toml --out demo \
    && $runner tramoya tts --script demo/script.json --lang en --out-dir demo/voices/en \
    && $runner tramoya assemble --video demo/take.webm --marks demo/marks.json \
         --voice-dir demo/voices/en --out demo/notes-demo.mp4 --lang en)
  cp "$app/demo/notes-demo.mp4" assets/video/notes-demo.mp4
  ffmpeg -loglevel error -y -i assets/video/notes-demo.mp4 \
    -vf "fps=8,scale=720:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer" \
    assets/gifs/demo.gif
}

filmstrip() {
  echo "-- filmstrip (one real frame per scene of the demo take, from its marks)"
  local app=${NOTES_APP:-examples/notes-app}
  "$PY" scripts/render_brand.py filmstrip "$app/demo/take.webm" "$app/demo/marks.json"
}

cli_gif() {
  echo "-- cli gif (real recording of the installed command)"
  record_cast scripts/cli-cast.sh assets/gifs/cli.gif
}

init_gif() {
  echo "-- init gif (init from flags, import a recording, the plan, the snapshot gate)"
  record_cast scripts/init-cast.sh assets/gifs/init.gif
}


case "$what" in
  logos) logos ;;
  init) init_gif ;;
  pipeline) pipeline ;;
  demo) demo ;;
  filmstrip) filmstrip ;;
  gif) cli_gif ;;
  assistant) assistant_gif ;;
  walkthrough) walkthrough ;;
  all) logos; pipeline; demo; filmstrip; cli_gif; init_gif; assistant_gif; walkthrough ;;
  *) echo "usage: $0 [logos|pipeline|demo|filmstrip|gif|init|assistant|walkthrough|all]" >&2; exit 2 ;;
esac
echo "-- done"
du -h assets/*.png assets/gifs/*.gif 2>/dev/null
