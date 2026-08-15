# Village Houses — procedural town dressing (2026-08-13)

The user's ask: houses whose point is DECORATION — "something to make
the town feel real" — plus a church and an inn eventually, with
POPULATION driving how much town exists and changing it over time.
Growth art comes later, so changes are instantaneous for now, but the
architecture is chosen so growth slots in without a rewrite.

## The core idea: a stable LOT PLAN, materialized up to population

`VillageHouses` derives, for each town, a DETERMINISTIC ORDERED list
of building lots from stored facts alone (Living parcels + the castle
routes + the terrain), and population decides how many lots get
buildings. Houses are a PREFIX of the plan:

- Household #13 appears on the lot the plan always reserved for it;
  nothing already built moves or reshuffles. That is the growth
  contract, and it falls out of determinism + ordering, not out of
  stored state.
- Swapping graybox boxes for real art later touches ONLY the
  materializer (`House`/`Church`/`Inn`); the plan never changes.
- Growth animation later = animating the materialize step for newly
  added lots; the plan and the already-built prefix are untouched.

## Doctrine

- **Houses are SCENERY derived from stored facts** — the TimberWoods
  precedent, not the wall-graph one: deterministic regeneration from
  (Living parcels, `Estate.Households`), joins no registry, saves
  nothing (CastleSave already carries both inputs), root "Villages"
  sits OUTSIDE `splineParent` (the cutaway has no business slicing a
  town), colliders ON so walk mode is real, and no player tool can
  touch them by construction (LandLaw keeps masonry on Castle parcels
  anyway).
- **`VillageHouses.EnsureCurrent()` runs every frame from
  BuildMenu.Update** beside `LandParcels.EnsureGenerated` — a two-int
  signature check (plot count, households); a change rebuilds
  wholesale. Load, Resurvey, snapshot-undo and future population
  growth all flow through the same door for free.
- **Lots line the STREETS, two rows deep, ordered by distance from
  the gate plus a back-row penalty** — the town grows outward from
  the castle like the real ones; both sides, burgage-style: narrow
  frontage to the street, long axis running back, gable end facing
  the road, and a second row a lane behind. The streets are the
  castle routes PLUS back lanes (`OffsetPolyline` copies at
  `LanePitch` 58m — an honest burgage-plot depth — two each side),
  so the village-flood blob (LandParcels, 2026-08-14) fills with
  town instead of tracing a line; lanes exist only where their lots
  land inside the Living polygon. Fill order is castle distance +
  `BackRowPenalty` (60m) for row two — the user's rule: the second
  house from the road beside the castle fills before the tenth house
  down the lane.
- **No lot within `RiverClear` (10m) of a waterway's edge** (feature
  half-width + margin — the TimberWoods keep-out at house scale);
  houses were standing in the carved channel without it.
- **No lot inside or within `CastleClear` (8m) of a Castle parcel** —
  the grounds are the lord's, not the town's. Lanes (sideways offsets
  of routes trimmed only on the centerline) and the frontage
  tolerance both leaked houses across the gate before this check.
- **Primary-street front rows tolerate the outline wiggle**
  (`EdgeTolerance` 14m): the traced Living outline is simplified
  raster steps, so lots near the boundary wobble in and out of
  strict containment (measured live: a run of street-front lots out
  by 0.2–12.9m). A front-row lot on a castle route within 14m of the
  Living-parcel edge counts as frontage; back rows and lanes stay
  strictly inside — only the true street front earns the slack.
- **Each house seats itself**: floor at the highest corner of its
  footprint, a plinth skirting 2.6m down into the slope — the
  foundation-slab-skirt idea without slabs. Lots steeper than the
  plinth can bury are SKIPPED at plan time. Real `TerrainPads.Flatten`
  terraces (inside the ±3m law) stay available for later, when houses
  are art and deserve real pads.
- **The church is unconditional** (every 1220 village has one): the
  most prominent (highest) of the first few lots. **The inn arrives at
  `InnThreshold` households** — it exists at the starting dozen, and
  it exercises the population-reactive path from day one.
- **Neighbor towns get a fixed `NeighborHouseholds`** (12) — they are
  scenery of scenery; their populations become real numbers when AI
  lordships do.

## Deferred, deliberately

- Growth ART (construction states, scaffolding) — the plan/prefix
  contract above is the hook.
- Real house models; everything is primitive boxes + a 45°-rotated
  cube for the gable roof.
- Terrain terracing per house (Flatten ops), back lanes off the main
  street, greens/market squares, Spiš (no roads → no lots → no town).
- Households affecting anything but count (wealth → house size, etc.).
