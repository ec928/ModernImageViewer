# Unifying Cinematic & Multi-Image Auto-Play: Architecture & Options Analysis

**Document Status (6 Nov 2026): not an open decision.** The direction has been chosen. The multi-image engine, built from scratch, is the foundation. Cinematic slideshow was abandoned as the engine to rebuild. Do not pick one of the options below and build it. Sequence beats come back later, on the storyboard, when there is a design for them. See `MULTI_IMAGE_DESIGN.md` (R14). This file remains the options analysis written before that choice.

**Earlier status:** Architectural Proposal / Reference Document
**Date:** November 2026
**Target Application:** ModernImageViewer (`MultiViewControl` and `CinematicWindow`)  

---

## 1. Executive Summary & Problem Statement

ModernImageViewer currently features two independent presentation engines:
1. **Cinematic (`CinematicWindow`)**: A single-image slideshow player designed for sequenced playback, automatic pacing, and manual Ken Burns keyframe editing.
2. **Multi-Image Auto-Play (`MultiViewControl`)**: A procedural collage presentation engine that arranges 1 to 16 images into dynamic layouts (Solo, Mix, Collage, Wall), moves a virtual camera across the canvas, and continuously crossfades between layouts.

### The Problem
If the long-term roadmap converges these two engines into a single unified presentation system, two critical capabilities currently unique to Cinematic must be introduced to Multi-Image:
1. **Sequencing & Management**: Cinematic possesses a deterministic timeline (`SequenceBar`) where slides can be reordered, duplicated, hidden, and saved for exact replay. Multi-Image possesses only an **Image Tray**, which acts as a passive, unsequenced pool of raw files rather than a curated sequence of scenes.
2. **Manual Ken Burns Overrides**: Cinematic allows the author to drag interactive start and end crop boxes (`WysiwygOverlay`) to guarantee exact framing. Multi-Image’s camera movement is 100% procedural (`CameraMove.Plan`).

### The Non-Negotiable Constraint
Multi-Image's core appeal is **frictionless spontaneity**: dropping photos in and immediately watching an intelligent, aesthetically pleasing presentation without having to curate a playlist or edit keyframes. Any unification must preserve this instant-play experience while offering optional curation tools.

---

## 2. Architectural Comparison

| Dimension | Cinematic (`CinematicWindow`) | Multi-Image (`MultiViewControl`) |
| :--- | :--- | :--- |
| **Atomic Unit** | **Slide (1 Image)** | **Layout / Scene (1–16 Images)** |
| **Sequencer** | Deterministic timeline (`SequenceBar` filmstrip) | None (procedural real-time shuffle from pool) |
| **The "Tray"** | A sequenced playlist of clips | An unsequenced asset pool of unused photos |
| **Camera Model** | 2D image crop (StartRect to EndRect) | Full-canvas virtual camera (zoom, pan, drift) + floating parallax |
| **Overrides** | Interactive on-screen handles (`WysiwygOverlay`) | None (Camera modes: Auto, Zoom In, Zoom Out, Drift) |
| **Persistence** | `.cinshow` (JSON sequence + override ledger) | `.mivlayout` (single static canvas state) |
| **Replayability** | 100% deterministic | Probabilistic (seeded or random shuffle) |

---

## 3. Four Unification Strategies

```
┌────────────────────────────────────────────────────────┐
│                      CANVAS                            │
│  [ ← Back ]                                            │
│  ┌──────────────────────────────────────────────────┐  │
│  │   Multi-Image Collage or Solo Image              │  │
│  │   (Optional Green Start / Red End Camera Box)    │  │
│  └──────────────────────────────────────────────────┘  │
│                                                        │
│  HUD: [ ‹ Layout ] [ ▷ Play ] [ Layout › ]   [ ⚙ ]     │
├────────────────────────────────────────────────────────┤
│ TRAY / STORYBOARD DRAWER (Collapsible)                 │
│ Tab A: [ Asset Pool (24) ]  |  Tab B: [ Show Story (8) ]│
└────────────────────────────────────────────────────────┘
```

### Option 1: "Story Deck" (Spontaneous by Default, Pinned Scenes on Demand)
*Keep the Image Tray as an asset pool; add a lightweight scene-pinning workflow.*

* **Mechanics**:
  - The Image Tray remains an unsequenced holding pen.
  - A subtle **[ 📌 Pin to Show ]** or **[ Save Scene ]** button sits on the HUD or Auto-Play card.
  - When watching Auto-Play or manually building a layout, clicking this button captures the composition, dwell time, and camera path into a collapsible **Story Deck** drawer.
  - Auto-Play provides two playback modes:
    - `Dynamic Shuffle` (current continuous procedural generator).
    - `Story Replay` (plays through pinned scenes in order).
* **Ken Burns Overrides**:
  - Pinned scenes can be inspected. If paused on a pinned scene, clicking *Edit Camera* reveals visual Start and End boxes on the canvas.
* **Verdict**:
  - **Pros**: Zero cognitive load on casual users. Does not alter existing Tray behavior.
  - **Cons**: Building a 30-slide presentation requires manual step-by-step pinning.

---

### Option 2: Dual-Mode Image Tray (`[ Pool ]` vs `[ Sequence ]`)
*Transform the existing bottom Tray into a toggleable view.*

* **Mechanics**:
  - A segmented pill at the top of the Tray toggles between:
    - `[ Asset Pool (36) ]`: Unused images (existing behavior).
    - `[ Show Sequence (14) ]`: Linear filmstrip of scenes.
  - In **Sequence Mode**, each tile in the strip represents an ordered scene. Dragging reorders scenes; right-clicking offers *Hide*, *Duplicate*, or *Remove*.
  - When Auto-Play is started in Sequence mode, it advances through the strip sequentially from left to right.
