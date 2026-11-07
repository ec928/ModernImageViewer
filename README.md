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

**Shows** — **Play show**, in the sidebar, the selection bar, the folder menu and the image viewer, plays the pictures as a show in Multi-View, with full screen and music. The **Cinematic** preset on the Show card shows one image at a time with slow camera moves, faces framed by the eyes and long dissolves. Cinematic Slideshow, the separate window, was removed in 0.33.0; shows saved from it no longer open.

**Music and image editor** — The folder and the lowest star rating are set from the gear in the main window. **Music**: 3.5 stars to start, and unrated songs are left out unless you include them. Ratings are read from MP3 and FLAC tags, half-stars included; M4A and WMA count as unrated. Songs play in random order during Multi-View shows, which can be turned off. On a show, **Soundtrack** on the Show card can land each change on the beat, hold a loud passage shorter and a quiet one longer, and cut straight through when the song suddenly gets louder. **Image editor**: the program **Edit Image** opens a picture in, Paint to start.

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

Select two or more images in the tile grid (Ctrl+click or Shift+click), then click one of them or press **Enter**. Clicking an image outside the selection opens that image on its own, and the selection is still there when you come back. The grid shows up to 16 images. When more are selected, the rest go in the **Image Pool**, and the **Drawer** button on the bar counts them, for example **Drawer (7)**, when it opens on the Image Pool. The multi-view window keeps its own size, separate from the normal window.

The bar appears when you move the mouse:

| Control | What it does |
|---|---|
| **Back** / **Esc** | Back to the tiles. Your layout stays while the app is open: the Multi-View button at the top returns to it. Choosing images and **View Together**, or opening a layout, replaces it, and the old one is kept under **Autosaved** in Save/Load |
| **Drawer** | Opens or closes the drawer on the tab you last used, the **Image Pool** or the **Show**, with that tab's count. **B** opens it on the Show |
| **Add images** | Find images in your folders using the app itself. Click the ones you want, then press **Add images** or **Enter**, or press **Add this folder** or **With subfolders**, which leaves out files that are only in the cloud. Below 16 images on the canvas, floating cells included, they fill any empty cells and then join the grid as new cells up to 16, and the grid evens out its lines. The rest go in the Image Pool. At 16 or more, the first 4 become floating cells and the rest go in the Image Pool. Images already in the session are skipped. The bar says what will happen before you press. **Esc**, or the close button with nothing selected, returns to the layout without adding |
| **3 Rows** / **Columns** | The switch on the bar turns the grid, and the side in use shows how many lines. Either side opens the grid layout card, and the side in use closes it again. The layout inspector and playbar stay as an immersive dark glass HUD over the images. **−** and **+** at the top of the card set how many lines. Each line is drawn as that many images, with the count on the bar: rows are bands, columns are stacks from the left. A line's **−** and **+** move one image. **+** takes it from the last other line that has more than one, and **−** moves it to the last line, or to the line before when that line is already the last. **Even out** puts the lines back to an even split when they differ. **R** flips rows and columns. The **−** and **+** keys change how many lines. **Hero** in the card picks **Off**, **Banner**, **Rectangle** or **Pair**, and **↻** moves it while one is on: a banner or pair goes round the top, right, bottom and left sides. **Banner** is one large image along a side. **Rectangle** is one large image in a corner block, with 4, 5, 6, 7 or 9 images. **Pair** is the first two images large, with 3 or more. **H** steps through them. |
| **Play** and full screen | **Play** plays the show. Scenes are made as it plays and kept, so you can step back through them. While it runs the button reads **Pause** and is filled. The full screen button, last on the bar, also **F11**, works while a show plays, and pressing it again goes back to the window. The camera also moves slowly over each scene, zooming or drifting, and floating cells follow it a little further and float gently. Scenes change with a crossfade, and sometimes a zoom through, a fade through dark or a slide, each taking from 0.75 to 2.5 seconds (**Transition length**, in Engine settings). A big moment in the music can cut straight through. Saved layouts and Undo never see that movement, and pausing brings the whole layout back. It starts only when you ask. A click on the canvas, **Esc**, **Space** or the button pauses it, and that click does nothing else. **Space** starts it again. **Next** goes to the next scene. |
| **Show** | Opens the **Show** card. Presets are Multi-Image, Cinematic, 1, 2 and 3: press to use one, hold or right-click to keep this show's values in it, and the reset symbol puts it back. The card also has images per scene, duration, smart pace, motion, sequence beats, scenes per step, design odds, **Soundtrack**, and **Engine settings…** (`Images & faces`, `Designs`, `Camera`, `Transitions`, `Canvas & progress`). Play always plays the show. |
| **Canvas** | Aspect ratio: 16:9, 16:10, 21:9, 4:3, 1:1 or 3:4, or resize the window for any other. Surface and color: what shows behind the cells. **Frame & Border**: a line, rounded corners, a margin and a shadow around the whole canvas. **Fit whole images** or **Fill cells**: Fit shows the whole of each image, Fill crops each image to its cell. **Reset canvas and images**, at the end, starts again with four empty cells and a plain canvas; Undo brings your layout back, and it is also kept under **Autosaved** in Save/Load |
| **Save/Load** (**Ctrl+S**) | Copy the canvas to the clipboard (**Ctrl+C**), or save it as a JPEG or PNG at 720p to 4320p, at the window's size, or at a custom size. Save the layout as a `.mivlayout` file and reopen it later. Image sizes are on the left of the menu, layouts on the right, with your favourite and recent folders. A folder with more layouts than fit shows **+N**, which opens the file picker |
| **Undo** / **Redo** | **Ctrl+Z** and **Ctrl+Y**. Removing a scene, a cell or a layout never asks first; Undo brings it back |

