# Multi-image implementation plan

**What this is.** How `MULTI_IMAGE_DESIGN.md` gets built, slice by slice. The design says *what*; this file says *how*,
and is rewritten as each slice lands. Where the two disagree, the design wins and this file is fixed. Slice 1 is
planned in full; slices 2 to 6 are outlines, planned in full only when the slice before has been shown to Ed and
adjusted.

**Status: plan, not started.** Nothing here is built. "Today" means v0.31.2. File and line references were read on
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
   as the director does, not from the control's field.
4. **The director uses it.** `BuildScene` calls `SceneCall.Make` with the previous scene's recipe and kind.
   `ChooseMotionKind` is deleted, and its stills become a user setting: **Still scenes**, odds in the camera
   settings, default 0% (Ed, 6 Nov: stills add a little variety, but not by default). `SceneCall.Make` rolls it before
   planning; a hit returns no move. Still in a scene's camera menu stays as it is. The
   saved path is planned for hold + overrun, as the canvas's is; it is re-timed at play anyway
   (`ToCameraMove(_layoutSeconds + CameraOverrunSeconds)`), so only the strength scaling by length changes, slightly.
5. **Dynamic scenes get faces.** At play, a Dynamic scene reads its pictures' facts (`ReadFactsAsync`) and builds the
   subjects from its saved cells (each cell's rect and its photo's faces, as `CameraSubjects` does), then calls
   `SceneCall.Make` with that layout to keep. First measure how long the facts read takes on a scene of 9 large
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

## Slices 2 to 6: outline

| Slice | Main code | First thing to settle |
|---|---|---|
| 2. One image through the designs | `LayoutComposer.ComposeSolo` and the Single images odds go; each design's builder takes one image; Crop at most replaces the 65% rule in `ComposeSolo` (the Cinematic Slideshow window's own copy is not touched, design R14); shaped cells measured by shape; settings migration for the Single images odds (design section 7) | How each builder lays out one image (design 3.2) on a portrait and a landscape, before any settings |
| 3. Play through the director | Play drafts one scene ahead through `ShowDirector.DraftSceneAsync` and keeps scenes; show values into `ShowGlobalDefaults`; schema 1 to 2 (`ShowProject.FromJson`); cinematic values off Generator Advanced onto the storyboard; the five presets in app settings | The Play timing targets (first picture within 1 s, no scene held late over 30 minutes) on Ed's largest folder, before anything else in the slice |
| 4. One move on one image | The zoom order (design 3.5): the cell opens on the fit or fill, Zoom is where the move arrives; `AimAtSubject` no longer zooms a one-image cell before the camera does | Zoom frequency at 100% and 0% doing what it says (design check 15) |
| 5. Design settings | Frame color and width for every frame; Backdrop odds for every design with a backdrop; ambient blur backdrop | Cost of a blurred copy per image during Play |
| 6. Sequence beats | The director rolls Sequence beats and Scenes per step, writes closer views, keeps the layout for views of one image, Cut transition, no overrun before a change of view, two-shot off inside the step; `SceneRequest` gains the start frame | What "closer view" picks on a group with no featured image |
| Later | Design 3.10, cut on the music | Approved on its own when its turn comes |
