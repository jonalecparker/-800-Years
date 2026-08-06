# Next Session

> Long arc: `Docs/Roadmap.md` — five phases from builder to siege, plus
> the "Near arc" section (agreed 2026-08-05): save → real terrain →
> permanent castle → economy → procedural town.

**Where we left off:** The castle **saves and loads**. `CastleSave` serializes the whole graph (nodes, edges, openings, stairs, room rings, wells) to `Saves/castle.json`; geometry rebuilds on load through the same paths a commit uses. F5 saves, F9 loads, build mode only. Verified in play mode with a full-feature test castle (perfect fingerprint round-trip; the loaded graph still splits and breaches correctly) and hand-tested by the user. The lose-the-castle-every-recompile loop is over. Also fixed: the UnityMCP server registration was missing for this project (present now — tools connect natively next session if the server is running).

## Top priorities

1. **Real European terrain** — pick a real castle site, pull ~1m national
   LiDAR (France RGE ALTI / England DEFRA both free), import as the Unity
   terrain. One site, one tile. Saves made before this land are throwaway
   (terrain-tied by design — the save warns on mismatch but loads anyway).
2. **Then: the permanent castle** — build and walk it on the real hill (the
   feel-judgement session that has slipped five times, now on ground that
   means something). Fold in the round-three playtest leftovers
   (`Docs/Stairs.md` step 5: landing feel at 2.5m, the 5cm coplanar lip,
   raised sills/thresholds, door step readout, stairs arriving at a
   doorway) and the performance-pass confirmation (restart the editor
   first; judge stutter *character*, not framerate). Decide here whether
   this castle is also the game's Day-One inherited castle (design doc
   tension flagged 2026-08-05).
3. **Graduate the two design briefs.** `Docs/Stairs.md` and `Docs/Doors.md`
   fold into `CLAUDE.md` and become history once they've survived a
   playtest — same lifecycle as `WallGraphRebuild.md`.

## Backlog / open questions

