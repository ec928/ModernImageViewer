# 🚀 Modern Image Viewer (Public Beta)

A high-performance, hardware-accelerated image viewer built for creators, 3D artists, and power users. Designed specifically to handle massive directories of high-resolution images with zero UI lag and unique windowing capabilities.

## ✨ Key Features

**Tear-Off Viewports** — Instantly detach any image into its own borderless, floating window. Ideal for secondary monitors, or overlaying reference images while working in Blender, Photoshop, or AI tools.

**GPU-Accelerated Rendering** — Powered by Win2D (Direct2D) for near-instant rendering and smooth scaling, offloading heavy image decoding to the graphics card.

**Massive Folder Support** — A tiered-decoding architecture allows seamless browsing of folders containing thousands of high-resolution (10 MB+) images without stuttering or unbounded memory growth. Preview, display, and high-fidelity tiers are capped (see `docs/ARCHITECTURE.md`).

**Native Windows 11 Design** — Built on the Windows App SDK (WinUI 3) for a clean, modern aesthetic with Mica & Acrylic materials, Light/Dark/System themes, and 3 quick-recall window & tile size presets (`Ctrl+1`, `Ctrl+2`, `Ctrl+3`).

**Comic Book Reader (`.cbz` / `.cbr`) & Dual View Modes** — Browse folders in responsive Tile View or high-density multi-column List View with an instant metadata/thumbnail inspector, and read `.cbz`/`.cbr` comic archives natively with 2-page spread mode (`B`).

**Picks up where you left off** — The app reopens the folder you last had open, or Pictures if that folder can't be reached. When a file changes in the folder you're viewing, only that tile changes. Press **F1** for the keyboard shortcuts, and **F6** to reach the header buttons from the keyboard.

**Favourite Folders** — Star up to 10 folders in the Favourites and Recent Folders menu to keep them above your 10 most recent folders. A favourite stays until you remove it, even when its drive is unplugged, and Clear Recent Folders leaves favourites alone.

