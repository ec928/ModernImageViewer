# Multi-Image Auto-Play UI Redesign Plan

## 1. Overview & Core Philosophy

This document outlines the redesign and implementation plan for the **Multi-Image Auto-Play UI** in ModernImageViewer.

### Core UX Tenets
1. **Minimal First-Touch Surface**: Keep what the user first sees to an absolute minimum, offering only the fundamental options required to start an enjoyable presentation.
2. **Complexity Behind ⚙ (Gear / Wheel Icon)**: Deep mathematical parameters, recipe probabilities, saliency thresholds, and geometry settings are kept strictly isolated behind an **Advanced Options** entry point.
3. **Smart Pacing as Default**: Compositions automatically adjust dwell times (dense multi-image layouts hold longer, solo/duo layouts hold shorter).
4. **Cinematic Window Untouched for Now**: `CinematicWindow` remains separate and active. It will only be deprecated once the Multi-Image Auto-Play experience is proven to be far superior.

---

## 2. UI Architecture

```
┌────────────────────────────────────────────────────────┐
│                      CANVAS                            │
│                                                        │
│   ┌────────────────────────────────────────────────┐   │
│   │ [ ← Back ] Advanced Show Settings (Deep)       │   │
│   │ • 6 Recipe Odds (Off/Rare/Normal/Frequent)     │   │
│   │ • Face Zoom & Crop Margins                     │   │
│   │ • Camera Style & Drift Ratios                  │   │
│   │ • Matting, Gaps & Tilt Degrees                 │   │
│   │ [Reset to Defaults]                     [Done] │   │
│   └───────────────────────▲────────────────────────┘   │
│                           │ (Drill-down via ⚙)         │
│   ┌───────────────────────┴────────────────────────┐   │
│   │ Primary Show Card (Fundamental Options Only)   │   │
│   │ • Images: [Solo 1] [Story 1-4] [Collage] [Wall]│   │
│   │ • Pacing: [5s] [8s] [12s] [20s] [Random]       │   │
│   │   ☑ Smart Pacing (enabled by default)          │   │
│   │ • Camera Motion: [Off ──────●───── Dynamic]    │   │
│   │ • Soundtrack: [Track Info] [⏮] [⏭] [Vol Slider]│   │
│   │ • [ ⚙ Advanced show settings... ]              │   │
│   └───────────────────────▲────────────────────────┘   │
│                           │ (Opens from HUD ⚙)         │
│ ┌─────────────────────────┴──────────────────────────┐ │
│ │ Unified HUD: [←] [+] │ [Rows] [Cols] │ [Surprise] │ │
│ │              [ ▷ Play ] [ ⚙ ] [ ⛶ ]                │ │
│ └────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────┘
```

---

## 3. Surface Specifications

### Surface 1: Unified HUD Playbar (`UnifiedHud`)
- **Single Source of Truth**: Only one Play/Pause button (`AutoPlayButton`). No duplicate Play buttons inside cards or flyouts.
- **Micro-Progress Countdown**: A 2px high accent-colored bar or countdown indicator visualizes elapsed time against `_layoutSeconds`.
- **Keyboard Shortcuts**:
  - `Space`: Play / Pause
  - `Left Arrow`: Previous layout (`Undo()`)
  - `Right Arrow`: Next layout (immediate advance)
  - `F11`: Fullscreen

### Surface 2: Primary Show Card (First-Touch Popover)
Opened via `SurpriseCardButton` (`⚙` icon next to Play in HUD):
- **Width**: ~412px (replaces the bloated legacy mega-flyout).
- **Header**: "Auto-Play" with a subtle `[ ⚙ ]` icon button for direct access to advanced options.
- **Section 1: Images per Layout**:
  - Chips: `[Solo 1]`, `[Mix 1–4]`, `[Collage 2–6]`, `[Wall 9–16]`.
- **Section 2: Duration (Single-Row Dual-Thumb Range Slider)**:
  - Unified single-row control: `Duration` [ ───●─●─── ] `14s - 18s`.
  - When thumbs coalesce (e.g. `17s - 17s`), renders as a single merged thumb disc.
  - Interactive behaviors:
    - Drag left thumb: adjusts start duration.
    - Drag right thumb: adjusts end duration.
    - Drag between thumbs: slides the window maintaining the duration span.
    - Click track outside thumbs: jumps the respective thumb.
    - Double-click: collapses both thumbs to the clicked second for instant fixed duration.
  - Unifies fixed and random hold times:
    - Equal values (`start == end`): fixed duration (`AutoPlayRandom = false`).
    - Range values (`start < end`): random/smart-paced duration (`AutoPlayRandom = true`).
  - Toggle: `Smart pace` (enabled by default). Plain English helper: *"When duration has a range, holds dense multi-photo layouts toward the longer end and solo/pair layouts toward the shorter end"*.
