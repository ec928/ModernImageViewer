# ModernImageViewer review tracker

Living list of issues from the 2026-08-26 codebase review. Update **Status** when a fix lands. Do not delete issues — mark them `fixed` and note the approach.

Decode tiers, cache ownership, and thread rules live in `ARCHITECTURE.md`.

## Summary

The Win2D viewer core is real, but GPU lifetime was not reference-counted end-to-end. Collage and cinematic could leak or upload full-resolution textures. Folder scans could mix listings. There were no tests and no architecture ledger.

## Issues

| # | Sev | File | Status | Notes |
|---|-----|------|--------|-------|
| 1 | bug | MainWindow.IO.cs | fixed | AddRange lambdas capture `myScanId` and no-op if superseded |
| 2 | bug | CollageCanvas.xaml.cs | fixed | All writes go through `ImageCache.Publish` (releases previous; will not downgrade HF) |
| 3 | bug | CollageCanvas.xaml.cs | fixed | `SetLocalGpu` dispose-and-replace; Delete shares `RemoveSelectedCells_Click` |
| 4 | bug | MainWindow.Viewer.cs | fixed | Prefetch uses a frozen path set; `_activeDecodes.Remove` in `finally` on UI thread |
| 5 | bug | MainWindow.Viewer.cs | fixed | Device mismatch `Evict`s and re-decodes; no in-place `Dispose` of shared GPU |
| 6 | bug | ViewerEngine.cs | fixed | Dropped unused `ColorManagementProfile` allocation |
| 7 | bug | ViewerEngine.cs | fixed | GIF Left/Top, disposal 2/3, canvas sized to clamped logical screen |
| 8 | bug | SceneAnalysisService.cs | fixed | Cinematic decode capped at `DisplayMaxEdge` (4096) |
| 9 | bug | CinematicWindow.xaml.cs | fixed | `PeekNextIndex` / consume; preload and advance share the same index; dead RequiresCut branch removed |
| 10 | bug | CinematicWindow.xaml.cs | fixed | Paused playback skips full-rate invalidate unless freeze-frame/telemetry; size-change rebuilds trajectories |
| 11 | bug | CinematicViewModel.cs | fixed | Auto-save under `%LocalAppData%\ModernImageViewer\CinematicAutoSave.json` |
| 12 | bug | MainWindow.xaml.cs | fixed | Multi-select uses `RecycleFile`; failures shown in a dialog |
| 13 | bug | DetachedWindow.xaml.cs | fixed | Delete removes grid item + cache; rename raises INPC and moves cache key |
| 14 | bug | DetachedWindow.xaml | fixed | `AddToCollageRequested` wired to main window |
| 15 | bug | ImageViewerControl.xaml.cs | fixed | `GammaChanged` → `ApplyGamma` on main + detached; persisted in settings |
| 16 | bug | CollageElement.cs | fixed | `DrawCell` rotates around cell center inside the clip |
| 17 | bug | CollageCanvas.xaml.cs | fixed | High-res is 4096-capped, released on zoom-out/deselect, max 3 live |
| 18 | bug | CollageEditorWindow.xaml.cs | fixed | Close/Load prompt when `HasUnsavedChanges` |
| 19 | bug | CollageEditorWindow.xaml.cs | fixed | `using` SoftwareBitmap; extra CanvasBitmaps disposed in `finally` |
| 20 | bug | App.xaml.cs | fixed | Unknown exceptions no longer `Handled = true` |
| 21 | bug | ModernImageViewer.csproj | fixed | RID follows Platform; ARM64 pubxml is win-arm64; relative PublishDir |
| 22 | suggestion | ImageItem.cs | fixed | Decode failures log + HUD; user-facing rename/delete errors shown |
| 23 | suggestion | ImageViewerControl.xaml.cs | fixed | Effect stack disposed on Unloaded; collage resets shared effects |
| 24 | suggestion | MainWindow.xaml.cs | fixed | OneDrive VideoDirector fallback removed; dead slideshow toggle deleted |
| 25 | suggestion | memory_ledger.md | fixed | `ARCHITECTURE.md` + `ViewerMath` xUnit project |
| 26 | nit | various | fixed | History / compiler-trivia comments removed |
| 27 | nit | Package.appxmanifest | fixed | Unused `systemAIModels` capability removed |
| 28 | bug | ModernImageViewer.slnx | fixed | Test project sat *outside* the repo and was untracked; the committed slnx pointed at `..\ModernImageViewer.Tests`, so a fresh clone got no tests and a solution that would not load. Moved to `tests\ModernImageViewer.Tests`, with a `Compile Remove="tests\**"` in the app csproj so the SDK's default glob does not compile the test sources into the app. |
| 29 | bug | ModernImageViewer.csproj | fixed | No `Version` was ever set, so every build stamped the SDK default `1.0.0.0` — the shipped v0.9.0 exe disagreed with its own release tag. `Version`/`AssemblyVersion`/`FileVersion` now pinned. |
| 30 | bug | Cinematic/CinematicWindow.xaml.cs | fixed | Three `async void` methods (trajectory update, add-from-folder, sequence drop) caught nothing or only `TaskCanceledException`. `App_UnhandledException` sets `Handled = false`, so a camera-math fault or a drop from a vanished network path terminated the whole viewer. Each now logs and continues. |
| 31 | bug | ARCHITECTURE.md | fixed | Referenced `CINEMATIC_PROPOSAL.md`, which was untracked — the doc was absent from every clone. Now committed. |
| 32 | suggestion | MainWindow.xaml | fixed | The `?` button jumped straight to the GitHub issue tracker with no explanation of where it was sending you. Replaced with a standard About dialog: what the app is, the version read from the assembly, and Report a problem / View on GitHub as dialog buttons. |
| 33 | bug | MainWindow.IO.cs | fixed | Name ordering used ordinal comparison, so a numbered folder read 1, 10, 11, ... 2, 20 instead of Explorer's order. All six name-sort sites (image grid, four folder-tree sorts, Name Asc/Desc menu) now call `NaturalSort`, which wraps the shell's own `StrCmpLogicalW`. |

