# 3D ruleset

What a picture does in a 3D show, and what the two stress tests on 2026-11-10 decided. This matches the code in `ModernImageViewer\bin` published at 23:05 that day (`ModernImageViewer.dll` 3,641,344 bytes). It is a prototype. It is not a release, the version was not bumped, and the copy under `OneDrive\Apps\ModernImageViewer` was not updated.

The code is `ParallaxJudge.cs`, `LayoutComposer.cs` (`SurpriseOptions`), `MultiViewControl.Depth.cs`, and `extensions\Parallax\DepthMaps.cs`.

## Motions

A picture plays one motion, or it is left out of the show. It is not shown as a flat photo inside that show.

| Motion | What moves | What stays still | Travel |
|---|---|---|---|
| Slide | The background, once, in one direction. The subject is a cutout. | The far plane (`DepthPivot` returns 0). | 3.2% of the width (`DepthTravel` 0.032). |
| Sway | The whole picture. Near and far move opposite ways, then it comes back. The judge calls this Rock. | The median depth. | The sway amount below. Cap 4%. Played floor 1.6%. |
| Pan | The same path as Sway, used when the camera move is already larger than 3% of the width. | The median depth. | 3.2% of the width. The auto sway number is not used. |

Slide does not use the sway percent or the swing time. Sway and Slide are not combined on the same picture. One swing is center, one side, the other side, center. The swing control sets that time for every Sway picture, from 1.5 s to 12 s. A settings file without the key keeps 4.25 s. The old rotation slider is gone. A settings file may still contain `SwayRotation`. The draw ignores it.

The gear "3D debug" readout, on by default, shows `Sway x.x%`, `Slide`, or `Pan`, and `Filter t s`. The percent is the rotation the frame uses. Filter times around 0.7–1.3 s on this machine are the healthy range.

## The depth map

Depth Anything's output is stretched to 0–1 per picture before any of the numbers below. A higher value is closer. The stretch throws away how far the scene was in the world. A chair just behind a baby and a sky behind a vault can both fill the whole range.

Two maps are kept.

| Map | What it is | What uses it |
|---|---|---|
| Raw | The stretched values, as bytes. | Edge, gap, and the Slide cut. |
| Grey | Raw, then a 3×3 max, then a 3×3 blur. | The local sway step, and the median pivot. |

**Edge** is the median depth on the subject's rim minus the median on the ring of background just outside it. **Gap** is the median of all foreground minus the median of all background. Both are fractions of Raw. Foreground is the subject mask, alpha above 127. Texture is a Sobel on the photo's brightness in that ring, scaled by 1/1.08 because the measure was calibrated on 768-wide photos and the depth map is about 518 wide.

`RawSpan`, the unstretched range of the model output, is stored and not used for sway or for who plays.

## Who plays

Ed, 2026-11-07: a clear subject with a distant background plays Slide. A clear subject that can be rotated, without that background, plays Sway. Anything else is left out.

The subject mask has to be clear. A subject that covers more than 88% of the picture, or touches 70% of the sides, is the picture and is left out. No background around the subject leaves it out.

| Who | Admitted when | Otherwise |
|---|---|---|
| A face | Edge at least 0.15 and gap at least 0.12. | Left out. "People against a flat background." |
| No face | Edge at least 0.30 and gap at least 0.25. | Left out. "Small depth step at its edge." |

A picture that passes that gate is then tried as Slide. Slide also requires all of these. Failing one does not by itself leave the picture out. A face goes to Sway. A picture with no face goes to Sway only when the close-subject test below passes, and is left out otherwise.

| Slide check | Threshold | Fail |
|---|---|---|
| Edge | At least 0.30 | Sway if a face or a close subject, else out |
| Outline | Vague at most 0.085 | Same |
| Background texture | At least 0.03 | Same. A plain background is this fail. |
| Subject area | At most 65% | Same |
| Sides touched | Under 30% | Same |
| Top and bottom | Not both over 5% | A face Sways. Anything else is out. |
| Far background | 90th percentile of the remaining background at most 0.135 | Same as the first row |
| Ground | The connected foreground may not cover more than 65% when the subject itself is under 45% | Same |

Close subject, the test that lets a non-face Sway after a Slide check fails: edge at least 0.40, gap at least 0.40, and one of these: area at least 48%, texture under 0.03, or a sharp close subject (area at least 15%, vague at most 0.085, texture under 0.16).

On the 73 clips marked 2026-11-07, none of those measures separated Slide's passes from its fails. The best, the outline, got 46 of 59. The fails were mostly buildings and trees.

### The Slide cut added 2026-11-10

A picture that passes every row above still has to be one subject on a clean background. This is `SlideIsOneCleanCut`. Failing it leaves the picture out. It is not shown as Slide, and it is not turned into Sway.

| Check | Threshold |
|---|---|
| One subject | A second foreground piece of 2% of the picture or more is another subject. A smaller speck does not count. Pieces are 4-connected. |
| Solid | That one piece fills at least 40% of its own box. |
| Clean background | On the background, the 90th percentile of Raw minus the 10th is at most 0.28. |

One solid shape against a clean sky can still pass. The models do not know a building from an animal. A wrong outline on that one shape can still play.

If the Slide layers are built and the subject is not at least 0.08 in front of the rest, the picture Sways instead.

Reading 4% does not leave a picture out. That exclusion was published to bin at 20:19 on 2026-11-10 and taken out the same evening. 4% is the most the sway will go. It is not a reason to drop a picture.

## Sway amount

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
