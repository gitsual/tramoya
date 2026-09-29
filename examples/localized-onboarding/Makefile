# One take, one video per language.
OUT ?= out
LANGS ?= en es

.PHONY: take videos
videos: take
	for lang in $(LANGS); do \
	  tramoya tts --script $(OUT)/script.json --lang $$lang --out-dir $(OUT)/voices/$$lang && \
	  tramoya assemble --video $(OUT)/take.webm --marks $(OUT)/marks.json \
	    --voice-dir $(OUT)/voices/$$lang --out $(OUT)/onboarding-$$lang.mp4 --lang $$lang; \
	done

take:
	tramoya direct tramoya.toml --out $(OUT)
