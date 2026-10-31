# 🚀 Modern Image Viewer (Public Beta)

A high-performance, hardware-accelerated image viewer built for creators, 3D artists, and power users. Designed specifically to handle massive directories of high-resolution images with zero UI lag and unique windowing capabilities.

## ✨ Key Features

**Tear-Off Viewports** — Instantly detach any image into its own borderless, floating window. Ideal for secondary monitors, or overlaying reference images while working in Blender, Photoshop, or AI tools.

**GPU-Accelerated Rendering** — Powered by Win2D (Direct2D) for near-instant rendering and smooth scaling, offloading heavy image decoding to the graphics card.

**Massive Folder Support** — A tiered-decoding architecture allows seamless browsing of folders containing thousands of high-resolution (10 MB+) images without stuttering or unbounded memory growth. Preview, display, and high-fidelity tiers are capped (see `docs/ARCHITECTURE.md`).

**Native Windows 11 Design** — Built on the Windows App SDK (WinUI 3) for a clean, modern aesthetic with Mica & Acrylic materials, Light/Dark/System themes, and 3 quick-recall window & tile size presets (`Ctrl+1`, `Ctrl+2`, `Ctrl+3`).

**Comic Book Reader (`.cbz` / `.cbr`) & Dual View Modes** — Browse folders in responsive Tile View or high-density multi-column List View with an instant metadata/thumbnail inspector, and read `.cbz`/`.cbr` comic archives natively with 2-page spread mode (`B`).

**Favourite Folders** — Star up to 10 folders in the Favourites and Recent Folders menu to keep them above your 10 most recent folders. A favourite stays until you remove it, even when its drive is unplugged, and Clear Recent Folders leaves favourites alone.

