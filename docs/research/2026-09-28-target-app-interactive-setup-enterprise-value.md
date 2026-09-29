# tramoya: targeting one app, configuring it interactively, and proving enterprise value

Research note, 2026-09-28. Three inputs: an audit of the codebase at `caa3b02`, a survey of
comparable tools, and a search for documented enterprise uses of automated demo video. Every
external claim carries its source; vendor-published numbers are marked as such.

## 1. Where tramoya stands today

The pipeline is complete (direct, record, tts, assemble, deck, captions, music, cues) and the
assistant (`ask`/`do`) turns plain language into post-production commands. What is missing is the
front of the pipeline: telling tramoya *which application* to demo without writing Python.

| Fact | Evidence |
|---|---|
| The target app lives only inside a hand-written director | `examples/notes-app/director.py:124` builds the URL as a literal |
| `direct` has no `--url`/`--app` flag | `src/tramoya/cli.py:354-362` |
| `open_stage(url, ...)` already takes a URL, nothing surfaces it | `src/tramoya/stage.py:282-322` |
| `AppProcess` can clone and launch a git-hosted app, and nobody calls it | `src/tramoya/stage.py:206-279` |
| No config file, no env var, no `init` subcommand, no wizard | whole `src/tramoya/`; only `TRAMOYA_LLM*`, `ANTHROPIC_API_KEY`, `OLLAMA_HOST` exist |
| The assistant prompt knows commands, not the app | `src/tramoya/assistant.py:37-81` |
| A minimal director is ~25-35 lines, the example is 153 because of boilerplate (local server, arg parsing) | `examples/notes-app/director.py` |
| Narration text is hand-authored (`script.json`) | `src/tramoya/cli.py:364-368` |
| Tests fake Playwright and the LLM transport completely | `tests/test_stage.py`, `tests/test_assistant.py` |

The eight gaps, by leverage: (1) app config surface, (2) app profile data model, (3) `tramoya init`,
(4) `AppProcess` unwired, (5) no selector discovery, (6) assistant unaware of the app, (7) no
interactive loop beyond one y/n, (8) narration not derivable.

## 2. What comparable tools do

| Tool | Input | How the app is chosen | Setup UX | Output | OSS |
|---|---|---|---|---|---|
| Playwright codegen | record clicks | `npx playwright codegen <url>` | flags | runnable script | yes |
| Charm VHS | `.tape` DSL, or `vhs record` | terminal only | declarative file | GIF/MP4 | yes |
| Chrome DevTools Recorder + `@puppeteer/replay` | record in Chrome | any page | built into Chrome | JSON, replayable | yes |
| Stagehand / browser-use / Skyvern | LLM agent from a goal | URL + goal | SDK | run; Stagehand caches resolved steps | yes |
| Tango / Scribe / Guidde | browser extension | web + desktop | click, perform, done | step guide / narrated video | no, $18-23 per user per month |
| Arcade / Storylane / Navattic / Supademo | screen or HTML capture | web app | capture, edit, share | interactive demo | no, enterprise tiers |
| Reprise | full sandbox clone | web app | heavy custom setup | interactive sandbox | no |

Sources: playwright.dev/docs/codegen, github.com/charmbracelet/vhs, github.com/puppeteer/replay,
skyvern.com/blog/browser-use-vs-stagehand-which-is-better, help.tango.ai, arcade.software/post/best-interactive-demo-software-2026, storylane.io/blog/navattic-vs-arcade.

Two patterns matter for a scripted-video CLI:

- **Record once, then edit the generated script.** VHS (`vhs record > file.tape`) and Playwright
  codegen both turn a live session into the same artifact a human would write. Nobody in the
  demo-video space combines that with narration and a per-scene cut, which is exactly tramoya's core.
- **Resolve with an LLM once, replay deterministically.** Stagehand's `act()`/`observe()` cache:
  the model finds the element on the first run, the resolved selector is baked in, and reruns need
  no model and no network. That is the right shape for `tramoya ask|do` if it ever drives the UI.

Interactive setup done well (gh auth login, create-vite, clig.dev): a linear question flow, every
prompt mirrored by a flag, prompts only on a real TTY, non-interactive runs fail with the name of
the missing flag. Sources: cli.github.com/manual/gh_auth_login, deepwiki.com/vitejs/vite/12-project-scaffolding-(create-vite), clig.dev.

## 3. Proposal: three commands and one file

### 3.1 `tramoya.toml`, the app profile

One file at the project root, written by the wizard and read by everything else. Nothing here
requires the user to know Playwright.

```toml
[app]
name = "Notes"
url = "http://127.0.0.1:8000/"          # or launch = "npm run dev" with ready = "http://127.0.0.1:5173"
viewport = [1280, 720]
storage_state = ".tramoya/auth.json"    # Playwright session, captured once by `tramoya login`

[demo]
languages = ["en", "es"]
pace = 1.0
brand = "assets/theme.conf"

[[scene]]
name = "search"
label = "Search filters as you type"
narration = { en = "Type and the list narrows.", es = "Escribe y la lista se acorta." }
```

Where it plugs in: `stage.py` (`open_stage`, `AppProcess` for `launch`), `cli.py direct` (`--url`,
`--launch`, `--storage-state` mirror the file), and the assistant prompt (section 3.4).

### 3.2 `tramoya init`, the wizard

Questions, in order, each backed by a flag so CI can run the same thing with `--no-interactive`:

1. App name.
2. How to reach it: a URL, or a command to launch plus a readiness URL (`AppProcess` finally gets used).
3. Does it need a login? If yes, open a browser, let the user sign in, save `storage_state`.
   Credentials never touch tramoya.
