# Tramoya on Tramoya

A tutorial of the Windows window recorded by tramoya itself: the profile drives the
`tramoya easy` page (the settings, with the language and the AI and a real connection
test; a new video; «Ask the AI»; the steps the model wrote; the video), and the model
records the notes app from `../notes-app`. It shows two things the other examples do not:

- the agent from the window (`#objetivo-ia`, `#btn-ia`): a model does the process and
  writes the steps, then the person reviews them as if they had recorded them;
- `wait <selector> | <max seconds>`: the connection test, the model thinking and the video
  being made are wait windows that `assemble` speeds up to a few seconds with a caption.

```bash
# Windows, Git Bash. Tramoya.exe in PATH or TRAMOYA_EXE; Ollama running (or ANTHROPIC_API_KEY).
TRAMOYA_HEADLESS=1 bash record.sh      # English, tramoya.toml: tramoya-on-tramoya.mp4 next to this file
TRAMOYA_HEADLESS=1 bash record.sh es   # Spanish, tramoya.es.toml: tramoya-on-tramoya.es.mp4
```

It takes 6 to 60 minutes: the model decides how many turns it needs, and a model that has to
be loaded from disk on every turn (because another program shares the GPU) takes minutes per
turn. The outer recording is headless: the window of tramoya exists only as a page. With
`TRAMOYA_HEADLESS=1` the model's own browser is headless too, so nothing appears on the
desktop and you can keep working. The result of one run is the tutorial at the top of the
main README: [`assets/video/tramoya-tutorial.mp4`](../../assets/video/tramoya-tutorial.mp4).
