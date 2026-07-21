# VideoDirector — Multi-Track Plan

**Status:** design agreed, foundation started. This doc is the durable source of truth for
the multi-track work. It is readable standalone by any session or tool — no prior chat context
required.

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

## 6. Current state (already committed)

- `cf26e47` — additive fields on `CinematicOperation`: `StartTime` (+`StartTimeSeconds`),
  `Opacity`, `EndTimeOnTimeline`, `IsActiveAt(storyTime)`. Non-breaking; Track 1 ignores them.
- `83340a4` — overlay editing/management UI + playback correctness fixes (continuous
  story-time off the active player, fixed clip-boundary double-count, deterministic
  pause/resume, media-open race, SMTC disabled, telemetry throttled) + aspect-correct sizing.
- Build is green. Overlay currently uses a **single** static transform (`OverlayTransform1/2`
  on the grid) and **two** overlay players for **two simultaneous overlays**.

**Player-allocation shift to make:** current = 2 (Track 1 A/B) + 2 (overlay slots, 2
simultaneous). Target = **one clip playing per track at a time**, so the two overlay players
become **Track 2 and Track 3** players (1 each). Add a per-track A/B pair only when enabling
upper-track transitions (target 2/track → 6 total; fine on the target hardware). Start lean
(1/track, hard cuts) and add the pair with transitions.

---

## 7. Implementation phases

Recommended order. Each phase (and sub-step) ends green + committed.

### Phase B — Clip-type convergence (behaviour-preserving)
Goal: Track 2 is a real `CinematicOperation`; today's static-PiP behaviour preserved.
- `DirectorViewModel`: `OverlayClips` → `ObservableCollection<CinematicOperation>`;
  `SelectedOverlay` → `CinematicOperation`. Store upper tracks as a **list** (even with one
  entry) so N-tracks is a contained change later — do **not** hardcode "Track 2".
- `AddOverlayAsync` → creates a `CinematicOperation` with `StartMark`/`EndMark` at scale 0.3
  (the default PiP), muted, `StartTime` = current playhead.
- `ProjectData` (save/load) serializes the unified type; keep backward-compat load
  (old array + old overlay wrapper) mapping onto the new type.
- Engine overlay methods take `CinematicOperation`; read `StartTime`/`OpDuration`/`Opacity`/
  `IsActiveAt`; map the static transform onto `StartMark.Scale/X/Y` for now
  (`ApplyOverlayTransform` reads `StartMark`).
- UI: overlay inspector bindings updated to the unified type (start/duration/opacity/z-order).
- Delete `OverlayClip.cs` once nothing references it.
- **Commit.** Test: overlays behave exactly as before, backed by the unified type.

### Phase C — Content/placement split, motion, view switching (core capability)
Sub-stepped; this is the big one.

- **C1 — Placement box + clip.** Add placement to `CinematicOperation`: `PlacementScale`,
  `PlacementX`, `PlacementY` (output space; defaults = corner @30%). Nested render structure:
  outer box (placement, sized/positioned/**clipped**) + inner `MediaPlayerElement` (content).
  Content still static (`StartMark`) for now. Track 1's placement = identity (full frame).
  **Commit.** Test: PiP is a proper clipped box; content can't spill outside it.
- **C2 — Content motion.** Interpolate the clip's Start/Mid/End marks over its own duration in
  the upper-track render path, reusing the same `UpdateSpatial` math Track 1 uses (static clip
  = `StartMark == EndMark`). **Commit.** Test: an upper clip can Ken Burns / push-in.
- **C3 — Full-screen edit + unified controls.** Selecting any clip → Edit view: placement =
  identity (full-screen), marks active, the **same** Set Start/Mid/End + curve + record
  controls as Track 1 (unify the inspector — one control set, not two). Edit/Canvas toggle.
  **Commit.** Test: editing a Track 2 clip is identical to editing a Track 1 clip.
- **C4 — Canvas-view placement editing.** In Canvas view, select a PiP → drag to move,
  handles to resize; writes back to placement fields. **Commit.**

### Phase E — C-lite arrangement dock
Goal: move arrangement to a toggleable bottom dock; right panel → inspector-only.
- Bottom dock: horizontal lanes per track, clips left-to-right, **one shared horizontal
  scroll**. Reoriented tile template. Drag-to-reorder within a lane; file-drop onto a lane.
- Toggle (reuse the storyboard-visibility pattern). **Handle the canvas `SizeChanged`** the
  toggle causes so the WYSIWYG rectangles and overlay aspect-sizing stay aligned (this is the
  one real coupling — failure is visible, not silent).
- Transport pill stays floating over the canvas (available even when the dock is hidden);
  trim slider stays with it for now.
- Right panel: remove the clip lists; keep only the selected-clip inspector (+ project
  Save/Load/Clear). **Commit.**

### Phase D — Track 3
Trivial once the model is a track-list and the dock renders N lanes: add a second upper track
(z-order 3) + its lane + its player. 3-way compositing is the same code as 2-way, one more
layer. **Commit.** (Can slot in anytime after B; placed here for UI cleanliness.)

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

- **C-full** — time-scaled timeline (px = seconds, ruler, drag-to-position, snapping,
  playhead in the dock). The dock's shared-scroll container is the on-ramp.
- **N > 3 tracks** — the model is built as a track-list so this is additive.
- **Advanced per-clip transitions** and **canvas-level transitions/effects**.
- **Audio** — proper mixing/ducking/per-clip levels. Until then: upper tracks muted by default.

---

## 10. How to resume

1. Read this doc + `git log` (`cf26e47`, `83340a4`).
2. Confirm build is green.
3. Start at the first unfinished phase; work in the smallest steps that end green + committed.
4. After each phase, launch the app and manually verify the specific behaviour that phase adds
   (this is a WinUI app — no automated UI test; verification is by running it).