**Multi-View & Storyboard Presentations** — Compare up to 16 images in one window or craft dynamic presentations. Build grids and freeform collages, arrange scenes in the Storyboard drawer, orchestrate custom Ken Burns camera moves with the visual Camera Director, and save shows as `.mivshow` projects. Layouts can be exported as high-resolution images or reopened at any time. See [Multi-View](#-multi-view) below.

**Hardware-Accelerated Animated GIFs** — Instant multi-frame GIF playback directly in GPU memory with interactive playback controls (play, pause, frame-by-frame stepping) across single viewer and multi-view canvases.

**Cinematic (beta)** — A slideshow that varies its pace and camera moves by itself, with full screen and music: one file, or random songs from a folder at or above a star rating. **Edit show** lets you reorder, hide, remove and duplicate pictures and set where the camera goes on a slide. Save a show and open it again from the main window. It is still being hardened; save often.

**Music and image editor** — Both are set from the gear in the main window. **Music**: choose a folder and the lowest star rating, 3.5 to start, and unrated songs are left out unless you include them. Ratings are read from MP3 and FLAC tags, half-stars included; M4A and WMA count as unrated. Songs play in random order during Cinematic shows and Auto-Mix, and each of those can be turned off. If both are playing, the one started last is heard, and when it stops the other carries on. A saved show can have its own music, which comes first. **Image editor**: the program **Edit Image** opens a picture in, Paint to start.

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

Select two or more images in the tile grid (Ctrl+click or Shift+click), then click one of them or press **Enter**. Clicking an image outside the selection opens that image on its own, and the selection is still there when you come back. The grid shows up to 16 images. When more are selected, the rest go in the **Image Pool**, and the drawer button on the bar counts them, for example **Image Pool (7)**. The multi-view window keeps its own size, separate from the normal window.

The bar appears when you move the mouse:

| Control | What it does |
|---|---|
| **Back** / **Esc** | Back to the tiles. Your layout stays while the app is open: the Multi-View button at the top returns to it. Choosing images and **View Together**, or opening a layout, replaces it, and the old one is kept under **Autosaved** in Save/Load |
| **Add images** | Find images in your folders using the app itself. Click the ones you want, then press **Add images** or **Enter**, or press **Add this folder** or **With subfolders**, which leaves out files that are only in the cloud. Below 16 images on the canvas, floating cells included, they fill any empty cells and then join the grid as new cells up to 16, and the grid evens out its lines. The rest go in the Image Pool. At 16 or more, the first 4 become floating cells and the rest go in the Image Pool. Images already in the session are skipped. The bar says what will happen before you press. **Esc**, or the close button with nothing selected, returns to the layout without adding |
| **3 Rows** / **Columns** | The switch on the bar turns the grid, and the side in use shows how many lines. Either side opens the grid layout card, and the side in use closes it again. The layout inspector and playbar stay as an immersive dark glass HUD over the images. **−** and **+** at the top of the card set how many lines. Each line is drawn as that many images, with the count on the bar: rows are bands, columns are stacks from the left. A line's **−** and **+** move one image. **+** takes it from the last other line that has more than one, and **−** moves it to the last line, or to the line before when that line is already the last. **Even out** puts the lines back to an even split when they differ. **R** flips rows and columns. The **−** and **+** keys change how many lines. **Hero** in the card picks **Off**, **Banner**, **Rectangle** or **Pair**, and **↻** moves it while one is on: a banner or pair goes round the top, right, bottom and left sides. **Banner** is one large image along a side. **Rectangle** is one large image in a corner block, with 4, 5, 6, 7 or 9 images. **Pair** is the first two images large, with 3 or more. **H** steps through them. |
| **Storyboard** / **Auto-Mix** | The mode switch: what **Play** plays. **Storyboard** walks the storyboard's scenes in order, and is greyed out until there is one. **Auto-Mix** arranges the images into a new design each time: Edge to edge, Gallery wall, Hero spotlight (three or more images), Scattered prints or Circles (three to nine), or Slanted panels (three to six). Choosing a side changes nothing on the canvas. In Auto-Mix, **Next** (›) makes a new design now, and **Previous** (‹) or **Undo** goes back. Auto-Mix tries dozens of versions of a design and avoids any that cut or cover a face, or mostly hide an image. Faces are found by Windows on this PC and are not stored, and a face turned away may not be found. A new design replaces the arrangement, frames, shapes and canvas, and can be edited and saved like any other layout. With more images than a design may use (16, or the **Most each click** you set), each one uses the next few of them, the Image Pool included, and all of them get a turn before any comes round again. Anything left out of a layout stays in the Image Pool. An image you remove with **Remove cell** stays out. Right-click an image for **Feature in Auto-Mix**: it is always used and gets the best place. With fewer than two images, **Play** and **Next** open **Add images** |
| **Play**, the options button beside it and full screen | The full screen button, also **F11**, works while a show plays, and pressing it again goes back to the window. **Play** shows a new layout every few seconds, with a fade between them and your music if it is set, and the button then shows the speed and lights up. The camera also moves slowly over each layout as one picture, a Ken Burns effect, zooming or drifting, and floating cells follow it a little further and float gently. Layouts change with a crossfade, and sometimes a zoom through, a fade through dark or a slide. Saved layouts and Undo never see that movement, and pausing brings the whole layout back. It starts only when you ask. A click on the canvas, **Esc**, **Space** or the button pauses it, and that click does nothing else. **Space** starts it again, and **Next** while Auto-Mix plays gives a new layout at once. The gear button opens the **Auto-Play options card**, which stays open until you click away or press **Esc**; closing it leaves auto-play running. The card provides: **Transport**: `‹ Layout` and `Layout ›` step through history without pausing, alongside `Play`/`Pause`. **Images**: quick presets for `Solo 1`, `Mix 1–4`, `Collage 2–6`, or `Wall 9–16`. **Duration**: a dual-thumb range slider (e.g., `10s – 15s`) unifying fixed and random intervals; drag the span or double-click to collapse to a single duration. **Smart pace**: automatically extends dwell time on dense collages and shortens solo shots. **Motion**: slider for camera drift and zoom intensity. **Soundtrack**: in-card toggle, volume slider, track title, and song stepping. **Advanced auto-play settings** (in-place drill-down): categorized expanders for `Images & faces` (images per scene, in the same boxes as the first page; face zoom & frequency, subject margins, grouping), `Designs` (frequency and toggles for all 6 layout recipes, version variety), `Camera & transitions` (camera mode: Auto, Zoom in, Zoom out, Drift; zoom vs drift slider, and Transitions: odds for crossfade, zoom through, fade through black and slide, with the mix they make shown underneath), and `Look` (tilt, hero size as a range, gallery gaps, card borders, Keep my canvas). |
| **Canvas** | Aspect ratio: 16:9, 16:10, 21:9, 4:3, 1:1 or 3:4, or resize the window for any other. Surface and color: what shows behind the cells. **Frame & Border**: a line, rounded corners, a margin and a shadow around the whole canvas |
| **Save/Load** (**Ctrl+S**) | Copy the canvas to the clipboard (**Ctrl+C**), or save it as a JPEG or PNG at 720p to 4320p, at the window's size, or at a custom size. Save the layout as a `.mivlayout` file and reopen it later. Image sizes are on the left of the menu, layouts on the right, with your favourite and recent folders. A folder with more layouts than fit shows **+N**, which opens the file picker |
| **Wheel** (advanced) | **Undo** (**Ctrl+Z**), **Redo** (**Ctrl+Y**), and **Fit whole images** or **Fill cells**: Fit shows the whole of each image, Fill crops each image to its cell. **Reset canvas and images** starts again with four empty cells and a plain canvas; Undo brings your layout back, and it is also kept under **Autosaved** in Save/Load |

**The Image Pool** holds the session's images that are not on the canvas. It is saved with a layout, Undo keeps it, and Auto-Mix draws from it. To bring an image to the canvas, right-click a cell and choose **Swap with** > **Not on the canvas**, or the same item on an empty cell, and pick from the thumbnails, which you can search by name. **Move to Image Pool** on a cell's menu takes its image off the canvas and keeps it. The drawer button on the bar opens the Image Pool without choosing a cell: clicking an image there puts it on the canvas, as a new cell or, with 16 already there, floating. Right-click an image in the Image Pool to remove it from the session. **Remove cell**, the **X** on a cell, takes the image out of the session: the canvas gets one smaller, nothing from the Image Pool takes its place, and **Undo** brings it back.

With a Banner or Pair, the rows, columns and per-line counts arrange the other images. A Rectangle does not follow those lines, so changing the line count or a line's count turns it off.

On the images themselves:

- Drag the edge between two images to resize them; double-click an edge to make the images on either side equal. Changing how many lines there are, or how many images are in a line, puts those sizes back to even. Switching between rows and columns keeps them, except beside a banner or pair, where the other images are laid out again.
- Scroll to zoom, drag to pan, middle-click to reset, double-click to zoom in or back out.
- Hover over an image, or open its right-click menu, to show its buttons at the top right: view it on its own, or remove the cell.
- **Alt+drag** an image onto another to swap the two.
- Right-click a cell to swap, copy or move its image to another cell, set its frame or shape, replace it, or clear it. **Clear cell** takes the image out and keeps the cell, so its space stays empty on the canvas and in an export. **Replace image** also recovers a cell whose file is missing. **Shape** changes the cell from a rectangle. **Replace image** and **Clear cell** are under **Image**, which also rotates, mirrors and flips the image. The corners a shape leaves show the canvas, and a click there still reaches the cell.
- The maximise button at the top right shows one image on its own. **←** / **→** step through the images, skipping empty cells; **Esc** returns to the grid.

A cell keeps its own settings. **Swap with**, **Copy to** and **Move to** each open a table of the other positions, with two columns:

- **Contents only** changes just the image a cell shows. Its frame, zoom and rotation stay, and the new image takes them on. **Alt+drag** works this way too.
- **With attributes** brings the source cell's frame, image rotation and mirroring, and zoom and pan along with the image. It brings the cell's shape too. The cell's own rotation comes only between two floating cells, because a grid cell cannot turn.

**Move to** replaces the image in the target. A floating cell then disappears, and a grid cell is left empty.

Either way, each remaining cell stays where it is at its own size and layer. **Replace image** works like Contents only: find an image using the app itself, starting in the folder of the current one, and click it. The cell keeps its settings and shows the new image. **Esc** returns without changing it.

**Frame & Border** opens beside the cell, or against the window's far edge when the cell is too big to sit beside. It sets the line, corners, gutter, mat and shadow. Flush removes them all. Hold a preset button to save the current frame there, and click it to use it again. The last swatch in each color row opens the Windows color picker.

The same card under **Canvas** frames the whole canvas. As in a cell, the gutter is on the outside and the mat fills it, then the line, then the grid. The shadow falls from the grid onto the gutter, so raising it adds a gutter if there is none. Floating cells stay where they are and can sit over the margin. Corner radius rounds the line and the grid inside it, and the canvas and mat stay square. It is saved in the layout and included in an export.

### Floating cells

Right-click a cell and choose **Freeform** to float its image above the grid. The image moves to a new cell, which starts with the same frame, and the original cell is left empty. Click an empty cell, or right-click it and choose **Add image**, to find an image using the app itself: click one, or press **Enter** on it, and it goes straight into that cell, or press **Esc** to return without adding. Copy to can also fill it.

Drag images in from File Explorer to add them, and they follow the same rule as **Add images**: into the empty cells and new grid cells up to 16, then the Image Pool, with the first 4 floating where you drop them on a canvas that already has 16. Drop onto an empty cell and the first image goes into that cell, which keeps its settings. The caption on the drag says which you will get. Folders, comic books, saved layouts and storyboards open as they do from anywhere else.

- Drag to move, and drag the edges or corners to resize. A cell can hang over the edge of the canvas as long as its centre stays on it, and whatever hangs over is left out of an export. **Ctrl+drag** pans the image inside the cell. Dragged cells snap to the canvas, each other and the grid; hold **Alt** to move freely.
- **Shape** sets the shape: rectangle, squircle, circle or oval, arch (tombstone), L-shape, triangle, right triangle, rhombus (diamond), trapezoid, parallelogram, hexagon, or organic blob. It also mirrors and flips the shape.
- Hover over a floating cell for its handles. A dashed outline shows the rectangle that resizes, whatever the shape, with a white square at each corner to drag. The round handle at the top rotates it: hold **Shift** for 15 degree steps, or **Ctrl** to turn the shape and keep the image still. On an L-shape, the small handle moves the inner corner.
- **Layer** sets which floating cell is in front. Floating cells stay above the grid.
- **Dock to Grid** puts the image back into its original cell, or a new cell if that one is gone or in use. On a grid that already has 16 cells it uses an empty one, and with none empty the menu offers only **Move to Image Pool**. Closing a floating cell removes its image; Undo brings it back.

Floating cells keep their share of the canvas when the window changes size. Save/Load lists your last three sessions under **Autosaved**, from when you left or replaced them or closed the app. Save a layout to keep it for good.

### Storyboard Presentations & Camera Director

Multi-View pairs procedural Auto-Mix playback with a rich Storyboard presentation engine:
- **The drawer**: the button on the playbar opens it on the tab it was last left on and names that tab: `Image Pool (N)`, the images not on the canvas, or `Storyboard (N)`, the scenes. **B** opens it on the Storyboard tab. Make a show from the Image Pool, add the layout on the canvas as a scene, reorder scenes by dragging their cards, duplicate or remove them, and play the show (**Play Show**). Each card is drawn in its scene's own arrangement. Removing a scene does not ask: **Ctrl+Z**, or **Undo** on the bar, brings it back.
- **Camera Director (Visual Keyframe Editor)**: click the camera and hold badge on a scene card and choose **Edit camera path…**, or the camera button on the card. Pan and zoom (1.0x to 2.5x) the green Start and red End frames, with a live 2.5-second preview. Faces found in the scene are outlined; **Aim at face** centres the selected frame on one (each click moves to the next), and **Exact…** takes typed zoom and position. **Done** or **Enter** keeps the path; **Cancel** or **Esc** leaves the scene as it was.
- **Growing a show**: **Add new layout** makes one new scene at the end from images not in the show yet, without replacing the others.
- **Play storyboard**: from the sidebar (the whole folder) or the selection bar (the selected images). It starts playing once two scenes are made and keeps adding scenes until every image is in the show.
- **Cinematic storyboard**: a storyboard that looks like Cinematic Slideshow: one image at a time, full screen, with the camera moving and crossfades. Change how it looks in Layout Generator Settings > Advanced. Add new layout and Replace show keep it Cinematic. To go back to your main settings, turn off **Use for this storyboard** there, press Clear, or start a new storyboard with Play storyboard.
- **Single images**: Layout Generator Settings > Advanced > Designs sets the odds that a one-image layout fills the screen, is matted, or is shown whole and uncropped.
- **Editing a scene**: while a scene is on the canvas, moving, resizing or turning a floating cell and dragging an edge between images are kept in that scene, and so are other changes when you play, pause or move to another scene. A scene is only changed while it is the one on the canvas; **Update scene from canvas**, on a card's right-click menu, copies the canvas into that scene. **Undo** steps back through these changes.
- **Storyboards & Layouts**: Save a storyboard as a `.mivshow` file, or a single layout as a `.mivlayout` file, from **Save/Load** (**Ctrl+S**), where storyboards are listed first in each folder. Both also open from File Explorer or by dropping them on the window.

## ⌨️ Shortcuts

| | |
|---|---|
| **F1** | Every keyboard shortcut |
| **F6** | Move to the header buttons: **←** / **→** or **Tab** move along, **Enter** presses, **Esc** returns |
| **Ctrl+1 / 2 / 3** | Recall saved window & tile size preset 1, 2, or 3 |
| **Enter** | Open the picture, or view the selected pictures together |
| **Space** | Select or deselect the picture in the folder view; play or pause an animation in the viewer |
| **B** | Toggle 2-page comic spread in the viewer |
| **F2** / **F5** | Rename / refresh |
| **Backspace** | Up one folder |
| **F11** | Fullscreen |
| **← / →** | Previous / next image |
| **Home / End** | First / last image |
| **Delete** | Send to the Recycle Bin, or warn first where that isn't possible |
| **Esc** | Close the preview |
| **Space** / **Esc** in Multi-View | Pause the storyboard or Auto-Mix. **Space** starts it again |
| **← / →** in Multi-View | While playing, the previous or next scene or layout |
| **Tab** | Show or hide the sidebar (tile grid and multi-view) |
| **B** in Multi-View | Open the drawer on the Storyboard tab, or close it |
| **Ctrl+S** in Multi-View | Open Storyboards & Layouts menu |
| **Ctrl+Z / Ctrl+Y** in Multi-View | Undo / redo layout and scene edits |

The sidebar also opens when the pointer reaches the left edge.

## 💬 Feedback

This is an active public beta. If you encounter bugs, performance issues, or have feature requests, please open a ticket in the [Issues](https://github.com/ec928/ModernImageViewer/issues) tab — or use **Report a problem** in the app's About dialog, which opens the same place.
