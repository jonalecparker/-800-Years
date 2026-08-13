# Castle Territories (2026-08-12)

The survey's successor design (agreed 08-11/12): the countryside belongs
to LORDSHIPS, not to distance rings around the map's center. On the
Marches the center is empty countryside halfway between the castles —
the old `MaxSurveyRadius` 1500m survey put the player's demesne in the
middle of nowhere. This brief replaces the rings with castle sites and
territories. User calls (08-12): **Grosmont is the player's home**, and
**White Castle joins** — the historical Three Castles complete.

## The model

- **`CastleSite` is a scene component on the marker objects** — name
  from the GameObject, position from the transform, `isPlayerHome` set
  in the scene (Grosmont). Sites are scene facts like the terrain, NOT
  save facts: the save references them by index into the name-sorted
  site list (`CastleSite.Ordered`), the same way it references terrain
  by `Ground.BaseName`. A scene with no sites (Spiš) falls back to one
  synthetic player-home site at the map center — the old behavior,
  minus neighbors.
- **The territory partition is nearest-site by XZ** (`TerritoryOf`).
  Every point on the map belongs to some lordship; the seam is the
  Voronoi boundary. With the Three Castles 5–7km apart the partition is
  generous, which is honest: a 1220 lordship claimed far more than it
  worked.
- **Parcels exist only where land is WORKED**: within `SurveyReach`
  (1000m) of a parcel's own site. The lattice is still one global
  jittered grid (gx/gz keep meaning map-wide; adjacency never lies),
  but only cells near a castle materialize as `LandPlot`s — ~900 per
  site instead of 62,000 map-wide. The deep country between lordships
  has no parcels at all; it is claimed, unworked, and lawless.
- **Classification is per-territory**: relief quantiles computed over
  each territory's own cells, so Skenfrith's riverside meadows and
  White Castle's hills each classify against their own country, not
  against each other's.
- **Every castle has a settled demesne, and the village is ONE
  CONTIGUOUS TOWN by the castle** (the user's one-town-per-manor
  verdict): the seed is the parcel under the castle itself, and the
  settled cluster GROWS by grid adjacency — always annexing the
  flattest parcel touching the cluster — village plots first, then
  fields, then pasture. Scattered flattest-first picks are dead; the
  town is a place you can point at.
- **Ownership follows the lordship**:
  - Player's territory: the settled demesne is yours; **everything
    else in your own territory starts bandit-held** — the lordship is
    yours on parchment, the waste is lawless until cleared plot by
    plot. This is the land ladder's first rung made literal ("the land
    around you is dead until cleared").
  - Neighbor territories: wholly `Neighbor`-owned, demesne and waste
    alike — their estates are their problem.
  - The old center-distance rings (`DemesneRadius`, `BanditRadius`) and
    `MaxSurveyRadius` are DELETED, not dormant.
- **The frontier rule now gates bandit clearing ONLY.** Buying a
  neighbor's parcel is negotiation, not conquest — it needs no shared
  border, and with territories 6.6km apart adjacency would make buying
  impossible forever. The hover line and the click refusal change
  together (one-test doctrine): bandit parcels still warn "(beyond your
  borders)", neighbor parcels never do.
- **`LandPlot.territory` is a stored fact** (CastleSave v7). Pre-v7
  saves migrate ONCE at load — each plot takes its nearest site — and
  store it thereafter.

## The second pass (same day, after the user's hand-test)

