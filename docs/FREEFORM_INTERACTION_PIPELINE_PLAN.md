# Freeform Card Interaction Pipeline: Implementation Plan & Regression Post-Mortem

## 1. Executive Summary & Purpose

In Multi-View mode, freeform (floating) cards allow users to freely position, rotate, shape, frame, resize, and solo-maximize photos on a canvas. A series of regression errors introduced in commit `cf6cef6` (`v0.33.2`) and subsequent micro-patches severely broke this subsystem:
- Floating and grid cards became unselectable or difficult to select.
- Toolbar menus (`[ ⇄ ⌕ 🎨 🔲 | ✕ ]`) either failed to receive pointer clicks, disappeared, or failed to update the visual photo when a shape was picked.
- Rotation handles `( ↻ )` timed out after 1.2 seconds, failed to rotate, or floated detached from the actual image.
- Overlapping cards suffered from "Z-index leapfrogging," where moving the cursor across one card caused background cards to elevate and steal mouse focus.
- Ghost drags caused cards to follow the mouse without any button pressed, or clicks to fail to register when releasing.
- Rogue top-right canvas toolbars appeared floating on the empty viewport background.

This document serves three vital purposes:
1. **Permanent Engineering Record**: Documents the exact git history of when the code worked, what broke it, and an exhaustive post-mortem of all failed AI patch attempts.
2. **Systematic AI Failure Analysis**: Documents why three successive instances of advanced AI models (Gemini 3.8 Flash working on high reasoning) utterly failed to resolve this regression despite having full git history, working code, and an architecture plan.
3. **The Ground-Truth Restoration Plan**: Specifies the exact reversion and restoration path to return the interaction pipeline to the proven `6eefcf1` baseline.

---

## 2. Git History: Working Baseline vs. Breaking Commits

### 2.1 The Working Baseline: Commit `6eefcf1`
* **Commit Hash**: `6eefcf14cd4c04895474b8c606f38326a61aa17f`
* **Author**: Ed C
* **Date**: Sat Nov 7 14:59:35 2026 +0000
* **Subject**: `v0.33.1 (internal): frame width 0.5x to 1.5x, Play show names its preset`

#### How the Pipeline Functioned in `6eefcf1`:
1. **Predictable Resting Elevation**: Cards maintained their natural Z-index (`card.ZIndex = 5000 + i`, `Translation.Z = 256 + (i * 8)`).
2. **Measured Hover Routing**:
   ```csharp
   // Only cellGrid listened to PointerMoved, and ONLY if topControls was not yet visible
   cellGrid.PointerMoved += (s, e) =>
   {
       if (_isHudVisible && !topControls.IsHitTestVisible)
           SetCellHover(true);
   };
   ```
   `slotGrid.PointerMoved` was **not** attached, preventing pointer flutter across card edges.
3. **Controlled HUD Visibility**:
   ```csharp
   void UpdateHudVisibility()
   {
       bool show = isCellSelected || isMenuOpen || (_isHudVisible && isCellHovered);
       topControls.Opacity = show ? 1.0 : 0.0;
       topControls.IsHitTestVisible = show;
       topControls.Tag = isCellSelected;
   }
   ```
   When the global HUD hid, `HideHudAndOverlays()` checked `if (fe.Tag is bool isSel && isSel) continue;`. Only selected cards kept controls visible.
4. **Shaped Card Hit-Testing**:
   Non-rectangular cards had `photoGrid.Background = null` and inserted a `Polygon` `hitArea` matching `cardOutline`. Pointer events in transparent corners passed cleanly through to elements underneath.

---

### 2.2 The Breaking Commit: `cf6cef6`
* **Commit Hash**: `cf6cef6a0e9fb9af1706c523093c87ff5dae8e88`
* **Author**: Ed C
* **Date**: Sun Nov 8 16:50:16 2026 +0000
* **Subject**: `v0.33.2 (internal): fix freeform card resize lag, hover menu persistence, solo maximize, and add show titles, date captions, play mode pill`

