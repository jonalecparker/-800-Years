# Doors — 2026-08-03

A doorway is **stored on the edge**, not as a rider. Built in one session
alongside landings and stair wells; this file records why it is shaped
this way, and moves into `CLAUDE.md` once it has survived a playtest or
two.

## The claim

A stair is a *rider* on the graph (`WallStair`, `WallRoom`) because it is
a separate thing standing next to the masonry. A doorway is the opposite:
it is **an absence in one wall's masonry and nothing else's**. So it
belongs to the same data the sections are derived from.

```csharp
public struct Opening { public float t; public float width; public float head; }
public List<Opening> openings;      // on WallEdge
```

That keeps the doctrine untouched: an edge's geometry still depends on
nothing but its own fields, and nothing rebuilds because a neighbour
changed. The alternative — a `WallDoor` rider — could only cut its hole
by making the wall ask what is standing near it, which is exactly the
inference this graph exists to avoid.

`t` is the **centre**, in curve parameter, because that survives
subdivision by a straight lerp (`SubCurve` is exact polar-form, so a
sub-curve's parameter maps to its parent's linearly). `width` and `head`
are metres, and a split doesn't change them because it doesn't change the
geometry.

## A lintel is a section with a raised bottom

`SectionSpec` already carries an independent `bottomY` and `topY`. So the
whole of a doorway's geometry is:

- cut the section boundaries at the opening's two ends (`WallEdge.Runs`
  yields solid runs and openings in arc order);
- emit the opening as **one** section whose `bottomY` is the ground plus
  the head, and whose `topY` still comes from the wall.

**No new mesh code at all.** The jambs are the neighbouring sections' end
caps, which every section already has. The masonry over the door lines up
with the masonry beside it because it takes its top from the same place.
A head taller than the wall leaves nothing to build and `BuildSpecFrame`
drops it — a full-height gateway, not an error.

This is the same trick the stair used on the same machinery: a stair is
the section sweep with tops that step, a doorway is the section sweep
with one bottom raised.

## What surgery has to carry

`WallEdge.CarryOpenings(from, to, t0, t1)` is called at **both** places
in `WallGraph` that make sub-edges — `DeleteSections` and
`SplitEdgeAtMany` — right beside the existing `sub.roomBuilt =
edge.roomBuilt`. An opening survives only if it fits **whole** inside the
piece; one the split runs through is dropped, because half a doorway
either side of a new node is not what anyone drew and the masonry there
is being re-formed anyway.

Verified: a tee landing at x=9 on a 12m wall carries the doorway at x=6
onto the correct sub-edge with `t` remapped 0.500 → 0.667 and the world
position still exactly 6.00; a tee landing *on* the doorway drops it and
the wall closes.

## The tool

Same shape as the stair tool, deliberately: **no gesture.** Point at a
wall's side face and the opening is already there; the wall answers
everything except where along it you want the hole. Which face you
approach from doesn't matter — a doorway goes all the way through.

The ghost is a box standing where the masonry would go, because the
opening is a subtraction and the honest ghost is the volume being
removed. Red with a stated reason when refused. Three refusals, all
spoken to the BuildLog rather than silently swallowed:

- the wall is shorter than `width + 2·jamb`;
- the doorway is too close to an end (`DoorJamb` 0.3m must remain);
- it would run into a doorway already there.

`width` 1.1m and `head` 2.1m are fixed for the same reason the stair's
width is: dialling them would need a fifth meaning for the scroll wheel,
which has to join `ClaimingScrollWheel` arbitration.

**Deleting one:** the Delete tool, clicking the **lintel** — the masonry
over the doorway. That removes the opening and closes the wall, rather
than deleting the lintel as a section, which would leave a hole exactly
where a hole already is. `WallEdgeSection.openingIndex` is what lets the
section know it is a lintel.

## The sill: doorways serve a floor, not the ground (2026-08-03)

From the first playtest: doors cut in a big room's downhill wall were
filled by the room's own floor slab, with a sliver of light at the top.

`WallRoom.BuildFloor` sets `floorY` to the base step **at or above the
highest** interior ground, so terrain never pokes through the slab. Across
a large room on a slope that puts the floor well above the downhill wall's
footing, and a doorway cut from *that* ground opens into the side of the
slab. (The floor is also built from `outline` — the ring **centerline**,
not inset like the roof deck — so the slab's face sits halfway into the
doorway's depth, which is what made it look like a wall rather than a
step.)

**The geometry is unavoidable and worth stating.** A level floor over
sloping ground has a step on one side no matter what. Excavating to the
lowest ground just moves it: the downhill doors start working and the
uphill ones become a ledge. `max` already picks a side — it makes the
**uphill** doors work at grade. (It has a second reason too: footings
quantize *down* and `floorY` quantizes *up*, so `floorY ≥ every wall's
footing`, which is what stops daylight appearing under the uphill walls.
Lowering the floor would mean skirting those walls down to meet it.)

