# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. The world direction is settled:
> **real Britain, real castle sites, Europe-ready** (memory +
> `Docs/MarchesRealism.md`).

**Where we left off:** The 08-12/13 session made the countryside three
LORDSHIPS (`Docs/Territories.md`, four passes): `CastleSite` markers
(Grosmont = home, Skenfrith + White Castle = neighbors), nearest-site
territories, relative per-territory classification, ownership by
lordship, `LandLaw` (build only on your own Castle parcels), CastleSave
v7. Parcel boundaries snap to roads and rivers, the road network is
filtered to the three castle routes of 1220, timber parcels grow real
oaks. The castles round then shipped: terrain-fitted castle grounds
(`CastleGrounds`, 36 rays, dials on LandSurvey), roads trimmed at the
gate, villages settled ON the road before the gate, and graybox
Skenfrith + White Castle built from the real plans as untouchable
scenery. **None of the castles round has been hand-tested** — Unity
crashed overnight right after the final save (recovery verified
redundant and discarded at wrap).

## Top priority: judge the castles round

1. **Walk/fly Grosmont, Skenfrith and White Castle**: do the grounds
   shapes read right (60–140m, terrain-fitted)? Does the road end
   convincingly at the gate? Are the villages on the road where a
   village would be? Do the two graybox castles hold up at walking
   distance? Dials: `castleGroundsMin/Max/Relief` on Land Survey
   Settings; Resurvey re-runs in play mode (resets ownership).
2. **Income rebalance** — ~11× hot at 200m cells; recalibrate
   `Estate` rates once the cell size feels settled.
3. **Procedural houses on Living land** (the economy's visible face,
   population-driven): the survey now hands them parcels; they must
   keep out of Castle parcels by stored type.

## Second lane: the economy's remaining halves

- **Manor decision dials** (taxes/customs per the design doc).
- Build time / afford-gating / salvage losses — one package.
- The land ladder past rung one: clearing bandit land plot by plot
  works; what makes it worth doing (yield? renown?).

## Realism backlog (briefs: `MarchesRealism.md`, `Rivers.md`)

- Tier B remainder: grass/scrub detail meshes, landuse splat variation,
  **hedgerows along parcel boundaries** (parcels exist now), height
  detail noise.
- Tier C: **Welsh 1m LIDAR at the castle sites** (per-tile resolution
  raise — brings Grosmont's real moat into the heightmap).
- Rivers "good enough for now" (user, 08-12): flow currents, foam, the
  altitude LOD seam stay deferred.
- Re-bake order on the Marches source Terrain: Generate → Split →
  **Carve Rivers** → Dress (Split is the un-carve).

## Backlog / open questions (carried)

- **Any new stored fact must join `CastleSave`** — schema v7; v2+
  load, v1 refuses.
- **Structural support deliberately absent**; nothing may assume
  construction is instantaneous in the world.
- Deferred by design this round: river-weighted territory seams,
  bandit camps at the seams (waits for combat), edge OWNER field (only
  needed if neighbor castles become real wall-graph builds).
- Hand-test debt from 08-09: right-click undo forms, wall chaining
  feel, stair rework. Folded/switchback stair agreed, not speced.
- Watch-item: terrain wall drags through slab-borne buildings.
- Graduate playtested briefs into CLAUDE.md (`Stairs.md`, `Doors.md`,
  `BuildingRebuild.md`, `MarchesRealism.md`, `TerrainTiles.md`,
  `Rivers.md`, now `Territories.md`).
- Curved rooms floor chorded — trace-STAMP fill deferred.
- Britain-scale when leaving 15km: tile streaming, floating origin,
  simulation LOD — additive on the tile grid, deliberately later.
- `BuildMenu` categories placeholder-ish; wall material lacks
  roughness/AO; walk speeds untuned; parapets wait on wall detailing.
- Design queue after territories: renown (#11), special rooms (#14),
  AI-prop bake-off, walking liveliness (#12), hard-mode death (#4),
  liege (#5), AI nobles (#6), vassal departure (#7).
- Editor gotchas: wall graph is play-mode only; STOP PLAY before
  editing scripts (mid-play recompile husks runtime objects);
  `execute_code` is a method body (no class declarations; reflection
  GetField needs `Public` for public fields of private classes); public
  fields are scene-serialized (the `stairWidth` trap); draped meshes
  fight the terrain LOD and its error is SCREEN-space;
  `Terrain.activeTerrain` is FORBIDDEN outside Ground/TerrainPads; the
  MCP bridge drops during multi-minute bakes — verify by STATE or an
  Editor.log watcher, never re-run; edit-mode code never sees
  `LandSurvey.Instance` — use the `CastleGrounds.Survey` fallback
  pattern.
