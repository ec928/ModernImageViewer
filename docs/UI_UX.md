# ModernImageViewer UI/UX Specification & Design Ledger

Authoritative reference for UI/UX patterns, styling invariants, and desktop ergonomics across ModernImageViewer. Consult this ledger before modifying XAML styles, control layouts, themes, or micro-interactions.

---

## 1. Visual Language & Fluent Design Principles

ModernImageViewer is built with **WinUI 3** (Windows App SDK) and adheres to Windows 11 Fluent Design desktop paradigms:

- **Backdrops & Depth**:
  - The window uses dynamic theme-aware materials (Acrylic and Mica) for headers, flyouts, and dialog overlays.
  - Image canvases (Single Viewer and Multi-View) intentionally enforce a neutral, ultra-dark backdrop (`#0A0A0A` to `#121212`) so photographic colors remain unskewed by window framing.
- **Corner Radii**:
  - Controls, buttons, and badges: `4px`.
  - Cards, flyouts, and HUD overlays: `8px` – `10px`.
  - Dialog windows: `8px` outer with `4px` nested inputs.
- **Borders & Elevation**:
  - Card and flyout borders: 1px subtle stroke (`{ThemeResource CardStrokeColorDefaultBrush}` or `{ThemeResource DividerStrokeColorDefaultBrush}`).
  - Never use heavy, opaque borders or saturated drop shadows.

---

## 2. Light & Dark Theme Parity

Both themes must provide equivalent hierarchy, contrast, and visual polish:

| Element | Dark Theme | Light Theme | High Contrast |
| :--- | :--- | :--- | :--- |
| **Canvas** | Deep dark (`#0A0A0A`) | Deep dark (`#0A0A0A`) for photos | System window background |
| **Header / Chrome** | Dark acrylic (`#202020` @ 80%) | Light acrylic (`#F9F9F9` @ 85%) | HighContrast Window |
| **Primary Text** | `#FFFFFF` | `#1A1A1A` | System text |
| **Secondary Text** | `#C5FFFFFF` | `#5D5D5D` | System secondary |
| **Disabled Text** | `#60FFFFFF` | `#9E9E9E` | GrayText |
| **Favourite Star (Active)** | Gold `#FFC83B` / `#FFB900` | Warm Amber `#D97706` | Gold `#FFD700` |
| **Dividers / Strokes** | 1px `DividerStrokeColorDefaultBrush` | 1px `DividerStrokeColorDefaultBrush` | System border |

> [!IMPORTANT]
> **Light Theme Star Legibility**: Pure yellow (`#FFD700`) washes out against white or light-gray flyouts. Always use amber `#D97706` in Light theme and vibrant gold `#FFC83B` in Dark theme via `FavouriteStarBrush`.

---

## 3. Typography & Iconography

### Typography Hierarchy
- **Font Family**: `Segoe UI Variable` (system default), falling back to `Segoe UI`.
- **Weights**:
  - Section headers, active selections, and primary HUD filenames: `SemiBold`.
  - Body text, button labels, and secondary details: `Normal`.
- **Casing**:
  - Use **Sentence Case** or **Title Case** for labels and section headers (e.g., *"Favourites"*, *"Recent Folders"*, *"Clear Recent Folders"*).
  - Avoid ALL CAPS in UI labels.

### Iconography Rules
- **Font Family**: Always specify `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"` on `FontIcon` elements to ensure graceful fallback on Windows 10 and 11.
- **String Encoding Invariant**:
  - In XAML: Use XML entities (e.g. `Glyph="&#xE735;"` for filled star, `Glyph="&#xE734;"` for outline star).
  - In C#: Use escaped Unicode code points (e.g. `Glyph = "\uE735"`), **never** raw multi-byte UTF-8 character literals.
- **Semantic Icons**:
  - Standard folders: `Symbol.Folder` (`\uE838` / `\uE8B7`).
  - Comic book archives (`.cbz`, `.cbr`, `.cb7`, `.zip`): `Symbol.Library` (`\uE82D`), distinguishing bundled archives from loose folders across both the sidebar and menus.

---

## 4. Menus, Flyouts & Navigation (Favourites Engine)

The **Favourites and Recent Folders** flyout (`RecentFoldersMenu`) serves as the quick-access hub for library navigation:

```
+------------------------------------------+
| Favourites (3 of 10)                     |  <-- SemiBold caption header
|  [Folder] Photos 2026                 ★ |  <-- Gold filled star (#FFC83B / #D97706)
|  [Library] Batman #01.cbz             ★ |  <-- Comic archive library glyph
|  [Folder] Wallpapers (unavailable)    ★ |  <-- Dimmed row, removable star
|------------------------------------------|  <-- Full-bleed divider (-4px margin)
| Recent                                   |
|  [Folder] Downloads                   ☆ |  <-- Outline star reveals on hover
|  [Folder] Screenshots                 ☆ |
|------------------------------------------|
|  [Delete] Clear Recent Folders           |  <-- Dedicated secondary action
+------------------------------------------+
```

