#!/usr/bin/env bash
# Recorded by scripts/render-assets.sh walkthrough, from examples/notes-app with
# nothing but the three sources present. One request; the assistant plans
# direct -> tts -> assemble and runs it for real (about two minutes).
ty(){ printf '\033[1;33m❯ \033[0m'; for ((i=0;i<${#1};i++)); do printf '%s' "${1:i:1}"; sleep 0.035; done; printf '\n'; sleep 0.4; }
clear
ty 'tramoya do "record the notes app demo with director.py, give it an English voice and build the final video" --yes'
tramoya do "record the notes app demo with director.py, give it an English voice and build the final video" --yes 2>&1 | cut -c1-108
sleep 4