- **Section 3: Camera Motion**:
  - Slider: `Off` (0%) to `Dynamic` (100%), default 50%.
- **Section 4: Soundtrack (Dedicated Multi-Row Architecture)**:
  - Header: SemiBold `Soundtrack` with `[ Settings ]` bridge action on right + subtle divider.
  - Row 1: `Background music` [Folder summary subtitle] with right-aligned `ToggleSwitch`.
  - Row 2: `Volume` with full 140px slider and percentage readout (`78%`).
  - Row 3: Current song title (untruncated) with interactive `[ ‹ Song ]` and `[ Song › ]` buttons.
  - Standard Windows 11 Fluent Blue (`#4CC2FF` / `#0078D4`) styling with high-contrast text and interactive button chrome.
  - Strict two-column Grid alignment across all rows terminating at a uniform right vertical edge.
- **Bottom Action**:
  - `[ ⚙ Advanced auto-play settings... ]` styled as a subtle card bridge button.

### Surface 3: Advanced Show Settings (Deep Controls Behind ⚙)
Invoked from the Primary Show Card. Operates as an in-place drill-down view (or docked side panel) with a `[ ← Back ]` header:
- **Design Recipes & Frequency**:
  - 6 LayoutComposer recipes (`Hero and companions`, `Balanced mosaic`, `Filmstrip`, `Gallery wall`, `Scattered prints`, `Circles`).
  - Frequency dropdowns: `Off`, `Rare`, `Normal`, `Frequent`.
- **Face & Subject Intelligence**:
  - Face zoom maximum magnification (Off to 3.5×).
  - Face zoom frequency (0–100%).
  - Uncut face safety margin (50–100%).
  - Smart grouping by date & folder.
  - Skip screenshots and duplicate shots.
- **Camera & Transitions**:
  - Camera style: `Auto`, `Zoom in`, `Zoom out`, `Pan`.
  - Zoom vs Drift tendency (0–100%).
  - Face targeting likelihood (0–100%).
  - Transition style: Crossfade, Dip-to-Dark, Slide.
  - Transition duration.
- **Canvas & Matting**:
  - Backdrop style: Ambient blur, Matte dark, Clean light.
  - Gallery gap scale (0.5× to 2.0×).
  - Maximum print tilt angle (0° to 15°).
  - "Keep my canvas" toggle.
- **Dedicated Command Footer**:
  - Anchored bottom-left: `[Reset to defaults...]`.
  - Anchored bottom-right: `[Done]`.

---

## 4. Implementation Phasing & Status

- [x] **Phase 1: Rapid UI Mockup (Bin Prototype)**
  - Redesign `ShowSurpriseCard()` in `MultiViewControl.Surprise.cs`.
  - Remove duplicate Play, Previous Layout, and Next Layout buttons from inside the card.
  - Reorganize into Primary Card (Fundamentals) + In-Place Advanced View (`_showingSurpriseAdvanced` toggle).
  - Wire the gear wheel `⚙` button and header navigation.
  - Built directly to `ModernImageViewer\bin\ModernImageViewer.exe` for immediate visual testing.
- [x] **Phase 2: Duration Slider & Pacing Consolidation**
  - Replaced discrete number buttons with dual `Duration from` and `Duration to` continuous sliders.
  - Seamlessly handles both fixed duration (`from == to`) and random hold intervals (`from < to`).
  - Added inline volume slider into the Primary Card soundtrack row.
  - Ensured `SmartPace = true` is default.
- [x] **Phase 3: Visual Polish & Fluent WinUI Compliance**
  - Widened card to 530px; eliminated all label truncation.
  - Locked 230px right column enforcing strict vertical alignment across sliders, readouts, toggles, and dropdowns.
  - Single-row dual-thumb `RangeSlider` with solid Windows Blue (`#4CC2FF`) span and collapse behavior.
  - Restored and wired layout transport controls (`‹ Layout`, `Play / Pause`, `Layout ›`) for instant stepping.
  - Consolidated actions: removed inline Settings button from Soundtrack header and added dedicated `Soundtrack & audio` expander in Advanced Settings.
  - Vertical centering on multi-line rows (`MinHeight = 48`) and path truncation safety with `TextTrimming.CharacterEllipsis` + tooltip.
  - Proximity & grouping: 16px bottom margin below segmented camera mode and design preset chips.
  - Slider thumb & track accent overrides guaranteeing consistent Windows Blue styling even at 0.
- [ ] **Phase 4: Micro-Progress & Countdown**
  - Add optional micro-progress countdown bar on `UnifiedHud` during playback.
- [ ] **Phase 5: Cinematic Parity Evaluation**
  - Benchmark Multi-Image Auto-Play against legacy `CinematicWindow`.
  - User review and acceptance testing before any convergence steps.
