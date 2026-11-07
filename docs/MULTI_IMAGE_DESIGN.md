# Multi-image design: layouts, storyboards and Play

**This is the one design for the whole multi-image area**: the layout engine (Layout Generator), the storyboard and
its director, cinematic storyboards, Play, and cutting on the music (3.10). There is no separate design for any part
of it. Every other plan or proposal in `docs/` about this area is superseded by
this file. Do not read, implement from, or reconcile with them. How it is built, slice by slice, is
`MULTI_IMAGE_IMPLEMENTATION.md`; this file wins where they differ. `MULTI_IMAGE_UI.md` describes the app as it is, not
the design. Renamed on 6 Nov 2026 from `LAYOUT_GENERATOR_DESIGN.md`.

**Status: proposal, partly built on a branch, not approved.** Slices 1 to 6 were built on 6 Nov on branch
`slice1-one-engine-call`, engine-first by mistake (see "What the user meets", below); the screen is being rebuilt to this
file. Nothing here is in the released app until Ed has said yes to it. Where this file says "today", it means v0.31.2,
the released app, described in `MULTI_IMAGE_UI.md` and the README.

**How to use this file.** Start with **What the user meets** (R17): it is the first principle and the first thing built.
The direction below is where the work goes. The core principle is what the engine is. Section 1
is Ed's requirements, with where each came from. Every later decision cites the requirement it serves (R1, R2 ...).
Section 6 lists every number the generator uses, so none is decided silently. Section 9 is the questions only Ed can
answer. A reviewer should start at section 11.

**Names.** These four are not interchangeable.

