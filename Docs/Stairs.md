# Stairs — design brief

*Agreed 2026-08-02, the remaining half of the ⚠ storeys/stairs design call
(the storeys half shipped the same day). This is the brief; when it has
shipped and settled, its conclusions move into `CLAUDE.md` and this file
becomes history — same lifecycle as `WallGraphRebuild.md`.*

## The claim

A stair is the **first stored vertical relationship** in this model.

Everything else in the graph is planar and lives at exactly one `baseY` —
that is why `WallGraph.SameBase` can filter every query with a single
float. A stair belongs to two storeys at once. That is not a wall with a
slope; it is a different kind of fact, and it gets its own object.

This does not front-run the deferred **structural support** call. Support
is "what rests on what" — spatial, inferrable, correctly deferred. A
stair is "you can get from here to there" — authored, because the player
drew it. Storing it costs nothing that has to be unwound when support
lands, and walk-mode (phase 4) plus attacker pathing (phase 5) both need
to *read* circulation as stored structure rather than re-derive it from
geometry.

## What was rejected, and why

**A `WallEdge` variant** (an edge with a stepped top and two bases) is
tempting because the geometry is right. It dies on the planar graph: a
stair leaning against a curtain wall must not split that wall, must not
weld to its nodes, must not appear in `FindCrossings`. So it would need
an `isStair` flag that every query filters out — at which point it is not
a `WallEdge`, it is a different object borrowing the fields, plus exactly
the infer-from-a-marker pattern the graph rebuild deleted.

**A room** (a designated face with a ramped slab) cannot express an
external stair at all — no enclosure, and a forestair up a curtain wall
is the most common stair in a castle. A ring is also same-base by
construction, so it cannot span two storeys. Designation *is* the right
long-term home for auto-filled stairwells (phase 4 furnishing: designate
a face "stairwell", the rulebook drops a newel stair in), but that is
downstream of this, not instead of it.

## The model

`WallStair` — a rider on the graph, the way `WallRoom` is. Its own `All`
registry, registered in `OnEnable`, **not a member of any `WallNode`**,
invisible to every planar query. It never splits a wall and no wall
splits it.

```
spans         List<Span>  the run as a CHAIN of quadratics, each carrying its
                          cumulative arcStart/arcEnd along the whole stair
bottomY       float       elevation the first riser starts from
topY          float       elevation of the landing
baseFrom      float       the storey it stands on (-inf = terrain); the wedge never digs below it
width         float
```

**A chain, not one curve** (2026-08-02, from playtest). A stair confined
to a single `WallEdge` pins itself to whichever node it started from: a
circle is eight edges and a wall with tees in it is several, so the run
clamps inside the edge under the cursor and the only thing left free to
move is its length. It follows the masonry **through its nodes** instead,
taking the straightest continuation on the same storey — the same walk
the wall tool's ride already does, for the same reason
(`StraightestContinuation` is shared).

Treads are cut over the **total** climb and then clipped into whichever
spans they land in, so a tread straddling a joint becomes two meshes at
one height and the risers stay exactly equal all the way round. That is
the same answer the graph gives when an edge crosses a node.

**Geometry is baked at commit; the host wall is not stored.** The wall
you pointed at is what the *cursor resolver* used to compute those
numbers, nothing more — exactly as a wall snapped to another wall's flank
stores its own node position, not "I am snapped to that". So a stair
never rebuilds because a neighbour changed, and deleting the wall it
leans on leaves it standing in the air — consistent with an upper storey
over a deleted wall, and with support being deferred.

Steps are **derived, never stored** — same doctrine as sections, node
posts, and room slabs.

## Geometry: one section per step

`WallEdge.BuildSpecs` already picks a section count so every section comes
out the *same* arc length. That is exactly what treads are. So a stair is
the wall sweep with two changes:

- the count comes from the **step count** rather than a target length;
- section *i* gets `topY = bottomY + (i+1) × rise`.

`WallEdge.BuildSectionMesh` needs **no change at all**. Every section
already caps both ends, so at a step boundary the taller section's start
cap *is* the riser, and the coincident portion below it is two caps
facing opposite ways — which the project already treats as fine. The
result is watertight for the usual reason (neighbours evaluate the shared
curve at the same boundary parameter) and its masonry courses line up
with the walls' (the same world-height V anchoring).

