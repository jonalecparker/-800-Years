# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" (agreed 2026-08-05): save → real terrain → permanent
> castle → economy → procedural town. Steps 1–2 are done.

**Where we left off:** The slab half of the building rebuild was
**redrawn as outlines** (late 08-08, after the tiles lost the
hand-test): foundations and floors are now the wall-style chain gesture
— click corners, close on the first, the polygon fills as one prism.
The anchor states the plane, **Ctrl+scroll dials a foundation's plane
in ¼m steps**, the burial/skirt laws refuse live (terrace where they
do), floors anchor on wall tops and snap to wall corners, a stairwell
is drawn around. Save schema v3; v2 tile saves migrate. The first
hand-test batch also shipped: modal camera stand-down, slot trashcans,
and the sunken-foundation crusher (CutawayView restoring Y-scale to 1
every frame) found and fixed. **The user has hand-tested the outline
tools and likes them.**

## Top priorities

1. **Walls along the foundation edge** (user's declared pick-up point):
   walls should snap to a foundation outline's edges — or auto-build
   along the whole outline. The outline IS a stored polyline at a
   stated plane; the wall chain wants to ride it the way it rides wall
   tops. Design questions to settle first: snap-to-edge vs one-click
   perimeter walls (or both), corner handling at outline vertices
   (nodes at each corner?), and what happens where a doorway-sized gap
   is wanted — probably just delete/door tools as usual.
2. **Keep hand-testing the outline slab tools** while building: judge
   the knobs by feel (>50% burial, 2m MaxSkirt, 0.5/0.4m outline
   corner/edge snap, floor node snap ±0.2m, closure radius = node snap
   radius). **Watch-item**: a terrain wall drags through a slab-borne
   building unimpeded (different storeys never meet) — decide whether
   that needs a new law.
3. **Then: the permanent castle** — build and walk it (the
   feel-judgement session, now on ground that means something). Decide
   whether it's also the game's Day-One inherited castle (design-doc
   tension flagged 08-05).
4. **Graduate the briefs.** `Docs/Stairs.md`, `Docs/Doors.md`, and now
   `Docs/BuildingRebuild.md` fold into CLAUDE.md once playtested.
   Roofs (rebuild step 4) and support rules (~20m span, same-height
   connection) wait on that verdict.

## Backlog / open questions

- **Any new stored fact must join `CastleSave`** (`WallEdge`/`WallStair`/`SlabTile`) — unserialized fields silently vanish on load. Schema is v3 (polygon slabs); v2 tiles migrate as 4-corner outlines, v1 refused and deleted.
- **Structural support deliberately absent** (user's call): deleting a lower wall leaves masonry hanging; `baseY` reconstructs support when the design lands. Standing rule: **no new system may assume construction is instantaneous in the world.**
- **Roof decks/wall tops have no parapet** — you walk off the edge. Crenellations are on the procedural-detailing list.
- Curved rooms floor chorded (outline edges are straight) — the answer is a trace-STAMP floor fill: `Enclosure` runs once at click time and emits an ordinary stored polygon. Deferred until the drawn outline is judged.
- The saves list has no rename (delete shipped 08-08, a per-row trashcan); slots live in `Saves/` (committed). Real slots move to persistentDataPath when the game has a front end.
- **A doorway with 0.6–2.2m of wall above its sill reads as a full-height gateway** — may read as damage.
- **Circle/rect don't get the skirt** (one shared `EdgeParams` per figure) — a stacked circle on a stepped host shows gaps the wall tool doesn't.
- **The room chain's live segment is a straight chord** when riding a curved wall top; the wall tool bends, the chain doesn't.
- Mid landing / dog-leg stairs need a floating platform object; spiral/mural stairs, pitch dial deferred. Windows = `Opening` with sill AND head; arched heads, opening doors deferred.
- Stepped landings shallower than ~40° can't reach the host in the 2m probe — angle-scaled reach if it bites.
- Known graph edges (minor): 0.75–0.9m dead sliver near node ends; Alt can bury an end inside a wall unconnected; 2-valent collinear nodes never re-merge; sliver crossings under ~10° look rough.
- Room stamp tool (rect first, 2026-07-31); rect length/width solves free ends only; lock-top planes are per-gesture.
- Level-area ground job on the wishlist (08-07) — foundations cover most of it.
- **Procedural detailing** (2026-07-30): the game dresses the player's shapes — crenellations, arrow loops as section variants, towers, chimneys. Derived, never stored. Waits on wall types.
- `BuildMenu` categories still placeholder-ish; wall material lacks roughness/AO; dirt tiling repeats; world ends hard at the terrain edge.
- Walk speeds / eye height / body radius untuned by feel; node posts have no collider (visual clip at outside corners).
- Builds default to High Fidelity while the editor runs Performant — known, arguably right. Real performance read needs a local session (RDP).
- Art pipeline step 1 (parallel lane): one realistic modular pack → courtyard → HDRP lighting pass. User judges the image.
- Design queue: renown (#11), special rooms (#14), the lord's income (#16, near-arc step 4), AI-prop bake-off, walking liveliness (#12), hard-mode death (#4), liege (#5), AI nobles (#6), vassal departure (#7). Deep-research pass: real ~1220 landowners.
- Editor gotchas: wall graph is **play-mode only**; `execute_code` is a method body (fully qualify `UnityEngine.Object`); same-call physics tests have unsynced colliders; new .cs files need `refresh_unity scope=all`; don't leave play mode running across recompiles. Mouse-look walls up over RDP — accepted, don't try again.
