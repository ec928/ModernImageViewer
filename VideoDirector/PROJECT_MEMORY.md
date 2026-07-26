# VideoDirector — Project Memory & Architecture Ledger

**Path**: `c:\Users\chan_\OneDrive\Apps\ModernImageViewer\VideoDirector\PROJECT_MEMORY.md`  
**Purpose**: This living authoritative document serves as the single source of truth for the VideoDirector Non-Linear Editor (NLE) architecture. Its primary mandate is to prevent AI memory drift across sessions, record all established solutions and improvements, enforce core architectural laws, and outline strategic next steps.

---

## 1. Accomplished Improvements & Architectural Ledger (Priority 1)

This ledger documents all completed features, bug fixes, and architectural optimizations. **Do not re-propose or regress these items.**

### 🎬 Timeline & Track Behavior Unification
* **Consolidated Track Menu Bar (Timeline Toolbar)**: Re-homed global and project-level operations out of the scattered central transport/inspector zones into a dedicated Timeline Toolbar directly above the timeline (`TrackDock`). The left zone hosts History (Undo/Redo) and Timeline Mode Tools (Snapping, Ripple, Waveforms); the right zone hosts View Controls (Zoom, Fit Window), Project Actions (Save, Load, Clear), and MP4 Export.
* **Direct Track Loading via Clickable Labels**: Enabled every Track label ("Track 1", "Track 2", "Track 3", "Track 4") on the timeline to act as an interactive load button. Clicking a label opens a file picker (`LoadIntoTrack`) to load video/image assets directly into that specific track lane, eliminating reliance solely on dragging from Windows Explorer.
* **Intuitive Track Block Reordering (`ResolveOverlaps`)**: Replaced rigid `ClampToFreeSlot` restrictions on Tracks 2, 3, and 4 with dynamic `ResolveOverlaps()`. When dragging an overlay clip (e.g., moving *Backrooms* on Track 3 to sit before *The Magic Faraway Tree*), its new start time is applied directly and sibling clips automatically shift to the right to make room. This achieves behavioral parity with Track 1 (Spine).
* **Bidirectional Cross-Track Transfers**: Unified drag-and-drop boundary crossing in `TimelineBar_PointerMoved`. Dragging a clip vertically between Track 1 (Spine) and Tracks 2/3/4 (Overlays) dynamically transfers, re-indexes, and resolves overlaps without clamping errors or clips disappearing.
* **Magnetic Timeline Snapping**: Implemented runtime magnetic snapping (`GetTimelineSnapPoints`, `ApplyScrubSnapping`, `ApplyClipSnapping`) with an 8-pixel screen threshold. Playhead scrubbing and clip dragging smoothly snap to adjacent clip edges and anchors across all tracks.
* **Ghost-Follow Dragging on Spine Track**: Decoupled visual dragging from commit on Track 1 (Spine), drawing a free ghost block following the cursor during drag while committing the exact sequence index once upon drop.
* **Context Flyouts for Track Blocks**: Right-clicking any timeline block brings up a clean context flyout (`Duplicate` / `Remove` / `Split at playhead` / `Snapshot still`), completely re-homed and debugged so it doesn't conflict with left-click selection or rebuild the canvas on `PointerReleased`.

### 🎯 Modal Clarity & Playbar Refinement
* **Mode-Specific Playbar Operations**: The Playbar is explicitly mode-aware. Clip-specific operations (such as Trim controls) are strictly confined to **EDIT Mode** and banned from **PLAYBACK Mode** and **ARRANGE Mode** where modifying individual clips is dangerous or illogical.
* **Concise Playbar Mode Indicators**: Integrated clean, professional visual mode indicators directly into the Playbar UI:
  * **EDIT Mode**: <span style="color:red; font-weight:bold;">RED</span>
  * **PLAYBACK Mode**: <span style="color:green; font-weight:bold;">GREEN</span>
  * **ARRANGE Mode**: <span style="color:cyan; font-weight:bold;">CYAN</span>
* **Interactive Mode Badge (One-Click Edit Exit)**: In EDIT mode, the mode badge on the Playbar acts as an interactive button. Clicking it cleanly exits Edit mode and returns to Arrange mode—eliminating the annoying hunt for the top-right "DONE" button.

