# VideoDirector — Multi-Track Plan

**Status:** working prototype — Track 1 + Track 2 (with up to two simultaneous PiPs) + bottom
dock all functional. Next: Canvas view (real C3+C4) → C-full → N-tracks. See §6 (progress) and
§7 (roadmap). This doc is the durable source of truth for the multi-track work; readable
standalone by any session or tool — no prior chat context required.

**Working principle (non-negotiable):** every step ends in a **green build and a commit**.
Never leave the tree in a non-building state. Small steps, committed often, so we are never
more than one `git revert` from safety. This is the antidote to the failure mode that
destroyed an earlier version of this code.

---

## 1. Objective

A multi-track video sequencer/compositor with **3 tracks**:

- **Track 1** — the base narrative spine. Purely sequential, no gaps, always playing,
  full-frame. Defines the sequence's total length. Its clips' start times are computed
  (display-only).
- **Track 2 / Track 3** — upper layers composited *over* Track 1, playing simultaneously.
  Sequential **with gaps allowed**, so start time is **editable**. Default to a small
  picture-in-picture (PiP) in opposing corners so both the wide shot (Track 1) and the
  close-up (upper track) are visible at once.

Use case that drives it: see a person's **close-up expression** (upper track) while the
**wide shot** of them moving (Track 1) plays behind.

---

## 2. The unified model

**A clip is a clip.** There is exactly one clip type (`CinematicOperation`) on every track,
with identical Zoom & Motion controls. There is no separate "overlay" type — that was a
design mistake being unwound here.

- **Clip** = `{ source, in/out trim, timelineStart, motion (Start/Mid/End marks + curve),
  opacity, transition, placement }`.
- **Track** = an ordered layer of clips + a z-order + a placement rule
  (sequential-no-gaps vs sequential-with-gaps).
- **Sequence** = the master story clock + the tracks.

**The tracks differ in only two ways — neither is a property of the clip:**

1. **Compositing order (z-order).** Higher track renders over lower. Track 3 > Track 2 > Track 1.
2. **Placement rule.** Track 1 auto-abuts (sequential, no gaps). Upper tracks are freely
   placed (editable start, gaps allowed). Under the hood *every* clip has an explicit start
   time; Track 1 just auto-assigns them by stacking end-to-end.

**Within a track, clips never overlap** (a track is a single video stream). Editing a start
time clamps against neighbours; gaps allowed, overlaps prevented.

---

## 3. Two transforms per clip — the crux

Every clip carries **two independent transforms**, applied at different moments:

1. **Content framing = Zoom & Motion.** What the clip *shows* — zoom into the face, pan,
   Ken Burns push via Start/Mid/End marks + curve. Operates on the clip's own frame.
   **Identical across all tracks.** Edited **full-screen**, clip alone.
