# Multi-image screen

What the multi-image screen shows, taken from the source. The copy that is run day to day lives outside this source tree and can differ until it is deployed.

`docs/UI_UX.md` section 6 is an older description. Where they disagree, this file is the authoritative reference.

The bar hides after 2.5 seconds without a real pointer move. While Play is running, a new layout does not bring the bar back. A pointer move, a key, or a click does. A progress line, when enabled, stays along a screen edge while the bar is hidden.

## Playbar

One pill along the bottom, left to right. Dividers separate the groups. Rows and Columns, and the divider in front of them, are hidden when fewer than two images are on the grid.

| Control | Words | What it does |
|---|---|---|
| Back | Back | Leaves multi-image for the tiles. Esc does the same when no card is open. The layout stays, and Multi-View returns to it. |
| Drawer | Drawer, or Drawer (N) | Opens or closes the drawer, on the tab it was last left on: Image Pool or Show. N is what that tab shows: images not on the canvas, or scenes. It never changes what Play plays. A light plate shows while the drawer is open. |
| Previous | icon | Previous scene, or the previous layout when no show is playing. Left arrow. |
| Play | Play, or Pause | Plays the show. Scenes are made as it plays and kept. While it runs: `Pause`. Space does the same. Filled while running. |
| Next | icon | Next scene, or one new layout when no show is playing. Right arrow. |
| Show | Show | Opens the Show card: the preset, this show's values, and Soundtrack. Engine settings are behind **Engine settings…** on that card. |
| Rows | Rows, or N Rows | Arranges the grid in rows and opens the grid layout card. R switches between rows and columns. B, R, H, + and − work only without Ctrl or Alt held, so Ctrl+B still toggles the sidebar. |
| Columns | Columns, or N Columns | Arranges the grid in columns. Same card and same R key. |
| Canvas | Canvas | Aspect ratio, surface, color and Frame & Border; then Fit whole images (or Fill cells), which changes how every image sits in its cell; then Reset canvas and images, on its own at the end. |
| Save/Load | Save/Load | Save the canvas as JPEG or PNG, copy it, or save and open layouts (`.mivlayout`) and storyboards (`.mivshow`). Ctrl+S opens it. |
| Undo, Redo | icons | Undo (Ctrl+Z) and Redo (Ctrl+Y). Greyed out when there is nothing to take back or bring back. They are on the bar because removing a scene, a cell or a layout never asks first. |
| Full screen | icon | Full screen toggle (F11). Last on the bar, after a divider, as in a video player. |

While a storyboard plays, each new scene's name shows at the top for a moment only while the bar is showing. Once the bar has hidden for want of movement, scenes change with nothing over the picture. Messages that answer something you did, such as Paused or Duplicated scene, still show.

### What Play plays

Play always plays the show. A show with no scenes yet is made as it plays, from the preset on the Show card. Opening the drawer does not change that. Clicking a scene card shows that scene. Editing its camera path opens Camera Director.

## Image Pool and storyboard drawer

A sheet along the bottom with two tabs and Close. Esc closes it. A click outside closes it, except a click on the playbar. B opens it on the Storyboard tab, or closes it when that is showing. The drawer can be dragged taller, but never past the playbar, which always sits above it.

The tabs read `Image Pool` or `Image Pool (N)`, where N is the images not on the canvas, and `Show` or `Show (N)`, where N is the scenes. **New show** on the Show tab starts an empty show with the preset and values from the Show card. Play then makes its scenes from the Image Pool.

### Image Pool

Images in the session that are not on the canvas.

- Heading: how many are in the Image Pool, and what a click does. Clicking one puts it on the canvas.
- Search box: filters by file name.
- Add images: adds more images from a folder.
- Clear pool: asks, then removes every image that is not on the canvas.
- Thumbnails: right-click to remove one from the session.
- Empty states say how images get into and out of the Image Pool.

### Show

**Add this layout** keeps the images on the canvas as a new scene. **New show**, on the right, asks and then starts an empty show with the preset and values from the Show card. Play on the bar makes the scenes. With no scenes yet, the tab says to press Play, and that Add this layout keeps the canvas as a scene. The status line names the preset. The summary is the scene count and the running time.