### Invariants:
1. **Pinned Favourites (Cap: 10)**:
   - Favourites remain pinned at the top, separate from Recent.
   - A favourite is never duplicated in the Recent section.
   - Missing/unplugged folders remain in Favourites (rendered dimmed with `IsEnabled = false` on the path button), but the star remains interactive so users can un-star and remove them.
   - "Clear Recent Folders" removes only recent history; favourites are never wiped.
2. **Hover Affordance**:
   - Recent items display an outline star only when the row is hovered by pointer or focused via keyboard navigation (`FocusState.Keyboard`).
   - Clicking a star toggles favourite status in place and keeps the flyout open without losing keyboard focus.
3. **Tooltip Hitboxes**:
   - Tooltips must be attached to the entire row container (`FolderMenuButton`), not just inner text blocks, ensuring zero dead zones.
   - When the favourite cap (10) is reached, hovering over any unstarred item's star displays:
     `"Favourites limit reached (10 of 10)"` (enabled via a transparent container wrapper that captures pointer events even when the button is disabled).
4. **Divider Geometry**:
   - Flyout dividers must bleed to the border edges by setting `Margin="-4,4,-4,4"` against the `FlyoutPresenter`'s `Padding="4"`.

---

## 5. Inspection HUD & Overlay Standards

Information overlays must provide instant diagnostic readability without obscuring the photograph:

### Top-Left Inspection HUD (Single Viewer & Multi-View Solo)
The Single Viewer and Multi-View Solo/Maximize modes share an **identical** HUD specification:

- **Container**: Floating acrylic pill, `CornerRadius="8"`, `Padding="12,8"`, subtle 1px border.
- **Backdrop**: Semi-transparent dark acrylic (`rgba(20, 20, 20, 0.85)` / `CardBackgroundFillColorDefaultBrush`).
- **Hierarchy**:
  - **Line 1 (Title)**: `Filename.ext` — `SemiBold`, `14px`, Primary Text (`#FFFFFF`).
  - **Line 2 (Metrics)**: `Width × Height • Size • Date Taken/Modified` — `12px`, Secondary Text (`#C5FFFFFF`).
  - **Line 3 (State)**: `Zoom: XX% • HF` — `12px`, Secondary Text with bold `HF` (High-Fidelity decode indicator).

### Bottom Floating Playbar / Command Bar
- Pill layout centered horizontally near the bottom edge.
- Subtle button styles with smooth hover transitions.
- Auto-hides on cursor inactivity during fullscreen or presentation; reveals smoothly on pointer motion.

---

## 6. Multi-View UI & Interaction Patterns

Multi-View shows up to 16 grid cells in fluid mosaics, with floating cells above them. Its right-click menu is grouped by what the user is doing:

```
Move Left / Up
Move Right / Down
Swap with...              >   Contents only | With attributes
Copy to...                >   Position 1    | Position 1
--------------------------    Position 2... | Position 2...
Frame & Border...
Advanced                  >   Replace image...
--------------------------    Rotate image left / right
                              Mirror image, Flip image
Solo / Maximize               ----------- floating cells only
Freeform  (or Dock to Grid)   Window  >   Rectangle, Squircle, Circle / Oval, Arch / Tombstone,
Hide image                                L-shape, Triangle, Right triangle, Rhombus / Diamond,
                                          Trapezoid, Hexagon, Organic blob
                                          Rotate left / right
--------------------------                Rotate shape only, left / right
Remove from View                          Mirror shape, Flip shape, Reset window
                              Layer   >   Bring to front, Bring forward,
                                          Send backward, Send to back
```

The playbar's wheel holds Undo, Redo, Copy to clipboard, Save as PNG, and Canvas (Surface, Color).

Swap with and Copy to name positions only, never file names, so the two columns stay about the width of a single column of named entries. Each row is one menu entry, and the side of the row under the pointer is the column chosen, shown by a highlight on that half. Pointing at a target marks its cell's badge, with "+ attributes" for the second column. The same layout appears with any number of cells, down to one row with two photos.

Hovering a floating cell shows its resize rectangle as a dashed outline with a white square grip at each corner. This is the same for every shape, because the corners of an irregular shape's rectangle are otherwise empty space with nothing to show they can be dragged.

Rows and Columns are a switch on the playbar, with the line count on the side in use. Clicking the other side turns the grid and opens the Grid layout card. Clicking the side in use opens or closes the card. The card holds how many lines and what is in each. The switch stays clickable while the card is open, so one click changes sides.

The Frame & Border card always shows at full height for the window, in front of every cell. It opens beside its cell, or against the window edge farther from a cell too big to sit beside, and scrolls only when the window is too short.

### Invariants:
1. **Dynamic Swap Badges**:
   - When the user opens the "Swap with..." context menu, all potential destination cells immediately display a clear, high-contrast numeric position badge (`1`, `2`, `3`...) on their top-left.
   - This prevents ambiguous guessing of zero-based vs one-based index positions.
2. **Alt+Drag Direct Manipulation**:
   - Holding `Alt` and dragging any cell onto another swaps their positions instantly.
   - Visual affordance: Drop target glows with a subtle accent highlight.