## Improvements

| # | Status | Item |
|---|--------|------|
| I1 | fixed | `ImageCache` is the only shared-cache writer (`TryGet` / `Publish` / `Evict` / `Rename`) |
| I2 | fixed | Caps: preview 1200, display/cinematic/collage-HR 4096, HF 16384, export native |
| I3 | open | Split god windows further (`FolderScanService`, director UI vs playback) — deferred |
| I4 | fixed | `ModernImageViewer.Tests` covers `ViewerMath` |
| I5 | fixed | `ARCHITECTURE.md` ledger |
| I6 | fixed | Relative pubxml; `publish.bat` remains the supported ship path |
| I7 | fixed | README notes collage/cinematic beta and HEIC/AVIF codecs |
| I8 | fixed | Debounced `FileSystemWatcher` refreshes the grid when the preview is closed |
| I9 | fixed | Displayed cache entries `AddRef` in `InjectImage`; collage does not Publish over a live decode |
| I10 | fixed | Device-lost clears the shared cache (GIF frames included) before re-decode |
| I11 | fixed | File-activation HF timer restarts after the scan sets `_currentIndex` |
| I12 | fixed | Prefetch `finally` only removes its own CTS |
| I13 | fixed | Folder watcher defers refresh while preview/scan is busy; rename checks old and new extension |
| I14 | fixed | Cinematic Play is clean; autosave is crash restore (prompt + atomic write); New clears ledger |
| I15 | fixed | Cinematic pending next is clip id; loop/stop at end; failed files skipped; windows rooted |

## Log