| Name | Meaning |
|---|---|
| Engine call | One layout and one move (today's two-shot included), from one entry point (Core principle). The engine does not keep it. |
| Canvas layout | One engine call placed on the canvas. It is not a scene. |
| Direct Auto-Mix | What v0.31.2 plays continuously. Each step is an engine call, and the result is discarded. It goes (R15, R17). |
| Play | Always the director (R15, R17). It plays the show; a show with no scenes yet is made as it plays, and kept. |
| Show card | What the user meets: the show's preset, its values and its soundtrack. Opened from Show on the bar. |
| Engine settings | How the engine makes one layout and one move. Behind one link on the Show card, not the first thing seen. |

## Direction

The foundation is the multi-image engine. It was built from scratch, from fundamentals. Cinematic slideshow is not that
foundation. Sequence beats were removed so the cinematic engine could be rebuilt from fundamentals, and that rebuild
did not succeed. Cinematic slideshow was then abandoned as the engine to rebuild. Its window is still in the app
(v0.31.2). Development does not continue from it, and `CINEMATIC_PROPOSAL.md` is not the direction.

Sequence beats come back on the director (R14), as **odds** the user sets, like every other choice (R2): the chance
that a step of the show is written as two scenes of the same picture, splitting one span of time across them. The
default is **25%** (Ed, 7 Nov): about one step in four is more than one scene. It was 0%. Nothing of this comes back inside the engine, inside Generator Advanced, or by reopening the cinematic
slideshow. Sections 3 to 6 stay the engine: one call. The
director's half is the section after the core principle.

Everything goes through the director, including what feels immediate (R15, R17). The director is what the user meets;
the engine works behind the scenes. Play drafts the next scene when it is needed, keeps the scenes it has made, and so
can use sequence beats, the show's own settings, and the memory of the last scene. Direct Auto-Mix, which v0.31.2 plays,
goes. One layout on the canvas stays one engine call, and is not Play.

---

## What the user meets (R17)

The director is the front. The engine has no screen of its own that the user starts from; its settings are behind one
link. This section is the first thing built and the thing every later slice is checked against.

| Where | What it is | What the user does there |
|---|---|---|
| **Play** on the bar | The director playing the show | Plays the show. A show with no scenes yet is made as it plays, from the preset and values on the Show card, and every scene is kept. Space does the same. |
| **Previous / Next** on the bar | Steps through the show | Moves between the show's scenes. With no show, Next makes one layout on the canvas: the one direct engine use, not kept. |
| **Show** on the bar | Opens the Show card | The only settings button on the bar. There is no Storyboard / Auto-Mix switch: Play always plays the show. |
| **Show card** | The show: what Play plays | **Preset**, one line of buttons (Multi-Image, Cinematic, 1, 2, 3): press to use one, hold or right-click to keep this show's values in it, reset symbol on the right. The show's values (images per scene, duration, smart pace, motion, sequence beats, scenes per step, design odds), **Soundtrack**, and the **Engine settings** link. No images and no Play here. |
| **Show** tab (drawer) | The director's kept scenes | Steps back, edits, reorders, hides and saves scenes; **Add this layout**; **New show** (an empty show with the same preset and values, made from the Image Pool on Play; asks before replacing scenes). Its settings button opens the same show settings as the Show card. No Play: the play bar plays. |
| **Engine settings** (behind the link) | How one layout and one move are made | Images & faces, designs (frames, backdrops, each design's own settings), camera, transitions, canvas and progress. Nothing about one show. Its design odds are the defaults a show uses when it holds none of its own (the Multi-Image preset); a show's pace and images per scene are set on the Show card. |
| **Cinematic storyboard** command | Starts a show from the Cinematic preset | The same director and engine; only the starting values differ. |

Where each control from v0.31.2 goes:

| v0.31.2 | Under R17 |
|---|---|
| Generator button on the bar, opening **Layout Generator Settings** | **Show** on the bar, opening the Show card |
| Layout Generator Settings, first page: images per scene, duration, smart pace, motion | The Show card, as the show's values |
| Storyboard / Auto-Mix switch on the bar | Gone; Play plays the show |
| Advanced (images & faces, designs, camera, transitions, canvas, cinematic) | Engine settings, behind the link; the cinematic group is the Cinematic preset |
| Play in Auto-Mix: a new layout each time, nothing kept | Play: the director, every scene kept |
| Surprise me: one new layout on the canvas | Next with no show: one layout on the canvas, not kept |

### Why this section exists

On 6 Nov slices 1 to 6 were built from this file engine-first: Layout Generator Settings and Auto-Mix stayed what the
user met, and the director, with its presets, sat behind a "(1-9)" button in the storyboard drawer. Ed's direction had
been the opposite since R15: everything through the director, the engine behind it. How it happened:

1. This file grew from an engine-only design (5 Nov, `LAYOUT_GENERATOR_DESIGN.md`). The director was added on top on
   6 Nov (R13 to R16, the director section), but nothing said what the user meets, and section 4 kept the engine
   design's line about keeping Layout Generator Settings' first page "as simple as today". That line contradicted R15
   and was not resolved.
2. The build order had no slice for the screen. Every slice changed the engine or the director's internals, and each
   was checked against its own tests, never against R15.
3. When Ed said twice that it looked the same as before, the build explained it by defaults (beats off, blur Rare)
   instead of seeing the structure was wrong.
4. The presets were put where the old controls already were, instead of where the design said the user works.
5. Reviews checked the file against itself and the code; the file was consistent in an engine-first way, so they passed it.

The rule from this: R17 and this section come first, are built first, and every slice is checked against this screen as
well as its own tests. A reviewer starts here (section 11).

---

## Core principle: one engine, called on demand

The engine is dumb. One call takes parameters and images, and returns one layout and one move. It does not know the
previous call or the next one. Today, direct Auto-Mix is that call repeated, and the result is not kept. Play, in the
future, goes through the director, which makes those calls and keeps them (R15).

**The call, exactly.** One entry point. Every layout and every move in the multi-image area comes from it.

| Goes in | Comes out |
|---|---|
| The images (or the pool to choose from) | The layout (today's `Composition`) |
| One resolved set of values (below) | One move (today's `CameraMove`). Today's two-shot, a snap or cut to the face inside the move 25% of the time, is part of a move and stays as it is |
| The duration to hold | |
| What not to repeat: the last design's recipe, the last kind of move | |
| Optionally, where the camera starts: the previous scene's end frame (sequence beats, below) | |
| Optionally, a layout to keep: the previous scene's layout. The call then rolls no design and plans only the move (sequence beats on one image, below) | |
| A seed | |

The transition into a scene is the director's choice, not the engine's: it is between two calls.

**Today the call is only half real.** The layout half is: `LayoutComposer.Compose` holds no state and is told what
not to repeat (`avoid: previous?.Recipe`). The move half is not. Three callers plan the move themselves, each
differently, so the same settings give different moves depending on who asks (a breach of R4):

| Caller | How it plans the move |
|---|---|
| Canvas and direct Auto-Mix (`MultiViewControl.Camera.cs`, `StartCanvasCamera`) | Faces, Camera mode, the last move, duration plus 2.5 s overrun |
| A storyboard scene with no saved path (`MultiViewControl.Surprise.cs`, playing a scene) | **No faces passed**, so it cannot aim at one |
| `ShowDirector.BuildScene` | Faces, but `ChooseMotionKind`'s own odds override Camera mode; no last move; no overrun |

All three become callers of the one entry point. Nothing outside it calls `CameraMove.Plan` or `ChooseMotionKind`,
and `ChooseMotionKind`'s odds go (R5).

**Memory is passed in, never kept.** The engine keeps nothing between calls, and neither does the screen. Today the
last move is a field on the control (`_lastCameraKind`). It moves to the caller: the director for a show, the canvas
for a canvas layout, each passing it in as "what not to repeat".

**One rule for every value.** A value comes from the scene if the scene holds it, else from the show, else from Layout
Generator Settings. It is resolved once, in one place, before the call. The engine receives one set of values and
never knows which level each came from. A canvas layout has no scene and no show, so it is the engine's settings
alone.

The storyboard orchestrates. It keeps an ordered list of calls. A temporal idea is two calls, not a sequence inside the
engine. To show a photo and then snap to the face, the storyboard stores two scenes of the same image: the first called
with the wide parameters, the second called with the zoom aimed at the face.

A cinematic storyboard is that list. Each scene calls the same engine. A **limited** parameter narrows a control the
engine already has, such as one image, or duration 10 to 15 seconds. A **special** parameter sets a different value of
a control the engine already has, such as the zoom aimed at a face, or a small rotation. The engine does not gain a
shot list, a second phase inside one hold, or a reader for the music. The storyboard chooses the duration it passes.

The two are separate controls. Engine settings (Layout Generator Settings in v0.31.2) belong to the engine: they say
how one call makes a layout and a move, and they sit behind the Show card (R17). A single layout on the canvas, with no
show, uses those settings and keeps nothing. The show belongs to the director: it keeps the ordered calls, and it keeps
the values it passes, edited on the Show card and the storyboard. A cinematic
storyboard's usual values, and any value a single scene passes of its own, are edited with the storyboard. They are
not a group inside Generator Advanced. Putting them there makes the director look like more generator settings, which
is how the two keep being merged into one.

---

## The director

Ed called this the right direction on 6 Nov, and it is the front of the app (R17). The rows are the proposal that
follows from R13 to R17, not a second list of word-for-word requirements.

The storyboard already is a director, limited to basic sequencing. It keeps the scenes in order, drafts each one by
one engine call, and avoids repeating the last design, the last transition and the last kind of move. It alternates
short and long holds. The user can reorder, hide, remove, duplicate and add scenes, and Camera Director can edit one
scene's path. It cannot plan two calls as one idea, and the show's own values are edited inside Generator Advanced.

**Show settings live with the show.** Images per scene, design odds, duration, smart pace, motion, sequence beats and
Scenes per step for this show are edited on the Show card, and from the storyboard's settings button. A new show starts from the engine's settings when the user has not chosen a preset. After that,
live play uses the show's values.

**Presets: one engine, five sets of values.** A preset is a saved set of the show values above, nothing else; every
preset plays through the same director and the same engine call.

| Preset | Kind | Values |
|---|---|---|
| Multi-Image | Built in | The engine's defaults: design odds all six at Normal, sequence beats 25% |
| Cinematic | Built in | 3.7: one image, Edge to edge Often, Hero spotlight Rare, others Off, 10 to 15 s, smart pace, motion 40% to 60%, sequence beats 25% (Scenes per step 2 to 2) |
| My presets 1, 2, 3 | The user's | Whatever the user saved into them, the same full set of values, sequence beats included (25%, say). Empty until saved |

A built-in preset's values can be changed on a show; the preset itself is the starting point. Choosing a preset copies
its values into the show (section 7). Presets are kept in the app's settings, not in a show file: the built-in
Cinematic values where they are read today, and the user's three beside them, new and empty in an old settings file.
They are not called "slots": the viewer already has window-size slots 1, 2 and 3 (`WindowPresets`), and one term
means one thing (R4).

**Play is the director, drafting on demand.** Play does not use direct Auto-Mix (R15). The director calls the engine
for the next scene when playback needs it, and starts once that scene is ready. A large pool is not drafted first.
Scenes already made stay in the storyboard, so the user can step back, edit and save. That kept list is the enhanced
option. The engine call is the same either way. A canvas layout is not Play.

**Play stays one scene ahead.** Play stands or falls on this, so it has numbers. The director starts drafting the
next scene as soon as the current one starts playing, off the UI thread. The next scene must be ready before the
current hold ends. If it is not, the current scene keeps holding (its move ends and the frame stays still) until the
next is ready. The screen never goes blank and Play never stops for this. The first scene of Play is shown as soon as
it is drafted, without scanning or drafting the rest of the folder. Targets, measured in slice 3 (Build order) on
Ed's largest folder: first picture within 1 s of Play, and no scene held past its duration over 30 minutes of play.
Neither is measured yet. A miss is the first thing fixed, before anything else in slice 3.

**Sequence beats: further views are the director writing further scenes.** Two settings on the show, edited on the
storyboard and rolled for every step (R2, R16):

| Setting | What it is | Default |
|---|---|---|
| **Sequence beats** | Odds that a step is more than one scene | 25% (Ed, 7 Nov). 0% is one scene every step |
| **Scenes per step** | A range: how many scenes a step that hits may be | 2 to 2. Up to 4 |

At 25% about one step in four is more than one scene. A miss stays one scene, and that scene's call plays today's two-shot by its own odds, unchanged.
0% is one scene every step.

When the roll hits, the director rolls a count in Scenes per step and writes that many scenes, each a closer view than
the one before. It stops early, as an ordinary short step, when there is no closer view left:

| Starting from | Next closer view |
|---|---|
| Several images | One image from them: the featured one if there is one, else rolled, weighted to images with faces |
| One image with faces | A face, aimed by the zoom (the largest first, then the next) |
| One face, or no face and no featured image | None |

| Situation | What the director writes | What each engine call gets |
|---|---|---|
| The roll misses | One scene | That scene's values. Today's two-shot by its odds |
| The roll hits | The count, as closer views | One call per scene. A view of the same image keeps the previous scene's layout and starts from its end frame |
| One layout on the canvas, no show | Nothing. There is no list | One call, not kept |

**Each scene is an ordinary scene.** It takes its hold from Duration and smart pace like any other, so a step is not a
split of one span and has no minimum of its own (R4). Inside a step of more than one scene, the
calls pass two-shot off. A justified fixed rule (R2): the step already is the second view, kept, and a two-shot on top
of it would show the same face twice. One scene steps roll the two-shot as today.

**The change inside a step.** Between two views of one image it is a **Cut**, fixed: the layout does not change,
only the camera, so a crossfade or slide would blend the picture into itself and show nothing. Cut is a new transition
kind (today `LayoutTransition` has Crossfade, Zoom through, Through dark and Slide), used only there and not added to
the ordinary transition odds. A change to a new layout (group then one image) takes an ordinary transition. The scene
before a change of view inside one image has no overrun (today the move runs 2.5 s past the change, `CameraOverrunSeconds`): its end
frame is where the next starts, so an overrun would show the camera somewhere the next scene is not.

The scenes are ordinary kept scenes: stepping back shows each, and any can be edited or removed. The file stores no
grouping.

The director passes the last design and the last move into each call. If the pictures allow only one shape, it repeats. The engine remembers none of this.

The move inside a call comes from the camera settings, through the one entry point (Core principle).
`ShowDirector.ChooseMotionKind` and its odds go (R5). The director's part is passing in the last move so it is not
repeated (R13).

Both built-in presets ship at sequence beats 25% (Ed, 7 Nov). A preset may hold any other value, including 0%.

A design that cannot hold the image count drops out of that call's roll of design odds.

**A show file carries its own values.** Today a show stores only the name of its overrides
(`ShowProject.GeneratorOverrides` = "Cinematic"); the values themselves sit in the app's settings
(`SurpriseOptions.Cinematic`), so changing them there changes every cinematic show. Under R13 the values are the
show's. Section 7 has the file rules.

Still out, and stated in section 1 where they are requirements: a locked design and backdrop (R16), a special first or
last scene (R16), the engine reading the music, titles, chapters, and the slideshow's shot templates (R14). The director chooses
durations. Landing a cut on the music is a later way of choosing them (3.10).

---

## Build order

**Slice 0: the screen the user meets (R17).** Built first in any rebuild; on 6 Nov it was built last, by mistake (see
"Why this section exists"). It is "What the user meets": Show on the bar, the Show card, Play always the director, the
engine's settings behind one link, no Storyboard / Auto-Mix switch. Every slice below is checked against this screen,
not only its own tests.

Thin slices, each one something Ed can see running. Each slice is finished, shown, and adjusted before the next
starts. The rest of this file is detail for those slices; tunable numbers start as stated defaults and change once
seen.

| Slice | What it builds | What Ed sees | Not in it |
|---|---|---|---|
| 1. One engine call | The entry point in the Core principle: layout and one move from one place; the three move planners become its callers; memory passed in; the one rule for values. Today's two-shot stays inside the move | The same settings giving the same kind of moves on the canvas, in Auto-Mix and in a storyboard; a storyboard scene with no saved path aiming at faces | Anything else |
| 2. One image through the designs | 3.1 to 3.4 for one image: the Single images branch goes, one-image layouts roll the design odds, Crop at most replaces the 65% rule, shaped cells measured by shape | A portrait and a landscape regenerated: full-screen, framed, hero, print, circle, panel, by the odds; a portrait shown whole when the fill would crop past Crop at most | New per-design settings (3.3), frame and backdrop changes |
| 3. Play through the director | Play drafts one scene ahead and keeps the scenes; the show file carries its values; the cinematic values leave Generator Advanced and are edited on the storyboard; old settings and shows migrate (section 7); the five presets | Play starting at once on a large folder, stepping back through kept scenes, a cinematic storyboard edited on the storyboard, an old show opening as it did | Sequence beats above 0% |
| 4. One move on one image | 3.5: the zoom order, no double zoom | Zoom frequency doing what it says | |
| 5. Design settings | 3.3, Frame color and Backdrop for every design | Frames and backdrops varying by the odds | |
| 6. Sequence beats | The director section: the odds, Scenes per step, closer views, Cut between views of one image | A portrait as wide then its faces, a group as group then one image then a face, all kept | |
| Later | 3.10, cut on the music | | |

The fundamentals, which are expensive to reverse and get the most care: the engine is one call, one entry point, one layout and one move, with no memory
(Core principle); what a show file saves (section 7); Play never stalling (the director); and anything unread,
missing or old loading and playing as it does today.

---

## 1. Requirements

From Ed's own words. Dates are 2026. Where a requirement is a reading of his reaction rather than his words, it says so.
Where a proposal needs a reason those words do not already state, the reason is written here. It is not hung on a
nearby requirement.

| # | Requirement | Source |
|---|---|---|
| R1 | **Easy to use, yet powerful if needed.** Sensible defaults up front; finer control folded away. | 4 Nov: "we want it to be easy to use, yet powerful if needed". 1 Nov: "keep this easy for the user, not show too much complexity, while still giving them fairly powerful options". |
| R2 | **Options shape the odds; nothing is disabled.** Ranges, thresholds and probability curves are the options. A choice can be made rare, never impossible; the Off end of an odds scale is the user's own choice. A fixed value is fine when it is justified: it simplifies something very complex, or a range would add close to nothing. Each fixed number in section 6 says which. | 31 Oct: "why the heck would I disable any? those aren't options"; "changing ranges, thresholds, and probability curves to favour certain things are legit value added options". 6 Nov: fixed values are fine "if justified, if it simplifies something very complex, if there is close to nil added value to have a range". |
| R3 | **No single rule for single images.** A user-defined random distribution: sometimes cropped, sometimes not, "in line with the design pattern for the rest". | 4 Nov. |
| R4 | **Consistency.** One mechanism, one pattern, one term and one kind of control for one kind of setting, everywhere. No ad hoc exceptions. | Repeated: one term for one thing; "follow the standard UI design pattern for motion"; 5 Nov, rejecting designs that applied only to some layouts, with single images and portraits each on their own mechanism, as an inconsistent design. |
| R5 | **No arbitrary hard-coding.** No rule the developer decided that overrides or bypasses the user's settings. | 5 Nov: asked where else a hard-coded rule, decided by the developer, overrides the settings. |
| R6 | **Random variety.** Regenerating gives a different result whenever the settings allow more than one. | 5 Nov: "regenerate results in the same thing ... rather than random"; "hard coded to show the same thing over and over again". |
| R7 | **Frames follow the user's choice and vary.** | 5 Nov: "frames are random depending on user choice". |
| R8 | **Sensible defaults.** | 5 Nov: "pick reasonable defaults"; zoom defaults 4x, 70%, 85% set by Ed. |
| R9 | **Name things by what they do; a design's settings sit under that design.** | 5 Nov: "move design items into the design group"; earlier naming decisions (Layout Generator, Image Pool, Auto-Mix). |
| R10 | **A cinematic storyboard calls the one engine. A portrait shows the whole image, with no empty space above and below, when Crop at most says the fill would be too deep.** The cinematic slideshow's shot sequences are not part of this requirement. | 4 Nov (end goal: cinematic folds into the one generator); 5 Nov: "true cinematic shows the whole image without empty space top and bottom for portrait". The earlier sentence "without losing what it does" was a reading. It is withdrawn, because it was taken as "copy the slideshow's director." |
| R11 | **Zoom you control, with or without faces**, for the layout and for the camera (Ken Burns). | 1 Nov (Ken Burns for Surprise me, "how much zoom" proposed); 5 Nov: "forget faces. where are the general zoom settings that control the camera???" |
| R12 | **Every layout goes through the designs**, one image included. *(Reading of Ed's 5 Nov reaction to designs applying only to layouts of two or more.)* | 5 Nov. |
| R13 | **The engine is one call. The storyboard orchestrates. They are two entities.** One call returns one layout and one move. A second view of the same image is another scene, with different parameters. Layout Generator Settings, including Advanced, hold how one call is made. The storyboard holds which calls it makes and the values it passes. A cinematic storyboard's parameters are that second entity. They are not a section of Generator Advanced. | 6 Nov: the engine triggers on demand. The storyboard plays a scene and then the same image snap-zoomed by calling the engine again. Do not add that sequence to the engine. Same day: the design is the core engine, and a storyboard that directs it. Putting cinematic storyboard parameters into Generator Advanced treats the two as one, and that is the friction. |
| R14 | **The foundation is the multi-image engine, built from scratch.** Sequence beats were removed to rebuild the cinematic engine from fundamentals. That rebuild did not succeed. Cinematic slideshow was abandoned as the engine to rebuild. Sequence beats come back when there is an appropriate design. | 6 Nov: "we removed it as I wanted to rebuild the engine from fundamentals - that did not succeed. Instead we abandoned the cinematic slideshow and built the multi-image engine from fundamentals, from scratch. When we have an appropriate design we will bring it back." |
| R15 | **Play goes through the director, including when it feels immediate.** For most scenarios the user does not use direct Auto-Mix. The director is what provides the enhanced options. It still generates on demand, so Play does not wait for a finished show. A canvas layout stays one engine call. *(The canvas-layout sentence is the reading of "most scenarios," so one layout is not forced into a show.)* | 6 Nov: the director proposal is the right direction. "In the future, the user really should be going through the director for most scenarios including seemingly real time generation, as that gives you enhanced options. Rather than going to auto-mix directly." |
| R16 | **Each call rolls. Nothing is locked, and no scene is special because of where it sits.** The odds a call was given are rolled every time. Narrow odds are not a lock: Hero Rare can still come up, and Hero Off never does. A scene differs only when its own parameters say so. There is no rule for the first scene and no rule for the last scene. Play that is still generating has no last scene. *(Reading. He did not list these two rules. They are what "one engine, and the storyboard is that engine with specific settings" rules out.)* | 6 Nov: "what you are proposing is effectively a branch, rather than the concept of one core engine. And cinematic storyboard is merely more of the same but with specific settings." |
| R17 | **Everything goes through the director. The director is what the user meets; the engine works behind the scenes.** Play, a show, a cinematic storyboard and live generation are all the director, with a preset to start from and the show's own values. The engine's settings (faces, zoom, crop, camera, frames, backdrops, transitions) sit behind one link and are not the first thing the user sees or uses. The only direct engine use is one layout on the canvas, which is not Play. | 6 Nov: "two fundamental design components, of which the most important would be the director, and the engine relegated to a behind the scenes engine that the user wouldn't access first hand." R15 already said it: "the user really should be going through the director for most scenarios including seemingly real time generation." |

---

## 2. Today (v0.31.2): three mechanisms

1. **Two or more images:** six designs, chosen by design odds (`LayoutComposer.Compose`).
2. **One image:** no design. Its own odds, Fill / Matted / Whole image (`ComposeSolo`, `SurpriseOptions.PickSolo`). Matted
   is a one-image Gallery wall built a second way.
3. **One image that filling would crop past the 65% rule, or through a face:** a third branch inside (2). Fill becomes
   "Fitted", whole at full height. A 2:3 portrait on a 16:9 screen is in this branch. A portrait only slightly taller
   than the screen is not, when more than 65% of it would still show.

**Play today.** Continuous play is direct Auto-Mix. Play storyboard and Cinematic storyboard go through `ShowDirector`
and keep scenes. R15 replaces direct Auto-Mix with Play. A canvas layout stays one engine call.

Breaches of the requirements today:

| Today | Breaks |
|---|---|
| Designs skip one-image layouts; single images have their own odds and look | R4, R12 |
| Portraits have a rule no design cell has (65% shown) | R4, R5 |
| Frame color and Frame width reach Gallery wall, Matted and Whole image only; Hero, Scattered prints and Circles are always white | R4, R7 |
| Odds fixed in code with no setting (section 6) | R2, R5 |
| Caps on images per design (9, 6) are fixed and unstated | R5 (Q4 states the reason) |
| Cinematic storyboard's defaults (Fill only) leave a portrait one shape every time; only zoom varies it | R3, R6 |
| Cinematic storyboard parameters sit inside Advanced Layout Generator Settings: images per scene, Fill / Matted / Whole, duration, smart pace, motion, and the switch that applies them to the open storyboard. The Cinematic storyboard button says to change the look there | R13 |
| The move is planned by three callers three ways (canvas and Auto-Mix, a storyboard scene with no saved path, `ShowDirector`); the storyboard path passes no faces; `ChooseMotionKind` overrides Camera mode; the last move is kept on the control | R4, R5, R13 |

---

## 3. Proposed design

### 3.1 One pipeline for every layout (R4, R12)

| Step | Decided by | Requirement |
|---|---|---|
| 1. How many images | **Images per scene**: any count in the range, each equally likely. Fixed: the range already says how many, and a bias inside it adds a control for almost no visible difference | R2, R4 |
| 2. Which design | **Design odds** among designs that can take that many. A design at Off is never used. If none left on can take that many, the layout takes fewer. Each call rolls the odds it was given. The engine's odds are the usual ones. A storyboard may pass narrower odds (3.7). Narrow odds are not a lock (R16). | R2, R12, R16 |
| 3. Arrangement | The design and its own settings (3.3) | R9 |
| 4. Each cell's crop | **Crop at most** (3.4) | R3, R4, R10 |
| 5. Each cell's zoom | One image: the cell opens on the fit or the fill, and **Zoom** is where the move arrives (3.5). Several images: **Zoom** inside each cell | R10, R11 |
| 6. Which version | Versions that cut a face (**Face kept in frame**) or mostly hide an image are set aside; the pick is from the best **Pick from the best** | R1 |
| 7. Camera while playing | Camera settings (3.6). A storyboard scene may pass its own values of those settings. The storyboard keeps those values (3.7). Generator Advanced does not gain a group for them | R6, R10, R11, R13 |

### 3.2 Designs, including one image (R3, R12)

| Design | 1 image | 2+ images | Most images |
|---|---|---|---|
| Edge to edge | Fills the screen; shown whole if Crop at most would be passed | A mosaic filling the screen | Q4 |
| Gallery wall | The image set in card (today's Matted) | A mosaic set in card | Q4 |
| Hero spotlight | The image as the hero, alone on its backdrop (takes over today's Whole image) | A hero over a mosaic of the rest | Q4 |
| Scattered prints | One tilted print | Tilted prints | Q4 (today 9) |
| Circles | One round image | Circles of varied size | Q4 (today 9) |
| Slanted panels | One leaning panel, full height | Leaning panels | Q4 (today 6) |

With one mechanism, a one-image layout varies the way any layout does: by design odds, frame odds, zoom and the
design's own settings (R3, R6). Each call rolls the design and the backdrop from the odds it was given (R16). A
cinematic storyboard passes the narrower odds in 3.7, and those odds are still rolled. The **Single images** section
is removed.

### 3.3 Settings that belong to designs (R2, R7, R9)

Shared by every design that has the thing, shown once above the designs:

| Setting | Applies to | Default |
|---|---|---|
| **Frame color**: Any / White / Black / From the images (Any picks per layout) | Every frame: Gallery wall card, Hero outline, print borders, circle outlines | Any (Q13). Today Hero, prints and circles are white, so this changes them |
| **Frame width**: range | The same frames; each layout picks a width in the range | 1x to 1x (0.25x to 3x allowed). It was a single value here, against the rule that settings are ranges unless a fixed value is justified; a saved single width reads as that width alone |
| **Backdrop**: odds of ambient blur, vignette, gradient, solid | Every design with a backdrop | Vignette Normal, Gradient Normal, Ambient blur Rare, Solid Rare (Q5). Today is vignette or gradient, 50/50 |

Under each design, folded away:

| Setting | Design | Default (today's look) |
|---|---|---|
| **Gaps**: odds of none or hairline | Edge to edge | none 1 time in 3 |
| **Rounded corners**: odds | Gallery wall | 1 time in 3 |
| **Gap size** (today's random 1.2% to 2.4% of the short side) | Gallery wall | as today. A setting only if Ed says so in Q6; the recommendation there is to keep it fixed |
| **Hero size**: range | Hero spotlight | 62% to 82% (exists) |
| **Hero shape**: odds of arch, oval, squircle, hexagon (and rectangle?) | Hero spotlight | equal; Q7 |
| **Hero position**: odds of left, centre, right | Hero spotlight | equal |
| **Print tilt**: range | Scattered prints | up to 6.5 degrees (exists, as a maximum) |
| **Print size variety** (today's random 1.05 to 1.3 of an even share) | Scattered prints | as today; Q6 |
| **Lean**: range | Slanted panels | 0.2 of a panel's width (today fixed) |
| **Mirrored**: odds | Slanted panels | 50% |
| **Circle size variety** (today 0.82 to 1.02) | Circles | as today; Q6 |

**Featured** images keep their place: always used, the best place in each design (the hero, the largest print or circle).
How much larger a featured print or circle is (today 1.5x) becomes a setting: **Featured size**, default 1.5x (R5).

### 3.4 Crop at most (R3, R4, R10)

Replaces the 65% portrait rule with one setting for every cell in every design: **how much of an image may be cropped
away to fill its cell**.

- Default **35%** (today's limit of 65% shown). Q1.
- **Safe crop rule**: A cell shows its image whole inside the cell on its backdrop if filling the cell would crop away
  more than **Crop at most**, OR if filling would crop a face past **Face kept in frame** (default 85%).
- When filling the cell is safe, the crop is anchored to the faces or focus point (with the 8% headroom above center)
  rather than blind geometric center cropping.
- When shown whole, the image sits inside the cell at full height or full width, unframed on Edge to edge, backed by
  its backdrop (the Backdrop odds in 3.3; Q5).
- So one portrait on a landscape screen shows whole at full height when filling would exceed Crop at most (R10), but fills and crops to pan across the subject when within the threshold—one universal rule, not an ad-hoc portrait rule.
- At 100%, nothing is ever shown whole for cropping area (though face protection still holds). At 0%, every image is shown whole.
- Mosaic designs already prefer arrangements whose cells match the images' shapes, so in a mosaic this rarely triggers.
- **A cell that is not a rectangle** (a circle, the hero's arch, oval, squircle or hexagon, a leaning panel) is measured
  against the shape, not its bounding box. The box is larger than the shape, so a crop that "passes" against the box
  still hides the corners, and Crop at most would not mean what it says (R5). Filling is safe only when the shape itself
  keeps at least **Crop at most** of the image and keeps the face. A shape cannot show an image whole (the renderer
  fills every cell), so a design whose shape would crop too far **takes no part in that call**, as a design that cannot
  hold the image count does (Ed, 6 Nov). Edge to edge, Gallery wall and Scattered prints can always show an image
  whole, so a layout is always made. The corners a circle must hide count toward the crop.

### 3.5 Zoom (R11)

The range and the frequency stay as built in v0.31.2. A single image uses them as the end of one move:

- **Zoom**: a range, default 1.5x to 4x (Ed's 4x). **Zoom frequency**: default 70% (Ed's). Every image rolls; one that
  zooms picks an amount in the range, toward its faces or, with none found, its focus point.
- It stops short of cutting a face, and of showing the photo past twice its own pixels. Both are numbers the user cannot
  change today (section 6): Q8.
- **Several images** keep both moves. **Zoom** crops inside a cell. **Camera zoom** moves over the whole layout.
- **One image is one move**, decided in this order. The cell opens on the fit or the fill from 3.4. Nothing zooms that
  frame and then zooms it again.
  1. **Camera mode** Zoom in, Zoom out or Drift is the move on every scene. Zoom frequency is not consulted, because the
     user already chose the kind of move (R5). A drift stays on the 3.4 frame and uses the slight motion zoom (1.06x to
     1.3x from Motion). Zoom in travels from the 3.4 frame to the **Zoom** amount. Zoom out travels from that amount back
     to the 3.4 frame.
  2. **Camera mode Auto.** Roll **Zoom frequency**. A hit travels between the 3.4 frame and the **Zoom** amount, in or
     out with equal chance, aimed at the faces or the focus point. A miss then rolls **Zooms rather than drifts**. A zoom
     on a miss uses **Camera zoom** (1.15x to 1.8x) from the 3.4 frame, in or out with equal chance. A drift stays on the
     3.4 frame with the slight motion zoom. The miss roll does not run after a hit, so a 70% setting reaches the Zoom
     range 70% of the time (R5).
- One call is this one move and no second phase. A snap onto the face after the wide view is another call, stored as
  the next storyboard scene (R13).

### 3.6 Camera (R11)

As built in v0.31.2:

| Setting | Default |
|---|---|
| Camera mode: Auto / Zoom in / Zoom out / Drift | Auto |
| **Camera zoom**: range, how far each zoom goes | 1.15x to 1.8x |
| Motion: range, how far a drift goes (0 is still) | 40% to 60% |
| Zooms rather than drifts | 50% |
| Aims at faces | 70% |
| Two-shot scenes | 25%, inside the move |
| Movement curves, Curved path, Rotation | as today |

The table is what is built. The rest of this subsection is the proposal.

One call aims where that call's parameters say. For one image, that is the faces or the focus point. For several
images, the one move may aim at the featured image or the largest face for the whole of that call. A wide view of a
group and then a close view of one photo are two scenes, written by the director (R13).

Today's two-shot (25%, a snap or a cut, inside one move) stays as it is. This proposal does not raise it, and does
not add a face-to-face move, a pull-out, or a second phase that spends most of the hold on the close view. A further
view is sequence beats, on the director. The calls of a step of more than one scene pass two-shot off (director
section); one-scene steps roll it as today.

**Still scenes** is new: odds that a call plays no move, default 0%. It replaces the stills `ChooseMotionKind` added by
its own odds (5% to 35% by image count, R5). A scene's own Still, from its camera menu, is unchanged.

**Rotation** is the tilt of the one move. Its default is off, as today. A storyboard scene that should tilt passes a
small value, such as 0.5°.

Fixed today and listed in section 6: the 2.5x ceiling, the wide shot's zoom in a two-shot. Q9. Today a drift is sideways
3 times in 4. The proposal follows the crop instead: vertical when the crop is tall, horizontal when it is wide, either
when both have room. A tall crop's spare room is vertical, so a sideways bias repeats one move. It is not a new setting
(Q9). There is no first-scene rule and no last-scene rule (R16).

### 3.7 What the storyboard keeps and passes (R10, R13)

The engine's settings are the engine's controls, behind the Show card (R17). The show does not add a section to them,
and it does not store its values inside them. It keeps an ordered list of calls, and it keeps the values each call
passes. The user edits those values on the Show card and the storyboard.

A cinematic storyboard is one such list. Its usual scene passes the limited values below. They are values of controls
the engine already has. A scene that should differ, such as the same photo snapped to a face, keeps different camera
values on that scene, and the same image. Widening Images per scene, or turning a design on, is the user editing that
storyboard. The engine is unchanged (R2).

| Parameter | Kind | Usual cinematic value |
|---|---|---|
| Images per scene | Limited | 1 to 1 |
| Design odds | Limited | Cinematic only: Edge to edge Often, Hero spotlight Rare, others Off (Q3). The Multi-Image preset and the generator default are all six at Normal |
| Backdrop odds | Not passed | The shared default (3.3, Q5): Vignette Normal, Gradient Normal, Ambient blur Rare, Solid Rare |
| Duration | Limited | 10 to 15 s. The storyboard may pass a chosen duration instead |
| Smart pace | Limited | On, when the storyboard does not pass a chosen duration |
| Motion | Limited | 40% to 60% |
| Sequence beats | Limited | 25%; Scenes per step 2 to 2 (director section) |

Where the scene does not pass its own zoom, frames, backdrop, camera mode or rotation, the call uses the engine's
settings. Direct Auto-Mix, today, reads the engine's settings only. Play uses the show's values (R15). A canvas layout
reads the engine's settings and is not kept.

Today (v0.31.2) those cinematic values are a section of Advanced Layout Generator Settings, and the Cinematic
storyboard button points there. That section is the merge this proposal separates. The values move to the storyboard.
They do not stay as a named override group inside the generator's settings.

### 3.8 Unchanged

Face detection, Face kept in frame (85%), Pick from the best (3), Same folder and date (80%), Skip screens and repeats,
Images per scene (default 1 to 9, ceiling 16), the Duration range, smart pace, transitions, movement curves, Camera
Director, and today's two-shot. Titles, chapters and a shot list are not added; the music is read only by the director, later (3.10).

### 3.9 What the storyboard does, and the engine does not

The engine holds for the duration it was given. It does not read the soundtrack. If a cut should land on the music, the
director chooses the durations it passes (3.10, later).

Beyond today's two-shot, the engine does not play two views in one call. Showing the photo, then the face, as two kept scenes is sequence beats.

Cinematic Slideshow used to have **Sequence beats** (`BeatCount`, default 1), set on the show and overridable on one
slide. Its director split that slide's duration across that many moves, and when the slide's own sequence was shorter
it added further moves until the count was met. That control was removed on 1 Nov 2026, with technique and curve, so
the cinematic engine could be rebuilt from fundamentals. That rebuild did not succeed. The slideshow was abandoned as
the engine to rebuild, and the multi-image engine was built from scratch instead (R14). Older cinematic shows drop
the setting. The cinematic director still divides one slide's time across the shots it already chose. It no longer
asks for a count and invents the rest.

Multi-image has no sequence-beats control today. The storyboard passes a duration, and the engine may play today's
two-shot inside that one call. Under this design that stays, and sequence beats come back on the director as odds,
default 25% (Ed, 7 Nov; director section; R14, R15). They do not come back inside this engine, inside Generator Advanced, or by reopening the
cinematic slideshow. Sequence beats are views, not music: they never read the soundtrack.

---

### 3.10 Cut on the music (later, after the six slices)

The director chooses each scene's hold; landing the change on a beat is one more way it chooses. The engine does not
change and does not read the music (R13). **Not part of approving this design:** nothing here is in the six slices, and it is reviewed and approved on its own when its turn comes.

**Today.** The hold is chosen when a scene is drafted (`ShowDirector.BuildScene` -> `SurpriseOptions.NextPlaySeconds`)
and saved as `ShowScene.DwellSeconds`. The music is `Cinematic/MusicBed.cs`: a Windows `MediaPlayer` playing one file
on a loop, or a folder of songs filtered by stars, with next and previous, saved in `ShowSoundtrackSettings`. Nothing
reads the audio. A folder shuffles, the user can skip, and a saved show replays over other music, so the song under a
scene is not known when it is drafted.

**The rule: the cut moves at play time, inside Duration.** The draft keeps choosing and saving the hold as today; that is
the planned end. When a scene starts playing, the director moves its end to the beat nearest the planned end, between
start + shortest and start + longest Duration. `DwellSeconds` is not changed, so a show replayed over other music, or
with music off, plays as drafted. The transition starts on the beat.

| Situation | What the scene holds |
|---|---|
| Music off, failed, volume 0, beats not yet read, or read with low confidence | The planned hold |
| Random duration off (one fixed value) | The planned hold |
| Beats read, one inside the window | The beat nearest the planned end |
| No beat inside the window | The planned hold |
| The song changes during the hold | The next song's beats if read by then, else the planned hold |
| The scene has a Camera Director path | Same rule; the path is stretched to the new hold. Not checked: how a path behaves today when a hold changes |

No rule for the first or last scene (R16).

**Reading the beats.** A song is decoded in the background to a low-rate mono signal; an onset envelope gives tempo and
beat times. Assumption, not verified: `Windows.Media.Audio.AudioGraph` decodes the same files `MediaPlayer` plays, with
no new dependency. The song starts at once; until it is read, scenes take the planned hold. A read below a confidence
threshold counts as unread, so free-time music and speech fall back to smart pace. Results are cached per file (path,
size, modified time). The audio never leaves the machine.

**The control.** One switch in the storyboard's Soundtrack section, **Cut on the beat**, default on: with nothing read
it changes nothing. A canvas layout is not affected; Play, which is always the director, is (R15, R17).

| Number | Value |
|---|---|
| Beat window | Duration's shortest to longest, from the scene's start |
| Which beat | Nearest the planned end. Bars do not outrank beats |
| Confidence threshold | Internal; set from check M1, then fixed and stated |
| Cache key | Path, size, modified time |

Working defaults, not reviewed decisions: on by default; the transition's start, not its midpoint, sits on the beat;
beats not bars; adjust at play time only, never save the snapped hold.

Checks before it is called done:
- M1. A corpus of real songs (steady pop, rubato piano, spoken word, silence, a very short file, one that will not
  decode): how many read, how many fall back; listened to on the steady ones.
- M2. 30 scenes over a steady song: every hold inside Duration, every change on a beat of the read grid.
- M3. Music off, volume 0, a failed song, random duration off: every hold equals its saved `DwellSeconds`.
- M4. Skip a song mid-hold: no error, the hold ends inside Duration. A saved show replayed with other music keeps its
  saved holds. The first song starts without waiting for its read.
- Each check fails with the change taken out.

Out of scope: motion timed to the music, effects that react to it, drops or sections, and any change to how music is
chosen or played.

## 4. What the user sees change

The screen comes first: "What the user meets" (R17) has its own table of where every v0.31.2 control goes. The rows
below are what changes inside the engine's settings and in what the engine makes.

| Today | Proposed |
|---|---|
| Advanced > Designs > Single images: Fill the screen, Matted, Whole image | Removed. One-image layouts use the design odds. |
| Frame color, Frame width under Gallery wall | Above the designs, for every framed design. Frame color defaults to Any (Q13) |
| Hero spotlight: Hero size | Hero size, Hero shape, Hero position |
| Scattered prints: Print tilt | Print tilt (a range), Print size variety |
| Slanted panels, Edge to edge, Circles: no settings | Lean, Mirrored; Gaps; Circle size variety |
| Nothing for backdrops | Backdrop odds: Vignette Normal, Gradient Normal, Ambient blur Rare, Solid Rare (Q5) |
| Nothing for crop | Images & faces: Crop at most |
| Advanced > Cinematic storyboard: images per scene, Fill / Matted / Whole, duration, smart pace, motion, and "use for this storyboard". The Cinematic storyboard button points here | Removed from the engine's settings. They are the Cinematic preset; the show keeps its values and passes them (3.7). The engine is called the same way |
| One image is cropped by Zoom, then the camera zooms again | One image opens on the fit or the fill. Zoom is where that move arrives |
| Direct Auto-Mix plays by an engine call and keeps nothing | Play goes through the director. It drafts the next scene when it is needed and keeps the scenes it has made (R15). A canvas layout stays one engine call |
| No sequence beats in multi-image | Sequence beats (odds, default 25%) and Scenes per step (range, default 2 to 2) on the Show card. When the roll hits, a step is kept scenes, each a closer view |
| Cinematic values in Generator Advanced | Five presets on the Show card: Multi-Image, Cinematic, and three of the user's |
| The canvas, Auto-Mix and a storyboard plan moves three ways; a storyboard scene with no saved path ignores faces | One entry point plans every move from the same values |

Folded under each design, in the engine's settings behind the Show card (R17), so what the user meets first stays simple (R1).

---

## 5. Trace: every element to a requirement

| Element | Requirements |
|---|---|
| One pipeline, designs for 1 to 16 images | R4, R12 |
| Single images section removed | R4, R12 |
| Crop at most replacing the portrait rule | R3, R4, R5, R10 |
| Frame color and width for every frame | R4, R7 |
| Per-design odds and ranges replacing fixed odds | R2, R5, R9 |
| Featured size as a setting | R5 |
| Zoom for every image, Camera zoom | R11 |
| One zoom on a one-image scene, in the order in 3.5 | R5, R10, R11 |
| A fill is anchored on the faces or the focus point | R5, R10. A centered fill can cut a face. Face kept in frame is what stops that |
| Drift follows the crop | Q9. A tall crop's spare room is vertical, so today's sideways bias repeats one move. Not a setting |
| Counts inside Images per scene are equally likely | R2. The range is the setting. The flat distribution is fixed and stated: a bias adds little |
| Crop at most on a shape that is not a rectangle | R5. Measured against the shape (3.4) |
| A second view of a group is the next scene | R13. One call may aim its one move at one image. It does not then spend the rest of the hold on that image |
| Cinematic storyboard calls the one engine; the scenes are kept. Its shot sequences are not copied | R10, R13, R14. R10 is the portrait and the one engine. It is not the slideshow's director |
| Storyboard values edited with the storyboard, not inside Generator Advanced | R13. The engine's settings say how one call is made. The storyboard's settings say which calls, and with which values. One surface for both is how cinematic parameters keep being added to the generator |
| Each call rolls the odds it was given. No locked design and backdrop. No special first or last scene | R16 |
| One entry point for layout and move; memory passed in; one rule for values | R4 (one mechanism; today three move planners disagree), R13 (the engine is one call), R5 (`ChooseMotionKind`'s odds override the user's Camera mode) |
| Sequence beats on the director, as odds, default 25% | R13 (a second view is another scene, Ed 6 Nov: "do not add that sequence to the engine"), R14 (sequence beats come back on the director). R2 (odds, not a count). Ed 7 Nov: the default is 25%. Ed 6 Nov had said 0% |
| Scenes per step as a range; each scene a closer view; each scene's hold from Duration; two-shot off inside a multi-scene step; Cut between views of one image | R2 (a range, not a cap), R4 (a scene is a scene), R2 (two-shot off inside a step is justified: the step already is the second view). Group then one image is the proposal Ed called the right direction on 6 Nov |
| Presets: Multi-Image, Cinematic, three user slots, all values of one engine | Ed 6 Nov; R13, R16 |
| Play goes through the director and keeps the scenes. A canvas layout does not | R15 |
| One call, one move. A second view is the next storyboard scene | R13 |
| Drift of one move follows the crop | Q9 |
| Rotation of one move can be passed per scene | R11, R13 |
| Defaults matching today's look | R1, R8. Changes from today: Frame color Any (Q13); backdrop adds blur and solid at Rare (Q5) |

Anything in the build that cannot be put in this table is a question for Ed, not a decision.

---

## 6. Every number

Proposal: **S** = becomes or stays a setting; **Q** = question for Ed (section 9); **I** = internal search effort, no
effect on the look, stays fixed; **P** = decided and stated. Not a setting, and not an open question.

### Layout

| Number | Today | Where | Proposal |
|---|---|---|---|
| Images per scene | default 1 to 9, up to 16 (`MosaicLayout.MaxShown`), each count equally likely | setting | S. Flat inside the range, fixed |
| Same folder and date | 80% | setting | S |
| Design odds | Normal each | setting | S. Each call rolls the odds it was given (R16). The generator default, and the Multi-Image preset, are all six at Normal. Cinematic passes Edge to edge Often, Hero Rare, others Off (Q3). Those odds are not a generator setting |
| Most images, Scattered prints and Circles | 9 | `MaxScattered` | Q4 |
| Most images, Slanted panels | 6 | `MaxPanels` | Q4 |
| Most images, any layout | 16 | `MosaicLayout.MaxShown` | Q4 |
| Fill shows at least | 65% | `ComposeSolo` | S: Crop at most, Q1 |
| Single images odds | Fill 2, Matted 1, Whole 1 | setting | P: removed. One-image layouts use the design odds (Q12) |
| Pick from the best | 3 | setting | S |
| Face kept in frame | 85% | setting | S |
| Mostly hidden | 60% covered | `Judge` | Q10 |
| Versions tried per design | 48 | `Versions` | I |
| Mosaic trees per version | 120 | `TreesPerVersion` | I |
| Edge to edge no gap | 1 in 3 | `BuildEdgeToEdge` | S: Gaps |
| Gallery wall gap | 1.2% to 2.4% of the short side, x Frame width | `BuildGalleryWall` | Q6 |
| Gallery wall rounded corners | 1 in 3 | `BuildGalleryWall` | S |
| Gallery wall card | Any = white, black or tint, 1 in 3 each | setting | S |
| Hero size | 62% to 82% | setting | S |
| Hero shapes | arch, oval, squircle, hexagon | `BuildHeroSpotlight` | S, Q7 |
| Hero position | left third, centre, right third | `BuildHeroSpotlight` | S |
| Hero width at most | 70% of the canvas | `BuildHeroSpotlight` | Q6 |
| Hero outline | white, 0.7% of the short side | `BuildHeroSpotlight` | S: Frame color, Frame width |
| Print tilt | up to 6.5 degrees; each print 23% to 100% of it | setting + code | S: range |
| Print size | 1.05 to 1.3 of an even share | `BuildScatteredPrints` | Q6 |
| Print border | off-white, 1.2% of the short side | `BuildScatteredPrints` | S: Frame color, Frame width |
| Featured print or boldest circle | 1.5x | `FeaturedLarger` | S: Featured size |
| Circle size | 0.82 to 1.02 | `BuildCircles` | Q6 |
| Circle outline | white, 0.6% of the short side | `BuildCircles` | S: Frame color, Frame width |
| Slanted panels lean | 0.2 of a panel | `BuildSlantedPanels` | S |
| Slanted panels mirrored | 50% | `BuildSlantedPanels` | S |
| Backdrops (Edge to edge whole, Hero, Prints, Circles) | vignette or gradient, 50/50 | builders | S: Backdrop odds. Default Vignette Normal, Gradient Normal, Ambient blur Rare, Solid Rare (Q5). Each call rolls the odds it was given (R16) |
| Whole image print margin | 5.5% of the short side | `ComposeSolo` | P: goes with Hero spotlight for one image |
| Faces sit above the middle | 8% of the cell | `AimAtSubject` | Q10 |
| Screenshot: flat area | 15% or more | `ScreenFlatShare` | Q10 |
| Near-identical shots | 5 bits of 64 | `NearlyTheSame` | Q10 |

### Zoom

| Number | Today | Where | Proposal |
|---|---|---|---|
| Zoom range | 1.5x to 4x | setting | S |
| Zoom frequency | 70% | setting | S |
| Zoom at most, any setting | 5x | `MostOfAnyZoom` | I (slider end) |
| Faces take at most | 85% of the cell | `FacesKept` | Q8 |
| Detail limit | 2x the photo's pixels | `MostEnlarged` | Q8 |

### Camera

| Number | Today | Where | Proposal |
|---|---|---|---|
| Camera zoom | 1.15x to 1.8x, up to 2.5x | setting | S |
| Camera zoom ceiling | 2.5x | Camera Director's limit | Q9 |
| Motion | 40% to 60% | setting | S |
| Strength scale | 0.32 at full Motion; x0.6 to x1.25 by scene length against 12 s | `CameraMove` | Q9 |
| Widest view | 1.03x | `WidestZoom` | I |
| Drift zoom | 1.06x to about 1.3x, from Motion | `CameraMove` | Q9 |
| Drift sideways | 3 in 4 | `CameraMove` | P: follows the crop. Vertical when the crop is tall, horizontal when it is wide, either when both have room. Q9 |
| Two-shot minimum scene | 8 s | `ShortestTwoShotSeconds` | Q9. Inside the move only, as today. Not used by sequence beats: each scene's hold is from Duration |
| Two-shot wide shot | 1.06x plus | `PlanTwoShots` | Q9 |
| Two-shot split | 55/45 (snap), 50/50 (cut) | `PlanTwoShots` | Q9. Inside the move only. Sequence beats does not split a span |
| One-image zoom applied twice | the still is zoomed, then the camera zooms again | `AimAtSubject`, then `Plan` | P: one move, in the order in 3.5 |
| Cinematic directed scenes | about 65% of slides that have a face | `CinematicDirector.GeneratePrimarySequence` | P: not added to the engine. Two views are two storyboard scenes (R13) |
| Face-to-face move | about 3 in 5 of the window's filled face slides that have two faces | `CinematicDirector` | P: not added to the engine. Two faces in turn are two scenes (R13) |
| Other filled scenes in the window | one third each: cut, snap, one push-in | `CinematicDirector` | P: not copied. The engine keeps one move per call |
| Extra 5 s on the long push-in | the window adds 5 s to that one pattern | `CinematicDirector` | P: not copied. Duration wins (R5) |
| Tilt | up to about 0.5° on the window's fit and drift; the generator's Rotation defaults off | `AmbientBlurStrategy`, Rotation | S: Rotation stays the tilt of one move. Default off. A scene may pass another value |
| Still scenes | 5% to 35% by image count, in drafted shows only | `ChooseMotionKind` | S: odds, default 0% (Ed, 6 Nov) |
| Different move from the last | 8 tries | `TriesForADifferentMove` | I |
| Curved path at most | 13% of the path | `MostBow` | I (slider end) |
| Rotation at most | 6 degrees | `MostTurn` | I (slider end) |
| Floating cells' depth and float | depth 0.08 to 0.3; float `MostDrift` 0.012, `MostTurn` 1.2; period 11 to 21 s | `CameraMove` (`CellDrift`) | Q9 |
| Move runs past the change | 2.5 s | `CameraOverrunSeconds` | I. Not before a change of view inside one image: that end frame is where the next scene starts |
| Camera Director zoom | 1x to 2.5x; Aim at face fills 35% | `CameraDirector` | Q9 |

### Pace

| Number | Today | Where | Proposal |
|---|---|---|---|
| Duration | 10 to 15 s, 3 to 60 | setting | S |
| Scenes per step | none | -- | S: 2 to 2 default, up to 4 |
| Transition between views of one image | none | -- | P: Cut. Any other transition blends the picture into itself |
| Smart pace | long and short halves alternate; layout density weighs 35% | `NextPlaySeconds` | Q11 |
| Storyboards: one scene at the top of the range | always one | `ShowDirector` | P: goes. A forced outcome, not odds (R2, R16); every scene rolls in the range. Q11 |
| Onsets | not read | the soundtrack player | P: not in the engine. The director reads them later (3.10) |
| Single duration fallback | 5 s when none is set | `PlaySeconds` | I |

---

## 7. Saved settings

- **Single images odds** (`SoloFillOdds`, `SoloMattedOdds`, `SoloWholeOdds`, and Cinematic's): dropped. One-image
  layouts follow the design odds. There is no faithful mapping, because the old odds applied to one image only and design
  odds apply to every count; the release notes say so. Q12.
- **Cinematic storyboard**: its parameters leave the generator's settings and are kept with the storyboard. Its Single
  images odds give way to the design odds in 3.7. Each scene remains one call. Playing one uses the one camera. A scene
  that has its own Camera Director path keeps that path.
- **The show file (`.mivshow`)** is the part that is hard to undo, so its rules are fixed here:
  - The show's values (images per scene, design odds, duration, smart pace, motion, sequence beats, Scenes per step)
    go in the show file, in the
    existing `globalDefaults` block (`ShowGlobalDefaults`), which already holds the show's dwell, smart pace, motion
    and camera mode. One place for show values, not a second block beside it.
  - A value the show does not hold means "use the engine's setting". So a show made without the cinematic preset
    stays exactly what it is today: it follows the engine's settings.
  - `$schemaVersion` goes from 1 to 2. Opening a version 1 show with `GeneratorOverrides` = "Cinematic" copies the
    app's current cinematic values into that show once, when it is opened. Its scenes, holds and camera paths are not
    touched. It is written as version 2 only when the user saves.
  - An older app opening a version 2 show ignores the new fields and plays the saved scenes; new scenes it adds
    follow its own settings. From the code: `ShowProject.FromJson` sets no unmapped-member handling, so unknown
    properties are skipped, and nothing reads `SchemaVersion` to refuse a file. Still to be confirmed by opening a
    version 2 file in v0.31.2 during slice 3.
  - The app's settings keep reading their old Cinematic block, so an old settings file loads with no error. Its values
    become the defaults of the Cinematic preset that a new cinematic storyboard starts from. Generator Advanced no
    longer shows them.
  - Before slice 3 is called done: Ed's own saved shows and settings file, copied, opened in the new build, played,
    and compared with the same files in v0.31.2.
- **New settings** read as their defaults, which are today's look, apart from one-image layouts. Two defaults
  also change multi-image layouts: Frame color Any (Q13), and backdrop odds of vignette and gradient Normal, ambient
  blur and solid Rare (Q5). The release notes say so.
- **Shows already made** keep their layouts. A scene added to a storyboard is another call.

---

## 8. Checks before it is called done

Written from what Ed does, not from the description. Each is run and its numbers reported; then watched in the app.

1. A Cinematic storyboard portrait, regenerated 30 times with its defaults: how many full-screen, framed, and aimed at a
   zoom. The zoom amount is where the move arrives. The opening frame is the fit or the fill.
2. The same with the main settings, and with a landscape without faces.
3. Each design with 1 image, 2 images and its most: 30 each, nothing buried, no face cut where a clean version exists.
4. A design at Off never appears, for any count from 1 to 16.
5. Frame color Black reaches Gallery wall, Hero, prints and circles.
6. Crop at most 100%: no image is shown whole for cropping area (only face protection past Face kept in frame can show it whole). 0%: every image is shown whole.
7. Every new setting changes what its tooltip says it does, measured over 30 layouts at each end of its range.
8. An old settings file loads with no error, today's look kept apart from one-image layouts. The cinematic values that
   used to live in those settings are read onto the storyboard, and Generator Advanced no longer shows them.
9. Each check fails when the change it covers is taken out (control).
10. Watched on screen: a cinematic storyboard of portraits and landscapes, under Play. Then Play with the show using
    the main design odds, long enough to see every design. This check is not a canvas layout, and it is not direct
    Auto-Mix.
11. Two scenes of the same portrait stored on a storyboard: the first is the wide view, the second is called with the
    zoom aimed at the face. Each engine call returns one move. A canvas layout of that portrait returns one move and
    is not kept. Play with sequence beats at 0% writes one scene per step. At 100%, see check 18.
12. Images per scene set to 1 to 6. One engine call returns one layout. It does not spend a second phase of the hold
    on one photo of the six.
13. A Cinematic storyboard saved before this change opens, plays and adds a scene without error.
14. A portrait whose faces sit near an edge, with Crop at most and Face kept in frame pulling opposite ways (filling is
    inside Crop at most but cuts a face): 30 layouts, no face cut, and the result matches the safe crop rule in 3.4.
15. One image, Camera mode Auto, Zoom frequency at 100% and at 0%, 30 scenes each. At 100%, every move reaches the Zoom
    range. At 0%, none does: each is a Camera zoom or a drift, about half and half. Camera mode Drift: none reach the
    Zoom range.
16. Engine settings, with a cinematic storyboard open: the page has no Cinematic storyboard group. Images per scene,
    duration, motion and sequence beats are edited on the Show card and the storyboard. A canvas layout does not read
    them.
17. Play on Ed's largest folder: time to first picture, and over 30 minutes the number of scenes held past their
    duration because the next was not ready. Targets in the director section.
18. Play on a folder of mixed portraits and groups, with sequence beats at 100% and Scenes per step 2 to 4. Each step
    is kept scenes, each a closer view: a group, then one image, then a face; a portrait, then its faces. A step stops
    early when there is no closer view. Every hold is inside Duration. Views of one image change by a Cut, without the layout
    changing. Stepping back shows each scene. A canvas layout of one of those pictures is one
    call, one move, and is not kept.
19. The same settings, one image with a face, 30 moves each from the canvas, Play and a storyboard scene with no
    saved path: the same spread of zoom, drift and face aim from all three. Today they differ; this is the check for
    the one entry point.
20. No code outside the engine entry point calls `CameraMove.Plan` or `ChooseMotionKind`, and no
    planner reads a last move it was not passed (a search, not a run). The canvas keeps its own last move and passes it in.
21. Open the app on a folder (R17). The first settings the user reaches from the bar are the Show card: preset, the
    show's values, new show, soundtrack, and one Engine settings link. No Storyboard / Auto-Mix switch on the bar.
22. With no storyboard, choose Cinematic on the Show card and press Play: scenes are one image, full screen, made as it
    plays and kept; stepping back shows them. Choose Multi-Image instead: scenes follow the engine's settings.
23. With a storyboard of scenes open, press Play: that show plays; nothing direct, nothing replaced. New show from these
    images asks before replacing it.
24. Change duration or motion on the Show card while Play runs: the scenes drafted after that use it; the scene
    playing keeps its own hold.
25. Save as My preset 1, start a new show, choose My preset 1: the new show plays with the saved values.

---

## 9. Questions for Ed

| # | Question | Recommendation |
|---|---|---|
| Q1 | Crop at most default | 35%, today's limit |
| Q2 | *(Merged into Q5.)* | -- |
| Q3 | Cinematic storyboard design odds | Edge to edge Often, Hero spotlight Rare, others Off. A justified fixed value (R2): odds add up, so four designs at the rarest setting (0.1 each, `RarestDesign`) against Often (5) would put about one scene in thirteen on a print, circle, card or panel, which is not a film (R10). Off here is the preset's starting value; the user can turn any design back on. The Multi-Image preset and the generator default stay all six at Normal |
| Q4 | Most images per design: keep 9 / 6 / 16 as fixed limits (past them prints and panels get too small), or settings | Fixed, stated in the panel; they are the shape's limits, not taste |
| Q5 | Backdrop default odds, including what sits beside an image shown whole (a blurred copy, as Cinematic Slideshow's `BakeBlurredBackground`, or a tint). Today it is vignette or gradient, 50/50 | Vignette Normal, Gradient Normal, Ambient blur Rare, Solid Rare. Sound: vignette and gradient at Normal is today's 50/50 (R8), and the two new looks appear without taking over (R6). The cinematic preset uses the same default; it passes no backdrop odds (3.7). Ambient blur costs a blurred copy per image; slice 3's Play timing covers it |
| Q6 | Size variety, gaps and hero width: settings, or fixed as part of each design's look | Size variety as settings; gap size fixed (it is part of Gallery wall's look, and a range would barely show); hero width at most 70% fixed and stated, a shape limit like Q4: past it the rest of the mosaic is too narrow to read |
| Q7 | Add a plain rectangle to Hero shapes | Yes |
| Q8 | Zoom's limits (faces take at most 85% of the cell; 2x the photo's pixels): settings, or fixed and stated | Fixed and stated: past them a face is cut or the image blurs |
| Q9 | Camera internals (strength scale, drift zoom, two-shot timing, floating cells, Camera Director's 2.5x) | Drift zoom as a setting; two-shot minimum 8 s kept inside the move so both shots have time to read (avoids a flip-book; sequence beats does not use it); drift direction follows the photo and is fixed, not a setting; the rest fixed and stated |
| Q10 | Detection thresholds (mostly hidden 60%, screenshot 15% flat, near-identical, faces above the middle) | Fixed and stated: they define what is detected, not the look |
| Q11 | Smart pace's weighting and the storyboard's one scene at the top of the range | Keep smart pace's weighting (it is how smart pace works, and smart pace is a switch). Drop the forced scene at the top: it is an outcome fixed by the developer, not odds (R2, R16) |
| Q12 | Saved Single images odds: drop, or carry over | Drop |
| Q13 | Frame color default: Any (frames vary, R7, but Hero, prints and circles stop being always white) or White (today's look, R8) | Any. Sound: R7 asks for frames that vary by the user's choice, and Any is that choice's varied end; one setting for every frame is R4. The cost is that prints and circles are no longer always white; White is one click. The release notes say so |
| Q14 | *(Resolved in 3.4.)* A shape that is not a rectangle is measured against the shape. The bounding box would make Crop at most agree to a crop the shape then hides (R5) | -- |
| Q15 | *(Resolved in 3.5.)* On one image the rolls run in one order. A second roll after a Zoom hit would make Zoom frequency mean about half of what it says (R5) | -- |

A user who wants two images in a Cinematic storyboard sets Images per scene to 1 to 2. There is no separate portrait-pair
option (R4, R12).

---

## 10. Out of scope

- Cinematic Slideshow's window stays in the app and is not the engine being rebuilt (R14). This proposal does not
  rewrite it, and it does not copy the window's shot sequences into the engine.
- A layout file stays one canvas. A show stays an ordered list of scenes. This proposal does not add a shot list or a
  soundtrack reader to either file (3.10 reads songs at play time and saves nothing in them). The cinematic parameter values that today live in the generator's settings move to
  the storyboard; they do not remain a named group inside those settings. Camera Director and transitions are unchanged.
- No titles, chapters or timeline editor. No soundtrack reader in the engine or the slices; the director's, later, is 3.10. The engine holds for the duration it is given.
- Sequence beats are on the director (director section). Putting them in the engine, or in Generator Advanced,
  or resuming the cinematic slideshow to get them, is the wrong direction.

---

## 11. For a reviewer

Check this design, not its author's reasoning:

0. Start with "What the user meets" and R17. Anything the user meets first that is the engine's (a settings page, a mode,
   a switch) is a finding. A slice or section that leaves the engine in front is a finding, however correct inside.
1. Read section 1. R1 to R13 are met in sections 3 and 4. R14 is met in the Direction and section 10. R15 and R16 are
   met in the director section and section 4. A requirement with nowhere to land is a finding. Do not expect R14, R15
   or R16 to be restated as engine steps.
2. For each element in section 3, find its line in section 5. For each row in the director section, find its line in
   section 5 and read what that line claims. A fill pattern (Scenes per step, closer views, group then one image)
   cited as if R14 had stated it is a finding. Those rows are a proposal. Anything else with no requirement is a
   finding.
3. For each number in section 6, check the code still has that value and the proposal is S, Q, I or P; anything missing
   is a finding. A Q written as a chosen default in section 3 is a finding.
4. Look for any place a layout is made, or a move is chosen, other than through the one entry point (Core
   principle), 3.1 and 3.6. Any caller of `CameraMove.Plan` outside it, any move returning more than one shot, and any
   memory of the last call kept by the engine or a control are findings. The director choosing
   which calls to make, and which parameter values to pass, is not such a place. A count, an orientation, a mode, a
   design, or a caller with its own composing path is a breach of R4, R12 and R13.
5. Check that every check in section 8 is written from Ed's use, not from the code.
6. Read the core principle. One call that plays two views, or that reads the music, is a finding. A second view belongs
   in the next storyboard scene. A value the storyboard passes (3.7) is a parameter of that call, not a second engine.
7. A cinematic or other storyboard parameter placed inside Layout Generator Settings or Generator Advanced is a finding
   (R13). Section 3.7 is the storyboard's list. The engine's settings are how one call is made when the caller does
   not pass its own value.
8. Read the Direction and R14. A change that rebuilds or extends the cinematic slideshow as the engine, or that copies
   its shot sequences into the layout engine, is a finding. Sequence beats placed in the engine or in Generator Advanced are a
   finding. Today's two-shot inside the move is not. Sequence beats' absence from sections 3 to 6 is not a finding:
   they are in the director section.
9. Read R15, R17 and the names at the top. Play that calls the engine and keeps nothing is a finding. A canvas layout is
   one engine call and is not such a finding. Describing direct Auto-Mix as the future path is a finding. A director
   rule that changes what one engine call returns is a finding.
10. Read R16. A locked design and backdrop, or a first or last scene treated differently because of its index, is a
    finding. Narrow odds rolled on every call are not a lock.
