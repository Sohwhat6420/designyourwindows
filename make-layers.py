"""make-layers.py: cut one image into the layers designyourwindows draws, at your screen's size.

Run it once per image. It writes four files into the output folder (default layers/):
  plate.png    the image, cropped to cover the screen
  fg.png       the subject and the ground nearer than the sky (alpha), drawn OVER the clock
  glass.png    the plate blurred and slightly saturated: the widget panels are cut from it, so they read as
               frosted glass over the wallpaper without any live blur
  layout.json  the clock box (pixels), an ink colour chosen for contrast, and how much of the box the subject covers

Usage:
  python -I make-layers.py <image> [--out layers] [--models models] [--size WxH] [--anchor 0.5] [--clock-width 0.36]
  python -I make-layers.py --sample sample.png [--size WxH]     draws the procedural demo image used in docs/screens

  --size     defaults to the primary screen in physical pixels (Windows); 1920x1080 elsewhere
  --anchor   vertical crop anchor for images taller than the screen: 0 top, 0.5 centre, 1 bottom
Needs: numpy, opencv-python, pillow, and onnxruntime for the cut (not for --sample).
"""
import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

MEAN = np.array([0.485, 0.456, 0.406], np.float32)
STD = np.array([0.229, 0.224, 0.225], np.float32)


def screen_size():
    """Primary screen in physical pixels, ignoring display scaling."""
    try:
        import ctypes
        u = ctypes.windll.user32
        try:
            u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2
        except AttributeError:
            u.SetProcessDPIAware()
        return u.GetSystemMetrics(0), u.GetSystemMetrics(1)
    except (AttributeError, OSError):
        return 1920, 1080


def cover(im, W, H, fy=0.5):
    s = max(W / im.width, H / im.height)
    im = im.resize((round(im.width * s), round(im.height * s)), Image.LANCZOS)
    x, y = (im.width - W) // 2, round((im.height - H) * fy)
    return im.crop((x, y, x + W, y + H))


def norm(arr):
    return ((arr / 255.0 - MEAN) / STD).transpose(2, 0, 1)[None].astype(np.float32)


def subject_mask(sess, rgb, W, H):
    """BiRefNet lite: the salient subject, 0..1."""
    x = norm(np.asarray(Image.fromarray(rgb).resize((1024, 1024), Image.BILINEAR), np.float32))
    y = sess.run(None, {sess.get_inputs()[0].name: x})[0][0, 0]
    return cv2.resize(1 / (1 + np.exp(-y)), (W, H), interpolation=cv2.INTER_LINEAR)


def depth_map(sess, rgb, W, H):
    """Depth Anything V2 Small, normalised so 1 = near."""
    h = 518
    w = max(14, round(518 * W / H / 14) * 14)
    x = norm(np.asarray(Image.fromarray(rgb).resize((w, h), Image.BICUBIC), np.float32))
    d = cv2.resize(sess.run(None, {"pixel_values": x})[0][0], (W, H), interpolation=cv2.INTER_CUBIC)
    lo, hi = np.percentile(d, 2), np.percentile(d, 98)
    return np.clip((d - lo) / (hi - lo + 1e-6), 0, 1)


