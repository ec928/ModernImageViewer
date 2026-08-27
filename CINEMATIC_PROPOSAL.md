# Cinematic in ModernImageViewer: an excellent stills show

**Status:** proposal. A 2026-08-27 attempt was reverted (lied: Clear-to-Auto on Auto; no visible start/end). Rebuild must follow §4–§6: two modes, no lying.  
**Date:** 2026-08-27 (two-mode law)  
**Scope:** The cinematic slideshow **in this app**. VideoDirector stays the multi-track compositor and MP4 exporter. Cinematic does not become an NLE.

This document is product direction. Section 2 is what ships today. The destination is not “a better playlist on a mediocre player.” It is an **excellent cinematic show inside ModernImageViewer**.

---

## 1. Destination (the bar)

Excellence here means excellence **in this app**: a stills browser whose Play button puts on a show people would actually watch, and whose Edit show tools can fix that show in minutes. It does not mean beating a wedding-slideshow SaaS or replacing VideoDirector.

A user with a folder of stills should be able to:

1. Press Play and feel the result was *directed* — pacing, variety, faces, crossfades — without opening anything.
2. If the set is wrong, see it, hide, reorder, and hold a portrait, in the same window.
3. Put music under it.
4. Put it on the screen that matters (fullscreen / chosen display), chrome gone.
5. Open that show again tomorrow from ModernImageViewer.

If any of those is missing, cinematic in this app is still incomplete. A filmstrip on a samey 12-second drift is not excellent. An auto camera nobody can curate is not excellent either. Both halves are required.

**Non-goals for cinematic (on purpose):** six tracks, authored Start/Mid/End as the default, mixed video, audio *editing*, emailing an MP4. Those are VideoDirector, or later. “I ran a great show in this app” is the loop. “I exported a movie” is the other app.

---

## 2. What exists today

Fire-and-forget works as a *gesture* and fails as a *show*. Play copies a path list and starts. The auto camera is real (faces, saliency, named techniques, beats, crossfade). Management is not: anonymous ticks, frozen membership, overrides keyed by **file name**, director knobs aimed at “this filename while it happens to be on screen.” Default duration is a flat 12s; consecutive slides can feel like the same move. No music. No first-class show in the main window. Fullscreen/present is not a show feature. One image on screen except during crossfade.

VideoDirector is the opposite: empty start, strong timeline, weak stills transitions, export.

**Failure to avoid:** cinematic becomes a worse VideoDirector.  
**Failure to avoid equally:** we ship curation and call it done while Play still looks like a timer on a pan.

---

## 3. Jobs

| Job | Cinematic in MIV | VideoDirector |
|---|---|---|
| Play this folder; it should look directed | **Primary, must be excellent** | Not the product |
| Fix which stills, order, a few holds | Required, optional UI | Strong, empty-start |
| Music under stills | Required for excellence | Clips on a track (overkill here) |
| Present the show | Required (this window, chosen display) | Has present-on-display |
| Two stills as a moment | Later, after the list | Tracks / PiP now |
| Mixed video, marks, MP4 | No | Yes |

Cinematic *is* the show in this app. VideoDirector *composes and exports*. Keep the split. Do not keep using the split as an excuse for a mediocre player.

---

## 4. One sentence

**Play is automated. If the user chooses to look, show the real values. If they choose to edit, let them. If they choose to preview, show the actual shot. No lying, including by omission.**

Two modes, never mixed in one pane:

- **Edit** (must be good): this slide’s start/end zoom-pan, duration, later transition/audio. Auto fills the numbers; the user can see them, change them, and preview.
- **Arrange** (phase 1 may be basic): order, hide, add, jump to Edit. Automated until they open it.

Do not put the playlist inside the camera inspector. Do not show “Clear to Auto” on an Auto slide. Do not hide start/end. VideoDirector already proves start/end rectangles can be honest; cinematic Edit is not allowed to be weaker than that.

---

## 5. User rules (normative)

1. **Play with no editing must be a good show**, not a demo of the engine. Pacing and variety are product, not leftovers.
2. **The picture list is how you enter Edit show.** Camera knobs are the inspector for a selected thumbnail, never the door.
3. **Hide is hide.** The file and the main grid do not change. Hidden slides stay recoverable in that show.
4. **A new Play folder / selection / from-viewer starts clean.** Curated shows return through Open show (or one-shot crash restore of an untitled dirty session). Shuffle never rewrites the filmstrip.
5. **Pause is still Play.** Pause does not open Edit show. Pause-chrome is not Arrange.
6. **When pairs exist:** two visible members; un-pair restores both; next/prev steps by entry.

---

## 6. Play vs Edit show

Two views of one object.

**Play.** Folder, selection, or from the current image (full listing, start there; loop wraps unless off). Directed auto camera, shuffle, loop, space/arrows, receding HUD. Progress is a thin bar or ticks — not 400 working thumbnails. Pause holds the picture.

