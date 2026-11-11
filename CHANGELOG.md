# Changelog

Dates are in Singapore time. The project is pre-1.0, so anything may change between minor versions.

## 0.1.0 (11 Nov 2026)

The first public release.

- **Depth effect.** `make-layers.py` cuts your image into a plate, a foreground and a pre-blurred glass layer at your
  screen's size, using BiRefNet lite and Depth Anything V2 Small on the CPU. `--sample` draws a procedural mountain
  if you have no image to hand.
- **One 62 KB native exe**, built by `build.ps1` with the C# compiler that ships inside Windows.
- **Minute mode.** The full frame is rendered once. Each minute only the time strip and widget rows are redrawn, and
  only those bitmap rows are written: 38 ms of work, then a kernel timer until the next minute. Idle cost is 0.057 %
  of one core.
- **Static mode.** Your text (or the date) is drawn once and set as the wallpaper, and nothing is left running.
  `--install --static` re-renders at midnight.
- **Glass widgets.** The date, a month calendar with the ISO week, the battery, and optional Open-Meteo weather (no
  key).
- **A macOS-style dock** with DWM acrylic. It is configured in `dock.json`.
- **`--install` / `--uninstall`.** A per-user scheduled task, no administrator rights needed. Uninstalling puts the
  previous wallpaper back.
- **`--render`** writes one frame to a file and leaves the wallpaper alone.
- **CI** builds the exe, cuts the sample with both models and renders a frame on every push.