def scene_mask(depth, W, H):
    """Everything clearly nearer than the far plane that rises from the bottom edge. Floating near things
    (clouds) are dropped: they would cover the top of the digits, which the depth effect should never do."""
    far = np.percentile(depth[: H // 3], 30)
    soft = np.clip((depth - (far + 0.18)) / 0.08 + 0.5, 0, 1)
    _, lab = cv2.connectedComponents((soft > 0.5).astype(np.uint8))
    grounded = list(set(np.unique(lab[-3:, :])) - {0})
    keep = np.isin(lab, grounded).astype(np.float32)
    k = 2 * round(4 * W / 1920) + 1
    return soft * cv2.dilate(keep, np.ones((k, k), np.uint8))


def place_clock(fg, W, H, width):
    """The box the digits fill ("00:00" in a display face is about 3.6 times as wide as it is tall). Prefer the
    centre; the foreground may cover the lower part only, ideally about a fifth of the glyph area."""
    bw = int(W * width)
    bh = int(bw / 3.6)
    best = None
    step = max(2, round(6 * W / 1920))
    for cx in (W // 2, int(W * 0.42), int(W * 0.58)):
        for top in range(int(H * 0.11), int(H * 0.34), step):
            box = fg[top: top + bh, cx - bw // 2: cx + bw // 2]
            upper, occ = box[: bh // 2].mean(), box.mean()
            score = abs(occ - 0.2) + 3 * upper + (0 if cx == W // 2 else 0.06) + 0.15 * top / H
            if best is None or score < best[0]:
                best = (score, cx, top, occ, upper)
    _, cx, top, occ, upper = best
    return {"x": cx - bw // 2, "y": top, "w": bw, "h": bh,
            "occlusion": round(float(occ), 3), "upper_occlusion": round(float(upper), 3)}


def ink_for(rgb, fg, c):
    """A clock colour from the picture itself: deep on a bright sky, near-white on a dark one."""
    reg = rgb[c["y"]: c["y"] + c["h"], c["x"]: c["x"] + c["w"]].reshape(-1, 3).astype(np.float32)
    keep = fg[c["y"]: c["y"] + c["h"], c["x"]: c["x"] + c["w"]].reshape(-1) < 0.3
    reg = reg[keep] if keep.sum() > 500 else reg
    mean = reg.mean(0)
    lum = float(0.2126 * mean[0] + 0.7152 * mean[1] + 0.0722 * mean[2]) / 255
    hsv = cv2.cvtColor(np.uint8([[mean]]), cv2.COLOR_RGB2HSV)[0, 0]
    hue, sat = int(hsv[0]), min(int(hsv[1]), 90)
    if lum > 0.58:
        ink, mode = cv2.cvtColor(np.uint8([[[hue, sat, 60]]]), cv2.COLOR_HSV2RGB)[0, 0], "dark"
    else:
        ink, mode = cv2.cvtColor(np.uint8([[[hue, min(sat, 28), 250]]]), cv2.COLOR_HSV2RGB)[0, 0], "light"
    return {"ink": "#%02x%02x%02x" % tuple(int(v) for v in ink), "mode": mode, "bg_lum": round(lum, 3)}


def glass(rgb, W):
    """The plate behind frosted glass: a wide blur and a little extra colour, as the real material does."""
    g = cv2.GaussianBlur(rgb, (0, 0), 34 * W / 1920)
    hsv = cv2.cvtColor(g, cv2.COLOR_RGB2HSV).astype(np.float32)
    hsv[..., 1] = np.clip(hsv[..., 1] * 1.35, 0, 255)
    return cv2.cvtColor(hsv.astype(np.uint8), cv2.COLOR_HSV2RGB)


def cmd_layers(a, W, H):
    import onnxruntime as ort
    models, out = Path(a.models), Path(a.out)
    for name in ("birefnet-lite.onnx", "depth-anything-v2-small.onnx"):
        if not (models / name).exists():
            sys.exit(f"missing {models / name}: see 'Get the two models' in the README")
    out.mkdir(parents=True, exist_ok=True)
    so = ort.SessionOptions()
    so.log_severity_level = 3
    seg = ort.InferenceSession(str(models / "birefnet-lite.onnx"), so, providers=["CPUExecutionProvider"])
    dep = ort.InferenceSession(str(models / "depth-anything-v2-small.onnx"), so, providers=["CPUExecutionProvider"])
    plate = cover(Image.open(a.image).convert("RGB"), W, H, a.anchor)
    rgb = np.asarray(plate)
    subj = subject_mask(seg, rgb, W, H)
    scene = scene_mask(depth_map(dep, rgb, W, H), W, H)
    fg = np.clip(cv2.GaussianBlur(np.maximum(subj, scene), (0, 0), 1.2 * W / 1920), 0, 1)
    clock = place_clock(fg, W, H, a.clock_width)
    alpha = (fg * 255).astype(np.uint8)
    cut = rgb.copy()
    cut[alpha == 0] = 0  # nothing hidden under a clear pixel: smaller file
    plate.save(out / "plate.png")
    Image.fromarray(np.dstack([cut, alpha]), "RGBA").save(out / "fg.png")
    Image.fromarray(glass(rgb, W)).save(out / "glass.png")
    layout = {"version": 1, "size": [W, H], "clock": clock, **ink_for(rgb, fg, clock),
              "subject_cover": round(float(subj.mean()), 3), "scene_cover": round(float(scene.mean()), 3)}
    (out / "layout.json").write_text(json.dumps(layout, indent=1))
    print(json.dumps(layout))


# ---------- the procedural demo image (no source picture: everything below is drawn from numbers) ----------

def noise(rng, W, H, cells, octaves=5):
    """Fractal value noise in 0..1."""
    acc, amp, tot = np.zeros((H, W), np.float32), 1.0, 0.0
    for o in range(octaves):
        n = max(2, cells * 2 ** o)
        g = rng.random((max(2, n * H // W), n)).astype(np.float32)
        acc += amp * cv2.resize(g, (W, H), interpolation=cv2.INTER_CUBIC)
        tot += amp
        amp *= 0.5
    return np.clip(acc / tot, 0, 1)


def ridge_line(rng, W, base, amp, rough, n=9):
    x = np.arange(W) / W
    y = np.full(W, base, np.float32)
    for k in range(1, n + 1):
        y += amp * rough ** (k - 1) * np.sin(2 * np.pi * (k * 1.7 * x + rng.random()))
    return y


def cmd_sample(path, W, H):
    rng = np.random.default_rng(41)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    v = yy / H
    # sky: deep slate blue at the zenith to a pale amber haze at the horizon
    pos = [0.0, 0.32, 0.56, 0.70, 1.0]
    cols = np.array([(18, 38, 64), (44, 84, 118), (150, 160, 162), (238, 196, 150), (238, 196, 150)], np.float32)
    rows = np.stack([np.interp(np.arange(H) / H, pos, cols[:, c]) for c in range(3)], 1).astype(np.float32)
    sky = np.broadcast_to(rows[:, None, :], (H, W, 3))
    sun = np.exp(-(((xx - W * 0.63) / (W * 0.16)) ** 2 + ((yy - H * 0.60) / (H * 0.16)) ** 2))[..., None]
    rgb = sky + (np.array([255, 226, 186]) - sky) * sun * 0.75
    hz = np.array([232, 196, 160], np.float32)

    def paint(mask, col, haze):
        nonlocal rgb
        c = np.array(col, np.float32) * (1 - haze) + hz * haze
        rgb = rgb * (1 - mask[..., None]) + c * mask[..., None]

    # far ranges, fading into the haze
    for i, (base, amp, haze, col) in enumerate([(0.58, 0.05, 0.62, (92, 108, 128)), (0.63, 0.06, 0.45, (70, 86, 104))]):
        line = ridge_line(rng, W, H * base, H * amp, 0.62)
        paint(np.clip(yy - line[None, :] + 0.5, 0, 1), col, haze)

    # the subject: one sharp peak whose summit rises into the clock's lower half
    px, summit = W * 0.47, H * 0.255
    dx = (xx[0] - px) / W
    # asymmetric: a long shoulder to the left, a steeper fall to the right, a sub-peak on the shoulder
    prof = summit + H * np.where(dx < 0, 0.95 * np.abs(dx) ** 0.95, 1.35 * np.abs(dx)) + H * 0.2 * dx ** 2
    prof -= H * 0.05 * np.exp(-((dx + 0.15) / 0.035) ** 2)
    det = np.zeros(W, np.float32)
    for k in range(1, 60):
        det += (0.010 / k ** 0.8) * np.sin(2 * np.pi * (k * 3.1 * xx[0] / W + rng.random()))
    prof = prof + H * det * np.clip(np.abs(dx) * 8, 0.15, 1)
    mtn = np.clip(yy - prof[None, :] + 0.5, 0, 1)
    rock = noise(rng, W, H, 24, 6)
    # ribs and couloirs radiate from the summit: noise over the angle, slowly varying with distance
    theta = np.arctan2(xx - px, yy - summit + H * 0.02)
    r = np.hypot(xx - px, yy - summit) / H
    knots = 420
    rib = (theta + np.pi) / (2 * np.pi) * knots
    g = rng.random((8, knots + 1)).astype(np.float32)
    g = cv2.GaussianBlur(g, (0, 0), 0.8)
    ri = np.clip(r * 6, 0, 6.99)
    i0, f = np.floor(rib).astype(int) % knots, (rib - np.floor(rib)).astype(np.float32)
    rr, rf = np.floor(ri).astype(int), (ri - np.floor(ri)).astype(np.float32)
    a = g[rr, i0] * (1 - f) + g[rr, i0 + 1] * f
    b = g[rr + 1, i0] * (1 - f) + g[rr + 1, i0 + 1] * f
    gully = np.clip(((a * (1 - rf) + b * rf) - 0.5) * 2.4 + 0.5, 0, 1)
    crest = px + W * 0.05 * (noise(rng, W, H, 6, 4) - 0.5) + (yy - summit) * 0.08
    lit = 1 / (1 + np.exp(-(xx - crest) / (W * 0.006)))
    lit *= np.clip(1 - (yy - summit) / (H * 0.65), 0.15, 1)
    shadow_col, lit_col = np.array([44, 56, 74], np.float32), np.array([186, 146, 118], np.float32)
    face = shadow_col + (lit_col - shadow_col) * lit[..., None]
    depth_fade = np.clip(1 - r * 1.6, 0.35, 1)  # ribs soften towards the valley haze
    face *= (0.80 + 0.32 * rock[..., None]) * (1 + 0.24 * ((gully - 0.5) * depth_fade)[..., None])
    snow_line = summit + H * 0.11 + H * 0.05 * (noise(rng, W, H, 18, 4) - 0.5) * 2 + H * 0.10 * (gully - 0.35)
    snow = np.clip((snow_line - yy) / (H * 0.006), 0, 1)
    snow_col = np.array([112, 132, 158], np.float32) + (np.array([252, 238, 224]) - np.array([112, 132, 158])) * lit[..., None]
    snow_col *= (0.93 + 0.1 * rock[..., None])
    face = face * (1 - snow[..., None]) + snow_col * snow[..., None]
    face = face * (1 - 0.18) + hz * 0.18 * np.clip((yy - H * 0.5) / (H * 0.3), 0, 1)[..., None]
    rgb = rgb * (1 - mtn[..., None]) + face * mtn[..., None]

    # mist pooling in the valley in front of the peak
    mist = np.exp(-((yy - H * 0.70) / (H * 0.05)) ** 2) * (0.55 + 0.45 * noise(rng, W, H, 3, 3))
    rgb = rgb + (np.array([226, 200, 178]) - rgb) * (mist * 0.55)[..., None]

    # near hills with a pine line: the dark foreground
    for base, amp, col in [(0.80, 0.025, (36, 50, 56)), (0.88, 0.02, (20, 30, 34))]:
        line = ridge_line(rng, W, H * base, H * amp, 0.55)
        trees = np.zeros(W, np.float32)
        for tx in rng.choice(W, size=int(W / 7), replace=False):
            hgt, wid = H * (0.02 + 0.035 * rng.random()), W * (0.004 + 0.004 * rng.random())
            lo, hi = int(max(0, tx - wid)), int(min(W, tx + wid))
            span = np.arange(lo, hi)
            trees[span] = np.maximum(trees[span], hgt * (1 - np.abs(span - tx) / wid))
        paint(np.clip(yy - (line - trees)[None, :] + 0.5, 0, 1), col, 0.06)

    rgb += rng.normal(0, 2.2, rgb.shape)  # film grain: no banding in the gradients
    Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8)).save(path)
    print("wrote", path)


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("image", nargs="?")
    p.add_argument("--out", default="layers")
    p.add_argument("--models", default="models")
    p.add_argument("--size")
    p.add_argument("--anchor", type=float, default=0.5)
    p.add_argument("--clock-width", type=float, default=0.36)
    p.add_argument("--sample", metavar="PNG")
    a = p.parse_args()
    W, H = (int(v) for v in a.size.lower().split("x")) if a.size else screen_size()
    if a.sample:
        return cmd_sample(a.sample, W, H)
    if not a.image:
        p.error("give an image, or --sample out.png")
    cmd_layers(a, W, H)


if __name__ == "__main__":
    main()
