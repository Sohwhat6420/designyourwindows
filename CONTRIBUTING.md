# Contributing

Thanks for looking. designyourwindows has been measured on one laptop, so reports from other screens, scaling settings
and Windows versions are the most useful things you can send.

## Build and try it

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
python -I make-layers.py --sample sample.png
python -I make-layers.py sample.png
.\bin\designyourwindows.exe --render try.png --time 2026-11-11T09:41
```

`--render` writes a frame to a file and leaves your wallpaper alone, so it is the quickest way to check a change.
The cut needs the two models in `models/`; see "Get the two models" in the README.

CI runs the same steps on every push and keeps the rendered frame as an artifact. Please attach a `--render` frame
to any PR that changes what is drawn.

## Reporting a problem

Open an issue with:

- the Windows version, the screen resolution and the scaling (Settings → Display);
- the mode (minute or static), and whether the dock was on;
- what you expected and what you saw, ideally with a `--render` frame;
- the last lines of `%LOCALAPPDATA%\designyourwindows\log.txt`.

**Leave out your own photos if you'd rather not share them.** The `--sample` mountain reproduces most layout problems.

## Pull requests

- Keep them small and focused, with a one-line reason in the title.
- Keep the idle cost low. If a change touches the minute engine, say how you measured CPU and memory before and after
  (the README's "Measurements" section has the method).
- No new runtime dependencies for the exe: it builds with Windows' own compiler, and that is the point.
- Widgets show only readings that are true when you see them. Nothing personal is read: no accounts, no events, no
  location lookup.
- British English in docs and UI text.

## Good first issues

Look for the [`good first issue`](https://github.com/Sohwhat6420/designyourwindows/labels/good%20first%20issue) label,
and ask in the issue if anything is unclear.
