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

## Log

- 2026-08-26: Review filed. Fix pass started.
- 2026-08-26: Fix pass landed (see statuses). Remaining: split oversized code-behinds (I3); collage hit-testing still uses unrotated AABB.
- 2026-08-27: Verification pass. Release build clean (0 warnings, 0 errors); 5/5 tests pass. Found and fixed #28 and #29. Removed a stale 5.7 MB `build.log`. Released as v0.10.0.