**The Image Pool** holds the session's images that are not on the canvas. It is saved with a layout, Undo keeps it, and a show is made from it. To bring an image to the canvas, right-click a cell and choose **Swap with** > **Not on the canvas**, or the same item on an empty cell, and pick from the thumbnails, which you can search by name. **Move to Image Pool** on a cell's menu takes its image off the canvas and keeps it. The drawer button on the bar opens the Image Pool without choosing a cell: clicking an image there puts it on the canvas, as a new cell or, with 16 already there, floating. Right-click an image in the Image Pool to remove it from the session. **Remove cell**, the **X** on a cell, takes the image out of the session: the canvas gets one smaller, nothing from the Image Pool takes its place, and **Undo** brings it back.

With a Banner or Pair, the rows, columns and per-line counts arrange the other images. A Rectangle does not follow those lines, so changing the line count or a line's count turns it off.

On the images themselves:

- Drag the edge between two images to resize them; double-click an edge to make the images on either side equal. Changing how many lines there are, or how many images are in a line, puts those sizes back to even. Switching between rows and columns keeps them, except beside a banner or pair, where the other images are laid out again.
- Scroll to zoom, drag to pan, middle-click to reset, double-click to zoom in or back out. A cell that gets smaller, as when the window does, shows your pan trimmed to fit, and brings it back when it grows again.
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

- Drag to move, and drag the edges or corners to resize. A cell can hang over the edge of the canvas as long as its centre stays on it, and whatever hangs over is left out of an export. **Ctrl+drag** pans the image inside the cell. Dragged cells snap to the canvas, each other and the grid; hold **Alt** to move freely. When the canvas changes size or shape, a floating cell keeps its centre's place and its own shape, scaled to fit, and its image's pan scales with it.
- **Shape** sets the shape: rectangle, squircle, circle or oval, arch (tombstone), L-shape, triangle, right triangle, rhombus (diamond), trapezoid, parallelogram, hexagon, or organic blob. It also mirrors and flips the shape.
- Hover over a floating cell for its handles. A dashed outline shows the rectangle that resizes, whatever the shape, with a white square at each corner to drag. The round handle at the top rotates it: hold **Shift** for 15 degree steps, or **Ctrl** to turn the shape and keep the image still. On an L-shape, the small handle moves the inner corner.
- **Layer** sets which floating cell is in front. Floating cells stay above the grid.
- **Dock to Grid** puts the image back into its original cell, or a new cell if that one is gone or in use. On a grid that already has 16 cells it uses an empty one, and with none empty the menu offers only **Move to Image Pool**. Closing a floating cell removes its image; Undo brings it back.

