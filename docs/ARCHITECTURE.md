# ModernImageViewer architecture ledger

Authoritative invariants. Consult this before changing decode, cache, or GPU lifetime. Update it when those rules change.

## Product

Unpackaged WinUI 3 / Win2D image browser (portable zip). The in-app cinematic director is an in-tree beta surface, not described as a core README feature. Collage work happens in Multi-view. The separate Collage editor was removed on 2026-10-31.

Local state lives under `%LocalAppData%\ModernImageViewer\` (`settings.json`, cinematic window bounds, `CinematicAutoSave.json`). Never `ApplicationData.Current` — this app has no package identity.

UI/UX styling, typography, theme parity, and component specifications are documented in `UI_UX.md`.

## Decode tiers

| Tier | Max edge | Who | GPU? |
|------|----------|-----|------|
| Thumbnail | ~250 | `ImageItem` (shell thumbnail, WIC fallback) | No |
| Fast preview | 1200 | `ViewerEngine.DecodeFastPreviewAsync` | Uploaded on UI thread |
| Viewer, fit | long edge of the view | main window and detached window, after the preview | Yes |
| Viewer, zoomed past fit | native, capped at 16384 | same windows, one further decode | Yes |
| Display / cinematic | 4096 | `SceneAnalysisService` | Yes |
| Export | sized to the cell | Multi-view export | Temporary, disposed after save |

`DecodeHighFidelityAsync(path, token, maxEdge)` is the viewer decode past the preview. `maxEdge` is the long edge to produce, never above 16384. At fit that edge is the view. Zoomed past fit it is the file, capped at 16384. The scroll wheel's lower limit stays 0.1 for an ordinary photo. Fit of a wider image may go below 0.1, and that fit becomes the wheel floor for that image only.

WIC color-manages to sRGB. The draw path does **not** use Win2D `ColorManagementEffect`. Do not allocate a `ColorManagementProfile` unless a draw path consumes it.

`.tif` and `.tiff` use that same decode. A multi-page TIFF shows its first page. A frame count above one is played only for other files, which is the GIF path.

## Image cache ownership

`ImageCache` is the only writer of the shared decode cache.

- `Publish(path, entry)` — cache takes the entry (initial refcount 1). Releases any *different* previous occupant. Will not replace a longer `DecodedLongEdge` with a shorter one (a high-fidelity entry still beats a preview when an edge was not recorded). Uses `TryRemove`/`TryAdd`, never a raw indexer write.
- `TryGet` — borrow. A surface that draws an entry must `AddRef` for the time it holds GPU pointers (`ImageViewerControl.InjectImage`) and `Release` in `PrepareForNewImage` / Unload / device-lost.
- `Evict(path)` — `Release` + remove. A detached window that displays a cache entry keeps it alive via the control’s `AddRef` until it `Release`s. Closing the window cancels an in-flight decode and releases that entry if it has not been published.
- `Rename(old, new)` — move the slot; release anything displaced at `new`.
- Never `dictionary[path] = entry`. Never `Dispose()` GPU fields on a shared entry. Device-lost: `ImageCache.Clear()` (including GIF `AnimationFrames`) and re-decode. Treat animation-frame device mismatch like `GpuBitmap` mismatch.

Refcount: cache hold = 1. Detached windows `AddRef` when they display a cache entry and `Release` on close. `ManageCache` `Evict`s keys outside the keep-alive radius (current ± 20). Prefetch is current +15 / −8.

GPU objects (`CanvasBitmap`, `CanvasRenderTarget`, Win2D effects) are created and disposed on the UI thread that owns the `CanvasDevice`.

## Folder scan

`ScanFolder` increments `_currentScanId`. Every UI marshal that mutates `Images` must capture that id and no-op if it no longer matches. A new scan may `Images.Clear()` only after in-flight batches have been dropped (they check the id).

The file list is one enumeration. `AddRange` reports each appended item. It does not raise `Reset`, which recycles the visible rows and restarts their thumbnails on every batch.

A debounced `FileSystemWatcher` on `_currentDirectory` re-runs `ScanFolder` when the grid is showing (preview closed). Events while the preview is open or a scan is running set a dirty flag and run when the preview closes / the scan finishes. A rename is dirty if either the old or new extension is supported.

## GIF compositing

Multi-frame decode reads `/imgdesc/Left`, `/imgdesc/Top`, `/grctlext/Delay`, `/grctlext/Disposal`. Frames are composited onto a canvas sized to the (possibly 16384-clamped) logical screen, not each frame's bitmap size.

Disposal: 0/1 keep, 2 restore background (punch frame rect), 3 restore to previous (snapshot before draw).

## Threads

- Directory enumeration and WIC decode: thread pool (`Task.Run` + `AsTask(token)`).
- `CanvasBitmap.CreateFromSoftwareBitmap` / `FinalizeHighFidelityGpuResources`: UI thread with a live `CanvasDevice`.
- Cancellation: `_currentImageLoadId` / `_currentScanId` plus `CancellationTokenSource`. Prefetch must always remove `_activeDecodes[path]` in a `finally` on the UI thread.

## Versioning and release

`<Version>` / `<AssemblyVersion>` / `<FileVersion>` in `ModernImageViewer.csproj` are the single source of truth. Bump them in the same commit as the release tag, so the tag, the zip name (`ModernImageViewer-vX.Y.Z.zip`) and the exe's file properties all agree. `Package.appxmanifest` carries the same number for tidiness only — this app is unpackaged (`WindowsPackageType=None`), so nothing reads it.

`publish.bat` is the supported ship path. It publishes self-contained x64 loose files to `bin\`, copies `LICENSE` and `README.md` in, checks `System.Private.Xml.dll` is full size (a part-installed SDK yields a build that compiles but crashes on launch), then smoke-launches the exe. Do not ship a zip that skipped the smoke test.

Tests live at `tests\ModernImageViewer.Tests` **inside** the repo. `ViewerMath.cs` is linked in by source rather than by project reference: the app targets `net8.0-windows10.0.19041.0` with WinUI, which an ordinary test project cannot reference. Keep pure geometry in `ViewerMath` so it stays testable.

## Cinematic direction

Product proposal (not implemented): `CINEMATIC_PROPOSAL.md`. Bar is **excellent cinematic in this app**: directed Play on a filled show, optional Edit show, music, present. VideoDirector remains the compositor / MP4 export app.

A new Play from the grid or viewer starts from the launch list. `CinematicAutoSave.json` is a crash restore only: written atomically while a show is open, deleted on clean close of the last cinematic window, offered once on the next Play if the file is still there.

## Multi-view

Multi-view is cell based, not multi-image. A freeform cell is a cell that can also be moved and resized and sits at a high z-index. `CreateCell` builds both kinds, and both are indexed by `_items` and `_cellRoots`. A freeform cell is hosted on `FreeformCanvas` and is not in the `MosaicLayout` tree, so `GridCellCount` leaves it out.

A cell holds the parameters that are applied to its content, and the content is the image. By default, copying an image changes only the target cell's content: the target keeps its parameters and applies them to the new image. A swap exchanges content in the same way.

Copy and swap can also include the cell's attributes, from the second column of their menu (`CopyCellAttributes`, `ExchangeCellAttributes`). Attributes are the frame, the image's turn and flips, and zoom and pan. The window part of `CellGeometryDto` (shape, its flips and rotation, `ImageCounter` and the L's corner) moves only between two floating cells: export and `CoverBox` read a grid cell's geometry as they read a floating cell's, so a grid cell must stay a rectangle. Place, size and layer belong to the cell and never move. The values are cloned, so the two cells stay independent. Alt+drag remains content only.

A floating cell draws its shape and frame from `FreeformCard.Item`, so that must point at the new `ImageItem` before the cell redraws. Copy once set it afterwards, and the redraw read the old item, which has no parameters left after `MoveCellParameters`: the target drew as a plain rectangle while its stored frame and shape were intact.

Triggering Freeform always generates a new cell, whatever other cells exist or show: the existing cell is left empty, and the new cell has the image, a clone of the existing cell's parameters and the highest z-index, so a 4-cell view becomes a 5-cell view. The new cell is independent: changing it does not change the existing cell.

A picture dropped from outside the app also generates a new floating cell, but it has no source: no other cell changes, `SourceSlot` is -1 so Dock gives it a new grid cell, and it starts with default parameters and the highest z-index (`AttachFileDrop`, `AddDroppedPicturesAsync`). Its size follows the picture's shape, read from the file header, up to 45% of the canvas in each direction. One drop adds at most `MaxDroppedCells`. The handler sits on the control's `RootGrid` and marks the drop handled, or the main window's own drop would replace the session. A drop with no pictures goes on to the main window (`OpenDroppedItemsAsync`), and so does any drop during an Alt-drag swap or Solo.

The Add images button parks the session while the user finds pictures with the app's own navigation. Coming back makes each selected picture a new floating cell, as a drop from Explorer does (`AddPicturesAsync`).

An empty cell is a cell whose `ImageItem` has no path (`IsEmptyCell`, `EmptyCell`). It holds no image, keeps its parameters for the next image, draws nothing but a label that fades with the playbar, leaves the canvas showing in an export, and is saved in a layout with a blank path. Freeform and Clear cell make one. Layouts saved when cells could be hidden load those cells as empty, including a hidden cell whose photo was not in use elsewhere.

Going back to the grid deletes the extra cells and resets cell parameters. Grid Layout, and its keys, need at least two grid cells that show an image: empty cells and freeform cells do not count.

Each cell has its own `ImageItem`, and `_itemBorders` and `_cellGeometry` hold its frame and turns against that object. When the image changes, `MoveCellParameters` or `ExchangeCellParameters` carries them over, so they stay with the cell. Docking returns the image to its source cell while that cell shows no image, and otherwise adds a new grid cell. `FreeformCard.SourceSlot` is still an index. `RemoveItem` keeps it correct. `SpawnFreeformCell` clones the frame and the image turn, while zoom and pan start at default.

A floating cell can move and resize until its centre reaches the canvas edge (`FreeformBounds`). The centre is the limit because a cell turns about it, so the limit is the same at any rotation and for any shape. Limiting the rectangle stopped a turned cell at an edge that was not on screen. A new cell, from Freeform or a drop, still starts fully inside. `FreeformCanvas` is clipped to the canvas (`AttachCanvasClip`), so what hangs over is cut on screen as it is in an export, and cannot cover the title bar or take its clicks.

A floating cell keeps its place and share of the canvas when the canvas changes size, as a grid cell does (`RescaleFreeformCards`), so a change of shape also changes the cell's shape. A layout file records the canvas size it was saved on, and its floating cells are scaled to the canvas it opens on. A file with no size is taken as the current canvas.

The canvas is `RootGrid`, not `CellsGrid`. The canvas frame (`_canvasFrame`, `ApplyCanvasFrame`, saved as `canvas.frame`) moves `CellsGrid` in by its gutter plus line, so the line never covers a photo. From the outside in the order is gutter and mat, line, grid, the same as a cell. Exports and the canvas label measure `RootGrid`, and grid cells are placed relative to it. The mat and line sit behind the grid and floating cells sit above them, the same order the export draws. As in a cell, the canvas and mat stay square and only the line and what is inside it round: a composition clip keeps the grid inside the rounded line. The mat is a whole-canvas fill with the line's shape cut out, and the shadow the grid and line cast onto the gutter is an image on that same layer. The export puts back what grid cells covered past the line, draws the line, then the floating cells.

A file that will not open marks its cell `Failed`. The cell says so by name, export carries on without it, and it stops counting as still loading (`AnyPhotoLoading`). A photo waiting off screen past the 16-photo limit has no cell and does not count either. A layout tree that does not place each photo exactly once falls back to the plain layout (`MosaicNodeDto.FitsPhotos`).

The Advanced menus are in `MultiViewControl.Advanced.cs`. Every cell can replace its image from a file picker (`PickReplacementImage`, `ReplaceCellImage`), which is a change of content like any other: the cell keeps its parameters, and a `Failed` cell recovers when the new file loads. Every cell can rotate its image in quarter turns and mirror or flip it. Every cell can take a window shape (`WindowShapes`, `ApplyCellShape`) and mirror or flip it. A freeform cell can also rotate the window from the menu in 15 degree steps or with its drag handle, and move the L's inner corner with its handle. Its layer among the floating cells is set from the menu; clicking a floating cell does not raise it, so a chosen order holds. A dragged floating cell snaps to the canvas, other floating cells and showing grid cells (`FreeformSnap`); Alt turns that off. The playbar's Advanced button holds undo and redo. Its Canvas button holds the canvas shape, surface and color (`CanvasLook`), which show wherever no cell covers the canvas, and in the export. Save/Load holds copy to clipboard.

The shape clip goes on the mat and the card, never on the window: a composition visual on the floating window stopped it taking pointer input. A shaped window's photo grid has no background, and a filled polygon inside it is the area that takes pointer input, so clicks in the empty part reach what is behind. A grid cell has nothing behind it, so it keeps its background and its whole rectangle takes pointer input. The window's resize handles stay on its rectangle, so on a shape they can sit in empty space. While the pointer is over a floating cell of any shape, a grip marks each corner (`MakeResizeGrip`) and a dashed outline marks the rectangle. On every cell the hover buttons and position label sit on the cell root, outside the clip. On a floating cell the label is centred and stays upright (`ApplyWindowTurn`). The rotate and L-corner handles and the hover buttons stay for `WindowHoverGraceMs` after the pointer leaves, because they can sit outside the shape. A shaped window turns off the card's rectangular stroke and draws its frame along the outline, pulled in by half the stroke so it is not cut at the photo's edge. `ApplyBorderToCell` reapplies the outline, so it follows every frame change. The gutter moves every edge of the shape inward by the same distance (`WindowShapes.Placed`): the mat fills the outer outline, and the photo and frame sit on the inner one. Outlines are polygons in a unit square, with curves as many short edges, so a new shape needs only its points. Corner radius rounds the corners of any shape that has them; the oval, squircle and blob have none (`HasNoCorners`). A gutter too big for the shape is reduced until it fits (`WindowShapes.Inset`): a shape inset past its middle comes out upside down with its area sign unchanged, so reversed edges are the test (`TurnedInsideOut`). The shadow is cast from the inner outline onto the mat (`FrameShadow.RenderAlpha` with an outline), and `ApplyShapedShadow` renders it again only when something it depends on changes. Rim glow belongs to rectangles, so a shape keeps its shadow whichever is chosen. On screen every shadow renders at half size and stretches to fit (`ShadowPreviewDivisor`), because it is soft and a full-window render took over 300 ms on every drag step. Export renders at full size.

Rotating the shape alone turns the window one way and the photo the other (`ImageCounter`), so the photo holds still on screen. Hold Ctrl on the rotate handle. In Fill the photo grows only enough to cover the shape (`WindowShapes.CoverBox`).

Undo (`MultiViewControl.Undo.cs`) keeps whole-session snapshots in the layout file format. After a pointer release, wheel, key or closed menu it compares the session with the last snapshot, so anything saved in a layout is undoable with no extra code. A restore rebuilds the cells and hands them the images already decoded. Snapshots hold floating cells as shares of the canvas, so the window's size is not part of undo. Until something has been changed, the starting snapshot follows the canvas (`RebaseUntouchedUndo`), because a session can open before the canvas has its size.

Solo follows the cell's quarter turns and mirroring with `SoloTurn` on the image, and zoom and pan on its host, so panning follows the screen. Stepping through Solo skips empty cells.

Move to is a copy followed by emptying a grid source or closing a floating one (`MoveItemTo`). Each row of Swap with, Copy to and Move to is one `MenuFlyoutItem`, retemplated to draw two cells (`MakeCellTargetMenu`, `CellTargetRowTemplate`), because a menu cannot lay its entries out in columns. The menu handles the click, and where the pointer sits in the row picks the column; the Left and Right keys pick it from the keyboard. Do not put buttons inside a menu entry: the first version did, and its clicks failed for the mouse while touch worked.

Strokes, gradient ends and mats share one set of swatches (`FramePalette`), and a test fails if any two in it, or in the canvas list, come too close. The last swatch, and Custom in the canvas menu, open the standard Windows color dialog (`WindowsColorDialog`). It has no transparency, and its custom slots last until the app closes. Leaving Multi-View by Back, Esc or the tile and list buttons does not clear the session: `CloseMultiView` collapses the control and sets `_multiViewKept`, and the Multi-View button resumes it, undo history included. Choosing photos with View Together or opening a layout replaces it. It is not kept when the app closes. `AutoSaveSession` keeps the last three sessions in `autosave` beside the settings, in the undo snapshot format, when a session is left or replaced and when the app closes. It skips blank sessions and repeats, and Save/Load lists them. A crash writes nothing, so work since the last leave is lost.

Rows and Columns are a switch on the playbar (`CountsSwitch`). The Grid layout card is a plain panel above the bar (`CountsCard`), not a flyout: a flyout dismisses itself on any outside click, and that raced with the switch reopening it, so the card sometimes failed to open. Only `OpenCountsCard` and `CloseCountsCard` change it. A press outside the card or switch closes it, and Esc closes it first. The frame card closes while the dialog is open and comes back with its settings (`ReopenFrameCard`). The card is placed by a point in the window (`FrameCardPosition`): beside its cell when there is room, otherwise against the window edge farther from the cell. A placement relative to the cell squeezed the card into whatever space was left, and with a large floating cell that cut it down to a few rows. Presets are Flush and three frames saved by holding the button, kept in `settings.json`.

Known limits of this prototype. Snapping uses a rotated cell's unrotated rectangle. Solo shows the image's quarter turns, not a shape-only turn. A cell's zoom and pan are in pixels, so they are clamped rather than scaled when the canvas changes size.

The layout is a `MosaicLayout` tree of row and column splits; a leaf is a photo's index in `_items`. `MosaicLayout.ShownCount` is the one place the 16-photo limit lives. When the selection is larger, the playbar reads `16 of 23 shown`. Grids come from `FromCounts` (photos per line), Banner and Pair from `CreateHeroLines` (the hero plus the other photos in lines), and Rectangle from `CreateRectangleHero`, which is drawn by hand for 4, 5, 6, 7 and 9 photos only.

The multi-view root forces Dark, so the photo canvas and playbar stay dark in a light window. The grid-layout card and its flyout presenter are pinned to Dark as well (`ApplyGridLayoutTheme`), to match the playbar.

Cells are owned by slot: `_cellRoots[i]` is the cell showing `_items[i]`. A swap leaves the cells in place and moves the decoded images between them, so no photo decodes again and dragged sizes stay. A photo still opening, or waiting off screen, has nothing to move and loads into the cell instead. Removing a photo also keeps the other cells: its root is dropped, its entries leave the shared lists through the `CellParts` on its `Tag`, and the layout is rebuilt for the new count with `_relayoutKeepingCells`; only a photo past the limit that moves into view decodes. Because a cell outlives a swap, its controls (move arrows, menu, maximise, drag) look up `Slot(item)` when used; never capture the index at creation. Save JPEG reads cell `i` from `_cellRoots[i]` for the same reason: the order cells are built in follows the tree, not the photo order, and differs for a hero on the right or bottom.

Changing the line count, a line's count or the hero builds a new layout and resets dragged sizes, but keeps the cells (`RelayoutKeepingCells`), so nothing decodes again. The rows/columns switch turns a plain grid or a Rectangle a quarter (`Transpose`) and keeps dragged sizes; beside a Banner or Pair it relays the other photos.

Never close a thumbnail's `SoftwareBitmap` after `SetBitmapAsync`, and never dispose its `SoftwareBitmapSource`. Closing the bitmap when the call completed caused a crash when switching between the tile grid and Multi-View: WinUI failed fast in `AsyncCopyToSurfaceTask::CopyOperation` on a background thread, with RO_E_CLOSED on the stack and nothing in `crash.log`. WinUI most likely copies from the bitmap again after the call completes, such as when a tile is recreated during a resize. The source can be on screen in several places at once: its tile, a cell's placeholder, the viewer and the preview. Both are left to the garbage collector. A native fail fast like this leaves a dump in `%LocalAppData%\CrashDumps`, and Microsoft's public symbols for `Microsoft.UI.Xaml.dll` name its stack.

A cell's grid thumbnail is only a placeholder: it is removed when the photo loads, and `CellParts.Photo` names the photo's `Image`. Save JPEG takes each photo's native size from that `Image`; reading the first `Image` in the cell found the ~250 px thumbnail and capped the export at thumbnail resolution.

## Bare Alt

There is no Alt peek at the sidebar; it was removed because it fired whenever Alt was held for an Alt-drag. The bare Alt key-down and key-up are still marked handled so Alt does not move focus to the window's system menu; Alt+key combinations are unaffected because the system tracks the modifier itself.

## Name ordering

Anything that orders file or folder names for display goes through `NaturalSort`, which calls the shell's `StrCmpLogicalW` — the function Explorer's Name column uses. Ordinal comparison sorts digit runs as characters, which puts 10 between 1 and 2. Do not hand-roll a replacement: leading zeros, embedded spaces and punctuation all have to match, and any divergence is the same bug in a narrower case. `StringComparer.OrdinalIgnoreCase` stays correct for dictionary keys and path equality, which are lookups rather than display order.

## Known remaining debt

- The cinematic window is still a large code-behind. Further splits (`FolderScanService`, director UI vs playback) are welcome but not required for correctness. Tracked as improvement I3 in `REVIEW.md`.
- HEIC/AVIF need the Windows codecs pack. Decode failure shows a HUD.
- Unsigned portable zip: SmartScreen warning is expected.
