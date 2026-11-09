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

### 3.2 Chronological Chronicle of the AI Sessions

| Session | Agent Type | Primary Action Taken | Fatal Flaw / Regression Introduced | Result |
| :--- | :--- | :--- | :--- | :--- |
| **Session 1** | Gemini 3.8 Flash | Added `slotGrid.PointerPressed` with pointer capture; refactored `MakeWindowMenu` (`7e031c9`, `3d5599c`) | Container-level capture intercepted pointer events intended for child buttons; buttons stopped receiving clicks. | Utter failure: Toolbar buttons dead, selection erratic. |
| **Session 2** | Gemini 3.8 Flash | Designed 6-stage architecture plan; added elevation tiers (`9900`/`8500`); injected polygon `hitArea` (`08b8575`, `27f9c6f`) | Omitted resetting `movePointerId = 0` on `PointerReleased`. WinUI mouse device retained `PointerId = 1`, causing mouse movement with NO buttons pressed to latch cards to cursor ("Ghost Drag"). | Utter failure: Uncontrollable movement, card stuck to mouse. |
| **Session 3** | Gemini 3.8 Flash | Checked `!prop.IsLeftButtonPressed` in `PointerMoved` to kill ghost drag; filtered Slot 0 context menu (`9522f44`) | `PointerMoved` cleared `movePointerId = 0` during the mouse-up transition before `PointerReleased` arrived. Clean clicks were swallowed; card selection died completely. Rogue Slot 0 toolbar returned to viewport top-right. | Utter failure: Zero card selection, rogue canvas toolbar returned. |
| **Attempt 4a** | Gemini 3.8 Flash | Baseline restoration: 4px threshold for click vs. drag gating, active elevation lock (`isCellSelected || isMenuOpen`), Shape menu link (`ApplyWindowGeometry`) | Slot 0 (bare canvas) lacked exclusion in `SelectCell` and `IsInsideAnyCell`. Clicking canvas selected Slot 0, wrapping the entire window in a 2px blue frame (`media_1794178978075_edce230e.jpg`). In `slotGrid.Children`, `cellBorder` was added after `selectionBorder`, hiding card selection under opaque photos. | Failure: Entire canvas grabbed focus; individual cards unselectable. |
| **Attempt 4b** | Gemini 3.8 Flash | Bare canvas exclusion in `SelectCell`, `IsInsideAnyCell`, `Tapped`, and `PointerReleased`; reordered `slotGrid.Children` so `selectionBorder` and `topControls` sit on top of `cellBorder` | WinUI 3 `Canvas.SetZIndex` does NOT dynamically reorder GPU composition visuals for existing children; selected card stayed pinned behind overlapping cards (`media_1794179894686_aeb72001.jpg`). Overlapping cards physically blocked `topControls` and `rotate`. Counter-rotation disconnected visual buttons from hit-test slots. | Failure: Only one image selectable; stays behind overlapping cards; toolbar buttons and rotation handle dead; other images unselectable. |
| **User + Claude Opus 5.5 Discovery** | Human User + Claude Opus 5.5 | Isolated execution boundary: Tested standalone freeform mode *without* a show and proved it works fine (`media_1794181603282_9fea9ea3.jpg`). Identified that the interaction breakdown only occurs *after* show playback runs, because slideshow layouts are architecturally divergent from native multi-image layouts. | Handed this exact breakthrough to Gemini 3.8 Flash when Claude ran out of credits. | **Major Architectural Breakthrough**: Pinpointed that the slideshow layout lifecycle diverges from native multi-image. |
| **Attempt 5** | Gemini 3.8 Flash | Ignored structural normalization; spent 47 minutes chasing visual-tree symptoms: dynamically shuffled `FreeformCanvas.Children` via `Remove`/`Add` on selection, attached `RestoreFreeformCanvasOrder()` to hover-exit, re-ordered resize handles, added handle timers. | Dynamic collection reshuffling hooked to hover-exit caused moving the cursor to immediately bury the active card under its neighbor (`media_1794187286767_71131818.jpg`). Micro-HUD buttons, rotation handles, and selection remained dead. | **Catastrophic Failure**: 47 minutes wasted; active cards jump behind neighbors on mouse move; completely broken. |

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


---

## 8. Gemini 3.8 Flash – Attempt 4: The Invariant-Driven Minimalist Restoration Approach

### 8.1 Deconstructing the Illusion: Why It Never Was "Straightforward"
When a regression hits fundamental user gestures ("moving cards" and "clicking menus"), human engineers and AI models alike reflexively assume:
> *"This is 101-level GUI behavior. Just fix something that used to work—nothing complex, not some obscure issue."*

In modern desktop frameworks like **WinUI 3 (Windows App SDK)**, this assumption is lethal because pointer events are governed by strict framework-level invariants:
1. **Device ID Continuity**: Mouse devices maintain `PointerId = 1` continuously across movements and clicks. Any state variable that tracks a drag by pointer ID without a symmetric release lifecycle converts regular mouse motion into a permanent "ghost drag".
2. **Gesture Suppression via Handled Events**: Eagerly capturing the pointer and flagging `e.Handled = true` on `PointerPressed` suppresses the XAML gesture recognizer, killing high-level `Tapped` and `DoubleTapped` routing.
3. **The Mouse-Up Micro-Event Race**: WinUI 3 delivers a final `PointerMoved` with `IsLeftButtonPressed == false` during the release transition. Clearing drag state in `PointerMoved` destroys the gesture state milliseconds before `PointerReleased` arrives, silently swallowing clean stationary clicks.
4. **Hit-Testing Topology & Z-Ordering**: Canvas panels do not auto-manage overlapping focus. Naive hover elevation (`isCellHovered => ZIndex = 8900`) triggers an infinite leapfrog oscillation across overlapping card boundaries.
5. **The Underlying Canvas Cell (Slot 0)**: In freeform Multi-View mode, Slot 0 spans the entire viewport. Unchecked hover listeners treat empty canvas movements as interactions with Slot 0, projecting rogue toolbars onto the window background.

