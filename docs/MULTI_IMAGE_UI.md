# Multi-image screen

What the multi-image screen shows, taken from the source. The copy that is run day to day lives outside this source tree and can differ until it is deployed.

`docs/UI_UX.md` section 6 is an older description. Where they disagree, this file is the screen.

The bar hides after 2.5 seconds without a real pointer move. While Play is running, a new layout does not bring the bar back. A pointer move, a key, or a click does. A progress line, when it is on, stays on a screen edge while the bar is hidden.

## Playbar

One pill along the bottom, left to right. Dividers separate the groups. Rows and Columns, and the divider in front of them, are hidden when fewer than two photos are on the grid.

| Control | Words | What it does |
|---|---|---|
| Back | Back | Leaves multi-image for the tiles. Esc does the same when no card is open. The layout stays, and Multi-View returns to it. |
| Image Pool | Image Pool, or Image Pool (N) | Opens or closes the image pool. N is every photo in the pool. A light plate shows while the pool tab is open. |
| Storyboard | Storyboard, or Storyboard (N) | One of the two playback modes, and the button that opens the storyboard. N is the number of scenes. B does the same. Filled while Storyboard is the mode. |
| Auto-Mix | Auto-Mix | The procedural playback mode. Clicking it chooses Auto-Mix and generates one new layout. Filled while Auto-Mix is the active mode. |
| Previous | icon | Previous scene while a storyboard is playing. Otherwise the previous layout, which pauses play. Left arrow. |
| Play | Play Show, Play Mix, or Pause | Starts or pauses. Play Show when a storyboard is the mode and play is stopped. Play Mix when Auto-Mix is the mode and play is stopped. Pause while something is running. Space does the same. Filled while running. |
| Next | icon | Next scene, or the next layout. Right arrow. |
| Play options | chevron | Opens or closes Auto-Mix Settings. |
| Rows | Rows, or N Rows | Arranges the grid in rows and opens the grid layout card. R switches between rows and columns. The side in use has a light plate, brighter while the card is open. |
| Columns | Columns, or N Columns | Arranges the grid in columns. Same card and same R key. |
| Full screen | icon | Full screen. The tooltip says F11. |
| Canvas | Canvas | Aspect ratio, surface, color, and the canvas frame. |
| Save/Load | Save/Load | Save the picture, or save and open a layout. Ctrl+S opens it. |
| Advanced | gear | Undo, redo, fill or fit, and reset. |

Playback Mode (Storyboard and Auto-Mix) sits in its own dedicated 2-segment pill. The Transport group (previous, Play, next, options) follows as a centralized control zone. Image Pool is its own shelf group before playback. Rows and Columns are their own group, after playback. Full screen sits with Canvas and Save/Load.

### Which mode is filled

A storyboard plays by default once scenes exist. Auto-Mix plays when there is no storyboard, or after Auto-Mix has been chosen. Exactly one of those two buttons is filled, including while play is paused. Play is filled only while something is running.

Choosing Storyboard again is the Storyboard button, Play Show inside the storyboard, or the Storyboard chip in Auto-Mix Settings. Opening the storyboard from a closed panel pauses play, except when that click is the switch away from Auto-Mix while play is already running. Then play keeps going and the next picture is a scene.

Making the first show selects Storyboard. Replacing a show that already exists leaves an Auto-Mix choice in place. Clearing the storyboard, clearing the session, or opening a saved storyboard returns play to the storyboard.

## Image pool and storyboard

One centered card. The header is two tabs and Close. Esc closes the card. A click outside the card closes it, except a click on Image Pool or Storyboard, which switches or toggles the panel.

The tabs read `Image Pool (N)` and `Storyboard` or `Storyboard (N)`.

### Image pool

Photos that are not on the canvas.

- Heading: `N not on the canvas.` Clicking one puts it on the canvas. Opened from an empty cell or from Swap with, the click fills that position instead, and the heading says so.
- Search names.
- Add photos. Same as adding from a folder. Photos fill the canvas up to 16. The rest stay off the canvas. On a full canvas, the first 4 go on top of it.
- Thumbnails. The tooltip is the file name. Right-click: Remove from image pool.
- Right-click a photo to remove it.
- Empty, with photos already on the canvas: All photos are on the canvas. The text says to use Move to Image Pool, or to add more photos.
- Empty, with no photos: No photos yet. Add photos from a folder to start.
- A search with no match: No photo matches that name.

### Storyboard

When there are no scenes, the button row is hidden. The center says `Makes a show from these photos, one screen at a time.` with Make a show and Use this layout as the first scene. The summary line is `No scenes yet` or `No scenes yet · N photos`.

When scenes exist, the summary is `N scenes · length`, and `· N not in the show` when some pool photos are not in any scene. Length under a minute is `Ns`. Otherwise `M min` or `M min Ss`.

The button row, when scenes exist:

