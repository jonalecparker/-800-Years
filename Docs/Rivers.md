# Rivers — carve + two-tier water (2026-08-11)

The user walked the lanes and called the next two moves: drop the road
ribbons (the splat paint alone reads better — "just the terrain"), and make
the rivers real: carve the terrain out according to river size, put water in
the channel, and render that water cheaply everywhere but expensively up
close. This brief records the design.

## Roads: paint only

The draped road ribbons are gone from `Dress()` — the third terrain layer
painted along every corridor (MarchesRealism.md, tier B's first slice) IS
the road now. The splat falloff numbers stand as tuned; the ribbon's "crisp
edge" job is retired by the user's eye, not replaced. Water ribbons remain —
they're the cheap tier of the water story below. `roadLift` died with the
road ribbons; the pixelError contract (tiles pinned to 2) stays, because the
water ribbons still live by it.

## The carve

`MarchesDressing.CarveRivers()` (ContextMenu, edit-mode bake) lowers the
tiles' heightmaps along every waterway polyline. **Size sets the cut**:

- depth = clamp(width × `carveDepthPerWidth` 0.22, 0.4 … 1.8 m) — the
  Monnow (~8 m) cuts ~1.8 m, a 2 m stream dips ~0.45 m.
- profile: full depth across the channel (±width/2), smoothstep back to
  zero across the bank (`bankWidthMin` 6 m or width × 1.5, whichever is
  wider — the 7.37 m heightmap cells can't hold a sharper shoulder).

**Idempotent because the reference never moves**: each centerline sample
captures a bank reference — the LOWER of the two ground heights just
outside the falloff — sampled from the **pre-split source terrain**, never
the live tiles, and the carve applies `newH = min(currentH, bankRef −
depth × profile)`. The first cut of this bake sampled banks live and the
second-run test caught it RATCHETING (65k samples still moving): at
confluences and tight meanders a bank point lands inside another channel's
carved zone, so every run lowered the refs and dug deeper. The source
never carves, so targets are a pure function of features + source ground
and re-running is a measured no-op. Seams hold because shared edge samples
compute the same world-space target on both tiles.

**What the carve is NOT**: it does not touch the deactivated pre-split
source terrain — re-running `TerrainTiler.Split` is the un-carve (bake
order: Generate → Split → Carve → Dress). It is not a save fact: like all
dressing it's scene authoring; TerrainPads caches the carved bed as
pristine at first play use, so the deviation law (±3 m) measures from the
riverbed — players can bridge a river but never fill one in beyond the law,
the same doctrine that forbids moat-digging in reverse.

Trees learned to avoid water: the wood scatter skips cells near a waterway
(same spatial-hash mechanism as the road exclusion), so the Monnow doesn't
run through trunks.

## The water — two tiers, split by distance

**Tier 1 (everywhere, cheap)**: the draped water ribbons, re-shaped for the
channel. Each ribbon vertex row now sits FLAT across the width at the water
level — carved bed + `waterFill` (0.5) × depth — and the ribbon is cut
wider than the channel so its edges bury themselves in the rising banks (no
shoreline gap; terrain hides what's underground). Ribbons are chunked by
**spatial cell** (`waterCellSize` 300 m) instead of by vertex count, which
is what makes the distance swap possible, and each chunk carries a
`RiverChunk` component recording its water-level range at bake time.

**Tier 2 (near the camera, expensive)**: `RiverWaterLOD` (runtime, ticks
every frame by design — it's a view system like tree LOD, no stand-down:
it reads no input and must keep working in walk mode, which is where it
matters most). Every `checkInterval` it finds the water chunks within
`upgradeRadius` of the camera, caps them at `maxActive`, and swaps each:
the cheap renderer hides, an HDRP `WaterSurface` (custom-mesh geometry over
the same chunk mesh) appears. Walking away swaps back. HDRP water must be
enabled in the pipeline asset (one-time settings change).

**The honest guard**: custom-mesh water keeps the mesh's own vertex
heights — HDRP sets waterToWorld to IDENTITY for custom meshes (verified
in the package source before trusting it), so a sloping chunk renders as
sloping water and banks are never clipped. The guard is therefore
COSMETIC: the ripple simulation is a horizontal model, and a chunk whose
water level falls more than `maxChunkFall` (8 m) across its span is a
torrent that would shimmer like a tipped lake — it keeps its ribbon. The
Monnow and its valley reaches upgrade; tumbling hill brooks stay ribbons.
The guard reads the `RiverChunk` numbers the bake stored, not a live
re-derivation. (HDRP also draws the assigned mesh EXPLICITLY with the
water material — the ribbon renderer disables during an upgrade without
affecting the water.)

## Shipped 2026-08-12 (measured)

- Road ribbons deleted (6 chunks + assets); splat paint carries all 1,872
  roads alone. Water ribbons: 438 waterways into **1,332 cell chunks**.
- Carve: 124,457 channel samples, 89,761 heightmap samples lowered across
  16 tiles. Monnow cuts 1.72–1.86 m below its lower bank (target 1.76);
  2 m streams dip ~0.2–0.4 m (the 7.37 m cells' honest limit). Re-run
  moves 30 samples (16-bit quantum stragglers) and 0.00000 m of ground;
  seams bit-perfect across all 24 tile pairs. Trees: 212,951 (water
  keep-out removed ~36k riverside trunks).
- Water level measured +0.90 m over the carved bed (waterFill 0.5 ×
  1.8 m). LOD verified both directions in play mode: 3 surfaces active at
  the reach / ribbons hidden, 0 surfaces + all 1,332 ribbons back at
  900 m. 704/1332 chunks pass the 8 m guard — and **148/166 of the
  big-river cells**, the ones that matter. Editor frame cost ≈ 7 ms for
  3 sims (dials: maxActive, upgradeRadius, waterSimulationResolution in
  the HDRP asset).
- One-time settings change: `supportWater = true` on the HDRP Performant
  asset (shared with Spiš — harmless there, no surfaces exist in it).

## Second pass, same day (the user's first water test)

- **Riverbed paint**: a FOURTH terrain layer (`RiverbedLayer` — the dirt
  textures remapped dark wet brown, via the shared `LoadOrCreatePaintLayer`)
  along every waterway: full strength past the water ribbon's own edge so
  no grass survives underwater, fading out at the bank line. Where road
  and river paint meet, the RIVER wins (a ford's channel is wet mud, not
  worn track). Verified bed = 1.00 under the Monnow; RefreshSurface
  preserves it like the road layer.
- **Waves at brook scale**: HDRP's river defaults are SEA-sized
  (largeWindSpeed 30) — the big rolling swells the user flagged. The LOD's
  Waves header dials swell to 3, ripples 4.5, chaos 0.6/0.85, current
  0.45, time 0.8, all scaled by `RiverChunk.maxWidth` (a new bake fact) so
  the Monnow keeps visible flow while a 2 m stream barely stirs (measured:
  Monnow chunk largeWind 3.0 / band0Mult 1.0, stream chunk 0.9 / 0.3).

## Third pass (the user's hillside screenshot): terraces, not gashes

Two defects, one screenshot. **Cross-slope over-dig**: bank refs were the
LOWER of two samples 11 m out — on a hillside that sample sits metres
below the uphill ground, so streams carved deep shadowed zigzag GASHES.
Since refs read the immutable source terrain (the ratchet fix), they can
be sampled CLOSE-IN: `ChannelRef` = min of the centerline and the two
points just past the channel edge, bounding the cross-slope contribution
to ~2 m of run — a terrace notch. **Distance pop**: the water ribbons
"loaded in when close" because the terrain's distance LOD legitimately
simplifies a sub-cell trench away, and the risen simplified ground
z-killed bed-level water. Fix is the user's instinct — raise the water:
level = ChannelRef − (1 − `waterFill` 0.7) × depth (small freeboard below
grade, floored 0.12 m above the carved bed so a submerged run never
shreds into shards). Verified from a 20%-grade stream reach: thin blue
lines to the horizon, no gashes, no pop.

**Fourth pass — the freeboard can't win alone**: the terrain LOD's height
error is ~pixelError (2px) of SCREEN space, i.e. it GROWS with distance
(metres at a kilometre), so any fixed raise eventually loses and far
ribbons still dashed (the user's third screenshot). `RiverWaterLOD` now
counters it the same way the error scales: far chunks lift by
`liftPerMeter` (0.004) × (distance − `liftStart` 250 m), capped at
`maxLift` 8 m — sub-pixel at the distance it applies, zeroed near the
camera and on an HDRP upgrade. A VIEW correction owned by the view
system, never baked into the meshes.

## Deliberately out (v1)

- **The LOD seam from altitude** (user, first water test): from high up
  you can see where HDRP water hands back to ribbons. Revisit when
  there's performance budget — candidates: altitude-scaled upgradeRadius,
  larger maxActive, or a mid-tier ribbon material. Deferred deliberately.
- Flow/current maps, foam, waterfalls at steep chunk boundaries.
- Water gameplay (fords, bridges as objects, drowning) — dressing only.
- Heightmap upscaling along river corridors (the tiered-resolution lane
  exists for when a channel earns real cross-section).
- Any CastleSave involvement — nothing here is a stored gameplay fact.