Two `WallEdge` statics open up to make this possible — `SampleSliceGround`
and a new `BuildSpecFrame` (the frame/mesh half of the old private
`FinishSpec`, which now calls it). New identity, borrowed geometry.

**Treads**: `TargetRise` 0.22m, `TargetGoing` 0.25m — about 41°, a real
castle stair. Count = `round(rise / TargetRise)`, and the actual rise is
`rise / count`, so every step is exactly equal. Run = `count × TargetGoing`.

**The wedge is solid.** Each step's bottom is the lowest ground under its
slice (`SampleSliceGround`, so it steps like a wall does), floored at
`baseFrom`, and clamped to at most one riser below its own top so a
rising slope can never invert a step. Buried stone costs nothing;
daylight does — the same reasoning as `WallEdge.skirt`.

## The gesture

**Point at a wall's side face. The ghost appears immediately.** No drag.

The wall you point at answers every question:

| question | answer |
|---|---|
| where it starts | the bottom of the section under the cursor (`sec.bottomY`) |
| where it lands | the top of that same section (`sec.topY`) |
| how long it is | derived from that rise — never a player choice |
| which face | whichever side the cursor is on (`WallEdge.SideOf`) |
| what curve | the host's own sub-curve (`WallGraph.SubCurve`), offset sideways |
| which storey | the host's `baseY` |

Because the length is *derived*, **there is no pitch refusal**. A stair
cannot be too steep or too shallow — the only thing the player chooses is
where and which way. The pitch refusal first proposed here was machinery
that existed only to police a freedom worth not granting.

The one refusal that survives is **not enough wall**: a climb needs its
run, and if the masonry stops first no amount of arithmetic conjures
more. The ghost still draws where it would go and runs red, and the HUD
says how much more wall it wants. That is a real constraint, not a
policed freedom.

**Offset spans must be welded at their corners.** Offsetting each span
independently does not leave them touching — at a turn the inner offsets
overlap and the outer ones gap, by roughly 2·offset·tan(half the turn),
which measured **0.84m at an octagon's corner** before it was fixed:
three treads' worth of jump. `WallStair.WeldOffsetJoints` meets them at
the miter, the same fix `WallRoom.InsetOutline` uses for the deck.

Welding then changes the length — an inside run shortens, an outside one
lengthens (3.09m vs 4.91m for the same 3.5m climb around that octagon).
Left alone the pitch drifts ±20%, which would quietly break the very
reason this tool needs no refusal. So the sizing loop **corrects for it**,
rescaling the walk until the finished run matches the climb. It settles
in 1–4 passes (instantly on a level wall with no corners) and lands
within about a degree.

The loop settles the landing at the same time: the run is sized off the
wall under the cursor but must finish flush on the wall where it actually
ends, which may be a step higher or lower.

Pointing at a wall's **top** face does nothing (side hits only,
`normal.y < 0.7`) — that is the ride gesture's territory.

Where the cursor sits along the wall is the stair's **bottom**; it climbs
away. The span slides inward to fit when the wall is long enough, and
overhangs honestly when it is not.

**Climb direction is the camera's, and R flips it.** Project the camera's
forward onto the wall's tangent: the stair climbs the way you are
looking, away from you. R reverses it — and the moment R is pressed, the
camera stops driving that stair, or orbiting would silently undo the
player. Moving to a different wall re-arms the camera. R already means
"turn the thing you are pointing at" in the straight-build hover, so this
is an existing habit, not a new key.

If in play the cursor-picks-the-face rule turns out to mean constantly
flying to a wall's far side, R grows into a four-way cycle (near/far face
× each direction). Start with the flip.

**The stair does not ride.** `TryGetBuildSurface` applies `RideOnto`
unconditionally on a top-face hit, locking a run to the host's centerline
and curve. A stair meeting a wall-walk runs *across* the wall; the ride
lock would swing it to run *along* it. The stair tool uses its own
resolver and never rides.

## The cutaway (`CutawayView`)

Building *inside* a roofed room is impossible without one: the deck is in
the way of the eye **and** is the first thing the cursor ray hits. So the
cutaway is a horizontal plane through the world — masonry crossing it is
**sliced**, masonry entirely above it is hidden and un-collidable.