**Edit show.** A control that means the pictures. Filmstrip of the playing show: order, hide/restore, add from this folder, Auto vs Custom, current slide. Inspector: all slides vs this slide (always labelled), duration, intensity, technique, direction, beats, focus box on the paused canvas. Close the panel; Play continues the same show.

Multiple cinematic windows stay allowed. Play from the main app does not silently replace a dirty show.

**Selection seed:** marquee / shift-range = **grid order**. Ctrl-click = **click order**.

---

## 7. Session, save, Open from the app

| Action | Result |
|---|---|
| Play folder / selection / from viewer | New filled show from that launch |
| Edit show, close, no Save | Discard. Prompt only if they opened Edit show *and* changed membership, order, or intent. Shuffle/loop alone do not prompt. |
| Save show | Named show in this app |
| Open show | From cinematic **and from the main window**. Missing files reported; rest play. |
| Crash | One-shot untitled restore. Next deliberate Play is clean. |

The show must live in ModernImageViewer, not only inside an already-open cinematic window. A saved show on the main surface (recent shows, or Open next to Play) is part of excellence, not polish.

User-facing object: “this slideshow, in this app, again.” Not an MP4.

---

## 8. What “directed Play” has to mean

Without this, Edit show only curates a dull reel.

**Pacing.** Auto slides do **not** share one fixed length. Each Auto slide rolls a duration in **5–20 seconds**, and consecutive Auto slides **alternate** short and long (if the last roll was in the long half, the next is in the short half, and vice versa). That is the default “directed” pace: readable, not a lecture, not a flip-book.

The randomizer is only the default. **If the user chooses**, they can set a different duration instead: this slide (one number for that entry) or all slides (one number for every Auto slide; randomizer off). They never have to. Leaving both on Auto keeps 5–20 alternating. A per-slide choice always beats an all-slides choice.

**Variety.** Consecutive Auto slides must not repeat the same technique and direction. The director already has several strategies; Play has to *use* them as a sequence, not a coin flip that can land on drift five times.

**Subject.** Faces and saliency already exist. Play should look like it found something in the picture. The focus box is how a user insists when Auto misses.

**Transition.** Keep the stills crossfade. It is already the one thing VideoDirector cannot do. Cut is an option, not the default.

**Intensity.** Default motion should be felt, not nauseous. Intensity remains the global “how much” knob.

This pillar is design and tuning of the engine that already exists, plus a small sequencing policy. It is not a new NLE. It is also not optional if the word is excellent.

---

## 9. Music

A stills show without sound is a viewer in kiosk mode. For excellence in this app, cinematic needs a **single music bed**:

- Pick one audio file (or a small stack played in order).
- Loop or stop with the show.
- Volume on the Play chrome.
- Stored with the saved show.
- Optional: fade with the first/last slide. Not clip-level audio editing.

No mixer, no per-slide stems, no ducking UI in v1 of music. If they need that, VideoDirector.

---

## 10. Present (window vs fullscreen)

First launch of cinematic is a **normal window** (the cinematic window on the desk). After that, **remember the last choice**: windowed or fullscreen.

- Fullscreen: this cinematic window, chrome recedes; Esc returns to windowed (or closes fullscreen), same as a player.
- Windowed: initial default; transport/Edit show available.
- Chosen display can follow the last fullscreen monitor once Present is used; it is not required on first windowed Play.

Do not send them to VideoDirector to put photos on the TV.

---

## 11. Pillars and order

All pillars are in scope for “excellent cinematic in this app.” Order is dependency, not a permission to stop early.

| Order | Pillar | User sees |
|---|---|---|
| 1 | **Directed Play** | Folder Play is watchable: 5–20s alternating pace, variety, subject, crossfade |
| 2 | **Show object + Edit show** | Filmstrip, hide/reorder/add, inspector on path+index, Auto vs Custom, Save/Open, Open from main window |
| 3 | **Music** | One bed under the show, persists |
| 4 | **Present** | Fullscreen / chosen display |
| 5 | **Pairs** | Optional two-still moments, membership rules in §5.6 |

**Do not ship 2 without 1.** A filmstrip on a boring reel is the mediocre outcome.  
**Do not ship 1 and call it done** if they still cannot drop three photos.  
**3 and 4** are what make it a *show* rather than a preview.  
**5** after the list exists; collage remains the still page.

Export to MP4 stays VideoDirector until/unless a later capture path is worth it. It is not required to call cinematic in this app excellent.

---

## 12. UI anatomy

| Surface | When | Job |
|---|---|---|
| Play chrome | Play | Transport, shuffle, loop, volume, present, **Edit show** |
| Filmstrip | Edit show | Membership, order, hide, **add** (picker, Explorer drop, from the main viewer), marks, current |
| Inspector | Edit show | All slides vs this slide; existing director knobs; focus on canvas |

Huge shows: Play of 800 is valid. Edit show states the count and offers search / Custom / Hidden. Curation is *aimed at* a selection or a trimmed show.

Hide copy: “Hide in this show,” never Delete.

