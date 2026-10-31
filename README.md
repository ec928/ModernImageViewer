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
| **Back** / **Esc** | Back to the tiles. Your layout stays while the app is open: the Multi-View button at the top returns to it. Choosing photos and **View Together**, or opening a layout, replaces it, and the old one is kept under **Autosaved** in Save/Load |
| **Add images** | Find pictures in your folders using the app itself. Click the ones you want, or press **Enter** on each, then press **Add to layout** or **Enter**: each becomes a new floating cell. **Esc**, or the close button with nothing selected, returns to the layout without adding |
| **3 Rows** / **Columns** | The switch on the bar turns the grid, and the side in use shows how many lines. Either side opens the grid layout card, and the side in use closes it again. The layout inspector and playbar stay as an immersive dark glass HUD over the photos. **−** and **+** at the top of the card set how many lines. Each line is drawn as that many photos, with the count on the bar: rows are bands, columns are stacks from the left. A line's **−** and **+** move one photo. **+** takes it from the last other line that has more than one, and **−** moves it to the last line, or to the line before when that line is already the last. **Even out** puts the lines back to an even split when they differ. **R** flips rows and columns. The **−** and **+** keys change how many lines. |
| **Hero** (**H**) | The button reads **Hero** when it is off, and **Hero: Banner**, **Hero: Rectangle**, or **Hero: Pair** while that kind is on. Click for the next kind, and again after the last to turn it off. **Banner** is one large photo along a side. **Rectangle** is one large photo in a corner block, with 4, 5, 6, 7 or 9 photos. **Pair** is the first two photos large, with 3 or more |
| **↻** (next position) | Shown while a hero is on. Moves it: a banner or pair goes round the top, right, bottom and left sides |
| **Canvas** | Shape: 16:9, 16:10, 21:9, 4:3, 1:1 or 3:4, or resize the window for any other. Surface and color: what shows behind the cells. **Frame & Border**: a line, rounded corners, a margin and a shadow around the whole canvas |
| **Save/Load** (**Ctrl+S**) | Copy the canvas to the clipboard (**Ctrl+C**), or save it as a JPEG or PNG at 720p to 4320p, at the window's size, or at a custom size. Save the layout as a `.mivlayout` file and reopen it later. Image sizes are on the left of the menu, layouts on the right, with your favourite and recent folders. A folder with more layouts than fit shows **+N**, which opens the file picker |
| **Wheel** (advanced) | **Undo** (**Ctrl+Z**), **Redo** (**Ctrl+Y**), and **Fit whole photos** or **Fill cells**: Fit shows the whole of each photo, Fill crops each photo to its cell |

With a Banner or Pair, the rows, columns and per-line counts arrange the other photos. A Rectangle does not follow those lines. Changing the line count or a line's count turns that hero off.

On the photos themselves:

- Drag the edge between two photos to resize them; double-click an edge to make the photos on either side equal. Changing how many lines there are, or how many photos are in a line, puts those sizes back to even. Switching between rows and columns keeps them, except beside a banner or pair, where the other photos are laid out again.
- Scroll to zoom, drag to pan, middle-click to reset, double-click to zoom in or back out.
- Hover over a photo, or open its right-click menu, to show its buttons at the top right: view it on its own, or remove the cell.
- **Alt+drag** a photo onto another to swap the two.
- Right-click a cell to swap, copy or move its photo to another cell, set its frame or shape, replace it, or clear it. **Clear cell** takes the photo out and keeps the cell, so its space stays empty on the canvas and in an export. **Replace image** also recovers a cell whose file is missing. **Window** gives the cell a shape. **Replace image** and **Clear cell** are under **Image**, which also rotates, mirrors and flips the photo. The corners a shape leaves show the canvas, and a click there still reaches the cell.
- The maximise button at the top right shows one photo on its own. **←** / **→** step through the photos, skipping empty cells; **Esc** returns to the grid.

A cell keeps its own settings. **Swap with**, **Copy to** and **Move to** each open a table of the other positions, with two columns:

- **Contents only** changes just the photo a cell shows. Its frame, zoom and rotation stay, and the new photo takes them on. **Alt+drag** works this way too.
- **With attributes** brings the source cell's frame, image rotation and mirroring, and zoom and pan along with the photo. It brings the window shape too. The window's rotation comes only between two floating cells, because a grid cell cannot turn.

**Move to** replaces the photo in the target. A floating cell then disappears, and a grid cell is left empty.

Either way, each remaining cell stays where it is at its own size and layer. **Replace image** works like Contents only: find a picture using the app itself, starting in the folder of the current one, and click it. The cell keeps its settings and shows the new picture. **Esc** returns without changing it.

**Frame & Border** opens beside the cell, or against the window's far edge when the cell is too big to sit beside. It sets the line, corners, gutter, mat and shadow. Flush removes them all. Hold a preset button to save the current frame there, and click it to use it again. The last swatch in each color row opens the Windows color picker.

The same card under **Canvas** frames the whole canvas. As in a cell, the gutter is on the outside and the mat fills it, then the line, then the grid. The shadow falls from the grid onto the gutter, so raising it adds a gutter if there is none. Floating cells stay where they are and can sit over the margin. Corner radius rounds the line and the grid inside it, and the canvas and mat stay square. It is saved in the layout and included in an export.

### Floating cells

Right-click a cell and choose **Freeform** to float its photo above the grid. The photo moves to a new cell, which starts with the same frame, and the original cell is left empty. Right-click an empty cell and choose **Add image** to find a picture using the app itself: click one, or press **Enter** on it, and it goes straight into that cell, or press **Esc** to return without adding. Copy to can also fill it.

Drag pictures in from File Explorer to add them: each one becomes a new floating cell where you drop it, up to 16 at a time and with a message if there are more, in front of the others and with no frame. Drop onto an empty cell and the picture goes into that cell instead, which keeps its settings; with several pictures, the first goes in and the rest float. The caption on the drag says which you will get. No other cell changes. Folders, comic books and saved layouts open as they do from anywhere else.

- Drag to move, and drag the edges or corners to resize. A cell can hang over the edge of the canvas as long as its centre stays on it, and whatever hangs over is left out of an export. **Ctrl+drag** pans the photo inside the cell. Dragged cells snap to the canvas, each other and the grid; hold **Alt** to move freely.
- **Window** sets the shape: rectangle, squircle, circle or oval, arch (tombstone), L-shape, triangle, right triangle, rhombus (diamond), trapezoid, parallelogram, hexagon, or organic blob. It also mirrors and flips the shape.
- Hover over a floating cell for its handles. A dashed outline shows the rectangle that resizes, whatever the shape, with a white square at each corner to drag. The round handle at the top rotates it: hold **Shift** for 15 degree steps, or **Ctrl** to turn the shape and keep the photo still. On an L-shape, the small handle moves the inner corner.
- **Layer** sets which floating cell is in front. Floating cells stay above the grid.
- **Dock to Grid** puts the photo back into its original cell, or a new cell if that one is gone or in use. Closing a floating cell removes its photo; Undo brings it back.

Floating cells keep their share of the canvas when the window changes size. Save/Load lists your last three sessions under **Autosaved**, from when you left or replaced them or closed the app. Save a layout to keep it for good.

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
