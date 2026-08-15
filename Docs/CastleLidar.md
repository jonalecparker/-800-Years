# Castle-site LIDAR — real ground under the Three Castles (2026-08-14)

The user's ask: "we need to start getting 1 or 2m terrain data under
the castle sites." **SHIPPED same day**: `CastleLidar` (component
beside MarchesDressing, ContextMenu "Apply Lidar Patches") baked the
2m NRW DTM into the tiles around all three castles — 59,950 samples
across 3 tiles. Verified: datum offsets came out −23.85/−23.30/−23.49m
at the three sites (the world's vertical datum vs ODN — their
near-identity across 27k ring samples is the georef proof), the patch
edge steps 0.07m over 10m, and patched terrain matches lidar+offset
to centimetres on open ground (~1m on the mound itself, the expected
downsampling error). Re-running `TerrainTiler.Split` is the un-patch.

**Gotchas paid for**:
- Mono's `ZipArchive` refuses the NRW zips ("local file header is
  corrupt") — extract with PowerShell `Expand-Archive` instead; the
  loader reads loose `.asc` from `TerrainSources/lidar/asc/`.
- ASC heights are MILLIMETRES (`_mm_units`); rows start at the NORTH
  edge; cell-center registration.
- Only header-parse tiles outside the patch reach — full-parsing all
  ~100 tiles costs minutes for nothing.
- The world→BNG chain (doc-anchored Web Mercator → WGS84 → Helmert →
  Airy TM) verified against White Castle's documented SO 379 167 and
  the other two castles' real grid refs, all within ~50m.
- The survey disk cache invalidates itself (its signature sweeps the
  heightmap) and the next play run regenerates parcels + castle
  grounds over the real earthworks — by construction, no extra work.

**Honest limit, said out loud**: the tile heightmaps sample at ~7.3m,
so the 2m data arrives DOWNSAMPLED — the real spur, ditches and moat
shapes, not 2m mesh density. Crisper ground under the castles is the
phase-2 resolution bump below.

## What we have (in `TerrainSources/lidar/`, outside Assets on purpose)

NRW LIDAR archive, **2m DTM, 2006 flights, OGL license**, Esri ASCII
grid (.asc) tiles of 2×2km (1000×1000 cells), **EPSG:27700 (British
National Grid)**, heights in **MILLIMETRES** (`_mm_units` in the file
names — divide by 1000), NODATA −9999:

- `2m_res_SO42_2006_dtm.zip` (34MB) — covers **Grosmont**
  (tile SO4022/SO4024, castle at ~BNG 340520, 224380) and
  **Skenfrith** (~345700, 220200).
- `2m_res_SO31_2006_dtm.zip` (80MB) — covers **White Castle**
  (tiles SO3616/SO3816, castle at ~BNG 337900, 216700).

Verified by reading a header from the Grosmont tile: `ncols 1000,
nrows 1000, xllcorner 340000, yllcorner 224000, cellsize 2,
NODATA_value -9999`, values ~65,000mm = 65m — honest Monnow valley.

**Where it came from — the repeatable path**: DataMapWales GeoServer,
WFS on the archive tile catalogue gives per-tile direct download URLs:

    https://datamap.gov.wales/geoserver/ows?service=WFS&version=2.0.0
      &request=GetFeature
      &typeNames=inspire-nrw:NRW_LIDAR_ARCHIVE_TILE_CATALOGUE
      &bbox=<minE>,<minN>,<maxE>,<maxN>,EPSG:27700
      &outputFormat=application/json

Each feature carries `DSM_URL`/`DTM_URL` into
`https://lle.blob.core.windows.net/lidar/...` plus resolution and
flight year. The 2006 archive is 2m here; the Wales-wide **1m
2020-23 programme** is (so far) published as point cloud, not as a
raster on this server — when its DTM lands in the catalogue, the same
query hands us the upgrade. 2m starts us honestly; the castles' earth-
works (Grosmont's moat and hornwork, White Castle's wet moat) are
metres deep and will read clearly even at 2m.

## The importer (next session's build)

An edit-mode step in the bake order, run BETWEEN Split and Carve:
**Generate → Split → ApplyLidarPatches → Carve Rivers → Dress.**
Re-running Split remains the un-patch, exactly like the carve.

1. **Georef**: world XZ ↔ WGS84 is the verified linear Web Mercator
   map (CastleSite georef). BNG ↔ WGS84 needs the standard OSGB36
   Helmert + Airy ellipsoid math (~5m absolute accuracy without the
   OSTN15 grid). 5m offset at 7.3m heightmap samples is tolerable for
   a first pass; if the motte visibly sits off its scene marker, fit
   a small affine from 4 corner control points instead of chasing
   OSTN15.
2. **Patch**: for each castle site, resample the ASC (bilinear, skip
   NODATA) into the terrain tiles over a radius (~600m — grounds +
   village), **feathering** the outer ~150m band into the existing
   base heights. Fit a constant vertical offset over the feather ring
   first (mean of patch−base) so a datum disagreement between the
   LIDAR and the base DEM becomes a smooth blend, not a cliff.
3. **Shared tile edges** get identical values by the same argument
   the tiler makes — write in world space, sample the same function.
4. **Resolution truth**: the tiles hold ~7.3m samples, so 2m data is
   downsampled for now — still a huge win over the base DEM (the
   ditch profiles, the motte, the river scarps all survive at 7m).
   Phase 2, if the castles deserve it: bump ONLY the castle tiles'
   heightmap resolution (tiles are separate Terrains and may differ)
   — but that opens LOD seam work; don't start it casually.
5. **Downstream facts**: TerrainPads' pristine cache picks the patch
   up as pristine (bake-order argument, same as the carve); the ±3m
   law then measures from the REAL earthworks. The survey cache
   signature sweeps the heightmap, so a patch invalidates it and the
   parcels regenerate over the new ground — both for free.

## Deferred

- DSM (surface w/ trees & buildings) — downloaded URLs exist; only
  the DTM matters for ground.
- 1m/50cm upgrades when the 2020-23 rasters reach the catalogue.
- OSTN15-grade georeferencing (only if the first pass visibly drifts).
