# Wall graph rebuild — design brief (decided 2026-07-29)

User decision after two sessions of whack-a-mole junction fixes: rebuild the wall
system core around a **node/edge graph**. Root cause being fixed: walls are
independent objects whose relationships are *inferred* by physics probes
(bands, grazes, overlap boxes, yaw gates) instead of *stored* — every new
gesture spawned new ambiguous overlap cases needing new classifiers. The grid's
real loss wasn't snapping, it was structure-by-construction; the graph restores
that without the grid.

## The principles (everything derives from these)

1. **A castle is a graph.** Every wall is an edge between exactly two nodes.
   Nodes and edges are the stored model — never rediscovered from geometry.
2. **Edges never overlap or cross; they meet only at nodes.** Illegal states
   are unrepresentable, not detected. Landing on a wall's face or end means
   landing on a node (splitting the host edge mid-run when needed).
3. **Nodes own all joint geometry.** The existing WallNode wedge rule (implicit
   round post radius = half thickness; sort member inward bearings; each gap
   exposes gap−180° of arc) becomes the ONLY joint renderer — any angle, any
   valence. Edges sweep cleanly between their nodes and cap flat ON the node
   point (the nodeAtStart/nodeAtEnd capping already built).
4. **Placement is graph editing.** Click ground = new node; click a wall =
   existing node or split-node; drag = propose an edge. All assists (length/
   angle stepping, height matching, distance guides, Offset tool) are just
   ways of choosing node positions.

## Decided behaviors

- **Crossing gesture auto-splits**: drawing across an existing wall creates a
  4-way node — both walls split, four edges meet, the node draws the joint.
  (This IS the user's "two walls, one on each side," with the bookkeeping
  automated. Refusal was considered and rejected as needless friction; under
  the graph it would be a one-branch preference anyway.)
- Flank-anchored placement steps direction from the face normal (15°,
  perpendicular default); snap ghosts are rectangular stubs wholly outside the
  host — both shipped 2026-07-29 in the old model, keep the UX in the new one.
- Near-parallel/redraw-along gestures: snapping collapses them onto existing
  nodes/edges; a wall along a wall is unrepresentable. Flush face-to-face
  parallel walls (side-by-side courses) remain legal — faces touching is not
  overlap. Keep a face-flush snap assist for it (Offset tool already does).

## What this deletes (most of SplineWall's logic, most of CLAUDE.md's gotchas)

FindJunctionBands, graze lanes, cut planes + ClipRow mesh clipping, the 10°
JunctionAngle gate, IsBlocked duplicate detection, buildOrder cut asymmetry,
ignoreWalls plumbing, buriedInMember + all-buried refusal, through-band
crossing refusal, EndExtension overshoot/contact-fusing, Links inference and
linked-rebuild flows (the graph IS the links), hasGaps (deleting a mid-section
splits the edge into two edges at new flat-capped nodes instead).

## What survives nearly untouched

- Spline section sweep meshing (arc tables, shared boundary params,
  watertight sections, runtime-owned meshes).
- WallNode wedge/post meshing — promoted from patch to renderer. The "flank
  member" special case dies: a T splits the host, so all members are end
  members; valence handles the rest.
- Vertical span math: bottomY/topY, terrain-stepped bases, smooth height
  scroll, locked-top matching, stacked courses (attach to edges; posts rise).
- The whole snap/assist UI: TryEndSnap/TryFlankSnap become "find or create
  node," gold tinting, height adoption, angle arc, guides, Offset tool.
- Delete tool granularity (sections), reinterpreted as edge splitting.

## Why now

Structural support (the next design pillar) wants load paths = graph
traversals. Rooms later = enclosed faces of the planar graph (solved problem),
which feeds floors/roofs. Graybox scenes are throwaway — no migration.

## Suggested build order (next session)

1. Data model: `WallGraph` registry — Node {position, edge-end list},
   Edge {nodeA, nodeB, control (curves keep first-class), height data,
   sections}. Nodes/edges serializable in-scene (components, like today).
2. Edge meshing from the existing section sweep, always node-capped both ends.
3. Node meshing from the existing wedge rule, all members end-members.
4. Placement rewrite: anchor/drag → node resolution (new / existing / split),
   commit = graph mutation that triggers remesh of touched edges + nodes.
   Auto-split on crossing (intersection point → node → split both).
5. Delete: edge removal (dissolve 2-valent/empty nodes, remerge collinear?),
   section deletion = edge split.
6. Courses + height matching re-hookup; then regression playtest.

Old-model reference points worth keeping open: SplineWall.cs @ b17fdb5+ for
the sweep/wedge math; CLAUDE.md wall-system section describes the outgoing
behavior in detail.
