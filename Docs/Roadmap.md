# Roadmap — from graybox builder to the vision

*Agreed 2026-07-30. This is the long arc; `Docs/NextSession.md` holds the
short arc. Update this doc when a phase gate is passed or a design call
lands — it should survive session wraps unchanged otherwise.*

## The vision (player-facing)

A high-def castle **designed by the player** — not every nook handpicked,
but unmistakably theirs. They can **walk into every room**; every room has
a purpose and is decorated for it, even if only a few purposes carry
gameplay. And the castle is a **real defensive structure**: when armies
attack, the design decisions matter — expensive walls hold longer, a tower
is a real archer post, the siege is a scene worth watching.

## Why this is buildable

The wall graph is the foundation every pillar consumes — none of them
needs a second foundation:

- **Authorship + high-def**: player intent lives in shapes, heights, wall
  types, and room designations (all stored). The game's dressing is
  *derived* detail over the graph (see the procedural-detailing entry in
  the NextSession backlog). Art bar is the honest risk → purchased packs
  supply pieces, the rulebook places them, HDRP lighting does the rest.
- **Walk every room**: doors are section mesh variants (slot system
  exists); interior lighting is a one-time HDRP pass; **storeys/stairs
  are the one genuinely missing system** and need a real design session.
- **Rooms with purpose**: designation shipped 2026-07-30. A bedroom =
  designation + seeded furnishing rulebook + prop library (AI-prop
  bake-off feeds this). Only a few room types get gameplay wiring.
- **Sieges**: a breach **is** `WallGraph.DeleteSections` — damage is HP
  per section by wall type, and existing surgery handles the hole.
  Archer posts are graph queries (walkable tops, tower roofs). Collapse
  is the structural-support design. New work is agent AI (runtime
  navmesh over player geometry) and battle presentation.

## Phases

Each phase ends playable. War goes last because it consumes everything
before it (types→HP, towers→posts, gates→targets, cost→stakes).

### 1. Finish the builder
- Hand-playtest + feel pass (never yet done by hand).
- **Wall types** — palisade → stone → fortified ladder: cost, strength,
  walkable tops, material. *The most load-bearing item*: detailing,
  economy, and siege HP all key off it.
- **Openings** as section variants — doors first (walkability gate),
  then windows/arrow loops.
- **Storeys + stairs** ⚠ design session required (floors at height;
  touches courses, rooms, roofs).
- **Gates / gatehouse.**
- **Structural support** ⚠ design call required: refuse unsupported
  placement vs. let it collapse; vertical rests-on semantics.

*Gate: a hand-built multi-storey keep with doors you can walk through.*

### 2. Make it cost
- Resource costs per wall type/section; build time; construction phases
  (construction visuals partly exist).
- *Gate: a castle you had to afford and wait for.*

### 3. Make it beautiful
- Art pack purchase + courtyard scene + HDRP lighting pass (existing
  parallel lane — user judges the image).
- Procedural detail rulebook (crenellations, window placement, quoins),
  derived + seeded, steered by wall type and designation.
- Interior shells + interior lighting.
- *Gate: a screenshot you'd put on a store page.*

### 4. Make it lived-in
- Room purpose roster (#14) + furnishing rulebook per type.
- Walk-mode polish; the few gameplay rooms wired (hall, kitchen, …).
- Renown reads the same graph the detailer reads (#11 tie-in).
- *Gate: a walkthrough tour of a furnished castle.*

### 5. Make it fought-over
- Damage model: HP per section by wall type; breach = existing delete
  surgery; collapse = support system.
- Defender posts from graph queries; archer AI on wall-walks/towers.
- Attacker AI in stages: approach/pathing → ladders/ram → siege engines.
- Siege presentation pass. **Scope guardrail: Stronghold, not Total War**
  — dozens of agents, readable drama.
- *Gate: survive (or lose) a siege where your design visibly mattered.*

## Open design questions (need sessions, not code)

1. **Storeys and stairs** — the largest undesigned system (phase 1).
2. **Structural support** — refuse vs. collapse (phase 1, plan Step 4).
3. Detail rulebook sourcing — the 1220 deep-research pass becomes the
   rulebook's source material (window/crenel conventions), not just
   flavor (phase 3).

## Prior-art anchors

Tiny Glade (derived detail over free walls), Cities: Skylines (node/edge
network tooling), Manor Lords (free polygons with gameplay meaning + solo
dev bar), Valheim (support-feel middle tier), Stronghold (siege scale),
Revit (why topology is stored, never inferred).