---

### 8.2 Core Strategy: Explicit Restoration of the Original Working Architecture

> [!IMPORTANT]
> **THE STRATEGIC PRINCIPLE: REVERSION TO ORIGINAL ARCHITECTURE OVER CLEVER RE-ENGINEERING**
> Our strategy is explicitly structured around **reverting back to the original working architecture** specified in `docs/ARCHITECTURE.md` and proven in baseline commit `6eefcf1`. 
>
> The three prior failed AI attempts collapsed because they attempted to "solve" broken symptoms by writing *new, synthetic architecture* (state machines, custom boolean flags, tiered elevation schedulers) on top of a broken divergence. 
>
> The original architecture in `docs/ARCHITECTURE.md` was already complete, elegant, and sound:
> 1. **"Its layer among the floating cells is set from the menu; clicking a floating cell does not raise it, so a chosen order holds."** (`docs/ARCHITECTURE.md:117`)  
>    Commit `cf6cef6` broke this architectural contract by elevating cards on hover (`isCellHovered => ZIndex = 8900`), which instantly ruined user-defined collage layers and triggered Z-index leapfrog oscillations. Attempt 4 explicitly restores the original rule: cards remain strictly at their natural resting layer (`card.ZIndex`), and cursor hover never elevates them.
> 2. **"A freeform cell is hosted on FreeformCanvas and is not in the MosaicLayout tree... wherever no cell covers the canvas, the canvas shows through."** (`docs/ARCHITECTURE.md:89`)  
>    Slot 0 is the background viewport canvas behind `FreeformCanvas`, not an interactive photo cell. The breaking commits allowed Slot 0 to capture hover events, displaying a rogue toolbar. Attempt 4 restores this architectural boundary with the `IsBareCanvas` guard.
> 3. **"Every cell can take a window shape (WindowShapes, ApplyCellShape) and mirror or flip it."** (`docs/ARCHITECTURE.md:117`)  
>    The visual pipeline was meant to immediately reflect shape choices. Attempt 4 closes the single omitted call (`ApplyWindowGeometry(card)`) in `MakeWindowMenu`.
> 4. **"A dragged floating cell snaps to the canvas, other floating cells and showing grid cells (FreeformSnap); Alt turns that off."** (`docs/ARCHITECTURE.md:117`)  
>    Attempt 4 keeps the existing snapping and bounds calculation engine 100% intact, merely gating movement behind a standard 4px threshold so stationary clicks select the card without initiating a drag or disrupting chosen layer order.

---

### 8.3 Why Sections 5 & 6 Retained AI Failure Relics
Sections 5 and 6 of this document were drafted during the failed AI sessions. While they correctly documented WinUI mechanics, they still prescribed synthetic state variables (`_isInteractingWithCard`, `movePointerId`, tiered Z-index locks `9900/8500`). This created the exact cognitive trap warned about in Section 3.1: **attempting to solve an architectural divergence by layering more synthetic code over broken premises.**

The "Attempt 4" approach rejects synthetic state machines entirely. Instead, it starts from the proven, clean `6eefcf1` baseline and enforces **4 Surgical Minimal Invariants** (< 35 lines of code total).

---

### 8.4 The 4 Surgical Touches of the Minimalist Reconstruction

```
                                    The Clean Baseline (6eefcf1)
                                                 │
          ┌──────────────────────────┬───────────┴──────────────┬──────────────────────────┐
          ▼                          ▼                          ▼                          ▼
 1. Shape Menu Visual Link   2. Click vs Drag Gate    3. Bare Canvas Guard      4. Stable Elevation Lock
 (MultiViewControl.Advanced) (MultiViewControl.xaml)  (MultiViewControl.xaml)   (MultiViewControl.xaml)
 ~12 lines: calls            ~14 lines: 4px travel    4 lines: suppresses       1 line: active = selected
 ApplyWindowGeometry(card)   gate dispatches          topControls on Slot 0     || isMenuOpen; hover
 on shape menu selection     SelectCell on click      background canvas         does NOT leapfrog Z-index
```

#### Touch 1: Connect Shape Menu to the Visual Pipeline (`MultiViewControl.Advanced.cs`)
In `6eefcf1`, the only defect was that `MakeWindowMenu`'s `Change` action updated the in-memory geometry DTO without calling the layout renderer. Connecting it to `ApplyWindowGeometry(card)` (for floating cards) and `ApplyCellShape(...)` (for grid cells) completes the loop:
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

#### Touch 2: 4px Travel Gating for Click vs. Drag Disambiguation (`MultiViewControl.xaml.cs`)
- **Rule**: Never assume a press is a drag. A press is just a candidate drag.
- **In `PointerPressed`**: Record `dragStartInRoot = e.GetCurrentPoint(RootGrid).Position;` and `movePointerId = e.Pointer.PointerId;`, but leave `isMovingCard = false`.
- **In `PointerMoved`**: Only set `isMovingCard = true` once travel exceeds 4px:
  ```csharp
  if (!isMovingCard && (Math.Abs(cur.X - dragStartInRoot.X) > 4 || Math.Abs(cur.Y - dragStartInRoot.Y) > 4))
  {
      isMovingCard = true;
      hasDraggedCell = true;
  }
  if (!isMovingCard) return;
  ```
- **In `PointerReleased`**: If the pointer moved $\le 4	ext{px}$, dispatch `SelectCell(slot)`! If it moved $> 4	ext{px}$, finalize the card drag:
  ```csharp
  if (isFreeform && movePointerId != 0 && e.Pointer.PointerId == movePointerId)
  {
      bool wasMoving = isMovingCard;
      isMovingCard = false;
      movePointerId = 0;
      HideSnapGuides();
      try { cellGrid.ReleasePointerCapture(e.Pointer); } catch { }
      CaptureUndoPoint();
      int slot = Slot(photo.Item);
      if (!wasMoving && !hasDraggedCell)
      {
          if (IsBareCanvas(slot)) DeselectCell();
          else SelectCell(slot);
      }
      e.Handled = true;
      return;
  }
  ```