#### What Changed & Why It Broke:
1. **Aggressive Hover Elevation Loop**:
   Commit `cf6cef6` added aggressive pointer listeners:
   ```csharp
   cellGrid.PointerMoved += (s, e) => { hoverOff?.Stop(); SetCellHover(true); ShowHudAndOverlays(); };
   slotGrid.PointerMoved += (s, e) => { hoverOff?.Stop(); SetCellHover(true); ShowHudAndOverlays(); };
   topControls.PointerEntered += (s, e) => { hoverOff?.Stop(); SetCellHover(true); Hud_PointerEntered(s, e); };
   ```
   And changed `UpdateCardElevation`:
   ```csharp
   bool active = isCellSelected || isMenuOpen || isCellHovered;
   if (active)
   {
       Canvas.SetZIndex(slotGrid, 8900);
       slotGrid.Translation = new Vector3(0, 0, 800);
   }
   ```
   *The Breaking Effect*: Whenever the cursor crossed overlapping cards, **any** card under the mouse set `isCellHovered = true`, immediately elevating to `ZIndex = 8900`. Background cards constantly leapfrogged foreground cards, hijacking mouse control.
2. **Bare Canvas Focus Theft**:
   Slot 0 (the 1×1 full-window grid cell behind floating cards) was not excluded from selection. Clicking anywhere on empty canvas selected Slot 0, displaying a giant blue selection rectangle around the viewport and placing a rogue toolbar in the top-right corner.

---

## 3. Systematic AI Failure Analysis: Why Three Successive AI Agents Utterly Failed

> [!CRITICAL]
> **READ THIS BEFORE TOUCHING ANY CODE:**
> Three separate instances of Gemini 3.8 Flash working on high reasoning failed across multiple hours to resolve this regression. This section documents the systemic cognitive and methodological traps that caused all three to fail, so that future agents do not repeat them.

### 3.1 The 4 Systemic Failure Modes of the Models

#### Failure Mode 1: The "Clever Re-architecture" Trap (Replacing Proven Code with Complex Synthetic Logic)
When an AI agent is presented with a regression problem and a complex codebase, it exhibits a strong systemic bias toward:
- Inventing elaborate new state machines (`_isInteractingWithCard`, `movePointerId`, `isMovingCard`).
- Introducing multi-tiered Z-index scheduling schemes (`9900`, `8500`, `5000`).
- Writing 300-line comprehensive implementation plans.
- Attempting to "solve" the problem by writing *more* code on top of broken code.

**The Reality**: A regression means the software **already worked**. The optimal, professional, and correct response to a regression is to diff the working baseline against the broken commit, identify the exact lines that diverged, and revert the broken delta. Building a synthetic 6-stage pipeline over an already broken architecture was an exercise in over-engineering that introduced more bugs than it solved.

#### Failure Mode 2: Anchoring on the Broken Commit (`cf6cef6`) as the Baseline
Every AI agent treated the current broken state of `master` (or `cf6cef6`) as the foundation to be repaired. They asked:
*"How do we fix the broken event handlers in `cf6cef6`?"*
Instead of asking:
*"How did `6eefcf1` handle pointer events, HUD visibility, and selection so cleanly without any of these bugs?"*
Because they anchored on `cf6cef6`, every fix was a patch on top of a broken premise.

#### Failure Mode 3: Symptom Whack-A-Mole
Each session operated in a tight loop of symptom-chasing:
- Symptom: Shape clicks don't work -> Fix: Capture pointer at container level -> Broke toolbar buttons.
- Symptom: Toolbar buttons don't click -> Fix: Rewire flyouts and reorder visual children -> Broke selection.
- Symptom: Background cards elevate on hover -> Fix: Add hardcoded `9900` elevation lock for selected cards -> Cards couldn't be selected, so lock never engaged.
- Symptom: Mouse drags card uncontrollably ("ghost drag") -> Fix: Check `!prop.IsLeftButtonPressed` inside `PointerMoved` and clear `movePointerId = 0` -> Swallowed mouse-up and broke stationary click selection completely.
- Symptom: User clicks canvas background -> Fix: Filter context menu items -> Left rogue canvas toolbar visible at top-right of window.

Every patch created an equal or greater regression in an adjacent event lifecycle because the AI patched individual handlers in isolation rather than maintaining global WinUI event invariants.

#### Failure Mode 4: Confusing Unit Test Passes with Physical Interaction Verification (Working Agreement Point 18 Violation)
Each model ran targeted unit tests (`WindowShapesTests`, `MultiViewLayoutFileTests`), saw green checkmarks (59 passed in 718ms), and prematurely declared: *"Root cause identified and resolved! The binary is ready."*
**Unit tests do not exercise the physical WinUI 3 pointer pipeline, mouse capture release, flyout modal overlays, or XAML render tree hit-testing.** Declaring a UI interaction bug resolved without physically verifying the gestures is a direct violation of Point 18 of the Working Agreement.

