# Video Director — UI/UX brief (for external design review)

This is a self-contained description of a multi-track video tool's interface, written so a
designer or another AI with **no prior context** can critique it and propose a better UI/UX. It
describes what the product is, its interaction model, every control and where it lives, and the
specific inconsistencies that need solving. Please read the "Problems" and "Design questions"
sections as the actual ask.

---

## 1. What the product is

A **multi-track video sequencer / compositor**, a desktop app built in **WinUI 3 / Windows App
SDK** (mouse + keyboard primary; touch possible). It composes several video/image clips into one
output:

- **Track 1 — the "spine".** A row of clips played **sequentially, end to end, no gaps**. Always
  shown **full-frame**. It defines the total length of the piece. Clips have optional transitions
  between them (crossfade/dip, treated as additive time).
- **Tracks 2–4 — "overlay" tracks.** Clips placed **freely in time** (gaps allowed, one clip at a
  time per track) and composited **over** Track 1 as **picture-in-picture (PiP)** boxes — a smaller
  window you can move, resize and reshape anywhere on the frame, with an opacity.
- **Bounded to 4 tracks** (1 spine + 3 overlays = up to 3 simultaneous PiPs).

Every clip — spine or overlay — is the *same underlying object*. A clip can also carry a **"Ken
Burns" motion**: an animated zoom/pan across the clip defined by keyframes (a Start framing, an
optional Mid, an End framing) eased by a curve. A clip can be a **still** (playback speed 0) that
you Ken-Burns across for a duration.

---

## 2. Interaction model — three modes

1. **Play.** The whole composite plays. A transport bar controls it; a playhead sweeps the
   timeline; clips currently on screen are highlighted on the timeline.
2. **Arrange.** The composite is shown **paused at the playhead**. This is where you lay things out:
   scrub the timeline, drag/reorder clips, and directly manipulate the PiP boxes on the canvas
   (drag to move, edge/corner drag to resize/reshape, wheel to scale). Nothing is full-screen.
3. **Edit.** **One clip** is shown **full-frame** so you can set its internal **content framing** —
   the Ken Burns keyframes and curve, plus per-clip properties. Entered by clicking a clip on the
   timeline, or double-clicking the clip's picture on the canvas. Exited via a Done button, Esc, or
   double-click.

A design rule the app tries (imperfectly) to honour: **the mode alone decides what input does** —
e.g. in Arrange a drag moves a PiP box; in Edit the same drag pans the clip's content. The mouse
wheel likewise means "resize the box" in Arrange but "zoom the content" in Edit.

---

## 3. Where the controls live today