### ✂️ Trimming & Canvas Ergonomics
* **Trim "Messy Blob" Resolution**: Solved the visual scaling issue where trimming a short clip (e.g., 10 seconds) from a massive source video (e.g., 45 minutes) caused the trim controls and clip segment to collapse into an unusable, unreadable "messy blob." Trim view scaling is properly handled for ergonomics.
* **WYSIWYG Canvas Polish**: Removed the inactive outer full-frame white bounding box (`WysiwygFullFrameRect`) during Edit mode preview in `VideoPlaybackEngine.cs`. The canvas now displays *only* the active, useful boundary box for the clip being edited.
* **Inspector Formatted Timecodes**: Added human-readable timecode labels (`00:00:00.00`) in the Inspector panel alongside numeric inputs for professional time tracking and precision.
* **Compact Inspector & Telemetry HUD (`c45c45c`)**: Re-homed PiP size coordinates and operational readouts into a clean Telemetry HUD, compacting the Inspector UI for better workflow clarity.

### 🚀 Playback Engine Synchronization & Performance Polish (`95cd10a`, `a3adb0c`)
* **Wall-Clock Time Advancement for Still Ken Burns**: Resolved continuous audio/video stuttering and drift-correction seek-jumping on overlay tracks (Tracks 2–4) when Track 1 plays a still image with Ken Burns applied. When `op.IsStill` is true, master story time and spatial animation progress advance continuously via real wall-clock time rather than remaining frozen at `MediaPlayer.Position = 0:00`.
* **Per-Frame UI Layout & GPU Composition Optimization**: Guarded overlay bounding box layout adjustments (`grid.Margin = ...` in `ApplyOverlayBox`) and `CompositeTransform` property assignments (`ApplyMarksAtProgress`) against redundant per-frame overwrites (`Math.Abs(...) > 0.0001` and `Margin.Left != left`). This eliminates 60 FPS unnecessary Measure/Arrange XAML layout passes and prevents dirtying DirectComposition visual trees when transforms and bounding boxes are static.
* **Canvas Edit Mode Visual Cleanliness**: Removed the redundant thick outer accent border (`<Border BorderThickness="3" ... />`) around the video canvas during Edit mode in `VideoDirectorControl.xaml`. Edit mode visual indicators are now cleanly confined to the inspector panel header and the interactive WYSIWYG crop/motion overlays directly on the video.

---

## 2. Established Architectural Laws & Principles

1. **Holistic Design over Piecemeal Hacks**: Never apply localized fixes that break the overarching NLE mental model. All tracks must follow consistent interaction laws.
2. **Strict Track Roles (Simultaneity vs. Sequence)**:
   * **Track 1 (Spine / A-Roll)**: Keeps its dedicated A/B-roll sequence path for primary story pacing.
   * **Tracks 2, 3, & 4 (Overlays / B-Roll / PiP)**: Provide layered compositing. Each overlay track is strict (never stacking two clips at the exact same timestamp on the same track), using `ResolveOverlaps()` to push siblings sequentially when reordering occurs.
3. **Modal Separation of Concern**:
   * Editing handles, Trim bars, and PiP bounding boxes must **never** leak into screening (PLAYBACK) or macro-structuring (ARRANGE).

---

## 3. Strategic Next Step Options (Lower Priority / Under Evaluation)

When transitioning from visual foundations and basic mechanic stabilization to end-to-end prototyping, we will evaluate the following architectural directions:

### Option A: End-to-End Workflow & Visual Prototyping (Low Risk / Immediate Value)
* **Objective**: Focus on end-to-end visual foundations across the entire application, stubbing out high-risk backend time-manipulation areas where necessary.
* **Benefit**: Allows rapid UI/UX evaluation of the overall editing loop (importing, arranging, editing, screening, exporting) while balancing technical risk.

### Option B: Trimming Edge Mechanics Across Overlay Tracks (Medium Priority)
* **Objective**: Establish comprehensive rules for trimming a clip's In/Out edges on Tracks 2–4. Define exact behaviors for **Ripple Trimming** (extending a clip edge pushes downstream clips) versus **Roll Trimming** (extending an edge consumes empty gap space or overwrites).
* **Benefit**: Completes behavioral parity between Track 1 and overlay tracks for edge manipulation.

### Option C: Deep Data Schema Unification (High Reward / Higher Risk)
* **Objective**: Refactor the underlying data models so that `TimelineNodes` (Track 1) and `OverlayTracks` (Tracks 2–4) share a single unified `ObservableCollection<TimelineTrack>` schema, differentiated only by compositing attributes (e.g., `TrackRole.Spine` vs `TrackRole.Overlay`).
* **Benefit**: Permanently eliminates divergence between track types at the data layer. Best attempted after visual prototypes and user workflows are 100% locked in.
