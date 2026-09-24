#!/usr/bin/env bash
ty(){ printf '\033[1;33m❯ \033[0m'; for ((i=0;i<${#1};i++)); do printf '%s' "${1:i:1}"; sleep 0.035; done; printf '\n'; sleep 0.4; }
clear
ty "tramoya --version"; tramoya --version; sleep 1.2
ty "tramoya marks tests/fixtures/legacy-marks-v11.json"
tramoya marks tests/fixtures/legacy-marks-v11.json; sleep 3.5
clear
ty "tramoya assemble --video take.mp4 --marks tests/fixtures/legacy-marks-v11.json --voice-dir voices/es --out demo.mp4 --dry-run | head -4"
tramoya assemble --video take.mp4 --marks tests/fixtures/legacy-marks-v11.json --voice-dir voices/es --out demo.mp4 --dry-run | head -4 | cut -c1-300; printf '\033[2m… 23 more ffmpeg calls, nothing encoded\033[0m\n'; sleep 3.5
ty "tramoya record --out take.mp4 --geometry '0,48 1920x1032' --fps 10 --dry-run"
tramoya record --out take.mp4 --geometry '0,48 1920x1032' --fps 10 --dry-run; sleep 3
