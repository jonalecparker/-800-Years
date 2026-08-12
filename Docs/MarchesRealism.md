# Marches Realism — the dressing plan (2026-08-11)

The user flew Grosmont → Skenfrith and the distance feels right; before the
20-minute walk, the world needs to look like somewhere. This brief records the
exploration ("how far can we reasonably take making the terrain feel
realistic") and the slice being built now.

## What real data exists

OpenStreetMap via Overpass (free, no auth, © OpenStreetMap contributors, ODbL)
for the Marches bbox (51.822, -2.944, 51.958, -2.725) — counts verified
2026-08-11:

- **2,152 road/track/path ways** (`highway=*`) with full polyline geometry.
  Rural Monmouthshire lanes overwhelmingly follow their medieval
  predecessors, so minor-road geometry is an honest 1220 route network.
- **569 woodland polygons** (`natural=wood` + `landuse=forest`) — the actual
  woods of the Monnow valley, where they actually stand.
- **511 waterway ways** (`waterway=*`) including the Monnow itself — the
  river that defines both castles.

Wales also publishes free 1m LIDAR (DataMapWales / NRW) covering
Monmouthshire — that's the later site-fidelity tier, and it carries the real
castle earthworks (Grosmont's moat and mound, Skenfrith's platform).

## The tiers

- **A — real land cover** (this slice): woods as terrain trees from OSM
  polygons; roads as draped dirt ribbons from OSM polylines; the Monnow and
  its streams as draped water ribbons.
- **B — ground detail** (later): grass/scrub detail meshes, splat variation
  by landuse, hedgerows along parcel boundaries (ties into the survey — the
  game's own field patchwork becomes the visual one), height detail noise.
- **C — site fidelity** (later): 1m Welsh LIDAR patches around the two
  castles — Spiš-grade ground exactly where building happens.
- **D — atmosphere** (this slice): HDRP volumetric fog/clouds, sun angle,
  exposure. Biggest mood-per-hour on the list.
- **E — the 1220 filter** (ongoing design stance): subtract the present.
  Modern road classes render as the same dirt as lanes; no modern building
  footprints (the manor system places villages); plantations read broadleaf.

Not reasonable (deliberately out): photoreal vegetation everywhere, every
hedge in Britain, dynamic seasons.

## The v1 mechanics (decisions of record)

- **Features are a baked asset, not a live fetch**: Overpass JSON is
  preprocessed offline into `Assets/Terrain/MarchesFeatures.json` (world-XZ
  polylines/polygons, classified) using the verified georeferencing
  (z13 tiles x4029–4033 / y2707–2711; mpp = 15084/1279; the mapping that
  matched Graig Syfyrddin's summit to one pixel). Unity never talks to the
  network.
- **Roads are ribbon meshes, not alphamap paint**: the alphamap is 29.5m/px —
  a 3m track would be sub-pixel mush. A draped ribbon (terrain-sampled
  centerline, perpendicular offsets, slight lift) is crisp at any distance.
  Widths by class: through-roads 4m, lanes/tracks 3m, paths 1.8m — all the
  same dirt (tier E: tarmac becomes hollow-way).
- **Water is a draped ribbon too** (v1): the Monnow at ~8m, streams ~2m,
  translucent blue, sitting just above the ground line. HDRP's real water
  system is a later upgrade, not this slice.
- **Trees are Unity terrain trees**: graybox low-poly oak (code-built mesh,
  saved as prefab with a trunk collider) scattered deterministically
  (hashed, not Random) inside wood polygons; a bush prototype scatters at
  wood edges. Density tuned to keep total instances in the low hundreds of
  thousands.
- **Dressing is scene content, not stored gameplay facts** — nothing joins
  CastleSave; `MarchesDressing` is a ContextMenu bake like TerrainGenerator,
  re-runnable, with a Clear that removes everything it made.
- **No colliders on ribbons** (the walker must never trip on a road);
  tree trunks DO collide (you shouldn't walk through an oak).

## Shipped 2026-08-12: rivers carved + two-tier water; road ribbons retired

Docs/Rivers.md is the brief of record. The road ribbons are DELETED (the
user's call — the splat paint alone is the road); waterways are carved
into the tiles (idempotent bake, bank refs from the pre-split source) and
water ribbons sit flat at bed + fill, chunked by 300 m cell so
`RiverWaterLOD` swaps the near chunks for HDRP custom-mesh water surfaces
(vertex heights respected — HDRP treats world space as water space for
custom meshes) and back. `roadLift`/`waterLift` died with the road
ribbons; pixelError 2 still pinned for the water ribbons.

## Shipped 2026-08-11 evening: road splat (tier B's first slice)

The terrain became a 4×4 tile grid (Docs/TerrainTiles.md) and roads got
their ground: a third terrain layer (RoadLayer — the dirt layer's real
textures, warm-tinted; a flat white diffuse reads alpha=1 as FULL
smoothness in HDRP TerrainLit and shines like wet sand) painted along
every road corridor into the tiles' 1024 alphamaps — full strength over
roadbed + 1m, fading over 2.5m, the control map's bilinear blur adding
the rest of the softness. The ribbon stays on top for the crisp edge;
the halo reads as trampled verge. TerrainPads.RefreshSurface preserves
layers past grass/dirt, so earthworks under a lane keep its paint.
`splatResolution` is the dial for the day a corridor earns denser paint.

## Shipped 2026-08-11 (same day)

Tier A + D are in `Assets/Marches.unity`: 1,872 road ribbons, 438 water
ribbons, 249,019 trees/bushes (oak 32 tris, bush 20), fog + volumetric
clouds + afternoon sun in `Assets/Terrain/MarchesSkyFog.asset` (a clone —
the shared profile still drives the Spiš scene). ~50fps in editor play
mode. Two lessons paid for in debugging time, recorded in CLAUDE.md: a
draped mesh fights the terrain's LOD (pixelError), not its heightmap, and
ribbon winding mirrors silently — read baked normals back before trusting
a probe reproduction.