**Add** is not one dialog. The user can add stills from a file picker, from Explorer (drag-drop or Open), and from the main image viewer (current image or selection) while cinematic is open. Same show, several doors.

---

## 13. Data model (product)

1. **Show** = ordered entries + global defaults + music + playback flags + present preference.
2. **Entry** = path(s), layout (single until pairs), Auto or Custom, knobs, focus rect. Identity = **full path + index**.
3. **Auto is complete** and must meet §8. No unconfigured slide.
4. Saved show persists list, intent, music reference, not bitmaps.
5. Filename ledger is retired as source of truth.

---

## 14. Later pairs (rules now, UI later)

- Join two filmstrip items; both remain visible in the cell.
- Un-pair restores both at that index.
- Transport steps by entry.
- Crossfade between entries stays default.
- Not six tracks. Not collage.

---

## 15. What not to do

- Do not require Edit show before Play.
- Do not start Edit show empty.
- Do not open Edit show on pause.
- Do not ship the filmstrip and skip directed Play.
- Do not key customizations by file name.
- Do not let shuffle redraw the strip.
- Do not silently reshape a show when the folder watcher fires.
- Do not prompt Save on a Play-only session.
- Do not put a timeline of tracks in this window.
- Do not make Start/Mid/End the default author.
- Do not replace crossfade with fade-to-black as the only cut.
- Do not leave the filmstrip on screen for Play-only users.
- Do not show Send to VideoDirector until it works.
- Do not call the job done when people can order photos but would not watch the Play button.
- Do not keep a single global 12s as the Auto pace.

---

## 16. Acceptance

**Play (directed)**

- A typical folder Play is watchable: Auto durations land in 5–20s and alternate short/long, not a fixed 12s tax and not a run of identical lengths.
- Adjacent Auto slides are not the same move twice.
- A face-heavy photo spends time on a person, not a random corner, often enough to trust Auto.
- Crossfade is the default between stills.

**Edit show**

- Bing-folder Play: no filmstrip, no leftover hides, no save dialog.
- Twelve trip photos: select → Play → Edit show → hide duds → reorder → run. Files stay in the grid.
- Grandma’s portrait: thumbnail, longer duration, focus box. Technique names optional.
- Unsaved Play-again: fresh show.
- Shuffle: strip stays put.

**Show**

- Music plays under Play, volume on chrome, saved with the show.
- Present fills the chosen display; Esc returns.
- Open show from the main window replays it.

If Play is still dull after the filmstrip ships, the proposal failed.

---

## 17. Delivery order

1. Directed Play (5–20s alternating Auto duration, no-repeat technique/direction, keep crossfade and subject finding).
2. Show object at launch (path + index); Play consumes it invisibly.
3. Edit show: filmstrip, hide/reorder/add, count/search, inspector, Save/Open, Open from main.
4. Music bed.
5. Present (fullscreen + display pick).
6. Pairs, with §14.

---

## 18. Key decisions

| Decision | Choice | Why |
|---|---|---|
| Bar | Excellent *in this app* | Watchable Play + fixable show + music + present |
| Default | Directed Play on a filled show | MIV’s gesture |
| Management | Optional Edit show | Inverse of VD empty-start |
| Auto duration | Per-slide roll 5–20s, alternate short/long | Directed pace without a single global tax |
| Present | Windowed first; then last windowed/fullscreen | Desk is default; remember how they ran it |
| Add stills | Picker, Explorer, main viewer | Several doors, one show |
| Engine | Tune and sequence Auto; do not replace it | The show must look directed without a timeline |
| Music | One bed | Slideshow, not a DAW |
| Present | This window | Running the show is in-app excellence |
| Export | VideoDirector | Do not pretend cinematic is an NLE |
| Entrance | Pictures, not camera jargon | Curators think in photos |
| New Play | Clean snapshot | Play means this folder, now |
| v1 trap | Do not stop at the filmstrip | That is the mediocre outcome |

---

## 19. Locked vs still open

**Locked**

- Auto duration: 5–20 seconds per slide, alternating short/long. Optional: the user may set a different duration (this slide and/or all slides). They do not have to; random stays the default until they choose.
- Present: cinematic **window** on first use; after that, last setting (window or fullscreen).
- Add: file picker, Explorer (drop / open), and from the main viewer. Not a single exclusive UI.
- Edit show control: a labelled **Edit show** on Play chrome. Pause does not open it.
- Crash restore: keep an untitled dirty session until the next deliberate Play or until the process is quit and the user declines restore — then gone. (Default so this does not block.)

**What “Open from the main window” meant** (not windowed vs fullscreen). Tomorrow, from the *grid* in ModernImageViewer, how do you start a show you saved? Proposed default: **Open show** next to Play, file picker, plus a short recents list if any saves exist. Say if you want only a picker, or only recents.

Naming is decided: UI **Edit show**.

---

## Related

- Implementation today: `Cinematic/`
- Cache, decode, unpackaged paths: `ARCHITECTURE.md`
- Bug tracker: `REVIEW.md`
- Companion NLE: VideoDirector `ARCHITECTURE.md`
