# VideoDirector — Multi-Track Plan

**Status:** working prototype on a **clean two-mode architecture** (Edit / Arrange, strictly
segregated — see §5A). Track 1 + Track 2 clips, bottom dock, Edit mode (frame one clip + Ken
Burns preview), Arrange mode (composite + drag/wheel-move/resize PiPs) all functional. Next:
**PiP reshaping** → Start Time field → C-full → N-tracks. See §6 (progress) and §7 (next steps).
This doc is the durable source of truth; readable standalone by any session or tool.

> **Design lesson (do not repeat):** an earlier attempt grew multiple overlapping ways to
> select/arrange/edit that bled into each other and broke. The fix, and the law now, is
> **strict mode segregation: the mode alone decides what input does — nothing else.** Any new
> interaction must belong to exactly one mode. Do not add a second parallel way to do a thing.

**Working principle (non-negotiable):** every step ends in a **green build and a commit**.
Never leave the tree in a non-building state. Small steps, committed often, so we are never
more than one `git revert` from safety. This is the antidote to the failure mode that
destroyed an earlier version of this code.

---

## ⚠️ KNOWN LIMITATIONS — skipped for now, MUST eventually be addressed

These were consciously deferred to keep the prototype moving. They are **not "won't do"** (that
is §9) — they are **owed work**. Do not let them quietly become permanent.

