# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. The world direction is settled:
> **real Britain, real castle sites, Europe-ready** (memory +
> `Docs/MarchesRealism.md`).

**Where we left off:** The 08-14/15 session rebuilt the world onto the
**British National Grid** (`Docs/WorldFrame.md`, `WorldFrame.cs`,
`MarchesBaker.cs` — THE TILE IS THE ARTIFACT: 64 2km tiles baked from
the 2m LIDAR + OS Terrain 50, 9 castle tiles at 1025, 23 river tiles
at 513; `TerrainTiler`/`CastleLidar` deleted; CastleSave v8 migrates
old saves), fixed the castle-route graph (junction-preserving feature
bake + main-component snap), and dressed the new ground to explain
itself: hedgebanks on the LIDAR's field boundaries, grass mottle,
thalweg-snapped river carves, roads repainted. Foundations now draw
via the Circle/Rectangle checkboxes and the outline chain got angle
stepping. The user began building at Grosmont (`Saves/Grosmont.json`).

## Top priority: the first castle at Grosmont (agreed at wrap)

1. **Build the first castle on the Grosmont site** — the user builds,
   we fix what the building surfaces. Expect build improvements to
   fall out of every hour of this. Untested and likely to come up:
   foundation rect/circle shapes, chain angle stepping, stairs on the
   real spur, the cutaway against terraced foundations.
2. **The rim-snap watch**: wall-to-foundation edge snapping failed on
   the user's FIRST play of a session (before walk mode; fine ever
   after) and resisted every live probe. If it recurs: note whether
   it's the first play after opening the editor, fresh vs loaded
   foundation, and whether tapping Alt clears it — then probe live
   via the bridge in the broken state.
3. **Income rebalance** — ~11× hot at 200m cells, hotter since
   bandits stood down. Recalibrate `Estate` rates.

## Second lane: the economy's remaining halves

- **Manor decision dials** (taxes/customs per the design doc).
- Build time / afford-gating / salvage losses — one package.
- The land ladder past rung one: what makes clearing/buying land
  worth doing (yield? renown?).
- Population currently a debug dial — hook `Estate.Households` to
  something real when the economy deepens.

## Realism backlog (briefs: `WorldFrame.md`, `MarchesRealism.md`, `Rivers.md`)

- Far-NE tile [6,7]: 2,836 samples with no source (flat 60m stand-in)
  — eyeball it; the LIDAR gap is real, T50 covered most of it.
- Mixed-resolution seams: Unity's auto-connect can't stitch LOD
  across 257/513/1025 (console spam is that fact; geometry is
  crack-free by pinning). The LOD-seam pass is deferred, opened by
  the resolution ladder.
- 1m LIDAR upgrade when the 2020-23 rasters land = re-bake castle
  tiles at 2049 — the ladder already carries it.
- Tier B remainder: grass/scrub detail meshes, landuse splat
  variation, height detail noise. Hedgebanks shipped 08-15; dials
  (`hedgeBankSignal`, `splatMottle`, `maxTreeInstances` 450k) are
  tune-by-eye.
- TimberWoods still hits its 250k cap every survey — widen
  `timberTreeSpacing` (dial, user's call).
- Rivers "good enough for now" (user, 08-12): flow, foam, the
  altitude LOD seam stay deferred. Thalweg snap shipped 08-15 but
  is unwalked.
- `TerrainSources/` is gitignored (~1.2GB: NRW ASC + OS Terrain 50);
  `WorldFrame.md` records both re-download queries.

## Backlog / open questions (carried)

- **Any new stored fact must join `CastleSave`** — schema v8; v2+
  load, v1 refuses.
- **Structural support deliberately absent**; nothing may assume
  construction is instantaneous in the world.
- Deferred by design: river-weighted territory seams, bandit camps
  (waits for combat), edge OWNER field, Spiš annulus tracing.
- Hand-test debt: right-click undo forms, wall chaining feel, stair
  rework (folded/switchback agreed, not speced), foundation shapes,
  chain angle stepping.
- Watch-item: terrain wall drags through slab-borne buildings.
- Graduate playtested briefs into CLAUDE.md (`Stairs.md`, `Doors.md`,
  `BuildingRebuild.md`, `MarchesRealism.md`, `Rivers.md`,
  `Territories.md`, `VillageHouses.md`, now `WorldFrame.md`).
- Curved rooms floor chorded — trace-STAMP fill deferred.
- Britain-scale: Phase B streaming (detail ring + coarse far field)
  and Phase C (origin rebasing, regional saves) are designed in
  `WorldFrame.md`, deliberately later.
- `BuildMenu` categories placeholder-ish; wall material lacks
  roughness/AO; walk speeds untuned; parapets wait on wall detailing.
- The dressing/baker component sits on a GameObject named
  "GameObject" — rename it.
- Design queue: renown (#11), special rooms (#14), AI-prop bake-off,
  walking liveliness (#12), hard-mode death (#4), liege (#5), AI
  nobles (#6), vassal departure (#7).
- Editor gotchas: wall graph is play-mode only; STOP PLAY before
  edit-mode bakes; `execute_code` is a method body; public fields are
  scene-serialized (the `stairWidth` trap); draped meshes fight the
  terrain LOD; `Terrain.activeTerrain` FORBIDDEN outside
  Ground/TerrainPads; fire long bakes via
  `EditorApplication.delayCall` and verify by console STATE (the MCP
  relay times out ~30s and a STALE SESSION eats calls silently —
  re-`initialize` when execute_code returns success:false/null);
  `read_console` takes `filter_text` (the auto-connect spam floods
  unfiltered reads); edit-mode code never sees `LandSurvey.Instance`;
  bump `LandParcels.CacheVersion` when the survey algorithm changes.