- 2026-08-26: Review filed. Fix pass started.
- 2026-08-26: Fix pass landed (see statuses). Remaining: split oversized code-behinds (I3); collage hit-testing still uses unrotated AABB.
- 2026-08-27: Verification pass. Release build clean (0 warnings, 0 errors); 5/5 tests pass. Found and fixed #28 and #29. Removed a stale 5.7 MB `build.log`. Released as v0.10.0.
- 2026-08-27: Follow-up pass. Cache display refcount, device-lost GIF eviction, file-activation HF, watcher deferral, cinematic clean Play + crash restore, playback clock. Remaining: I3 god windows; collage AABB hit-test; directed Play pacing/music/Open-show (proposal).
- 2026-08-27: Ken Burns duration is this-slide (marks Custom); beats stay global on Auto; unsaved zoom/pan is discarded on pane close. Collage dirty tracking survives JSON load; export decodes native with EXIF/sRGB and disposes per cell.
- 2026-08-27: Release pass for v0.11.0. Build clean (0 warnings, 0 errors); 5/5 tests pass. Guarded three `async void` paths in `CinematicWindow` that could terminate the app (#30). Committed `CINEMATIC_PROPOSAL.md`, which `ARCHITECTURE.md` referenced but no clone had (#31). Deleted the `_revert_cinematic` snapshot and its build exclusion — git holds the same bytes at 46ba963.
- 2026-08-27: v0.11.1. About dialog replaces the bare `?` (#32). Verified by driving the real UI: first build opened scrolled to the bottom because focus landed on a link inside the scroll area, and the links rendered as dead plain text — both fixed by moving the actions to dialog buttons.
- 2026-08-27: v0.11.2. Hold Alt to peek at the sidebar (Ed's suggestion). Verified by driving the real window: the first harness attempt proved nothing because SetForegroundWindow failed silently and the synthetic Alt went to the terminal; AppActivate fixed it, and Ed confirmed by hand.
- 2026-08-27: v0.11.3. Explorer name ordering (#33). `NaturalSort` calls `StrCmpLogicalW` rather than reimplementing the rule. Six xUnit cases, one encoding the exact order from Ed's screenshot; MIV tests 5 -> 11.
- 2026-09-17: v0.11.4. Persistent sorting order. Added `SortOrder` to `AppSettings`, applied persistent sort choice in `ScanFolder`, and updated UI to use `RadioMenuFlyoutItem` to show current state.
- 2026-10-25: v0.11.5. The viewer decodes to the size of the screen, and to the file when zoomed past fit. A very wide image can fit. Large folders append instead of resetting. Drag-out attaches the file before the drag starts. Closing a floating window cancels a decode still in progress. Portrait slides blur the background once.
- 2026-10-25: v0.11.6. A plain click opens a photo and leaves the grid with nothing selected. Ctrl or Shift still selects.
- 2026-10-25: v0.11.7. Arrow keys, Page Up, Page Down, Home, and End move a cursor on the tile grid. Enter opens that photo and Enter again returns to the tiles. The cursor is a thin ring, not a selection.
- 2026-10-25: v0.11.8. The tile cursor is a light edge inside the photograph. The filename is left clear. A selected tile keeps its check and loses the box around the caption.
- 2026-10-25: v0.11.9. A selected photograph gets an accent edge and a check on the picture. The filename stays clear.
- 2026-10-25: v0.11.10. The header is a thin bar with refresh, reset, sort, and tiles or list. List view is filenames in columns. A separate window previews the cursor row and hides while a photo is open.
- 2026-10-25: v0.11.11. The list preview is a band under the files. List view can open the parent folder, a subfolder, or another drive. The tiles or list choice is remembered. A selected tile is tinted, with a check. A selected list row is tinted across the row. The selection actions sit under the files. In tile view the keyboard cursor raises that tile.
- 2026-10-25: v0.11.12. Fluent header bar with sidebar toggle, folder pill with photo count badge and clickable path flyout to open in Explorer, and segmented Tiles/List toggle. Uniform 1-line tile captions, interactive hover/selection check badges, untinted photo selection, and floating multi-select bar. List mode uses 24px rows with full-width column math, photo glyphs, clickable preview thumbnail, and clean folder preview rows. Multi-select delete batches all paths into a single off-UI-thread `SHFileOperation` call and single collection reset while pausing `_folderWatcher`. Default window width set to 1278 with debounced resize persistence.
- 2026-10-31: v0.19.0, internal. Multi-View is the collage mode: cell-based architecture, floating cells with window shapes, image rotation and mirroring, canvas surface and color, frame presets and custom colors, undo and redo, PNG export, and copy to the clipboard.
- 2026-10-31: Collage editor removed; Multi-View is the collage mode. Collage entries above are history. The Video Director button and its launcher are gone from the sidebar, since it is a separate app.
- 2026-10-31: Multi-View prototype refinement, not released. Export no longer blocked past 16 photos or by a missing file. Floating cells follow the canvas size. Menus grouped. Unused frame presets and helpers removed.
- 2026-10-26: v0.12.0. Native `.cbz` (ZIP) and `.cbr` (RAR4/RAR5 via SharpCompress) comic book reader support with responsive 1-page / 2-page spread mode (`DrawDualMappedImages`), cover thumbnail decoding, `CBZ`/`CBR` tile badges and list book icons, virtual folder navigation (`archive::|::entry`), sidebar tree integration, and LRU page byte caching.
- 2026-10-31: Multi-View, not released. Copy onto a floating cell no longer draws it as a plain rectangle. Swap and Copy have a With attributes column and name positions only. The Frame & Border card is placed by point, so a large cell cannot squeeze it. Seven more window shapes.
- 2026-10-31: Multi-View, not released. Swap and Copy rows are ordinary menu entries, after buttons inside an entry failed for the mouse; the pointer's side of the row picks the column. Pictures dropped from Explorer become new floating cells.
- 2026-10-31: v0.20.0, released. Multi-View as the collage mode: everything in the internal v0.19.0, plus swap and copy with or without cell attributes, seven more window shapes, pictures dropped from Explorer as floating cells, and the fixes to copy on floating cells and the frame card.
- 2026-10-31: Multi-View, not released. Advanced > Replace image picks a file for the cell, which keeps its parameters and recovers a missing file.
- 2026-10-31: Multi-View, not released. Hovering a floating cell shows corner grips and a dashed outline of its resize rectangle, and the corner grab areas are larger.
- 2026-10-31: Multi-View, not released. A floating cell moves and resizes until its centre reaches the canvas edge, at any rotation and shape. Floating cells are clipped to the canvas, so they no longer draw over the title bar.
- 2026-10-31: Multi-View, not released. Coming back from Photos no longer depends on the selection count: a cell whose photo stays selected keeps it, so cells sharing a file and arrangements survive, and new photos fill only deselected cells.
- 2026-10-31: v0.21.0, internal. Replace image from a file, resize grips and outline on floating cells, floating cells limited by their centre and clipped to the canvas, and Photos returning when cells share a file.
- 2026-10-31: Multi-View, not released. Rows and Columns are a switch on the playbar that opens the Grid layout card. The card keeps how many lines and what is in each.
- 2026-10-31: Multi-View, not released. The Grid layout card is a plain panel above the bar rather than a flyout, because self-dismissal raced with the switch and the card sometimes failed to open.
- 2026-10-31: Multi-View, not released. Save/Load is one two-column menu with no sideways scrolling, shortened folder names, and layouts sharing the height available.
- 2026-10-31: Multi-View, not released. Freeform leaves its source cell empty rather than hidden. Empty cells keep their parameters, can be filled by Add image, Copy to, Swap with and Photos, and save in layouts.
- 2026-10-31: Crash switching between the tile grid and Multi-View fixed: thumbnail bitmaps are no longer closed after SetBitmapAsync, and thumbnail sources are no longer disposed.
- 2026-10-31: v0.22.0, internal. Crash switching between tiles and Multi-View fixed. Rows and Columns switch on the bar with a plain Grid layout panel, Save/Load as one two-column menu, and Freeform leaving its source cell empty.
- 2026-10-31: Multi-View, not released. Clear cell replaces Hide image, so a cell with no image has one meaning. Layouts with hidden cells load them as empty.
- 2026-10-31: Multi-View, not released. Controls audit: leaving keeps the session; Reset, move arrows and items, Save as PNG in the wheel and menu rotate items removed; Canvas menu on the bar; Replace image at the top level; Shape submenu; Back and Remove cell wording.
- 2026-10-31: Multi-View, not released. Photos becomes Add images: find pictures with the app's own navigation, and each comes back as a floating cell. Start over added to the wheel. Reset zoom removed, since middle-click does it.
- 2026-10-31: Multi-View, not released. Reset Canvas replaces Start over and stays in Multi-View, as one undo step. The last two sessions are autosaved and listed in Save/Load.
- 2026-10-31: Multi-View, not released. Reset Canvas removed. Three sessions are autosaved, timed to the second.
- 2026-10-31: v0.23.0, internal. Multi-View: Frame & Border for the whole canvas, Move to, Parallelogram, shapes on every cell, the cell menu reorganised (Shape, Layer and Image), one-picture picker for Add image and Replace image, click an empty cell to add, Explorer drop onto an empty cell, window keeps its size while picking, Fill or Fit moved to the wheel with no shortcut, autosave of three sessions. Shadows drawn small and stretched: a 7680x4320 canvas shadow from 38 s to under 70 ms in a Debug build.
- 2026-10-31: App audit fixes, not released. Delete warns where there is no Recycle Bin. Settings and autosaves are written whole and one at a time. A folder that fails says so. Long animations are capped at 2 GB. A window saved off-screen comes back. Offline folders no longer freeze the Recent or Save/Load menus, startup, or moving between folders. A changed file updates its tile in place. Folder scans take the metadata the directory already returned: 20,000 files from 1.5 s to about 30 ms. Thumbnails follow the tile size. Opens the last folder. F1 lists the shortcuts and F6 reaches the header.
- 2026-10-31: Multi-View, not released. Hero moves from the bar into the grid layout card. Reset canvas and images in the wheel.
- 2026-10-31: Detached windows, not released. Counted by window, so the app cannot stay running hidden. A picture that fails says so. Handled keys are ignored. Stays fitted through resizing until zoomed, by either way of detaching. Dragging out stops at 16 windows.
- 2026-10-31: Clicking a photo outside a selection opens that photo and keeps the selection.
- 2026-10-31: v0.24.0, released. Everything since v0.22.0.
- 2026-10-31: Multi-View, not released. Surprise me: arranges the pictures into Edge to edge, Gallery wall, Hero spotlight or Scattered prints, scored against face detection. One empty grid cell under floating cells now counts as bare canvas.
- 2026-10-31: Multi-View, not released. Surprise me deals a large set from a deck, so every picture gets a turn; it had been shrinking to the pictures on screen after the first click.
- 2026-10-31: v0.25.0, internal. Surprise me.
- 2026-10-31: Multi-View, not released. Surprise me options in the wheel: designs, zoom on faces up to 5x, pictures from a large set, keep my canvas.
- 2026-10-31: Multi-View, not released. Surprise me: the design tick-list is gone, and the options become a card of ranges and odds. Slanted panels and Circles. Pictures from one occasion, skipping screens and repeats, Feature in Surprise me, soft and dark pictures kept from the best place, and auto-play with a fade. Its interval is saved with the other settings, and only a click on the canvas, Esc or Space pauses it.
- 2026-10-31: Multi-View, not released. Each auto-play layout is its own undo step. Saved framing is no longer wiped while a picture decodes, and Undo can no longer get stuck on a layout whose picture loaded slowly.
- 2026-10-31: v0.26.0, internal. Surprise me options, new designs, featuring, auto-play, and the framing and Undo fixes.
- 2026-10-31: v0.26.0, released. Everything since v0.24.0.
- 2026-11-01: Arrow keys in the preview no longer stop working after a tile click. The preview never took focus, so the tile behind it took the key.
- 2026-11-01: v0.26.1, internal. Arrow keys in the preview after a tile click.
- 2026-11-01: Cinematic Play round: pacing, no repeated move, stronger motion, crossfade only, moving through the fade, wide pictures pan, fullscreen and Esc, pause holds, failed pictures skipped. Upload measured at 2 to 35 ms per picture, so left on the UI thread. For Ed to judge by watching.
- 2026-11-01: Cinematic Edit show: one door, picture strip, plain slide panel, Save show and Open show including from the main window, one music track. Removed technique, beats and curve, and the viewer's unused slideshow timer. Not run: a real mouse, a real drag-and-drop on the strip, audible music, a real song file.
- 2026-11-01: v0.27.0, internal. Cinematic Play round, Edit show, Save and Open show, one music track.
- 2026-11-01: Music from a folder by rating in Cinematic and Surprise me, set from the options gear with the image editor. Surprise me's bar says Playing or Paused; Esc pauses auto-play. Measured: start about 1.1 s to the picture, idle CPU 0, auto-play over 90 minutes moves between about 370 and 530 MB with handles and threads flat, so no leak.
- 2026-11-01: v0.28.0, released. Everything since v0.26.0.
- 2026-11-01: v0.28.1, internal. Leaving Multi-View in full screen no longer leaves the window stuck small with no frame. Reproduced on 0.28.0 first.
- 2026-11-01: v0.28.2, internal. Opening Multi-View in full screen keeps its saved window size.
- 2026-11-01: Multi-View, not released. Image tray: added images fill the canvas to 16, then 4 float and the rest go in the tray; positions count only the canvas; Move to Tray; Remove cell leaves the canvas smaller and each is its own undo step. Surprise me as one control with one card. Tested in the app: add rule, removal to the last cell, Undo, reopen, hero layout, tray search, 5,000 images. Not run: real drag and drop, Redo, Save/Load through the picker, custom splits, long auto-play with a tray.
- 2026-11-01: Audit of the tray, Surprise me and music. A removed image could return through the Surprise me deck, which only ever added; it now follows the session, and a change to Most takes effect. Letters typed in the tray's search reached the R, H and +/- shortcuts, and with a layout made by Surprise me the arrows in a single opened photo undid layouts instead. Play with fewer than two images claimed to play. Music: a failure was saved into a show as "no music", which outlasted the cause; Use my music gave silence with music off for shows; saving settings restarted the song; the gear's volume missed an open show; two beds could sound at once; settings changed while paused, or before the card was ever opened, were not followed. Code read and built; the deck has a controlled test; not yet run in the app.
- 2026-11-01: Multi-View, not released. The count on the bar opens the image tray, a click there puts the image on the canvas by the add rule, and right-click removes it from the session. Multi-View's labels and About say image. Built only; not yet run in the app.
- 2026-11-01: The audit and tray additions run in the app. It found one more bug: after removing an image from the tray, focus was lost, so Esc and Undo did nothing. Passed: removed images stay out across Surprise me, typing in the tray's search, the bar's count, Move to Tray, Play with one image, arrows and Space on a single opened photo, song steps and the switch while paused, no qualifying songs in auto-play and Cinematic, Use my music, a dragged edge, a tray image into a floating cell, saving through the picker and reopening, 40 s of auto-play at 3 s. Not run: real drag and drop from Explorer, both music beds at once, how the music sounds.
- 2026-11-01: Multi-View, not released. During auto-play the camera moves over each layout, floating cells follow it further and float gently, and layouts change with a crossfade, zoom through, fade through dark or slide. One Motion slider, the rest folded under Camera and transitions. The bar no longer comes back with every layout. Watched frame by frame in the app. That found four bugs before they shipped: a one-frame shrunken flash at each change, a crash on tilted floating cells, images left soft by XAML transforms, and lost tilt. It now moves composition visuals: CPU fell from 12.3% to 3.5% of all cores on the same show. A pause matches the layout re-applied from Undo. Left: about 40 ms pause while the next layout builds under its cover; aim is at a cell, not the face. Not run: Motion with real audio, or more than a few minutes.
- 2026-11-01: v0.29.0, released. Everything since v0.28.0: image tray, Surprise me as one control, camera motion and transitions, settings cards, the main dialog button on the right.
- 2026-11-01: v0.29.1, internal. When Cinematic and auto-play both want music, the one silenced carries on when the other stops. The rule has a controlled test. The sound itself was not checked.
- 2026-11-01: v0.29.2, internal. A full screen button in the Surprise me control, with Cinematic's symbols.
- 2026-11-01: v0.29.2, released. The full screen button and silenced music carrying on. Neither was checked by hand before release: the button was built and the app started, and the music rule has a test.
- 2026-11-01: v0.29.3, internal. Audit fixes: re-read published cache entries, HUD/menu detach activates without tear-off drag state, empty floating cells survive Undo/Redo and layout load, tray images brought to a full canvas survive duplicate paths, Multi-View Solo skips tray images and updates zoom HUD on double-click, floating cell top-right buttons inset clear of corner resize grip, dark presenter style on Save/Load row menus, and stroke style disposal.
- 2026-11-02: v0.29.4 and v0.29.5, internal. Shared face readings across Auto-Mix, camera aimed at faces, the sharpest of a burst, lookahead decoding; Auto-Mix Settings with a range slider for duration and one soundtrack section.
- 2026-11-02: Storyboard. `ShowProject` and `ShowDirector` make a show of scenes from the images; a drawer holds the image tray, now the Image Pool, and the storyboard, with scene cards, reordering, camera and hold badges, and Play Show.
- 2026-11-03: v0.29.7 to v0.29.9, internal. Storyboard toolbar and cards, Camera Director (a start and end frame per scene), storyboards in Save/Load and Autosaved, the playbar shows the playback mode, canvas edits stored into the scene on show.
- 2026-11-04: Animated GIFs play again in the viewer and detached windows. A scene is written only while it is on the canvas; Re-draft asks before it replaces a show; Play reads Pause while running.
- 2026-11-04: Line endings normalised in four Multi-View files that had a blank line after every line, or CR CR LF.
- 2026-11-04: v0.29.10, internal. Camera Director: Cancel and Esc leave a scene as it was, and F11 works there. Multi-View's B, R, H, + and - ignore Ctrl and Alt, so Ctrl+B is the sidebar. A `.mivshow` opens from Explorer, the command line or a drop. Checked in the app, each against the previous build.
- 2026-11-04: v0.29.11, internal. One Storyboard | Auto-Mix switch that only chooses what Play plays; a drawer button that names its tab and count; Undo and Redo on the bar; scene cards drawn in the scene's own split, with named footer buttons; Image Pool, images and Auto-Mix across the app; no emoji, radio items in menus. Removed scenes come back with Undo. Checked in the app; drags not tried.
- 2026-11-04: v0.29.12, internal. Undo did nothing while a storyboard scene was on the canvas: storing the canvas into the scene stamped the show as modified each time, so each Undo took a new step. Fixed; Undo also goes back past the first scene and past Replace show. Card clicks and Camera Director no longer change the mode. The Back button is named; the selected mode is the drawer's blue. Each checked in the app against the previous build.
- 2026-11-04: v0.30.0, released. Everything since v0.29.2: the storyboard, Camera Director, the mode switch and drawer, Undo on the bar, and the fixes above.
- 2026-11-04: v0.30.1, internal. Add new layout replaces Update scene on the Storyboard toolbar: one new scene from images not in the show, without replacing it.
- 2026-11-04: v0.30.2, internal. Single images have Fill, Matted and Whole odds in Advanced Settings. Play storyboard from the sidebar and the selection bar starts after two scenes and keeps drafting. Camera Director outlines faces and has Aim at face and Exact. Fixed: storing a scene of floating prints dropped its empty grid cell, so the next show put a pool image behind the prints and stored it into the scene; Undo with the drawer open lost the selected scene; the Image Pool count went stale; the play button read "Pause, Pause" to a screen reader; tiles and scene cards read as type names. Checked in the app with the real mouse and keys; 341 unit tests pass.
- 2026-11-04: v0.30.3, internal. Closing the window could log a NullReferenceException from the title-bar theme handler after the title bar was gone (seen once in the crash log, from v0.13.0 code). Guarded. Three launch-and-close runs logged nothing, but the old build only showed it once, so the fix is not proven by a repeat.
