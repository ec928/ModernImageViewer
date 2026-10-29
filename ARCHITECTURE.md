# ModernImageViewer architecture ledger

Authoritative invariants. Consult this before changing decode, cache, or GPU lifetime. Update it when those rules change.

## Product

Unpackaged WinUI 3 / Win2D image browser (portable zip). Collage editor and in-app cinematic director are in-tree beta surfaces, not described as core README features.

Local state lives under `%LocalAppData%\ModernImageViewer\` (`settings.json`, collage window bounds, cinematic window bounds, `CinematicAutoSave.json`). Never `ApplicationData.Current` — this app has no package identity.

## Decode tiers

| Tier | Max edge | Who | GPU? |
|------|----------|-----|------|
| Thumbnail | ~250 | `ImageItem` (shell thumbnail, WIC fallback) | No |
| Fast preview | 1200 | `ViewerEngine.DecodeFastPreviewAsync` | Uploaded on UI thread |
| Viewer, fit | long edge of the view | main window and detached window, after the preview | Yes |
| Viewer, zoomed past fit | native, capped at 16384 | same windows, one further decode | Yes |
| Display / collage high-res / cinematic | 4096 | collage `LoadHighResAsync`, `SceneAnalysisService` | Yes |
| Export | native | collage export | Temporary, disposed after save |

`DecodeHighFidelityAsync(path, token, maxEdge)` is the viewer decode past the preview. `maxEdge` is the long edge to produce, never above 16384. At fit that edge is the view. Zoomed past fit it is the file, capped at 16384. The scroll wheel's lower limit stays 0.1 for an ordinary photo. Fit of a wider image may go below 0.1, and that fit becomes the wheel floor for that image only.

WIC color-manages to sRGB. The draw path does **not** use Win2D `ColorManagementEffect`. Do not allocate a `ColorManagementProfile` unless a draw path consumes it.

## Image cache ownership

`ImageCache` is the only writer of the shared decode cache.

- `Publish(path, entry)` — cache takes the entry (initial refcount 1). Releases any *different* previous occupant. Will not replace a longer `DecodedLongEdge` with a shorter one (a high-fidelity entry still beats a preview when an edge was not recorded). Uses `TryRemove`/`TryAdd`, never a raw indexer write.
- `TryGet` — borrow. A surface that draws an entry must `AddRef` for the time it holds GPU pointers (`ImageViewerControl.InjectImage`) and `Release` in `PrepareForNewImage` / Unload / device-lost. Collage must not `Publish` over a slot that already has decodable content.
- `Evict(path)` — `Release` + remove. A detached window that displays a cache entry keeps it alive via the control’s `AddRef` until it `Release`s. Closing the window cancels an in-flight decode and releases that entry if it has not been published.
- `Rename(old, new)` — move the slot; release anything displaced at `new`.
- Never `dictionary[path] = entry`. Never `Dispose()` GPU fields on a shared entry. Device-lost: `ImageCache.Clear()` (including GIF `AnimationFrames`) and re-decode. Treat animation-frame device mismatch like `GpuBitmap` mismatch.

Collage keeps a **local** `CanvasBitmap` dictionary (`SetLocalGpu`) bound to that canvas's device. It may `Publish` a preview entry for native width/height, but must not clobber HF.

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

`publish.bat` is the supported ship path. It publishes self-contained x64 loose files to `bin\x64\Release\`, copies `LICENSE` and `README.md` in, checks `System.Private.Xml.dll` is full size (a part-installed SDK yields a build that compiles but crashes on launch), then smoke-launches the exe. Do not ship a zip that skipped the smoke test.

Tests live at `tests\ModernImageViewer.Tests` **inside** the repo. `ViewerMath.cs` is linked in by source rather than by project reference: the app targets `net8.0-windows10.0.19041.0` with WinUI, which an ordinary test project cannot reference. Keep pure geometry in `ViewerMath` so it stays testable.

## Cinematic direction

Product proposal (not implemented): `CINEMATIC_PROPOSAL.md`. Bar is **excellent cinematic in this app**: directed Play on a filled show, optional Edit show, music, present. VideoDirector remains the compositor / MP4 export app.

A new Play from the grid or viewer starts from the launch list. `CinematicAutoSave.json` is a crash restore only: written atomically while a show is open, deleted on clean close of the last cinematic window, offered once on the next Play if the file is still there.

## Multi-view

The layout is a `MosaicLayout` tree of row and column splits; a leaf is a photo's index in `_items`. `MosaicLayout.ShownCount` is the one place the 16-photo limit lives. When the selection is larger, the playbar reads `16 of 23 shown`. Grids come from `FromCounts` (photos per line), Banner and Pair from `CreateHeroLines` (the hero plus the other photos in lines), and Rectangle from `CreateRectangleHero`, which is drawn by hand for 4, 5, 6, 7 and 9 photos only.

The multi-view root forces Dark, so the photo canvas and playbar stay dark in a light window. The grid-layout card does not inherit that. `WindowTheme` reads the window theme from an ancestor above this control and sets it on the card and its flyout presenter.

Cells are owned by slot: `_cellRoots[i]` is the cell showing `_items[i]`. A swap exchanges the two roots and rebuilds with `_keepMosaic`, so no photo decodes again and dragged sizes stay. Removing a photo also keeps the other cells: its root is dropped, its entries leave the shared lists through the `CellParts` on its `Tag`, and the layout is rebuilt for the new count with `_relayoutKeepingCells`; only a photo past the limit that moves into view decodes. Because a cell outlives a swap, its controls (move arrows, menu, maximise, drag) look up `Slot(item)` when used; never capture the index at creation. Save JPEG reads cell `i` from `_cellRoots[i]` for the same reason: the order cells are built in follows the tree, not the photo order, and differs for a hero on the right or bottom.

Changing the line count, a line's count or the hero builds a new layout and resets dragged sizes, but keeps the cells (`RelayoutKeepingCells`), so nothing decodes again. The rows/columns switch turns a plain grid or a Rectangle a quarter (`Transpose`) and keeps dragged sizes; beside a Banner or Pair it relays the other photos.

A cell's grid thumbnail is only a placeholder: it is removed when the photo loads, and `CellParts.Photo` names the photo's `Image`. Save JPEG takes each photo's native size from that `Image`; reading the first `Image` in the cell found the ~250 px thumbnail and capped the export at thumbnail resolution.

## Bare Alt

There is no Alt peek at the sidebar; it was removed because it fired whenever Alt was held for an Alt-drag. The bare Alt key-down and key-up are still marked handled so Alt does not move focus to the window's system menu; Alt+key combinations are unaffected because the system tracks the modifier itself.

## Name ordering

Anything that orders file or folder names for display goes through `NaturalSort`, which calls the shell's `StrCmpLogicalW` — the function Explorer's Name column uses. Ordinal comparison sorts digit runs as characters, which puts 10 between 1 and 2. Do not hand-roll a replacement: leading zeros, embedded spaces and punctuation all have to match, and any divergence is the same bug in a narrower case. `StringComparer.OrdinalIgnoreCase` stays correct for dictionary keys and path equality, which are lookups rather than display order.

## Known remaining debt

- Collage and cinematic windows are still large code-behinds. Further splits (`FolderScanService`, director UI vs playback) are welcome but not required for correctness. Tracked as improvement I3 in `REVIEW.md`.
- Collage hit-testing ignores `Rotation` (AABB). Visuals rotate; pointers still use the unrotated layout.
- HEIC/AVIF need the Windows codecs pack. Decode failure shows a HUD.
- Unsigned portable zip: SmartScreen warning is expected.
