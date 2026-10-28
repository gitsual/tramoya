#!/usr/bin/env bash
# Tramoya grabando a Tramoya (Windows, Git Bash). Levanta la ventana de Tramoya sin
# ventana (solo su servidor local) sobre una carpeta de datos limpia, sirve la app de
# notas de los ejemplos, y el escenario de tramoya.toml recorre la interfaz como una
# persona: ajustes, nuevo vídeo, pedírselo a la IA y hacer el vídeo. Solo se graba la
# página, nunca el escritorio; las esperas largas quedan en ventanas de espera que el
# montaje acelera.
#
# Necesita: Tramoya.exe (en el PATH o en TRAMOYA_EXE), python, y un modelo para la IA
# (Ollama en local, o ANTHROPIC_API_KEY). Sale tramoya-sobre-tramoya.mp4 aquí al lado.
set -u
AQUI="$(cd "$(dirname "$0")" && pwd)"
EXE="${TRAMOYA_EXE:-Tramoya.exe}"
LOCAL="$(cygpath "$LOCALAPPDATA")/Temp/tramoya-sobre-tramoya"   # disco local: las uniones no van en discos de red
OUT="$LOCAL/out"
KIT="$LOCAL/kit"      # datos limpios: la lista de vídeos empieza vacía
rm -rf "$OUT" "$KIT"; mkdir -p "$OUT/voices/es" "$KIT/app"
# el kit quiere navegador\ al lado: una unión a la caché local de Chromium si ya existe
# (si no, el exe lo descarga la primera vez)
if [ -d "$(cygpath "$LOCALAPPDATA")/Tramoya/navegador" ]; then
  powershell -NoProfile -Command "New-Item -ItemType Junction -Path '$(cygpath -w "$KIT/navegador")' -Target '$LOCALAPPDATA\Tramoya\navegador' | Out-Null"
fi

# 0. la web que la IA recorrerá: la app de notas de los ejemplos
python -m http.server 8770 --bind 127.0.0.1 -d "$AQUI/../notes-app" >/dev/null 2>&1 &
WEBPID=$!   # el propio python, no una subshell: así el taskkill del final lo cierra

# 1. las voces, antes del escenario: así cada «say» dura lo que dura la frase
python -X utf8 - "$AQUI/tramoya.toml" "$OUT/script.json" <<'PY'
import sys, json, tomllib
prof = tomllib.load(open(sys.argv[1], 'rb'))
script = {f"scene-{s['name']}": dict(s.get('narration', {})) for s in prof['scene']}
json.dump(script, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
"$EXE" tts --script "$(cygpath -w "$OUT/script.json")" --lang es --out-dir "$(cygpath -w "$OUT/voices/es")" >/dev/null

# 2. la ventana de Tramoya (sin ventana: solo el servidor) sobre el kit limpio
export TRAMOYA_KIT="$(cygpath -w "$KIT")"
"$EXE" easy --no-window > "$OUT/easy.url" 2>"$OUT/easy.err" &
EASYPID=$!
for i in $(seq 1 40); do grep -q "http://127.0.0.1" "$OUT/easy.url" 2>/dev/null && break; sleep 0.5; done
URL="$(grep -o 'http://127.0.0.1:[0-9]*/' "$OUT/easy.url" | head -1)"
echo "== Tramoya en $URL"
unset TRAMOYA_KIT

# 3. el escenario recorre la ventana sin cabeza: solo se graba la página. (Con ventana,
#    un gestor de ventanas en mosaico la encoge cuando la IA abre su navegador.)
"$EXE" direct "$(cygpath -w "$AQUI/tramoya.toml")" --url "$URL" --out "$(cygpath -w "$OUT")" --lang es 2>&1 | tee "$OUT/direct.log"
rc=${PIPESTATUS[0]}
# en Git Bash, kill no llega a un programa nativo: taskkill con su PID de Windows
for p in $EASYPID $WEBPID; do taskkill //T //PID "$(cat /proc/$p/winpid 2>/dev/null || echo $p)" //F >/dev/null 2>&1; done

# 4. montaje: cada escena dura lo que su voz; las esperas largas se aceleran
"$EXE" assemble --video "$(cygpath -w "$OUT/take.webm")" --marks "$(cygpath -w "$OUT/marks.json")" --voice-dir "$(cygpath -w "$OUT/voices/es")" --out "$(cygpath -w "$AQUI/tramoya-sobre-tramoya.mp4")" --lang es 2>&1 | tee -a "$OUT/direct.log"
echo "== fin (direct rc=$rc)"
