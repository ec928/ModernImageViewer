# 3D ruleset

What a picture does in a 3D show. The sections "Motions", "The depth map", "Who plays" and "How Sway is drawn" describe v0.35.4 (2026-11-12). Everything after them is the history that led there, kept because it records what was tried and why it was dropped.

The code is `ParallaxJudge.cs`, `LayoutComposer.cs` (`SurpriseOptions`), `MultiViewControl.Depth.cs`, `SwayDepth.cs`, `Shaders\SwaySplat.hlsl`, and `extensions\Parallax\DepthMaps.cs`.

## Motions

A picture plays one motion, or it is left out of the show. It is not shown as a flat photo inside that show.

| Motion | What moves | What stays still | Travel |
|---|---|---|---|
| Slide | The background, once, in one direction. The subject and the scene connected to it are a cutout. | The cutout. | 4.2% of the width (`SlideTravel` 0.042). |
| Sway | The whole picture. Near and far move opposite ways, then it comes back. The judge calls this Rock. | The median depth. | The Sway depth setting: 4% by default, 0.4% to 6%. The same for every Sway picture. |
| Pan | The same path as Sway, used when the camera move is already larger than 3% of the width. | The median depth. | 3.2% of the width (`DepthTravel` 0.032). Drawn with the displacement warp, not the forward warp. |

Slide does not use the sway percent or the swing time. Sway and Slide are not combined on the same picture. One swing is center, one side, the other side, center. Engine settings (from the Show card) > Camera has two sliders for Sway: **Sway depth** (how far, 0.4% to 6%, default 4%) and **Each swing** (how long one swing takes, 1.5 s to 12 s, default 4.25 s). A settings file without a key gets the default.

The "3D debug" readout has no switch in the app since the Settings (gear) row was removed, and a saved setting no longer turns it on. Its code remains: when on, it shows `Sway x.x%`, `Slide`, or `Pan`, and `Filter t s`. The percent is the sway depth the frame uses. Filter times around 0.7–1.3 s on this machine are the healthy range.

## How Sway is drawn

Ed, 2026-11-11: with this drawing, 20 of the 22 Sway pictures in the Pictures show took 4%, the slider's top then, and 6% was fine on all but one. So the amount is one setting for every picture. The per-picture formula in "Sway amount" below is gone.

**Forward warp** (`Shaders\SwaySplat.hlsl`, a Win2D pixel shader). Each photo pixel lands at its own position plus shift × (depth − pivot), along the row. For each output pixel the shader walks the sources within reach, finds every one that lands there, and keeps the nearest. An edge is drawn once. The old displacement warp read depth where a pixel lands rather than where it came from, so where depth jumped faster than the shift it drew edges twice: ghost ear outlines with grass through them, hair dragged into the carpet. That old warp still draws Pan, and draws Sway if the shader cannot be made (logged to `play-timing.log` when that log is on).

**Gaps.** Where a moving edge uncovers background the photo never saw, the shader finds where the background proper starts beyond the edge and mirrors it into the gap. Stretching the edge across the gap instead made ears grow (`ed.jpg`). Mirroring about each segment's own end gave a barcode. A mirror that would reach something nearer (the other ear) repeats the background edge instead.

**Crisp outline** (`SwayDepth.Crisp`, from the subject mask the judge already made). The depth map fades from subject to background, over a few of its pixels or many. Within 3% of the width of the mask's edge: inside the mask a pixel takes the depth of the subject just inside it, if that is nearer than its own; outside it takes the farthest depth within that distance. Elsewhere the depth is unchanged. Without this, ears sat half in each depth and vanished at both peaks (`ed.jpg`), and hair whose depth faded tore (`IMGP0801.JPG`). Taking the nearest depth anywhere within reach, the first version of the wider rim, lifted a profile's lips to the shoulder in front of them (`Let_Me_Tell_You[1].jpg`). `tests\...\SwayDepthTests.cs` holds the hair and lips cases, and the v0.35.4 cases below.

Ed's verdict on v0.35.0: not perfect, but good enough. Left over: a slight stair-step on some outlines, a few dark specks where a shoulder meets a small background gap, and a thin dark crescent beside a nose in profile.

