#!/usr/bin/env bash
# What the init gif records: a profile from flags, scenes imported from a
# DevTools Recorder export, the plan, and the snapshot gate. Runs in a scratch
# directory next to the notes app; nothing here needs a browser.
set -u
ty(){ printf '\033[1;33m❯ \033[0m'; for ((i=0;i<${#1};i++)); do printf '%s' "${1:i:1}"; sleep 0.03; done; printf '\n'; sleep 0.4; }
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d); cd "$work"
cat > flow.json <<'JSON'
{"title":"notes","steps":[
 {"type":"navigate","url":"http://127.0.0.1:8770/index.html"},
 {"type":"click","selectors":[["aria/New note"],["#new"]]},
 {"type":"change","value":"Water the plants","selectors":[["#title"]]},
 {"type":"change","value":"home","selectors":[["#tag"]]},
 {"type":"click","selectors":[["#save"]]}]}
JSON
clear
ty "tramoya init my-demo --name Notes --url http://127.0.0.1:8770/index.html --scene 'welcome: A small notes app' --no-interactive"
tramoya init my-demo --name Notes --url http://127.0.0.1:8770/index.html --scene 'welcome: A small notes app' --no-interactive | sed "s#$work/##g"; sleep 3
clear
ty "tramoya import flow.json --into my-demo/tramoya.toml     # a Chrome DevTools Recorder export"
tramoya import flow.json --into my-demo/tramoya.toml | sed "s#$work/##g"; sleep 2.5
ty "cat my-demo/tramoya.toml"
sed -n '/\[\[scene\]\]/,$p' my-demo/tramoya.toml | head -16; sleep 4
clear
ty "tramoya direct my-demo/tramoya.toml --out out --dry-run"
tramoya direct my-demo/tramoya.toml --out out --dry-run; sleep 4
clear
ty "tramoya snapshots diff approved/ out/snapshots"
mkdir -p approved out/snapshots
cp "$root"/examples/notes-app/snapshots/welcome.txt approved/; sed 's/heading "Notes"/heading "Notas"/' approved/welcome.txt > out/snapshots/welcome.txt
tramoya snapshots diff approved/ out/snapshots; sleep 4
rm -rf "$work"
