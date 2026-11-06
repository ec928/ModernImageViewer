# Multi-image implementation plan

**What this is.** How `MULTI_IMAGE_DESIGN.md` gets built, slice by slice. The design says *what*; this file says *how*,
and is rewritten as each slice lands. Where the two disagree, the design wins and this file is fixed. Slice 1 is
planned in full; slices 2 to 6 are outlines, planned in full only when the slice before has been shown to Ed and
adjusted.

**Status: slices 1, 2, 3a and 4 built on branch `slice1-one-engine-call`; Ed has run 1, 2 and 3a.** 3a's timing: first
picture 0.83 s and 1.12 s on two runs, no scene held late in 2.6 minutes; the 30-minute run is not done. Slice 4 adds the
face and detail ceiling to one image's camera move (Ed saw a snap cut a close-up's eyes) and aims a zoom without faces at
the focus point. 3b, 3c, 5 and 6 not started. "Today" means v0.31.2. File and line references were read on
6 Nov 2026 and will drift.

**How each slice runs**

1. Build on a branch, no version bump while iterating. Prototype builds go to `bin\` only, never to
   `OneDrive\Apps\ModernImageViewer`, until Ed says the slice is right.
2. Attack before assert: try the slice the way Ed uses it (a large real folder, a saved storyboard, settings changed
   mid-session) before writing any test.
3. Each new test is control-verified: put the old behaviour back, watch it fail, restore.
4. No full suite or gate runs unless Ed asks. Run the tests that cover the files changed.
5. Ed sees it running. Report what was built, tested, watched, and what was not.

---

## Slice 1: one engine call

**Goal (design, Core principle).** Every layout and every move in the multi-image area comes from one entry point.
Memory is passed in, never kept by the engine. Values are resolved once before the call.

**What Ed sees.** The same settings give the same kind of moves on the canvas, in Auto-Mix and in a storyboard. A
storyboard scene set to Dynamic aims at faces. Today's two-shot is unchanged.

### What the code does today

| Caller | Where | Layout | Move |
|---|---|---|---|
| Canvas and direct Auto-Mix | `MultiViewControl.Surprise.cs` ~4623 and ~4726 (`LayoutComposer.Compose`); `MultiViewControl.Camera.cs` `StartCanvasCamera` ~42 | Compose, `avoid` = last recipe | `CameraMove.Plan` with the layout's faces (`Composition.CameraSubjects()`), Camera mode, `_lastCameraKind`, hold + `CameraOverrunSeconds` (2.5 s) |
| Storyboard scene set to **Dynamic** | `MultiViewControl.Surprise.cs` `PlayNextStoryboardSceneAsync` ~4002 | The scene's saved layout file | `CameraMove.Plan` with **no faces** (`Array.Empty`): the saved layout file holds no faces. "Dynamic" is the scene camera menu's choice that clears the saved path (`FillCameraMenu` ~1973) |
| Show drafting | `ShowDirector.cs` `DraftShow` ~89/102, `DraftSceneAsync` ~146, `BuildScene` ~160 | Compose, `avoid` = previous recipe; falls back to one image | `ChooseMotionKind` picks the kind by its own odds by image count (including 5% to 35% "Still"), overriding Camera mode, then `CameraMove.Plan` with that kind forced, `last` = null, hold with no overrun. The move is saved as the scene's path |

Settings: the canvas reads `SurpriseChoices`; a show reads `GeneratorSettingsFor(show)` =
`SurpriseChoices.WithOverrides(show.GeneratorOverrides)` (`MultiViewControl.Surprise.cs` ~103). Memory:
`_lastCameraKind` is a field on the control, reset in `ForgetSurprise`.

### Steps

1. **The entry point.** A new `SceneCall` (new file `SceneCall.cs`), one static method:
   `SceneCall.Make(SceneRequest) -> SceneResult`.
   - `SceneRequest`: the photos (`PhotoFacts`), canvas size, the resolved `SurpriseOptions`, the hold in seconds,
     what not to repeat (last recipe, last `CameraKind`), an optional layout to keep (a `Composition`), a seed.
     The start-frame input from the design waits for slice 6, which is the first to need it.
   - It composes with `LayoutComposer.Compose` (or keeps the given layout), then plans the move with
     `CameraMove.Plan`: subjects from `CameraSubjects()`, amount from `PickMotion`, `MotionZoomChance`,
     `MotionFaceChance`, `ForcedCameraKind`, `CameraStyle()`, the last kind, and hold + `CameraOverrunSeconds`.
   - `SceneResult`: the `Composition` and the `CameraMove` (null when Motion is 0).
   - `CameraOverrunSeconds` moves from `MultiViewControl.Camera.cs` to `SceneCall`, since the call now owns it.
2. **The canvas and Auto-Mix use it.** The prepared layout (~4623, ~4726) comes from `SceneCall.Make`. When it is
   shown, `StartCanvasCamera` calls `SceneCall.Make` again with that layout to keep and the hold now known, and plays
   the move. Two calls, one entry point: the layout is prepared ahead, and the hold is only fixed when it is shown.
3. **Memory moves out of the engine path.** `_lastCameraKind` stays the canvas's own memory, but is passed into each
   call rather than read inside a planner. A storyboard scene takes its "last" from the previous scene's saved kind,
   as the director does, not from the control's field. *Built differently:* during storyboard play the control
   keeps the kind of the move that last played (saved path or Dynamic) and passes it in, because a Dynamic scene's
   previous scene may itself be Dynamic, with no saved kind.
4. **The director uses it.** `BuildScene` calls `SceneCall.Make` with the previous scene's recipe and kind.
   `ChooseMotionKind` is deleted, and its stills become a user setting: **Still scenes**, odds in the camera
   settings, default 0% (Ed, 6 Nov: stills add a little variety, but not by default). `SceneCall.Make` rolls it before
   planning; a hit returns no move. Still in a scene's camera menu stays as it is. The
   saved path is planned for hold + overrun, as the canvas's is; it is re-timed at play anyway
   (`ToCameraMove(_layoutSeconds + CameraOverrunSeconds)`), so only the strength scaling by length changes, slightly.
5. **Dynamic scenes get faces.** At play, a Dynamic scene reads any facts not yet cached (`ReadFactsAsync`), takes
   the faces as shown from Camera Director's `FacesOnCanvas` (the saved layout file holds no faces), and passes the
   three largest as the layout to keep (`KeptLayout(null, subjects)`). *Differs from `CameraSubjects`:* that takes one
   point per cell, weighted across its faces; this takes one point per face. First measure how long the facts read takes on a scene of 9 large
   images; if it delays the scene visibly, read the facts while the previous scene plays.
6. **One rule for values.** All three callers get their options from one place: `GeneratorSettingsFor(show)` for a
   show, `SurpriseChoices` for the canvas. Today that is already true; this step only removes any read of the
   settings inside a planner. The scene level (a scene's own values) is its saved path today and needs nothing new.

### Tests

Attack first, then thin tests:

- **Same spread from every caller (design check 19).** One image with a face, the same options, 30 moves from each of:
  the canvas path, `ShowDirector.DraftSceneAsync`, and a Dynamic scene. Compare the share of zoom in / zoom out /
  drift and the share aimed at the face. Control: put `ChooseMotionKind` back and the director's spread differs.
- **Dynamic aims at faces.** A Dynamic scene of one portrait with a face, 30 plays: the aim lands on the face at about
  `MotionFaceChance`. Control: pass `Array.Empty` again and it never does.
- **Nothing else plans (design check 20).** A source search: no caller of `CameraMove.Plan` or `LayoutComposer.Compose`
  outside `SceneCall` (tests excepted); `ChooseMotionKind` gone.
- **Still scenes.** At 0%, 30 calls give no still; at 100%, every call is still. Control: ignore the setting and 100% fails.
- **Two-shot unchanged.** Camera mode Auto, `TwoShotChance` 25%: about one in four eligible moves has two shots, from
  every caller.
- Existing tests that call `CameraMove.Plan` and `LayoutComposer.Compose` directly (`CameraStyleTests`,
  `LayoutComposerTests`, `ZoomVarietyTests`, `SettingsAreObeyedTests`) stay: they test the engine's parts, which do
  not change. `ShowDirectorTests` covers partitioning only and is unaffected.

**Watched in the app by Ed:** Auto-Mix on a folder of portraits; a drafted storyboard played; one scene switched to
Dynamic and played.

### Risks and open points

- The canvas's second call (step 2) plans the move with the layout kept. Checked: today no move exists before
  `StartCanvasCamera`, whose only caller is the show step (`MultiViewControl.Surprise.cs` ~4665), so nothing relies on one.
- Faces for Dynamic scenes cost a facts read per scene (step 5). Unmeasured.
- Drafted shows have no stills by default (step 4); Ed agreed on 6 Nov, with Still scenes as a setting.

---

## Slice 2: one image through the designs

**Goal (design 3.1 to 3.4, for one image).** A layout of one image rolls the design odds like any layout. The Single
images branch and its odds go. **Crop at most** (default 35%) replaces the 65% rule. A shaped cell is measured by its
shape.

**What Ed sees.** A portrait and a landscape regenerated: full screen, in card, hero, print, circle, panel, by the
design odds; a portrait shown whole when filling would crop more than Crop at most.

### What the code does today

- `LayoutComposer.Compose` sends one image to `ComposeSolo` (~777) before any design is rolled.
- `ComposeSolo` fills the screen and measures the share shown; under 65% or with a face cut, Fill becomes **Fitted**:
  a transparent floating cell sized to the image, on a toned vignette. Then `SurpriseOptions.PickSolo` rolls Fill /
  Matted / Whole (odds 2 / 1 / 1). Matted is `BuildGalleryWall` with one photo; Whole is a framed print on a vignette,
  labelled Hero spotlight.
- `CanMake` refuses every design under two images. Of the builders: `BuildScatteredPrints` and `BuildCircles` already
  work for one; `BuildHeroSpotlight` needs at least one image beside the hero; `BuildSlantedPanels` with one image makes
  a panel 1.67 screens wide.
- **The renderer fills every cell.** A layout file has `State.IsFillMode`, but the control does not read it from a
  file, and it is one switch for the whole canvas. So "shown whole" can only be drawn today the way Fitted does it:
  a cell sized to the image's own shape.
- A shape's outline is known (`WindowShapes.Outline`, `CoverBox` in `MultiViewLayoutFile.cs`), so the share of an
  image a shape hides can be measured exactly.

### Decided (Ed, 6 Nov): a shape that would crop too far steps aside (option A)

The design (3.4) says a cell that would crop past Crop at most shows its image "fitted inside the bounding box, clipped
by the shape, on the backdrop". For a rectangle that is the Fitted cell above. For a circle, hero shape or leaning
panel it is a new kind of drawing: an image smaller than its shape, with backdrop showing inside the shape around it.
The renderer cannot draw that today.

| Option | What a portrait in a circle does | Cost |
|---|---|---|
| **A. The design steps aside for that image (recommended)** | That call rolls among the designs that can show it within Crop at most, as a design that cannot hold the image count does today. Edge to edge (fitted when needed), Gallery wall (whole in its card) and Scattered prints (a print is the image's own shape) can show any image, so a layout is always made | None in the renderer. Every shape keeps its look. Crop at most means exactly what it says |
| B. The shape takes the image's proportions | A tall oval instead of a circle; a stretched hexagon | Shapes stop looking like themselves |
| C. Draw an image inside its shape, smaller than it | A circle with the whole portrait inside and backdrop at its sides | New renderer work in the cell drawing, which this slice was not sized for, and which can only be checked by eye |

A respects R2 (a design is not turned off; it takes no part in a call it cannot honour, as with the image count) and
R5 (Crop at most is never overridden). The design's 3.4 sentence changes to match whichever is chosen.

### Steps (written for A; B or C change step 3 only)

1. **One image goes through the designs.** `Compose` stops sending one image to `ComposeSolo`. `CanMake` accepts one
   image for every design. Each builder gets a one-image form: Edge to edge, one full-screen cell; Gallery wall, one
   cell in its card; Hero spotlight, the hero alone on its backdrop (today's Whole print becomes this, with the hero
   shapes); Scattered prints and Circles, as today with one; Slanted panels, one panel at full height and the
   image's width.
2. **Crop at most.** A new setting, `CropAtMost`, default 0.35, under Images & faces. One measure,
   `CropShare(cell)`: the share of the image the cell hides when filled, measured against the shape's outline, not
   its box.
3. **Shown whole, or stepping aside (A).** Edge to edge: past Crop at most or through a face, the cell becomes today's
   Fitted cell. Gallery wall: the card fills the screen and the image sits whole in it. Scattered prints: never crops.
   Hero, Circles, Slanted panels: past Crop at most, the design leaves that call's roll.
4. **Single images goes.** `SoloFillOdds`, `SoloMattedOdds`, `SoloWholeOdds`, `PickSolo`, `ComposeSolo` and the
   Single images section of Generator Advanced are removed; the Cinematic overrides' copies too (design section 7).
   Old settings files still load: the fields are ignored. The 65% in `ComposeSolo` goes with it; the Cinematic
   Slideshow window's own copy stays (R14).
5. **Cinematic.** Its preset's design odds (Edge to edge Often, Hero spotlight Rare, others Off) replace its Single
   images odds.

### Tests

- Attack first: a 2:3 portrait, a 3:2 landscape, a 1:3 panorama, a tiny image and a face at the edge, each
  regenerated 30 times with default odds: which designs come up, and none crops past 35% or cuts a face.
- Crop at most at 100%: nothing shown whole for crop; at 0%: Edge to edge always fitted, Hero, Circles and Panels never
  chosen for a portrait. Control: ignore the setting and both ends fail.
- A design at Off never appears for one image (design check 4 for count 1).
- `SettingsAreObeyedTests` covers the old Single images odds and will be rewritten for the design odds; which of its
  tests go is listed in the commit.

### As built

- At the defaults, across 120 layouts each: a portrait gets Prints, Gallery wall, Edge to edge and Hero (rarer);
  a 3:2 landscape adds Slanted panels; Circles come up only for an image within about 6:5 of square; a panorama gets
  Prints, Gallery wall and Edge to edge. No layout crops past 35% or cuts a face, and none fails to be made.
- One image's layout zoom is rolled once, after its design is chosen (`ZoomedIfOne`), as `ComposeSolo` did. Rolled per
  version, the ranking always picked an unzoomed version, since it scores how much of the image shows.
- Zoom still crops after Crop at most is checked; slice 4 orders the two (design 3.5).

### Risks

- Gallery wall's whole-in-card for one image is a new layout (a floating cell on a card-coloured canvas). It renders
  the way Fitted does, but nobody has looked at it.
- Today's Whole image always had a frame colour from Gallery wall's settings; as a hero it takes the hero's white
  outline until slice 5 brings Frame color to every design.

## Slice 3: Play through the director

**Goal (design, The director; R15).** Play goes through the director and keeps what it plays. Show values live in
the show file. The cinematic values and the five presets live on the storyboard.

### What the code does today

- **Play storyboard already is the director playing on demand.** `MakeAndPlayStoryboardAsync`
  (`MultiViewControl.Surprise.cs` ~3790) drafts one scene at a time with `ShowDirector.DraftSceneAsync`, starts
  playing after 2 scenes (`ScenesBeforePlay`), stays at most 3 ahead while playing (`ScenesAheadWhilePlaying`), keeps
  every scene, and stops drafting once every image is in a scene. Play then loops the kept scenes
  (`_showPlaybackIndex = (currentIndex + 1) % count`).
- **Play in Auto-Mix is not.** With no storyboard, or after the user chose Auto-Mix, Play runs `SurpriseAsync` on a
  timer: each step deals a hand from a `PictureDeck` (every image before any repeats; Same folder and date;
  Skip screens and repeats; featured first), prepares the next layout while this one holds, keeps nothing, and never
  ends.
- So the work is not building a director. It is moving Auto-Mix's Play onto the existing one, and moving Auto-Mix's
  way of choosing pictures (the deck) into the director's "next images" step.

### Decided (Ed, 6 Nov): when every image has been in a scene, Play from images keeps drafting; a saved or opened show loops

| Option | What Play does after the last image | Fits |
|---|---|---|
| **A. Keep drafting (recommended for Play from images)** | Deals the next hand from the deck as Auto-Mix does, so new scenes keep coming, all kept. An hour at 12 s is about 300 scenes | R6 (variety), today's Auto-Mix; nothing a user sees today is lost |
| B. Loop the kept scenes | Replays scene 1 onward, as Play storyboard does today | A saved show, which plays what was saved |

The recommendation is both, by what is playing: Play started from images keeps drafting (A); a show the user opened
or saved replays its scenes (B), as the design's "saved shows replay what was saved" says.

### Steps (slice 3 is split so the visible part is seen first)

**3a. Play through the director (what Ed sees).**
1. Auto-Mix's Play calls the director path (`MakeAndPlayStoryboardAsync`) instead of the `SurpriseAsync` timer.
2. The director's next images come from the deck (`DealHand`), not "the next images not yet in the show", so Same
   folder and date, Skip screens and repeats, featured first and taking turns keep working.
3. Play starts after 1 scene, not 2, for the 1 s target; the next is drafted while the first holds.
4. When the deck has gone round, Play keeps drafting (A) or loops (B), by the answer above.
5. Measure on Ed's largest folder: time to first picture, and scenes held past their duration over 30 minutes. A miss
   is fixed before 3b.

*3a as built:* with a storyboard already open and Auto-Mix chosen, Play still mixes without keeping, so that
storyboard is not replaced. A hand that cannot be laid out is skipped; five in a row end the drafting. Not yet
measured: the timing targets (they need the app running on Ed's largest folder).

**3b. Show values in the file (design section 7).** `ShowGlobalDefaults` gains images per scene, design odds,
duration, smart pace, motion, sequence beats and Scenes per step; `$schemaVersion` 2; a version 1 "Cinematic" show
copies the app's cinematic values once on open; `GeneratorSettingsFor` reads the show's own values first.

**3c. Presets and the storyboard's settings.** The cinematic values leave Generator Advanced and are edited on the
storyboard; Multi-Image, Cinematic and My presets 1, 2, 3 in the app's settings.

### Tests

- 3a: a headless run of the director's next-images step over a folder list: every image used before any repeats,
  featured first, screens skipped; control by dealing in folder order instead.
- 3a: Play from images past the last image keeps adding scenes (A); an opened show loops (B).
- 3b: a version 1 Cinematic show opens with the app's cinematic values copied in, is unchanged on disk until saved,
  and an older reader skips the new fields.

## Slices 2 to 6: outline

| Slice | Main code | First thing to settle |
|---|---|---|
| 2. One image through the designs | `LayoutComposer.ComposeSolo` and the Single images odds go; each design's builder takes one image; Crop at most replaces the 65% rule in `ComposeSolo` (the Cinematic Slideshow window's own copy is not touched, design R14); shaped cells measured by shape; settings migration for the Single images odds (design section 7) | How each builder lays out one image (design 3.2) on a portrait and a landscape, before any settings |
| 3. Play through the director | Play drafts one scene ahead through `ShowDirector.DraftSceneAsync` and keeps scenes; show values into `ShowGlobalDefaults`; schema 1 to 2 (`ShowProject.FromJson`); cinematic values off Generator Advanced onto the storyboard; the five presets in app settings | The Play timing targets (first picture within 1 s, no scene held late over 30 minutes) on Ed's largest folder, before anything else in the slice |
| 4. One move on one image | The zoom order (design 3.5): the cell opens on the fit or fill, Zoom is where the move arrives; `AimAtSubject` no longer zooms a one-image cell before the camera does | Zoom frequency at 100% and 0% doing what it says (design check 15) |
| 5. Design settings | Frame color and width for every frame; Backdrop odds for every design with a backdrop; ambient blur backdrop | Cost of a blurred copy per image during Play |
| 6. Sequence beats | The director rolls Sequence beats and Scenes per step, writes closer views, keeps the layout for views of one image, Cut transition, no overrun before a change of view, two-shot off inside the step; `SceneRequest` gains the start frame | What "closer view" picks on a group with no featured image |
| Later | Design 3.10, cut on the music | Approved on its own when its turn comes |