**Outline clean-up (v0.35.4).** `SwayDepth.Crisp` also takes the photo's pixels and uses them on the outline. Built on `IMGP0801.JPG` and checked on `IMGP0997.JPG` and `ed.jpg`, judged by Ed from side-by-side swing videos, 2026-11-12: better on `IMGP0801.JPG` and `ed.jpg`. On `IMGP0997.JPG` it is better, but a small dark notch is left where the hair meets a navy cushion stripe.
- *Soft edge.* The photo's hair fades into the wall over a few pixels where the mask is under 0.5. At the wall's depth those pixels stayed behind as a dark ghost of the outline when the head swung away. Now the outline is the mask blurred a little and cut at 0.1, 1-2 px outside the 0.5 line, and every pixel inside it takes the subject's depth. Taking the mask's own ragged 0.02 line instead made the whole outline shimmer.
- *No dip.* Pixels at the old 0.5 edge must take the subject's depth too. Left half-way, they formed a one-pixel valley behind the hair, covered and uncovered every frame: shimmer. The subject's depth past the mask is carried out from inside, then the nearest within one depth-map cell.
- *Stray hair.* Pixels within 2% of the width outside the mask join the subject if their colour is nearer the hair's than the wall's beyond, but only beside hair: subject no brighter than 40 and no more coloured than 15 (of 255). Without that limit, dark shadow beside an arm joined the arm. Joined hair moves as one piece, its gaps closed: as a patchwork it streaked. The dark smear beside `IMGP0801.JPG`'s hair was these wisps, left on the wall and copied into the gap.
- *Small enclosed holes.* Background the subject fully encloses, under 0.25% of the picture, keeps the v0.35.0 depth. Made crisp, the cushion between `IMGP0997.JPG`'s hand and tummy was filled from the hand in pink specks. The limit comes from these pictures alone: that hole is 0.19%, and the gaps between `ed.jpg`'s legs and between an arm and a body (0.35-0.45%) must stay out, or a pale line comes back beside the trousers.
- *Shader.* Where a gap is only partly filled, the shader now mixes the edge's colour with the fill's colour. It used to sample one point part way between them, which landed on a stray pixel that changed every frame.
- *Cost.* About 0.1 s more on a 540 px photo and 0.6 s more on a 960 x 1715 one, before the 3D starts (Release build, measured off screen).
- *Tried and dropped.* Growing the whole outline 0.4% (v0.35.3: carried strips of background with arms and hair). Skipping 0.6% of the gap fill (v0.35.2: no visible change). Joining the lit rim beside the hair (carried the leather's bright bands). Cutting the outline at 0.2 or 0.3 (doubled the flicker on `ed.jpg`).
- *How to check a change here.* `MultiViewControl.SwayCapture.cs` saves the app's own inputs and frames when the folder `%LocalAppData%\ModernImageViewer\sway-capture` exists. The off-screen probe reproduced those frames exactly. Judge a change from whole-swing videos of every affected picture, and count pixels that flip and flip back for shimmer.

## The depth map

Depth Anything's output is stretched to 0–1 per picture before any of the numbers below. A higher value is closer. The stretch throws away how far the scene was in the world. A chair just behind a baby and a sky behind a vault can both fill the whole range.

Two maps are kept.

| Map | What it is | What uses it |
|---|---|---|
| Raw | The stretched values, as bytes. | Edge, gap, and the Slide cut. |
| Grey | Raw, then a 3×3 max, then a 3×3 blur. | Drawing Sway and Pan (Sway through `SwayDepth.Crisp`), and the median pivot. |

**Edge** is the median depth on the subject's rim minus the median on the ring of background just outside it. **Gap** is the median of all foreground minus the median of all background. Both are fractions of Raw. Foreground is the subject mask, alpha above 127. Texture is a Sobel on the photo's brightness in that ring, scaled by 1/1.08 because the measure was calibrated on 768-wide photos and the depth map is about 518 wide.

`RawSpan`, the unstretched range of the model output, is stored and not used for sway or for who plays.

## Who plays

Ed, 2026-11-07: a clear subject with a distant background plays Slide. A clear subject that can be rotated, without that background, plays Sway. Anything else is left out.

The subject mask has to be clear. A subject that covers more than 88% of the picture, or touches 70% of the sides, is a close-up: since 0.35.1 it plays Sway without the gate below (Ed's 2026-11-07 marks passed 12 of the 14 such photos he marked). Before 0.35.1 it was left out. A subject too small, or an outline the mask is unsure of, is left out. No background around the subject leaves it out.

| Who | Admitted when | Otherwise |
|---|---|---|
| A face | Edge at least 0.15 and gap at least 0.12. | Left out. "People against a flat background." |
| No face | Edge at least 0.30 and gap at least 0.25. | Left out. "Small depth step at its edge." |

A picture that passes that gate is then tried as Slide. Slide also requires all of these. Since 0.35.1, failing any of them makes the picture Sway, face or not.

| Slide check | Threshold | Fail |
|---|---|---|
| Edge | At least 0.30 | Sway |
| Outline | Vague at most 0.085 | Sway |
| Background texture | At least 0.03 | Sway. A plain background is this fail. |
| Subject area | At most 65% | Sway |
| Sides touched | Under 30% | Sway |
| Top and bottom | Not both over 5% | Sway |
| Far background | 90th percentile of the remaining background at most 0.135 | Sway |
| Ground | The connected foreground may not cover more than 65% when the subject itself is under 45% | Sway |

Before 0.35.1 a non-face picture that failed one of these Swayed only if it also passed a close-subject test (edge and gap at least 0.40, and area at least 48%, texture under 0.03, or a sharp close subject), and top-and-bottom sent it out. That test was there because the old displacement warp tore outdoor scenes. On Ed's 2026-11-07 marks, the Slide checks were leaving out 20 pictures he had passed, against 9 he had failed.

On the 73 clips marked 2026-11-07, none of those measures separated Slide's passes from its fails. The best, the outline, got 46 of 59. The fails were mostly buildings and trees.

### The Slide cut added 2026-11-10

A picture that passes every row above still has to be one subject on a clean background. This is `SlideIsOneCleanCut`. Failing it means the picture is not shown as Slide. Since 0.35.1 it Sways; before, it was left out.

| Check | Threshold |
|---|---|
| One subject | A second foreground piece of 2% of the picture or more is another subject. A smaller speck does not count. Pieces are 4-connected. |
| Solid | That one piece fills at least 40% of its own box. |
| Clean background | On the background, the 90th percentile of Raw minus the 10th is at most 0.28. |

One solid shape against a clean sky can still pass. The models do not know a building from an animal. A wrong outline on that one shape can still play.

If the Slide layers are built and the subject is not at least 0.08 in front of the rest, the picture Sways instead.

Reading 4% does not leave a picture out. That exclusion was published to bin at 20:19 on 2026-11-10 and taken out the same evening. 4% was then the most the sway would go (6% since 0.35.0). It is not a reason to drop a picture.

## Sway amount (history: removed in 0.35.0)

This was the per-picture formula the 2026-11-10 builds played. Ed's maxima on 2026-11-11 showed it did not predict what a picture could take (see "Ed's sway maxima" below), and the forward warp made it unnecessary. The code and its tests were removed in 0.35.0.

Taken once, when the scene is built, from Grey and from the gap on the judgement. The frame uses that number. Three steps, in order.

**1. Local step, the most the picture may take.** On Grey, the 90th percentile of the absolute change across 8% of the width (at least 2 pixels), horizontal and vertical. A step of 0.40 is the 4% cap. A smaller step is that fraction of 4%. A second sample at 25% of the width takes a little off only when the extra change is more than nothing and less than half of what a straight slope would add. The trim is 0.02 per 1.0 of extra depth, and at most 0.8 points. A steady slope is left alone. One edge, the same jump at both spans, is left alone.

**2. Gap, which may only hold that amount down.** Gap 0.20 or less leaves the cap in place. Gap 0.75 or more falls to 0.4%. A straight line between. The picture plays the lower of this and the local step. A small gap cannot turn a mild step up.

**3. Floor.** If that lower number is under 1.6%, the picture plays 1.6%. 0.4% only vibrates. The ceiling stays 4%. A missing gap does not hold the picture down. The local step is used, then the floor.

The gap of 0.49 is 2.1% on step 2, which is above the floor, so a picture with that gap stays at 2.1% when its local step is at least 2.1%. That is the abbey case below. It was not read off the screen after this build.

Hair at the edge of a Sway is left as it is. The Grey soften is what opens that fringe at full swing. Filling the gap would invent pixels.

## Stress test, 37 scenes

Pictures library, preset 3D, the local-step sway and the Slide rules from before the cut above. Ed, 2026-11-10. Most of the failures were Slide. Some were Sway. Scene numbers are this show's. They are not the numbers in the 27-scene test.

| Mark | Scenes |
|---|---|
| Full fail | 21, 23, 24, 25, 28, 29, 30, 31, 34 |
| Partial fail | 14, 17, 20, 32, 36 |

14 of 37. A scene 50 was also named. This show stops at 37, so 50 is not in it. The vault is scene 30. The giraffes are scene 29. The other scene numbers were not tied to filenames in the log.

Pictures watched on that ruleset, with the readout he sent:

| Picture | Readout | What he said |
|---|---|---|
| Baby in the star onesie | Sway 1.4% | Fair for the formula. A close-up with little behind it should be able to take 4%. The formula was holding it down. |
| Two adults in the stone ruins | 2.8%, then 0.9% | 2.8% was too high. 0.9% was way too low. His mark was about 1.6%. After the slope was left alone the aim was about 1.4%. He did not send a new readout. |
| Girl with the lollipop | Sway 2.9% | The bend was acceptable. The hair fringe at both ends of the swing stays. |
| Stone vault against the sky | Sway 4.0% | Shears. It cannot take 4%. Excluding other pictures because they read 4% was rejected. 4% is the lenient end. |
| Giraffes | Slide | Rectangular blocks and a seam. The still photo is clean. |

The snake tight crop and the strawberry girl had been Sway 3.3%, which was slightly too high. The full step was moved from 0.36 to 0.40 before the 2.9% readout.

## Stress test, 27 scenes

Same library, after the gap was made the only sway input and the Slide cut above was added. The show went from 37 scenes to 27. He did not mark the ten that left. The giraffes are not in the 27. The complaints he sent were all about the sway amount.

The order below is the order the passing pictures were judged in `play-timing.log` at 22:34–22:36, with `Stand_Back![1].jpg` already on screen as scene 1. Scenes 22 and 24 match the screenshots he sent (the impala and the rays, both reading Sway 0.4%). The other rows are that same order. The percent is the gap run through the curve that was playing, which depended only on the gap. Only 22 and 24 were read off the screen. The others were not.

| Scene | File | Edge | Gap | That curve | What he said |
|---|---|---|---|---|---|
| 6 | `ed - Copy.jpg` | 0.23 | 0.33 | 3.1% | Too high. Used to be fine. |
| 7 | `ed.jpg` | 0.23 | 0.33 | 3.1% | Too high. Used to be fine. |
| 10 | `Luxor004.jpg` | 0.22 | 0.22 | 3.9% | Too high. |
| 11 | `Luxor005.jpg` | 0.23 | 0.30 | 3.3% | Too high. |
| 16 | `photo.JPG` | 0.36 | 0.66 | 1.0% | Better when the sway was higher. |
| 22 | `20250428_OHR.OrangeImpala_…jpg` | 0.71 | 0.82 | 0.4% | Vibrates. The screenshot reads 0.4%. |
| 23 | `20250806_OHR.SweetheartAbbey2025_…jpg` | 0.49 | 0.49 | 2.1% | Too high. The stone shears. |
| 24 | `20250814_OHR.SpottedEagleRay_…jpg` | 0.87 | 0.89 | 0.4% | Vibrates. The screenshot reads 0.4%. |
| 26 | `20251103_OHR.MexicoJelly_…jpg` | 0.77 | 0.74 | 0.5% | Better when the sway was higher. |
| 27 | `20260303_OHR.BabyElephant_…jpg` | 0.64 | 0.73 | 0.5% | Vibrates. |

Scenes 6, 7, 10, and 11 are the small-edge portraits. The judge already calls that edge a small step. The previous amount followed the step and kept them mild. The gap is small too, so a gap-only curve treated them as close-ups and turned them up.

Scenes 22, 24, 26, and 27 have a large gap, so the same curve pinned them at 0.4–0.5%. That amount does not read as a sway.

Scene 23 has less gap than those four, so every curve that only falls as the gap rises gives the abbey more sway than the impala. He wants the abbey lower than 2.1% and the impala up off 0.4%. One gap curve cannot do both. Flattening the line to lift the impala also lifts the abbey.

## What was rejected

| Idea | When | Why it is not in the build |
|---|---|---|
| Leave out every picture whose sway sits on 4%. | Published 20:19, removed 21:32. | 4% is the lenient end. Pictures that can take it were dropped. The vault shears because it is at the maximum, not because the maximum is a bad picture. |
| Sway follows only the gap. Little behind, up to 4%. A lot behind, down to 0.4%. | The 22:28 bin. Replaced at 23:05. | The 27-scene test. Mild portraits were turned up and sheared. Far-background pictures vibrated. The abbey stayed above the impala. |
| Fill the hair fringe on the lollipop portrait. | Asked, then dropped. | The fill invents pixels and can tear hair, ears, and shoulders, and it would cut other edges. |
| Play Slide and Sway together on one picture. | Asked. | The two motions fight on the same pixels. |

## What this bin does about that test

Ed chose this on 2026-11-10, after the three options were put to him.

The local step is the cap, so scenes 6, 7, 10, and 11 cannot be turned up by their small gap. They go back to the amount the local step gives, which is the amount that used to be fine. The new percent was not read off those pictures after the change.

The gap can still hold a picture down. Anything under 1.6% is lifted to 1.6%, so scenes 22, 24, 26, and 27 come up from 0.4–0.5%, and scene 16 comes up from 1.0%, when the gap side is the lower of the two. 1.6% is the low end he marked on the ruins (0.9% was way too low, about 1.6% was the mark). It has not been seen on the impala or the rays. Scene 16 and the jelly may still be short of the higher amount from before.

The abbey stays at 2.1% when its local step is at least that. Its edge is 0.49. The local-step percent on that file was not measured after the change. Pushing the abbey down with the same gap curve would push the impala back toward a vibration. Dropping the abbey was the third option. It was not taken. A rule that drops every picture spanning the frame would also drop the elephant, which he wanted kept and only lifted off 0.5%.

The Slide cut is unchanged. The show stays at 27 unless a later run judges differently. The hair fringe is unchanged.

This build has not been watched. The sentences above are what the rule does to the logged edge and gap. They are not a new stress test.

## Ed's sway maxima, 2026-11-11

The Sway rotation slider is back on the play bar (branch `prototype-3d-sway`), and the bin plays the slider's value, not a computed one. Ed set the most each Sway picture takes before it distorts, on the 27-scene show. The 22 values, with file, the computed amount and the judge's edge and gap, are in `0-Development\parallax-lab-data\sway-max-2026-11-11.tsv`. They run from 0.6% (baby elephant) to 4.0% (two baby close-ups, a third baby photo, the jelly). 4.0 is the slider's top, so those four may take more.

Ed's notes: several pictures were held down by corruption in one small area, not by the picture as a whole. The baby elephant breaks up near the ears, and the strawberry girl's hair breaks up left and right (4.0 without it). He expects this rarely applies to people. The abbey is 1.0 or left out.

| Predictor | Within 0.3% of Ed (4.0 counts at 3.7 or more) | Rank correlation with Ed |
|---|---|---|
| The computed amount in the 23:05 bin | 7 of 22 | 0.20 |
| Edge or gap alone | No order: gap 0.74 takes 4.0 (jelly), gap 0.73 takes 0.6 (elephant) | -- |
| Worst 2% patch of horizontal depth change times photo detail (`tools\parallax-lab\worst_spot.py`) | 6 of 22 | 0.18 |
| Worst 2% patch of horizontal depth change alone | 9 of 22 | 0.34 |

None of these predicts Ed's maxima. The patch measure scores the jelly (4.0) and the two `ed` photos (1.6) as the worst. Fine detail stretched against black does not read as corruption. Detail at a depth edge is not the same thing as what Ed sees break.

## The forward warp, 2026-11-11

The tears were the warp, not the pictures. Sway read each pixel's depth where the pixel lands, so where depth jumps faster than the shift the same source pixels were read twice: a second ear outline with grass through it, doubled outlines on the strawberry stickers, hair pulled into the carpet. Sway now draws with `Shaders\SwaySplat.hlsl`: for each output pixel it finds the source pixels along the row that land there and keeps the nearest, so an edge is drawn once. Where a gap opens behind a moving edge, the edge is stretched over it. Pan, and Sway when the shader cannot be made, still use the displacement warp.

Checked before Ed watched it: the compiled shader, run off screen on this PC on the elephant at 3%, matched the Python splat in `tools\parallax-lab\sway_warp_spike.py` (mean difference 3 of 255), with both ears whole on both peaks; 0.4 to 0.8 ms a frame at 1920x1080.

Ed's maxima on the same 27-scene show with this warp (the last column of `sway-max-2026-11-11.tsv`): every Sway picture takes 4.0%, the slider's top, except the two `ed` photos (scenes 6 and 7), at 1.6% because the ears look unnaturally big at more. That is the depth map's shape on a face, not a tear. The abbey, the elephant and the strawberry girl, the three that broke up before, all take 4.0%. Because 4.0 is the slider's top, the real maxima for those 20 are not known.