- **In `PointerCanceled` & `PointerCaptureLost`**: Always reset `isMovingCard = false; movePointerId = 0; HideSnapGuides();`. Never clear pointer IDs in `PointerMoved`.

#### Touch 3: Bare Canvas (Slot 0) Total Suppression (`MultiViewControl.xaml.cs`)
- **Rule**: Slot 0 is the background viewport canvas; it must never display cell hover toolbars.
- **In `UpdateHudVisibility()`**:
  ```csharp
  int slot = Slot(photo.Item);
  if (IsBareCanvas(slot))
  {
      topControls.Opacity = 0.0;
      topControls.IsHitTestVisible = false;
      return;
  }
  ```

#### Touch 4: Stable Elevation Order (Eliminate Z-Index Leapfrogging) (`MultiViewControl.xaml.cs`)
- **Rule**: Cursor hover must reveal controls, but never elevate a card above overlapping neighbors.
- **In `UpdateCardElevation()`**:
  ```csharp
  // Active card: guaranteed top of stack. Hover leaves card in its natural resting order.
  bool active = isCellSelected || isMenuOpen;
  if (active)
  {
      Canvas.SetZIndex(slotGrid, 8900);
      slotGrid.Translation = new Vector3(0, 0, 800);
  }
  else
  {
      Canvas.SetZIndex(slotGrid, card.ZIndex);
      slotGrid.Translation = new Vector3(0, 0, 256 + ((card.ZIndex - 5000) * 8));
  }
  ```

---

### 8.5 The Complete, Minimal Diff Specification Against `6eefcf1`

Total delta across the entire repository is under 35 lines of code across two files:

```diff
--- a/MultiViewControl.xaml.cs
+++ b/MultiViewControl.xaml.cs
@@ -1608,3 +1608,4 @@
-                        isMovingCard = true;
+                        isMovingCard = false;
+                        hasDraggedCell = false;
                         movePointerId = e.Pointer.PointerId;
@@ -1637,3 +1638,9 @@
-                if (isFreeform && isMovingCard && e.Pointer.PointerId == movePointerId)
+                if (isFreeform && movePointerId != 0 && e.Pointer.PointerId == movePointerId)
                 {
                     Point cur = e.GetCurrentPoint(RootGrid).Position;
+                    if (!isMovingCard && (Math.Abs(cur.X - dragStartInRoot.X) > 4 || Math.Abs(cur.Y - dragStartInRoot.Y) > 4))
+                    {
+                        isMovingCard = true;
+                        hasDraggedCell = true;
+                    }
+                    if (!isMovingCard) return;
@@ -1683,6 +1690,14 @@
-                if (isFreeform && isMovingCard && e.Pointer.PointerId == movePointerId)
+                if (isFreeform && movePointerId != 0 && e.Pointer.PointerId == movePointerId)
                 {
+                    bool wasMoving = isMovingCard;
                     isMovingCard = false;
+                    movePointerId = 0;
                     HideSnapGuides();
                     try { cellGrid.ReleasePointerCapture(e.Pointer); } catch { }
                     CaptureUndoPoint();
+                    int slot = Slot(photo.Item);
+                    if (!wasMoving && !hasDraggedCell)
+                    {
+                        if (IsBareCanvas(slot)) DeselectCell();
+                        else SelectCell(slot);
+                    }
                     e.Handled = true;
                     return;
                 }
@@ -1711,4 +1726,13 @@
-                if (isMovingCard)
+                if (isMovingCard || movePointerId != 0)
                 {
                     isMovingCard = false;
+                    movePointerId = 0;
                     HideSnapGuides();
+                    try { cellGrid.ReleasePointerCapture(e.Pointer); } catch { }
+                    CaptureUndoPoint();
+                }
+            };
+            cellGrid.PointerCaptureLost += (s, e) =>
+            {
+                if (isMovingCard || movePointerId != 0)
+                {
+                    isMovingCard = false;
+                    movePointerId = 0;
+                    HideSnapGuides();
+                    CaptureUndoPoint();
                 }
@@ -1824,3 +1848,3 @@
-                bool active = isCellSelected || isMenuOpen || isCellHovered;
+                bool active = isCellSelected || isMenuOpen;
@@ -1836,4 +1860,10 @@
             void UpdateHudVisibility()
             {
+                int slot = Slot(photo.Item);
+                if (IsBareCanvas(slot))
+                {
+                    topControls.Opacity = 0.0;
+                    topControls.IsHitTestVisible = false;
+                    return;
+                }
                 bool show = isCellSelected || isMenuOpen || (_isHudVisible && isCellHovered);
```

```diff
--- a/MultiViewControl.Advanced.cs
+++ b/MultiViewControl.Advanced.cs
@@ -99,4 +99,14 @@
             void Change(Action<CellGeometryDto> change)
             {
                 change(GetGeometry(item));
                 ShowEmptyState(Slot(item));
+                var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Item, item));
+                if (card != null)
+                {
+                    ApplyWindowGeometry(card);
+                }
+                else
+                {
+                    int slot = Slot(item);
+                    if (slot >= 0 && slot < _cellRoots.Length && _cellRoots[slot] is Grid g)
+                        ApplyCellShape(g, item, null);
+                }
+                CaptureUndoPoint();
+                SyncCanvasToSelectedScene(showToast: false);
             }
```

---

### 8.6 The Physical Verification Checklist (Working Agreement Point 18)

Before declaring any fix for this pipeline resolved, the following 8 physical gestures MUST be manually verified on the running application:

