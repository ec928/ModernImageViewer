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
