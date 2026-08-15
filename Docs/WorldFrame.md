# The World Frame — BNG, 2km tiles, streaming-ready (brief, 2026-08-15)

**The decision** (agreed 2026-08-15, discussion in session): the game's
world coordinate system becomes the **British National Grid**, the
terrain becomes a grid of **2km tiles on even-kilometre BNG lines**,
and streaming is designed in from the start rather than retrofitted.
This supersedes the linear Web Mercator map and the 4×4 tiler. It is
the last moment the datum is cheap to change: after streaming ships,
every baked tile on disk is anchored to it.

## Why (the two problems this solves at once)

1. **The current frame doesn't scale.** World XZ today is Web Mercator
   times cos(51.89°) — one world metre equals one real metre only at
   the Three Castles' latitude. Across England the error runs ~−4%
   (Cornwall) to ~+10% (Scottish border). BNG is the projection the
   Ordnance Survey built for exactly this island: worst-case scale
   error ~0.05% anywhere in Britain.
2. **Every British dataset is BNG-native, tiled on round km lines.**
   NRW LIDAR (2km tiles), EA LIDAR (1km/5km tiles), OS terrain (10km
   sheets). A game tile sitting ON those lines ingests source data by
   axis-aligned rectangle copy — no rotation resample, no blur, no
   multi-file straddling arithmetic. At 3 castles that's convenience;
   at hundreds of castles and ~38,000 tiles it's the difference
   between a batch job and a debugging career.

Also learned this session, and the reason the re-tile question became
a datum question: **nothing about today's numbers was chosen.**
`terrainSize = (res−1) × demCellSize` — the map extent, the 7.3m cell,
the 3750m tile are all inherited from whatever the downloaded DEM
happened to measure. This brief replaces accidents with decisions.

## The world frame

- **World X = easting − E₀, world Z = northing − N₀, in metres.**
  E₀/N₀ is ONE whole even-kilometre constant pair, stored in ONE
  place (a static `WorldFrame`), and every real↔world conversion goes
  through it. **E₀ = 332000, N₀ = 210000** (revised 2026-08-15 from
  the brief's 334000/212000 when the ASC coverage was mapped: the
  shifted window is covered by the LIDAR already on disk minus 3
  far-NE-corner cells, vs 13 missing in the original). The window is
  [0..16000]²; all three castles keep ≥1.6km margin.
- **World Y = metres ODN** (the national vertical datum), terrain
  objects positioned to match. This retires the per-site datum-offset
  fitting the LIDAR bake needed (~−23.5m) — LIDAR and any future
  elevation data drop in with NO vertical fit at all. y=0 stops
  meaning "the lowest point of whatever DEM we downloaded".
- **Grid convergence**: BNG grid north differs from the current
  world's north by ~0.7° at our longitude. Nothing gameplay-visible
  cares (sun, camera, compass are all self-consistent); it only shows
  up as the rotation term in the save migration below.
- **Float precision law**: floats carry ~7 digits, so raw eastings
  (up to ~660,000) would cost centimetre precision — hence the E₀/N₀
  subtraction. At Britain scale one static offset stops sufficing;
  the streaming phase adds origin rebasing. The law that makes that a
  one-constant change later: **no code converts between real and
  world coordinates except through `WorldFrame`.** Stored facts that
  must be Britain-absolute (tile identity, future cross-region saves)
  store grid coordinates, not world floats.

## The tile grid

- **2km × 2km tiles, corners on even-km BNG intersections.** A
  tile's identity IS its grid reference — Grosmont's tile is SO4022
  (easting 340000–342000, northing 224000–226000). Tile names carry
  it: human-readable, globally unique, permanent, and the natural
  key for a streaming index covering all of Britain.
- **The resolution ladder** (heightmaps are 2ⁿ+1):
  257 → 7.81m (base countryside, ≈ today's density) · 1025 → 1.95m
  (castle tiles, the 2m LIDAR) · 2049 → 0.98m (the coming 1m
  programme) · 4097 → 0.49m (50cm surveys, if ever). A future
  fidelity bump is a per-tile re-bake, never a re-tile.
- **The castle rule**: any tile a castle's LIDAR patch (~600m radius
  + margin) touches gets 1025. Edge-straddling castles bump 2 tiles,
  corner-sitters 4 — the rule, not tile-size luck, is what handles
  "hundreds of castles WILL sit on edges". Bumped neighbours meet at
  same-resolution seams (no cracks by construction); the mismatched
  seam only ever lies at the bumped set's boundary, out in smooth
  countryside. There, the fine tile's edge samples are **pinned
  collinear** with the coarse neighbour's edge (inside the bake, so
  it can't be forgotten); residual LOD hairlines are accepted until
  seen in practice.