- **Any new stored fact must join `CastleSave`** — a field added to `WallEdge`/`WallRoom`/`WallStair` that isn't serialized silently vanishes on load. (Also in CLAUDE.md.)
- **Roof decks have no parapet** — you walk straight off the edge, since the deck is flush with the wall tops. Honest for a graybox; crenellations are on the procedural-detailing list.
- **`CLAUDE.md`'s motion-blur screenshot note is probably obsolete** — the smear was TAA ghosting, and the camera is FXAA now. Confirm on the next capture, then update the note.
- **Builds still default to High Fidelity** while the editor is on Performant (`m_PerPlatformDefaultQuality`, no scripting API). Arguably right — light editor, full-fidelity build for art judgement — but know it's there.
- **The real performance read isn't available over RDP.** Judging whether the art pipeline's lighting pass is affordable needs a local session or a build run at the machine.
- Walk speeds (2.8 walk / 6 run m/s), eye height 1.7m, body radius 0.35m are public fields on `WalkMode` and untuned by feel.
- Node posts have no collider, so you can lean into the quarter-barrel at an outside corner. Visual clip, not a hole — adding one would change what the *cursor* hits too.
- **A room inside a room may lay a second floor slab coplanar with the first** (found 08-04, **not confirmed**). `BuildFloor`'s elevated-room branch needs `wallBase > floorY + 0.25`, and a deck exactly one base step above the terrain doesn't clear it. On sloped ground the branch fired correctly.
- **A doorway with 0.6m–2.2m of wall above its sill becomes a full-height gateway** rather than a doorway. Intentional for a low wall with the sill at grade; may read as damage where the sill was *raised*.
- **Structural support is deliberately absent** (user's call): nothing stores what rests on what, so deleting a lower wall leaves masonry hanging. `baseY` alone is enough to reconstruct support when the design call lands — don't add a spatial "what is under me" query meanwhile. Standing rule from the 2026-08-05 design session: **no new system may assume construction is instantaneous in the world.**
- **Mid landing / dog-leg** — needs a real floating platform object at an intermediate base. Also deferred: spiral/newel stairs, mural stairs, a pitch dial (would join `ClaimingScrollWheel`).
- **Windows** — the same `Opening` with a sill *and* a head, so `Runs` wants a second raised-bottom case. Also deferred: arched heads, a door that opens, doorways at nodes.
- **Circle and rect don't get the skirt** — one shared `EdgeParams` per figure, so a stacked circle on a stepped host still shows gaps the wall tool no longer does.
- **The room chain's live segment is a straight chord** — riding a *curved* wall top follows the centerline at the ends but doesn't bend along it. The wall tool does.
- **Elevated rooms get no floor slab** — a room designated on an upper storey has nothing to stand on.
- **A room's roof reads its own ring edges' tops**, so stacking a wall over a room's low wall does *not* make that room roofable. `WallRoom.RetryRoofless` fires on every `CommitEdge`, which isn't the same thing.
- **Stacked walls sit on a buried `skirt`, not a stepped bottom.** If a storey should visibly step with its sloped host, the fix is a baked per-section bottom profile — which must survive `SubCurve` reparameterization and serialization.
- **Undesignating a room** — not built, not designed. A sub-face inside a designated room is refused as "already a room", which blocks splitting a hall in two.
- **Vertical delete** doesn't span a stack (see structural support).
- `WallGraph.FaceAt` traces every edge both ways each hover frame in Designate mode; `CutawayView` walks a few hundred renderers with one array allocation a frame. Both free at this scale, both want revisiting together in the hundreds.
- Stepped landings shallower than ~40° can't reach the host within the 2m probe and stay free at the stepped bearing — may want an angle-scaled reach if it bites in play.
- Room stamp tool (user picked "both — rect first" 2026-07-31): copy a finished room's footprint and stamp clones elsewhere, rotation snapping to the wall bearing at the drop point.
- Rect length/width solves scan **free (1-valent) wall ends only** — right for curtain ends; revisit if towers should also solve against T-nodes or room corners.
- Lock-top planes are per-gesture. Workflow answer for now — chain the curtain wall corner to corner so one plane travels the circuit.
- Known graph edges (minor): 0.75–0.9m dead sliver near node ends where neither snap fires; Alt placement can bury an end inside a wall with no graph connection; 2-valent collinear nodes from deletes never re-merge; drag ghosts from a node overlap the host near the joint.
- Refusal gate is deliberately narrow (<3°, <0.15m, >0.6m run) and crossings split at ANY angle — sliver crossings under ~10° may look rough.
- **Procedural detailing** (potential feature, 2026-07-30): the game dresses the player's shapes — crenellations on wall-walk tops, arrow loops / windows as *section variants* (`WallEdge.Opening` is the proof this works), towers at corners, chimneys on kitchens. Details are DERIVED, never stored. Waits mostly on wall types.
- Marquee delete takes occluded walls inside the box (selects by screen projection); red shells are the confirmation surface.
- `BuildMenu` categories are still placeholder-ish — real wall types will force the issue.
- Dirt texture tiling repeats on the hill shoulder; world ends hard at the 800m terrain edge.
- Wall material lacks roughness/AO (HDRP mask map packing) — deferred for scope.
- Art pipeline step 1 (parallel lane, not started): buy one realistic modular medieval pack → courtyard scene → HDRP lighting pass. User judges the image, Claude supplies settings.
- Design renown (#11) — top undesigned system. Special rooms — #14. AI-prop bake-off (Tripo / Meshy / Rodin); Character Creator 5 trial. Walking liveliness — #12. Hard-mode death — #4. Liege — #5. AI nobles — #6. Vassal departure — #7. Alliances — ⏸ tabled. Deep-research pass: real lives of ~1220 landowners. The lord's income (#16) — near arc step 4.
- Editor gotchas: the wall graph is **play-mode only** (`OnEnable` registration — edit-mode tests register nothing and leave orphans). `execute_code` runs as a *method body*: no `using` directives, and `Object` is ambiguous — fully qualify `UnityEngine.Object`. Objects built and physics-tested in the **same** `execute_code` call have unsynced colliders — split across calls or `Physics.SyncTransforms()`. MCP bridge: HTTP server at `127.0.0.1:8080/mcp`, user starts it; see memory note `unity-mcp-bridge-recovery`. Don't leave play mode running across recompiles.
- **Mouse-look walls up over Remote Desktop** — `Mouse.delta` returns zero against a screen edge and cursor lock doesn't physically confine ([Unity issue](https://issuetracker.unity3d.com/issues/mouse-dot-delta-returns-zero-when-moving-the-cursor-against-the-screen-edge-when-using-a-remote-desktop-connection)). Accepted, not fixable in code. Don't try again.
