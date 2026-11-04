# Multi-image screen

What the multi-image screen shows, taken from the source. The copy that is run day to day lives outside this source tree and can differ until it is deployed.

`docs/UI_UX.md` section 6 is an older description. Where they disagree, this file is the authoritative reference.

The bar hides after 2.5 seconds without a real pointer move. While Play is running, a new layout does not bring the bar back. A pointer move, a key, or a click does. A progress line, when enabled, stays along a screen edge while the bar is hidden.

## Playbar

One pill along the bottom, left to right. Dividers separate the groups. Rows and Columns, and the divider in front of them, are hidden when fewer than two photos are on the grid.

| Control | Words | What it does |
|---|---|---|
| Back | Back | Leaves multi-image for the tiles. Esc does the same when no card is open. The layout stays, and Multi-View returns to it. |
| Story Deck | Story Deck, or Story Deck (N) | Opens or closes the Storyboard drawer. N is the number of scenes. Filled with an accent border while Storyboard is the playback mode. |
| Auto-Mix | Auto-Mix | The procedural playback mode. Clicking it chooses Auto-Mix and generates a new procedural layout. Filled while Auto-Mix is the active mode. |
| Previous | icon | Previous scene while a storyboard is playing. Otherwise previous layout. Left arrow / PageUp. |
| Play | Play Show, Play Show · i/N, Play Mix, or Pause | Starts or pauses. Stopped on a storyboard: `Play Show`, or `Play Show · i/N` when resuming past the first scene. Stopped on Auto-Mix: `Play Mix`. While either is running, the label is `Pause`. Space does the same. Filled while running. |
| Next | icon | Next scene, or next layout. Right arrow / PageDown. |
| Play options | chevron | Opens or closes Auto-Mix Settings. |
| Rows | Rows, or N Rows | Arranges the grid in rows and opens the grid layout card. R switches between rows and columns. B, R, H, + and − work only without Ctrl or Alt held, so Ctrl+B still toggles the sidebar. |
| Columns | Columns, or N Columns | Arranges the grid in columns. Same card and same R key. |
| Full screen | icon | Full screen toggle (F11). |
| Canvas | Canvas | Aspect ratio, surface, color, and canvas frame options. |
| Save/Load | Save/Load | Save or open layouts (`.mivlayout`) and storyboards (`.mivshow`). Ctrl+S opens it. |
| Advanced | gear | Undo, redo, fill or fit, and reset options. |

Playback Mode (Story Deck and Auto-Mix) sits as a dedicated 2-segment mode selector. The Transport group (previous, Play, next, options) follows as a centralized control zone. Rows and Columns are their own group after playback. Full screen sits with Canvas and Save/Load.

### Which mode is active

A storyboard plays by default once scenes exist. Auto-Mix plays when there is no storyboard, or after Auto-Mix has been clicked. Exactly one of those two buttons is active, including while playback is paused. While nothing is running, Play reads `Play Show`, `Play Show · 4/33`, or `Play Mix`. While something is running, Play reads `Pause`.

Clicking Story Deck switches the playback target back to Storyboard mode. If the drawer is already open, clicking Story Deck closes it. If closed, it opens the drawer.

## Image pool and storyboard drawer

A slide-up bottom tray with two tabs and Close. Esc closes the drawer. A click outside closes it.

The tabs read:
- `🗃 Pool (N)`
- `🎬 Storyboard (N)`

### Image pool

Photos that are in the session pool but not currently on the canvas.

- Heading: `N not on the canvas.` Clicking one puts it on the canvas.
- Search box: Filters pool images by file name.
- Add photos: Adds more photos from a folder to the pool.
- Thumbnails: Right-click to remove a photo from the session pool.
- Empty states inform the user how to move pictures to/from the pool.

### Storyboard Drawer

When no scenes exist, an empty-state illustration prompts the user to generate scenes with Auto-Draft or add the current canvas as the first scene.

When scenes exist, the drawer top toolbar provides a compact, non-overflowing control set:

| Control | Label | What it does |
|---|---|---|
| Play Show | `▶ Play Show` | Starts playback from the active scene and locks Storyboard mode. |
| Add Layout | `+ Add Layout` | Appends current canvas layout and photos as a new scene at the end of the show. |
| Update Scene | `💾 Update Scene` | Saves current canvas layout and photo positions into the selected storyboard scene. |
| Auto-Draft | `✨ Auto-Draft` | Drafts scenes from the images. When scenes already exist the button reads Re-draft and asks before it replaces them. |
| Draft Range | `(min–max) ▾` | Compact flyout button to adjust the minimum and maximum images per scene (1 to 16). |
| Summary | `N scenes · length` | Displays total scenes and estimated show runtime. |
| Clear | `Clear` | Confirmation dialog to clear all scenes from the show. |

### Scene Cards

Each scene card in the horizontal filmstrip displays:
- Multi-photo composite thumbnail preview (up to 4 sub-tiles for multi-photo layouts).
- Scene order badge: `#1`, `#2`, etc.
- Motion & Duration badge: e.g. `Pan · 12s`, `Zoom In · 8s`, `Still · 12s`.
- Recipe / layout subtitle in the card footer.
- Drag-and-drop reordering: Drag any card horizontally to reorder scenes.

