# Buildings — design brief

*Agreed 2026-08-08, after the quarter-grid unification shipped — and
SHIPPED the same day: sequencing steps 1–3 are in (foundation tool,
floor tool, kill list, enclosure-as-state, save schema v2, old saves
deleted). Roofs (step 4) remain. Once playtested, conclusions move into
`CLAUDE.md` and this file becomes history — same lifecycle as
`WallGraphRebuild.md` and `Stairs.md`. The doctrine already lives in
CLAUDE.md's "Buildings" section; this file keeps the why.*

## The claim

Buildings are authored bottom-up from four kinds of object —
**foundations, walls, floors, roofs** — and no plane is ever inferred.

The current room system computes floor and roof as *outputs* of
designation: the floor from whatever ground the ring happens to enclose,
the roof from a majority vote over whatever tops the walls happen to
have. Every structural patch of the last two weeks — the crown vote's
4m refusals, `CarriedToPlane`, the partition tell, the floor step — is
the cost of intuiting a plane the player already had in their head. That
information is not in the geometry and never will be; the player states
it instead. This is stored-never-inferred applied to the vertical axis,
finishing what the wall-graph rebuild did to the planar one.

The quarter-grid unification and the elevation readouts (2026-08-08) are
the substrate: planes can be dialed to exact rendezvous, and the HUD can
tell you what to match. They shipped first for a reason.

## The model

**Foundation** — square tiles, placed one at a time. The ghost is a
1×1m square, sizable up to 5×5m (Ctrl+scroll, the tool-in-hand dial —
joins the wheel arbitration per-mode). Tiles snap edge-to-edge onto one
another and **adopt the plane of the tile they snap to**; a building's
foundation is one plane, laid tile by tile. The first tile of a patch
seats like a floor slab does today: top at the highest ground under it,
stepped up to the quarter grid, skirted down below the lowest ground so
dips stay closed. **The one refusal: a tile whose top is MOSTLY clipping
through terrain** (ground stands above the tile's top over more than
half its area) — the burial gate, tile-local and self-scaling, the
`GroundAbove` doctrine transferred from rooms to the object that now
owns the level. Dips never refuse; the tile spans them. The ground tools
remain the only shovel.

The foundation's job: it sets the building's footprint and its level,
and it IS the first storey's floor. It is a build surface (walls stand
on it, free-form, like decks today).

**Walls** — unchanged as objects; the graph is untouched. What changes
is provenance: **a building wall is a wall standing on a slab**
(foundation or floor), replacing `roomBuilt`. Terrain walls survive
untouched as curtain walls — the wall tool on bare ground works exactly
as it always has, terrain-stepped footings and all. Consequence worth
naming: building walls stand on a FLAT plane, so their sections all
bottom at the slab top by construction — the footing-stepping
complexity (skirts, `StackFit`'s burial math) remains only where terrain
actually exists, under curtain walls.

**Floor** (between storeys) — the same tile gesture with a different
legality: a floor tile must connect to **the top of a wall, or to
another floor tile**. Seeded at a wall top (adopting that top's plane —
the quarter lattice is what makes the rendezvous exact), then grown
tile by tile off itself. This is the marriage mechanism in its final
form: a floor spanning from building A's wall tops across to building
B's is placed, not computed, and its plane is wherever the player seeded
it. A floor is a build surface for the next storey's walls.
**Stairwells become omission**: don't tile over the stair, and the well
exists — no polygon cutting, no stored well list on the slab.

**Roof** — a separate object, mostly decoration, designed later. It
adds no structural meaning; a building is weatherproof in gameplay terms
when its top storey is floored (or roofed — the rulebook decides later,
not the geometry).

**Rooms** — enclosure remains a checked fact (a face of building walls
on a slab, traced as today) but **adds no geometry**. "Roomness" is
derived gameplay state for the economy and furnishing phases; the slab
under it and the slab over it are the player's.

## Deferred rules, deliberately

Support comes later, as simple stated rules — a floor tile run must
connect walls of equal height; no span more than ~20m without a wall
under it. Same doctrine as the graph rebuild: build without rules first.
Nothing in the tile model has to be unwound when they land — tiles
already know what they connect to, because connection is how they were
legal to place at all.

## Kill list

- `WallRoom.BuildFloor` / `TryBuildRoof` / `CarriedToPlane` /
  `RiderReaches` — the whole derivation machinery.
- The crown vote, `roofNote`, `RetryRoofless` / `NotifyCourseLaid`.
- The partition tell (`overbuildRoom`) and the floor step
  (`stepRoom`/`stepRoofEnd`, the two-span drag) — with explicit floors,
  drawing on a floor is just drawing on a build surface. The
  never-cross-INTO-a-building T-landing survives as wall-vs-wall law.
- Designate as a geometry-producing act. The designate *gesture* may
  survive as the enclosure check's trigger — open question below.
- `RaiseSills` in its room-driven form: a doorway in a building wall
  sills at the slab top by construction.

## Save

Schema resets: foundation tiles, floor tiles, and (later) roofs join
`CastleSave` as new typed lists in the same commit they exist. **Old
saves are deleted, not migrated** (user's call, 2026-08-08 — "keep it
simple"). `Saves/castle.json` is removed when the new schema lands.

## Open questions (settle before code)

1. **Orientation.** Square tiles vs free-angle walls: a round tower's
   foundation out of axis-aligned squares is a staircase circle.
   Proposed: a tile snapped to another inherits its orientation; the
   first tile of a patch takes a dialable bearing (R, like the hover
   stub). Wedge/triangle tiles are a later luxury. Walls may slightly
   overhang slab edges; decide how much is legal.
2. **Split-level foundations.** Snapping adopts the plane — is there a
   way out (Alt, per the free-placement convention) to start a new
   plane adjacent to an old one, for terraced buildings? Proposed: yes,
   Alt breaks adoption; the two patches are then two buildings' worth
   of level, and a floor can later bridge their walls.
3. **The burial threshold.** "Mostly clipping" = ground above tile top
   over >50% of area, sampled on a grid. Tunable; start there.
4. **Tile size stepping.** 1–5m; proposed 0.5m increments to sit on the
   existing lattice.
5. **What arms the enclosure check** — automatic on every commit (like
   `RetryRoofless` was), or still a designate-like gesture that names a
   room for gameplay? Leaning automatic-detection, player-named later.

## Sequencing

1. Foundation tool: tiles, snapping, plane adoption, burial gate, build
   surface, save. Walls on foundations get flat bottoms.
2. Floor tool: same tiles, wall-top/self connection law, wells by
   omission, build surface, save. Stairs and doorways re-point at slabs.
3. Enclosure as pure state; kill list executed; old saves deleted.
4. Roofs, when decoration matters.