- **Alphamaps/trees scale with the tile**: 512 alphamap on a 2km tile
  keeps today's ~3.7m splat texel; per-tile tree lists as now.

## The bake pipeline: THE TILE IS THE ARTIFACT

The "deactivated source terrain as re-bake authority" doctrine dies
here (it cannot exist at England scale — there is no 700km Terrain).
Its replacement: **each tile bakes independently, deterministically,
from source rasters + stored facts**, and the un-do of anything is
"re-bake the tile from sources."

    per tile: Base heights → Carve rivers → Dress (splats, trees,
              features)

- **The base DEM IS the LIDAR** (discovered at implementation,
  2026-08-15): the SO31/SO32/SO41/SO42 ASC sets already on disk cover
  the whole shifted window except 3 far-NE cells. Base tiles are the
  2m DTM area-downsampled to 7.81m; castle tiles the same source at
  1.95m. The separate "LIDAR patch" step DISSOLVES — no feather, no
  per-site datum fitting (world Y is ODN), no water keep-out (carve
  runs after base, never before it). Gaps (3 corner cells + NODATA
  holes) fill from the legacy terrarium DEM through `WorldFrame`'s
  legacy chain, logged per tile — no silent fill.
- All bake inputs are world/grid-space facts (feature polylines,
  castle sites, patch sets), so two neighbouring tiles compute
  identical shared-edge samples by the same argument the old tiler
  made — seams cannot open.
- The feature bake (OSM → `MarchesFeatures.json`) re-runs offline
  with the BNG mapping. Castle routes, grounds, survey all consume
  world XZ as before — they never knew what projection fed them.

## Streaming (phased — the grid is built for it, the loader comes after)

- **Phase A (this rebuild)**: the Marches window, 8×8 = 64 tiles,
  fully resident — no loader yet, but every tile is already the
  independent, grid-named artifact the loader will page.
- **Phase B (the loader)**: detail ring ~4km (~25 resident tiles:
  collider + full splats + trees), paged by grid key; 257-tile
  collider bakes are ~ms, 1025 castle tiles the heavy case and
  time-sliceable. Beyond the ring, a coarse far-field (e.g. 10km
  sheets at low res) for the vistas — design deferred to its phase.
- **Phase C (Britain)**: origin rebasing (floating origin), regional
  save identity, simulation LOD for far castles. Additive on the
  same grid; nothing in A/B changes shape for it.
- Frame cost is governed by `pixelError` LOD and resident-ring AREA,
  not tile count — 2km was chosen inside the performance-comfortable
  range (roughly 1–4km) at the point that lands on the data grid.
  Import convenience never overrode play; they happen to agree.

## Migration (the honest bill)

- **Terrain**: regenerate in the BNG frame from re-fetched BNG-native
  DEM. The old source terrain and 4×4 tiles are deleted, not kept.
- **Georeferenced content**: CastleSite markers move to their
  documented grid refs (we already know them); MarchesFeatures.json
  re-bakes; `CastleLidar`'s Mercator→BNG chain collapses to the
  identity (keep the code in a corner? No — delete it; the WFS
  catalogue query in `CastleLidar.md` is the provenance).
- **Saves**: old saves live in the old frame — off by a ~0.7°
  rotation + small scale, tens of metres at map edges. The exact
  old→new transform is known in closed form, so a **one-time
  similarity-transform migration** (rotate+scale+translate every
  stored XZ, re-foot heights on load as normal) carries the permanent
  castle save across; bump the save schema so migrated-ness is a
  version fact, not a guess. Pre-migration saves refuse with advice
  (the v1 precedent) if the transform ever proves unsound in hand
  testing.
- **Survey**: the disk cache invalidates itself (its signature sweeps
  the heightmap) — but bump `LandParcels.CacheVersion` anyway; the
  algorithm's INPUT frame changed, and stale-cache bugs are silent.
- **TerrainPads**: per-tile pristine caches regenerate at first use;
  saved earthwork ops are world-space XZ and ride the same save
  migration. The ±3m law measures from the new pristine (which now
  includes LIDAR ground everywhere a patch exists — as before).
- **Spiš stays untouched**: single-terrain fallback scene, no georef,
  `Ground` facade already handles it.

## Order of work

1. `WorldFrame` (the constants + conversions) and the BNG-native DEM
   fetch for the Marches window.