#### Card Interactions & Menus
- **Single-click card**: Opens and displays that scene on the canvas immediately.
- **Double-click card**: Displays scene on canvas and closes the drawer for unobstructed viewing.
- **Left-click Motion/Duration badge**: Opens the quick Scene Timing & Motion flyout:
  - Concrete motions: `Zoom In`, `Zoom Out`, `Pan Across`, `Still (No Motion)`
  - Dynamic runtime mode: `✨ Dynamic (Auto at Playback)`
  - Duration presets: `5s`, `8s`, `12s`, `16s`, `20s`, `30s` (ticks current value; immediately updates badges)
  - Visual Keyframe Editor: `🎯 Edit Camera Path…`
- **Right-click card context menu**:
  - `▶ Play from Here`: Starts storyboard presentation from this scene.
  - `💾 Update Scene from Canvas`: Syncs current canvas edits into this scene.
  - `🎬 Camera Motion…`: Concrete camera motions and Dynamic mode.
  - `⏱ Duration…`: Quick duration adjustment (5s to 30s).
  - `Duplicate Scene`: Clones the scene and inserts it after.
  - `Remove Scene`: Deletes the scene from the show.
  - `Move Earlier` / `Move Later`: Shifts scene position.

## Camera Director (Visual Keyframe Editor)

Launched via `🎯 Edit Camera Path…` from any scene card badge or context menu. Closes chrome and bottom HUD to provide an unobstructed full-canvas visual keyframe editor.

### Toolbar Controls
Floating at the top of the canvas, strictly elevated above all cards:
- **Scene Steppers**: `◀` and `▶` to step between scenes with header `🎬 Scene #4 of 33 · Title`.
- **Keyframe Mode Pills**: `🟢 Start` (emerald green) and `🔴 End` (ruby coral).
- **Preview Move**: Plays a live 2.5-second Ken Burns camera transition between Start and End keyframes.
- **Reset**: Reverts camera keyframes to default framing.
- **Cancel**: Leaves without keeping this scene's changes. Esc does the same. A scene left with the steppers has already kept its path.
- **Done**: Commits the custom camera keyframes and returns to the storyboard. Enter does the same.

While it is open, Space previews, Tab switches Start and End, Left and Right step scenes, F1 still lists the keys, and F11 still toggles full screen. Other keys do nothing, so Undo and Save/Load cannot change the canvas under the frames.

### Wireframe Editing
- **Interactive Wireframes**: Green rectangular wireframe indicates camera Start; red wireframe indicates camera End.
- **Panning**: Drag anywhere inside the active wireframe box to reposition the camera center.
- **Zooming**: Scroll the mouse wheel over the wireframe to zoom in/out (from 1.0x to 2.5x).
- **Superimposed Keyframe Priority**: When Start and End have identical zoom and position (e.g. Still motion), clickable top-level badges (`🟢 Start · 1.0x` and `🔴 End · 1.25x`) at Z-index 300 remain elevated above both boxes. Clicking either badge instantly selects that keyframe.
- **Visual Dominance**: The active keyframe has full opacity (1.0), a prominent 3.5px border, and drag priority; the inactive keyframe is softly dimmed (0.55 opacity, 1.8px border).

## Canvas Layout Auto-Sync

When viewing a storyboard scene on the canvas, any manual layout adjustment automatically syncs to the scene in memory:
- Dropping/moving a card commits the new coordinate layout and creates an undo restore point.
- Resizing a freeform card handle commits the new size.
- Rotating a card commits the new rotation angle.
- Dragging a mosaic grid splitter commits the updated row/column weights.
- Play, pause, and leaving a scene write the canvas back only when that scene is the one on the canvas. A mix, or a scene marked selected before it was shown, is not written over the scene. Update Scene still stores the canvas when asked.
- Full undo history (`Ctrl+Z` / `Ctrl+Y`) is preserved across scene navigation and editing.

## Save / Load (Storyboards & Layouts)

Opened via `Save/Load` or `Ctrl+S`:
- Section header: **Storyboards & Layouts**.
- **Save Storyboard as…**: Exports current presentation as `.mivshow` JSON project.
- **Open Storyboard…**: Opens a saved `.mivshow` file. A `.mivshow` also opens from Explorer, the command line or a drop on the window. That replaces the session, which goes to Autosaved.
- **Recent & Favourites Folders**: Scans for both `.mivlayout` and `.mivshow` files, automatically prioritizing `.mivshow` storyboards at the top of folder lists with filmstrip icons (`🎬`) and blue `Show` badges.
- **Autosaved Sessions**: Displays both image count and storyboard scene metadata: `181 images · 🎬 33` with full scene restore tooltips.

## Animated GIF Playback

- **Single Viewer**: Animated GIFs automatically decode all animation frames into GPU memory on open.
- **Playback Controls**: Displays interactive animation HUD (`[◀] [⏸] [▶]  1 / N`), supporting Space (play/pause), Left Arrow (previous frame), and Right Arrow (next frame).
- **Multi-View Canvas**: Multi-view cells and Solo mode support animated GIF playback with hardware acceleration.