**Slicing, not hiding** (2026-08-03, from playtest). The first version was
all-or-nothing per object, and that is wrong twice over. Every object
vanishes at whatever height its own bottom sits at, so a room loses its
roof, then its walls, then its corner posts, in three unrelated steps —
the posts last, because a node wedge starts 2cm below the walls it joins
(`min(a.bottomY, b.bottomY) - 0.02f`). And the one view actually worth
having — walls cut to knee height so you can see and work inside the room
— exists at no level at all. One plane cuts everything at the same
elevation because it *is* the same elevation.

The slice is a **transform scale, not a mesh rebuild**: every mesh here is
a vertical prism (wall sections, node wedges, stair treads and room slabs
all sweep a flat top and a flat bottom), so scaling Y about the object's
own bottom walks the top face down and leaves everything else exact. View
state stays out of geometry — no `WallEdge`, `WallNode` or `WallStair`
code knows this exists, and nothing rebuilds because the cut moved.

The collider rides along for free, since it scales with the transform.
That is what makes building inside a cut room possible at all: the near
wall and the roof are the first things the cursor ray hits, and pointing
at something you cannot see is worse than not being able to build there.
**What you can see is what you can hit.**

Two consequences worth knowing:

- A sliced wall's exposed cross-section has an upward normal exactly like
  a real wall top, so the three resolvers that read `hit.normal.y > 0.7f`
  (`TryGetBuildSurface`, `TryGetCurveTarget`, `TryGetStairHost`) ask
  `CutawayView.IsCutSurface` first. Without it, pointing at the cut face
  offers to stack a storey at the wall's *real* top, metres above the
  surface being pointed at.
- Vertical spans are derived from `CutawayPivot.authoredY` + live mesh
  bounds, never from `Renderer.bounds`. A renderer already sliced reports
  the *slice*; read that back and `WorldSpan` decides the cut has cleared
  the top of the world one step above wherever it currently sits, and
  switches itself off. `CutawayPivot` caches exactly one float, because
  the mesh cannot say where the transform started once the transform has
  moved — and building inside a cut room remeshes posts constantly.

The rule is a renderer's own bounds, so it knows nothing about walls,
stairs, slabs or posts and needs no upkeep when a new kind of object
appears. It walks only the committed `WallGraph` subtree, so placement
ghosts above the cut stay visible: you can always see what you are about
to build.

`PgUp` / `PgDn` step it by 0.5m, `Home` clears it. The first press starts
just under the tallest thing standing — the press that means "show me
under the roof" — and raising it clear of everything turns it off rather
than leaving it hovering above the roofline pretending to do something.
The readout is **centred at the top**: the BuildLog owns the left margin,
and a view control that covers the log is a worse trade than the one it
was solving.

**Deliberately not in `ModifierHints`.** That panel reports what the tool
*in hand* reads, and this is a view control like the camera. Its own
readout carries its keys instead.

Static and ticked from `GridPlacementSystem.Update`, so it needs no
component in the scene. One array allocation a frame over a few hundred
renderers — revisit alongside `FaceAt` if edge counts reach the hundreds.

## The landing (2026-08-03)

`WallStair.LandingRun` = 2.5m of the run carried on **level** at `topY`,
so arriving at the next floor doesn't mean arriving at a drop.

It is part of the same span chain, not a separate object, because it
follows the same masonry through the same nodes — a landing that stopped
at a corner would be exactly the bug the chain was built to fix.
`runArc` marks where the climb ends; everything past it is landing.

Two things fall out of that split, both good:

- **The pitch became exact.** The treads are now cut over `runArc`
  (a measured arc taken from a chain deliberately walked longer than the
  climb) instead of over the whole chain, so every going comes out at
  exactly `TargetGoing` and the landing absorbs the difference between
  what the sizing loop asked for and what the welded corners returned.
  Measured: 0.250m going on a straight wall and on a circle. The
  correction loop still runs — it has to reach far enough — but it no
  longer has to land *precisely*.
- **The landing is best-effort, the climb is required.** A wall long
  enough to climb is never refused for being too short to stand on at
  the top; it just gives a shorter landing. Only `climb` feeds
  `stairBlocked` and `stairShortfall`.

## The ceiling well ✅ (2026-08-03)

A stair through a room's ceiling needs a hole in it. `WallRoom.wells` is
a stored `List<Well>` of `(WallStair owner, List<Vector3> poly)`, cut at
**exactly two moments** and never re-derived:

