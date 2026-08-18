# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. The world direction is settled:
> **real Britain, real castle sites, Europe-ready** (memory +
> `Docs/MarchesRealism.md`).

**Where we left off:** The 08-16/17 session finished the wall-detailing
build order. **Buttresses** shipped — face piers as edge data, clasping
corner blocks as node data, both governed by THE FOUNDING RULE (a pier
rests on the first solid thing beneath it: a platform under its whole
footprint, else the earth). **Crenellations** shipped — a battlement is
a span on the edge plus a merlon on each joint it crosses, drawn by one
drag that follows the masonry through the graph, with persistent
Ctrl+scroll height and Alt+scroll spacing dials. Two cross-cutting
fixes fell out: **every runtime mesh must call `RecalculateTangents()`**
(the stone material's parallax march eats untangented faces), and
**`WalkMode.MoveSwept`** caps each frame's travel at 0.15m, which is why
stairs are climbable again at 10 m/s and 17 fps. CastleSave is v12.

## Top priority: keep building at Grosmont

1. **Hand-test the crenel gesture** — the hover ghost, the pending
   phase and the two dials were each verified by probe but never
   driven by a real mouse. Also worth a look: a battlement on a wall
   that carries an upper storey puts merlons inside that wall
   (deliberately ungated — the wall's top is the wall's top).
2. **Keep building the castle** — the user builds, we fix what the
   building surfaces. Still untested from earlier sessions:
   foundation rect/circle shapes, chain angle stepping, the cutaway
   against terraced foundations.
3. **Income rebalance** — ~11× hot at 200m cells, hotter since bandits
   stood down. Recalibrate `Estate` rates.

## Wall detailing — the order is complete

String course, quoins, buttresses and crenellations all shipped. No
next detailing item is chosen. Offered and not taken up: plinths, a
palisade timber material, stochastic/hex tiling in Shader Graph, a
per-edge string-course opt-out.

## Second lane: the economy's remaining halves

- **Manor decision dials** (taxes/customs per the design doc).
- Build time / afford-gating / salvage losses — one package.
- The land ladder past rung one: what makes clearing/buying land worth
  doing (yield? renown?).
- Population currently a debug dial — hook `Estate.Households` to
  something real when the economy deepens.

## Realism backlog (briefs: `WorldFrame.md`, `MarchesRealism.md`, `Rivers.md`)

- Far-NE tile [6,7]: 2,836 samples with no source (flat 60m stand-in)
  — eyeball it; the LIDAR gap is real, T50 covered most of it.
- Mixed-resolution seams: Unity's auto-connect can't stitch LOD across
  257/513/1025 (console spam is that fact; geometry is crack-free by
  pinning). The LOD-seam pass is deferred.
- 1m LIDAR upgrade when the 2020-23 rasters land = re-bake castle
  tiles at 2049 — the ladder already carries it.
- Tier B remainder: grass/scrub detail meshes, landuse splat
  variation, height detail noise.
- TimberWoods still hits its 250k cap every survey — widen
  `timberTreeSpacing` (dial, user's call).
- Rivers "good enough for now" (user, 08-12): flow, foam, the altitude
  LOD seam stay deferred. Thalweg channels shipped 08-15, unwalked.
- `TerrainSources/` is gitignored (~1.2GB); `WorldFrame.md` records
  both re-download queries.

## Backlog / open questions (carried)

- **Any new stored fact must join `CastleSave`** — schema v12; v2+
  load, v1 refuses.
- **Structural support deliberately absent**; nothing may assume
  construction is instantaneous in the world.
- The rim-snap watch: wall-to-foundation edge snapping failed on the
  user's FIRST play of a session (08-15) and resisted every probe. If
  it recurs, note whether it's the first play after opening the
  editor, fresh vs loaded foundation, and whether tapping Alt clears
  it — then probe live in the broken state.
- Deferred by design: river-weighted territory seams, bandit camps
  (waits for combat), edge OWNER field, Spiš annulus tracing.
- Hand-test debt: right-click undo forms, wall chaining feel, stair
  rework (folded/switchback agreed, not speced), foundation shapes,
  chain angle stepping, the crenel gesture.
- Watch-item: terrain wall drags through slab-borne buildings.
- Graduate playtested briefs into CLAUDE.md (`Stairs.md`, `Doors.md`,
  `BuildingRebuild.md`, `MarchesRealism.md`, `Rivers.md`,
  `Territories.md`, `VillageHouses.md`, `WorldFrame.md`).
- Curved rooms floor chorded — trace-STAMP fill deferred.
- Britain-scale: Phase B streaming and Phase C (origin rebasing,
  regional saves) are designed in `WorldFrame.md`, deliberately later.
- `BuildMenu` categories placeholder-ish; walk speeds untuned
  (`walkSpeed` 10, `runSpeed` 50 — fast, and the reason `MoveSwept`
  had to exist).
- The dressing/baker component sits on a GameObject named
  "GameObject" — rename it.
- Design queue: renown (#11), special rooms (#14), AI-prop bake-off,
  walking liveliness (#12), hard-mode death (#4), liege (#5), AI
  nobles (#6), vassal departure (#7).
- Editor gotchas: wall graph is play-mode only; **`WallEdge.All` /
  `WallNode.All` read stale or EMPTY after a mid-play recompile — use
  `FindObjectsByType` in any probe run after one**; STOP PLAY before
  edit-mode bakes; `execute_code` is a method body; public fields are
  scene-serialized (the `stairWidth` trap); draped meshes fight the
  terrain LOD; `Terrain.activeTerrain` FORBIDDEN outside
  Ground/TerrainPads; fire long bakes via `EditorApplication.delayCall`
  and verify by console STATE (the MCP relay times out ~30s and a
  STALE SESSION eats calls silently — re-`initialize` when
  execute_code returns success:false/null); `read_console` takes
  `filter_text`; edit-mode code never sees `LandSurvey.Instance`; bump
  `LandParcels.CacheVersion` when the survey algorithm changes.