---

### 3.2 Chronological Chronicle of the Three AI Sessions

| Session | Agent Type | Primary Action Taken | Fatal Flaw / Regression Introduced | Result |
| :--- | :--- | :--- | :--- | :--- |
| **Session 1** | Gemini 3.8 Flash | Added `slotGrid.PointerPressed` with pointer capture; refactored `MakeWindowMenu` (`7e031c9`, `3d5599c`) | Container-level capture intercepted pointer events intended for child buttons; buttons stopped receiving clicks. | Utter failure: Toolbar buttons dead, selection erratic. |
| **Session 2** | Gemini 3.8 Flash | Designed 6-stage architecture plan; added elevation tiers (`9900`/`8500`); injected polygon `hitArea` (`08b8575`, `27f9c6f`) | Omitted resetting `movePointerId = 0` on `PointerReleased`. WinUI mouse device retained `PointerId = 1`, causing mouse movement with NO buttons pressed to latch cards to cursor ("Ghost Drag"). | Utter failure: Uncontrollable movement, card stuck to mouse. |
| **Session 3** | Gemini 3.8 Flash | Checked `!prop.IsLeftButtonPressed` in `PointerMoved` to kill ghost drag; filtered Slot 0 context menu (`9522f44`) | `PointerMoved` cleared `movePointerId = 0` during the mouse-up transition before `PointerReleased` arrived. Clean clicks were swallowed; card selection died completely. Rogue Slot 0 toolbar returned to viewport top-right. | Utter failure: Zero card selection, rogue canvas toolbar returned. |

---

### 3.3 Strict Directives for the Next AI Agent (How to Actually Fix This)

> [!IMPORTANT]
> **MANDATORY RULES FOR THE NEXT AGENT:**
> 1. **DO NOT** invent new state variables, boolean flags (`isInteracting`, `isMovingCard`), or custom gesture pipelines.
> 2. **DO NOT** attempt to patch the current code on `master` or `fix-freeform-interaction`.
> 3. **RUN THIS COMMAND FIRST**:
>    ```bash
>    git diff 6eefcf14cd4c04895474b8c606f38326a61aa17f cf6cef6a0e9fb9af1706c523093c87ff5dae8e88 -- ModernImageViewer\MultiViewControl.xaml.cs ModernImageViewer\MultiViewControl.Advanced.cs
>    ```
> 4. **REVERT AND RESTORE**: Restore the exact pointer routing, elevation logic, and HUD visibility checks from working commit `6eefcf1`.
> 5. **THE ONLY NECESSARY DELTA**: In `6eefcf1`, the only missing piece was connecting the shape menu click to `ApplyWindowGeometry(card)` / `ApplyCellShape()` (a ~3-line call so changing the shape updates the visual card). **That is all that was ever needed.**

---

## 4. Catalog of Failed Micro-Patches (Anti-Patterns to Never Repeat)

### Failed Attempt 1: Marking Container-Level Pointers as Handled
* **What was tried**: Adding `topControls.PointerPressed += (s, e) => e.Handled = true;` to prevent button clicks from falling through to card drag.
* **Why it failed**: In WinUI 3, marking `PointerPressed` as `Handled = true` on a container suppresses the bubbling gesture lifecycle. Child `Button` controls inside the container never fire their `Click` events.
* **Rule**: Never mark pointer pressed handled on a container that hosts interactive buttons.

### Failed Attempt 2: Overriding `photoGrid.Background` to Make Shaped Cards Clickable
* **What was tried**: Setting `photoGrid.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))` on shaped cards and collapsing `hitArea`.
* **Why it failed**: `photoGrid` is a rectangle. Giving it a background brush makes the **entire rectangular bounding box** opaque to hit testing. On circles, hexagons, and slanted panels, the invisible corners intercepted clicks intended for adjacent cards, making it impossible to select shapes behind or next to them.
* **Rule**: Shaped cards must keep `photoGrid.Background = null` and use `Polygon` `hitArea` so transparent corners pass clicks through.