1. [ ] **Stationary Click Selection**: Click on a rectangular card without moving mouse. Blue accent border appears; `topControls` menu displays.
2. [ ] **Card Dragging**: Click and drag a card past 4px. Card moves smoothly under cursor, snap guides display, and card drops cleanly at release position without snapping back.
3. [ ] **Ghost Drag Immunity**: Move the mouse across cards and canvas with *no mouse buttons pressed*. No card follows the cursor.
4. [ ] **Toolbar Button Clickability**: Click the `[ 🎨 ]` button on the `topControls` pill. The Shape flyout menu opens immediately.
5. [ ] **Shape Selection Visual Update**: Pick "Hexagon" or "Circle" from the shape menu. The photo card immediately re-shapes on screen.
6. [ ] **Shaped Card Pass-Through Hit-Testing**: Place a Circle card partially overlapping a Rectangle card. Click in the transparent corner outside the circle. The Rectangle card behind it receives the click.
7. [ ] **Z-Index Stability (No Leapfrogging)**: Move the cursor slowly back and forth across two overlapping cards. Cards remain in their stable resting Z-stack; background cards do not bounce to the top.
8. [ ] **Bare Canvas Immunity**: Click and hover over the empty canvas background. Selection clears (`DeselectCell`); NO rogue toolbar appears in the top-right corner of the window.### 8.5 The Complete, Minimal Diff Specification Against 6eefcf1

Total delta across the entire repository is minimal, surgical, and structured strictly around the original baseline:

`diff
--- a/MultiViewControl.xaml.cs
+++ b/MultiViewControl.xaml.cs
@@ -272,8 +272,12 @@
         private bool IsInsideAnyCell(object? source)
         {
             for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
             {
-                if (node is FrameworkElement fe && fe.Tag is CellParts)
+                if (node is FrameworkElement fe && fe.Tag is CellParts parts)
+                {
+                    int slot = Slot(parts.Bound.Item);
+                    if (IsBareCanvas(slot)) return false;
                     return true;
+                }
             }
             return false;
         }
@@ -1227,6 +1231,11 @@
         public void SelectCell(int slot)
         {
+            if (slot >= 0 && IsBareCanvas(slot))
+            {
+                DeselectCell();
+                return;
+            }
             if (_selectedSlot == slot) return;
             if (_selectedSlot >= 0)
             {
@@ -1608,3 +1617,4 @@
-                        isMovingCard = true;
+                        isMovingCard = false;
+                        hasDraggedCell = false;
                         movePointerId = e.Pointer.PointerId;
@@ -1637,3 +1647,9 @@
-                if (isFreeform && isMovingCard && e.Pointer.PointerId == movePointerId)
+                if (isFreeform && movePointerId != 0 && e.Pointer.PointerId == movePointerId)
                 {
                     Point cur = e.GetCurrentPoint(RootGrid).Position;
+                    if (!isMovingCard && (Math.Abs(cur.X - dragStartInRoot.X) > 4 || Math.Abs(cur.Y - dragStartInRoot.Y) > 4))
+                    {
+                        isMovingCard = true;
+                        hasDraggedCell = true;
+                    }
+                    if (!isMovingCard) return;
@@ -1683,6 +1699,14 @@
-                if (isFreeform && isMovingCard && e.Pointer.PointerId == movePointerId)
+                if (isFreeform && movePointerId != 0 && e.Pointer.PointerId == movePointerId)
                 {
+                    bool wasMoving = isMovingCard;
                     isMovingCard = false;
+                    movePointerId = 0;
                     HideSnapGuides();
                     try { cellGrid.ReleasePointerCapture(e.Pointer); } catch { }
                     CaptureUndoPoint();
+                    int slot = Slot(photo.Item);
+                    if (!wasMoving && !hasDraggedCell)
+                    {
+                        if (IsBareCanvas(slot)) DeselectCell();
+                        else SelectCell(slot);
+                    }
                     e.Handled = true;
                     return;
                 }
@@ -1711,4 +1735,13 @@
-                if (isMovingCard)
+                if (isMovingCard || movePointerId != 0)
                 {
                     isMovingCard = false;
+                    movePointerId = 0;
                     HideSnapGuides();
+                    try { cellGrid.ReleasePointerCapture(e.Pointer); } catch { }
+                    CaptureUndoPoint();
+                }
+            };
+            cellGrid.PointerCaptureLost += (s, e) =>
+            {
+                if (isMovingCard || movePointerId != 0)
+                {
+                    isMovingCard = false;
+                    movePointerId = 0;
+                    HideSnapGuides();
+                    CaptureUndoPoint();
                 }
@@ -1824,3 +1857,3 @@
-                bool active = isCellSelected || isMenuOpen || isCellHovered;
+                bool active = isCellSelected || isMenuOpen;
@@ -1836,4 +1869,10 @@
             void UpdateHudVisibility()
             {
+                int slot = Slot(photo.Item);
+                if (IsBareCanvas(slot))
+                {
+                    topControls.Opacity = 0.0;
+                    topControls.IsHitTestVisible = false;
+                    return;
+                }
                 bool show = isCellSelected || isMenuOpen || (_isHudVisible && isCellHovered);
@@ -1826,1 +1865,0 @@
-            slotGrid.Children.Add(selectionBorder);
@@ -2076,1 +2114,0 @@
-            slotGrid.Children.Add(topControls);
@@ -2109,1 +2146,0 @@
-            slotGrid.Children.Add(positionBadge);
@@ -2184,2 +2220,5 @@
             slotGrid.Children.Add(gutterBorder);
             slotGrid.Children.Add(cellBorder);
+            slotGrid.Children.Add(selectionBorder);
+            slotGrid.Children.Add(positionBadge);
+            slotGrid.Children.Add(topControls);
`

`diff
--- a/MultiViewControl.Advanced.cs
+++ b/MultiViewControl.Advanced.cs
@@ -99,4 +99,14 @@
             void Change(Action<CellGeometryDto> change)
             {
                 change(GetGeometry(item));
                 ShowEmptyState(Slot(item));
+                var card = _freeformCards.FirstOrDefault(c => ReferenceEquals(c.Item, item));
+                if (card != null)
+                {
+                    ApplyWindowGeometry(card);
+                }
+                else
+                {
+                    int slot = Slot(item);
+                    if (slot >= 0 && slot < _cellRoots.Length && _cellRoots[slot] is Grid g)
+                        ApplyCellShape(g, item, null);
+                }
+                CaptureUndoPoint();
+                SyncCanvasToSelectedScene(showToast: false);
             }
`