4. Languages for narration.
5. Scenes: either type three to six scene titles now, or choose "record them" (3.3).

Output: `tramoya.toml`, a `director.py` scaffold with one function per scene, `script.json` with
the narration keys, and a `Makefile`/`justfile` target that runs direct, tts, assemble. First video
in one command afterwards. Implementation: `questionary` or `rich.prompt` behind an
`Answers` protocol so tests inject canned answers, the same way `test_assistant.py` fakes the LLM.

### 3.3 `tramoya record <url>`, draft scenes from a real session

Attach Playwright to a headed browser, let the user click through, press a key to mark a scene
boundary, and emit the director script with real selectors. Two entry paths, both cheap:

- Playwright's own codegen recorder, filtered to tramoya's `Stage` verbs.
- Import of Chrome DevTools Recorder JSON: zero install, `@puppeteer/replay` documents the schema,
  Cypress and WebdriverIO already import it.

The generated script is a draft the user edits, not a black box. This is the single change that
removes "write Python" from the first-run path.

### 3.4 The assistant learns the app

Add the profile to the prompt: name, URL, scene list, narration keys. Then `tramoya do "add a
scene that shows the export button"` can propose a scene function. For selectors, follow Stagehand:
the model resolves against the accessibility tree once, the resolved selector is written into the
director, reruns are deterministic. Keep the current guardrails: only `tramoya` commands, dry-run
propagation, preflight.

### 3.5 Guard the demos in CI

Emit an accessibility-tree snapshot per scene next to the video and diff it in CI. A moved button
fails the build instead of shipping a broken demo. This is what makes the enterprise cases below
credible: a demo that cannot rot.

## 4. Enterprise uses that show real value

| Use case | Who | Trigger | Claimed value | Source |
|---|---|---|---|---|
| Validation evidence at test-step level | FDA Computer Software Assurance guidance | each validation run | video accepted as objective evidence instead of a screenshot per step | valgenesis.com/blog/computer-software-assurance-csa-focus-on-records-not-documentation |
| Living test documentation | Playwright traces used as docs | every CI run | always-current docs at no authoring cost | thegreenreport.blog (turn-your-playwright-traces-into-living-test-documentation) |
| Release-note video per release | ngram over Linear | new release | draft video before notes ship, re-rendered in DE/JA/PT | ngram.com/convert/release-notes-to-video |
| Video knowledge base kept in sync | Guidde customer | UI change | 200 articles in 3 weeks, 25 percent fewer support calls, 13 point CSAT lift (vendor-published) | guidde.com/customer-support |
| Training localized at scale | Synthesia and Teleperformance | new module | "100 hours to 10 minutes" per translation (vendor-published) | synthesia.io/case-studies |
| Training localized at scale | HeyGen and Würth | new content | 50 percent less production time, 80 percent lower translation cost (vendor-published) | heygen.com/en-in/customer-stories |
| Sales demos at scale | Navattic customers | page load | 33 percent conversion lift; 3x demo-to-opportunity (vendor-published) | navattic.com/customer/highlights, navattic.com/report/state-of-the-interactive-product-demo-2025 |
| Demo automation | Reprise with Cloudera and Hireology | sales cycle | 75 percent lower acquisition cost; 50 percent faster cycle (vendor-published) | reprise.com/resources/blog/demo-automation-software-ultimate-guide |
| Mandatory recorded demo for procurement | GSA Schedule 36 ERM | vendor onboarding | vendors must submit a self-recorded demo before listing | ezgsa.com/submit-your-schedule-36-erm-demos-now |

Where a scripted, reproducible, CI-friendly generator beats screen-recording SaaS, ranked:

1. **Regulated validation and QA evidence.** The recording must match the build under test. A
   script re-run against real code guarantees it; a screen recording cannot.
2. **Release-note video per tag.** Changelog automation is already CI-shaped (GitLab's changelog
   API); video is the missing artifact.
3. **Localization.** The vendors' headline savings come from not re-shooting per language. tramoya
   already re-voices from one script.
4. **Support knowledge base that never goes stale.** The failure mode of manual video is rot on
   every UI change; a re-run fixes it without anyone noticing it first.
5. **Sales demos.** Real money in the space, but the SaaS value is interactivity and CRM analytics,
   which a linear video does not replace. tramoya wins only the "always truthful" slice.

Honesty notes from the search: no third-party audit of any vendor figure was found; no native
video changelog from GitLab, Linear or Atlassian exists (ngram is a wrapper); mandatory recorded
demos in procurement were confirmed only for GSA Schedule 36.

## 5. Suggested example set for the README and `examples/`

Each one is a folder with `tramoya.toml`, a director and a script, runnable in CI, no private data:

- `examples/release-notes/`: a tagged build of a sample app, one scene per changelog entry, video in
  two languages, triggered by a GitHub Actions workflow on `v*` tags.
- `examples/validation-evidence/`: the same flow recorded with a visible test id and timestamp per
  scene, plus the accessibility snapshot diff, packaged as an evidence bundle.
- `examples/support-kb/`: three short "how do I" videos regenerated by a nightly job.
- `examples/localized-onboarding/`: one script, four voices.

## 6. Order of work

1. `tramoya.toml` + `--url/--launch/--storage-state` on `direct` (small, unblocks everything).
2. `tramoya init` with an injectable answers provider and tests.
3. `tramoya record` via Playwright codegen, then the DevTools Recorder importer.
4. App profile in the assistant prompt; selector resolution with caching.
5. Accessibility snapshot per scene and the CI diff.
6. The four examples, each with its workflow file.