2. **Placement = the PiP box.** *Where and how big* the framed result appears in the
   composite — corner + size. A **compositing** property. Track 1's placement is
   "full frame" (identity); upper tracks default to ~30% in a corner. Applied as an outer
   transform + **clip** (so zoomed content can't spill outside the box).

Render pipeline for any clip:
`source → [content transform from marks] → [placement transform + clip] → composite`.

This separation is what lets editing be identical everywhere: when editing a clip, placement
is temporarily identity (full-screen) so you frame content with the normal controls; at
playback the framed result is placed into its corner.

> Note: an earlier "box" attempt was rejected because it made the user frame *inside* a tiny
> box. The fix is: **frame full-screen, placement is automatic.** The nested transform still
> exists under the hood; the user never touches it while framing.

---

## 4. Two views + the toggleable dock

- **Edit view** — one clip, full-screen, alone. Identical Zoom & Motion controls for every
  track (scroll=zoom, drag=pan, Set Start/Mid/End, curve, record). Placement is bypassed here.
- **Canvas view** — the whole composite: Track 1 full-frame base + upper-track PiPs in their
  corners, z-ordered. This is where **layout** happens: select a PiP and move/resize it
  against the live composite (WYSIWYG placement editing).

Switching: select a clip (from a track's storyboard/dock) → Edit view for that clip;
a Canvas/Composite button (or Esc) → back to the composite; in Canvas view, click a PiP to
select it for move/resize, double-click (or "Edit") to jump into its content.

**Arrangement dock (C-lite):** a **bottom** dock — three horizontal lanes, clips
left-to-right (time flows rightward), **one shared horizontal scroll** across all lanes so a
column position means roughly the same moment across tracks. It is a **sequencer, not a
timeline**: not time-scaled, so precise start times are set **numerically in the inspector**
until C-full. Toggleable on/off. Track 1's lane gets a subtle visual distinction (the spine).

**Panel split:** arrange on the **bottom** (dock), adjust on the **right** (inspector). The
right panel sheds the clip lists and becomes purely the selected clip's properties
(Zoom & Motion, trim, speed, transition, and for upper clips placement/opacity).

---

## 5. Confirmed decisions (do not re-litigate)

- One clip type (`CinematicOperation`) on all tracks; retire `OverlayClip`.
- Tracks differ only by z-order + placement rule.
- Track 1: base, sequential, no gaps, always playing, **defines total length**, display-only
  start time, full-frame.
- Track 2/3: sequential **with gaps**, editable start time, **no overlap within a track**.
- Default placement for upper clips: **~30% PiP in opposing corners**; z-order **3 > 2 > 1**.
- Content (marks) vs placement (box) are **separate transforms**.
- Edit view = full-screen identical controls; Canvas view = composite + PiP move/resize.
- **Upper-track audio muted by default** (Track 1 is the audio bed); per-clip unmute later.
- **Transitions functional on all tracks** (upper clips inherit transition fields; leave working).
- Aspect-correct overlay sizing already done (grid sized to the video's native aspect — no bars).
- Dock is **C-lite** (ordered, shared-scroll, non-time-scaled); C-full is deferred.

---

## 6. Progress (as built)

Working prototype. Build green. Commits, oldest → newest:

- `cf26e47` — additive `CinematicOperation` fields (`StartTime`, `Opacity`, `IsActiveAt`, …).
- `83340a4` — overlay editing/mgmt UI + playback correctness fixes + aspect-correct sizing.
- `e2440e7` — **Phase B**: retire `OverlayClip`; Track 2 is now `CinematicOperation`. Upper-track
  audio muted at the player level. (Deviation: kept a **single** `OverlayClips` collection —
  did **not** store upper tracks as a list. See §7 N-tracks.)
- `e2835d6` — **Phase C1**: content/placement split; overlay grid = clipped placement **box**,
  content transform on the inner player; editing an overlay is **full-screen**.
- `84472ae` — fix: edit-mode race after pausing playback.
- `0c5d092` — **Phase C2**: content **motion** — overlays interpolate Start/Mid/End marks over
  their duration via shared `ApplyMarksAtProgress` (identical to Track 1). Inspector: Set Start/End.
- `02463b3` — **Phase C4 (interim only)**: placement via inspector — Box Position (corner presets)
  + Box Size. **Not** the WYSIWYG drag/resize C4 was defined as.
- `88b4d18` / `64aa776` / `2a0cb6f` — **Phase E**: bottom track dock (Track 1 + Track 2 lanes),
  auto-hide during playback (`IsDockVisible`), right panel is now **inspector-only**; dock owns
  arrangement (select / reorder / context-menu / add).

**What actually works now:** Track 1 sequential base + Track 2 overlays with full-screen content
framing, Ken-Burns motion, corner+size placement, opacity, muted audio, add/duplicate/remove,
and a bottom dock. **Track 2 already shows up to TWO simultaneous PiPs** (the engine has two
overlay slots fed from `OverlayClips`), each independently framed/placed/animated — so the
multi-layer capability is already real, if loosely modelled (see §7 N-tracks).

**Placement representation:** `PlacementScale` + `PlacementCenterX/Y` (normalized 0..1) on the
clip. Corner presets in the inspector are a UI convenience that write these.

---

## 7. Phases — status & revised roadmap

Done: **B, C1, C2, C4-interim, E** (see §6). Each ended green + committed.

**Revised roadmap (agreed):** finish the **Canvas view (C3 + C4)** → **C-full** → **N-tracks**.
**Phase D (hardcoded Track 3) is dropped** — it proves nothing new and Track 2 already does two
simultaneous PiPs; a third layer comes free with generic N-tracks.

### NEXT — Canvas view (the real C3 + C4)  *(the "polish" that finishes Phase C)*
Both outstanding pieces hang off one missing thing: a **Canvas/composite view** — the paused
composite (Track 1 frame + every PiP at its placement) that you can manipulate directly.
- **Edit ⇄ Canvas toggle (rest of C3).** A way to flip between single-clip full-screen *edit*
  view and the *composite* view. Today: selecting an overlay = full-screen edit; playing =
  composite; there is no paused composite view.
- **WYSIWYG placement (real C4).** In Canvas view, click a PiP to select, **drag to move**,
  **handles to resize** → writes `PlacementCenterX/Y` + `PlacementScale`. Replaces the interim
  numeric/preset controls' blind set-then-play loop.
- **Unified-controls gap (rest of C3).** Overlay inspector still lacks **Set Mid, Curve
  Profile, and Record** (Track 1 has them; the engine already supports Mid/Curve via
  `ApplyMarksAtProgress`). Add them for true parity.
- Needs: render the composite while paused (currently the overlay eval only runs during the
  animation loop); pointer hit-testing on PiPs; resize-handle UI.

### THEN — C-full (time-scaled timeline)
- px = seconds, ruler, drag clips along time, snapping, a playhead moving through the dock.
- **Shared horizontal scroll across lanes** (never built in E) lands here — it's the time-axis
  on-ramp and becomes mandatory once lanes are time-scaled.
- Editable Track 2/3 start-times become spatial (drag) instead of numeric.

### THEN — N-tracks (generic; forget Track 3)
- Migrate the data model from the single `OverlayClips` + "2 slots" to a **list of upper
  tracks**, each a `CinematicOperation` collection with its own z-order + default corner.
- Decide track semantics: move from today's **loose** model (2 simultaneous clips from one
  collection) to **strict** (each track sequential/no-overlap; N simultaneous = N tracks).
  Strict is cleaner and pairs with the timeline; the loose model already gives the *capability*.
- Player allocation: per-track player(s); add an A/B pair per track only when enabling
  upper-track transitions.
- Dock renders N lanes from the track list; add/remove-track UI.
- Best built **with** C-full (stacked tracks + time axis is the natural home).

---

## 8. Risk areas + mitigations

- **Canvas resize coupling.** Canvas `ActualWidth/Height` feeds the WYSIWYG rect math and
  overlay aspect-sizing. Any layout change (dock toggle, bottom dock) resizes the canvas →
  recompute those visuals on `SizeChanged`. Failure is **visible** (boxes misalign), not
  silent. Handle it explicitly.
- **Nested render structure.** Content-inside-placement-with-clip must be built carefully
  (transform origins, clip geometry in the box's coord space). Verify a zoomed upper clip
  crops to its box.
- **Timing regressions.** Story time is driven continuously off the active player position +
  `_storyTimeAtClipStart`, accumulated **into the baseline** at boundaries (not into
  `CurrentStoryTime`, which double-counts). Do not reintroduce that bug when touching the
  render loop.
- **Transport-pill relocation.** The bottom edge is contested (dock vs transport vs trim).
  Keep transport floating; dock is a separate strip; revisit during prototyping.
- **Player lifecycle.** Release `MediaPlayer.Source` when a clip leaves its window; don't leak
  decoders. SMTC stays disabled on all players.

---

## 9. Deferred / future (explicitly NOT now)

- **Advanced per-clip transitions** and **canvas-level transitions/effects**.
- **Audio** — proper mixing/ducking/per-clip levels + per-clip unmute. Until then upper tracks
  are muted at the player level (no per-clip audio field yet).
- **Polish / cleanup backlog** (do opportunistically, none blocking):
  - Empty-state hint when a track lane / the whole timeline is empty (the old "drag files
    here" prompt was removed with the right-panel lists; drop-on-canvas still adds to Track 1).
  - Remove now-unused `OperationTemplate` / `OverlayTemplate` and the dead `ListView_GotFocus`.
  - Verify horizontal drag-reorder feels right in the dock lanes.
  - General visual polish of the dock tiles / inspector.

---

## 10. How to resume

1. Read this doc top-to-bottom + `git log` (latest: right-panel-inspector-only dock commit).
2. Confirm build is green (`dotnet build -c Debug -p:Platform=x64`).
3. Next work is the **Canvas view (real C3 + C4)** in §7. Then C-full, then N-tracks.
4. Work in the smallest steps that end **green + committed**. This is a WinUI app with **no
   automated UI test** — the author (a human) verifies each visible step by running it, so
   build+commit each increment and hand off for a visual check rather than stacking unverified
   changes. Layout/visual bugs are visible and reversible, not silent.
