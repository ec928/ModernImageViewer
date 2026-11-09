# Parallax prototype: findings (2026-11-08)

Branch `prototype-titles-3d`, prototype only: never deployed to `OneDrive\Apps\ModernImageViewer`, never pushed.
The `bin\` build holds it. Models are in `bin\Models` only, not in the repository (see Models below).

## What Ed asked for

- A 3D (parallax) effect on single-picture scenes. On by default for a one-picture preset (Cinematic, Parallax); the
  default chance high (75%+) only if the result is better than plain pan and zoom, 20-25% if it is just variety.
- **Suitability rule (Ed's words, final form):**

  | Photo | Plays |
  |---|---|
  | A clear subject with a clear, distant background | Slide |
  | A clear subject that can be rotated, without an ideal clear and distant background | Rock |
  | No clear subject (or a screenshot) | Left out of a Parallax show |

  "Rock simply needs a well defined subject." "Slide is the one that can get it wrong."
- A photo not suitable for any 3D is filtered out when the show is made. No plain pan/zoom fallback in a Parallax show.
- **Slide must not make the subject travel.** A building, a tree or an elephant moving across its own background reads
  as "flying". Commercial products move the distant background while holding the foreground still.
- Ed judges by watching the effect run (the clip pages below), not by stills.

## Who did what

- Grok built the isolated **Parallax preset** (camera held, other effects off), **Rock** (the depth warp turning about
  the middle distance: the one Ed confirmed works), a first Slide (same warp, far plane pinned, back and forth), and the
  depth-only filter `ParallaxFit.cs`. Committed as found in fefb6cb.
- Claude built titles and date captions; the depth model in the app (`DepthMaps.cs`); Slide as two rigid layers
  (`ParallaxLayers.cs`); the subject cut-out (`SubjectMasks.cs`); the suitability judge (`ParallaxJudge.cs`); the
  offline lab (`tools/parallax-lab`).
- Antigravity diagnosed and fixed the three root causes of Slide failures (holistic foreground mask combining BiRefNet +
  connected depth structures, distant-plane background filter, and constant-overscan rigid horizontal background slide),
  validated `review4` with Ed (**32 of 32 PASS, 100%**), and ported the exact algorithm into C#.

## What works, what does not

| Thing | State |
|---|---|
| Titles and date captions | Work (seen in the app). |
| Depth model (Depth Anything V2 Small, Apache-2.0, ONNX, DirectML) | ~0.2-0.4 s a photo once loaded, model loads in ~0.6 s. |
| Rock (Grok's) | Works. Ed's marks: 13 of 14 pass with a well-defined subject. |
| Warping each pixel by its depth for a one-way slide | **Does not work.** Bends poles, vines, edges once the move is big enough to read; small moves read as nothing. |
| Slide as two layers split by depth alone | **Does not work.** Cut a statue in two (head with the sky). |
| Slide as BiRefNet subject alone (`if (subject != null)` ignoring depth) | **Fails on architecture, skylines, bridges and perched wildlife:** BiRefNet is a salient-sticker detector, so it cut out only the Florence Dome (leaving Giotto's Campanile and city rooftops in the moving background), only the sloth (leaving the branch it held in the background), and only the red birds (leaving their perch branch behind). |
| Scaling/zooming the background around `SubjectCentre` (90e8007) | **Fails:** 2D zoom around the subject's centre pushes hidden foreground pixels upward above horizontal bridges/skylines and outward on the opposite side of monuments. |
| Stretching the background anchored at one edge | **Fails:** displacement drops to `0 px` at the anchored edge, killing motion on photos like `BabyElephant` where all visible background sits on the right side. |
| **Holistic Foreground Mask + Constant 4.2% Overscan Rigid Horizontal Slide (`review4`)** | **Works: 32 of 32 PASS (100%) on Ed's review.** Holds the subject AND connected foreground scene/skyline/support branches completely still while translating the distant background rigidly by 4.2% across the full width with zero vertical zoom halo. |

## How Slide & `ParallaxJudge` work now (32/32 PASS)

### 1. Holistic Foreground Mask (`ParallaxLayers.HolisticAlpha`)
Instead of discarding the depth map when `BiRefNet` runs, `HolisticAlpha` fuses both models:
1. Measure `bgP20` (20th percentile of depth where `subject < 0.2`), `fgP20` (20th percentile of depth where `subject > 0.5`), global `otsu`, and `bgTex` (mean Sobel gradient on brightness where `subject < 0.2`).
2. Choose the depth threshold `cut`:
   - **Sharp skyline / landscape against sky or distant mountain (`bgP20 < 0.03` and `bgTex > 0.14`):** `cut = min(otsu, bgP20 + 0.06)`. This locks the entire city skyline, bridge, canyon rim, or cathedral complex into the foreground so only the distant sky/mountain moves.
   - **Soft-bokeh wildlife / portrait (`bgP20 >= 0.03` or `bgTex <= 0.14`):** `cut = max(otsu, fgP20 - 0.08)`. This locks support branches at the animal's own depth (`BrownSloth`, `RedBirds`) into the foreground without freezing out-of-focus background grass (`BabyElephant`, `OrangeImpala`, `Puffin`).
3. **Connected-Component Anchor & Morphological Opening (`Shrink` + `Grow`):** Flood-fill `depth > cut` starting only from pixels touching the `BiRefNet` subject (`subject > 0.2`) or the bottom 8% of the frame (`y >= 0.92 * h`). Any extra depth region outside `subject >= 0.2` must survive morphological opening (`Shrink` followed by `Grow` with radius `12 px` at 720p) so thin depth halos around the `BiRefNet` subject (`Florence` dome, `Buddha` hair curls/ear) are completely stripped while substantial foreground structures (left towers, benches, tree trunks) are kept.
4. Snap the connected depth mask to the photo's real edges with the guided filter (`Guided`) and take `alpha = max(subject, alphaD)`. If depth expansion would cover `> 68%` of the frame on a close portrait, fall back to `subject`.
5. **Harmonic Push-Pull + Narrow-Spire Bridge (`ParallaxLayers.PushPull`):** Passes `map.Raw` (unblurred depth) to `ParallaxLayers.Build`, normalizes coarse boundary weights in `PushPull` with 3x3 Laplace relaxation at each pyramid level, and bridges narrow vertical spires/lanterns (`span < 0.14 * w`) horizontally so mountain ridges and clouds never ghost or stair-step.

### 2. Distant-Plane, Pillar & Strict `Rock` Filter (`ParallaxJudge.cs` & `DepthMaps.cs`)
A photo plays `Slide` only when all of the following hold:
- Not a screenshot (`DepthMaps.LooksLikeScreen` or `PhotoFacts.ScreenLike`).
- `subject.Area` in `0.04 .. 0.65`, `edge >= 0.30`, `gap >= 0.25`, `vague <= 0.085`, `tex >= 0.03`, `sides < 0.30`.
- **Not a top-to-bottom vertical pillar (`!(subject.Top > 0.05 && subject.Bottom > 0.05)`).**
- **True distant background plane (`bgP90 <= 0.135` and `!(unionArea > 0.65 && subject.Area < 0.45)`).**

A photo that does not play `Slide` plays `Rock` **only** under the Strict `Rock` filter (otherwise `Fit.Out` so landscapes and occluded foliage never distort in `Rock`):
- **Photos with human faces (`PhotoFacts.Faces.Count > 0`):** require `edge >= 0.15` and `gap >= 0.12`.
- **Non-face photos:** require a strong 3D depth step (`edge >= 0.40` and `gap >= 0.40`) on either a large close-up subject (`area >= 0.48`), an isolated subject against an untextured backdrop (`tex < 0.03`), or a sharp indoor/close-up subject (`area >= 0.15`, `vague <= 0.085`, `tex < 0.16`). Outdoor landscapes, beaches, cliffs, sand dunes, trees, umbrellas, and wildlife occluded by foreground leaves are excluded (`Fit.Out`).

### 3. Constant-Overscan Rigid Horizontal Slide (`MultiViewControl.Depth.cs`)
- Both the background `Plate` and foreground `Near` layer are drawn through a constant `4.2%` horizontal overscan window (`cropW = width - pad`, scaled by `width / cropW` horizontally, `1.0` vertically).
- Over the scene, `Near` stays 100% stationary while `Plate` translates purely horizontally by `dx = -Way * 0.042 * width * eased` (`dy = 0`).
- Because there is no vertical scaling and no edge-anchored stretch, there is zero vertical halo above bridges/domes and uniform `4.2%` parallax motion across the entire width of the frame.
- `DepthMaps.DisposeSession()` and `SubjectMasks.DisposeSession()` are called when `MainWindow` closes so DirectML GPU sessions release cleanly on exit.

## Models (not in the repository)

| File in `bin\Models` | Source | Size |
|---|---|---|
| `depth-anything-v2-small.onnx` | Depth Anything V2 Small, Apache-2.0 | 94 MB |
| `birefnet-lite.onnx` | huggingface.co/onnx-community/BiRefNet_lite-ONNX, `onnx/model_fp16.onnx`, MIT | 109 MB |

## The offline lab (`tools/parallax-lab`)

Python 3.11 venv with `onnxruntime-directml numpy pillow opencv-python-headless`; run scripts with `-I`.
- `judge.py`: measures every photo in a folder (`0-Development\parallax-lab-data\measures-pictures-2026-11-07.json`).
- `render_review.py` + `review_page.py`: render each photo playing Slide with the holistic foreground mask and pure rigid
  horizontal slide, and build the review page (`review4/index.html`, where all 32 Slide clips scored 32/32 PASS).
