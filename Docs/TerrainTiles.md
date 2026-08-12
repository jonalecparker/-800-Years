# Terrain Tiles — the multi-tile ground (brief, 2026-08-11)

The user walked Grosmont → Skenfrith and the world held; the next fidelity
step — roads painted into the ground instead of draped over it, rivers
carved into it — is unrepresentable on one Unity terrain. A single terrain
caps at a 4097 heightmap and 4096 alphamap; over 15km that is ~3.7m per
sample both ways, and a 3m lane or an 8m river channel is one smeared
pixel. The fix is systemic and deliberate: the ground becomes a GRID OF
TERRAIN TILES behind a facade, which is the Britain-scale foundation
(tiered resolution, later streaming) arriving in its natural order — the
user's call, 2026-08-11: "do it right once rather than patch."

## What this brief is NOT

- **Not streaming, not floating origin.** At 15km from origin float
  precision is ~2mm — nothing wobbles, and 16 resident tiles fit in
  memory. Streaming is ADDITIVE on a tile grid (swap "all tiles loaded"
  for "tiles page in"); nothing built here gets thrown away when it
  lands. Floating origin waits for the same day.
- **Not the road-paint or river-carve passes themselves.** Those are the
  payoff sessions and land on top of this foundation. This brief only
  makes them representable.
- **Not a new save schema.** Earthwork ops are already world-space
  (`TerrainPads.Op` stores world points); replay is tile-agnostic. The
  save format does not change.

## The decisions of record

- **`Ground` is the ONLY way scripts read the terrain** (new static
  class). `Terrain.activeTerrain` is henceforth forbidden outside
  `Ground` and `TerrainPads` internals — with multiple terrains it
  returns an arbitrary one, and `SampleHeight` silently CLAMPS outside
  its own tile, which is how a 15km world quietly reports the wrong
  hill. The facade routes by XZ:
  - `Ground.TileAt(x, z)` — the Terrain whose XZ bounds contain the
    point (ties at shared edges go to either; the shared edge sample is
    identical by construction so the answer is too).
  - `Ground.HeightAt(x, z)` — world Y including the tile's origin.y,
    clamped to the nearest tile when outside all of them (the old
    single-terrain clamp behavior, preserved deliberately).
  - `Ground.Any`, `Ground.Bounds` — existence and world XZ union
    (LandParcels sizes its survey grid from this).
  - `Ground.BaseName` — the tile name with its ` [r,c]` suffix
    stripped, so CastleSave's terrain-name check matches saves made on
    the pre-split terrain.
  Cursor rays are UNCHANGED: `hit.collider is TerrainCollider` is
  type-based and every tile's collider passes it — the raycast path
  needed no facade.
- **Tiles share edge samples.** A source terrain of resolution R splits
  N×N only when R−1 is divisible by N; tile [r,c] takes the sample
  range [512r..512r+512] (for 2049/4), so adjacent tiles carry
  IDENTICAL edge rows. Seam consistency is by construction, not by
  stitching. `Terrain.SetNeighbors` (via allowAutoConnect + one
  grouping ID) handles LOD so cracks can't open at distance.
- **Resolution is a PER-TILE dial, and that is the point.** Heightmap
  and alphamap resolutions are independent per tile: road paint needs a
  dense alphamap and is happy with coarse height cells; river carving
  needs dense height cells only along its corridor; the LIDAR tier
  (Welsh 1m at the castle sites) upgrades two tiles' heightmaps and
  touches nothing else. v1 ships all tiles at source height resolution
  (513, 7.37m cells — upsampling 7m data buys nothing) and 1024
  alphamaps (3.68m/px, an 8× improvement over the single terrain's
  29.5). **The seam rule for the day resolutions differ**: the FINER
  tile's edge row must conform to the bilinear interpolation of the
  coarser neighbor's edge — the coarse tile is the authority at a
  mixed seam. Not exercised in v1; recorded so the LIDAR tier doesn't
  rediscover it as a crack.
- **`TerrainPads` state goes per-tile; the law does not move.** The
  pristine cache, runtime clone, and preview snapshot become a
  per-tile record keyed by Terrain; `Reshape` runs its same sample
  loop on every tile whose bounds the op touches. A bench crossing a
  seam writes both tiles' shared edge samples to the same world
  target from the same pristine height — consistent by the same
  by-construction argument as the split itself. `MaxDeviation`,
  `KeepOut`, the one-test doctrine: all unchanged, enforced inside the
  same loop they always were.
- **The tiler is an editor bake, not a runtime system**
  (`TerrainTiler`, ContextMenu like TerrainGenerator/MarchesDressing):
  it reads the CURRENT single terrain's TerrainData — heights, size,
  layers, material — and emits N×N child Terrain objects with per-tile
  TerrainData assets (`Assets/Terrain/Tiles/`), recomputed slope-rule
  alphamaps at the per-tile resolution, and the source terrain
  deactivated in place (kept as the re-bake source; the tiler's Clear
  reverses the whole thing). Tiles are named `<source> [r,c]` — see
  `Ground.BaseName`.
- **MarchesDressing dresses the GRID**: drapes via `Ground.HeightAt`,
  buckets each tree instance into the tile that contains it, and
  applies the tree-distance dials and pixelError per tile. It moves
  off `RequireComponent(Terrain)` — it dresses whatever `Ground`
  reports, which also keeps it working on a single-terrain scene.
- **The Spiš scene stays single-terrain and must keep working.** The
  facade over one terrain IS the old behavior; `TerrainGenerator` is
  untouched. A one-tile grid is not a special case anywhere.

## Consequences to watch

- `TerrainPads.RefreshSurface` reads slope thresholds from a scene
  `TerrainGenerator` if present; tiled Marches has none active, so the
  defaults (24/38 — the same numbers) apply. If the thresholds ever
  diverge per scene, they move onto the tiler.
- Old saves recorded `terrainName` = the pre-split name; `BaseName`
  keeps the match so loads stay silent. A save made on tiles loads on
  the single terrain with only the ordinary re-foot warning.
- Tree colliders bake per tile exactly as they did per terrain; walk
  mode should feel no difference — verify on the seam roads.
