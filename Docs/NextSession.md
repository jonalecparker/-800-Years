# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. The world direction is settled:
> **real Britain, real castle sites, Europe-ready** (memory +
> `Docs/MarchesRealism.md`).

**Where we left off:** The 08-13/14 session shipped procedural village
houses (`VillageHouses`, stable lot plan, `[`/`]` population dial,
tellable inn), rebuilt village LAND as a gate-seeded flood region
(`Docs/VillageHouses.md` + the CLAUDE.md survey section — towns now
span their streets by construction, one Living polygon per lordship,
15-acre dial), made the survey effectively instant (disk cache +
timesliced drape), and **baked real NRW 2m LIDAR ground under all
three castles** (`Docs/CastleLidar.md`, `CastleLidar` component —
verified to centimetres, datum offsets ~−23.5m consistent across
sites). The patched ground has NOT been walked yet.

## Top priority: the heightmap resolution option (agreed at wrap)

1. **Discuss and decide the castle-tile resolution bump.** The tiles
   sample at ~7.3m, so the baked 2m data is downsampled — real spur
   and earthwork shapes, not 2m mesh density. Options: bump only the
   castle tiles (513 → 1025 or 2049; tiles are separate Terrains and
   MAY differ) — but mismatched-neighbor LOD stitching risks cracks
   along those tile edges; or bump all 16 tiles (uniform, but render
   cost at pixelError 2). Phase 2 notes in `Docs/CastleLidar.md`.
   Whatever changes must fold into the bake order (Generate → Split →
   **ApplyLidarPatches** → Carve → Dress) and `TerrainTiler.Split`
   must stay the un-everything.
2. **Walk the patched castle sites** — Grosmont's real spur, the
   moats, and the new flood-grown towns over the real ground; judge
   the village shapes at all three castles.
3. **Income rebalance** — ~11× hot at 200m cells, hotter since
   bandits stood down (whole home territory yields). Recalibrate
   `Estate` rates; the cell size (200m) feels settled now.

## Second lane: the economy's remaining halves

- **Manor decision dials** (taxes/customs per the design doc).
- Build time / afford-gating / salvage losses — one package.
- The land ladder past rung one: what makes clearing/buying land
  worth doing (yield? renown?).
- Population currently a debug dial — hook `Estate.Households` to
  something real (growth from prosperity?) when the economy deepens.

## Realism backlog (briefs: `MarchesRealism.md`, `Rivers.md`, `CastleLidar.md`)

- 1m LIDAR upgrade when the 2020-23 rasters reach the DataMapWales
  catalogue (the WFS query in the brief finds them).
- Tier B remainder: grass/scrub detail meshes, landuse splat
  variation, hedgerows along parcel boundaries, height detail noise.
- Rivers "good enough for now" (user, 08-12): flow currents, foam,
  the altitude LOD seam stay deferred.
- TimberWoods hits its 250k cap every survey — widen
  `timberTreeSpacing` or raise the cap (minor).
- `TerrainSources/` is gitignored (~1GB of ASC); the brief records
  the re-download query if a fresh clone needs it.

## Backlog / open questions (carried)

- **Any new stored fact must join `CastleSave`** — schema v7; v2+
  load, v1 refuses.
- **Structural support deliberately absent**; nothing may assume
  construction is instantaneous in the world.
- Deferred by design: river-weighted territory seams, bandit camps at
  the seams (waits for combat), edge OWNER field (if neighbor castles
  become real builds), Spiš ring-village annulus tracing (outer loop
  only — covers the castle; harmless in the fallback scene).
- Hand-test debt from 08-09: right-click undo forms, wall chaining
  feel, stair rework. Folded/switchback stair agreed, not speced.
- Watch-item: terrain wall drags through slab-borne buildings.
- Graduate playtested briefs into CLAUDE.md (`Stairs.md`, `Doors.md`,
  `BuildingRebuild.md`, `MarchesRealism.md`, `TerrainTiles.md`,
  `Rivers.md`, `Territories.md`, now `VillageHouses.md` +
  `CastleLidar.md`).
- Curved rooms floor chorded — trace-STAMP fill deferred.
- Britain-scale when leaving 15km: tile streaming, floating origin,
  simulation LOD — additive on the tile grid, deliberately later.
- `BuildMenu` categories placeholder-ish; wall material lacks
  roughness/AO; walk speeds untuned; parapets wait on wall detailing.
- Design queue: renown (#11), special rooms (#14), AI-prop bake-off,
  walking liveliness (#12), hard-mode death (#4), liege (#5), AI
  nobles (#6), vassal departure (#7).
- Editor gotchas: wall graph is play-mode only; STOP PLAY before
  editing scripts AND before edit-mode bakes (the LIDAR bake guard
  caught one); `execute_code` is a method body; public fields are
  scene-serialized (the `stairWidth` trap); draped meshes fight the
  terrain LOD (SCREEN-space error); `Terrain.activeTerrain` FORBIDDEN
  outside Ground/TerrainPads; the MCP bridge drops during multi-minute
  bakes — verify by STATE, never re-run; edit-mode code never sees
  `LandSurvey.Instance` (use the `CastleGrounds.Survey` fallback);
  Mono's ZipArchive refuses NRW zips — `Expand-Archive` instead;
  bump `LandParcels.CacheVersion` when the survey algorithm changes.
