# Premium Multi-Image & Storyboard UX Architecture Plan

**Document Status:** Implementation Plan  
**Target:** ModernImageViewer (`MultiViewControl`)  
**Date:** November 2026  

---

## 1. Objectives & Guiding Principles
- **Professional, Premium Feel**: Eliminate informal/playful phrasing ("Surprise me") in favor of industry-standard terms ("Auto-Mix").
- **"Less is More" & High Information Scent**: Keep the canvas and playbar clean, uncluttered, and obvious.
- **Progressive Disclosure**: High-frequency actions are one click away; deep configuration remains tucked into dedicated panels.
- **Foundation for Tile Editing**: Ensure the canvas remains active and visible while inspecting scenes in a bottom filmstrip.

---

## 2. Phased Implementation Roadmap

### Phase 1: Terminology & Playbar Hierarchy Modernization
1. **Professional Terminology Alignment**:
   - Replace `Surprise me` with **`Auto-Mix`** (or `Mix` where space is constrained).
   - Align labels across UI, tooltips, shortcuts, and documentation (`docs/MULTI_IMAGE_UI.md`).
   - Settings card titled **`Auto-Mix Settings`** (aligned with the mode name).

2. **Playbar Restructuring & Mode Decoupling**:
   - **Group A (Media Shelves)**: Group `[ Image Pool (N) | Storyboard (N) ]` together as a unified 2-segment shelf pill on the left of the playbar. Clicking either opens the drawer to that tab without side-effects on playback.
   - **Group B (Playback Engine & Transport)**:
     - Mode indicator/toggle: `[ Storyboard | Auto-Mix ]`
     - Transport group: `[ ‹ ]  [ ▶ Play Show / Play Mix / || Pause ]  [ › ]  [ ⚙ ]`
   - **Group C (Canvas Layout)**: `[ N Rows | Columns ]`
   - **Group D (Export & Canvas)**: `[ ⛶ Fullscreen | Canvas | Save/Load | ⚙ Settings ]`

---

### Phase 2: Filmstrip Drawer Docking for Live Canvas Editing
1. **Docked Filmstrip Mode**:
   - Enable switching the Storyboard drawer from a centered modal card into a docked **Bottom Filmstrip** positioned directly above the playbar.
   - Keeps the full canvas visible and interactive.
2. **Synchronized Canvas Projection**:
   - Single-clicking any scene card in the filmstrip projects that scene live onto the canvas for instant inspection.
   - Reordering scenes via drag-and-drop or adjusting dwell time remains instant.

---

### Phase 3: In-Situ Tile Selection & Micro-HUD
1. **Explicit Tile Selection**:
   - Clicking any cell/tile on the canvas establishes an active selection state (subtle 1.5px accent border).
2. **Contextual Floating Micro-HUD**:
   - A sleek, 4-button pill floating directly adjacent to the active tile:
     - **🔄 Swap / Replace**: Quick-pick from unplaced Image Pool photos or local file.
     - **⬡ Shape**: Quick-picker popover for shapes (Rectangle, Squircle, Arch, Oval, etc.).
     - **🖼 Frame**: Instant trigger for Frame & Border card.
     - **🔍 Solo / Inspect**: Fullscreen solo view.
3. **Menu Demotion**:
   - Reserve the 13-item right-click context menu strictly for secondary/structural operations (Layer ordering, Freeform toggle, Remove).

---

### Phase 4: Isolated Camera & Framing Overrides
1. **Explicit "Edit Camera" Mode**:
   - Camera motion Start (Green) and End (Red) crop wireframes remain strictly hidden during normal playback and tile editing.
   - Only revealed when entering an intentional "Edit Camera" state from scene options.
