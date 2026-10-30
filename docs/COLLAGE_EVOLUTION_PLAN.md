# Multi-View Collage Evolution: Implementation Plan

Authoritative architectural plan for expanding **Multi-View** into a seamless, high-performance collage and template creation studio, superseding the legacy Collage Editor.

---

## 1. Executive Summary & Philosophy

- **The Problem with Legacy Collage**:
  The existing `CollageEditorWindow` was built on an unconstrained 2.5D freeform layer canvas (manual coordinates, rotation handles, arbitrary bounding boxes). This made the 90% use-case—structured, beautifully proportioned layouts—clumsy, slow, and frustrating to align.
- **The Solution**:
  Multi-View (`MultiViewControl`) already possesses the ideal interaction mechanics: physical partition lines, live divider dragging, Alt+drag direct-manipulation swapping, 1-click Hero compositions, and aspect-ratio-clamped JPEG export.
- **The Phased Approach**:
  We evolve Multi-View gently, without breaking its existing fast-browsing and comparison strengths:
  - **Step 1**: Extensible **Save and Load Layout** mechanism (`.mivlayout` JSON).
  - **Step 2**: **Per-image Borders & Framing** via the cell right-click context menu.
  - **Step 3+**: Advanced polish (Polaroid mats, Copy-to-Clipboard, and future non-destructive overlays).

---

## 2. Step 1: Save & Load Layout Mechanism

### 2.1 UX & Menu Integration
In `MultiViewControl.xaml`, the existing bottom playbar **Save** button flyout is expanded into two clear sections:

```
+---------------------------------------------+
| EXPORT IMAGE                                |
|  720p   (1280×720)                          |
|  1080p  (1920×1080)                         |
|  1440p  (2560×1440)                         |
|  2160p  (3840×2160)                         |
|  This window                                |
|  Custom…                                    |
|---------------------------------------------|  <-- Divider
| LAYOUT TEMPLATES                            |
|  Save Layout as…            (Ctrl+Shift+S)  |
|  Open Layout…               (Ctrl+O)        |
+---------------------------------------------+
```

### 2.2 Extensible File Schema (`.mivlayout`)
Layout files will be human-readable, versioned JSON (`.mivlayout` or `.json`):

```json
{
  "$schemaVersion": 1,
  "name": "Triple Hero Layout",
  "created": "2026-10-30T15:00:00Z",
  "canvas": {
    "aspectRatio": "16:9",
    "backgroundColor": "#FF0A0A0A"
  },
  "layout": {
    "type": "RowSplit",
    "ratio": 0.65,
    "first": {
      "type": "Leaf",
      "slot": 0
    },
    "second": {
      "type": "ColumnSplit",
      "ratio": 0.50,
      "first": { "type": "Leaf", "slot": 1 },
      "second": { "type": "Leaf", "slot": 2 }
    }
  },
  "cells": [
    {
      "slot": 0,
      "imagePath": "C:\\Photos\\Landscape.jpg",
      "fillMode": "Fill",
      "panOffset": { "x": 0.0, "y": 0.0 },
      "zoomScale": 1.0,
      "border": {
        "thickness": 0.0,
        "color": "#00000000",
        "padding": 0.0,
        "cornerRadius": 0.0
      }
    }
  ]
}
```

### 2.3 Forward-Compatibility & Extensibility Rules
1. **Schema Versioning**: Every saved file records `$schemaVersion: 1`. If future versions add new properties (e.g. text layers, asymmetric mats), older files load gracefully with default values.
2. **Missing Files Grace Period**:
   - If a saved image is deleted or on an unplugged drive, the cell displays a clean placeholder tile with the missing filename, allowing the user to either locate the photo or drop a new photo into that slot.
3. **Template Reusability ("Apply to Current Photos")**:
   - When opening a layout, if the user already has photos selected in Multi-View, the app offers:
     - **Open Project**: Restores the exact saved photos and paths.
     - **Apply Layout as Template**: Retains the currently loaded photos, but arranges them into the saved layout ratios and framing settings.

---

## 3. Step 2: Per-Image Borders & Framing

Rather than cluttering the global playbar, border controls are introduced per-image via the **right-click context menu**, providing both surgical per-photo customization and 1-click global styling.

### 3.1 Cell Context Menu Addition
Right-clicking any cell reveals:

```
+---------------------------------------+
| Move Left / Up                        |
| Move Right / Down                     |
| Swap with...                        > |
| Solo / Maximize                       |
| Frame & Border...                   > |  <-- New Flyout Sub-menu
| Remove from View                      |
+---------------------------------------+
```

### 3.2 "Frame & Border" Flyout Controls
Selecting `Frame & Border...` opens a lightweight, dark-themed flyout:

1. **Border Color**:
   - Quick Palette: Clean White, Deep Black, Accent Blue, Gold, None.
   - Custom Color Picker (optional or eyedropper).
2. **Border Thickness**:
   - Slider: `0px` to `24px`.
3. **Cell Margin / Gap**:
   - Slider: `0px` (flush edge-to-edge) to `32px` (spaced cards).
4. **Corner Radius**:
   - Slider: `0px` (sharp) to `24px` (rounded card).
5. **Drop Shadow**:
   - Toggle on/off for soft elevation against the canvas.
6. **"Apply to All Photos" Button**:
   - One click applies the chosen border, thickness, and gap across all cells in the current Multi-View session.

### 3.3 Integration with Step 1
All border, gap, color, and corner radius values serialize directly into the `border` block of `.mivlayout`, ensuring that styled collages reload with 100% visual fidelity.

---

## 4. Step 3+: Future Roadmap (High-Level)

1. **In-Cell Nudge & Pan (Framing)**:
   - When in "Fill" mode, allow holding an modifier key or secondary drag to pan the photo inside its cell frame to avoid awkward head crops.
2. **Polaroid / Asymmetric Matting**:
   - A 1-click option to multiply the bottom border padding by 2.5× to achieve a classic Polaroid or fine-art gallery mat.
3. **Instant "Copy Collage to Clipboard" (`Ctrl+C`)**:
   - Render the collage directly to the Windows Clipboard at 1080p / 4K without requiring saving a file to disk first.
4. **Overlay / Freeform Layer (Strictly Non-Destructive)**:
   - If freeform overlapping photos are ever explored, they will be treated strictly as **floating overlay badges/cards** on top of the locked mosaic base—never compromising the divider-dragging and partition geometry underneath.
5. **Retirement of Legacy Collage**:
   - Once Steps 1 and 2 land, the old `CollageEditorWindow` and its associated codebase can be safely deprecated and removed from the tree.

---

## 5. Architectural Verification & Invariants

- **No Performance Regressions**: Border margins and thickness changes modify WinUI element properties (`BorderThickness`, `CornerRadius`, `Margin`); they do **not** trigger WIC re-decoding of the high-fidelity bitmaps.
- **Export Parity**: `MultiViewExport.cs` will read each cell's border thickness, color, and corner radius when drawing to the Win2D `CanvasRenderTarget`, ensuring exported JPEGs/PNGs match the on-screen presentation exactly.