3. **Draggable Dividers**:
   - Divider borders support smooth edge dragging to rebalance grid splits without reloading or re-decoding image bitmaps.
4. **Unified Solo / Maximize Mode**:
   - Clicking Solo/Maximize expands the chosen photo to fill the canvas while preserving all multi-view layout state.
   - The top-left info HUD is identical in visual treatment and data layout to the Single Viewer inspection HUD.
   - Exit button returns cleanly to the mosaic view.
5. **Show Controls Only When They Apply**:
   - The next-position button shows only while a hero is on. Grid Layout is disabled below two grid cells that show an image. An empty preset is dimmed.
   - A hidden cell's label fades with the playbar, so the composition can be seen without it.
6. **Floating Cell Labels and Handles**:
   - A floating cell's position label is centered and stays upright, however the window is turned. Its hover buttons and handles are not clipped by its shape, and stay briefly after the pointer leaves so they can be reached.
7. **One Word for a Thing**:
   - Menus and the frame card call a place a *Position* and a slot a *cell*. UI strings use the spelling *color*.
8. **Immersive Dark Flyout & Menu Presenters**:
   - The photo canvas and floating playbar are an immersive dark surface. All playbar flyouts (Aspect menu, Save resolutions) and photo cell context menus must render with a dark background (`#F2202020`), subtle 1px border (`#33FFFFFF`), `CornerRadius="10"`, and white text.
   - **WinUI 3 Trap Avoided**: Setting only `<Setter Property="RequestedTheme" Value="Dark" />` on `MenuFlyoutPresenter` causes the presenter's template background to resolve against the Light popup root while child items turn white (resulting in white text on a light beige background). Menus hosted in `XamlRoot` must explicitly set `Background="#F2202020"` and `BorderBrush="#33FFFFFF"` via `MultiViewMenuFlyoutPresenterStyle` and enforce dark styling on open.

---

## 7. View Modes: Tile Grid vs List Mode

| Mode | Target User Workflow | Visual Spec |
| :--- | :--- | :--- |
| **Tile Grid** | Visual browsing & curation | Responsive thumbnail grid (`~250px` base), smooth slider zoom scaling, subtle border selection glow. |
| **List Mode** | High-density asset management | Multi-column table (`Name`, `Dimensions`, `Size`, `Date Modified`, `Type`) with natural alphanumeric sorting (`StrCmpLogicalW`). |
| **Inspector Bar** | Metadata evaluation in List Mode | Bottom docked panel with complete metadata readouts and an instant live thumbnail preview on the right. |

---

## 8. Dialogs & Command Footers

In accordance with workspace UX standards:
- **Never** float commitment buttons inline with form content.
- **Dedicated Footers**: Dialogs (About, Settings, Save / Export) must feature a dedicated bottom footer spanning the full width, separated by a 1px border or subtle tint.
- **Button Alignment**: Primary actions (e.g., "Save", "Done") anchor to the **bottom-right** of the footer, with secondary actions ("Cancel") immediately to their left.
- **Button Hierarchy**:
  - Primary button: Accent fill (`AccentButtonStyle`).
  - Secondary / Dismiss button: Default or subtle stroke (`Default` / `SubtleButtonStyle`).

---

## 9. Keyboard Accessibility & Shortcuts

Every major workflow must be accessible without a mouse:

| Category | Shortcut | Function |
| :--- | :--- | :--- |
| **Navigation** | `Left` / `Right` | Previous / Next photo |
| | `Up` / `Down` | Grid row traversal / list navigation |
| | `Ctrl+B` or `Tab` | Toggle navigation sidebar |
| **Viewing** | `Enter` / `Space` | Open Single Viewer / toggle slideshow playback |
| | `F11` | Toggle borderless fullscreen |
| | `0` | Fit to window |
| | `1` | 100% native 1:1 pixel zoom |
| | `Ctrl + Wheel` / `+` / `-` | Smooth zoom in / out |
| | `B` | Toggle 2-page comic book spread mode |
| **Multi-View** | `Alt + Drag` | Drag and swap photo cells |
| | `Ctrl + Z` / `Ctrl + Y` | Undo / redo |
| | `Ctrl + C` | Copy the canvas as an image |
| | `Ctrl + S` | Save or load |
| | `F` / `H` / `R` | Fill or Fit / next hero / rows or columns |
| | `+` / `-` | More or fewer lines |
| **Floating cells** | `Ctrl + Drag` | Pan the photo inside the cell |
| | `Alt` while dragging | Move without snapping |
| | `Ctrl` on the rotate handle | Turn the shape and keep the photo still |
| | `Shift` on the rotate handle | Rotate in 15 degree steps |
| **System** | Bare `Alt` | Handled silently to prevent stealing focus to the Win32 system menu |

### Focus Visuals:
- Use modern WinUI 3 focus rectangles (`FocusVisualMargin`, `FocusVisualPrimaryThickness`); never rely on Win32 blue dotted outlines.
- When toggling items in dynamic menus (e.g., starring a favourite), focus must be restored quietly to the target element so the keyboard flow remains continuous.