- **Castle land vs village land** (the user's call): the settled
  cluster now grows CASTLE grounds first (`LandType.Castle`, one plot
  by default), then village, then fields, then pasture. **The
  castle-grounds law** (`LandLaw`): the player builds masonry on their
  own Castle parcels and NOWHERE else — wired into the wall family's
  live blocked flags (`previewBlocked`/`shapeBlocked`/`roomLiveBlocked`
  — red ghost and commit gate are one test) and into
  `SlabPolyRefusal` ("outside your castle grounds."). Stairs and
  doorways ride existing lawful walls and are deliberately unchecked;
  the Ground tool stays free. **The law STANDS DOWN when the player
  owns no Castle parcel** — old saves predate castle land and must
  keep building. The future procedural village keeps out of Castle
  parcels by the same stored type. Castle grounds yield nothing, count
  as no village for household dues, and a neighbor's castle parcel is
  never for sale — "changes hands with the castle" (hover + click, one
  test).
- **The survey dials moved to the inspector** (`LandSurvey`, "Land
  Survey Settings" object in the Marches scene): cellSize, surveyReach,
  and the four settled-plot counts. Generation-time only; the Resurvey
  context menu (play mode) re-runs the survey with the dials as they
  stand — and RESETS ownership, a design-tuning tool. **surveyReach 0
  means survey EVERYTHING**: every cell on the map parceled, one
  lord's land butting the next at the Voronoi seam (the user's
  assumed model — verified live: 200m cells / full map = 5,625 plots,
  lordships adjacent parcel-to-parcel). The `MaxPlots` guard (25,000)
  refuses GameObject soup with advice instead of a freeze; 60m cells
  over the full Marches would be ~62k. Open dial questions: cell size
  couples to demesne income (settled plot COUNTS are fixed, so bigger
  cells mean bigger settled acreage and more produce) and to castle
  size (one 200m castle plot is a vast bailey).
- The survey hover raycast is unbounded now — the 1000m cap silently
  dropped every far parcel read from altitude.

## The third pass (2026-08-12 evening): the world agrees with the map

- **Scene dials settled at 200m cells / full coverage** (the user:
  "200m feels better", and the assumed model was always
  lords-land-butting). Known consequence, flagged: settled plot COUNTS
  are fixed, so 200m cells inflate demesne acreage and income ~11×
  against the £18/yr calibration — rates need a rebalance pass once
  the cell size stops moving.
- **Roads are the roads of 1220** (`CastleRoads` + `castleRoadsOnly`
  on MarchesDressing, default on): the modern OSM density is filtered
  to the pairwise shortest ROUTES between castle sites over the
  stitched road graph (vertices merged at 2.5m; Dijkstra; unreachable
  pairs log and drop). Kept routes paint as through-roads whatever
  class their pieces were; splats, hedgerows and tree keep-outs all
  read the filtered set, so they agree by construction. The geometry
  stays real — rural lanes follow their medieval predecessors; it's
  the density that was the anachronism.
- **Timber parcels grow real oaks** (`TimberWoods.Plant`, runtime, at
  survey/load): deterministic jittered-grid planting inside every
  Timber parcel, skipping parcels that are already real woodland
  (density test against the dressing's OSM trees), points near the
  castle routes, and waterway banks. Spacing is a LandSurvey dial
  (`timberTreeSpacing`, 12m default; <2 disables); 250k planted cap
  thins by hash with a warning. Writes go through
  **`TerrainPads.SessionData`** — the session-clone door generalized
  from earthworks, because play-mode writes to the authored asset
  silently persist. Replants truncate each tile back to its
  dressing-time instance count first, so Resurvey/undo never stack
  trees on trees.
- **Parcel boundaries follow the roads and rivers** (the user's pick
  of the two candidates, same day): `SnapCornersToFeatures` — a
  lattice corner within 0.38 cells of a feature line (castle routes +
  waterways, via `LandFeatures`, the one runtime door to the feature
  polylines — the timber keep-out reads the same door) moves to the
  nearest point ON the line, replacing its jitter, so a field ends at
  the lane and the brook instead of straddling them. A snap is
  accepted only within 0.45 cells of the corner's grid SEAT: jitter
  plus snap could otherwise displace a corner past half a cell, and
  two corners walking more than half a cell toward each other can swap
  order and cross the quad. Edges between snapped corners are straight
  CHORDS of the feature — a tight meander still cuts the corner,
  accepted at survey scale. Verified: ~18% of corners sit exactly on a
  feature at 200m cells, zero self-intersecting outlines. Segment
  spatial hash keeps generation O(corners); a segment spanning >50
  bins is skipped as bad data rather than hanging the survey.
- **River-weighted territory seams** (crossing the Monnow costs extra
  "distance", so lordship borders settle onto the river — historically
  the Monnow WAS the boundary) remain on the table, not built.
- Hazard log, hard-earned: a mid-play script recompile domain-reloads
  the play session, and `LandPlot.verts` (readonly, unserialized)
  comes back EMPTY — every plot a husk. Play-session state does not
  survive recompiles; regenerate, don't debug the husks.

## The fourth pass (2026-08-12, castles): grounds, gates, neighbors

The user's redirection after walking the map: the castle area should
not be a survey cell — "centered around the castle site, its limits
determined by the terrain around it"; the village should "lie on the
road before the castle"; the road should stop running through the
middle of the building area; and the computer's castles need buildings
(scenery prefabs from the real plans — the user's pick over real
wall-graph builds or a procedural generator).

- **`CastleGrounds` shapes the castle parcel from the terrain**: 36
  radial rays march out from the site (4m steps) and stop where the
  ground rises or falls more than `castleGroundsRelief` (6m) from the
  site's own ground — the defensible platform's edge — clamped to
  [`castleGroundsMin` 60m, `castleGroundsMax` 140m], two smoothing
  passes so the boundary reads as a ward line, not ray spikes. All
  three dials live on LandSurvey. The polygon IS the castle parcel
  (`LandPlot` with 36 verts — the save already stores arbitrary
  outlines, no version bump); lattice cells whose centers fall inside
  stand aside, and nearby corners snap to the boundary through
  `SnapCornersToFeatures` — the castle edge is just another feature
  line. Castle size is decoupled from cell size (the "one 200m cell is
  a vast bailey" flag closes). Grosmont surveyed at 31,082m², the
  neighbors 61k/51k — genuinely different shapes per hill. The
  `castlePlots` dial is deleted. Edit-mode note: `LandSurvey.Instance`
  only registers in play mode, so CastleGrounds falls back to
  `FindAnyObjectByType` — the bake and the runtime read the SAME
  dials or paint and parcels would disagree.
- **The road ends at the gate**: `CastleRoads.Routes` takes the
  grounds polygons and trims each route end back to the LAST point
  inside its own destination's polygon, bisecting to the exact
  boundary crossing — the gate. Scanning for the last inside point
  (not the first outside one) matters: the nearest road vertex to a
  castle can sit BEYOND the grounds, so the route clips through the
  polygon and overshoots — a naive leading-run trim saw an
  already-outside end and left the clip (caught live at Grosmont,
  both approaches). Verified after the fix: 0 of 489 route points
  inside any grounds; all six route ends within ~10m of their
  boundary. Mid-run pass-throughs near a THIRD castle are
  deliberately left alone.
- **The village lies on the road before the gate**: `SettleTerritory`
  claims the `villagePlots` cells each trimmed approach passes
  through (`CellsAlong` — samples at cell/5, cells by seat coords),
  walking outward from the gate, round-robin across approaches so two
  roads share the town. Fields and pasture still grow flattest-first
  by grid adjacency around the settled core (the castle's seat cell
  anchors contiguity). A territory no road reaches (Spiš) falls back
  to flatness for the village too. Verified: all six village plots
  sit 7–64m from their road.
- **Neighbor castles are SCENERY, not graph** (the user's pick):
  graybox massings under the "Neighbor Castles" scene root, built
  from the real surviving plans — Skenfrith (Hubert de Burgh, begun
  c.1219 — contemporary!): quadrilateral curtain ~60×45m, four round
  corner towers, central round keep; White Castle: pear-shaped inner
  ward, twin-towered gatehouse, four flanking towers. Primitives with
  colliders (walker-solid), outside `splineParent` (the cutaway has
  no business slicing them), in no registry and no save. Untouchable
  by player tools by construction. When siege/conquest arrives, decide
  then whether they become real walls (which needs an owner field on
  edges). The marker pylons at the two built sites have their
  renderers disabled — components and positions stay.
- Grosmont builds nothing yet — the player builds the home castle.

## Bandits at the seams — the part that waits

The seam (Voronoi boundary between lordships) is where bandit CAMPS
belong when combat exists — deep country no lord works, cheap to claim
in coin and costly in blood. v1 ships no camps and no seam geometry:
`TerritoryOf` is the partition fact, and anything seam-shaped derives
from it when there is a system to consume it. Don't build seam
machinery ahead of its consumer.

## Georeferencing (verified 08-12)

The Marches world map is linear in **Web Mercator** (the DEM was
mosaicked from terrarium z13 tiles): fitting mercator→world through the
two verified markers gives kx = kz = 0.6172 = cos(51.89°) to seven
decimals. White Castle (51.8459, −2.9021 — OS SO 379 167) lands at
world **(−4640.3, −6428.2)**. Site markers seat themselves on the
ground at play (`Ground.HeightAt`) — the editor Y is approximate, the
runtime Y is the terrain's.

1220 note, recorded for flavor: the Three Castles were historically ONE
lordship under Hubert de Burgh. The game wants them as neighbors;
the names are there if the story ever wants the truth.
