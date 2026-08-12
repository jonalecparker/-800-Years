# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. The world direction is settled:
> **real Britain, real castle sites, Europe-ready** (memory +
> `Docs/MarchesRealism.md`).

**Where we left off:** The 08-11/12 stretch rebuilt the Marches terrain
as a 4×4 TILE GRID behind the new `Ground` facade (seams verified
bit-perfect, `Docs/TerrainTiles.md`), made roads splat paint on the
tiles' 1024 alphamaps (ribbons deleted — the user's eye), and shipped
the rivers pass (`Docs/Rivers.md`): carved beds sized by river width,
riverbed mud paint, two-tier water — ribbons everywhere, HDRP
custom-mesh water within 260m, brook-scale waves scaled per river.
Three user screenshot rounds drove real fixes (cross-slope terrace
carve, near-grade water, distance-scaled far lift). The user approved
roads and the water look; **the rivers haven't been re-walked since the
last fix**.

## Top priority: judge the rivers, then territories

1. **Walk the Monnow and a hill stream** — bank feel changed (water now
   ~0.55m below the Monnow's banks), far visibility fixed, waves calmed.
   Known deferred: the HDRP↔ribbon seam visible from altitude (dials
   and candidates in `Docs/Rivers.md`).
2. **Castle territories** (the design successor the survey waits on):
   castle sites → territories → land types per territory → **bandits at
   the seams between lordships**. Replaces `MaxSurveyRadius` 1500m and
   the center-distance ownership rings in `LandParcels`.
3. **One manor = one contiguous town by the castle** (user-confirmed;
   scattered living plots are wrong) — probably lands with territories.

## Second lane: the economy's remaining halves

- Procedural **houses on living land**, population-driven — the visible
  face of `Estate.Households`.
- **Manor decision dials** (taxes/customs per the design doc).
- Build time / afford-gating / salvage losses — arrives as one package.

## Realism backlog (briefs: `MarchesRealism.md`, `Rivers.md`)

- Tier B remainder: grass/scrub detail meshes, landuse splat variation,
  **hedgerows along parcel boundaries**, height detail noise.
- Tier C: **Welsh 1m LIDAR at the castle sites** (per-tile resolution
  raise — the tile grid exists for exactly this; brings Grosmont's real
  moat into the heightmap).
- Rivers later: flow-direction currents, foam, the altitude LOD seam.
- Re-bake order on the Marches source Terrain: Generate → Split →
  **Carve Rivers** → Dress (Split is the un-carve; features local).

## Backlog / open questions (carried)

- **Any new stored fact must join `CastleSave`** — schema v6; v2+
  load, v1 refuses.
- **Structural support deliberately absent**; nothing may assume
  construction is instantaneous in the world.
- Hand-test debt from 08-09: right-click undo forms, wall chaining
  feel, stair rework. Folded/switchback stair agreed, not speced.
- Watch-item: terrain wall drags through slab-borne buildings.
- Graduate playtested briefs into CLAUDE.md (`Stairs.md`, `Doors.md`,
  `BuildingRebuild.md`, `MarchesRealism.md`, now `TerrainTiles.md` +
  `Rivers.md`).
- Curved rooms floor chorded — trace-STAMP fill deferred.
- Britain-scale when leaving 15km: tile streaming, floating origin,
  simulation LOD — additive on the tile grid, deliberately later.
- `BuildMenu` categories placeholder-ish; wall material lacks
  roughness/AO; walk speeds untuned; parapets wait on wall detailing.
- Design queue after territories: renown (#11), special rooms (#14),
  AI-prop bake-off, walking liveliness (#12), hard-mode death (#4),
  liege (#5), AI nobles (#6), vassal departure (#7).
- Editor gotchas: wall graph is play-mode only; `execute_code` is a
  method body (no class declarations — reflect on private types, and
  GetField needs `Public` for public fields of private classes); public
  fields are scene-serialized (the `stairWidth`/`roadLift` trap); a
  draped mesh fights the terrain's LOD and that error is SCREEN-space
  (grows with distance); read baked normals back before trusting a
  winding probe; `Terrain.activeTerrain` is FORBIDDEN outside
  Ground/TerrainPads; the MCP bridge drops during multi-minute bakes —
  reconnects; verify bakes by STATE, not by the (identical) log line.