2. Tile baker: per-tile Base→Patch→Carve→Dress in the new frame,
   castle rule + edge pinning included from the first bake.
3. Scene swap: new tiles in, markers moved, features re-baked,
   `Ground`/`TerrainPads` pointed at the new grid (their doctrines —
   facade-only access, per-tile state, world-space ops — carry over
   unchanged).
4. Save migration tool + schema bump; walk Grosmont's spur at 1.95m
   and judge.
5. Streaming loader (Phase B) as its own session(s).

## Implementation record (2026-08-15 — steps 1–4 code-complete)

Everything below is written and verified off-editor; the editor-side
runbook at the end has not run yet.

- **`WorldFrame.cs`**: constants (E₀ 332000 / N₀ 210000, TileSize
  2000, HeightRange 700), BNG↔world, WGS84↔BNG both directions
  (forward verified against the castles' documented grid refs to the
  expected ~50m of their 100m rounding; round-trips to millimetres,
  compiled and tested outside Unity), grid-ref tile namer
  ("SO4024"), and the LEGACY Mercator chain — migration-only, marked
  for deletion when the era closes. `LegacyLift` (+23.85m, the
  Grosmont datum fit from 08-14) lifts old-frame Y to ODN.
- **`MarchesBaker.cs`** (component; TerrainTiler + CastleLidar are
  DELETED): `Migrate Scene From Mercator Frame` (one-time — moves
  CastleSites and the Neighbor Castles graybox through the legacy
  chain with the ~0.7° convergence yaw, adopts the MarchesDressing
  component off the old source terrain, deletes the old world and its
  assets and the stale survey cache; guarded against double runs),
  `Bake Tiles` (loads both ASC sources, bakes 8×8 grids — box-filter
  downsample for base, bilinear for castle tiles, per-sample
  T50 fallback logged per tile, >2% fallback named in the summary —
  pins fine-against-coarse edges, carves rivers in-memory from
  PRE-carve refs, writes `MarchesRiverRefs.json`, emits
  TerrainData assets + tile objects named "Marches [SO4024]"),
  `Clear Tiles`. Flight strips MERGE per sample (the old per-cell
  dictionary kept one file arbitrarily — the west column is
  40–98% NODATA per strip and needs the union). The carve ref
  EXCLUDES the centerline now: LIDAR ground carries real incised
  channels, and a bed-level ref would re-dig them; against edge-level
  refs an already-deep channel is untouched (min(current, target)).
- **OS Terrain 50** (`TerrainSources/os-terrain50/`): full-GB ASCII
  Grid zip from the OS Data Hub OpenData API — repeatable query:
  `GET https://api.os.uk/downloads/v1/products/Terrain50/downloads`,
  take the "ASCII Grid and GML (Grid)" item's url (162MB, no key).
  SO31/SO32/SO41/SO42 extracted to `asc/`. Gitignored like the LIDAR.
- **`Tools/bake_marches_features.py`** (committed this time; the
  Mercator-era script was session-scratch and is gone): Overpass →
  BNG world frame → Douglas-Peucker 1.5m → clip to window+200m
  (Overpass returns whole ways — the Monnow arrived with 8km of
  tail) → `MarchesFeatures.json`. Ran clean: 2996 roads, 774
  waterways, 740 woods, every vertex in [−200, 16200]. Mirror
  fallback + retries built in (overpass-api.de 504s freely).
- **`MarchesDressing`**: carve moved out (the baker owns it); water
  ribbons consume the SAVED refs (refuse loudly if missing/stale —
  levels must never re-derive from carved ground); `Resample`/
  `Unflatten`/`SlopeNumbers` public (one definition each is what
  makes refs index-align and paints agree); road splat paints at
  each tile's own alphamap resolution; no Terrain requirement.
- **`CastleSave` v8**: records the frame. Pre-v8 saves loading into
  the Marches (current BaseName "Marches", saved "Terrain") migrate
  once — every stored XZ through the legacy chain, every finite
  absolute Y + LegacyLift, sentinels and relative quantities
  untouched, terrain name rewritten. Spiš saves (same old name, but
  frames agree in their own scene) never migrate.

## First hand-test findings (2026-08-15, after the first bake)

The user ran the migration + bake + Dress and played. Three findings,
all addressed the same day:

- **The castle routes broke** ("castles share no road route", roads
  painted wrong): the feature bake's Douglas-Peucker deleted mid-way
  JUNCTION vertices (OSM ways that join share the literal node, often
  mid-way along one of them), shattering the road graph into 800
  components — and Skenfrith's nearest vertex was a 2-vertex orphan
  that's genuinely disconnected in OSM (a driveway mapped without a
  shared node). Fixed twice: `bake_marches_features.py` now counts
  node occurrences across road ways and forces shared coordinates
  through DP (roads only — waters/woods output stayed byte-identical,
  so the river refs kept aligning); `CastleRoads` now snaps castles
  to the nearest vertex ON THE LARGEST COMPONENT (the main network IS
  the network the routes ride). Verified offline: 40 components, all
  three castle pairs route. The stale survey cache was deleted, and
  `LandParcels.SurveySignature` now MIXES THE FEATURES ASSET (and
  `castleRoadsOnly`) — the floods stripe against roads/rivers, so a
  re-baked features file must invalidate the cache by itself.
- **The LIDAR's naked earthworks read as artifacts**: field-boundary
  banks and sunken lanes are real (the point of the LIDAR), but a
  hedgebank without its hedge on billiard-table grass looks like a
  rendering bug. `MarchesDressing` grew a HEDGEBANK PASS (crest
  detection on the fine tiles' heightmaps — sample proud of a 7m ring
  by `hedgeBankSignal`, ring spread >4m = hillside, skip; keep-outs:
  roads/waters cells, woods, castle grounds; capped, deterministic)
  and FIELD MOTTLE (two-octave world-space noise leaning the
  grass/dirt mix, `splatMottle`) so open country isn't one material.
- **The carve doubled sideways**: OSM centerlines are generalized and
  the real channel meanders ±5–10m around them — where they diverge
  the carve cut a braided second trench beside the real one (visible
  on 2m castle tiles). The carve now THALWEG-SNAPS: each station
  scans across the corridor (±8m past the half-width) for the lowest
  existing ground, snaps to it only when it sits ≥0.3m below its own
  banks (else terrain noise — stay on the map line), smoothed along
  the run. The snapped stations are SAVED in the refs
  (`RiverRefs.Row.pts`) and the water ribbons AND the bed splat
  follow the channel AS-BUILT, never the map line.
- **Rivers on 257 tiles were angular zigzags** (an 8m channel at 7.8m
  sampling is one sample wide): resolution now follows FEATURES, not
  just castles — tiles crossed by a waterway ≥ `riverTileMinWidth`
  (5m) bake at `riverResolution` (513, 3.9m). Edge pinning
  generalized to three tiers (pins run coarsest-first, and a corner-
  consensus pass gives every four-tile lattice corner the coarsest
  tile's value — the pin order alone could split a corner by cm).
- Unity's "different heightmap resolution — stop neighboring" errors
  at mixed-resolution seams are the known accepted LOD-hairline
  consequence (geometry is crack-free by pinning; auto-connect just
  can't stitch LOD across resolutions). Cosmetic, deferred with the
  LOD seam work.

## The editor runbook (next session, MCP or by hand)

1. STOP PLAY. Create an empty GameObject "Marches Baker" in the
   Marches scene; add `MarchesBaker`.
2. Context menu → **Migrate Scene From Mercator Frame** (once).
3. Context menu → **Bake Tiles** (minutes — ~64M ASC values parse).
   Check the summary log: 10 castle tiles expected (SO3822 SO3824
   SO4022 SO4024 / SO4418 SO4420 SO4618 SO4620 / SO3616 SO3816),
   T50 fallback confined to the west column and far corners, 0
   unfillable.
4. On the baker's own MarchesDressing component → **Dress**.
5. Play: survey regenerates over the real ground (new cache file
   SurveyCache-Marches.json); load the permanent castle save — the
   v8 migration fires, footings re-foot; walk Grosmont's spur.
6. `/wrap` should fold the doctrine changes into CLAUDE.md: the
   terrain-tile-grid section (Split/source-terrain doctrine → the
   tile-is-the-artifact bake), the LIDAR section, Rivers' un-carve
   line, and the new WorldFrame law.

## Open questions (decide during implementation, none block step 1)

- Whether the Marches keeps a 16×16km window or grows opportunistically
  once tiles stream (the window is just "which tiles exist on disk").
- Far-field representation for Phase B (coarse sheets vs impostors).
- Whether migrated saves keep `BaseName` matching or move to
  grid-windowed identity (leaning: keep BaseName until Phase C forces
  regional identity).
- The 1m LIDAR upgrade lands as "re-bake castle tiles at 2049" — no
  new design, but watch bake times.