### Scene cards

Each card shows:
- The scene's own arrangement: each image where the scene puts it, from its saved split, or from its rows and columns when no split was saved. Floating images sit on top when the scene saved its canvas size. A count shows when there are more than four.
- The order: `#1`, `#2`, and so on.
- The camera move and hold, such as `⤡ Zoom out · 12s`, `⤢ Zoom in`, `⇄ Pan`, `• Still`, or `Dynamic`.
- The scene's name, then four buttons, each with its name for screen readers: Play show from this scene, Edit camera path, Duplicate scene, and, set a little apart, Remove scene.

Cards can be dragged into a new order.

#### Card interactions and menus
- **Click a card**: shows that scene on the canvas and leaves the drawer open.
- **Double-click a card**: shows the scene and closes the drawer.
- **Click the camera and hold badge**: the camera choices (Zoom in, Zoom out, Pan across, Still, then Dynamic, chosen at playback), then Edit camera path…, then Duration (5s, 8s, 12s, 16s, 20s, 30s). The current choice of each is ticked as a radio item.
- **Right-click a card** (or Shift+F10): Play show from here, Update scene from canvas (copies the canvas into that card's scene), Camera motion (the same choices as the badge), Duration, Duplicate scene, Remove scene, Move earlier, Move later.
- **Remove scene** never asks. It says `Removed scene. Ctrl+Z brings it back.`, and Undo does. Removing, duplicating or moving a scene is one Undo step, by button, menu, Delete key or drag. New show is one step, and Undo can go back to the show it replaced.

## Play show, from the folders

**Play show** in the sidebar, under Cinematic Slideshow, plays the whole folder with the preset chosen on the Show card. The same button on the selection bar plays the selected images. Either opens Multi-View with those images, starts a new show and plays it once the first scene is made. While it plays it makes the next scenes from images not in it yet, a few ahead of the screen. While it is paused, drafting stops once every image has been dealt once. It stops if the show is replaced or cleared.

**Cinematic** is a preset on the Show card, not a second sidebar button. One image at a time, Edge to edge most of the time, Hero spotlight rarely, the other designs off, 10 to 15 seconds. Cinematic Slideshow remains the separate window opened by the sidebar button of that name.

To play with the Multi-Image preset again, press Multi-Image on the Show card. **New show** in the drawer starts an empty show and keeps the preset. Reset on the card puts the preset back to its starting values.

## Camera Director (Visual Keyframe Editor)

Opened with Edit camera path… from a scene card's badge or menu, or the camera button on the card. Closes chrome and bottom HUD to provide an unobstructed full-canvas visual keyframe editor.

### Toolbar Controls
Floating at the top of the canvas, strictly elevated above all cards:
- **Scene Steppers**: `◀` and `▶` to step between scenes with the header `Scene 4 of 33 · Title`.
- **Keyframe Mode Pills**: `Start` in emerald green and `End` in ruby coral, each in its own color.
- **Preview Move**: Plays a live 2.5-second Ken Burns camera transition between Start and End keyframes.
- **Reset**: Reverts camera keyframes to default framing.
- **Cancel**: Leaves without keeping this scene's changes. Esc does the same. A scene left with the steppers has already kept its path.
- **Aim at face**: Centres the selected frame on a face and zooms so the face fills about a third of it, up to 2.5×. Largest face first; each click moves to the next. Greyed out when no face was found in the scene.
- **Exact…**: Zoom, across and down (the frame's centre, as a percentage of the canvas), and turn (degrees), typed for the selected frame. A turned frame is drawn turned, at the size playback shows it: zoomed a little further so no corner of the canvas shows.
- **Shot 1 of 2**: In a scene of more than one shot, steps between its shots; Start and End are the selected shot's frames. A shot that follows on starts where the one before ends, so moving one of those frames moves the other; "cut" marks a shot that starts somewhere else. Preview Move plays every shot, 2.5 seconds each.
- **Curve**: How the selected shot gets going and comes to rest (see Movement curves).

On the frames: drag a frame to move it, scroll over it to zoom, and drag the round handle to turn it, the same handle and gesture as turning a cell. The handle sits inside the selected frame's top edge, or its bottom edge when the top is under the toolbar. A turn goes up to 6° either way, the Rotation limit in Camera settings, so Shift steps by whole degrees rather than a cell's 15.
- **Face outlines**: Every face found in the scene is outlined with a dashed box, where the image shows it: its crop, zoom and turn included. Faces that had not been read yet, as in a scene added by hand, are read when the editor opens.
- **Done**: Commits the custom camera keyframes and returns to the storyboard. Enter does the same.

While it is open, Space previews, Tab switches Start and End, Left and Right step scenes, F1 still lists the keys, and F11 still toggles full screen. Other keys do nothing, so Undo and Save/Load cannot change the canvas under the frames.

### Wireframe Editing
- **Interactive Wireframes**: Green rectangular wireframe indicates camera Start; red wireframe indicates camera End.
- **Panning**: Drag anywhere inside the active wireframe box to reposition the camera center.
- **Zooming**: Scroll the mouse wheel over the wireframe to zoom in/out (from 1.0x to 2.5x).
- **Superimposed Keyframe Priority**: When Start and End have identical zoom and position (e.g. Still motion), clickable top-level badges (`Start · 1.0x` and `End · 1.25x`) at Z-index 300 remain elevated above both boxes. Clicking either badge instantly selects that keyframe.
- **Visual Dominance**: The active keyframe has full opacity (1.0), a prominent 3.5px border, and drag priority; the inactive keyframe is softly dimmed (0.55 opacity, 1.8px border).

## Canvas Layout Auto-Sync

When viewing a storyboard scene on the canvas, any manual layout adjustment automatically syncs to the scene in memory:
- Dropping/moving a card commits the new coordinate layout and creates an undo restore point.
- Resizing a freeform card handle commits the new size.
- Rotating a card commits the new rotation angle.
- Dragging a mosaic grid splitter commits the updated row/column weights.
- Play, pause, and leaving a scene write the canvas back only when that scene is the one on the canvas. A mix, or a scene marked selected before it was shown, is not written over the scene. Update Scene still stores the canvas when asked.
- Full undo history (`Ctrl+Z` / `Ctrl+Y`) is preserved across scene navigation and editing.

## Layout Generator Settings

Opened from **Engine settings…** on the Show card. These are how one layout and one move are made. The show's own images per scene, duration, smart pace, motion, sequence beats, scenes per step and design odds are on the Show card, not here. The page is in sections: Images & faces, Designs, Camera, Transitions, Canvas & progress.

The defaults, which **Reset to defaults** on the Advanced page puts back. A settings file saved before a default changed keeps its own values until then.

| Setting | Default |
|---|---|
| Images per scene | 1 to 9 |
| Duration | 10 to 15 seconds (a single duration, with the thumbs together, defaults to 10 seconds) |
| Smart pace | On |
| Motion | 40% to 60% |
| Zoom (layout zoom, every image) | 1.5× to 4× |
| Zoom frequency | 70% |
| Camera zoom | 1.15× to 1.8× |
| Face kept in frame, at least | 85% |
| Pick from the best | 3 |

### Faces

Faces are found by Windows on this PC. What follows applies only to faces it finds; a face it misses, such as one turned away, very small or in shadow, is cropped like any other part of the image.

- **Framing.** A cell with faces is framed on the area that holds all of them, a little above the middle of the cell, whether or not it zooms. An image with no faces is framed on its focus point.
- **Zoom.** Zoom is not only for faces: see **Zoom**, below. With faces, a zoom heads for them and stops short of cutting any: all the faces found stay in, taking at most 85% of the cell. A face that already fills the cell, or a wide group, zooms little or not at all.
- **No cut faces.** Each layout is tried in several versions. A version cuts a face when less of it than **Face kept in frame, at least** shows, whether the cell's edge, its shape or another cell hides it. The pick is made from the best **Pick from the best** versions that cut no face and mostly hide no image; only when every version cuts a face is the best of them taken anyway. A small face in a crowd weighs less than a face that fills the image.
- **The big places.** The largest cell goes, by preference, to the image with the most in it, and faces count heavily. Hero spotlight takes its hero from the two images that rank highest, unless an image is featured (**Feature in Auto-Mix**), which always gets the best place.
- **One image.** A layout of one image is kept to Crop at most: see **One image**, below.
- **Camera.** 70% of camera moves head for a large cell with faces. Two-shot scenes are under Camera, below.

These are applied when a layout is made, so changing them does not change scenes already in a storyboard; Add new layout and Replace show use the new values.

### Zoom

Two settings zoom, and both work with or without faces:

- **Zoom** (Images & faces), a range, with **Zoom frequency**: when a layout is made, each image rolls against the frequency, and one that zooms picks an amount within the range, toward its faces or, with none found, its focus point. Only two things stop it short: it never cuts a face, and never shows the photo past twice its own pixels, so it stays sharp. The zoom stays for the scene. It replaces Zoom on faces, which zoomed only images with faces, by a fixed amount for each photo.
- **Camera zoom** (Camera), a range: how far a zoom in or out goes while a layout plays. Each zoom picks within it, and so does a two-shot's close-up, always closer than its wide shot. Before this setting the depth came from Motion and could not pass about 1.6×.

On a Cinematic storyboard portrait with the default settings, about 7 scenes in 10 the camera move zooms to an amount from 1.5× to 4× where the photo has the detail and the face allows, and the rest are a Camera zoom or a drift over the whole portrait. A layout of one image is not zoomed when it is made: it opens on its fit or fill, and **Zoom** is where its one camera move arrives. On Auto, **Zoom frequency** decides how often the move zooms in or out to the Zoom amount, toward the faces or, with none, the focus point; otherwise it is a Camera zoom or a drift. With the camera on Zoom in or Zoom out, every scene zooms to the Zoom amount. No camera view of one image goes past where a face would take more than 85% of the screen or the photo would show past twice its pixels; a two-shot with no room to snap in is not played.

### One image

A layout of one image goes through the design odds like any layout. **Crop at most** (Images & faces, default 35%) is how much of it a layout may crop away to fill its frame, measured against the frame's shape; a face is never cut past **Face kept in frame**. Past either, Edge to edge shows the image whole at full height or width on a tinted background, Gallery wall shows it whole in its card, and Scattered prints already shows it whole. Hero spotlight, Circles and Slanted panels can only crop, so they sit that layout out and another design is used. At the default, a circle comes up for one image only when it is within about 6:5 of square, since a circle alone hides about a fifth of its box. Zoom is where its camera move arrives: see **Zoom**, above. A Cinematic storyboard has its own design odds.

### Cinematic storyboard

A Cinematic storyboard is a show started from the **Cinematic** preset. Its values are its own, edited in this show's settings on the storyboard, and saved with it; for everything else, such as faces, the camera and Canvas & progress, it uses Layout Generator Settings. The preset's defaults:

| Setting | Default |
|---|---|
| Images per scene | 1 to 1 |
| Designs | Edge to edge Often, Hero spotlight Rare, the rest Off |
| Duration | 10 to 15 seconds |
| Smart pace | On |
| Motion | 40% to 60% |
| Sequence beats | 25% |

**Show** on the bar opens this show's settings. Presets are one line: Multi-Image, Cinematic, 1, 2 and 3. Press one to use it. Hold or right-click to keep this show's values in it. The reset symbol puts that preset back. What you change is kept in the app's settings for the next Play of that preset. The values are images per scene, duration, smart pace, motion, sequence beats (default 25%), scenes per step and each design's odds. A saved `.mivshow` file carries its own values. **Soundtrack** on the card sets Cut on the beat (default on), Follow the music (default 50%) and Hard cuts (default 100%).

A Cinematic storyboard uses your main Transitions settings.

### Motion and movement

**Motion**, on the first page and in the Cinematic storyboard section, is a range: each layout or scene picks how far the camera drifts from within it, and 0 keeps the camera still. How far it zooms is **Camera zoom**. Ends together is one strength. A saved single Motion becomes a range of that one value.

Under Camera:

- **Faces are framed by the eyes**: a zoom or a sideways drift aimed at a person, the face views of sequence beats and the close shot of a two-shot put the eyes a third of the way down the screen, as Cinematic Slideshow does, rather than the middle of the face in the middle. On a face view of one image the whole face stays in view, up to the closest zoom it allows. A drift up or down a picture travels past the eyes, and a picture with no face is centred on its focus point. At a gentle zoom the edge of the picture can stop the view, and the eyes then sit a little lower.
- **Two-shot scenes** (default 25%): the chance that a scene with a face, on screen 8 seconds or more (the camera's run-on past the change does not count), is two shots, as Cinematic Slideshow makes them: a wide shot, then either a snap zoom in to the face or a cut to the face and a slow push in. Only with the camera on Auto.
- **Movement curves**: odds for how each move gets going and comes to rest, on the Off to Often scale, with the mix shown underneath.

| Curve | What it looks like | Default |
|---|---|---|
| Steady | The same speed throughout, so it is still moving through a crossfade | Normal |
| Gentle | The mildest start and stop | Often |
| Smooth | A soft start and stop; every move eased this way before there was a choice | Normal |
| Cinematic | Lingers at both ends, quicker in the middle, as Cinematic Slideshow | Less |
| Settle | Under way at once, then glides to rest | Rare |
| Snap | Slow, a quick push, then a long settle; zooms only | Off |

- **Curved path** (default 0–50%): how far a pan's path bows to one side, up to Cinematic Slideshow's bend.
- **Rotation** (default Off, up to 6°): how far the view turns over a move. It zooms in just enough to keep every corner covered.

Storyboard scenes store their curve, bend, turn and shots, and play them; scenes made before these play as they did.

### Transitions

In its own section, Transitions, each kind of scene change has its own slider, on the same Off to Often scale as the designs: **Crossfade**, **Zoom through**, **Fade through black** and **Slide**. They are odds against each other. The line under them gives the mix they make, for example `Mix: crossfade 65% · zoom through 14% · fade through black 14% · slide 8%`. The same kind never comes twice in a row, except a crossfade, and the mix allows for that. With all four off, every change is a crossfade. They apply to Auto-Mix while it plays and to the scenes a storyboard makes; each storyboard scene keeps the transition it was made with and plays it. A change made by hand in Auto-Mix is a crossfade.

**Transition length** (default 0.75 to 2.5 seconds, from 0.25 to 4) is how long a change takes. Each change picks a length in the range, and the whole transition stretches to it, so a long crossfade is a slow dissolve, as in Cinematic Slideshow; drag the ends together for one length. The outgoing scene keeps moving for as long as the change takes. A hard cut on the music, and the cut between views of one image, stay cuts.

These replace the single Transition variety slider. A saved Transition variety is converted once to odds giving the same mix.

## Save / Load (Storyboards & Layouts)

Opened via `Save/Load` or `Ctrl+S`:
- Section header: **Storyboards & Layouts**.
- **Save Storyboard as…**: Exports current presentation as `.mivshow` JSON project.
- **Open Storyboard…**: Opens a saved `.mivshow` file. A `.mivshow` also opens from Explorer, the command line or a drop on the window. That replaces the session, which goes to Autosaved.
- **Recent & Favourites Folders**: Scans for both `.mivlayout` and `.mivshow` files, automatically prioritizing `.mivshow` storyboards at the top of folder lists with a video icon and a blue `Show` badge.
- **Autosaved Sessions**: Displays both image count and storyboard scene metadata: `181 images · 33 scenes` with full scene restore tooltips.

## Animated GIF Playback

- **Single Viewer**: Animated GIFs automatically decode all animation frames into GPU memory on open.
- **Playback Controls**: Displays interactive animation HUD (`[◀] [⏸] [▶]  1 / N`), supporting Space (play/pause), Left Arrow (previous frame), and Right Arrow (next frame).
- **Multi-View Canvas**: Multi-view cells and Solo mode support animated GIF playback with hardware acceleration.