| Button | What it does |
|---|---|
| + Add N new photos | Shown only when some photos are not in the show. Adds scenes for those photos. |
| Add this layout | The photos on screen become a new scene. |
| Replace show | Makes a new show from the pool and replaces the scenes. While there are no scenes this button says Make a show, and it lives in the center instead. |
| Play Show | Plays the scenes in order and selects Storyboard. |
| Clear | Asks first. The title is `Clear the storyboard?` Clear removes the scenes. Cancel is the default, so Enter does not clear. |

Under the buttons: `Images per scene:` with two boxes, from 1 to 16, and the word `to` between them. The same range is used by Auto-Mix. While a show is being made, a line reads `Reading the photos...`, `Reading N new photos...`, or `Making scene i of n...`.

A scene card is 176 by 136. One photo fills the card. More than one photo uses up to four tiles, and a `N photos` badge appears only when the scene has more than four. The corner is `#1`, `#2`, and so on. Beside it is the motion and the hold, such as `• Still · 12s`, `⤢ Zoom`, `⤡ Out`, `⇄ Pan`, or `⤢ Move`. The footer is the layout name, Duplicate scene, and Remove scene. Cards can be dragged into a new order.

Click a card to show that scene on the canvas and leave the panel open. Double-click shows it and closes the panel. Click the hold time for 5s, 8s, 12s, 16s, 20s, or 30s. The current one is ticked.

Right-click a card:

- Apply to Canvas & Close
- Preview on Canvas
- Hold time, with the same six times
- Remove Scene
- Move Earlier, except on the first card
- Move Later, except on the last card

## Auto-Mix Settings

Opened from the chevron in the playback group. Esc closes it. A click outside it closes it.

The first page:

| Row | Control |
|---|---|
| Title | Auto-Mix Settings, and a gear that opens the advanced page |
| Play | Storyboard and Auto-Mix. Storyboard is disabled until a scene exists. The choice matches the playbar and takes effect on the next picture. |
| Images per scene | Two boxes, 1 to 16 |
| Duration | A range from 3s to 30s. Thumbs together are one hold. Thumbs apart are a random hold. |
| Smart pace | With a range, denser layouts hold longer. |
| Motion | How far the camera moves, from Off to 100% |
| Soundtrack | Background music, with the folder summary under the label. Volume. The current song, Previous song, and Next song. |
| | Advanced auto-play settings… |

The advanced page starts with Back and the title Advanced Settings. One section is open at a time.

**Images & faces.** Fewest each click. Most each click. Same folder and date. Skip screens and repeats. Zoom on faces. Face zoom frequency. Uncut face shown.

**Designs.** Chips: All, Clean grids, Framed, Scrapbook. Then one slider each for Edge to edge, Gallery wall, Hero spotlight, Scattered prints, Slanted panels, and Circles. Each slider runs Off, Rare, Less, Normal, More, Often. Version variety runs from Best to Top 12.

The Clean grids chip names Hero and companions, Balanced mosaic, and Filmstrip. Those three names are not the six sliders.

**Camera & transitions.** Auto, Zoom in, Zoom out, Drift. Zooms rather than drifts. Aims at faces. Transition variety. At 0 the change is a crossfade. Above that, a change can zoom through, fade through dark, or slide.

**Soundtrack & audio.** Music library, the folder path, and Configure… which opens the app music settings. Background music for this play.

**Look.** Gallery card: Any, White, Black, From the pictures. Gallery gaps. Hero size. Most tilt. Keep my canvas. Play progress. Edge: Bottom, Top, Left, Right. The default edge is Bottom. A bottom or top line fills from the left. A side line fills from the top. Effect color: Accent, White, Amber, Aqua, Rose. Accent follows the Windows accent color.

Reset to defaults… asks `Reset every setting?` with Keep and Reset. Music settings are not reset.

## Grid layout

Opened from Rows or Columns. Clicking the side already in use opens or closes the card. Clicking the other side turns the grid and opens the card. Esc closes it. A press outside the card closes it. The switch stays usable while the card is open.

- Title: Grid layout. Even out makes the lines equal.
- A line under the title says how many images sit across how many rows or columns. With a hero on, it names the hero. Editing the lines turns a hero off when the hero does not use lines.
- Fewer and more rows or columns. Plus and minus do the same.
- A strip for each line, with one fewer or one more photo in that line.
- Hero: Off, Banner, Rectangle, Pair. Rectangle needs 4, 5, 6, 7, or 9 images. Pair needs at least 3. What the count cannot do is greyed out. While a hero is on, Next position cycles where it sits. H cycles the kind, then off.

## Canvas

- Aspect ratio: 16:9, 16:10, 21:9, 4:3, 1:1, 3:4. The current one is ticked. Any other ratio is the window being dragged, and the menu can then read Custom.
- Surface: Solid, Gradient, Vignette.
- Color: Black, White, Grey, Sand, Slate, Navy, Forest, Burgundy, then Custom…
- Frame & Border… opens the frame card for the whole canvas. That card has no Apply to all cells.

## Save/Load

Two columns. The left is Save image. The right is Layouts. A short window scrolls only the image sizes.