### Failed Attempt 3: Isolated Elevation Lock Without Selection
* **What was tried**: Setting `Canvas.SetZIndex = 9900` on selected cards in `UpdateCardElevation()` to prevent hover theft.
* **Why it failed**: Clicking a card never called `SelectCell()` because `PointerPressed` captured the pointer and `PointerReleased` exited early. Because the card was never selected, the `9900` elevation lock never activated.
* **Rule**: Elevation rules depend on selection state; selection dispatch must be fixed before elevation locks can work.

### Failed Attempt 4: Changing Child Order Without Fixing Click Disambiguation
* **What was tried**: Moving `topControls` after `cellBorder` in `slotGrid.Children`.
* **Why it failed**: While this fixed child layering, simple clicking remained broken because `cellGrid.PointerPressed` claimed every click as a drag (`isMovingCard = true`).
* **Rule**: Visual tree ordering does not solve gesture disambiguation.

### Failed Attempt 5: Relying on a Grace Timer for Handles Without Selection State
* **What was tried**: Setting `WindowHoverGraceMs = 1200ms` on `hideHandles` to keep handles visible.
* **Why it failed**: If the user hovered a card and took more than 1.2 seconds to reach the rotation handle, `hideHandles.Tick` ran `ShowHandles(false)`, setting `rotate.IsHitTestVisible = false`. Because `window.PointerMoved` was not wired, moving toward the handle did not revive it.
* **Rule**: Handles on selected cards must remain permanently visible and hit-testable until explicitly deselected.

### Failed Attempt 6: Asymmetric Transform Application on Rotated Cards
* **What was tried**: Applying `WindowRotation` to cards without synchronizing container vs. photo transform origins.
* **Why it failed**: As seen in screenshot `media_1794170122735`, `slotGrid` stayed at 0° while the inner photo tilted, causing the dashed outline, rotation handle, and toolbar to float 180px above the photo across adjacent images.
* **Rule**: Container bounds, handles, and rendered image must share a single transform origin.

### Failed Attempt 7: Premature Button State Clearing in `PointerMoved` (Ghost Drag & Swallowed Click Race)
* **What was tried**: Checking `if (!prop.IsLeftButtonPressed) { movePointerId = 0; ... }` inside `cellGrid.PointerMoved` to kill lingering ghost drags when mouse buttons were up.
* **Why it failed**: In WinUI 3, during a mouse-up gesture, the platform frequently delivers a final `PointerMoved` micro-event with `IsLeftButtonPressed == false` right before or in tandem with `PointerReleased`. Because `PointerMoved` eagerly reset `movePointerId = 0` and released pointer capture, when `PointerReleased` fired immediately afterward, its condition `if (movePointerId != 0 && e.Pointer.PointerId == movePointerId)` evaluated to `false`. The clean click handler (`SelectCell`) was completely bypassed, and stationary clicks failed to select cards.
* **Rule**: Pointer capture and drag tracking IDs must only be finalized and retired inside `PointerReleased`, `PointerCanceled`, or `PointerCaptureLost`, never pre-emptively inside `PointerMoved`.

### Failed Attempt 8: Unconditional `isCellHovered` on Bare Canvas (Rogue Viewport Toolbar Regression)
* **What was tried**: Changing `UpdateHudVisibility()` from `show = isCellSelected || isMenuOpen || (_isHudVisible && isCellHovered)` to `show = isCellSelected || isMenuOpen || isCellHovered`, and attaching unconditional `cellGrid.PointerMoved` hover listeners.
* **Why it failed**: In Multi-View freeform mode, Slot 0 is the underlying 1×1 `CellsGrid` background element spanning the entire window. Whenever the mouse moved across any empty portion of the canvas, Slot 0's `PointerMoved` fired, marking Slot 0 as `isCellHovered = true`. Because `UpdateHudVisibility()` lacked an `IsBareCanvas(slot)` exclusion check, Slot 0's `topControls` turned fully opaque and hit-testable, placing an unexpected rogue toolbar (`[ ⇄ ⌕ 🎨 🔲 | ✕ ]`) in the top-right corner of the whole window (screenshot `media_1794176204027_81df31c1.jpg`).
* **Rule**: Bare canvas background cells (`IsBareCanvas(slot) == true`) must NEVER display cell HUD controls (`topControls`), badges, or selection borders.

---

## 5. Root Cause Analysis (WinUI 3 Mechanics)

