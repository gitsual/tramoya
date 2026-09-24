#!/usr/bin/env bash
# Regenerate every rendered asset under assets/ from its source.
#
#   scripts/render-assets.sh            # logos + pipeline diagram + CLI gif
#   TAKE=path/to/take.mp4 scripts/render-assets.sh shots
#                                       # also cut the evidence frames from a real take
#
# Needs: rsvg-convert, magick, asciinema, agg, ffmpeg. The CLI gif is a real
# recording of the installed `tramoya` command, not a mock-up.
set -euo pipefail
cd "$(dirname "$0")/.."

THEME=1b1220,f4e9c8,2e1f33,c9424f,7fb069,e8c66a,6aa6c9,c9424f,b08d57,c8b8a0,3d2b44,d8626e,9fcf85,f0d68a,8ab8d8,d8626e,d8c8a0,ffffff
FONT="JetBrainsMono Nerd Font"

echo "-- logos"
rsvg-convert -w 1520 assets/logo.svg -o assets/logo.png
rsvg-convert -w 360 assets/logo-mark.svg -o assets/logo-mark.png

echo "-- pipeline diagram"
rsvg-convert -w 1800 assets/pipeline.svg -o assets/pipeline.png

echo "-- cli gif (real recording)"
cast=$(mktemp --suffix=.cast)
asciinema rec --overwrite --cols 110 --rows 24 -c scripts/cli-cast.sh "$cast" >/dev/null
agg --theme "$THEME" --font-family "$FONT" --font-size 15 --idle-time-limit 3 "$cast" assets/gifs/cli.gif 2>/dev/null
rm -f "$cast"

if [[ "${1:-}" == "shots" ]]; then
  : "${TAKE:?set TAKE to a demo rendered with tramoya}"
  echo "-- evidence frames from $TAKE"
  ffmpeg -loglevel error -y -ss 8  -i "$TAKE" -frames:v 1 -vf scale=960:-1 assets/shots/stage-meeting.png
  ffmpeg -loglevel error -y -ss 40 -i "$TAKE" -frames:v 1 -vf scale=960:-1 assets/shots/stage-run.png
  ffmpeg -loglevel error -y -ss 40 -i "$TAKE" -frames:v 1 -vf "crop=1920:60:0:1020,scale=960:-1" assets/shots/caption-band.png
  ffmpeg -loglevel error -y -ss 36 -t 6 -i "$TAKE" -vf "fps=8,scale=720:-1" assets/gifs/stage.gif
fi

echo "-- done"
du -h assets/*.png assets/gifs/*.gif assets/shots/*.png 2>/dev/null