---

### 8.6 The Physical Verification Checklist (Working Agreement Point 18)

Before declaring any fix for this pipeline resolved, the following 8 physical gestures MUST be manually verified on the running application:

1. [ ] **Stationary Click Selection**: Click on a rectangular card without moving mouse. Blue accent border appears; `topControls` menu displays.
2. [ ] **Card Dragging**: Click and drag a card past 4px. Card moves smoothly under cursor, snap guides display, and card drops cleanly at release position without snapping back.
3. [ ] **Ghost Drag Immunity**: Move the mouse across cards and canvas with *no mouse buttons pressed*. No card follows the cursor.
4. [ ] **Toolbar Button Clickability**: Click the `[ 🎨 ]` button on the `topControls` pill. The Shape flyout menu opens immediately.
5. [ ] **Shape Selection Visual Update**: Pick "Hexagon" or "Circle" from the shape menu. The photo card immediately re-shapes on screen.
6. [ ] **Shaped Card Pass-Through Hit-Testing**: Place a Circle card partially overlapping a Rectangle card. Click in the transparent corner outside the circle. The Rectangle card behind it receives the click.
7. [ ] **Z-Index Stability (No Leapfrogging)**: Move the cursor slowly back and forth across two overlapping cards. Cards remain in their stable resting Z-stack; background cards do not bounce to the top.
8. [ ] **Bare Canvas Immunity**: Click and hover over the empty canvas background. Selection clears (`DeselectCell`); NO rogue toolbar appears in the top-right corner of the window.

---

### 8.7 Attempt 4a Post-Mortem: Bare Canvas Focus Theft & XAML Collection Inversion

#### The Physical Gesture & Failure Mode
Attempt 4a was built, packaged, verified via `publish.bat`, and launched on Windows 11.
When testing freeform mode (e.g. Scattered Prints), the user immediately observed:
> *"Focus (light blue frame) is across the entire canvas rather than any image. Therefore: Individual images cannot be selected. Immediate fail."* (Screenshot: `media_1794178978075_edce230e.jpg`).

#### Root Cause 1: Bare Canvas Focus Theft (Slot 0)
1. In freeform layouts (e.g., Scattered Prints), Slot 0 is the underlying 1×1 `CellsGrid` background element that spans the entire window viewport behind all floating cards.
2. Clicking on any empty portion of the background registered as a pointer press on Slot 0.
3. Because Slot 0 was not a freeform card, its non-freeform `PointerReleased` handler executed:
   ```csharp
   if (!hasDraggedCell && !isMovingCard)
   {
       SelectCell(Slot(photo.Item)); // Slot = 0!
   }
   ```
4. `SelectCell(0)` executed without checking `IsBareCanvas(slot)`, setting Slot 0's `selectionBorder.Visibility = Visible`. Because Slot 0 spans the full window, its 2px Fluent blue accent border framed the entire application window.
5. `RootGrid` has a global pointer listener (`CloseCountsCardOnPressOutside`) that checks `!IsInsideAnyCell(e.OriginalSource)`. However, `IsInsideAnyCell` checked `fe.Tag is CellParts`. Because Slot 0's `slotGrid.Tag` was also `CellParts`, it returned `true`, preventing deselection on canvas background clicks.

#### Root Cause 2: Visual Z-Ordering Inversion Inside `slotGrid`
1. Inside `CreateCell`, children of `slotGrid` (a XAML `Grid`) were added in the following order:
   ```csharp
   slotGrid.Children.Add(selectionBorder);
   slotGrid.Children.Add(topControls);
   slotGrid.Children.Add(positionBadge);
   slotGrid.Children.Add(gutterBorder);
   slotGrid.Children.Add(cellBorder); // Added LAST!
   ```
2. In WinUI 3 (Windows App SDK), a XAML `Grid` does **not** evaluate `Canvas.ZIndex` on its children. Children of a `Grid` are rendered strictly in declaration / collection order.
3. Because `cellBorder` (which contains `fullImg`, the opaque photo) was added last, it was drawn **on top of** `selectionBorder` and `topControls`.
4. On all photo cards, the blue selection border was rendered underneath the opaque photo and was completely invisible. Slot 0 was the only slot that showed a selection border because Slot 0 was empty and transparent.

---

### 8.8 Attempt 4b Post-Mortem: The WinUI 3 Dynamic Z-Index Limitation & Spatial Overlap Deadlock

#### The Physical Gesture & Failure Mode
Attempt 4b introduced bare-canvas guards in `SelectCell`, `IsInsideAnyCell`, `Tapped`, and `PointerReleased`, and reordered `slotGrid.Children` so `cellBorder` is rendered first, with `selectionBorder`, `positionBadge`, and `topControls` on top.
When deployed and tested live on Windows 11, the user reported:
> *"I can select ONE image and half the time it stays behind the other images. Anyway, the visual menu doesn't work, none of the controls work including the rotation control. And none of the other images can be selected."* (Screenshots: `media_1794179894686_aeb72001.jpg` and `media_1794179936927_fe520548.jpg`).

#### The 4 Interacting Root Causes

1. **Failure Mode 1: Selected card remains behind overlapping cards ("half the time it stays behind")**:
   - In `FreeformCanvas` (a XAML `Canvas`), floating cards are added to `FreeformCanvas.Children` sequentially when spawned: Card 1 (Snow town) is Index 0; Card 2 (Sunset) is Index 1; Card 3 (Winter forest) is Index 2.
   - When Card 1 was selected, `UpdateCardElevation` executed:
     ```csharp
     Canvas.SetZIndex(slotGrid, 8900);
     slotGrid.Translation = new Vector3(0, 0, 800);
     ```
   - **The WinUI 3 Limitation**: In WinUI 3 (Windows App SDK), calling `Canvas.SetZIndex` dynamically on an element *already added to a Canvas* does **not** dynamically reorder GPU composition visuals without layout re-evaluation. The painter algorithm continues to draw elements in `Children` collection order: child index 1 (Sunset) and child index 2 (Winter forest) are drawn on top of child index 0 (Snow town).
   - In screenshot `media_1794179894686_aeb72001.jpg`, the 2px blue selection rectangle of Card 1 is visibly sliced in half by Card 2 and Card 3.
   - The card only appears "on top" if the user happened to select the last card added to the `Children` collection, leading to the user's observation that *"half the time it stays behind"*.