### 5.1 Swallowed Clicks via Pointer Capture
1. `cellGrid.PointerPressed` captures the pointer and flags `e.Handled = true`.
2. WinUI 3 cancels the `Tapped` gesture event when `PointerPressed` is handled.
3. `cellGrid.PointerReleased` checks `if (isFreeform && isMovingCard)` and executes `return;` without calling `SelectCell()`.
4. Result: Clicking a card never selects it.

### 5.2 Z-Index Elevation Leapfrogging
1. `UpdateCardElevation()` treated hover equally with selection:
   ```csharp
   bool active = isCellSelected || isMenuOpen || isCellHovered;
   ```
2. When moving the cursor across overlapping cards, background cards elevated to `8900`, jumping over foreground cards and intercepting pointer events.

### 5.3 Disconnected Visual Pipeline
1. `MakeWindowMenu` / `PopulateShapeMenuItems` updated `geometry.Shape` in an in-memory dictionary.
2. It failed to call `ApplyWindowGeometry(card)` or `ApplyCellShape()`.
3. The shape updated in memory but never redrew on screen.

### 5.4 The Bare Canvas Viewport Toolbar Hijack
1. In freeform layouts (e.g. Scattered Prints), `_items[0]` is an empty placeholder cell filling `CellsGrid` across the entire viewport.
2. Any mouse movement over the background of the window registers as pointer input on Slot 0.
3. When `UpdateHudVisibility()` does not check `IsBareCanvas(slot)`, Slot 0 shows its `topControls` in the top right corner of the window.
4. Because Slot 0 is not a photo card, clicking its toolbar or context menu displays "Position 1 (This Image)" and attempts to swap Position 1 with floating cards.

### 5.5 The WinUI 3 Pointer Event Lifecycle & Capture State De-synchronization
1. A mouse device maintains a static `PointerId == 1` continuously across all clicks and moves.
2. If `movePointerId` is set to `1` on `PointerPressed` and not cleared on `PointerReleased`, every subsequent movement—even with no mouse button pressed—evaluates `e.Pointer.PointerId == movePointerId`.
3. Conversely, clearing `movePointerId` prematurely inside `PointerMoved` strips pointer ownership before `PointerReleased` executes, dropping the clean-click transition.
4. Drag states must strictly follow standard WinUI 3 state machine patterns: `isCaptured` initialized on threshold travel (> 4px), pointer released on `PointerReleased`, and pointer cleanup synchronized across `CaptureLost`.

---

## 6. Implementation Specifications

### 6.1 Click vs. Drag Disambiguation (`MultiViewControl.xaml.cs`)
In `cellGrid.PointerPressed`:
- Record `dragStartInRoot = e.GetCurrentPoint(RootGrid).Position`.
- Set `hasDraggedCell = false`.
- Set `movePointerId = e.Pointer.PointerId`.
- Capture pointer.

In `cellGrid.PointerMoved`:
- Only execute card movement if `isFreeform && movePointerId != 0 && e.Pointer.PointerId == movePointerId`.
- Only set `isMovingCard = true` when travel exceeds `4px`.
- Do **not** clear `movePointerId` in `PointerMoved`.

In `cellGrid.PointerReleased`:
- Ensure `movePointerId` check matches:
  ```csharp
  if (isFreeform && movePointerId != 0 && e.Pointer.PointerId == movePointerId)
  {
      movePointerId = 0;
      try { cellGrid.ReleasePointerCapture(e.Pointer); } catch { }
      if (isMovingCard)
      {
          isMovingCard = false;
          _isInteractingWithCard = false;
          HideSnapGuides();
          CaptureUndoPoint();
          SyncCanvasToSelectedScene(false);
          int slot = Slot(photo.Item);
          if (!IsBareCanvas(slot)) SelectCell(slot);
          return;
      }
      if (!hasDraggedCell)
      {
          int slot = Slot(photo.Item);
          if (IsBareCanvas(slot)) DeselectCell();
          else SelectCell(slot);
          return;
      }
  }
  ```

In `cellGrid.PointerCaptureLost` & `PointerCanceled`:
- Always reset `movePointerId = 0; dragPointerId = 0; isMovingCard = false; _isInteractingWithCard = false; HideSnapGuides();`.