**A. Timeline dashboard (bottom, full width).** A proportional timeline: a time ruler with ticks,
one row per track (each track a distinct colour, carried onto that track's on-screen PiP frame +
a "T2/T3/T4" badge so you can tell which picture is which row), a red playhead with a time readout.
Interactions: tap a block = select (which currently also enters Edit); drag a block = move it
(overlay = reposition in time and/or drag to another track's row; spine = reorder); right-click =
Duplicate / Remove; drop a file on a row = add to that track; scrub on the ruler.

**B. Right inspector panel.** Appears while in Edit (and can be pinned open). Top: an "EDITING
&lt;filename&gt;" header + a **Done** button. Below, a single scrolling panel that mixes several
control groups depending on the selected clip's track:
- **Zoom & Motion (all tracks):** **Start / Mid / End** keyframe buttons (capture the current
  framing), a **Record** button (live motion capture), a **Curve** profile dropdown.
- **Track 1 only:** **Playback Speed**, **Transition-out** style + duration.
- **Overlays only:** **PiP Width**, **PiP Height**, **Opacity**, **Start Time**.
- **Shared:** **Duration**.
- Also, project-level **Save / Load / Clear** buttons live in this same panel.

**C. Transport pill (bottom-centre, floating).** Play/Pause, Prev/Next, a range/trim slider,
global speed, loop, and a toggle that pins the inspector panel.

**D. Canvas direct-manipulation.** In Arrange: drag a PiP to move, grab an edge/corner to
resize/reshape, wheel to scale. In Edit: drag = pan the clip's content, wheel = zoom it.

**E. Mode signalling.** In Edit: an accent border frames the whole canvas, and the inspector shows
the EDITING header. A small persistent text pill (top-left) shows the current mode name.

---

## 4. Problems to solve (the actual ask)

1. **"Select" and "Edit" are fused.** Selecting a clip (a single click on the timeline)
   immediately drops you into full-screen Edit. There is no lightweight "select this clip to
   inspect and position it while still seeing the whole composite." This is backwards for the
   overlay controls: **PiP size / position / opacity / timing are decisions about how a clip sits
   *in the composite*** — you want to make them *while looking at the composite* — yet they're only
   reachable in full-screen Edit, which **hides** the composite.

2. **Controls are inconsistent across track types.** The inspector shows a *different* set of
   controls for a Track 1 clip (Playback Speed, Transition) vs an overlay clip (PiP W/H, Opacity,
   Start Time). The keyframe buttons are shared but the surrounding layout and available actions
   differ, so "editing a clip" feels like a different tool depending on the track.

3. **No consistent handling of stills / speed / Ken Burns across tracks.** Track 1 supports playback
   speed 0 = a *still* you Ken-Burns across for a set duration. Overlay tracks expose **no
   equivalent** — there's no speed control and no obvious way to designate or handle an overlay
   still — so you can't even attempt the same creative move on an overlay. The controls simply
   aren't there or aren't consistent.

4. **Content-framing controls are a text-button panel for a visual task.** Setting the Ken Burns
   motion means clicking **Start / Mid / End** buttons in a side panel after manually framing via
   drag/scroll. Mainstream editors do this **on the canvas** (draggable start/end framing
   rectangles, a keyframe lane on the timeline) — far more direct for what is a visual operation.

5. **Mode entry/exit is inconsistent and easy to trip.** Entering Edit is a **single** click on
   the timeline but a **double** click on the canvas — same intent, two gestures. It's easy to end
   up in Edit without meaning to, and the only cues are an accent border + a panel header + a small
   text pill.

6. **The inspector conflates per-clip editing with project actions** (Save / Load / Clear live in
   the same panel as the clip's edit controls).

---

## 5. Constraints & preferences

- **Platform:** WinUI 3 / Windows App SDK — the result should feel native to that design language
  (Fluent), not a bespoke skin. Desktop, mouse + keyboard first.
- **Bounded:** at most 4 tracks (1 spine + 3 overlays). No need to design for unlimited tracks.
- **Every clip is one object type;** track differences should ideally be *defaults/role*, not
  fundamentally different tools.
- **Stage:** working prototype. Significant redesign is welcome; a **small design spec first**
  (mode model, control placement, on-canvas affordances, visual tokens) is preferred over
  incremental tweaks.
- The **timeline dashboard** (colours, per-track identity, spotlight-by-dimming of what's on
  screen) is considered reasonably good already; the focus is the **edit/arrange/inspector model**.

---

## 6. Design questions we want answered

1. How should we **separate "select & position in context" from "deep-edit one clip"**? Should
   selecting a clip keep the composite visible with an in-context inspector, and full-screen Edit be
   a deliberate second step?
2. What is a **single, consistent control model that works for every track** (spine and overlays
   as the same kind of thing, differing only by default placement/role)?
3. How should **stills, playback speed, and Ken Burns** be presented **consistently** across spine
   and overlay clips?
4. Should content **framing/keyframing move onto the canvas** (draggable framings, a keyframe
   lane), and if so what's the interaction and how does it coexist with the timeline?
5. What's the right **mode model and its signalling** — how many modes, how you enter/leave each,
   and how the user always knows which they're in and what their inputs will do?
6. Where should **project-level actions** (Save/Load/Clear) live, separate from per-clip editing?

Concrete, Fluent-native proposals (layouts, gestures, where each control lives, and the mode
transitions) are what we're after — ideally a short spec that could then be implemented.