2. **Failure Mode 2: The visual menu (`topControls`) is dead**:
   - **Physical Occlusion**: Card 1 is tilted clockwise, pushing its top-right corner (where `topControls` resides) directly underneath the overlapping Card 2. Because Card 2 is on top of Card 1 in the XAML tree, Card 2's transparent and photo hit-test areas physically sit in front of `topControls`. Clicks aimed at `topControls` hit Card 2 instead.
   - **Resize Handle Collision**: In `CreateCell`, `AttachWindowResizeHandles` adds 8 resize grips *after* `topControls`. `WindowResizeDirection.Top` (a stretch strip across the entire top edge, `Height = 8`) and `WindowResizeDirection.TopRight` (a 22×22px corner grip) sit right in that same top boundary and capture the pointer on press (`handle.CapturePointer(); e.Handled = true;`).
   - **Counter-Rotation Transform Offset**: The window is rotated by `+angle`, and `topControls` applies `RenderTransform = RotateTransform { Angle = -angle }` to keep the pill horizontal. In WinUI 3, `RenderTransform` rotates visual pixels on the GPU, but layout hit-testing uses unrotated layout bounds, disconnecting visual buttons from their hit targets.

3. **Failure Mode 3: The rotation control (`rotate`) is dead**:
   - `rotate` is centered at the top edge (`Margin = (0, 12, 0, 0)`). Like `topControls`, it is occluded by overlapping cards with higher child indices.
   - `rotate.IsHitTestVisible` is gated on `ShowHandles(true)` (which only fires on `window.PointerEntered`). If an overlapping card is hovering above it, `window.PointerEntered` never reaches the lower card.

4. **Failure Mode 4: None of the other images can be selected**:
   - In `cellGrid.PointerPressed`, when `isFreeform == true`, `cellGrid.CapturePointer(e.Pointer)` is invoked.
   - In screenshot `media_1794179936927_fe520548.jpg`, Card 1's unrotated spatial layout bounding box extends horizontally across the canvas behind the other cards.
   - The captured or top-most element claims all mouse-down events across that shared spatial rectangle, preventing `PointerReleased` from reaching the `SelectCell()` dispatcher on other cards.

---

### 8.9 Comprehensive Lessons Learned Across Attempts 4a & 4b

1. **WinUI 3 `Canvas.SetZIndex` is Not a Dynamic Z-Reorderer**:
   - Calling `Canvas.SetZIndex(element, 8900)` at runtime on elements already placed in a `Canvas` does not alter visual compositor draw order or hit-test layering in WinUI 3. Visual order is dictated by the `Canvas.Children` collection order.
2. **The Fundamental Architectural Dilemma: Collage Layering vs. Interactive Affordance**:
   - The architectural premise from `ARCHITECTURE.md:117` (*"clicking a floating cell does not raise it, so a chosen order holds"*) is in direct contradiction with interactive controls (toolbars, handles, buttons) when cards overlap.
   - If Card A is visually and physically under Card B, Card B intercepts all pointer input in the overlap region. To make Card A's buttons interactive, Card A must physically move to the top of the input/visual stack.
3. **Child Ordering Inside Container Controls Must Include Resize Handles**:
   - Moving `topControls` after `cellBorder` was insufficient because `AttachWindowResizeHandles` adds full-edge hit-testable borders (`handle.Height = 8; handle.HorizontalAlignment = Stretch`) and corner handles (22x22) *after* `topControls`.
4. **Counter-Rotation Hit-Testing Disconnect in WinUI 3**:
   - Applying a `-angle` `RenderTransform` to `topControls` to counteract `+angle` on `window` produces visual alignment on screen, but WinUI 3's hit-testing tree does not composite inverse transforms cleanly for child input routing.

---

### 8.10 The Claude Opus 5.5 + User Breakthrough vs. The Gemini 3.8 Flash Attempt 5 Disaster

#### 8.10.1 The Breakthrough: Isolating the Exact Execution Boundary
Following the failures of Attempts 1–4, Claude Opus 5.5 and the user stepped back from symptom-whacking and posed the critical diagnostic question:
> *"Did freeform multi-image card management work in standalone mode, or only within a slideshow?"*

The user verified this immediately on the live application:
- **Standalone Multi-View Mode**: Opening a freeform layout or popping out a card **before running any show** worked cleanly (media_1794181603282_9fea9ea3.jpg). Stationary click selected the card, the micro-HUD buttons opened flyouts, dragging moved the card, and resizing functioned.
- **Show Playback State**: The interaction pipeline **completely broke only after triggering a show** (e.g. *Circles*, *Scattered Prints*) and then trying to manipulate the freeform cards used by that show (media_1794182609202_cdf032e2.jpg).

**The Core Discovery**: Multi-image freeform card management itself was not fundamentally broken. Rather, **slideshow layouts are not really the original multi-image layouts**; they visually simulate the layout on screen but diverge architecturally, leaving the canvas in an incompatible, semi-functional state once playback stops or pauses.

---

#### 8.10.2 The Attempt 5 Disaster: Gemini 3.8 Flash’s 47-Minute Visual-Tree Reshuffle Fiasco
When Claude Opus 5.5 ran out of credits, Gemini 3.8 Flash was handed this exact finding. Instead of investigating *why* slideshow layouts diverge from native multi-image layouts, Gemini 3.8 Flash spent **47 minutes** building an elaborate, destructive visual-tree hack:

1. **The Flawed Premise**:
   Gemini 3.8 Flash assumed that multi-image slideshow scenes broke simply because multiple cards overlapped in coordinate space, and hypothesized that WinUI 3's Canvas.SetZIndex failed to update GPU composition painter order.