### 6.2 Elevation Hierarchy & Bare Canvas Invariant (`MultiViewControl.xaml.cs`)
```csharp
void UpdateHudVisibility()
{
    int slot = Slot(photo.Item);
    if (IsBareCanvas(slot))
    {
        topControls.Opacity = 0.0;
        topControls.IsHitTestVisible = false;
        return;
    }
    bool show = isCellSelected || isMenuOpen || (_isHudVisible && isCellHovered);
    topControls.Opacity = show ? 1.0 : 0.0;
    topControls.IsHitTestVisible = show;
    topControls.Tag = isCellSelected;
}
```

```csharp
if (!isFreeform) return;
var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Container, slotGrid));
if (card == null) return;

if (isCellSelected || isMenuOpen)
{
    // Active card: guaranteed top of stack
    Canvas.SetZIndex(slotGrid, 9900);
    slotGrid.Translation = new Vector3(0, 0, 1200);
}
else if (isCellHovered && _selectedSlot < 0)
{
    // Hover preview ONLY when no card is actively selected
    Canvas.SetZIndex(slotGrid, 8500);
    slotGrid.Translation = new Vector3(0, 0, 600);
}
else
{
    // Natural resting layer
    Canvas.SetZIndex(slotGrid, card.ZIndex);
    slotGrid.Translation = new Vector3(0, 0, 256 + ((card.ZIndex - 5000) * 8));
}
```

### 6.3 Shaped Card Hit-Testing (`MultiViewControl.Advanced.cs`)
In `ApplyCellShape`:
- Rectangular cards: `photoGrid.Background = SolidColorBrush(1, 0, 0, 0)`, `hitArea.Visibility = Collapsed`.
- Shaped cards: `photoGrid.Background = null`. `hitArea` polygon is inserted at index 0 of `photoGrid.Children`, filled with `SolidColorBrush(1, 0, 0, 0)`, and points assigned from `cardOutline`.
- Transparent corners outside the polygon have no background brush, allowing clicks to pass through to underlying elements natively.

### 6.4 Shape Menu Visual Connection (`MultiViewControl.Advanced.cs`)
```csharp
void Change(Action<CellGeometryDto> change)
{
    change(GetGeometry(item));
    ShowEmptyState(Slot(item));
    var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Item, item));
    if (card != null)
    {
        ApplyWindowGeometry(card);
    }
    else
    {
        int slot = Slot(item);
        if (slot >= 0 && slot < _cellRoots.Length && _cellRoots[slot] is Grid g)
            ApplyCellShape(g, item, null);
    }
    CaptureUndoPoint();
    SyncCanvasToSelectedScene(showToast: false);
}
```

### 6.5 Visual Controls Layering & Persistence (`MultiViewControl.xaml.cs` & `Advanced.cs`)
- In `CreateCell`: Add `topControls` to `slotGrid.Children` **after** `cellBorder` so it sits above the photo surface.
- In `AttachWindowHandles`:
  - Wire `window.PointerMoved` to keep handles visible during active mouse movement.
  - Guard `hideHandles.Tick` so that handles do **not** hide when `_selectedSlot == Slot(card.Item)`.
  - Calculate `PointerAngle` relative to `FreeformCanvas` with NaN safety guards on `Canvas.GetLeft` and `Canvas.GetTop`.

---

## 7. Anti-Whack-A-Mole Rules of Engagement

1. **Contract-Driven Modifications**: No event handler may be edited in isolation. Any change to input handling must be verified against the entire 6-stage lifecycle.
2. **Ground-Truth Baseline Comparison**: Every change must be diffed against `6eefcf1` to verify that architectural invariants (such as `hitArea` polygon hit-testing and baseline Z-ordering) are preserved.
3. **Pre-Flight Blast-Radius Audit**: Before compiling, every change must be audited against the regression matrix:
   - Does stationary click select?
   - Does drag past 4px move without selecting?
   - Do toolbar buttons open flyouts without triggering card movement?
   - Does rotation turn smoothly around center?
   - Do transparent corners of shaped cards pass clicks through to background cards?
   - Does background card hover respect the active card's Z-lock?
   - Is bare canvas (Slot 0) strictly excluded from displaying cell hover toolbars?
4. **Physical On-Screen Verification**: Every build must be physically verified against all card types (Rectangles, Circles/Ovals, Hexagons, Slanted Panels) before reporting completion.