Floating cells keep their share of the canvas when the window changes size. Save/Load lists your last three sessions under **Autosaved**, from when you left or replaced them or closed the app. Save a layout to keep it for good.

### Storyboard Presentations & Camera Director

Multi-View plays a show. The scenes are made as it plays and kept.
- **The drawer**: the button on the playbar opens it on the tab it was last left on and names that tab: `Image Pool (N)`, the images not on the canvas, or `Show (N)`, the scenes. **B** opens it on the Show tab. Reorder scenes by dragging their cards, duplicate or remove them. **Add this layout** keeps the images on the canvas as a scene. **New show** starts an empty show with the preset on the Show card; Play then makes its scenes. Each card is drawn in its scene's own arrangement. Removing a scene does not ask: **Ctrl+Z**, or **Undo** on the bar, brings it back.
- **Camera Director (Visual Keyframe Editor)**: click the camera and hold badge on a scene card and choose **Edit camera path…**, or the camera button on the card. Pan and zoom (1.0x to 2.5x) the green Start and red End frames, with a live 2.5-second preview. Faces found in the scene are outlined; **Aim at face** centres the selected frame on one (each click moves to the next), and **Exact…** takes typed zoom and position. **Done** or **Enter** keeps the path; **Cancel** or **Esc** leaves the scene as it was.
- **Camera movement**: Motion is a range, so each scene drifts its own amount; **Camera zoom** sets how far each zoom goes. Engine settings, behind the Show card, set two-shot scenes (a wide shot, then a snap zoom or a cut to a face), odds for six movement curves (Steady, Gentle, Smooth, Cinematic, Settle, Snap), a curved path, and rotation. A zoom or close view aimed at a person is framed by the eyes, a third of the way down the screen; on a picture with no face it is centred on the focus point. Camera Director edits each shot's frames, curve and turn.
- **Play show**: from the sidebar (the whole folder) or the selection bar (the selected images). It uses the preset chosen on the Show card, starts once the first scene is made, and keeps adding scenes. While it is paused, drafting stops once every image has been dealt once.
- **Cinematic**: a preset on the Show card. One image at a time, Edge to edge most of the time, Hero spotlight rarely, the other designs off. It took the place of Cinematic Slideshow, the separate window removed in 0.33.0.
- **The Show card**: **Show** on the bar. Presets are one line: Multi-Image, Cinematic, 1, 2 and 3. Press one to use it. Hold or right-click to keep this show's values in it. The reset symbol on the right puts that preset back. What you change is kept for the next Play of that preset. The values are images per scene, duration, smart pace, motion, sequence beats, scenes per step and each design's odds. **Engine settings…** is how one layout and one move are made. **Soundtrack** is the music for this show.
- **Sequence beats**: **Sequence beats** (on the Show card, default 25%) is the chance that a step of Play or of a drafted show is more than one scene, each a closer view of the one before: a group, then one image from it (the featured one, or one with faces more likely), then its faces, largest first. **Scenes per step** (2 to 4, default 2) is how many; a step stops early when no closer view is left. Each is an ordinary kept scene with its own hold from Duration, so stepping back shows every one. A view of the same picture keeps its layout and comes in by a cut, either snapping in from where the last scene ended or opening on the face; no view goes past where the face would take 85% of the screen. Inside such a step no scene plays a two-shot.
- **One image**: A layout of one image goes through the design odds like any layout. **Crop at most** (Images & faces, default 35%) is how much of it a layout may crop away to fill its frame, measured against the frame's shape; a face is never cut past **Face kept in frame**. Past either, Edge to edge shows the image whole at full height or width on a tinted background, Gallery wall shows it whole in its card, and Scattered prints already shows it whole. Hero spotlight, Circles and Slanted panels can only crop, so they sit that layout out and another design is used. At the default, a circle comes up for one image only when it is within about 6:5 of square, since a circle alone hides about a fifth of its box. A layout of one image is not zoomed when it is made: it opens on its fit or fill, and **Zoom** is where its one camera move arrives. On Auto, **Zoom frequency** decides how often the move zooms in or out to the Zoom amount, toward the faces or, with none, the focus point; otherwise it is a Camera zoom or a drift. With the camera on Zoom in or Zoom out, every scene zooms to the Zoom amount. No camera view of one image goes past where a face would take more than 85% of the screen or the photo would show past twice its pixels; a two-shot with no room to snap in is not played.

