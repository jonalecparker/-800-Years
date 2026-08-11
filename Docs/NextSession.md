# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. Steps 1–3 done; step 4 (economy)
> now has BOTH sides shipped in first-slice form: costs (08-09) and
> income (08-10). The world direction is settled: **real Britain,
> real castle sites, Europe-ready** (see memory + `Docs/MarchesRealism.md`).

**Where we left off:** The 08-10/11 stretch shipped the income slice
(irregular-parcel survey, Survey tool, GameClock with quarter-day
revenue — ~£18/yr knight-band start — CastleSave v6), settled the
open-world direction, and built `Assets/Marches.unity`: 15km of real
Monmouthshire with Grosmont and Skenfrith 6.6km apart, then dressed it
from real OSM data — 1,872 dirt-lane ribbons, the Monnow and its
streams, 249k trees/bushes, fog/clouds/afternoon sun. The user flew the
distance (feels right) and caught the 50m tree-cull bug (fixed: three
dials, not one). **The walk itself hasn't been taken yet.**

## Top priority: take the walk, then the verdicts it produces

1. **Walk (or ride) Grosmont → Skenfrith in walk mode** — the entire
   point of the scene. Judge: distance feel at ground level, the lanes
   as routes, tree density, where realism breaks. Known soft spots
   going in: woods vanish past 2500m, no dirt splat under ribbons,
   streams sit on the surface, clouds unconfirmed in play mode.
2. **Castle territories** (the design successor the survey is waiting
   on): castle sites → territories → land types per territory →
   **bandits at the seams between lordships**. Replaces the temporary
   `MaxSurveyRadius` 1500m guard and the center-distance ownership
   rings in `LandParcels`.
3. **One manor = one contiguous town by the castle** (user-confirmed
   model; current scattered living plots are wrong) — probably lands
   with territories.

## Second lane: the economy's remaining halves

- Procedural **houses on living land**, population-driven (design doc's
  housing loop) — the visible face of `Estate.Households`.
- **Manor decision dials** (taxes/customs per the design doc) — the
  income side's interactivity.
- Build time / afford-gating / salvage losses — deliberately deferred
  with construction-is-instant; arrives as one package.

## Realism backlog (brief: `Docs/MarchesRealism.md`)

- Tier B: grass/scrub detail meshes, landuse splat variation,
  **hedgerows along parcel boundaries** (ties the survey to the visual
  field patchwork), height detail noise.
- Tier C: **Welsh 1m LIDAR at the castle sites** — brings Grosmont's
  real moat and mound into the heightmap (DataMapWales/NRW, free).
- Tier E: the 1220 subtraction pass (ongoing stance, not a task).
- Re-bake is `MarchesDressing → Dress` on the Terrain; features asset
  is local, no network.

## Backlog / open questions (carried)

- **Any new stored fact must join `CastleSave`** — schema is v6
  (plots, clock, households); v2+ load, v1 refuses.
- **Structural support deliberately absent**; no new system may assume
  construction is instantaneous in the world.
- Hand-test debt from 08-09 still open: right-click undo (all forms),
  wall chaining feel, stair rework (width/dial/perpendicular/foot).
- Folded/switchback stair — agreed direction, not speced.
- Watch-item: terrain wall drags through slab-borne buildings.
- Graduate the briefs (`Stairs.md`, `Doors.md`, `BuildingRebuild.md`,
  now `MarchesRealism.md`) into CLAUDE.md once playtested.
- Curved rooms floor chorded — trace-STAMP fill deferred.
- Britain-scale engineering when we leave 15km: floating origin, tile
  streaming, simulation LOD (distant manors as numbers).
- `BuildMenu` categories placeholder-ish; wall material lacks
  roughness/AO; walk speeds untuned; parapets/crenellations wait on
  wall types' detailing.
- Design queue after territories: renown (#11), special rooms (#14),
  AI-prop bake-off, walking liveliness (#12), hard-mode death (#4),
  liege (#5), AI nobles (#6), vassal departure (#7).
- Editor gotchas: wall graph is play-mode only; `execute_code` is a
  method body; same-call physics tests have unsynced colliders; public
  fields are scene-serialized (bit us AGAIN this session — `roadLift`);
  a draped mesh fights the terrain's LOD (`heightmapPixelError`), not
  its heightmap; read baked normals back before trusting a winding
  probe; mesh terrain-trees need `treeBillboardDistance` AND
  `treeMaximumFullLODCount` raised, not just `treeDistance`; Overpass
  is IP rate-limited — space queries, check `/api/status`.