Decision (user's, 2026-08-03): **keep the level floor and the uphill
entrance, and make the doors honest.**

- `Opening.sill` is an absolute world Y (`-Infinity` = the wall's own
  footing). `WallRoom.FloorServing(edge)` gives the highest room floor a
  wall bounds — highest, because a wall between two rooms must clear both,
  and a doorway *into* a floor is worse than a step down onto one.
- The masonry below the sill becomes a **threshold block**: the lintel
  trick upside down, a section with its top lowered. Clamped to the wall's
  own top, because a floor can stand above a wall entirely on a steep
  slope — then the threshold fills the opening and the doorway is visibly
  **walled up**, which is the honest answer where a hole into the middle
  of a slab is not.
- Set at **two moments**, like the stair wells: when the doorway is cut
  (`WallRoom.FloorServing`) and when a floor arrives over an existing one
  (`WallRoom.RaiseSills`, called from `Create` after `BuildFloor`).
  Deliberately one-way — deleting the room does not drop sills back. A
  threshold left standing is visible and removable (fill the doorway in
  and re-cut it); a sill silently sinking into a slab would not be.
- The tool **says the step**: the HUD context and the BuildLog both report
  how far the sill stands above the ground outside.

## Steps up to a raised doorway

The companion to the decision above: without it the downhill door is an
honest opening you still cannot reach.

**Point the stair tool at a doorway's own masonry** — threshold or lintel
— and the run climbs to that doorway's **sill** instead of the wall top,
and *arrives* there rather than setting off from there.

That reversal is the only new idea. The walk always starts at the cursor
and runs outward, which puts the cursor at the stair's bottom — right for
climbing a wall, wrong for arriving at a door. `WallStair.ReverseSpans`
flips the chain, which costs nothing geometrically (a quadratic's control
point is unchanged by swapping its ends) and does **not** move the run:
the side offset was baked in during the walk, so only which end is high
changes. Two details fall out:

- The walk starts at the doorway's far **edge**, not its centre, or the
  landing — which is the platform outside the door — would leave half the
  opening hanging over the drop.
- The landing keeps its full length and any shortfall falls on the climb
  (already flagged blocked), because at a door the landing is the part
  that matters: `runArc = min(climb, got − LandingRun)`.

A sill lower than `DoorStepsNeeded` (three risers, 0.66m) is **not** a
stair target. Footings quantize down and floors quantize up, so nearly
every doorway has half a base step under it even on level ground; offering
a flight for that would be noise. It stays a doorstep.

Verified on a 20m room over a 2m slope: floor 9.50, downhill footing 7.00,
sill lifted to 9.50 with a 7.50–9.50 threshold block under it; the opening
is clear at sill height and solid below; the stair reports 9 steps, 0.250m
going, 2.50m landing, arrives with its nearest tread topping at exactly
9.50, and the doorway is clear from the landing.

## The section grid belongs to the wall, not to the doorway (2026-08-04)

From the second playtest: interior walls with doors cut into them didn't
reach their full height — they read as sunk.

Two separate causes, both in `WallEdge.BuildSpecs`, and both the same
mistake — **letting the doorway decide something about the wall.**

**A doorway used to re-divide the whole wall.** `Runs` split the arc into
solid runs and each run was then shared out evenly into sections. So
cutting a hole anywhere moved *every* boundary on the wall — and a
section's footing is `SampleSliceGround`: the LOWEST ground under its
footprint, snapped down to the base step. Longer footprint, lower
footing. A 4m partition divides into 4 sections of 1m; put a 1.1m door
through the middle and each side becomes ONE section of 1.45m, 45%
longer, which reliably drops half a base step. Half the wall sank, and
its top with it. Long curtain walls barely showed it (1m → 1.09m); short
interior walls always did, which is why they were where it was noticed.

The fix is to settle the grid **before** any opening is cut into it: cell
count and per-cell footing come from the edge's own arc and the terrain
under it, and a doorway then cuts INTO that grid. A cell the doorway
clips keeps its own footing, so the jamb beside a door stands exactly
where it stood before the door. Off-cuts shorter than half a cell merge
into the neighbour rather than becoming slivers — `BuildSpecFrame` drops
a degenerate section, and a dropped section is a gap.

Verified: on a 12m, a 5m and a 4m wall across a slope, **0 of 48 sampled
points of solid masonry moved** when the doorway went in (before: two
sections dropped 0.5m on the 12m wall, and 0.5m of the 4m wall dropped).
A wall with no openings comes out byte-identical to before the change —
the cell count is the same expression the single run used to get.

**A doorway that spans a step took one figure for two jobs.** `ground`
fed both the threshold's bottom and `wallTop = ground + height`. Where a
doorway straddles a step in the footing that number is the LOWER one, so
the masonry over the door sat half a step below the masonry beside it —
a notch, every time, on the high side. It now reaches **down to the
lowest cell it covers and up to the highest**: the threshold has to meet
the ground, the lintel has to meet the wall. Buried stone costs nothing;
a notch in the wall top does — the same trade `skirt` and `StackFit`
already make. (The head still measures from the low side, so the 2.1m
clearance holds across the doorway's full width.)

The general rule, and it outlives doors: **a section's footing is a
property of the wall's grid, not of where a section boundary happens to
fall.** Windows and arrow slits will cut into the same grid.

## Two places a raised bottom lied

Both found by the same question — "who reads `section.bottomY` and means
*the ground*?" — and both worth remembering, because the next feature that
raises a section's bottom will hit them again.

- **`WallRoom.BuildFloor`** takes `max(bottomY)` over the boundary to
  decide whether the room's walls stand on a deck. A lintel's bottom is
  2.1m up, so a single doorway in a boundary wall made the room conclude
  it was an upper storey and give itself no floor. It now skips lintels.
- **`MasonryFloorAt`** (`GridPlacementSystem`) — a stair anchored on a
  lintel would begin in mid-air, and the door ghost would float. Both ask
  the nearest solid section on the same wall instead.

## Deferred

- **A door that opens.** This is a hole, not a leaf, hinge, or state.
- **Windows.** The same `Opening` with a sill as well as a head — the
  struct wants one more float and `Runs` wants a second raised-bottom
  case for the masonry *below* the sill.
- **Arched heads.** The lintel's bottom is flat because a section's
  bottom is flat.
- **Mural stairs** — the same subtraction with a sloped head, and the
  reason `Docs/Stairs.md` now lists them as closer than they were.
- **Doorways at nodes.** An opening lives strictly inside one edge, so a
  gateway through a corner is not expressible. Probably right.