```
NotifyStairBuilt(stair)   a stair committed — every roofed room it breaks through
TryBuildRoof()            a roof arrived over stairs that already stand
NotifyStairGone(stair)    WallStair.OnDestroy — the well closes, the deck heals
```

Both directions are needed because a roof over a finished stair and a
stair under a finished roof are the same fact reached from opposite ends.
This *looks* like the spatial "what is under me" query the doctrine
forbids; it isn't — it is the `FindCrossings` pattern, a geometric
question asked at commit whose **answer is stored**. Nothing re-checks a
well per frame and no deck rebuilds because a stair moved.

**The hole's shape is headroom, not footprint.** The well opens from the
tread that first comes within `Headroom` (2m) of the deck and runs to the
end of the chain — the landing included, which matters as much as the
climb because the landing finishes flush *at* the roof plane and would
otherwise be coplanar with the deck it is supposed to arrive on.

**The lip.** A stair butts flush against the wall's inner face, which is
exactly where the deck's edge sits — so the well's natural footprint
*touches* the deck boundary rather than sitting inside it, and a keyhole
bridge needs a strictly interior hole. The well is drawn `WellLip`
(0.05m) narrower than the treads. The cost is a 5cm lip of deck lying
coplanar over the outer edge of the landing. If that reads as a shimmer
rather than a joint line, the honest fix is a real polygon difference
(the well as a notch in the outline), not a bigger lip.

**The triangulator held.** Holes go in via the keyhole bridge —
`BridgeHole` takes the shortest bridge that crosses nothing (an O(n·m)
scan, trivial at this scale, and more robust than the textbook
rightmost-vertex ray), winds the hole backwards and doubles the seam
vertices. The `!clipped` fan fallback was the flagged risk and it does
not trigger, but only after one fix: **a bridged hole repeats its two
seam vertices, and a repeat sits exactly ON the ear it duplicates**, so
the containment test counted it and no ear near the seam was ever
clippable — which would have dropped the whole cap to the fan and filled
the well back in. `Triangulate` now skips coincident vertices.

Verified: square room, top cap 83.10 = deck 88.36 − well 5.26 exactly;
circular room (3 spans), 97.04 = 102.25 − 5.20; all 28 rim triangles face
into the well; deleting the stair heals the cap back to 88.36 exactly.

Written generically over both slabs, though only `RoomRoof` can be
crossed today — an upper-storey room gets no floor of its own
(`BuildFloor` returns early) and there are no cellars.

## Deferred, deliberately

- **The mid landing.** Two separate things hide under the word "landing".
  The **top landing** is built (above). A **mid landing** turns a dog-leg
  into two flights and needs a floating platform at an intermediate base
  — a real object, not geometry the stair can grow. Still deferred.
- **Mural stairs** (inside a wall's thickness) — `WallEdge.openings` now
  exists for doorways, and a mural stair is the same subtraction with a
  sloped head. Closer than it was, still not this system.
- **Spiral / newel stairs** — one quadratic cannot turn 360°. The circle
  tool's answer (eight arcs) works but needs chaining.
- **Dog-leg with a mid landing** — needs a landing object, a floating
  mini-deck at an intermediate base. Real scope.
- **A pitch dial** — if the long shallow grand staircase is ever wanted,
  scroll is the obvious home, and it would have to join the wheel
  arbitration (`ClaimingScrollWheel`).
- **Support** — a stair checks nothing under it, exactly like a wall.

## Build order

1. ✅ **The stair** — `WallStair`, the stepped wedge, the Stairs button,
   the hover ghost, place, delete. External stairs work end to end.
2. ✅ **Playtest round one**, and its fixes: chaining through nodes, the
   corner weld, the pitch correction, and the cutaway.
3. ✅ **Playtest round two**, and its fixes: the cutaway became a real
   slice (see `CutawayView`) so walls and corner posts cut at one plane.
4. ✅ **The landing** and ✅ **the well**.
5. **Playtest round three** — the landing's feel at 2.5m, whether the
   well's headroom is generous enough to walk rather than clip, and the
   5cm coplanar lip along the landing's outer edge.

The stair came before the well because it is the risky-*feel* part and
the well is the risky-*code* part: if the gesture is wrong, the well is
aimed wrong too. Round one bore that out — the gesture was wrong in a way
no amount of well work would have revealed.
