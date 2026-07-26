# -800 Years — working notes for Claude

Unity 6 / HDRP castle-builder graybox. Session workflow: `/start` to recap, `/wrap` to archive+commit — `Docs/NextSession.md` and `Docs/SessionLog/` are the project memory; read them, not this file, for current state.

## Two wall systems coexist (deliberate)

- Straight walls = grid pieces (`GridPlacementSystem` + `PlacedPiece`); curved walls = `SplineWall` objects with uniform sections. Do not "unify" them casually — it's a known, discussed future step, not an oversight.
- Cross-system occupancy is **physics overlap with a 25° angle rule** (crossing = junction, near-parallel = duplicate → blocked). Both `SplineWall.IsBlocked` and `GridPlacementSystem.SplineWallBlocksSpan` implement it; change one, change both.
- Grid pieces have **square footprints — transform yaw does not encode run direction**. Any direction/angle test must use `PlacedPiece.runYaw` (recorded at placement). This has caused two real bugs already.

## Vertical math is spans, not layers

- `PlacedPiece.bottomY/topY` and `SplineWallSection.bottomY/topY` are the source of truth. Never assume uniform piece height or ground at y=0 (terrain exists; cell ground = min of 4 corners, quantized down by `baseStepSize`).
- Everything placement-related is deliberately **stepped** (height ×0.25, bulge 0.5m, bases 0.5m) so separately-built structures align. Don't make these continuous.

## Spline wall geometry

- Section meshes are watertight only because neighbors evaluate the same curve at the same boundary parameter from one shared arc table. Section `tStart/tEnd` can be **outside [0,1]** (end extensions) — use `ExtendedArcAt`/`TFromExtendedArc`, not `ArcAt`/`TAtArc`, for anything touching stored section params.
- Section meshes are runtime-owned (`ownedMesh`, destroyed with the section); preview meshes are cached by input-tuple and destroyed on rebuild — leaks appear fast if commit/cancel paths stop transferring ownership.

## Input arbitration

- The scroll wheel has three meanings: camera dolly (default), wall height (Ctrl), curve bulge (`GridPlacementSystem.ClaimingScrollWheel` during bend phase). `FreeFlyCamera` checks both before dollying — any new wheel use must join this arbitration.
- Assist boxes are the only trigger colliders in the build scene; raycast/overlap code filters `isTrigger` to skip them. Adding unrelated triggers will confuse placement targeting.

## Tooling gotchas

- `manage_texture`'s `set_import_settings` MCP action silently drops its payload — set import settings via `execute_code` (editor API) or hand-edit the `.meta`.
- HDRP motion blur smears screenshots taken the frame after a camera teleport — capture twice, keep the second.
- After script edits: `refresh_unity` (compile) + `read_console` for errors before proceeding; play-mode `execute_code` tests that delete-then-rebuild must use `DestroyImmediate` (deferred `Destroy` races validation).