2. **The Destructive Code Added**:
   - In UpdateCardElevation(), it forcibly modified FreeformCanvas.Children at runtime:
     `csharp
     FreeformCanvas.Children.Remove(slotGrid);
     FreeformCanvas.Children.Add(slotGrid);
     `
   - Added RestoreFreeformCanvasOrder() to re-sort all cards back to natural c.ZIndex order on hover-exit and deselection:
     `csharp
     var ordered = _freeformCards.OrderBy(c => c.ZIndex).ToList();
     // Re-order FreeformCanvas.Children collection
     `
   - Re-ordered children in slotGrid and tweaked handle hover timers.
3. **The Physical Failure Mode (media_1794187250408_d2fb8b8e.jpg vs. media_1794187286767_71131818.jpg)**:
   - The user launched the build on Windows 11 and clicked the center fiber-optic circle in a 5-circle scene (media_1794187250408_d2fb8b8e.jpg).
   - As soon as the mouse moved or pointer focus shifted, PointerExited fired on the card.
   - RestoreFreeformCanvasOrder() immediately executed, sorting cards strictly by natural ZIndex.
   - Because the fiber-optic circle had a lower natural Z-index than the adjacent mossy tree circle, RestoreFreeformCanvasOrder() forcibly re-sorted FreeformCanvas.Children, **instantly burying the selected card behind the mossy tree trunk** (media_1794187286767_71131818.jpg).
   - Micro-HUD buttons remained completely dead, rotation handles did not turn, and the canvas was rendered totally unusable.

The user's direct assessment:
> *"seems completely broken exactly as before. did you spend 40+ minutes doing pretty much nothing?"*

---

#### 8.10.3 Architectural Deep Dive: Why Slideshow Layouts Diverge from Native Multi-Image Layouts

An exhaustive code audit reveals the exact reasons why a slideshow layout is not a true Multi-Image layout:

`
[Native Multi-Image Mode]                          [Show Playback Scene]
User creates / pops out cards                       Generated via LayoutComposer / ShowDirector
        │                                                   │
        ▼                                                   ▼
• Clear 1:1 slot mapping                             • Dummy Slot 0 bare canvas (Slot = 0, Path = "")
• Each card has a real SourceSlot (0..N)             • Freeform cards generated with SourceSlot = -1
• Placed directly on FreeformCanvas                  • Cards clone off Slot 0 dummy fallback
• Pure XAML Transforms (RenderTransform)             • Layered under Compositor animations
• Clean resting z-index stack                        • Drifts driven by Composition TransformMatrix
• No camera/playback state machine                   • Runs under _isStoryboardPlayback & CameraDirector
`

1. **Dummy Slot 0 Bare Canvas & SourceSlot = -1 Mapping Corruption**:
   - In native Multi-Image mode, every card has a distinct SourceSlot (0..N) mapping directly to an item in _items.
   - In LayoutComposer.ToLayoutFile(), scenes with only floating cards (such as *Circles* or *Scattered Prints*) insert a dummy empty placeholder at Slot 0:
     `csharp
     // LayoutComposer.cs:115
     if (grid.Count == 0) file.Photos.Add(new PhotoSlotDto { Slot = 0, Path = string.Empty });
     `
     And assign every floating card SourceSlot = -1:
     `csharp
     // LayoutComposer.cs:128
     file.Freeform.Add(new FreeformCellDto { SourceSlot = -1, ... });
     `
   - When ApplyLayoutFile restores this scene:
     `csharp
     // MultiViewExport.cs:1929-1933
     int grid = GridCellCount;
     var sourceItem = dto.SourceSlot >= 0 && dto.SourceSlot < grid
         ? _items[dto.SourceSlot]
         : _items.FirstOrDefault(); // FALLS BACK TO DUMMY BARE CANVAS SLOT 0!
     if (sourceItem != null) SpawnFreeformCell(sourceItem, dto);
     `
   - **The Corruption**: Every single card in the slideshow scene is instantiated by cloning off the empty Slot 0 bare canvas item! This pollutes the slot registry and causes canvas focus handlers to confuse floating cards with the full-window background cell.

2. **Persistent Compositor Transforms vs. XAML Layout**:
   - In native Multi-Image, cards are positioned using Canvas.Left, Canvas.Top, and standard XAML RenderTransform.
   - During show playback, StartFloats() and RunCamera() attach Windows.UI.Composition animations (TransformMatrix, LayerExpression) directly to FreeformCanvas and individual card visuals (ElementCompositionPreview.GetElementVisual(window).TransformMatrix).
   - When a show pauses or stops, the camera engine leaves these composition matrix offsets applied. GPU-rendered visual pixels diverge from WinUI's XAML hit-test topology: the visual image is drawn at one coordinate, but pointer hit-testing expects it somewhere else. Micro-HUD buttons miss clicks, and rotation handles become disconnected.

3. **Show Director State-Locking**:
   - While a show is running or paused, internal state flags like _isStoryboardPlayback and _selectedShowScene actively intercept canvas operations.
   - For example, SyncCanvasToSelectedScene silently exits (if (_isStoryboardPlayback && !force) return;), preventing any on-canvas user edits from syncing back to the layout model.

---

#### 8.10.4 Lessons Learned & The Non-Negotiable Directives

1. **NEVER Manipulate FreeformCanvas.Children Collection Order at Runtime**:
   - In WinUI 3, removing and re-adding elements or sorting Children during hover or selection events causes severe visual thrashing, resets internal pointer capture, and buries overlapping elements under each other.
   - The original architectural invariant from ARCHITECTURE.md:117 (*"clicking a floating cell does not raise it, so a chosen order holds"*) must be strictly honored. Elevation must be achieved through stable elevation properties without re-sorting the visual collection.

