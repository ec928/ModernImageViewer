# Layout Generator: one mechanism (design proposal)

**Status: proposal, not built, not approved.** Nothing here is in the app until Ed has said yes to it. The app as it stands
(v0.31.2) is described in `MULTI_IMAGE_UI.md` and the README's "Layout Generator" section; where this file says
"today", it means v0.31.2.

**How to use this file.** Section 1 is Ed's requirements, with where each came from. Every later decision cites the
requirement it serves (R1, R2 ...). Section 6 lists every number the generator uses, so none is decided silently. Section
9 is the questions only Ed can answer. A reviewer should start at section 11.

---

## 1. Requirements

From Ed's own words. Dates are 2026. Where a requirement is a reading of his reaction rather than his words, it says so.

| # | Requirement | Source |
|---|---|---|
| R1 | **Easy to use, yet powerful if needed.** Sensible defaults up front; finer control folded away. | 4 Nov: "we want it to be easy to use, yet powerful if needed". 1 Nov: "keep this easy for the user, not show too much complexity, while still giving them fairly powerful options". |
| R2 | **Options shape the odds; nothing is disabled.** Ranges, thresholds and probability curves are the options. A choice can be made rare, never impossible; the Off end of an odds scale is the user's own choice. | 31 Oct: "why the heck would I disable any? those aren't options"; "changing ranges, thresholds, and probability curves to favour certain things are legit value added options". |
| R3 | **No single rule for single images.** A user-defined random distribution: sometimes cropped, sometimes not, "in line with the design pattern for the rest". | 4 Nov. |
| R4 | **Consistency.** One mechanism, one pattern, one term and one kind of control for one kind of setting, everywhere. No ad hoc exceptions. | Repeated: "why do you keep using different terms???"; "follow the standard UI design pattern for motion"; 5 Nov, on designs applying only to some layouts and single images and portraits each having their own mechanism: "what a fucking stupid idiotic inconsistent design". |
| R5 | **No arbitrary hard-coding.** No rule the developer decided that overrides or bypasses the user's settings. | 5 Nov: "tell me where else you idiotically imposed hardcoding based on some arbitrary rule you decided on". |
| R6 | **Random variety.** Regenerating gives a different result whenever the settings allow more than one. | 5 Nov: "regenerate results in the same thing ... rather than random"; "hard coded to show the same thing over and over again". |
| R7 | **Frames follow the user's choice and vary.** | 5 Nov: "frames are random depending on user choice". |
| R8 | **Sensible defaults.** | 5 Nov: "pick reasonable defaults"; zoom defaults 4x, 70%, 85% set by Ed. |
| R9 | **Name things by what they do; a design's settings sit under that design.** | 5 Nov: "move design items into the design group"; earlier naming decisions (Layout Generator, Image Pool, Auto-Mix). |
| R10 | **Cinematic folds into the one generator without losing what it does.** Cinematic storyboard is overrides on the same engine. A portrait shows as true Cinematic does: the whole image, no empty space above and below. | 4 Nov (end goal); 5 Nov: "true cinematic shows the whole image without empty space top and bottom for portrait". |
| R11 | **Zoom you control, with or without faces**, for the layout and for the camera (Ken Burns). | 1 Nov (Ken Burns for Surprise me, "how much zoom" proposed); 5 Nov: "forget faces. where are the general zoom settings that control the camera???" |
| R12 | **Every layout goes through the designs**, one image included. *(Reading of Ed's 5 Nov reaction to designs applying only to layouts of two or more.)* | 5 Nov. |

---

## 2. Today (v0.31.2): three mechanisms

1. **Two or more images:** six designs, chosen by design odds (`LayoutComposer.Compose`).
2. **One image:** no design. Its own odds, Fill / Matted / Whole image (`ComposeSolo`, `SurpriseOptions.PickSolo`). Matted
   is a one-image Gallery wall built a second way.
3. **One image that filling would crop to under 65% of it, or through a face (every portrait on a landscape screen):** a
   third branch inside (2). Fill becomes "Fitted", whole at full height.

Breaches of the requirements today:

| Today | Breaks |
|---|---|
| Designs skip one-image layouts; single images have their own odds and look | R4, R12 |
| Portraits have a rule no design cell has (65% shown) | R4, R5 |
| Frame color and Frame width reach Gallery wall, Matted and Whole image only; Hero, Scattered prints and Circles are always white | R4, R7 |
| Odds fixed in code with no setting (section 6) | R2, R5 |
| Caps on images per design (9, 6) are fixed | R5 |
| Cinematic storyboard's defaults (Fill only) leave a portrait one shape every time; only zoom varies it | R3, R6 |

---

## 3. Proposed design

### 3.1 One pipeline for every layout (R4, R12)

| Step | Decided by | Requirement |
|---|---|---|
| 1. How many images | **Images per scene**: any count in the range, each equally likely | R2, R4 |
| 2. Which design | **Design odds** among designs that can take that many. A design at Off is never used. If none left on can take that many, the layout takes fewer. | R2, R12 |
| 3. Arrangement | The design and its own settings (3.3) | R9 |
| 4. Each cell's crop | **Crop at most** (3.4) | R3, R4, R10 |
| 5. Each cell's zoom | **Zoom** range and **Zoom frequency** (3.5) | R11 |
| 6. Which version | Versions that cut a face (**Face kept in frame**) or mostly hide an image are set aside; the pick is from the best **Pick from the best** | R1 |
| 7. Camera while playing | Camera settings (3.6) | R11 |

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
design's own settings (R3, R6). The **Single images** section is removed.

### 3.3 Settings that belong to designs (R2, R7, R9)

Shared by every design that has the thing, shown once above the designs:

| Setting | Applies to | Default |
|---|---|---|
| **Frame color**: Any / White / Black / From the images (Any picks per layout) | Every frame: Gallery wall card, Hero outline, print borders, circle outlines | Any |
| **Frame width** | The same frames | 1x |
| **Backdrop**: odds of solid, vignette, gradient | Every design with a backdrop | Q5 |

Under each design, folded away:

| Setting | Design | Default (today's look) |
|---|---|---|
| **Gaps**: odds of none or hairline | Edge to edge | none 1 time in 3 |
| **Rounded corners**: odds | Gallery wall | 1 time in 3 |
| **Gap size** (today's random 1.2% to 2.4% of the short side) | Gallery wall | as today; Q6 |
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
- A cell that would be cropped more shows its image whole inside the cell, on the cell's backdrop (Q2: tint or blur).
- So one portrait on a landscape screen shows whole at full height (R10), and so does a portrait in a wide mosaic cell:
  one rule, not a portrait rule.
- At 100%, nothing is ever shown whole for this reason. At 0%, every image is shown whole.
- Mosaic designs already prefer arrangements whose cells match the images' shapes, so in a mosaic this rarely triggers.

### 3.5 Zoom (R11)

As built in v0.31.2, unchanged in kind:

- **Zoom**: a range, default 1.5x to 4x (Ed's 4x). **Zoom frequency**: default 70% (Ed's). Every image rolls; one that
  zooms picks an amount in the range, toward its faces or, with none found, its focus point.
- It stops short of cutting a face, and of showing the photo past twice its own pixels. Both are numbers the user cannot
  change today (section 6): Q8.
- It applies to every cell in every design, the whole-image view of 3.4 included.

### 3.6 Camera (R11)

As built in v0.31.2:

| Setting | Default |
|---|---|
| Camera mode: Auto / Zoom in / Zoom out / Drift | Auto |
| **Camera zoom**: range, how far each zoom goes | 1.15x to 1.8x |
| Motion: range, how far a drift goes (0 is still) | 40% to 60% |
| Zooms rather than drifts | 50% |
| Aims at faces | 70% |
| Two-shot scenes | 25% |
| Movement curves, Curved path, Rotation | as today |

Fixed today and listed in section 6: drift direction (sideways 3 in 4), the 8-second minimum for two-shots, the 2.5x
ceiling, the wide shot's zoom in a two-shot. Q9.

### 3.7 Cinematic storyboard (R10)

Its section holds overrides only. Design odds replace its Single images odds.

| Setting | Cinematic default |
|---|---|
| Images per scene | 1 to 1 |
| Design odds | Edge to edge Often, Hero spotlight Rare, others Off (Q3) |
| Duration | 10 to 15 s |
| Smart pace | On |
| Motion | 40% to 60% |

With these, most scenes show the image full screen, a portrait whole at full height, each zoomed by a different amount 7
times in 10; an occasional scene is framed. Everything else (zoom, camera, frames) comes from the main settings.

Cinematic Slideshow's features not yet in the generator (R10), for later:

- Two-shot scenes: 25% against about 65% of face slides; two patterns against five; no pan from one face to another.
- A slight tilt by default (here Rotation starts off).

### 3.8 Unchanged

Face detection, Face kept in frame (85%), Pick from the best (3), Same folder and date (80%), Skip screens and repeats,
Images per scene (1 to 9), Duration (10 to 15 s), Smart pace, transitions, movement curves, Camera Director.

---

## 4. What the user sees change

| Today | Proposed |
|---|---|
| Advanced > Designs > Single images: Fill the screen, Matted, Whole image | Removed. One-image layouts use the design odds. |
| Frame color, Frame width under Gallery wall | Above the designs, for every framed design |
| Hero spotlight: Hero size | Hero size, Hero shape, Hero position |
| Scattered prints: Print tilt | Print tilt (a range), Print size variety |
| Slanted panels, Edge to edge, Circles: no settings | Lean, Mirrored; Gaps; Circle size variety |
| Nothing for backdrops | Backdrop odds |
| Nothing for crop | Images & faces: Crop at most |
| Cinematic storyboard: Fill / Matted / Whole odds | Cinematic storyboard: design odds |

Folded under each design and off the first page, so the first page and the defaults stay as simple as today (R1).

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
| Cinematic storyboard as design odds | R6, R10 |
| Defaults matching today's look | R1, R8 |

Anything in the build that cannot be put in this table is a question for Ed, not a decision.

---

## 6. Every number

Proposal: **S** = becomes or stays a setting; **Q** = question for Ed (section 9); **I** = internal search effort, no
effect on the look, stays fixed.

### Layout

| Number | Today | Where | Proposal |
|---|---|---|---|
| Images per scene | 1 to 9 | setting | S |
| Same folder and date | 80% | setting | S |
| Design odds | Normal each | setting | S |
| Most images, Scattered prints and Circles | 9 | `MaxScattered` | Q4 |
| Most images, Slanted panels | 6 | `MaxPanels` | Q4 |
| Most images, any layout | 16 | `MosaicLayout.MaxShown` | Q4 |
| Fill shows at least | 65% | `ComposeSolo` | S: Crop at most, Q1 |
| Single images odds | Fill 2, Matted 1, Whole 1 | setting | removed |
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
| Backdrops, Scattered prints and Circles | vignette or gradient, 50/50 | builders | S: Backdrop, Q5 |
| Whole image print margin | 5.5% of the short side | `ComposeSolo` | goes with Hero spotlight for one image |
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
| Drift sideways | 3 in 4 | `CameraMove` | S |
| Two-shot minimum scene | 8 s | `ShortestTwoShotSeconds` | Q9 |
| Two-shot wide shot | 1.06x plus | `PlanTwoShots` | Q9 |
| Two-shot split | 55/45 (snap), 50/50 (cut) | `PlanTwoShots` | Q9 |
| Different move from the last | 8 tries | `TriesForADifferentMove` | I |
| Curved path at most | 13% of the path | `MostBow` | I (slider end) |
| Rotation at most | 6 degrees | `MostTurn` | I (slider end) |
| Floating cells' depth and float | depth 0.08 to 0.3; float `MostDrift` 0.012, `MostTurn` 1.2; period 11 to 21 s | `CameraMove` (`CellDrift`) | Q9 |
| Move runs past the change | 2.5 s | `CameraOverrunSeconds` | I |
| Camera Director zoom | 1x to 2.5x; Aim at face fills 35% | `CameraDirector` | Q9 |

### Pace

| Number | Today | Where | Proposal |
|---|---|---|---|
| Duration | 10 to 15 s, 3 to 60 | setting | S |
| Smart pace | long and short halves alternate; layout density weighs 35% | `NextPlaySeconds` | Q11 |
| Storyboards: one scene at the top of the range | always one | `ShowDirector` | Q11 |
| Single duration fallback | 5 s when none is set | `PlaySeconds` | I |

---

## 7. Saved settings

- **Single images odds** (`SoloFillOdds`, `SoloMattedOdds`, `SoloWholeOdds`, and Cinematic's): dropped. One-image
  layouts follow the design odds. There is no faithful mapping, because the old odds applied to one image only and design
  odds apply to every count; the release notes say so. Q12.
- **Cinematic storyboard**: its Single images odds give way to its new design odds, at their defaults.
- **New settings** read as their defaults, which are today's look, so nothing a user sees changes until they change a
  setting, apart from one-image layouts.
- **Shows already made** keep their scenes as made; Add new layout and Replace show use the new rules.

---

## 8. Checks before it is called done

Written from what Ed does, not from the description. Each is run and its numbers reported; then watched in the app.

1. A Cinematic storyboard portrait, regenerated 30 times with its defaults: how many full-screen, framed, zoomed, and the
   zoom amounts.
2. The same with the main settings, and with a landscape without faces.
3. Each design with 1 image, 2 images and its most: 30 each, nothing buried, no face cut where a clean version exists.
4. A design at Off never appears, for any count from 1 to 16.
5. Frame color Black reaches Gallery wall, Hero, prints and circles.
6. Crop at most 100%: no image is shown whole for cropping. 0%: every image is shown whole.
7. Every new setting changes what its tooltip says it does, measured over 30 layouts at each end of its range.
8. An old settings file loads with no error, today's look kept apart from one-image layouts.
9. Each check fails when the change it covers is taken out (control).
10. Watched on screen: a played Cinematic storyboard of portraits and landscapes; Auto-Mix through every design.

---

## 9. Questions for Ed

| # | Question | Recommendation |
|---|---|---|
| Q1 | Crop at most default | 35%, today's limit |
| Q2 | Beside an image shown whole: a tint of the image, or a blurred copy as Cinematic Slideshow | Ed has said the two look alike; tint is built |
| Q3 | Cinematic storyboard design odds | Edge to edge Often, Hero spotlight Rare, others Off |
| Q4 | Most images per design: keep 9 / 6 / 16 as fixed limits (past them prints and panels get too small), or settings | Fixed, stated in the panel; they are the shape's limits, not taste |
| Q5 | Backdrop default odds | as today for each design |
| Q6 | Size variety, gaps and hero width: settings, or fixed as part of each design's look | Size variety as settings; gaps and hero width fixed |
| Q7 | Add a plain rectangle to Hero shapes | Yes |
| Q8 | Zoom's limits (faces take at most 85% of the cell; 2x the photo's pixels): settings, or fixed and stated | Fixed and stated: past them a face is cut or the image blurs |
| Q9 | Camera internals (strength scale, drift zoom, two-shot timing, floating cells, Camera Director's 2.5x) | Two-shot minimum and drift zoom as settings; the rest fixed and stated |
| Q10 | Detection thresholds (mostly hidden 60%, screenshot 15% flat, near-identical, faces above the middle) | Fixed and stated: they define what is detected, not the look |
| Q11 | Smart pace's weighting and the storyboard's one scene at the top of the range | Keep, and state them |
| Q12 | Saved Single images odds: drop, or carry over | Drop |

---

## 10. Out of scope

- Cinematic Slideshow's own window and Edit show stay as they are.
- Two-shot patterns and face-to-face pans (3.7) come after this.
- No change to how layouts are saved, to Camera Director, or to transitions.

---

## 11. For a reviewer

Check this design, not its author's reasoning:

1. Read section 1. For each requirement, find where sections 3 and 4 meet it; note any that are not met.
2. For each element in section 3, find its line in section 5; anything with no requirement is a finding.
3. For each number in section 6, check the code still has that value and the proposal is S, Q or I; anything missing is
   a finding.
4. Look for any place a layout is made other than through 3.1: a count, an orientation, a mode or a design with its own
   path is a breach of R4 and R12.
5. Check that every check in section 8 is written from Ed's use, not from the code.
