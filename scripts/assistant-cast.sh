#!/usr/bin/env bash
# Recorded by scripts/render-assets.sh assistant, from examples/notes-app/demo
# after `render-assets.sh demo` (so demo.mp4, marks.json and voices/en exist).
ty(){ printf '\033[1;33m❯ \033[0m'; for ((i=0;i<${#1};i++)); do printf '%s' "${1:i:1}"; sleep 0.035; done; printf '\n'; sleep 0.4; }
clear
ty 'tramoya ask "put some quiet background music under demo.mp4"'
tramoya ask "put some quiet background music under demo.mp4"; sleep 4.5
clear
ty 'tramoya ask "translate the video into French"'
tramoya ask "translate the video into French"; sleep 4
clear
ty 'tramoya do "show me the scenes, then build the video with the English voice" --yes'
tramoya do "show me the scenes, then build the video with the English voice" --yes 2>&1 | cut -c1-108; sleep 4