1. **🔴 PiP rendering (§7A) — attempted and FAILED at least SEVEN times.** Every attempt to make
   the overlay PiP reshape/move without artefacts has failed: it greens on resize, blanks after
   manipulation, and the reshape handles disappear behind the video surface. Root cause: a
   `MediaPlayerElement` is a GPU video surface, not a plain image, even when paused — resizing/
   moving it corrupts it and it composites over XAML. **Seven distinct fixes, seven failures.**
   The still-image proxy (commit `2098e57`) also failed (the visibility swap didn't take effect).
   **Verdict: stop patching. The whole PiP-render component must be rebuilt from scratch**, ideally
   folded into §7B with "arrange = still image / playback = live video" designed in from the start.
2. **🟡 Overlay video is static while scrubbing (minor — author confirmed not a priority).**
   Overlays *are* visible, selectable, and reorderable — an earlier draft wrongly said they were
   invisible; they are not. The real, narrow limitation: when you scrub, the Track 2 overlay shows a
   **static frame** rather than seeking to the scrubbed moment — only Track 1 seeks live. The static
   composite-seek (§7G) only seeks the spine; seeking overlays per-frame too would fix it.
3. ~~**4-track model not built.**~~ **DONE (§7B, `b539df5`).** Strict track list — 1 spine + ≤3
   overlay tracks, one generic `EvaluateOverlays` loop indexed by track, one player/surface per
   track. Always 4 tracks; adding a track is data, not new branches.
4. ~~**Duplicate / Remove not re-homed.**~~ **DONE (`fac2aca`).** Right-click any timeline block for
   a Duplicate / Remove flyout. *(The long failure here was self-inflicted: `PointerReleased` fired
   for the right button too and rebuilt the Canvas, destroying the element the context gesture had
   started on — swapping RightTapped→ContextRequested→ContextFlyout was never going to fix it.)*
5. ~~**Spine (blue) drag has no ghost-follow.**~~ **DONE (`9668c73`)** — implemented exactly as the
   approach below describes.
   **Approach (implemented):** the spine is gapless/order-based, so a clip's position is *derived* from its
   index — there is nothing continuous to write (unlike an overlay's free `StartTime`), which is why a
   data-driven move snaps. So decouple visual from commit: (1) draw the dragged block as a free
   **ghost** at `cursorX − grabOffset`; (2) reflow the *other* clips to open a gap at the insertion
   point; (3) **commit the index change once on release** — no live data churn. Insertion index =
   count of *other* clips whose centre is left of the cursor (dragged clip excluded) → monotonic, so
   it cannot oscillate. Cosmetic follow, real discrete commit.
6. **🟡 Overlay drag rebuilds the whole timeline every pointer-move** (minor flicker / wasted work).
   Should move just the dragged block during the drag and rebuild once on release.
7. **🟡 Scrub robustness.** Rapid scrubbing re-seeks the paused main player many times a second
   (possible jank), and crossing a spine clip boundary loads the next source asynchronously (brief
   lag / first-time stall). Needs throttling/debounce + smarter source preloading.
8. **🟡 Back-compat.** `PlacementScale` was removed in favour of independent width/height, so **old
   saved projects lose custom PiP sizes** (revert to 0.3×0.3). Acceptable at prototype stage; revisit
   before any real persistence guarantees.
9. **🟠 UX/UI is prototype-grade and inconsistent — needs a dedicated design pass (author call).**
   The interface was grown reactively, not designed, and it shows: a developer **telemetry HUD**
   (raw text) is user-visible; **mode is signalled three ways** (accent border + top-left pill +
   panel header); several **competing idioms** (floating transport pill, right inspector panel,
   bottom timeline strip); **edit controls are a text panel** (Start/Mid/End/Record buttons) for
   what is a *visual* task — commercial NLEs do keyframing largely **on-canvas / direct-manipulation**;
   **ad-hoc visual language** (hand-picked hex colours, hand-drawn chrome) instead of a palette/
   spacing/type system; and the inspector **conflates** per-clip editing with project actions
   (Save/Load/Clear). Fix = a proper pass grounded in established tools (Premiere / Final Cut /
   DaVinci Resolve / CapCut): produce a **small design spec first** (layout, mode model, control
   placement, visual tokens), *then* implement — not more reactive tweaks. Telemetry should become
   opt-in/removed; Save/Load/Clear should move out of the per-clip inspector.

Severity: 🔴 blocks the core experience · 🟠 functional gap / regression · 🟡 polish/robustness.

> **BASELINE (2026-07-24, tag `baseline-7b`).** 7B is done and the multi-track prototype works
> end-to-end: 4 labelled tracks; one proportional timeline with a ruler-scrub, red playhead and
> selection shading; tap to select, drag to move (overlays reposition in time *and* move between
> tracks; spine reorders via a ghost), right-click Duplicate/Remove; drop files onto a row to add
> them to that track (row-aware, clamped into free gaps — no within-track overlap); play resumes
> from the playhead. **The one remaining 🔴 is §7A** (PiP render rebuild), untouched by design and
> gated behind its own test-first plan.

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

## 5A. The two-mode architecture (definitive — supersedes the old "canvas/edit" spec)

There are exactly **two strictly-segregated modes**. The mode alone decides what pointer input
does; there is no other condition. No bleed-over, no third state, no parallel mechanisms.

### Edit mode (transient)
Edit **one** clip only — identical behaviour on every track (Track 1, 2, … x).
- **Enter:** select a clip in the dock (Track Dashboard). (Entry point may broaden later.)
- Shows **only that clip**, full-screen. All other clips/overlays are hidden.
- **Zoom & Motion controls** (same layout for all tracks): scroll = zoom, drag = pan,
  Set Start / Mid / End = motion keyframes, Curve profile.
- **Start Time** field in the panel: **read-only/auto for Track 1** (sequential), **editable
  for Track 2+**. *(Field on Track 1 not yet added — see §7.)*
- **Play = ONLY this clip's Ken Burns preview** (loops); never the composite.
- **Exit** button (top-left, shown only in Edit) → returns to Arrange.
- `InputMode = Content`. Everything you do affects only the edited clip.

### Arrange mode (default)
The whole canvas, like playback will look.
- **Play = the entire composite** (all tracks).
- **Move / resize the PiP windows:** drag a PiP → move; wheel over a PiP → resize.
  (Focused on Track 2 for now; same behaviour to extend to all tracks later.)
- A PiP is only on screen when the playhead is inside its time window — **accepted WYSIWYG
  sacrifice**: to arrange a PiP, pause at a moment it's visible.
- `InputMode = ArrangePips`. All pointer input targets PiP placement (bounds hit-test via the
  full-screen InputLayer — the only reliable pointer catcher, since the PiP's MediaPlayerElement
  video surface does not raise its own pointer events).

### Enforcement notes
- The mode is held in the engine (`EditorMode`); it sets `DirectorPlayerControl.InputMode`.
- Edit isolation: entering Edit hides every non-edited surface (Track 1 edit releases both
  overlay slots; Track 2 edit hides the main players + the other slot). `ExitToArrange` restores
  the composite.
- `Play` is routed by mode (Edit → clip-scoped preview; Arrange → composite playback).

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
- `df8cdec` — C3 parity: Set Mid + Curve Profile in the overlay inspector.
- `2f2c2b2` — **the two-mode rebuild (§5A)**: replaced the tangled arrange/composite/edit-content
  machinery with strictly-segregated **Edit** and **Arrange** modes. Clip-scoped Edit preview;
  Arrange drag/wheel move+resize of the PiP under the cursor; Exit button; EDIT/ARRANGE badge.
- `3b913e9` — fix: Edit mode now shows **only** the edited clip (was leaking the overlay over
  Track 1 via a stale flag); moved Exit button clear of the title-bar drag region.

**What actually works now:** the two-mode architecture (§5A) — Edit mode frames one clip with
Ken-Burns preview; Arrange mode shows the composite and lets you drag + wheel-resize a Track 2
PiP. Plus: Track 1 sequential base, Track 2 overlays (up to **two simultaneous PiPs**, each
independently framed/placed/animated), motion, opacity, muted audio, add/duplicate/remove, the
bottom dock.

**Placement representation:** `PlacementWidth` + `PlacementHeight` + `PlacementCenterX/Y`
(normalized 0..1) on the clip — **independent dimensions**, so the box can be reshaped to any
aspect, crop-filled (`UniformToFill` + box clip): no distortion, no bars. **Note:** crop-fill is
reliable on a **still bitmap** but *not* on the live paused video surface — see §7A (still-image
redo); reshaping is currently broken pending that.

---

## 7. Next steps (in priority order)

Reshaping and timeline navigation can't be patched in piecemeal any longer; the design below was
worked through with the author (2026-07-23) and is built as **one interconnected rework**, not
incrementally — incremental change on an unsettled design is exactly what broke the PiP work (get
one overlay working, add a second, spend ages fixing everything that breaks). Operating rules:
- **Validate fast, harden slow.** Stand up a rough end-to-end prototype (proportional trackbar +
  working scrubber over a static composite) first, prove the design in the hand, *then* polish.
- **Stubbing to reach end-to-end is acceptable** — a known, deliberate cost of prototyping.
- **Rewrite over re-patch** when a component keeps failing (the still-image PiP below is a *rewrite*
  of the PiP-rendering path, not another workaround).
- **Bounded, not open-ended:** exactly **4 tracks max** (1 spine + 3 overlays).

### Already done (keep)
- **Reshaping model + UI:** independent `PlacementWidth`/`PlacementHeight` (default 0.3×0.3), the
  **PiP Width/Height** inspector rows, and the 8 reshape handles (`ClassifyGrab`: corner = W+H,
  edge = one dim, interior = move, opposite-anchored; wheel = uniform). Good; they stay.
- **Unified inspector (panel parity):** one inspector bound to `SelectedClip`; identical skeleton
  for every track; track-specific rows toggle via `IsTrack1Selected`/`IsOverlaySelected`
  (Speed + Transition-out → Track 1; PiP W/H + Opacity → overlays; Start Time shared, read-only for
  Track 1). Keep.
- **What FAILED:** rendering the PiP by reshaping the **live paused `MediaPlayerElement`** — it's a
  GPU video surface, not a plain image, so resizing/moving it blanks/greens it and it composites
  over the handles. Frame-refresh churn removed (`0ea76fc`). This is what §7A replaces.

### A — PiP render REBUILD  *(do AFTER 7B; never attempt before or outside it)*
> **Status: FAILED 7 times (2026-07-23). Do not patch — rebuild inside 7B.** Attempts 1–6 fought the
> live video surface (seek-to-refresh, `StepForwardOneFrame`, debounce); attempt 7 (`2098e57`) had the
> right idea (still-image proxy) but bolted it onto the tangled `ApplyOverlayBox` state machine and
> shipped it blind/untested — the visibility swap never fired, so the user was still manipulating the
> video surface. Root cause across all 7: a `MediaPlayerElement` is a **GPU video surface, not a plain
> image, even paused.** **No more standalone attempts. No "low complexity / low risk" labels — the
> seven-failure track record IS the estimate; treat as HIGH risk.**

**Sequencing:** **7B ships first (functional, not perfect). *Then* rebuild the PiP render** — inside
7B's new per-track pipeline, never as a patch on the old one.

**The invariant (a design rule, not a runtime toggle that can fail to fire):**
- In **Arrange**, the manipulable PiP is **always a plain element** (bitmap/border). The overlay
  `MediaPlayerElement` is **not instantiated or used in Arrange at all** — so there is literally
  nothing to green, blank, or composite over the handles.
- The **video surface renders only during playback**, sized/positioned to match the box, and is
  **never itself resized or moved by the user.**

**How this rebuild differs from the 7 failures (the lessons):**
1. **Rebuild the component; never bolt onto the old one.**
2. It lives **inside 7B's clean pipeline** — no tangled edit/animating/`EvaluateOverlays` state to
   lose the swap in.
3. **Verify the cursed mechanism first, in isolation, with the author** (Iteration 1 below) before
   stacking anything on it — the one deliberate exception to "bulldoze then harden".
4. **If it misbehaves, diagnose — never re-patch blind.** Add temporary on-screen/log output the
   author can read back (is the render path hit? is the image source set? is the video element
   actually absent?) and fix the real cause. All 7 failures were undiagnosed guesses.
5. **High risk by default**; sequence so a failure costs one tiny step, not a session.

**Iteration 1 — explicit goal + acceptance (get the author's YES before ANY further step):**
- **Goal (only this):** in Arrange, a Track-2 overlay renders as a **plain still bitmap** (its
  thumbnail) in the PiP box.
- **Acceptance — all author-confirmed:** (a) the overlay shows as a static image in its box;
  (b) changing the box size (a numeric field is enough) does **not** green / blank / corrupt it;
  (c) **no overlay `MediaPlayerElement` exists in Arrange** (confirm in code *and* on screen).
- **Explicitly OUT of scope for iter 1:** handles, drag-reshape, wheel-resize, marks/framing on the
  image, and the playback video swap.
- **If (a)–(c) don't all hold: stop and diagnose (lesson #4). Do not proceed, do not re-patch.**

**Later iterations, each gated on the previous being author-confirmed:**
2. Reshape **handles** on the still image. 3. **Corner/edge drag + wheel** reshape. 4. **Marks/
framing** applied to the still. 5. **Playback swap** — hide the still, show the live video surface
sized to the box.

#### 7A acceptance test spec (TDD — mandatory; this is the structure that replaces "trust me")

No UI test harness exists, and the cursed symptoms (green/blank/handles-behind-video) are *rendering*
behaviours WinUI can't self-observe. So every iteration has **two** test kinds:
- **(A) Automatable, written test-first** — pure logic (geometry/reshape math, state predicates) and
  observable invariants (element type in the box; whether any video-pipeline call happened). Requires
  a small **seam added up front**: extract geometry/reshape/state logic into UI-free methods, expose a
  **video-pipeline call counter** and a **render-state inspector**, add a tiny unit-test project.
- **(B) Author-run visual checks** — binary pass/fail, for the pixels the stack can't self-test.

**The backbone test, written first and kept GREEN through every iteration:** *no `MediaPlayerElement`
is instantiated or used in Arrange.* If that stays green, the entire 7A failure class is impossible.

**Iteration 1 — overlay is a plain static bitmap in Arrange; no video surface**
- A: **T1.1 (backbone)** overlay active in Arrange ⇒ no overlay `MediaPlayerElement` (not created, or
  Source null + not in tree). **T1.2** element filling the box is the plain-image type, not
  `MediaPlayerElement`. **T1.3** that Image's `Source` is non-null. **T1.4** box-rect math: (W,H,vp,A)
  → `fitW*W × fitH*H` centered. **T1.5** a box resize makes **zero** video-pipeline calls (counter==0).
- B: **V1.1** shows a recognizable still (not black/green/blank); **V1.2** numeric W/H resize stays
  clean across many rapid changes; **V1.3** no video ever plays in the PiP in Arrange.

**Iteration 2 — reshape handles on the still image**
- A: **T2.1** 8 handle positions == 4 corners + 4 edge-midpoints of box rect (±ε). **T2.2** re-assert
  T1.1/T1.2 *with handles present* (element under handles still the plain image). **T2.3** handles
  Visible **iff** (Arrange ∧ selected ∧ not playing) — assert all 2³ state combos. **T2.4**
  `ClassifyGrab(point)` returns the right region for a table of known points.
- B: **V2.1** all 8 handles visible on top of the image; **V2.2** stay visible through field-resize;
  **V2.3** deselect / playback ⇒ hidden.

**Iteration 3 — corner/edge drag + wheel reshape**
- A: **T3.1** corner grab + delta → opposite-anchored (W,H,center). **T3.2** edge grab changes one
  dimension only. **T3.3** interior grab moves center, W/H unchanged. **T3.4** wheel scales W and H by
  the same factor. **T3.5** results respect min-size + [0.05,1.0] clamps. **T3.6** a full drag-reshape
  gesture makes **zero** video-pipeline calls (counter==0). **T3.7** reshaped box (aspect ≠ image) ⇒
  fill == `UniformToFill` and clip rect == box rect (crop-fills, no bars).
- B: **V3.1** corner=W+H, edge=one dim, interior=move, all tracking the cursor; **V3.2** image
  crop-fills and **stays clean at narrow/extreme aspects** (the exact case that greened before);
  **V3.3** handles follow the box; **V3.4** wheel resizes uniformly.

**T1.1 and T\*.6 (the "no video surface / zero video calls" invariants) are the tests that make the
7A curse un-repeatable.** Write them first; keep them green; do not proceed past a red gate.

### B — Track model  *(build all 4 at once; generic over overlays, spine special)*
- **4 tracks max: 1 spine + up to 3 overlay tracks** (3 simultaneous PiPs).
- **Strict per-track:** clips are sequential and never overlap within a track; simultaneity is
  expressed by *using another track*.
- **Spine (Track 1) is special and NOT in the generic loop:** gapless, defines total length, keeps
  the A/B-roll + transitions.
- **Overlay tracks are a fixed array of ≤3, uniform:** one loop `for each overlay track: evaluate`,
  **exactly one player per overlay track** (no dynamic pool), **upper-track transitions stubbed**
  (HardSnap only) for the prototype. *(If this starts to need a player pool or per-overlay A/B,
  that's the signal it's drifting into the expensive over-general version — stop and reconsider.)*
- **Uniform interaction/shaping across all tracks:** same `CinematicOperation`, inspector, marks,
  reshape handles, edit/arrange flow. Track 1's "full-frame" becomes a **default placement value**,
  not a special code path — which *removes* code. The only asymmetry is data-level (role + defaults).
- **Data model:** replace `OverlayClips` + hardcoded "slot 1/2" with a **track list** (spine +
  overlay array). This is the foundation everything else hangs off.

**Concrete mechanism — a "+N loop capped at 4", not hardcoded slots 3/4:**
- **Data:** `OverlayTracks` (an `ObservableCollection<OverlayTrack>`, 1..3), each holding an ordered
  `Clips` collection + a default corner; `MaxOverlayTracks = 3`. Spine stays `TimelineNodes`.
  Migrate the old flat `OverlayClips` → `OverlayTracks[0]`.
- **State → arrays:** today's scalar slot pairs (`_activeOverlay1/2`, `_overlayMediaPlayer1/2`,
  `_overlayAspect1/2`) become **length-3 arrays** indexed by track.
- **Surfaces:** **pre-declare 3** overlay units in XAML (bounded), exposed as an `OverlayVisuals[3]`
  array (grid + video + still + transform + handles); z-order = track index. Chosen over runtime
  instantiation / `ItemsControl` because `MediaPlayerElement`-in-template is awkward and we're capped
  anyway — the *logic* stays fully generic, only the surface count is fixed.
- **Evaluation:** replace `EvaluateOverlays` (slot-1/slot-2 branches) with one `EvaluateTracks(t)`
  loop over `OverlayTracks[i]`; every existing slot method's `slot==1 ? _x1 : _x2` becomes `_x[i]`,
  so the duplicated bodies collapse into one indexed body.
- **Why this and not hardcoding 3/4:** adding a track becomes **data** (a new `OverlayTrack` + one
  pre-declared surface), never a new branch. It is *less* code than four hardcoded slots, and it
  removes the "add another slot and everything breaks" failure. N>4 would only bump the constant +
  add a surface — no code-path change. The spine is **not** in this loop (keeps its own A/B roll).

### C — Story-time authority + additive transitions
- **One story-time model** turns `clip durations + transition durations` into each clip's
  `[start,end]` on the global timeline. **Both playback AND the trackbar/scrubber read from it** —
  never a parallel calculation, or they disagree at transitions (where it shows most).
- **Transitions are ADDITIVE** (author's call: clips are very short, so no content is sacrificed to
  an overlap). Timeline = a strict non-overlapping sequence `[clipA][transAB][clipB][transBC]…`;
  total = Σ clip + Σ transition; **"gapless" = contiguous incl. the transition segments.**
- **Accepted tradeoff:** additive transitions blend **boundary frames** (freeze-frame dissolve /
  dip), not a live-motion crossfade (which needs overlap + would eat clip content). Fine for short
  clips. *(Future, explicitly not now/soon: fake a motion crossfade via an ongoing Ken-Burns.)*
- **Verify** how today's engine composes transitions (the A/B roll implies an *overlap* model) and
  align it to additive — additive likely *simplifies* the A/B usage (hold boundary frames + blend).

### D — Shared scale (pure proportional)
- **`px = seconds`** — one **scale authority** (`TimeToX` / `XToTime`) used by *both* block layout
  and the scrubber. Linear + monotonic ⇒ the scrubber is **truthful** (position = a real read of
  story time). This is why pure-proportional beat floored/columned layouts.
- **Two single-authority layers:** story-model → `[start,end]` (C), then scale → x (here). Nothing
  computes pixel positions independently.
- **Short-clip grabbability = zoom, DEFERRED** (additive, not interconnected). The scale carries a
  `pixelsPerSecond` from day one so zoom later just changes that number. At second-scale clips and
  reasonable story lengths, fit-to-width is already grabbable.

### E — Proportional trackbar  *(replaces the equal-tile dock lanes)*
- N track rows of proportional blocks on the shared scale. **Spine:** gapless, **order-based**
  (reorder = ripple; start = cumulative). **Overlay tracks:** **free-positioned** (drag a block
  along time = set its start; gaps allowed).
- Re-home select / reorder / remove onto the blocks. **Stays visible during playback** so the
  playhead can travel across it.

### F — Truthful global scrubber
- A playhead overlaying the trackbar; **drag/click → `XToTime` → static composite seek**. Replaces
  the click-a-clip-then-Exit navigation.
- **Convenience:** selecting/grabbing a PiP **auto-parks the playhead inside that PiP's window** so
  it's guaranteed visible for arranging.
- **Edit mode is unchanged:** it keeps the per-clip proportional slider (scrub one clip + trim). The
  global scrubber is the **Arrange** time control — mode-contextual, per §5A.

### G — Composite static-seek  *(the scrubber's engine dependency)*
- Position everything at story-time *T* **without playing**. **Spine** = live seek of the main
  player (scrubbing seeks present frames fine — unrelated to the paused-*resize* blanking).
  **Overlays** = **stills** (thumbnail if active at *T*) → light, no multi-decoder churn.

### H — Playback
- Spine A/B + **up to 3 overlay players (one per overlay track)**; upper-track transitions stubbed.

**Build order (revised after the 2026-07-23 session):**
- **DONE:** C (story-time authority), D (scale), E (proportional trackbar, single track display),
  F (scrubber + tap-select + drag-move/reorder), G (static composite-seek, spine only).
- **NEXT — and the first target when work resumes: B — the 4-track model.** Rush it to *functional,
  not perfect* (generic N-track evaluation, one player per overlay track, transitions stubbed). This
  is the end-to-end spine we did not reach last session; it comes before any more polishing.
- **THEN — A (PiP render rebuild)** inside B's new pipeline, per the iteration-1-gated plan in §7A.
- **THEN harden:** work the ⚠️ Known Limitations list (top of doc).

**Treat the current timeline UI (E/F) as provisional** — expect to adapt it to B's N-track data model
rather than assume it survives intact.

### Later polish (after the rework; additive, non-blocking)
- **Zoom / horizontal scroll** for dense or long timelines (the scale already supports it).
- Ruler / time ticks; snapping to a time quantum; dragging clips along time with snap.
- Add/remove-track UI beyond the 4-track default; per-clip audio/unmute (see §9).

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
3. Next work is the **multi-track timeline rework** in §7: §7A still-image PiP first (decoupled),
   then the 4-track model + story-time authority + pure-proportional trackbar + truthful scrubber.
4. Work in the smallest steps that end **green + committed**. This is a WinUI app with **no
   automated UI test** — the author (a human) verifies each visible step by running it, so
   build+commit each increment and hand off for a visual check rather than stacking unverified
   changes. Layout/visual bugs are visible and reversible, not silent.