Save image, for the current window shape: 720p, 1080p, 1200p, 1440p, 2160p, 4320p, each with the pixel size beside it. This window, when it fits. Custom…. Copy to clipboard (Ctrl+C).

Layouts: Save Layout as…. Open Layout…. Save Storyboard as… when scenes exist. Open Storyboard…. Autosaved, up to three, labeled with the time and the image count. Today is the time of day. Favourites, up to three folders, then Recent. Each folder lists layouts that fit, and a count of the rest. Clear Recent Locations. With nothing saved yet, the layouts column says No recent locations.

## Advanced

- Undo (Ctrl+Z). Also Ctrl+Shift+Z.
- Redo (Ctrl+Y).
- Fill cells, or Fit whole images. The row names the change it will make.
- Reset canvas and images. Four empty cells and a plain canvas. It does not ask. Undo brings the previous canvas back.

## A cell

Hovering a photo shows Solo / Maximize and Remove cell. Solo fills the screen with that photo. The solo bar is Back to Multi-View, previous, `N of M`, next, and Fit to Screen. The corner readout is the file name, size and date, zoom, and HF. Esc, clicking the black around the photo, or the back control returns to the grid. The wheel zooms. A double-click zooms in, or fits again if already zoomed. Middle-click fits.

An empty cell says `Click to add a photo`. When the pool has photos that are not on the canvas, it also says `Right-click for photos not on the canvas`. The words fade with the playbar.

Right-click:

| Item | When | What it does |
|---|---|---|
| Add image… | Empty cell | Pick one photo for this cell. |
| Not on the canvas (N)… | Empty cell, and the pool has photos off the canvas | Opens the pool aimed at this cell. |
| Swap with... | More than one cell | Other positions, in two columns: Contents only, or With attributes. If this cell has a photo and the pool has some off the canvas, that submenu starts with Not on the canvas (N)…. |
| Copy to... | A cell with a photo | Copies the photo to another position. |
| Move to... | A cell with a photo | Copies it, then clears the source. A floating cell disappears. A grid cell is left empty. |
| Frame & Border… | Always | The frame card for this cell. |
| Shape | Always | Rectangle, Squircle, Circle / Oval, Arch / Tombstone, L-shape, Triangle, Right triangle, Rhombus / Diamond, Trapezoid, Parallelogram, Hexagon, Organic blob. Then Mirror shape, Flip shape, and Reset shape. Mirror and Flip are greyed out when they would change nothing. |
| Layer | A floating cell, when more than one floats | Bring to front, Bring forward, Send backward, Send to back. |
| Image | A cell with a photo | Replace image…, Clear cell, Rotate image left, Rotate image right, Mirror image, Flip image. |
| Feature in Auto-Mix | A cell with a photo | Auto-Mix keeps using this photo and gives it the best place. |
| Freeform | A grid cell with a photo | Lifts the photo off the grid, on top of it. |
| Dock to Grid | A floating cell, when the grid has room | Puts it back in the grid. |
| Move to Image Pool | A cell with a photo | Keeps the photo in the pool, not on the canvas. |

Swap with, Copy to, and Move to name positions, not file names. The side of the row under the pointer picks Contents only or With attributes. The target cell shows its position number while the row is pointed at.

Alt and drag swaps two cells. Dragging a grid divider changes that split. On a floating cell, the corner grips resize it. Ctrl and drag pans the photo inside the cell. Alt while moving a floating cell turns snapping off. Ctrl on the rotate handle turns the shape and keeps the photo still. Shift on that handle steps by 15 degrees.

## Frame & Border

Opened from a cell, or from Canvas for the whole canvas. The title is `Frame & Border —` and the target. It is tall enough for its controls and scrolls only when the window is short.

- Flush, Preset 1, Preset 2, Preset 3. Click recalls. Hold saves over that preset. The caption says Saved, then returns.
- Stroke: Solid, Dashed, Muted, Gradient.
- Color, including Clear. Gradient also has End color, including Fade.
- Thickness, 0 to 24 px.
- Corner radius, 0 to 32 px.
- Gutter, 0 to 48 px.
- Mat: None, Solid, Translucent, then the mat colors.
- Shadow, 0 to 100%, and Rim on a rectangle. Rim is a light edge instead of a drop shadow.
- Apply to all cells, on a cell's card only. The canvas card does not have it.

## Keys

| Key | What it does |
|---|---|
| Esc | Closes the open card. While playing, pauses. In solo, returns to the grid. Otherwise Back. |
| Space | Play or pause. Does not step a scene. |
| Left / Right | While playing, previous or next scene, or previous or next layout. |
| B | Storyboard. |
| R | Rows or columns. |
| H | Next hero, then off. |
| + / − | More or fewer lines. |
| Ctrl+Z, Ctrl+Shift+Z, Ctrl+Y | Undo and redo. |
| Ctrl+S | Save/Load. |
| Ctrl+C | Copy the canvas. |
| F11 | Full screen, from the window. The button tooltip names it. |