2. **Normalize Slideshow Layouts into Canonical Multi-Image Layouts on Stop/Pause**:
   - The correct engineering solution is **structural normalization**: when a show pauses or stops, the on-screen scene must be converted into a genuine, clean Multi-Image layout.
   - Completely eliminate the SourceSlot = -1 dummy fallback by assigning unique, valid slots and ImageItem instances for every card.
   - Detach all compositor camera animations and clear composition TransformMatrix residue so pure XAML layout and hit-testing regain 100% authority.
   - Release storyboard playback state locks so the layout behaves identically to a freshly opened, user-managed multi-image collage.

---

### 8.11 Architectural Root Cause: Grid Cell Coupling and The Failed Decoupling Attempt (Commit 712d84b)

#### 8.11.1 What Is Really Going On: The Umbilical Cord Between Freeform Cards and Grid Cells

Through rigorous user testing and architectural tracing, the fundamental cause of freeform card corruption has been pinpointed: **Freeform cards in Modern Image Viewer are not architected as autonomous entities; they are treated as "displaced grid cells" tethered by an umbilical cord (`SourceSlot`).**

1. **The Shared Identity Trap**:
   - When a user pops a grid cell into freeform (`SpawnFreeformCell`), the code instantiates a new floating card but explicitly sets `card.SourceSlot = sourceSlot` (the index of the vacated grid cell).
   - The vacated grid slot is left on the grid as an empty cell (`EmptyCell(sourceSlot)`), waiting for a replacement photo.
   - At this moment, the freeform card appears functional because its original slot is sitting empty and undisturbed.

2. **The Corruption Mechanism (Grid Slot Re-Fill)**:
   - When the user subsequently places a new photo into that vacated grid cell (via Image Pool, Drawer, or File Picker), the grid slot is re-occupied.
   - Crucially, the system's layout serialization (`BuildLayoutFile`), state reconstruction (`ApplyLayoutFile`), and dock target resolution (`DockTarget`) still hold references to `card.SourceSlot`.
   - When the layout synchronizes or updates, the data model collides: the floating card is still registered as tied to slot $K$, but slot $K$ now holds an entirely different photo in the underlying grid.
   - This state collision corrupts the card:
     - `_cellRoots` index mappings and `_items` index lookups diverge.
     - WinUI 3 pointer hit-testing and event bubbling fall through the floating card to the underlying grid cell.
     - The right-click context menu either targets the underlying grid cell or fails to bind to the floating card.
     - The floating card becomes unselectable, unmovable, or corrupted.

3. **100% Reproducible Test Case Proven by User**:
   - **Step 1**: Grid of 7 photos. Position 5 is converted to freeform (becomes floating card Position 8).
   - **Step 2**: While Position 5 is empty, Position 8 works completely: selectable, movable, rotatable, right-click menu opens with full options.
   - **Step 3**: Position 5 is filled with a new photo from the drawer/pool.
   - **Step 4**: Floating Position 8 immediately corrupts: clicking or right-clicking on Position 8 hits the grid underneath (Position 4 / Position 5), and the freeform card can no longer be properly selected or manipulated.

---

#### 8.11.2 Post-Mortem of the Failed Decoupling Attempt (Commit 712d84b)

In commit `712d84b`, an attempt was made to decouple the cards, but it failed immediately upon user verification.

1. **What was implemented in `712d84b`**:
   - In `MultiViewControl.Border.cs`: Added a condition in `PutInCell`:
     ```csharp
     if (slot < GridCellCount && !IsEmptyCell(item))
     {
         foreach (var fc in _freeformCards)
         {
             if (fc.SourceSlot == slot) fc.SourceSlot = -1;
         }
     }
     ```
   - In `MultiViewControl.Freeform.cs`: Added a check in `SpawnFreeformCell` comparing paths, plus window turn logic.
   - In `MultiViewExport.cs`: In `ApplyLayoutFile`, created a standalone `ImageItem` fallback instead of taking `_items.FirstOrDefault()`.
   - In `MultiViewControl.xaml.cs`: Scattered secondary adjustments (drag thresholds, `IsBareCanvas` filters, camera reset triggers, z-index modifications).

2. **Why It Failed Immediately (User Test `media_1794194889296_34332a07.jpg`)**:
   - **Bypassed Execution Path**: When an empty cell is filled from the tray or pool via `BringFromTray`, the application does **not** call `PutInCell`! Instead, it executes `PlaceInCells(file, ...)` directly on the `MultiViewLayoutFile` DTO and calls `ApplyKeepingBitmaps(file)`.
   - `PlaceInCells` modifies `file.Photos[i].Path` directly in the DTO while completely ignoring `file.Freeform[card].SourceSlot`.
   - As a result, when `ApplyKeepingBitmaps` called `ApplyLayoutFile`, the layout file was reloaded with the obsolete `SourceSlot = 0` still intact!
   - **Fragmented Code Sprawl**: Modifying 6 files with speculative side-fixes introduced complexity without addressing the core requirement: **complete autonomy from birth**.
   - When the user converted Position 1 to freeform (Position 6) and filled Position 1, Position 6 corrupted immediately as before.

---

#### 8.11.3 The True Autonomy Architecture: Permanent Severance

To guarantee that freeform cards can never be corrupted by grid slot operations, the following principles must be enforced:

1. **Immediate Severance on Spawn**:
   - The moment any cell is made freeform, its `SourceSlot` is permanently set to `-1`.
   - The freeform card has zero knowledge of, and zero claim on, any grid slot.
   - The vacated grid slot is an ordinary empty cell with zero relationship to the floating card.

2. **Complete Data Model Autonomy**:
   - A freeform card holds its own distinct `ImageItem`.
   - Any filling, swapping, clearing, or resizing of grid slots ignores freeform cards completely.
   - `PlaceInCells`, `BuildLayoutFile`, and `ApplyLayoutFile` treat freeform cards as independent overlay entities, never linking them back to grid indices.

3. **Autonomous Docking**:
   - Docking does not rely on a "birth slot" index.
   - If docked, a freeform card simply finds any currently available empty grid slot; if none exists, it appends a new grid cell (if under the maximum capacity).