* **Verdict**:
  - **Pros**: Reuses existing Tray UI space; no new floating HUD elements needed. Directly mirrors Cinematic’s `SequenceBar`.
  - **Cons**: Representing multi-image collages within a linear thumbnail strip requires mini-composite thumbnails.

---

### Option 3: "Auto-Draft Storyboard" (Generative First, Editable Second)
*One-click generative show creation with full manual scrubbing and editing.*

* **Mechanics**:
  - The user adds a folder of images to the session.
  - A single action **[ Auto-Draft Show ]** runs `LayoutComposer` across the tray pool and drafts a complete, paced storyboard (e.g. *Scene 1: Solo portrait -> Scene 2: 3-Image Hero -> Scene 3: 2-Image landscape -> Scene 4: Wall*).
  - The filmstrip displays the drafted scenes.
  - **Immediate Playback**: The user can press Play immediately without editing anything.
  - **Granular Curation**: The user can scrub to any scene in the filmstrip, swap individual photos, adjust dwell duration, or adjust the Ken Burns framing.
* **Verdict**:
  - **Pros**: Bridges the gap between automation and manual craft. Eliminates the labor of building 20 layouts by hand. Unifies Cinematic naturally (a Cinematic show is just an Auto-Draft where image count is locked to `Solo 1`).
  - **Cons**: Highest initial UI implementation scope.

---

### Option 4: Unified Object Hierarchy (Engine & Data Foundation)
*Refactor the data model so that a Show is simply an ordered list of Layouts.*

* **Data Model**:
  ```csharp
  public class ShowProject
  {
      public string Version { get; set; } = "1.0";
      public string Title { get; set; } = "Untitled Show";
      public SoundtrackSettings Soundtrack { get; set; } = new();
      public List<ShowSceneRecord> Scenes { get; set; } = new();
  }

  public class ShowSceneRecord
  {
      public string Id { get; set; } = Guid.NewGuid().ToString();
      public MultiViewLayoutFile Layout { get; set; } = new(); // 1 to 16 images
      public double DwellSeconds { get; set; } = 12.0;
      public string Transition { get; set; } = "Crossfade";
      public CameraOverrideDto? CameraOverride { get; set; } // Null = procedural, Non-null = manual
      public bool IsHidden { get; set; }
  }

  public class CameraOverrideDto
  {
      public NormalizedRect StartViewport { get; set; }
      public NormalizedRect EndViewport { get; set; }
  }
  ```
* **Architectural Realization**:
  - Under this model, a classic Cinematic slideshow is simply a `ShowProject` where `Layout.CellCount == 1`.
  - Multi-View Auto-Play without a loaded show is procedural; with a loaded show, it is deterministic.

---

## 4. Ken Burns Overrides in Multi-Image

Cinematic’s Ken Burns editor operates on a single image. In Multi-Image, manual camera overrides must address two levels of framing:

### Level A: Canvas-Level Camera Framing (Composition Level)
* The author defines where the virtual camera points on the entire composite canvas at $t = 0$ (Start) and $t = 1$ (End).
* **UI Affordance**: Two draggable, resizable rectangles overlaid on the canvas:
  - **Green Box**: Start camera viewport.
  - **Red Box**: End camera viewport.
* **Solo Case**: When the layout is a `Solo 1` image, the Canvas-Level camera is mathematically identical to Cinematic’s current Ken Burns cropping.

### Level B: Cell-Level Subject Targeting (Focal Point Level)
* In a multi-image collage, the canvas camera might be procedural, but the author wants to guarantee that a specific person's face or subject in Cell #3 is never cut off or is zoomed into.
* **UI Affordance**: Right-clicking any cell -> *Camera Subject...* -> drop a focal pin. `LayoutComposer` and `CameraMove.Plan` already use `CameraSubjects()` and `SubjectOnCanvas()`; setting an explicit target simply overrides the automated face detector.

---

## 5. Recommended Phased Implementation Roadmap

When the time comes to execute unification, the recommended pathway avoids big-bang rewrites:

```
┌────────────────────────┐      ┌────────────────────────┐      ┌────────────────────────┐
│ Phase 1: Data Model    │ ───► │ Phase 2: Show Strip    │ ───► │ Phase 3: Ken Burns UI  │
│ ShowProject & Scenes   │      │ Storyboard & AutoDraft │      │ Visual Viewport Boxes  │
└────────────────────────┘      └────────────────────────┘      └────────────────────────┘
                                                                             │
                                                                             ▼
                                                                ┌────────────────────────┐
                                                                │ Phase 4: Convergence   │
                                                                │ Deprecate Cinematic    │
                                                                └────────────────────────┘
```

1. **Phase 1: Persistence Foundation (`ShowProject`)**
   - Create the `ShowProject` and `ShowSceneRecord` data classes.
   - Add "Save Show (`.mivshow`)" and "Open Show" to Multi-View's Save/Load menu.
2. **Phase 2: Storyboard Drawer & Auto-Drafting**
   - Implement the collapsible Storyboard strip beneath the canvas (Option 3 or Option 1).
   - Implement "Auto-Draft from Tray" so users can generate an editable sequence with one click.
3. **Phase 3: Visual Ken Burns Overlay**
   - Port the Start/End keyframe box overlay (`WysiwygOverlay`) to `MultiViewControl`.
   - Support both Solo layouts and multi-cell canvas viewports.
4. **Phase 4: Cinematic Deprecation & Migration**
   - Add a converter to import existing `.cinshow` files into `.mivshow` projects.
   - Deprecate `CinematicWindow` in favor of Multi-View in `Solo 1` mode.
