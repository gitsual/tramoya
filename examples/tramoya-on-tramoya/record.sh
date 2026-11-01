#!/usr/bin/env bash
# Tramoya recording Tramoya (Windows, Git Bash). Starts the Tramoya window without a
# window (only its local server) on a clean data folder, serves the notes app of the
# examples, and the stage in tramoya.toml walks through the interface like a person:
# settings, a new video, asking the AI and making the video. Only the page is
# recorded, never the desktop; the long waits become wait windows the montage speeds up.
#
#   bash record.sh            # English: tramoya.toml, the window in English, Zira's voice
#   bash record.sh es         # Spanish: tramoya.es.toml, the window in Spanish, Helena's voice
#
# Needs: Tramoya.exe (in PATH or TRAMOYA_EXE), python, and a model for the AI
# (Ollama locally, or ANTHROPIC_API_KEY). Writes tramoya-on-tramoya[.es].mp4 next to this file.
set -u
LANGUE="${1:-en}"
AQUI="$(cd "$(dirname "$0")" && pwd)"
PERFIL="$AQUI/tramoya.toml"; SALIDA="$AQUI/tramoya-on-tramoya.mp4"
if [ "$LANGUE" != "en" ]; then PERFIL="$AQUI/tramoya.$LANGUE.toml"; SALIDA="$AQUI/tramoya-on-tramoya.$LANGUE.mp4"; fi
EXE="${TRAMOYA_EXE:-Tramoya.exe}"
LOCAL="$(cygpath "$LOCALAPPDATA")/Temp/tramoya-on-tramoya-$LANGUE"   # a local disk: junctions do not work on network drives
OUT="$LOCAL/out"
KIT="$LOCAL/kit"      # clean data: the list of videos starts empty
rm -rf "$OUT" "$KIT"; mkdir -p "$OUT/voices/$LANGUE" "$KIT/app"
# the kit wants navegador\ next to it: a junction to the local Chromium cache if it exists
# (otherwise the exe downloads it the first time)
if [ -d "$(cygpath "$LOCALAPPDATA")/Tramoya/navegador" ]; then
  powershell -NoProfile -Command "New-Item -ItemType Junction -Path '$(cygpath -w "$KIT/navegador")' -Target '$LOCALAPPDATA\Tramoya\navegador' | Out-Null"
fi

# 0. the website the AI will go through: the notes app of the examples
python -m http.server 8770 --bind 127.0.0.1 -d "$AQUI/../notes-app" >/dev/null 2>&1 &
WEBPID=$!   # python itself, not a subshell: so the taskkill at the end closes it

# 1. the voices, before the stage: so each «say» lasts as long as its sentence
python -X utf8 - "$PERFIL" "$OUT/script.json" <<'PY'
import sys, json, tomllib
prof = tomllib.load(open(sys.argv[1], 'rb'))
script = {f"scene-{s['name']}": dict(s.get('narration', {})) for s in prof['scene']}
json.dump(script, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
"$EXE" tts --script "$(cygpath -w "$OUT/script.json")" --lang "$LANGUE" --out-dir "$(cygpath -w "$OUT/voices/$LANGUE")" >/dev/null

# 2. the Tramoya window (no window: only the server) on the clean kit, in the language
export TRAMOYA_KIT="$(cygpath -w "$KIT")"
"$EXE" easy --no-window --lang "$LANGUE" > "$OUT/easy.url" 2>"$OUT/easy.err" &
EASYPID=$!
for i in $(seq 1 40); do grep -q "http://127.0.0.1" "$OUT/easy.url" 2>/dev/null && break; sleep 0.5; done
URL="$(grep -o 'http://127.0.0.1:[0-9]*/' "$OUT/easy.url" | head -1)"
echo "== Tramoya at $URL"
unset TRAMOYA_KIT

# 3. the stage walks the window headless: only the page is recorded. (With a window, a
#    tiling window manager shrinks it when the AI opens its own browser.)
"$EXE" direct "$(cygpath -w "$PERFIL")" --url "$URL" --out "$(cygpath -w "$OUT")" --lang "$LANGUE" 2>&1 | tee "$OUT/direct.log"
rc=${PIPESTATUS[0]}
# in Git Bash, kill does not reach a native program: taskkill with its Windows PID
for p in $EASYPID $WEBPID; do taskkill //T //PID "$(cat /proc/$p/winpid 2>/dev/null || echo $p)" //F >/dev/null 2>&1; done

# 4. montage: each scene lasts as long as its voice; the long waits are sped up
"$EXE" assemble --video "$(cygpath -w "$OUT/take.webm")" --marks "$(cygpath -w "$OUT/marks.json")" --voice-dir "$(cygpath -w "$OUT/voices/$LANGUE")" --out "$(cygpath -w "$SALIDA")" --lang "$LANGUE" 2>&1 | tee -a "$OUT/direct.log"
echo "== done (direct rc=$rc): $SALIDA"
