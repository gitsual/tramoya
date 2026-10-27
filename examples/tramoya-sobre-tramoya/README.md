# Tramoya sobre Tramoya

A tutorial of the Windows window recorded by tramoya itself: the profile drives the
`tramoya easy` page (settings, a new video, «ask the AI», the steps the model wrote, the
video), and the model records the notes app from `../notes-app`. It shows two things the
other examples do not:

- the agent from the window (`#objetivo-ia`, `#btn-ia`): a model does the process and
  writes the steps, then the person reviews them as if they had recorded them;
- `wait <selector> | <max seconds>`: the model thinking and the video being made are wait
  windows that `assemble` speeds up to a few seconds with a caption.

```bash
# Windows, Git Bash. Tramoya.exe in PATH or TRAMOYA_EXE; Ollama running (or ANTHROPIC_API_KEY).
bash grabar.sh          # ~6 min: tramoya-sobre-tramoya.mp4 next to this file
```

The outer recording is headless: the window of tramoya exists only as a page. The model's
own browser is a normal window, so run it when nothing else needs the screen.
