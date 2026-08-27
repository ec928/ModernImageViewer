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
| Display / collage high-res / cinematic | 4096 | collage `LoadHighResAsync`, `SceneAnalysisService` | Yes |
| High fidelity | 16384 | `ViewerEngine.DecodeHighFidelityAsync` | Composited then uploaded on UI thread |
| Export | native | collage export | Temporary, disposed after save |

WIC color-manages to sRGB. The draw path does **not** use Win2D `ColorManagementEffect`. Do not allocate a `ColorManagementProfile` unless a draw path consumes it.

## Image cache ownership

`ImageCache` is the only writer of the shared decode cache.

- `Publish(path, entry)` — cache takes the entry (initial refcount 1). Releases any *different* previous occupant. Will not replace a high-fidelity entry with a preview.
- `TryGet` — borrow. The main viewer may draw a cached entry while the key remains in the keep-alive set; it must not `Dispose` fields on that entry.
- `Evict(path)` — `Release` + remove. A detached window that `AddRef`s the same entry keeps it alive until it `Release`s.
- `Rename(old, new)` — move the slot; release anything displaced at `new`.
- Never `dictionary[path] = entry`. Never `Dispose()` GPU fields on a shared entry. Device-lost: `Evict` and re-decode.

Collage keeps a **local** `CanvasBitmap` dictionary (`SetLocalGpu`) bound to that canvas's device. It may `Publish` a preview entry for native width/height, but must not clobber HF.

Refcount: cache hold = 1. Detached windows `AddRef` when they display a cache entry and `Release` on close. `ManageCache` `Evict`s keys outside the keep-alive radius (current ± 20). Prefetch is current +15 / −8.

GPU objects (`CanvasBitmap`, `CanvasRenderTarget`, Win2D effects) are created and disposed on the UI thread that owns the `CanvasDevice`.

## Folder scan

`ScanFolder` increments `_currentScanId`. Every UI marshal that mutates `Images` must capture that id and no-op if it no longer matches. A new scan may `Images.Clear()` only after in-flight batches have been dropped (they check the id).

A debounced `FileSystemWatcher` on `_currentDirectory` re-runs `ScanFolder` when the grid is showing (preview closed). It does not interrupt an open viewer.

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

## Known remaining debt

- Collage and cinematic windows are still large code-behinds. Further splits (`FolderScanService`, director UI vs playback) are welcome but not required for correctness. Tracked as improvement I3 in `REVIEW.md`.
- Collage hit-testing ignores `Rotation` (AABB). Visuals rotate; pointers still use the unrotated layout.
- HEIC/AVIF need the Windows codecs pack. Decode failure shows a HUD.
- Unsigned portable zip: SmartScreen warning is expected.
