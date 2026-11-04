# ModernImageViewer Verified Code Audit (Triaged)

**Date:** 2026-11-01  
**Resolved:** every item in Part 1 was fixed in v0.29.3 (see the `docs/REVIEW.md` log). Kept as a record.  
**Status:** Double-checked against source code, [`docs/ARCHITECTURE.md`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md), and [`docs/UI_UX.md`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/UI_UX.md).

---

## Part 1: Relevant & Verified Bugs (Worth Fixing)

These items have been re-verified line-by-line against the live codebase and represent genuine bugs or UX regressions that do not conflict with the architecture ledger.

### 1. Disposed `CanvasBitmap` Injection after `ImageCache.Publish` (High)
- **Where**:
  - [`ImageCache.Publish`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/ImageCache.cs#L37-L63)
  - [`MainWindow.LoadFullImage`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.Viewer.cs#L305-L308) (and recovery path [`L320-L321`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.Viewer.cs#L320-L321))
  - [`MainWindow.LoadOrDecodeSpreadEntryAsync`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.Viewer.cs#L431-L435)
  - [`MainWindow.HandleFileActivation`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.IO.cs#L80-L89)
- **Why it is real**:
  When `ImageCache.Publish(path, entry)` sees an existing entry with `DecodedLongEdge >= entry.DecodedLongEdge` (e.g. a prefetch task or a detached window published while `await DecodeFastPreviewAsync` was running), `KeepsExisting` returns `true` and `Publish` calls `entry.Release()`, immediately disposing `entry.GpuBitmap`.
  While [`DetachedWindow.LoadImageAsync`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/DetachedWindow.xaml.cs#L330-L336) and [`MainWindow.PromoteDisplayedAsync`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.Viewer.cs#L493-L495) properly re-fetch via `ImageCache.TryGet` after `Publish`, the three `MainWindow` paths above pass the local variable (`result` / `fastEntry`) directly to `InjectImage` / `InjectSpreadSecondPage`.
- **Fix**: Re-fetch via `ImageCache.TryGet` after `Publish` at those three sites (matching `PromoteDisplayedAsync` and `DetachedWindow`).

### 2. HUD Button & Context Menu "Detach Image" Leaves `_tearingOffWindow` Dangling and Window Unactivated (High)
- **Where**:
  - [`ImageViewerControl.DetachButton_Click`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/ImageViewerControl.xaml.cs#L1065-L1068)
  - [`MainWindow.ViewerControl_DetachRequested`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.Viewer.cs#L3870-L3876)
  - [`MainWindow.LaunchDetachedWindow`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.Viewer.cs#L175-L186)
- **Why it is real**:
  `DetachButton_Click` invokes `DetachRequested?.Invoke(this, _lastHudTriggerPos)`. Because `cursorSpawnPosition` is non-null, `LaunchDetachedWindow` assumes an active pointer drag tear-off: it sets `_tearingOffWindow = detachedWindow`, shows the window unactivated via `SW_SHOWNOACTIVATE`, and waits for `ViewerControl_TearOffCompleted` to activate it and clear `_tearingOffWindow`. Because a button or context-menu click is not a drag gesture, `TearOffCompleted` never fires.
- **Fix**: Pass `null` (or a separate boolean flag) when `DetachRequested` is invoked from a button/menu click rather than a pointer tear-off drag.

### 3. Empty Freeform Cells Silently Dropped on Undo / Redo / Layout Load (High)
- **Where**:
  - [`MultiViewControl.SpawnFreeformCell`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Freeform.cs#L66-L68)
  - [`MultiViewControl.ApplyLayoutFile`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewExport.cs#L1704-L1715)
- **Why it is real**:
  Per [`ARCHITECTURE.md:98`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md#L98), an empty cell is saved in a layout with a blank path and keeps its parameters. However, `SpawnFreeformCell` has:
  ```csharp
  if (item == null || (IsEmptyCell(item) && string.IsNullOrEmpty(restoreDto?.Path))) return;
  ```
  When a floating cell is cleared (`Clear cell`) and its source grid cell is also empty (the normal state after `Freeform`), both `IsEmptyCell(item)` and `string.IsNullOrEmpty(restoreDto?.Path)` are `true`. Undoing, redoing, or loading the saved layout hits that `return` and silently drops the empty floating cell.
- **Fix**: Change line 68 to `if (item == null || (restoreDto == null && IsEmptyCell(item))) return;`.

### 4. Tray Image Permanently Lost When Brought to a Full Canvas If Duplicate Path Exists on Canvas (Medium-High)
- **Where**:
  - [`MultiViewControl.BringFromTray`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Freeform.cs#L447-L467)
  - [`MultiViewControl.AddDroppedPicturesAsync`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Freeform.cs#L353-L360)
- **Why it is real**:
  When the canvas has 16 images (`room == false`), clicking an image in the tray calls `BringFromTray(path)`. It removes `slot` from `file.Photos`, applies the layout, and calls `_ = AddPicturesAsync(new[] { path })` to spawn it as a floating cell. However, `AddDroppedPicturesAsync` filters through `NotYetHere(paths)`. If another cell on the canvas already holds that same `path` (e.g. via `Copy to...`), `NotYetHere` rejects it with `"Those images are already here"`—after `BringFromTray` has already deleted it from the tray.

### 5. Multi-View Solo Mode Steps Through Off-Screen Tray Images & Miscounts Total (Medium)
- **Where**:
  - [`MultiViewControl.EnterSolo`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.xaml.cs#L2751-L2759)
  - [`MultiViewControl.StepSolo`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.xaml.cs#L2828-L2838)
  - [`MultiViewControl.SetupSoloInteractions` (`DoubleTapped`)](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.xaml.cs#L2641-L2650)
- **Why it is real**:
  Per [`UI_UX.md:150`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/UI_UX.md#L150), *"positions count only the canvas, grid cells first and then floating cells, so they run without a gap. A tray image has no position"*. However, `StepSolo` iterates across all `0 .. _items.Count - 1`, which includes tray slots (`ShownSlots .. GridCellCount - 1`), and `EnterSolo` sets `SoloCounterText.Text = $"{index + 1} of {_items.Count}"`. Also, double-tapping in Solo mode to zoom to `2.5x` (`L2641-L2650`) omits calling `UpdateSoloHud()`, leaving the HUD zoom readout stuck at `100%`.

### 6. Floating Cell Top-Right "Remove Cell" Button Overlaps Top-Right Corner Resize Grip (Medium UX)
- **Where**:
  - [`MultiViewControl.CreateCell`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.xaml.cs#L1767-L1868) (`topControls` at `Top`/`Right`, `ZIndex = 205`, `closeBtn` 46×32 px)
  - [`MultiViewControl.AttachWindowResizeHandles`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Freeform.cs#L702-L749) (`TopRight` handle at `Top`/`Right`, `ZIndex = 200`, 22×22 px)
- **Why it is real**:
  On a floating cell, the visual white square resize grip (`12×12 px`) is drawn in the exact top-right corner (`MakeResizeGrip`), but `topControls` (`ZIndex = 205`) sits directly on top of that corner (`0..46 px` from right, `0..32 px` from top). Clicking the top-right resize grip on a floating cell clicks the red "Remove cell" button instead of resizing.

### 7. Unstyled `MenuFlyout` on Save/Load Folder & Layout Rows in Light Theme (Medium UX)
- **Where**:
  - [`MultiViewControl.CreateLayoutFileRow`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewExport.cs#L484-L518)
  - [`MultiViewControl.CreateFolderRow`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewExport.cs#L637-L662)
- **Why it is real**:
  In `CreateFolderRow`, the right-click `MenuFlyout` sets `Foreground = _menuWhiteSecondary` on its `FontIcon`s (`L641`, `L654`), but does not assign `MenuFlyoutPresenterStyle = (Style)Resources["MultiViewMenuFlyoutPresenterStyle"]` (unlike `ShowTrayCard` at [`MultiViewControl.Surprise.cs:629`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Surprise.cs#L629)). When the app/OS is in Light theme, the popup presenter opens with a light background and white icons (`UI_UX.md §6.8`).

### 8. Minor Cleanups Worth Doing While Touching Nearby Code (Low)
- **Undisposed `CanvasStrokeStyle` / `InMemoryRandomAccessStream`**: Add `using` to `new CanvasStrokeStyle` in [`MultiViewExport.cs:1139, 1208, 1337`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewExport.cs#L1139) and `using var stream = new InMemoryRandomAccessStream();` in [`MultiViewExport.cs:1359`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewExport.cs#L1359).
- **`ComicArchive` `ByteCacheOrder` Case-Sensitivity**: [`ComicArchive.cs:40-42`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/ComicArchive.cs#L40-L42) uses `StringComparer.OrdinalIgnoreCase` for `ByteCache` but case-sensitive `LinkedList<string>.Remove(key)` for `ByteCacheOrder`. Normalizing the key string fixes any path-casing mismatch.
- **Silent "Edit Image" Error from Grid Context Menu**: [`MainWindow.GridMenuEdit_Click`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MainWindow.Viewer.cs#L3746-L3756) calls `ViewerControl_EditRequested`, which shows errors on `ViewerControl` even when `ViewerControl` is collapsed.
- **3 Small Ledger Nitpicks**:
  - Raw PUA char `""` at [`MultiViewControl.Border.cs:170`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Border.cs#L170) -> `"\uE783"`.
  - `"colour"` at [`MultiViewControl.Border.cs:529-530`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Border.cs#L529-L530) -> `"color"` (`UI_UX.md §6.7`).
  - Missing `, Segoe MDL2 Assets` fallback at [`MultiViewControl.Surprise.cs:132`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Surprise.cs#L132) (`UI_UX.md §3`).

---

## Part 2: Items That Should Be Left Alone (False Positives or Intentional Design)

These items from the initial draft should **not** be touched:

1. **False Positives Caught by Claude (Subagent Hallucinations)**:
   - **`"Crop to faces"` / `"pictures"` in `MultiViewControl.Surprise.cs`**: Does not exist in the file. `ShowSurpriseCard` (`L307-L343`) uses `"Zoom on faces"` and consistently says `"images"`.
   - **`"Favourites"` spelling**: Explicitly specified as `"Favourites"` in [`docs/UI_UX.md:51, 70`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/UI_UX.md#L51).
   - **Wrong line citations in `Border.cs` / `Layout.cs`**: `MultiViewControl.Border.cs:652` already has `Segoe Fluent Icons, Segoe MDL2 Assets` and `\uE706`.
   - **Unbounded `Translation.Z` on floating cells**: False. Per [`ARCHITECTURE.md:112`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md#L112), clicking a floating cell does not raise its Z-index, and [`MoveCardLayer`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/MultiViewControl.Advanced.cs#L350-L366) renormalizes all `ZIndex` values to `5001 .. 5000 + count` on every layer change.

2. **Intentional Architectural Choices Documented in `ARCHITECTURE.md` / `UI_UX.md`**:
   - **All `CinematicWindow` findings**: [`ARCHITECTURE.md:156`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md#L156) explicitly notes: *"Cinematic is due to be largely redone, so it has not been audited with the rest of the app. Tracked as improvement I3 in `REVIEW.md`."* Leave all `Cinematic/` code untouched.
   - **Synchronous UI-thread GIF compositing (`ViewerEngine.cs:479-566`)**: Documented in [`ARCHITECTURE.md:42, 60`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md#L60): *"GPU objects (`CanvasBitmap`, `CanvasRenderTarget`, Win2D effects) are created and disposed on the UI thread that owns the `CanvasDevice`... Compositing still runs on the UI thread, so a long animation pauses the window while it does."*
   - **`Task.WaitAll(..., 60)` in `ReadSaveFolders` (`MultiViewExport.cs:386`)**: Documented in [`ARCHITECTURE.md:50`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md#L50): *"Save/Load waits at most 60 ms first, so a folder that answers at once shows its layouts without a jump."*
   - **Whole-session JSON Undo on input events (`MultiViewControl.Undo.cs:29-37`)**: Documented in [`ARCHITECTURE.md:118`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md#L118): *"After a pointer release, wheel, key or closed menu it compares the session with the last snapshot, so anything saved in a layout is undoable with no extra code."*
   - **Hold-to-save Frame Presets (`MultiViewControl.Border.cs:469-520`)**: Documented in [`ARCHITECTURE.md:130`](file:///c:/Users/chan_/OneDrive/Apps/0-Development/ModernImageViewer/docs/ARCHITECTURE.md#L130): *"Presets are Flush and three frames saved by holding the button, kept in `settings.json`."* It already has clear tooltips and a `"Saved"` flash on hold.
   - **`ImageCache.Rename` concurrency**: All `ImageCache.Publish` and `ImageCache.Rename` calls happen on the UI thread (because GPU bitmaps are created on the UI thread prior to `Publish`), so a background thread cannot race between `TryRemove` and `Entries[newPath] = entry`.
   - **4-Way Stroke / Shadow Duplication & Partial File Splits**: The XAML vs. Win2D export rendering paths target fundamentally different APIs (`Border`/`Polygon`/`WriteableBitmap` vs. `CanvasDrawingSession`/`CanvasGeometry`). Abstracting them would add indirection without behavioral benefit.
