# 🚀 Modern Image Viewer (Public Beta)

A high-performance, hardware-accelerated image viewer built for creators, 3D artists, and power users. Designed specifically to handle massive directories of high-resolution images with zero UI lag and unique windowing capabilities.

## ✨ Key Features

**Tear-Off Viewports** — Instantly detach any image into its own borderless, floating window. Ideal for secondary monitors, or overlaying reference images while working in Blender, Photoshop, or AI tools.

**GPU-Accelerated Rendering** — Powered by Win2D (Direct2D) for near-instant rendering and smooth scaling, offloading heavy image decoding to the graphics card.

**Massive Folder Support** — A tiered-decoding architecture allows seamless browsing of folders containing thousands of high-resolution (10 MB+) images without stuttering or unbounded memory growth. Preview, display, and high-fidelity tiers are capped (see `ARCHITECTURE.md`).

**Native Windows 11 Design** — Built on the Windows App SDK (WinUI 3) for a clean, modern aesthetic with Mica & Acrylic materials, Light/Dark/System themes, and 3 quick-recall window & tile size presets (`Ctrl+1`, `Ctrl+2`, `Ctrl+3`).

**Comic Book Reader (`.cbz` / `.cbr`) & Dual View Modes** — Browse folders in responsive Tile View or high-density multi-column List View with an instant metadata/thumbnail inspector, and read `.cbz`/`.cbr` comic archives natively with 2-page spread mode (`B`).

**Multi-View** — Compare up to 16 photos in one window. Set how many rows or columns and how many photos sit in each, give one or two photos a hero spot, drag the edges between photos, swap photos with Alt+drag, and save the result as a JPEG. See [Multi-View](#-multi-view) below.

**Collage and Cinematic (beta)** — In-app collage editor, and a cinematic slideshow whose show you can edit: reorder, hide and duplicate slides, hold a Ken Burns move on one slide, and save or reload the show. These are still being hardened; save often.

HEIC/HEIF and AVIF need the corresponding Windows codecs installed. If a file will not open, the viewer shows a short HUD message rather than a blank canvas.

## 🛠 Technical Stack

| | |
|---|---|
| Framework | C# / .NET 8 |
| UI | WinUI 3 (Windows App SDK) |
| Graphics | Win2D (Direct2D) |
| Architecture | Asynchronous tiered decoding with reference-counted GPU resources for VRAM efficiency |

Supported formats: JPEG, PNG, BMP, GIF, WebP, HEIC/HEIF, AVIF, CBZ, CBR.

## 📦 Installation (Portable)

Distributed as a self-contained portable app — no installation or registry changes are required.

1. Download the latest `.zip` from the [Releases](https://github.com/ec928/ModernImageViewer/releases) page.
2. Extract the folder to your preferred location.
3. Run `ModernImageViewer.exe`.

**Windows SmartScreen**: because this is an unsigned indie beta, Windows Defender may flag it. Click **More info → Run anyway** to launch. This is standard for indie software that has not been signed with an expensive corporate certificate. You can verify the safety of the application by reviewing the source code provided in this repository.

## 🔨 Building from Source

Requires the .NET 8 SDK and the Windows App SDK workload (Visual Studio 2022, "Windows application development").

```
git clone https://github.com/ec928/ModernImageViewer.git
cd ModernImageViewer
dotnet build -c Release -p:Platform=x64
```

To produce a portable build, run `publish.bat`. It wraps the same publish profile Visual Studio uses, then sanity-checks the output and launches the result briefly to confirm it starts.

```
publish.bat            # publish, then smoke-test
publish.bat nosmoke    # publish only
```

Exit codes: `0` success, `1` publish failed, `2` output looks wrong, `3` app crashed on launch.

> `publish.bat` is the supported ship path (relative output under `bin\x64\Release`). Visual Studio `.pubxml` files now use the same relative `PublishDir`. Pass an extra folder argument to `publish.bat` if you want a different destination.

> Deliberately **not** published as a single file. Single-file self-extracts ~277 MB to `%TEMP%` on the first launch after every publish, measured at ~2.8 s to first window versus ~1.5 s for loose files.

## 🖼 Multi-View

Select two or more photos in the tile grid (Ctrl+click or Shift+click), then click one of them or press **Enter**. Multi-view shows up to 16 photos. When more are selected, the bar shows how many are on screen, for example **16 / 23**. The multi-view window keeps its own size, separate from the normal window.

The bar appears when you move the mouse:

| Control | What it does |
|---|---|
| **Grid** / **Esc** | Back to the tile grid |
| **Fill / Fit** (**F**) | Fill crops each photo to its frame; Fit shows the whole photo |
| **Grid Layout** (**3 Rows**) | Opens the layout card. **Rows** and **Columns** turn the grid, and the stepper beside them sets how many lines. Each line is drawn as that many photos: rows are bands, columns are stacks from the left. A line's stepper moves one photo. **+** takes it from the last other line that has more than one, and **−** moves it to the last line, or to the line before when that line is already the last. **Even out** puts the lines back to an even split when they differ. **R** flips rows and columns. The **−** and **+** keys change how many lines. |
| **Hero** (**H**) | Make the first photo large. Click again for the next kind, and again after the last to turn the hero off: **Banner** (one large photo along a side), **Rectangle** (one large photo in a corner block, with 4, 5, 6, 7 or 9 photos) and **Pair** (the first two photos large, with 3 or more) |
| **↻** (next position) | Move the hero: a banner or pair goes round the top, right, bottom and left sides |
| **Reset** | Undo the zoom and pan of every photo |
| **16:9** | Canvas shape: 16:9, 16:10, 21:9, 4:3, 1:1 or 3:4. Resize the window for any other shape |
| **Save** (**Ctrl+S**) | Save the grid as a JPEG at 720p to 4320p, at the window's size, or at a custom size |

With a Banner or Pair, the rows, columns and per-line counts arrange the other photos. A Rectangle does not follow those lines. Changing the line count or a line's count turns that hero off.

On the photos themselves:

- Drag the edge between two photos to resize them; double-click an edge to make the photos on either side equal. Changing how many lines there are, or how many photos are in a line, puts those sizes back to even. Switching between rows and columns keeps them, except beside a banner or pair, where the other photos are laid out again.
- Scroll to zoom, drag to pan, middle-click to reset, double-click to zoom in or back out.
- Hover over a photo to show its buttons at the top right. The move arrows move it one place earlier or later; they are left out on a very small photo.
- **Alt+drag** a photo onto another to swap the two.
- Right-click a photo to move it, swap it with any other, view it on its own, or remove it from the view.
- The maximise button at the top right shows one photo on its own. **←** / **→** step through the photos; **Esc** returns to the grid.

## ⌨️ Shortcuts

| | |
|---|---|
| **Ctrl+1 / 2 / 3** | Recall saved window & tile size preset 1, 2, or 3 |
| **B** | Toggle 2-page comic spread in the viewer |
| **F11** | Fullscreen |
| **← / →** | Previous / next image |
| **Home / End** | First / last image |
| **Delete** | Send to the Recycle Bin |
| **Esc** | Close the preview |
| **Tab** | Show or hide the sidebar (tile grid and multi-view) |

The sidebar also opens when the pointer reaches the left edge.

## 💬 Feedback

This is an active public beta. If you encounter bugs, performance issues, or have feature requests, please open a ticket in the [Issues](https://github.com/ec928/ModernImageViewer/issues) tab — or use **Report a problem** in the app's About dialog, which opens the same place.