**Multi-View** — Compare up to 16 photos in one window, or build them into a collage. Set the rows and columns, give photos a hero spot, drag the edges between them, frame each cell, float cells above the grid in shapes, and save the result as a JPEG or PNG or copy it to the clipboard. Layouts can be saved and reopened. See [Multi-View](#-multi-view) below.

**Cinematic (beta)** — A cinematic slideshow whose show you can edit: reorder, hide and duplicate slides, hold a Ken Burns move on one slide, and save or reload the show. It is still being hardened; save often.

HEIC/HEIF and AVIF need the corresponding Windows codecs installed. If a file will not open, the viewer shows a short HUD message rather than a blank canvas.

## 🛠 Technical Stack

| | |
|---|---|
| Framework | C# / .NET 8 |
| UI | WinUI 3 (Windows App SDK) |
| Graphics | Win2D (Direct2D) |
| Architecture | Asynchronous tiered decoding with reference-counted GPU resources for VRAM efficiency |

Supported formats: JPEG, PNG, BMP, GIF, TIFF, WebP, HEIC/HEIF, AVIF, CBZ, CBR. A multi-page TIFF opens on its first page.

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

> `publish.bat` is the supported ship path (output directly under `bin\`). Visual Studio `.pubxml` files now use the same relative `PublishDir`. Pass an extra folder argument to `publish.bat` if you want a different destination.

> Deliberately **not** published as a single file. Single-file self-extracts ~277 MB to `%TEMP%` on the first launch after every publish, measured at ~2.8 s to first window versus ~1.5 s for loose files.

## 🖼 Multi-View

Select two or more photos in the tile grid (Ctrl+click or Shift+click), then click one of them or press **Enter**. Multi-view shows up to 16 photos. When more are selected, the bar shows how many are on screen, for example **16 of 23 shown**. The multi-view window keeps its own size, separate from the normal window.

The bar appears when you move the mouse:

| Control | What it does |
|---|---|
| **Grid** / **Esc** | Back to the tile grid. From the comparison this drops the layout. **Esc** in the folder, after **Photos**, returns the photos you left |
| **Photos** | Pick different photos. The layout stays, including dragged edges, the hero, the line counts, Fill or Fit, and each frame's zoom and pan. Your cells' photos start selected. A cell whose photo stays selected keeps it, so your arrangement survives. Deselect a photo and select a new one, and **Enter** or **View Together** puts the new one in that cell. Extra photos wait off screen. If you deselect more than you add, the bar says how many more to pick and you stay in the folder |
| **Fill / Fit** (**F**) | Fill crops each photo to its frame; Fit shows the whole photo |
| **3 Rows** / **Columns** | The switch on the bar turns the grid, and the side in use shows how many lines. Either side opens the grid layout card, and the side in use closes it again. The layout inspector and playbar stay as an immersive dark glass HUD over the photos. **−** and **+** at the top of the card set how many lines. Each line is drawn as that many photos, with the count on the bar: rows are bands, columns are stacks from the left. A line's **−** and **+** move one photo. **+** takes it from the last other line that has more than one, and **−** moves it to the last line, or to the line before when that line is already the last. **Even out** puts the lines back to an even split when they differ. **R** flips rows and columns. The **−** and **+** keys change how many lines. |
| **Hero** (**H**) | The button reads **Hero** when it is off, and **Hero: Banner**, **Hero: Rectangle**, or **Hero: Pair** while that kind is on. Click for the next kind, and again after the last to turn it off. **Banner** is one large photo along a side. **Rectangle** is one large photo in a corner block, with 4, 5, 6, 7 or 9 photos. **Pair** is the first two photos large, with 3 or more |
| **↻** (next position) | Shown while a hero is on. Moves it: a banner or pair goes round the top, right, bottom and left sides |
| **Reset** | Undo the zoom and pan of every photo |
| **16:9** | Canvas shape: 16:9, 16:10, 21:9, 4:3, 1:1 or 3:4. Resize the window for any other shape |
| **Save/Load** (**Ctrl+S**) | Save the canvas as a JPEG or PNG at 720p to 4320p, at the window's size, or at a custom size. Save the layout as a `.mivlayout` file and reopen it later. Image sizes are on the left of the menu, layouts on the right, with your favourite and recent folders. A folder with more layouts than fit shows **+N**, which opens the file picker |
| **Wheel** (advanced) | **Undo** (**Ctrl+Z**), **Redo** (**Ctrl+Y**), **Copy to clipboard** (**Ctrl+C**), **Save as PNG**, and **Canvas**: the surface and color behind the cells |

With a Banner or Pair, the rows, columns and per-line counts arrange the other photos. A Rectangle does not follow those lines. Changing the line count or a line's count turns that hero off.

On the photos themselves:

- Drag the edge between two photos to resize them; double-click an edge to make the photos on either side equal. Changing how many lines there are, or how many photos are in a line, puts those sizes back to even. Switching between rows and columns keeps them, except beside a banner or pair, where the other photos are laid out again.
- Scroll to zoom, drag to pan, middle-click to reset, double-click to zoom in or back out.
- Hover over a photo to show its buttons at the top right. The move arrows move it one place earlier or later; they are left out on a very small photo.
- **Alt+drag** a photo onto another to swap the two.
- Right-click a cell to move it, swap or copy its photo to another cell, set its frame, view it on its own, hide it, or remove it. **Advanced** replaces the photo with a file you choose, which also recovers a cell whose file is missing, and rotates, mirrors and flips the photo.
- The maximise button at the top right shows one photo on its own. **←** / **→** step through the photos, skipping hidden ones; **Esc** returns to the grid.

A cell keeps its own settings. **Swap with** and **Copy to** each open a table of the other positions, with two columns:

- **Contents only** changes just the photo a cell shows. Its frame, zoom and rotation stay, and the new photo takes them on. The move arrows and **Alt+drag** work this way too.
- **With attributes** brings the source cell's frame, image rotation and mirroring, and zoom and pan along with the photo. Between two floating cells it brings the window shape and its rotation as well; a grid cell has no window shape, so with a grid cell on either side the floating cell keeps its own.

Either way, each cell stays where it is at its own size and layer, and a hidden cell stays hidden. **Advanced > Replace image** works like Contents only: the cell keeps its settings and shows the new file.

**Frame & Border** opens beside the cell, or against the window's far edge when the cell is too big to sit beside. It sets the line, corners, gutter, mat and shadow. Flush removes them all. Hold a preset button to save the current frame there, and click it to use it again. The last swatch in each color row opens the Windows color picker.

### Floating cells

Right-click a cell and choose **Freeform** to float a copy of it above the grid. The original cell is hidden, and the new cell starts with its frame.

Drag pictures in from File Explorer to add them: each one becomes a new floating cell where you drop it, up to 16 at a time, in front of the others and with no frame. No other cell is hidden. Folders, comic books and saved layouts open as they do from anywhere else.

- Drag to move, and drag the edges or corners to resize. A cell can hang over the edge of the canvas as long as its centre stays on it, and whatever hangs over is left out of an export. **Ctrl+drag** pans the photo inside the cell. Dragged cells snap to the canvas, each other and the grid; hold **Alt** to move freely.
- **Advanced > Window** sets the shape: rectangle, squircle, circle or oval, arch (tombstone), L-shape, triangle, right triangle, rhombus (diamond), trapezoid, hexagon, or organic blob. It also rotates, mirrors and flips the shape. **Rotate shape only** keeps the photo still.
- Hover over a floating cell for its handles. A dashed outline shows the rectangle that resizes, whatever the shape, with a white square at each corner to drag. The round handle at the top rotates it: hold **Shift** for 15 degree steps, or **Ctrl** to turn the shape and keep the photo still. On an L-shape, the small handle moves the inner corner.
- **Advanced > Layer** sets which floating cell is in front. Floating cells stay above the grid.
- **Dock to Grid** puts the photo back into its hidden cell, or a new cell if that one is gone or in use.

Floating cells keep their share of the canvas when the window changes size. **Grid** at the bottom left of the bar ends the session and clears them, along with any frames and settings, so save the layout first to keep your work.

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
