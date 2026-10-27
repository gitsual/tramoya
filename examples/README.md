# Examples

Every folder is a complete `tramoya.toml` you can copy and point at your own app. All of them
demo the same small notes app in `notes-app/`, so they run anywhere Playwright runs and carry no
private data (the last one needs the Windows exe and a model).

| Folder | Shows | Runs |
|---|---|---|
| `notes-app/` | The demo on the front page: six scenes as a profile (`tramoya.toml`) and the same six as a Python director (`director.py`), plus the approved accessibility snapshots. | by hand, and in this repository's CI (`snapshots diff`) |
| `release-notes/` | One scene per changelog entry, voiced in English and Spanish from one take. | `workflow.yml` on every `v*` tag; videos attached to the release |
| `validation-evidence/` | One scene per test step, each labelled with its id; the accessibility snapshots are diffed against the approved set and the take is kept as the evidence bundle. | `workflow.yml` on every pull request |
| `support-kb/` | Three short "how do I" answers regenerated against the current build. | `workflow.yml` nightly |
| `localized-onboarding/` | One take, one video per language; the cut adapts to each voice. | `make videos` |
| `tramoya-sobre-tramoya/` | Windows: tramoya records its own window, including «ask the AI» (a model does the process on the notes app and writes the steps) and the `wait` windows the montage speeds up. | `bash grabar.sh` with `Tramoya.exe` and Ollama |

The `workflow.yml` files are meant to be copied into `.github/workflows/` of the app's own
repository; they are not wired into this one.

```bash
cd examples/release-notes
tramoya direct tramoya.toml --out out --dry-run   # the plan
tramoya direct tramoya.toml --out out             # the take, marks, script and snapshots
```