### Layout Generator: how it decides, and what is open

The screen is described in `docs/MULTI_IMAGE_UI.md`. The design, including what is still open, is `docs/MULTI_IMAGE_DESIGN.md`. As built today, every choice comes from a setting:

| What | Setting | Default |
|---|---|---|
| Images in a layout | Images per scene: any count in the range, equally likely | 1 to 9 |
| Design | Design odds; a design set to Off is never used. With too many images for the designs left on, the layout takes fewer | All Normal |
| One image | The design odds, as any layout. Crop at most decides whether it fills or is shown whole; a design that can only crop sits that layout out | 35%; Cinematic storyboard Edge to edge Often, Hero spotlight Rare, the rest Off |
| Layout zoom | Zoom range and Zoom frequency, every image, toward faces or the focus point | 1.5× to 4×, 70% |
| Camera | Motion (drift), Camera zoom (zoom depth), Zooms rather than drifts, Aims at faces, Two-shot scenes, curves, curved path, rotation | 40–60%, 1.15–1.8×, 50%, 70%, 25% |

**Limits that remain, and why:**
- Crop at most applies to a layout of one image. A layout of several images still fills every cell.
- Circles and Scattered prints take at most 9 images, Slanted panels 6. Past those they crowd.
- A zoom stops where it would cut a face (faces take at most 85% of the cell, or of the screen for one image's camera move) or show the photo past twice its pixels.
- Two-shot scenes need a scene on screen 8 seconds or more: two shots in a short scene read as a flip-book.
- Camera zoom goes to 2.5×, the most Camera Director can edit.

**Odds still fixed in code, with no setting:** Edge to edge without gaps 1 time in 3; Gallery wall rounded corners 1 time in 3; a drift goes sideways 3 times in 4; in Scattered prints a featured image's print, and in Circles the boldest image's circle, is 1.5× larger; an image counts as mostly hidden at 60% covered; a screenshot is an image named so or with 15% or more flat color.

**Direction:** the foundation is this multi-image engine. Cinematic Slideshow was removed in 0.33.0, once its framing and long dissolves had been brought across; the Cinematic preset is its place. Sequence beats are on the Show card, default 25%. Cutting on the music is on Soundtrack on that card. The design is `docs/MULTI_IMAGE_DESIGN.md`.

**Checked by unit tests, not yet watched on screen:** two-image Circles, Scattered prints, Slanted panels and Hero spotlight; portraits at full height with zoom; the Camera zoom range during playback.
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
| **Space** / **Esc** in Multi-View | Pause the show. **Space** starts it again |
| **← / →** in Multi-View | While playing, the previous or next scene or layout |
| **Tab** | Show or hide the sidebar (tile grid and multi-view) |
| **B** in Multi-View | Open the drawer on the Storyboard tab, or close it |
| **Ctrl+S** in Multi-View | Open Storyboards & Layouts menu |
| **Ctrl+Z / Ctrl+Y** in Multi-View | Undo / redo layout and scene edits |

The sidebar also opens when the pointer reaches the left edge.

## 💬 Feedback

This is an active public beta. If you encounter bugs, performance issues, or have feature requests, please open a ticket in the [Issues](https://github.com/ec928/ModernImageViewer/issues) tab — or use **Report a problem** in the app's About dialog, which opens the same place.
