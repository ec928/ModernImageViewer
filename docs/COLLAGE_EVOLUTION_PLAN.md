# Multi-View Collage Plan

Multi-View is the app's collage mode, and it has replaced the separate Collage editor. The aim is to come closer to the more capable collage apps while staying extremely easy and fast to use. Anything more is built on this cell architecture.

How it is built is in `ARCHITECTURE.md`, under Multi-view. How it looks and behaves is in `UI_UX.md`. In short, it is cell based: a cell holds the parameters, and the image is only what it shows.

## Built, as a prototype

- Grid layout tool, heroes and draggable edges
- Per-cell frames: stroke, corner radius, gutter, mat and shadow, with Flush and three saved presets
- Swap, copy, move and clear, with parameters staying on the cell, and swap, copy and move that can bring the cell's attributes too
- Rotate, mirror and flip the image on any cell, and replace it with a picture found in the app
- Floating cells: move, resize, snap, layer, and Dock back into the grid
- Pictures from the app (Add images) or dropped from Explorer become new floating cells; an empty cell takes one from the app (Add image) or from a drop onto it
- Shapes on every cell: rectangle, squircle, circle or oval, arch, L-shape, triangle, right triangle, rhombus, trapezoid, parallelogram, hexagon and organic blob, with mirroring, and on floating cells rotation and shape-only rotation
- Canvas surface and color, including custom colors
- Frame & Border for the whole canvas
- Undo and redo, three autosaved sessions, and Reset canvas and images
- Surprise me: four designed layouts, scored against the pictures so faces stay clear
- Save and load `.mivlayout` layouts
- Export to JPEG or PNG, and copy to the clipboard

## Still open

- Select several cells to move or align together.
- Text as cell content.
- Layouts as templates, applied to any selection of photos.
- Revisit the canvas beyond its surface, color and frame.
