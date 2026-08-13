using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// The wall-building tool, speaking pure graph: click = node, drag = edge,
// landing on a wall = landing on a node (splitting the host under the
// point when needed), crossing a wall = a shared 4-way node at the
// intersection — all resolved by WallGraph at commit. There is no grid:
// placement is free, and alignment comes from assists — node snapping,
// drag stepping relative to the anchor, distance guides, and the offset
// tool. (The class keeps its historical name so scene references
// survive.)
public class GridPlacementSystem : MonoBehaviour
{
    [Header("Placement")]
    // Source of the wall material, the box ghost mesh, and the preview
    // pool instances — no pieces of it are placed anymore.
    public GameObject piecePrefab;
    // Wall thickness and target masonry section length.
    public float gridSize = 1f;
    // Half the prefab's natural (×1) height — doubled to get the ×1 wall
    // height the multiplier scales from.
    public float placementYOffset = 1.75f;
    // Foundation bases snap DOWN to this increment, so neighbouring walls
    // on gentle slopes share a base and real drops read as clean, uniform
    // foundation steps instead of a ragged edge.
    public float baseStepSize = 0.5f;

    [Header("Height")]
    // Wall height scrolls smoothly — lining up with a neighbour doesn't
    // come from shared increments but from snapping, which adopts the
    // snapped wall's exact height.
    public float minHeightMultiplier = 0.25f;
    public float maxHeightMultiplier = 3f;
    // Meters of wall height per wheel notch.
    public float heightScrollRate = 0.25f;

    [Header("Curve")]
    // Curvature moves in fixed steps so two separately-built curves over
    // the same chord can be made genuinely identical.
    public float curveBulgeStep = 0.5f;
    // Existing nodes within this range of the cursor grab a new wall's
    // endpoint, so walls chain into networks. Hold Alt to place freely.
    public float endpointSnapRadius = 0.75f;

    [Header("Stairs")]
    // Fixed for now rather than scrolled: the wheel already carries four
    // meanings and any new use has to join the arbitration, which isn't
    // worth it to dial a width.
    public float stairWidth = 1.8f;

    [Header("Doors")]
    // Fixed for the same reason a stair's width is: dialing them would
    // need a fifth meaning for the scroll wheel.
    public float doorWidth = 1.1f;
    public float doorHead = 2.1f;

    [Header("Preview")]
    public Color previewColor = new Color(0.3f, 0.9f, 1f, 1f);
    public Color deletePreviewColor = new Color(1f, 0.3f, 0.25f, 1f);
    // The ghost runs gold while a snap is deciding its position, and blue
    // the rest of the time.
    public Color snapColor = new Color(1f, 0.78f, 0.12f, 1f);

    [Header("Assists")]
    // While placing, the pending wall shows lines to the nearest walls
    // within this range, labelled with face-to-face distance.
    public float guideRange = 5f;
    public Color guideColor = new Color(1f, 0.85f, 0.2f, 1f);
    // Free placement is assisted, not gridded: drag direction is fully
    // freeform (hold Shift to snap it to angleStep degrees of bearing),
    // while length steps in lengthStep increments RELATIVE to the anchor
    // so strokes stay commensurable. Alt disables all stepping.
    public float angleStep = 5f;
    public float lengthStep = 0.5f;
    // The options panel's "Lock top height": the wall's top sits level at
    // anchor ground + height and the bottom follows the terrain down,
    // instead of the top riding the ground section by section.
    public bool lockTopHeight;

    public enum ToolMode { Build, Delete, Offset, Room, Stair, Door, Ground, Foundation, Floor, Land }
    public ToolMode Mode { get; private set; } = ToolMode.Build;

    // What the wall-family tools build OF (the menu's Walls items pick
    // it): decides the committed edges' material and price, nothing else.
    // Stone is the default because every wall before the ladder existed
    // was stone.
    public WallKind CurrentWallKind { get; private set; } = WallKind.Stone;

    public void SetWallKind(WallKind kind)
    {
        CurrentWallKind = kind;
    }

    // The Ground tool's two jobs. Path benches a graded walkable band
    // along a drag (Shift holds the anchor's height instead — a level
    // path); Restore returns a band to the natural hill — the undo for
    // every earthwork. Both obey the deviation law.
    public enum GroundAction { Path, Restore }
    public GroundAction GroundTask { get; private set; } = GroundAction.Path;

    // The Room tool is a chain gesture and nothing more since the
    // building rebuild (Docs/BuildingRebuild.md): closing a loop
    // ANNOUNCES the enclosure (Enclosure.AnnounceChainClose) but derives
    // no floor or roof — foundations, floors and roofs are the player's
    // own authored slabs.

    // Straight and Curved shape the wall tool's drag; Circle and Rect are
    // whole-figure gestures (center-out ring, corner-to-corner box) that
    // apply to whichever tool is in hand — walls or rooms.
    public enum BuildShape { Straight, Curved, Circle, Rect }
    public BuildShape Shape { get; private set; } = BuildShape.Straight;

    // True while a curve is in its bend-adjust phase, where the plain
    // scroll wheel means curvature — FreeFlyCamera stands down from
    // scroll-dollying while this is set.
    public static bool ClaimingScrollWheel { get; private set; }

    public float HeightMultiplier => heightMultiplier;
    public float BaseHeight => placementYOffset * 2f;
    // The dialed height LANDS on the quarter-metre grid (heightScrollRate),
    // it doesn't just move by it: fractional trackpad notches accumulate in
    // the multiplier, and absolute tops must rendezvous exactly. Footings
    // sit on the half-metre grid, so a gridded height puts every free-built
    // top on the quarter lattice — floors seat on the same lattice
    // (SlabTile.FloorStep), which is what lets one building's roof meet
    // another's floor by scrolling instead of luck. Snapped adoption still
    // copies exact heights (EffectiveWallHeight): matching a neighbour IS
    // alignment and beats the grid.
    // Round HALF-UP, not Mathf.Round: banker's rounding sends exact
    // half-ties alternately up and down, and the min-height clamp lands
    // the raw height on exactly such a tie (0.25 × 3.5m = 0.875m) — from
    // there every notch is a tie and the dial jumped 0.5m with repeats.
    public float CurrentWallHeight => heightScrollRate > 0f
        ? Mathf.Floor(BaseHeight * heightMultiplier / heightScrollRate + 0.5f) * heightScrollRate
        : BaseHeight * heightMultiplier;

    // The height the active ghost actually builds at: a snapped ghost
    // adopts the snapped wall's exact height (a scrolled height would
    // never land on it), anchor snap outranking a far-end snap outranking
    // hover. Ctrl+scroll overrides the match for the current context, and
    // Alt suppresses snapping and with it the height match.
    public float EffectiveWallHeight
    {
        get
        {
            if (heightOverride)
                return CurrentWallHeight;
            if (anchorSnapHeight.HasValue)
                return anchorSnapHeight.Value;
            if (dragEndSnap.HasValue && dragEndSnap.Value.edge != null
                && !dragEndSnap.Value.noHeightAdopt)
                return SnapHeightOf(dragEndSnap.Value.edge, dragEndSnap.Value.point);
            if (hoverSnapHeight.HasValue)
                return hoverSnapHeight.Value;
            return CurrentWallHeight;
        }
    }

    // What "match the snapped wall's height" means: its stored height —
    // except for a locked-top wall, whose stored height says nothing
    // about its profile on a slope. There the ghost takes the span that
    // puts its own top at the host's locked top at the snap point.
    float SnapHeightOf(WallEdge edge, Vector3 at)
    {
        if (float.IsNegativeInfinity(edge.fixedTopY))
            return edge.height;
        // Measured from the wall's OWN floor: a stacked wall stands on a
        // storey, not on the terrain a storey below, and its skirt (the
        // part buried in the masonry carrying it) is not height either.
        float floor = float.IsNegativeInfinity(edge.baseY)
            ? SampleCellGroundY(at.x, at.z)
            : edge.baseY;
        return Mathf.Max(0.25f, edge.fixedTopY - floor);
    }

    // The absolute elevation the active ghost's top will land at — set by
    // the preview builders from the SAME numbers the commit writes
    // (capture-at-anchor planes, stack fits), so ghost = commit extends to
    // the readout. Null when nothing is being ghosted. On an unlocked
    // terrain run the tops step per section; the readout reports the top
    // at the ANCHOR cell — the number you dial against.
    public float? GhostTopY => ghostTopY;
    private float? ghostTopY;

    // What the cursor is over, as absolute planes read from STORED data
    // (section tops, a room's floor/roof) — never from hit geometry, so
    // the cutaway's sliced colliders can't misreport them. Purely a
    // readout; no tool logic reads it.
    public string InspectLine { get; private set; }

    // How many sections the active preview will actually lay down —
    // 0 when idle or refused, so the HUD counter can show/hide off this
    // alone.
    public int ActiveDragCount
    {
        get
        {
            if (Mode == ToolMode.Delete)
                return 0;
            if (Shape == BuildShape.Circle || Shape == BuildShape.Rect)
                return shapeBlocked ? 0 : shapeSpecs.Count;
            if (Mode == ToolMode.Room)
                return roomFixedSpecs.Count + (roomLiveBlocked ? 0 : roomLiveSpecs.Count);
            if (previewBlocked)
                return 0;
            return splineSpecs.Count;
        }
    }

    // What the active preview will COST, priced from the same specs the
    // ghosts show (ghost = commit extends to the price tag). The commit
    // re-prices from the created edges' real sections, which can differ
    // by the pennies of re-sectioning across splits — a readout, not a
    // gate, so pennies are fine.
    public long ActiveDragCost
    {
        get
        {
            if (Mode == ToolMode.Delete)
                return 0;
            if (Shape == BuildShape.Circle || Shape == BuildShape.Rect)
                return shapeBlocked ? 0
                    : Estate.CostOfSpecs(shapeSpecs, gridSize, CurrentWallKind);
            if (Mode == ToolMode.Room)
                return Estate.CostOfSpecs(roomFixedSpecs, gridSize, CurrentWallKind)
                    + (roomLiveBlocked ? 0
                        : Estate.CostOfSpecs(roomLiveSpecs, gridSize, CurrentWallKind));
            if (previewBlocked)
                return 0;
            return Estate.CostOfSpecs(splineSpecs, gridSize, CurrentWallKind);
        }
    }

    // Slightly larger than the section it covers so the overlay shell
    // draws over the section's own surface instead of z-fighting with it.
    const float DeleteOverlayScale = 1.02f;

    // A plain click lays a stub wall this long: one thickness, so the
    // stub's footprint is square — there are no end extensions to grow a
    // token chord anymore, the edge IS the stub.
    float StubLength => gridSize;

    // One snap result: where the point locked, and how a wall should
    // leave it. A node snap locks onto an existing WallNode; a flank snap
    // locks onto a host's centerline mid-run (the commit splits the host
    // there into a T-node at any angle).
    struct EndSnap
    {
        // The representative wall at the snap — the height-match source.
        public WallEdge edge;
        // Set for node snaps.
        public WallNode node;
        public Vector3 point;
        // The bearing pointing from the snap back along the host — the
        // angle guide's zero.
        public float backBearing;
        // The yaw a pre-click stub chord takes here.
        public float stubYaw;
        // The direction a wall LEAVING this snap should run to meet the
        // host cleanly: the open direction for node snaps, sideways off
        // the face for flank snaps. Feeds the tangent-matched connector
        // and the flank drag's angle stepping.
        public Vector3 outDir;
        public bool isFlank;
        // How far along outDir the hover stub's START is buried in host
        // masonry (half a thickness for flank snaps) — the ghost starts
        // past it, at the visible face.
        public float stubTrim;
        // A building landing (the never-cross-into-a-building clamp):
        // the run's dialed height is sacred there — landing on a first
        // storey must NOT shrink a second-storey run to match it.
        public bool noHeightAdopt;
    }

    private readonly List<GameObject> ghostPool = new List<GameObject>();
    private readonly List<GameObject> deletePool = new List<GameObject>();
    private readonly List<Transform> doomedSections = new List<Transform>();
    private readonly List<WallEdge.SectionSpec> splineSpecs = new List<WallEdge.SectionSpec>();
    private readonly List<Mesh> previewMeshes = new List<Mesh>();
    private (Vector3 start, Vector3 end, Vector3 control, float height, float topY, float baseY, float trimStart, float trimEnd)? lastPreviewKey;
    // The TRUE curve a release will commit — the preview may trim its
    // display back to a host's face, but the graph always gets the full
    // centerline-to-centerline gesture.
    // One entry for an ordinary gesture; one PER HOST for a ride that runs
    // across several walls, since each committed edge takes the exact
    // curve of the wall under it and the graph meets them at nodes.
    private readonly List<(Vector3 s, Vector3 c, Vector3 e, float height, float topY, float baseY, float skirt)> pendingCommits
        = new List<(Vector3, Vector3, Vector3, float, float, float, float)>();
    // The host spans a ride currently covers, in order from the anchor.
    private readonly List<(WallEdge host, float tA, float tB)> rideSpans
        = new List<(WallEdge, float, float)>();
    private (Vector3 start, Vector3 end, float height, float baseY, int spans, float lastT)? lastRideKey;
    // Set while a ride is walking to a point it must hit exactly.
    private bool rideExact;
    private bool previewBlocked;
    private List<WallGraph.Crossing> previewCrossings = new List<WallGraph.Crossing>();
    private EndSnap? hoverSnap;
    private EndSnap? anchorSnap;
    private EndSnap? dragEndSnap;
    private bool snapTint;
    // The host-derived yaw of the current hover snap — snapped stubs sit
    // flush with the wall they snapped to instead of keeping their own R
    // rotation. Captured at click for the drag stub.
    private float? hoverSnapYaw;
    private float? anchorSnapYaw;
    // The snapped wall's height — the ghost matches it exactly instead of
    // using the scrolled height. Captured at click for the drag.
    private float? hoverSnapHeight;
    private float? anchorSnapHeight;
    // Set when Ctrl+scroll fires — the scrolled height beats the snap
    // match until the drag ends or the hover leaves the snap, then
    // matching re-arms.
    private bool heightOverride;
    private (Vector3 center, float fromBearing, float toBearing)? activeAngle;
    private LineRenderer angleLine;
    private Mesh prefabMesh;
    private Material wallMaterial;
    private Material palisadeMaterial;
    private Transform splineParent;
    private float currentYRotation;
    private float heightMultiplier = 1f;
    // The slab tools (Foundation / Floor): a drawn outline, chained
    // vertex by vertex like the room chain and filled as one polygon
    // prism on closure. The chain's PLANE is stated at the anchor (seated
    // from ground / adopted from a snap) and held for the whole outline;
    // Ctrl+scroll dials it in quarter steps (foundations only — a floor's
    // plane IS the wall top it connects to).
    private readonly List<Vector3> slabChain = new List<Vector3>();
    private float slabChainPlane;
    // Pre-anchor plane dial, quarter steps off the seated plane; folded
    // into slabChainPlane at the anchor click and reset on cancel/commit.
    private float slabPlaneDial;
    public int SlabChainCount => slabChain.Count;
    // The chain's fill ghost — one mesh, refilled in place each frame.
    private Mesh slabPreviewMesh;
    private readonly List<Vector3> slabTempPoly = new List<Vector3>();
    private readonly List<Vector3> slabBladePoly = new List<Vector3>();
    // The stairwells the tentative floor outline owes, refilled by
    // SlabPolyRefusal each frame (one-test doctrine: the wells the ghost
    // shows are the wells the closing click stores).
    private readonly List<SlabTile.Well> slabWells = new List<SlabTile.Well>();
    private float curveBulge;
    private float curveApexT = 0.5f;
    private Vector3? splineStart;
    private Vector3? splineEnd;
    private WallEdgeSection deleteAnchor;
    // What release will actually remove, as per-edge index ranges — the
    // overlay list above is just its visual shadow.
    private readonly List<(WallEdge edge, int lo, int hi)> deleteRanges =
        new List<(WallEdge edge, int lo, int hi)>();
    // Marquee state: a press on empty ground rubber-bands a screen rect;
    // everything whose center lands inside dies on release.
    private Vector2? marqueeStart;
    private Vector2? marqueeEnd;
    private readonly List<SlabTile> marqueeSlabs = new List<SlabTile>();
    // One-step delete undo: the whole world as save data, captured just
    // before each delete commits, restored by a right click through the
    // load path (CastleSave.RestoreSnapshot — full fidelity, one door).
    // Lives only within the delete-mode session: leaving the tool or
    // standing down clears it, so a stale snapshot can never roll back
    // work done since.
    private string deleteUndoJson;
    private WallEdge offsetSource;
    private float offsetDistance = 3f;
    private float offsetSide = 1f;
    private (Vector3 s, Vector3 c, Vector3 e, float height, float topY)? lastOffsetKey;
    private Camera cam;
    // Latched so the walk-mode stand-down runs on the frame it starts and
    // not every frame after it.
    private bool stoodDown;
    private Vector3? guideCenter;
    private float guideBearing;
    private readonly List<(Vector3 from, Vector3 to, float distance)> activeGuides = new List<(Vector3, Vector3, float)>();
    private readonly List<LineRenderer> guidePool = new List<LineRenderer>();
    private readonly Dictionary<GameObject, Color> ghostTints = new Dictionary<GameObject, Color>();
    private Material guideMaterial;
    private GUIStyle guideLabelStyle;
    // Corner-post ghosts: gold posts at node-snapped ends and pending
    // crossing points while placing — the joints the commit will create.
    private readonly List<(Vector3 pos, float ground, float height)> pendingNodeGhosts = new List<(Vector3, float, float)>();
    private readonly List<GameObject> nodeGhostPool = new List<GameObject>();
    private readonly List<Mesh> nodeGhostMeshes = new List<Mesh>();
    private readonly List<float> nodeGhostHeights = new List<float>();
    // Room tool chain: vertices fixed so far, the baked segments' preview
    // meshes (held until the loop closes or cancels), and the live
    // segment following the cursor. The chain carries ONE height and one
    // locked top, captured at the first click.
    private readonly List<Vector3> roomChain = new List<Vector3>();
    private EndSnap? roomStartAttach;
    private float roomChainHeight;
    private float roomChainTopY;
    private readonly List<WallEdge.SectionSpec> roomFixedSpecs = new List<WallEdge.SectionSpec>();
    private readonly List<Mesh> roomFixedMeshes = new List<Mesh>();
    private readonly List<WallEdge.SectionSpec> roomLiveSpecs = new List<WallEdge.SectionSpec>();
    private readonly List<Mesh> roomLiveMeshes = new List<Mesh>();
    // How many specs/meshes each baked room segment contributed — a right
    // click pops exactly one segment's worth back off the fixed lists.
    private readonly List<(int specs, int meshes)> roomBakeCounts = new List<(int, int)>();

    // A clean right CLICK — press and release without crossing the same
    // pixel threshold the camera's right-drag orbit uses — means "take
    // back the previous placement" in every chain gesture (slab outline
    // corners, room chain corners, wall chain legs). A right DRAG is the
    // orbit and never an undo. Recomputed once per frame, before dispatch.
    private bool rightClick;
    private bool rightPressLive;
    private Vector2 rightPressPos;
    private const float RightClickMaxPixels = 4f;

    // The straight wall tool CHAINS: each commit re-anchors the gesture
    // at its own end node, and each leg remembers the edges it created so
    // a right click can unwind it. History is only live while the chain
    // is (splineStart set); a fresh anchor click clears it.
    private struct WallChainLeg
    {
        public Vector3 start;
        public float? baseY;
        public float height;
        public List<WallEdge> created;
    }
    private readonly List<WallChainLeg> wallChainLegs = new List<WallChainLeg>();

    // Ground tool gesture: one drag = one bench or restore band.
    private Vector3? groundAnchor;
    // The anchor's ground height, captured at the click — the live
    // preview deforms the terrain under the anchor itself, so sampling
    // mid-drag would read the bench back instead of the hill.
    private float groundAnchorY;
    private Vector3? restorePaintLast;
    private float restorePainted;
    // Preview throttle: re-deform only when the drag meaningfully moved.
    private Vector3 groundPreviewB;
    private float groundPreviewYb, groundPreviewW;
    private bool groundPreviewLive;
    private Mesh groundGhostMesh;
    private Mesh groundGhostCutMesh;
    private Mesh brushGhostMesh;
    // Ctrl+scroll dials these, like wall height — 0.5m per notch within
    // honest bounds: a footpath to a road, a touch-up to a broad sweep.
    public float pathWidth = 2.5f;
    public float restoreBrushRadius = 4f;
    const float MinPathWidth = 1.5f, MaxPathWidth = 10f;
    const float MinBrushRadius = 1.5f, MaxBrushRadius = 12f;

    private (Vector3 a, Vector3 b, float h, float topY, float trimA, float trimB)? lastRoomKey;
    private bool roomLiveBlocked;
    private bool roomLiveClosing;
    // Circle/Rect gesture state: the anchor (circle center or first
    // corner), the whole figure's preview specs, and the crossing points
    // it would split (shown as gold posts).
    private Vector3? shapeAnchor;
    private readonly List<WallEdge.SectionSpec> shapeSpecs = new List<WallEdge.SectionSpec>();
    private readonly List<Mesh> shapeMeshes = new List<Mesh>();
    private readonly List<Vector3> shapeCrossings = new List<Vector3>();
    private (Vector3 anchor, Vector3 to, float w, float h, float topY)? lastShapeKey;
    private bool shapeBlocked;
    // Rotated-rect gesture: the first side's far end (fixed by click 2)
    // and the anchor's snap, kept for relative Shift-stepping and the
    // corner-angle arc while the side is laid along existing masonry.
    private Vector3? shapeSideEnd;
    private EndSnap? shapeAnchorSnap;
    // Ctrl+click plants the side's MIDPOINT instead of a corner — on a
    // free wall end the side poses perpendicular to that wall, so a
    // curtain wall runs into the center of the tower face (the classic
    // corner-tower junction). Click 2 bakes the derived corners and the
    // rest of the gesture proceeds as usual.
    private bool shapeCentered;
    // The figure's locked-top plane, captured AT THE ANCHOR CLICK while
    // the hover snap still feeds EffectiveWallHeight — ground(anchor) +
    // adopted height puts a snapped figure's plane exactly at the
    // host's. Recomputing later would silently use the HUD height (the
    // hover snap is gone by the drag frames) and miss by a step.
    private float shapeTopY = float.NegativeInfinity;
    // The anchor's stepped ground — the plane a terrain figure's tops
    // stand on (plus the dialed height). Ground is never moved.
    private float shapePadY;
    // White alignment lines: shown whenever a point being placed lines
    // up with existing masonry.
    private readonly List<(Vector3 from, Vector3 to)> alignLines =
        new List<(Vector3, Vector3)>();
    private readonly List<LineRenderer> alignPool = new List<LineRenderer>();
    private Material alignMaterial;
    // The figure's edges as last previewed — exactly what a commit lays.
    private readonly List<(Vector3 s, Vector3 c, Vector3 e)> shapeEdgesLive =
        new List<(Vector3 s, Vector3 c, Vector3 e)>();
    // On-figure dimension labels (side, width, radius), cleared per frame
    // — the answer to "are my towers actually the same size?"
    private readonly List<(Vector3 from, Vector3 to, float distance)> dimGuides =
        new List<(Vector3, Vector3, float)>();
    // Elevated ground: hovering a room slab (roof deck or floor) makes
    // its top the gesture's base — walls stand ON it instead of falling
    // to the terrain. Captured per gesture at the anchor click.
    private float? hoverBaseY;
    private float? anchorBaseY;
    // The wall whose TOP FACE the cursor is on, and the one the gesture
    // anchored on. While a run stays on one host's top it RIDES that
    // host's centerline: the drawn edge takes its curve from the wall
    // below (WallGraph.SubCurve), so a storey over a curved wall follows
    // it exactly instead of being eyeballed with the bulge wheel. Alt
    // (free placement) skips the ride like every other snap.
    private WallEdge hoverRideHost;
    private WallEdge anchorRideHost;
    // The storey the CURSOR RAY names, which is not always the storey the
    // gesture builds on: pointing at a ground wall's side means the ground
    // storey even though nothing elevated is being hovered. Snaps compare
    // XZ only, so a wall and the wall stacked on it are a dead tie — the
    // ray is the one thing that can tell them apart, and throwing it away
    // made the hover flip between storeys on every stack.
    private float? hoverSnapBase;
    // The slab the cursor is standing on (its top OR side face — a ray
    // just past the rim hits the prism's skirt, which is still an aim at
    // this slab). Walls hug its rim: the point pulls to the outline inset
    // half a wall thickness, so the wall's outer face lies exactly on the
    // slab edge (TrySlabRimSnap).
    private SlabTile hoverSlabTile;
    // The Survey tool's hover: the parcel under the cursor and the line
    // the inspect panel shows for it.
    private LandPlot hoveredPlot;
    private string landInspect;
    // The host the CURRENT preview is riding — the gesture's own host
    // while the run stays on it, cleared the moment Alt frees the run so
    // the ghost stops inheriting a curve it no longer follows.
    private WallEdge previewRideHost;
    private float? roomChainBaseY;
    private float? shapeBaseY;
    // Shape gestures adopt a snapped anchor's height like walls do.
    private float? shapeSnapHeight;

    // ---- Stair tool ------------------------------------------------
    // The stair tool has no gesture: point at a wall's side face and the
    // ghost is already there, because the wall answers every question
    // (where it starts, where it lands, how long it is, which curve).
    private readonly List<WallEdge.SectionSpec> stairSpecs = new List<WallEdge.SectionSpec>();
    private readonly List<Mesh> stairMeshes = new List<Mesh>();
    // The wall under the cursor last frame — changing walls re-arms the
    // camera's say over the climb direction.
    private WallEdge stairHost;
    // Which way the ghost climbs, and whether the player has taken that
    // decision off the camera by pressing R. Without the latch, orbiting
    // the camera would silently undo an R press.
    private bool stairForward = true;
    private bool stairHeld;
    // R's extra stop: the flight turns OUT from the wall face — a
    // straight perpendicular run from its own foot up to the wall top,
    // arriving flush at the face. It consumes no masonry arc, so it fits
    // where an along-wall run can't.
    private bool stairPerp;
    // Ctrl+scroll: the dialed CLIMB in quarter steps. Null (the rest
    // state) means the wall answers — the full top, the old behaviour.
    // The dial only ever chooses LESS than the hovered wall's own rise;
    // dialing back up to the top re-adopts it (back to null).
    private float? stairRiseDial;
    // The ghost's landing elevation, for the GhostTopY readout — a field
    // because the preview early-returns on its cache key most frames.
    private float stairPreviewTopY;
    private (WallEdge host, int section, float t, bool forward, float side,
        bool perp, float rise, float bottom)? lastStairKey;
    // Where the run came to rest, so the second sizing pass can read the
    // wall top it actually lands on rather than the one under the cursor.
    private (WallEdge edge, float t)? stairLanding;
    // Not enough masonry to carry the climb: the ghost draws where it
    // would go and runs red, and the HUD says how much more wall it wants.
    private bool stairBlocked;
    private float stairShortfall;
    // The run is climbing TO a doorway's sill rather than to a wall top,
    // so the cursor end is its top and the chain gets flipped.
    private bool stairToDoor;

    // Everything the commit needs, captured while the ghost is drawn.
    private struct PendingStair
    {
        public List<WallStair.Span> spans;
        public float bottomY;
        public float topY;
        public float runArc;
        public float baseFrom;
    }
    private PendingStair? pendingStair;

    // ---- Door tool -------------------------------------------------
    // Same shape of gesture as the stair, for the same reason: the wall
    // decides everything except where along it you want the hole.
    private Mesh doorGhostMesh;
    private WallEdge doorHost;
    private float doorT;
    private bool doorBlocked;
    private string doorRefusal;
    // Where the opening starts, and how far that is above the ground
    // outside — a doorway serving a raised floor needs steps up to it.
    private float doorSill;
    private float doorStep;
    // Masonry that must remain either side of an opening, so a doorway
    // can't eat a wall's end and leave a node holding a sliver.
    const float DoorJamb = 0.3f;
    // Below this, a raised sill is a doorstep you step over, not a climb.
    // Wall footings quantize DOWN to the base step and a room's floor
    // quantizes UP, so nearly every doorway has half a step under it even
    // on level ground — offering a flight of stairs for that would be
    // noise. Three risers is the smallest thing worth building steps for.
    const float DoorStepsNeeded = WallStair.TargetRise * 3f;

    const int CircleSegments = 8;
    const float MinCircleRadius = 1.5f;
    const float MinRectSide = 1f;
    // A figure side at least this coincident with existing masonry is
    // reused instead of redrawn.
    const float ReuseCoverage = 0.9f;
    // How far past the cursor a Shift-stepped room segment will run to
    // meet a wall the cursor is already grazing, so the step and the
    // landing can both be honoured. Overshooting is safe — the clamp
    // takes the FIRST crossing along the run.
    const float SteppedLandingReach = 2f;

    void Start()
    {
        cam = Camera.main;
        splineParent = new GameObject("WallGraph").transform;

        MeshFilter prefabFilter = piecePrefab != null ? piecePrefab.GetComponent<MeshFilter>() : null;
        prefabMesh = prefabFilter != null ? prefabFilter.sharedMesh : null;
        MeshRenderer prefabRenderer = piecePrefab != null ? piecePrefab.GetComponent<MeshRenderer>() : null;
        wallMaterial = prefabRenderer != null ? prefabRenderer.sharedMaterial : null;
        // Palisade timber is the stone material warmed brown — a graybox
        // stand-in until the wall-type detailing pass; the tint keeps the
        // two kinds tellable at a glance.
        if (wallMaterial != null)
        {
            palisadeMaterial = new Material(wallMaterial) { name = "PalisadeTimber" };
            palisadeMaterial.color = new Color(0.52f, 0.36f, 0.22f, 1f);
        }
    }

    Material MaterialForKind(WallKind kind)
        => kind == WallKind.Palisade && palisadeMaterial != null
            ? palisadeMaterial : wallMaterial;

    void Update()
    {
        if (piecePrefab == null || cam == null)
            return;

        // Walking is not building, so the tool stands DOWN rather than
        // merely stopping: a frozen Update would leave a half-drawn
        // gesture live, a ghost standing in the air, and the cutaway
        // still slicing — and a sliced wall's collider is sliced with it,
        // so a cut castle is one you walk straight through. The Saves
        // panel stands the tool down the same way: it captures the
        // keyboard for slot names, and a tool that kept reading R, C and
        // F5 under someone's typing would act on their spelling.
        if (WalkMode.Active || BuildMenu.ModalOpen)
        {
            if (!stoodDown)
            {
                StandDown();
                stoodDown = true;
            }
            return;
        }
        stoodDown = false;

        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null)
            return;

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        // Clicks meant for the HUD shouldn't also reach through it into the
        // world and start placing or deleting behind the bar.
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        // Right-CLICK detection (see the field block): fires on release
        // only if the pointer stayed inside the orbit threshold, so the
        // camera's right-drag orbit and the chain undo never collide.
        rightClick = false;
        if (mouse.rightButton.wasPressedThisFrame)
        {
            rightPressLive = true;
            rightPressPos = mouse.position.ReadValue();
        }
        else if (mouse.rightButton.wasReleasedThisFrame)
        {
            rightClick = rightPressLive && !overUI
                && (mouse.position.ReadValue() - rightPressPos).sqrMagnitude
                    <= RightClickMaxPixels * RightClickMaxPixels;
            rightPressLive = false;
        }

        // Save/load hotkeys. Build mode only, by construction — walk
        // mode's early return above never reaches here, which is right:
        // loading swaps the world out from under a walker. Loading also
        // stands the tool down first, so no half-drawn gesture or delete
        // selection is left pointing at objects the load destroys. Like
        // the cutaway's keys, these are not tool modifiers and stay out
        // of ModifierHints; the BuildLog reports what happened.
        if (keyboard != null && keyboard.f5Key.wasPressedThisFrame)
        {
            CastleSave.Save(CastleSave.DefaultPath);
            return;
        }
        if (keyboard != null && keyboard.f9Key.wasPressedThisFrame)
        {
            LoadSlot(CastleSave.CurrentSlot);
            return;
        }

        ClaimingScrollWheel = (Mode == ToolMode.Build && Shape == BuildShape.Curved && splineEnd.HasValue)
            || (Mode == ToolMode.Offset && offsetSource != null);

        // Placement branches re-set the pending wall's center and bearing
        // each frame; anything else (delete mode, idle off-target) leaves
        // it unset and the guides disappear with it. Same for the snap
        // state and the corner-angle readout.
        guideCenter = null;
        activeAngle = null;
        dimGuides.Clear();
        alignLines.Clear();
        snapTint = false;
        hoverSnap = null;
        hoverSnapYaw = null;
        hoverSnapHeight = null;
        hoverBaseY = null;
        hoverSnapBase = null;
        hoverSlabTile = null;
        pendingNodeGhosts.Clear();
        ghostTopY = null;

        if (Mode == ToolMode.Delete)
            UpdateDelete(mouse, keyboard, ray, overUI);
        else if (Mode == ToolMode.Land)
            UpdateLand(mouse, ray, overUI);
        else if (Mode == ToolMode.Offset)
            UpdateOffset(mouse, keyboard, ray, overUI);
        // Stairs have no gesture either — same reason as Designate below,
        // an armed Circle or Rectangle has nothing to do with them.
        else if (Mode == ToolMode.Stair)
            UpdateStair(mouse, keyboard, ray, overUI);
        // Nor does the door tool: point at a wall, the opening is there.
        else if (Mode == ToolMode.Door)
            UpdateDoor(mouse, ray, overUI);
        // Ground has one gesture for both jobs: drag a line on terrain.
        else if (Mode == ToolMode.Ground)
            UpdateGround(mouse, ray, overUI);
        // The slab tools draw an outline — a vertex chain, closed on its
        // own first corner, filled as one prism.
        else if (Mode == ToolMode.Foundation || Mode == ToolMode.Floor)
            UpdateSlab(mouse, keyboard, ray, overUI);
        else if (Shape == BuildShape.Circle || Shape == BuildShape.Rect)
            UpdateShapeGesture(mouse, keyboard, ray, overUI, Mode == ToolMode.Room);
        else if (Mode == ToolMode.Room)
            UpdateRoom(mouse, keyboard, ray, overUI);
        else
            UpdateBuild(mouse, keyboard, ray, overUI);

        // In Survey mode the inspect line reads the parcel under the
        // cursor instead of elevations — same panel, the tool's facts.
        InspectLine = overUI ? null
            : Mode == ToolMode.Land ? landInspect : InspectElevations(ray);

        // A scrolled height override lives as long as its context — the
        // active drag, or the hovered snap it was scrolled against.
        // Leaving both re-arms the snap height match.
        if (!splineStart.HasValue && !hoverSnapHeight.HasValue)
            heightOverride = false;

        UpdateDistanceGuides();
        UpdateAlignGuides();
        UpdateAngleGuide();
        UpdateNodeGhostVisuals();
        // A view control, not a build modifier — hence no ModifierHints
        // entry and no scroll-wheel claim. It walks only the committed
        // subtree, so the ghosts above stay visible.
        CutawayView.Tick(keyboard, splineParent);
    }

    // Both tools share the left mouse button, so switching abandons any
    // in-progress drag rather than letting it commit under the other tool's
    // rules.
    public void SetMode(ToolMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        // The survey map exists exactly while the Survey tool is in
        // hand — its colliders must never catch another tool's ray.
        LandPlot.ShowAll(mode == ToolMode.Land);
        if (hoveredPlot != null)
            hoveredPlot.SetHighlight(false);
        hoveredPlot = null;
        landInspect = null;
        // Entering the Ground tool always starts on Path — restoring is
        // a deliberate pick, exactly like Designate.
        if (mode == ToolMode.Ground)
            GroundTask = GroundAction.Path;
        offsetSource = null;
        // The delete undo dies with its tool session: anything built
        // under another tool must never be rolled back by a stale one.
        deleteUndoJson = null;
        CancelCurve();
        CancelShape();
        CancelRoomChain();
        CancelDelete();
        CancelStair();
        CancelDoor();
        CancelGround();
        CancelSlabChain();
    }

    // Everything the tool has on screen or half-finished, put away — run
    // once when walk mode takes the world. Same cancels SetMode makes,
    // plus the per-frame overlay state, which is cleared through the
    // normal update paths so the pooled lines and ghosts hide themselves
    // exactly the way an empty frame would hide them.
    void StandDown()
    {
        CancelCurve();
        CancelShape();
        CancelRoomChain();
        CancelDelete();
        CancelStair();
        CancelDoor();
        CancelGround();
        CancelSlabChain();
        HideGhosts();
        ClearPreviewMeshes();

        guideCenter = null;
        activeAngle = null;
        dimGuides.Clear();
        alignLines.Clear();
        pendingNodeGhosts.Clear();
        snapTint = false;
        hoverSnap = null;
        hoverSnapYaw = null;
        hoverSnapHeight = null;
        hoverBaseY = null;
        hoverSnapBase = null;
        hoverSlabTile = null;
        heightOverride = false;
        deleteUndoJson = null;
        ClaimingScrollWheel = false;
        // The survey map stands down too — the walker must not collide
        // with a draped overlay, and its colliders must not linger.
        LandPlot.ShowAll(false);
        if (hoveredPlot != null)
            hoveredPlot.SetHighlight(false);
        hoveredPlot = null;
        landInspect = null;
        // Walk mode hides the HUD, but the Saves panel doesn't — a stale
        // inspect line or ghost-top under the open panel would report a
        // hover that no longer exists.
        InspectLine = null;
        ghostTopY = null;

        UpdateDistanceGuides();
        UpdateAlignGuides();
        UpdateAngleGuide();
        UpdateNodeGhostVisuals();

        CutawayView.Clear(splineParent);
    }

    // The Saves panel's two doors — slot-aware wrappers that keep
    // splineParent and wallMaterial private. Saving or loading a slot
    // makes it the CURRENT slot, so F5/F9 keep acting on the save you
    // were last working in.
    public void SaveSlot(string slot)
    {
        CastleSave.CurrentSlot = slot;
        CastleSave.Save(CastleSave.PathFor(slot));
    }

    public void LoadSlot(string slot)
    {
        StandDown();
        if (CastleSave.Load(CastleSave.PathFor(slot), splineParent, wallMaterial,
            palisadeMaterial))
            CastleSave.CurrentSlot = slot;
    }

    // What the keyboard means RIGHT NOW, for the HUD's modifier panel.
    // Every line below mirrors a key the current tool actually reads in
    // its current phase — a key that does nothing here is left off rather
    // than listed hopefully, which is the whole value of the panel. Any
    // new modifier (or any change to the scroll-wheel arbitration) has to
    // be added here in the same edit, or the HUD starts lying.
    public void ModifierHints(out string context, out string hints)
    {
        var lines = new List<string>();
        if (Mode == ToolMode.Delete)
        {
            context = "Delete";
            lines.Add("Drag over walls — paint a selection");
            lines.Add("Drag from open ground — marquee");
            if (deleteUndoJson != null)
                lines.Add("Right-click — undo the last delete");
            lines.Add("Esc — clear the selection");
        }
        else if (Mode == ToolMode.Land)
        {
            context = "Survey";
            lines.Add("Hover — read a parcel");
            lines.Add("Click frontier land — clear or buy it");
        }
        else if (Mode == ToolMode.Offset)
        {
            context = offsetSource == null ? "Offset · pick a wall" : "Offset · placing";
            if (offsetSource != null)
                lines.Add("Scroll — offset distance");
            lines.Add("Ctrl + scroll — wall height");
            if (offsetSource != null)
                lines.Add("Esc — release the source wall");
        }
        else if (Mode == ToolMode.Stair)
        {
            if (stairHost == null)
                context = "Stair · point at a wall";
            else if (stairBlocked)
                context = $"Stair · needs {stairShortfall:0.0}m more wall";
            else if (stairToDoor)
                context = "Stair · up to the doorway";
            else if (stairPerp)
                context = "Stair · out from the wall";
            else
                context = "Stair";
            lines.Add("Point at a wall's side — the stair fits it");
            lines.Add("Point at a raised doorway — steps up to its sill");
            if (stairHost != null)
                lines.Add("R — flip the climb / turn it out from the wall");
            // The climb is dialable now; the LENGTH still follows from it
            // and stays undialable — that's the no-refusal doctrine.
            lines.Add(stairRiseDial.HasValue
                ? $"Ctrl + scroll — climb height ({stairRiseDial.Value:0.0#}m)"
                : "Ctrl + scroll — climb height (the wall top)");
            lines.Add("Click — place it");
        }
        else if (Mode == ToolMode.Door)
        {
            // Nothing is dialable here either: the wall gives the doorway
            // its floor and its thickness, and width and head are fixed.
            if (doorHost == null)
                context = "Door · point at a wall";
            else if (doorBlocked)
                context = "Door · won't fit here";
            else if (doorStep >= DoorStepsNeeded)
                context = $"Door · sill {doorStep:0.0}m up, on the floor inside";
            else
                context = "Door";
            lines.Add("Point at a wall's side — the doorway sits there");
            lines.Add("Click — cut it through");
            lines.Add("Delete tool, click a doorway's stone — fill it in");
        }
        else if (Mode == ToolMode.Foundation)
        {
            context = slabChain.Count > 0
                ? $"Foundation · outline ({slabChain.Count})" : "Foundation";
            lines.Add(slabChain.Count == 0
                ? "Click — start the outline (snapping an edge extends its level)"
                : "Click — add a corner; close on the first corner to fill");
            lines.Add("Ctrl + scroll — plane up/down (¼m)");
            lines.Add("Alt — ignore snaps");
            if (slabChain.Count > 0)
            {
                lines.Add("Right-click — undo the last corner");
                lines.Add("Esc — abandon the outline");
            }
            lines.Add("Red — mostly buried, skirt too deep, or overlapping");
        }
        else if (Mode == ToolMode.Floor)
        {
            context = slabChain.Count > 0
                ? $"Floor · outline ({slabChain.Count})" : "Floor";
            if (slabChain.Count == 0)
            {
                lines.Add("Start on a wall top or a floor's edge — the plane is theirs");
            }
            else
            {
                lines.Add("Click — add a corner; close on the first corner to fill");
                lines.Add("Corners snap to wall corners at this plane");
                lines.Add("A stairwell is drawn AROUND — leave it out of the outline");
                lines.Add("Right-click — undo the last corner");
                lines.Add("Esc — abandon the outline");
            }
            lines.Add("Alt — ignore snaps");
        }
        else if (Mode == ToolMode.Ground)
        {
            // Grade is whatever you draw, and the deviation law is not
            // a setting.
            if (GroundTask == GroundAction.Restore)
            {
                context = restorePaintLast.HasValue ? "Ground · Restore · sweeping" : "Ground · Restore";
                lines.Add("Hold and sweep — paint ground back to the natural hill");
                lines.Add($"Ctrl + scroll — brush size ({restoreBrushRadius:0.0}m)");
                lines.Add("(stops short of standing walls)");
            }
            else
            {
                context = groundAnchor.HasValue ? "Ground · Path · drag" : "Ground · Path";
                lines.Add("Drag a line — bench a walkable path (the ground moves live)");
                lines.Add("Hold Shift — hold the anchor's height (a level path)");
                lines.Add($"Ctrl + scroll — path width ({pathWidth:0.0}m)");
                lines.Add("Blue — earth raised. Red — earth cut.");
                if (groundAnchor.HasValue)
                    lines.Add("Esc — cancel, the hill comes back");
                lines.Add($"(ground moves at most {TerrainPads.MaxDeviation:0}m from natural)");
            }
        }
        else if (Shape == BuildShape.Circle || Shape == BuildShape.Rect)
        {
            string tool = Mode == ToolMode.Room ? "Room" : "Wall";
            string figure = Shape == BuildShape.Circle ? "Circle" : "Rectangle";
            if (!shapeAnchor.HasValue)
            {
                context = $"{tool} · {figure} · anchor";
                if (Shape == BuildShape.Rect)
                    lines.Add("Ctrl + click — centre the first side here");
                lines.Add("Alt — free placement (no snapping)");
                lines.Add("Ctrl + scroll — wall height");
            }
            else if (Shape == BuildShape.Circle)
            {
                context = $"{tool} · {figure} · radius";
                lines.Add("Alt — free radius (no 0.5m steps)");
                lines.Add("Ctrl + scroll — wall height");
                lines.Add("Esc — cancel the figure");
            }
            else if (!shapeSideEnd.HasValue)
            {
                context = $"{tool} · {figure} · side";
                lines.Add(shapeCentered
                    ? "Shift — 5° steps off the anchor wall's face"
                    : "Shift — 5° direction steps");
                lines.Add("Alt — free direction and length");
                lines.Add("Ctrl + scroll — wall height");
                lines.Add("Esc — cancel the figure");
            }
            else
            {
                context = $"{tool} · {figure} · width";
                lines.Add("Shift — square it (width = side)");
                lines.Add("Alt — free width (no 0.5m steps)");
                lines.Add("Ctrl + scroll — wall height");
                lines.Add("Esc — cancel the figure");
            }
        }
        else if (Mode == ToolMode.Room)
        {
            bool chaining = roomChain.Count > 0;
            context = chaining ? "Room · chaining" : "Room";
            if (chaining)
                lines.Add("Shift — 5° steps from the last corner");
            lines.Add("Alt — free placement (no snapping or stepping)");
            lines.Add("Ctrl + scroll — wall height");
            if (chaining)
            {
                lines.Add("Right-click — undo the last corner");
                lines.Add("Esc — abandon the chain");
            }
        }
        else if (Shape == BuildShape.Curved)
        {
            // Stacking inherits the host's curve exactly, so the bulge
            // wheel is not offered — there is nothing left for it to bend.
            if (anchorRideHost != null)
            {
                context = "Wall · Curved · stacking on a wall";
                lines.Add("Move along the walls — it follows on past corners");
                lines.Add("Alt — leave the wall below (free placement)");
                lines.Add("Ctrl + scroll — wall height");
                lines.Add("Esc — cancel the wall");
            }
            else if (splineEnd.HasValue)
            {
                context = "Wall · Curved · bend";
                lines.Add("Scroll — curve bulge");
                lines.Add("Ctrl + scroll — wall height");
                lines.Add("Esc — cancel the wall");
            }
            else if (splineStart.HasValue)
            {
                context = "Wall · Curved · drag";
                lines.Add("Alt — free placement (no snapping)");
                lines.Add("Ctrl + scroll — wall height");
                lines.Add("Esc — cancel the wall");
            }
            else
            {
                context = "Wall · Curved";
                lines.Add("Alt — free placement (no snapping)");
                lines.Add("Ctrl + scroll — wall height");
            }
        }
        else if (splineStart.HasValue)
        {
            // Stacking has no free direction, so the keys that steer one
            // (Shift, C) are deliberately absent — the run can only slide
            // along the wall below.
            bool wallChaining = wallChainLegs.Count > 0;
            if (anchorRideHost != null)
            {
                context = wallChaining
                    ? "Wall · chaining on a wall top" : "Wall · stacking on a wall";
                lines.Add("Drag along the walls — it follows on past corners");
                lines.Add("Alt — leave the wall below (free placement)");
                lines.Add("Ctrl + scroll — wall height");
            }
            else
            {
                context = wallChaining ? "Wall · chaining" : "Wall · drag";
                lines.Add("Shift — 5° direction steps");
                lines.Add("C — tangent-tied connector (both ends snapped)");
                lines.Add("Alt — free placement (no snapping or stepping)");
                lines.Add("Ctrl + scroll — wall height");
            }
            // Each commit chains: the wall's end is already the next
            // anchor. Right-click walks the chain back leg by leg.
            lines.Add(wallChaining
                ? "Right-click — take back the last wall"
                : "Right-click — drop the anchor");
            lines.Add(wallChaining
                ? "Esc — end the chain (walls stay)" : "Esc — cancel the drag");
        }
        else if (hoverRideHost != null)
        {
            // R is absent on purpose: the stub takes the host's bearing.
            context = "Wall · on a wall top";
            lines.Add("Drag along the walls — raise a storey over them");
            lines.Add("Alt — leave the wall below (free placement)");
            lines.Add("Ctrl + scroll — wall height");
        }
        else
        {
            context = "Wall";
            lines.Add("R — turn the stub 45°");
            lines.Add("Alt — free placement (no snapping)");
            lines.Add("Ctrl + scroll — wall height");
        }
        hints = string.Join("\n", lines);
    }

    public void SetGroundTask(GroundAction task)
    {
        if (GroundTask == task)
            return;
        GroundTask = task;
        CancelGround();
    }

    public void SetShape(BuildShape shape)
    {
        if (Shape == shape)
            return;
        Shape = shape;
        CancelCurve();
        CancelShape();
        CancelRoomChain();
    }

    // The Survey: the countryside's parcels as a draped map, shown only
    // while this tool is in hand. Hover reads a parcel into the inspect
    // panel; a click on the frontier takes it — bandit land cleared, a
    // neighbor's bought — through the ordinary treasury door. Owned
    // land answers to nothing here yet; the manor dials come later.
    void UpdateLand(Mouse mouse, Ray ray, bool overUI)
    {
        LandPlot plot = null;
        if (!overUI)
        {
            float best = float.MaxValue;
            // Unbounded range: the survey is read from altitude, and a
            // 1000m cap silently dropped every far parcel's hover.
            foreach (RaycastHit h in Physics.RaycastAll(ray, float.PositiveInfinity))
            {
                if (h.collider.isTrigger || h.distance >= best)
                    continue;
                LandPlot p = h.collider.GetComponent<LandPlot>();
                if (p != null)
                {
                    best = h.distance;
                    plot = p;
                }
            }
        }
        if (hoveredPlot != null && hoveredPlot != plot)
            hoveredPlot.SetHighlight(false);
        hoveredPlot = plot;
        if (plot == null)
        {
            landInspect = null;
            return;
        }
        plot.SetHighlight(true);

        string kind = plot.type switch
        {
            LandType.Farm => "Farmland",
            LandType.Pasture => "Pasture",
            LandType.Timber => "Timber",
            LandType.Castle => "Castle grounds",
            _ => "Village land",
        };
        float acres = plot.AreaM2 / 4047f;
        // The frontier rule gates bandit CLEARING only — buying a
        // neighbor's parcel is negotiation, not conquest, and with
        // lordships kilometres apart adjacency would forbid it forever
        // (Docs/Territories.md).
        bool frontier = plot.owner != LandOwner.Bandits
            || LandParcels.TouchesPlayerLand(plot);
        // Castle grounds change hands with the castle, never by sale —
        // the hover says so and the click refuses on the same test.
        bool castleGrounds = plot.type == LandType.Castle
            && plot.owner != LandOwner.Player;
        landInspect = plot.owner switch
        {
            LandOwner.Player =>
                $"{kind} · {acres:0.0} acres · yours —"
                + (plot.type == LandType.Castle ? " build here"
                    : $" {Estate.Format(Estate.AnnualYieldOf(plot))} a year"),
            LandOwner.Bandits =>
                $"{kind} · {acres:0.0} acres · bandit-held — clear for"
                + $" {Estate.Format(Estate.PriceOf(plot))}"
                + (frontier ? "" : " (beyond your borders)"),
            _ when castleGrounds =>
                $"{kind} · {acres:0.0} acres ·"
                + $" {LandParcels.TerritoryName(plot.territory)}'s —"
                + " changes hands with the castle",
            _ =>
                $"{kind} · {acres:0.0} acres ·"
                + $" {LandParcels.TerritoryName(plot.territory)}'s — buy for"
                + $" {Estate.Format(Estate.PriceOf(plot))}",
        };

        if (mouse.leftButton.wasPressedThisFrame
            && plot.owner != LandOwner.Player)
        {
            if (castleGrounds)
            {
                BuildLog.Add("Refused: a castle's grounds change hands"
                    + " with the castle.");
                return;
            }
            // The frontier rule, commit-side: the same adjacency the
            // hover line warns about (one-test doctrine).
            if (!frontier)
            {
                BuildLog.Add("Refused: the frontier moves outward from"
                    + " your own lands.");
                return;
            }
            Estate.Pay(Estate.PriceOf(plot), plot.owner == LandOwner.Bandits
                ? $"Drove the bandits off {acres:0.0} acres of {kind.ToLower()}"
                : $"Bought {acres:0.0} acres of {kind.ToLower()}");
            plot.owner = LandOwner.Player;
            plot.Retint();
        }
    }

    void UpdateBuild(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        HandleHeightScroll(mouse, keyboard);
        // Re-established every frame by whichever preview is actually
        // riding — an unclaimed frame means the ghost is free.
        previewRideHost = null;

        if (Shape == BuildShape.Curved)
            UpdateCurvedBuild(mouse, keyboard, ray, overUI);
        else
            UpdateStraightBuild(mouse, keyboard, ray, overUI);
    }

    // Straight walls: press, drag along a length-stepped line, release to
    // commit — the commit is a graph edit, so snapped ends join their
    // nodes, a far end on a wall face T-splits the host, and any wall the
    // drag crosses is auto-split into a shared 4-way node (posts preview
    // gold at the crossing points). Holding C with both ends snapped opts
    // into the tangent-tied connector (TryTangentControl) — the wall
    // auto-curves to tie in flush at both hosts; Shift means angle
    // stepping here as it does everywhere. An existing wall's top
    // face is ground like any other: the gesture builds the storey above
    // it. R turns the single-click stub's orientation.
    void UpdateStraightBuild(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelCurve();
            return;
        }
        // Right click unwinds the chain one leg; with nothing committed
        // yet it drops the anchor instead. Guarded on a live gesture so a
        // stale history (chain ended by Esc) can never fire.
        if (rightClick && splineStart.HasValue)
        {
            if (wallChainLegs.Count > 0)
                UndoWallChainLeg();
            else
                CancelCurve();
            return;
        }
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            currentYRotation += 45f;

        bool freePlace = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        Vector3 point = default;
        Vector3 rawSurface = default;
        bool hasPoint = !overUI && TryGetCurveTarget(ray, freePlace, out point, out rawSurface);

        if (!splineStart.HasValue)
        {
            if (hasPoint)
            {
                snapTint = hoverSnapYaw.HasValue;
                if (hoverSnap.HasValue && hoverSnap.Value.node != null)
                {
                    ShowNodeEndHighlight(hoverSnap.Value.node);
                }
                else if (hoverRideHost != null)
                {
                    // On a wall top the stub is a piece of the wall below,
                    // whatever else the point snapped to — a flank landing
                    // up here still cannot leave the masonry carrying it.
                    BuildRideStubPreview(hoverRideHost, point);
                    ShowSplineGhosts();
                }
                else if (hoverSnap.HasValue)
                {
                    BuildSnappedStubPreview(hoverSnap.Value, point);
                    ShowSplineGhosts();
                }
                else
                {
                    BuildStubPreview(point, hoverSnapYaw ?? currentYRotation);
                    ShowSplineGhosts();
                }
                guideCenter = point;
                guideBearing = -(hoverSnapYaw ?? currentYRotation);
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    // A fresh anchor starts a fresh chain — any history
                    // left from a chain Esc ended is dead now.
                    wallChainLegs.Clear();
                    splineStart = point;
                    anchorSnap = hoverSnap;
                    anchorSnapYaw = hoverSnapYaw;
                    anchorSnapHeight = hoverSnapHeight;
                    anchorBaseY = hoverBaseY;
                    anchorRideHost = hoverRideHost;
                }
            }
            else
            {
                HideGhosts();
            }
        }
        else
        {
            // Off-target frames (HUD, sky) keep the last stepped end — the
            // preview stays visible and release commits exactly what it
            // shows, instead of getting stuck dragging.
            bool snapAngle = keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
            bool tangentKey = keyboard != null && keyboard.cKey.isPressed;
            bool steppedAim = !freePlace && snapAngle;
            // A property of the gesture, not of the frame: off-target
            // frames (HUD, sky) keep showing the ridden curve instead of
            // snapping back to a straight chord.
            previewRideHost = !freePlace ? anchorRideHost : null;
            if (hasPoint)
            {
                // The far end snaps to nodes first, so closing a loop
                // lands exactly on the corner it started from — the snap
                // outranks length stepping while it holds. Failing that, a
                // wall's centerline grabs the end: the wall terminates
                // there and the commit T-splits the host, at any angle.
                // Under Shift the bearing outranks that grab — same rule
                // as the room chain — and the landing is found ON the
                // stepped ray instead (TrySteppedLanding), so a stepped
                // drag can still end exactly on the masonry.
                EndSnap flankSnap = default;
                bool flankInReach = !freePlace
                    && TryFlankSnap(rawSurface, splineStart, anchorBaseY, out flankSnap);
                // Anchored on a wall top: the run is a SPAN of the masonry
                // below, never a free direction. The end slides along that
                // host's centerline whatever the cursor is over — and
                // THROUGH its end nodes onto whatever carries on, so one
                // drag can raise a storey over a whole tower or room.
                // Shift, C and the bulge wheel have nothing to act on
                // here; Alt is the way out.
                if (anchorRideHost != null && !freePlace)
                {
                    splineEnd = RideDragEnd(anchorRideHost, splineStart.Value, rawSurface,
                        out EndSnap? rideMet);
                    dragEndSnap = rideMet;
                }
                else if (!freePlace && TryNodeSnap(rawSurface, splineStart, anchorBaseY, out EndSnap endSnap))
                {
                    splineEnd = endSnap.point;
                    dragEndSnap = endSnap;
                }
                else if (flankInReach && !steppedAim)
                {
                    splineEnd = flankSnap.point;
                    dragEndSnap = flankSnap;
                }
                // The slab rim grabs the far end too, so a drag along a
                // foundation edge lands corner-miter to corner-miter and
                // the run lies exactly on the inset line — face flush with
                // the slab edge. Like the flank, the rim yields to Shift's
                // stepped bearing; the snap outranks length stepping while
                // it holds, exactly as a node snap does.
                else if (!freePlace && !steppedAim && hoverSlabTile != null
                    && TrySlabRimSnap(hoverSlabTile, rawSurface, out Vector3 rimEnd, out _))
                {
                    splineEnd = rimEnd;
                    dragEndSnap = null;
                }
                else
                {
                    splineEnd = SteppedDragEnd(splineStart.Value, rawSurface, freePlace, snapAngle);
                    dragEndSnap = null;
                    // A snapped-anchor drag is fully free by default;
                    // holding Shift steps its direction RELATIVE to the
                    // anchor's own masonry (nearest member run at a node,
                    // host arms on a flank), so a lazy Shift-drag off a
                    // face lands exactly 90° flush and stepped corners
                    // read clean multiples even off the world grid.
                    if (steppedAim && anchorSnap.HasValue)
                    {
                        Vector3 d = splineEnd.Value - splineStart.Value;
                        d.y = 0f;
                        if (d.sqrMagnitude > 0.000001f)
                        {
                            float dragB = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
                            splineEnd = StepBearingFrom(
                                AngleRefBearing(anchorSnap.Value, dragB),
                                splineStart.Value, splineEnd.Value);
                        }
                    }
                    if (steppedAim && flankInReach
                        && TrySteppedLanding(splineStart.Value, splineEnd.Value, anchorBaseY,
                            out Vector3 landing, out EndSnap landSnap))
                    {
                        splineEnd = landing;
                        dragEndSnap = landSnap;
                    }
                    else
                    {
                        AddExtensionAlignGuides(splineEnd.Value);
                    }
                }

                // The old never-cross-into-a-building landing, the floor
                // step and the partition tell died with derived rooms
                // (Docs/BuildingRebuild.md): buildings stand on foundation
                // slabs now, so their walls live on a slab storey the
                // terrain gesture's planar graph never meets.
            }

            Vector3 anchor = splineStart.Value;
            Vector3 end = splineEnd ?? anchor;
            // Ends default to corner nodes on a straight wall — all the
            // turning lives in the posts. Holding C with both ends snapped
            // opts into the tangent-tied connector instead: the wall
            // auto-curves to leave one host and arrive at the other
            // straight-on, both joints flush end-on fuses. (It lived on
            // Shift until Shift was made to mean angle stepping and
            // nothing else, in the wall tool and the room chain alike.)
            float dragBulge = 0f, dragApexT = 0.5f;
            bool tangentTied = anchorSnap.HasValue && dragEndSnap.HasValue && tangentKey
                && TryTangentControl(anchor, end, out dragBulge, out dragApexT);
            if (!tangentTied)
            {
                dragBulge = 0f;
                dragApexT = 0.5f;
            }
            bool highlightOnly = false;
            if (previewRideHost != null && rideSpans.Count > 0)
            {
                // A ride is one edge per host, so it draws its own way —
                // there is no single chord to trim or bulge.
                BuildRidePreview(anchor, end);
                ShowPendingJointGhosts(anchor, end);
            }
            else if (FlatDistance(anchor, end) >= 0.25f)
            {
                BuildSplinePreview(anchor, end, dragBulge, dragApexT,
                    TrimInto(anchorSnap, anchor, end), TrimInto(dragEndSnap, end, anchor));
                ShowPendingJointGhosts(anchor, end);
            }
            else if (anchorSnap.HasValue && anchorSnap.Value.node != null)
            {
                // Barely-moved node anchor: still just the joint
                // highlight — releasing here commits nothing.
                ShowNodeEndHighlight(anchorSnap.Value.node);
                highlightOnly = true;
            }
            else if (previewRideHost != null)
            {
                BuildRideStubPreview(previewRideHost, anchor);
            }
            else if (anchorSnap.HasValue)
            {
                BuildSnappedStubPreview(anchorSnap.Value, anchor);
            }
            else
            {
                BuildStubPreview(anchor, anchorSnapYaw ?? currentYRotation);
            }
            snapTint = dragEndSnap.HasValue;
            if (!highlightOnly)
                ShowSplineGhosts();
            guideCenter = (anchor + end) * 0.5f;
            guideBearing = FlatDistance(anchor, end) > 0.01f
                ? Mathf.Atan2(end.z - anchor.z, end.x - anchor.x) * Mathf.Rad2Deg
                : -(anchorSnapYaw ?? currentYRotation);
            // The corner-angle readout only means something for a kinked
            // joint — a tangent-tied connector has no corner by design.
            if (anchorSnap.HasValue && !tangentTied && FlatDistance(anchor, end) >= 0.25f)
                activeAngle = (anchor,
                    AngleRefBearing(anchorSnap.Value, guideBearing), guideBearing);

            if (mouse.leftButton.wasReleasedThisFrame)
                CommitSpline();
        }
    }

    // Gold post ghosts at the joints the commit will CREATE mid-run:
    // auto-split crossing points. Snapped ends show no posts — joining
    // existing masonry is signalled by the node highlight and the gold
    // tint, not by new markers.
    void ShowPendingJointGhosts(Vector3 anchor, Vector3 end)
    {
        foreach (WallGraph.Crossing x in previewCrossings)
            AddNodeGhost(x.point);
    }

    // A plain click still means a wall: a short stub chord through the
    // clicked point, oriented by R — the smallest edge the graph can
    // hold, with a node at each end.
    void BuildStubPreview(Vector3 center, float yawDegrees)
    {
        float rad = yawDegrees * Mathf.Deg2Rad;
        Vector3 halfChord = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad)) * (StubLength * 0.5f);
        BuildSplinePreview(center - halfChord, center + halfChord, 0f, 0.5f);
    }

    // The stub on a wall top is a SPAN of that wall, so it cannot hang off
    // the end: it slides INWARD to fit instead of straddling the cap.
    // (A centred stub on a point pulled to the host's own end overhung by
    // half its length — half a thickness of masonry in mid-air.) A host
    // shorter than one stub is raised end to end.
    void BuildRideStubPreview(WallEdge host, Vector3 point)
    {
        previewRideHost = host;
        float[] table = WallEdge.BuildArcTable(host.A, host.control, host.B);
        float total = table[table.Length - 1];
        float span = Mathf.Min(StubLength, total);
        float at = WallEdge.ArcAt(table, host.NearestT(point, out _));
        float from = Mathf.Clamp(at - span * 0.5f, 0f, total - span);
        WallGraph.SubCurve(host.A, host.control, host.B,
            WallEdge.TAtArc(table, from), WallEdge.TAtArc(table, from + span),
            out Vector3 s, out _, out Vector3 e);
        BuildSplinePreview(new Vector3(s.x, 0f, s.z), new Vector3(e.x, 0f, e.z), 0f, 0.5f);
    }

    // The ghost for a snapped hover: a stub chord LEAVING the snap point
    // along its outward direction, its buried half trimmed so the ghost
    // sits wholly outside the host — flush with the face, while the
    // commit still anchors on the joint itself.
    void BuildSnappedStubPreview(EndSnap snap, Vector3 point)
    {
        Vector3 end = point + snap.outDir * (StubLength + snap.stubTrim);
        BuildSplinePreview(point, end, 0f, 0.5f, snap.stubTrim, 0f);
    }

    // Hovering a node shows no new ghost at all: the joint already
    // exists as masonry, so each member wall's END SECTION highlights
    // gold instead — you are continuing the wall from this node, and the
    // drag can leave at any angle. Clears the pending gesture, so a bare
    // click-release on a node commits nothing.
    void ShowNodeEndHighlight(WallNode node)
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        previewBlocked = false;
        previewCrossings.Clear();
        lastPreviewKey = null;
        lastRideKey = null;
        pendingCommits.Clear();

        int count = 0;
        foreach (WallNode.Member m in node.members)
        {
            if (m.edge == null)
                continue;
            var sections = m.edge.SectionsInOrder();
            if (sections.Count == 0)
                continue;
            WallEdgeSection end = m.atStart ? sections[0] : sections[sections.Count - 1];
            GameObject ghost = GetPooled(ghostPool, count++, snapColor, "PlacementPreview");
            ghost.SetActive(true);
            MeshFilter filter = end.GetComponent<MeshFilter>();
            SetGhostMesh(ghost, filter != null ? filter.sharedMesh : null);
            SetGhostTint(ghost, snapColor);
            FitShell(ghost.transform, end.transform, filter != null ? filter.sharedMesh : null);
        }
        // The node's own post glows too — without it the joint reads as
        // two separate highlighted pieces with a dark seam between them,
        // as if two nodes overlapped there.
        MeshFilter postFilter = node.GetComponent<MeshFilter>();
        if (postFilter != null && postFilter.sharedMesh != null)
        {
            GameObject ghost = GetPooled(ghostPool, count++, snapColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, postFilter.sharedMesh);
            SetGhostTint(ghost, snapColor);
            FitShell(ghost.transform, node.transform, postFilter.sharedMesh);
        }
        for (int i = count; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // How much of the pending wall's display to trim at an end that
    // lands on a flank: the run from the host's centerline to where the
    // wall's own line exits the host's face. Display only — the commit
    // keeps the full curve so the graph can split the host at the
    // centerline. Oblique exits reach further; capped so a near-parallel
    // drag doesn't eat the whole preview.
    float TrimInto(EndSnap? snap, Vector3 from, Vector3 toward)
    {
        if (!snap.HasValue || !snap.Value.isFlank || snap.Value.edge == null)
            return 0f;
        Vector3 dir = toward - from;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
            return 0f;
        dir.Normalize();
        float half = snap.Value.edge.thickness * 0.5f;
        float along = Mathf.Abs(dir.x * snap.Value.outDir.x + dir.z * snap.Value.outDir.z);
        return Mathf.Min(half * 2f, half / Mathf.Max(0.35f, along));
    }

    // C held with both drag ends snapped: solve the quadratic control
    // point that makes the connector LEAVE the anchor's host straight-on
    // and ARRIVE at the far host straight-on — the intersection of the
    // two snaps' departure rays. Each joint then meets its host collinear
    // (a flush butt at the node) instead of turning at a corner post, for
    // the deliberate sweeping run between towers. False — stay straight —
    // when either snap is a busy node (no single outward direction), the
    // tangents are parallel (an S no single quadratic can make), diverge,
    // or demand an extreme curve.
    bool TryTangentControl(Vector3 anchor, Vector3 end, out float bulge, out float apexT)
    {
        bulge = 0f;
        apexT = 0.5f;
        if (anchorSnap.Value.node != null && anchorSnap.Value.node.Valence > 1)
            return false;
        if (dragEndSnap.Value.node != null && dragEndSnap.Value.node.Valence > 1)
            return false;
        Vector3 d0 = anchorSnap.Value.outDir;
        Vector3 d1 = dragEndSnap.Value.outDir;
        if (d0.sqrMagnitude < 0.5f || d1.sqrMagnitude < 0.5f)
            return false;
        Vector3 delta = end - anchor;
        delta.y = 0f;
        float chordLen = delta.magnitude;
        if (chordLen < 0.25f)
            return false;

        // anchor + u*d0 = end + v*d1, both u and v ahead of their ends.
        float det = d0.x * d1.z - d0.z * d1.x;
        if (Mathf.Abs(det) < 0.02f)
            return false;
        float u = (delta.x * d1.z - delta.z * d1.x) / det;
        float v = (delta.x * d0.z - delta.z * d0.x) / det;
        if (u < 0.1f || v < 0.1f || u > 3f * chordLen || v > 3f * chordLen)
            return false;

        // Decompose the control point (anchor + u*d0) into the
        // (apexT, bulge) coordinates the preview/commit pipeline speaks.
        Vector3 w = d0 * u;
        Vector3 normal = new Vector3(-delta.z, 0f, delta.x) / chordLen;
        apexT = (w.x * delta.x + w.z * delta.z) / (chordLen * chordLen);
        bulge = (w.x * normal.x + w.z * normal.z) * 0.5f;
        return Mathf.Abs(bulge) <= 0.75f * chordLen;
    }

    // Drag assist: direction is freeform (Shift snaps it to angleStep
    // bearings) while length steps in lengthStep increments — relative to
    // the anchor, not to any world grid, so strokes stay commensurable
    // without constraining where they start. Alt frees everything.
    Vector3 SteppedDragEnd(Vector3 anchor, Vector3 target, bool free, bool snapAngle)
    {
        Vector3 delta = target - anchor;
        delta.y = 0f;
        float length = delta.magnitude;
        if (length < 0.001f)
            return anchor;

        float angle = Mathf.Atan2(delta.z, delta.x) * Mathf.Rad2Deg;
        if (!free)
        {
            if (snapAngle)
                angle = Mathf.Round(angle / angleStep) * angleStep;
            length = Mathf.Round(length / lengthStep) * lengthStep;
        }
        float rad = angle * Mathf.Deg2Rad;
        return anchor + new Vector3(Mathf.Cos(rad) * length, 0f, Mathf.Sin(rad) * length);
    }

    // Ctrl+scroll slides the wall height smoothly for subsequent placements
    // and the live preview. FreeFlyCamera stands down from scroll-dollying
    // while Ctrl is held, so the wheel only means one thing at a time.
    void HandleHeightScroll(Mouse mouse, Keyboard keyboard)
    {
        if (keyboard == null || (!keyboard.leftCtrlKey.isPressed && !keyboard.rightCtrlKey.isPressed))
            return;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f)
            return;

        // Windows reports raw ±120 per wheel notch; smaller magnitudes
        // (normalized notches, trackpad glides) already mean notches.
        if (Mathf.Abs(scroll) >= 100f)
            scroll /= 120f;
        float notches = Mathf.Clamp(scroll, -4f, 4f);
        heightMultiplier = Mathf.Clamp(
            heightMultiplier + notches * (heightScrollRate / BaseHeight),
            minHeightMultiplier, maxHeightMultiplier);
        // Scrolling is an explicit height choice — it beats any active
        // snap height match until that context ends.
        heightOverride = true;
    }

    // Curved wall placement. Three clicks: start point, end point (both
    // snapping to nodes and wall flanks first; Alt places freely), then
    // bend — plain scroll steps the bulge while the cursor drags the apex
    // along the chord — and a final click commits. An existing wall's top
    // face is ground like any other: the gesture builds the storey above
    // it. Escape backs out at any stage.
    void UpdateCurvedBuild(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelCurve();
            return;
        }

        bool freePlace = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        Vector3 point = default;
        Vector3 rawSurface = default;
        bool hasPoint = !overUI && TryGetCurveTarget(ray, freePlace, out point, out rawSurface);
        // The ride outlives the frame here too: the bend phase keeps the
        // host's curve rather than handing the wheel a curve to bend.
        if (splineStart.HasValue && !freePlace)
            previewRideHost = anchorRideHost;

        if (!splineStart.HasValue)
        {
            if (hasPoint)
            {
                snapTint = hoverSnapYaw.HasValue;
                if (hoverSnap.HasValue && hoverSnap.Value.node != null)
                {
                    ShowNodeEndHighlight(hoverSnap.Value.node);
                }
                else if (hoverSnap.HasValue)
                {
                    BuildSnappedStubPreview(hoverSnap.Value, point);
                    ShowSplineGhosts();
                }
                else
                    ShowStartMarker(point);
                guideCenter = point;
                guideBearing = -(hoverSnapYaw ?? 0f);
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    // A fresh anchor starts a fresh chain — any history
                    // left from a chain Esc ended is dead now.
                    wallChainLegs.Clear();
                    splineStart = point;
                    anchorSnap = hoverSnap;
                    anchorSnapYaw = hoverSnapYaw;
                    anchorSnapHeight = hoverSnapHeight;
                    anchorBaseY = hoverBaseY;
                    anchorRideHost = hoverRideHost;
                }
            }
            else
            {
                HideGhosts();
            }
        }
        else if (!splineEnd.HasValue)
        {
            // Chord phase: a straight wall from the anchor to the cursor.
            // A fresh click or a drag-release far enough away fixes the
            // far end; off-target frames keep the last preview.
            if (hasPoint)
            {
                // The hover's flank pick chose its outward side by cursor
                // (the anchor gesture); a far end must take the side the
                // ANCHOR is on — re-derive with the anchor picking.
                EndSnap? chordSnap = hoverSnap;
                if (chordSnap.HasValue && chordSnap.Value.isFlank)
                    chordSnap = null;
                // Same rule as the straight tool: a chord anchored on a
                // wall top can only run along that wall.
                if (anchorRideHost != null && !freePlace)
                {
                    point = RideDragEnd(anchorRideHost, splineStart.Value, rawSurface,
                        out EndSnap? rideMet);
                    chordSnap = rideMet;
                }
                else if (!freePlace && !chordSnap.HasValue
                    && TryFlankSnap(rawSurface, splineStart, anchorBaseY, out EndSnap chordFlank))
                {
                    chordSnap = chordFlank;
                    point = chordFlank.point;
                }
                if (previewRideHost != null && rideSpans.Count > 0)
                    BuildRidePreview(splineStart.Value, point);
                else
                    BuildSplinePreview(splineStart.Value, point, 0f, 0.5f,
                        TrimInto(anchorSnap, splineStart.Value, point),
                        TrimInto(chordSnap, point, splineStart.Value));
                dragEndSnap = chordSnap;
                ShowPendingJointGhosts(splineStart.Value, point);
                snapTint = hoverSnapYaw.HasValue || (chordSnap.HasValue && chordSnap.Value.isFlank);
                ShowSplineGhosts();
                guideCenter = (splineStart.Value + point) * 0.5f;
                guideBearing = Mathf.Atan2(point.z - splineStart.Value.z, point.x - splineStart.Value.x) * Mathf.Rad2Deg;
                if (anchorSnap.HasValue && FlatDistance(splineStart.Value, point) >= 0.25f)
                    activeAngle = (splineStart.Value,
                        AngleRefBearing(anchorSnap.Value, guideBearing), guideBearing);
                Vector3 flat = point - splineStart.Value;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.25f
                    && (mouse.leftButton.wasPressedThisFrame || mouse.leftButton.wasReleasedThisFrame))
                {
                    splineEnd = point;
                    dragEndSnap = chordSnap;
                }
            }
        }
        else
        {
            bool ctrlHeld = keyboard != null && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
            if (!ctrlHeld)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    float chordLength = Vector3.Distance(splineStart.Value, splineEnd.Value);
                    curveBulge = Mathf.Clamp(
                        curveBulge + Mathf.Sign(scroll) * curveBulgeStep,
                        -0.75f * chordLength, 0.75f * chordLength);
                }
            }

            // The cursor drags the apex along the chord. Its distance off
            // the chord is deliberately ignored — that axis belongs to the
            // stepped scroll bulge, which is what keeps curves repeatable.
            if (hasPoint)
            {
                Vector3 chord = splineEnd.Value - splineStart.Value;
                float t = Vector3.Dot(rawSurface - splineStart.Value, chord.normalized) / chord.magnitude;
                curveApexT = Mathf.Clamp(t, 0.15f, 0.85f);
            }

            // A ride settled its shape in the chord phase — the bend keys
            // have nothing to act on, so the spans just redraw.
            if (previewRideHost != null && rideSpans.Count > 0)
                BuildRidePreview(splineStart.Value, splineEnd.Value);
            else
                BuildSplinePreview(splineStart.Value, splineEnd.Value, curveBulge, curveApexT,
                    TrimInto(anchorSnap, splineStart.Value, splineEnd.Value),
                    TrimInto(dragEndSnap, splineEnd.Value, splineStart.Value));
            ShowPendingJointGhosts(splineStart.Value, splineEnd.Value);
            ShowSplineGhosts();
            guideCenter = (splineStart.Value + splineEnd.Value) * 0.5f;
            guideBearing = Mathf.Atan2(splineEnd.Value.z - splineStart.Value.z,
                splineEnd.Value.x - splineStart.Value.x) * Mathf.Rad2Deg;
            if (anchorSnap.HasValue)
                activeAngle = (splineStart.Value,
                    AngleRefBearing(anchorSnap.Value, guideBearing), guideBearing);

            if (mouse.leftButton.wasPressedThisFrame)
                CommitSpline();
        }
    }

    // Commits exactly what the ghost shows: the preview's own curve keys,
    // handed to the graph — which resolves nodes, splits crossed hosts,
    // and chains the edges. Straight drags, curves, and stubs all share
    // this one commit path; a ride across several walls arrives here as
    // one entry per wall, committed in order so consecutive spans meet at
    // a shared node.
    void CommitSpline()
    {
        var created = new List<WallEdge>();
        float chainHeight = 0f;
        if (!previewBlocked && splineSpecs.Count > 0 && pendingCommits.Count > 0)
        {
            foreach ((Vector3 s, Vector3 c, Vector3 e, float height, float topY, float baseY,
                float skirt) in pendingCommits)
            {
                created.AddRange(WallGraph.CommitEdge(splineParent, s, c, e,
                    EdgeParamsNow(height, topY, baseY, skirt)));
                chainHeight = height;
            }
        }
        Estate.Pay(Estate.CostOfEdges(created), Estate.KindLabel(CurrentWallKind));

        // The straight wall tool CHAINS like the room chain: the commit
        // re-anchors the gesture on its own end node, so the next click
        // grows the run — same storey, same height, ride re-locked if the
        // chain stands on masonry. Esc ends the chain; a right click
        // takes the last leg back. Curved keeps its three-click gesture.
        if (created.Count > 0 && Mode == ToolMode.Build && Shape == BuildShape.Straight)
        {
            wallChainLegs.Add(new WallChainLeg
            {
                start = splineStart.Value,
                baseY = anchorBaseY,
                height = chainHeight,
                created = created,
            });
            // Sub-edges chain from the gesture's anchor toward its end,
            // so the last created edge's B node IS the end joint.
            WallNode endNode = created[created.Count - 1].nodeB;
            float? chainBase = anchorBaseY;
            CancelCurve();
            splineStart = new Vector3(endNode.point.x, 0f, endNode.point.z);
            anchorBaseY = chainBase;
            anchorSnapHeight = chainHeight;
            anchorSnap = MakeNodeSnap(endNode, chainBase);
            anchorRideHost = chainBase.HasValue
                ? RideHostUnder(splineStart.Value, chainBase.Value) : null;
            return;
        }
        wallChainLegs.Clear();
        CancelCurve();
    }

    // Unwinds the last chain leg: its committed edges come back out of
    // the graph (hosts it split stay split — a collinear 2-valent node
    // renders nothing) and the gesture re-anchors where that leg began.
    // With no legs left the anchor itself is kept, so one more right
    // click can drop it.
    void UndoWallChainLeg()
    {
        WallChainLeg leg = wallChainLegs[wallChainLegs.Count - 1];
        wallChainLegs.RemoveAt(wallChainLegs.Count - 1);
        // The leg's price comes back in full — an undo is not a salvage.
        Estate.Refund(Estate.CostOfEdges(leg.created), "Leg undone");
        foreach (WallEdge e in leg.created)
        {
            if (e == null)
                continue;
            var doomed = new HashSet<int>();
            foreach (WallEdgeSection sec in e.SectionsInOrder())
                doomed.Add(sec.index);
            WallGraph.DeleteSections(splineParent, e, doomed);
        }
        CancelCurve();
        splineStart = leg.start;
        anchorBaseY = leg.baseY;
        anchorSnapHeight = leg.height;
        WallNode node = WallNode.FindAt(leg.start,
            leg.baseY ?? float.NegativeInfinity);
        if (node != null)
            anchorSnap = MakeNodeSnap(node, leg.baseY);
        anchorRideHost = leg.baseY.HasValue
            ? RideHostUnder(leg.start, leg.baseY.Value) : null;
    }

    WallGraph.EdgeParams EdgeParamsNow(float height, float topY,
        float baseY = float.NegativeInfinity, float skirt = 0f)
    {
        return new WallGraph.EdgeParams
        {
            height = height,
            thickness = gridSize,
            baseWallHeight = BaseHeight,
            targetSectionLength = gridSize,
            baseStep = baseStepSize,
            fixedTopY = topY,
            baseY = baseY,
            skirt = skirt,
            material = MaterialForKind(CurrentWallKind),
            kind = CurrentWallKind,
        };
    }

    // Cursor surface for the room/shape tools: the first non-trigger hit
    // flattened, plus the slab top when that hit is a room's roof deck or
    // floor — elevated ground the gesture builds on.
    bool TryGetBuildSurface(Ray ray, bool freePlace, out Vector3 point, out float? deckY)
    {
        point = default;
        deckY = null;
        hoverRideHost = null;
        hoverSnapBase = null;
        hoverSlabTile = null;
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            point = new Vector3(hit.point.x, 0f, hit.point.z);
            // A slab tile is elevated ground: building on it is
            // free-form (no ride lock). A terrain hit within rim reach
            // of an outline adopts the slab the same way — a near-grade
            // pad exposes no skirt to catch the ray just past its rim
            // (RimSlabFromTerrain has the why); Alt keeps terrain free.
            SlabTile slab = hit.collider.GetComponent<SlabTile>();
            if (slab == null && !freePlace && hit.collider is TerrainCollider)
                slab = RimSlabFromTerrain(point, hit.point.y);
            if (slab != null)
            {
                deckY = slab.topY;
                hoverSnapBase = deckY;
                hoverSlabTile = slab;
                return true;
            }
            // A wall's TOP FACE is elevated ground here too: the room and
            // shape tools raise a storey over existing masonry exactly the
            // way the wall tool does. Without this, only the wall tool saw
            // wall tops and a room could still only be inset onto a deck.
            WallEdgeSection section = hit.collider.GetComponent<WallEdgeSection>();
            if (section != null && hit.normal.y > 0.7f && section.edge != null
                && !CutawayView.IsCutSurface(hit.point))
            {
                deckY = section.topY;
                hoverRideHost = section.edge;
                point = RideOnto(section.edge, point);
            }
            // The ray names the storey even on a side hit — see
            // TryGetCurveTarget, same reason: a stack is a snap tie.
            hoverSnapBase = deckY;
            if (!deckY.HasValue && section != null && section.edge != null)
                hoverSnapBase = section.edge.baseY;
            return true;
        }
        return false;
    }

    void CancelCurve()
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        previewBlocked = false;
        previewCrossings.Clear();
        lastPreviewKey = null;
        lastRideKey = null;
        pendingCommits.Clear();
        lastOffsetKey = null;
        splineStart = null;
        splineEnd = null;
        curveBulge = 0f;
        curveApexT = 0.5f;
        anchorSnap = null;
        anchorSnapYaw = null;
        anchorSnapHeight = null;
        anchorBaseY = null;
        anchorRideHost = null;
        rideSpans.Clear();
        heightOverride = false;
        dragEndSnap = null;
        pendingNodeGhosts.Clear();
        HideGhosts();
    }

    // Circle and Rect: whole-figure gestures shared by the wall and room
    // tools. Press plants the anchor (circle center / first corner), the
    // drag sizes the figure — radius and sides step in lengthStep, Shift
    // squares the rectangle, Alt frees — and release commits every edge
    // through the graph (splitting whatever they cross, gold posts
    // previewing the joints). With the Room tool in hand the closed
    // figure designates its face as a room in the same act.
    void UpdateShapeGesture(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI, bool designate)
    {
        HandleHeightScroll(mouse, keyboard);
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelShape();
            return;
        }

        bool freePlace = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        // Shift's meaning follows the phase: 5° direction steps while
        // laying the rect's first side, the square constraint while
        // pulling its width.
        bool shiftHeld = keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        Vector3 raw = default;
        float? deckY = null;
        bool hasPoint = !overUI && TryGetBuildSurface(ray, freePlace, out raw, out deckY);
        // Snapping works on decks too, filtered to that deck's own walls
        // — the masonry a storey down never grabs the gesture.
        bool canSnap = !freePlace;
        hoverBaseY = deckY;

        if (!shapeAnchor.HasValue)
        {
            if (!hasPoint)
            {
                HideGhosts();
                return;
            }
            // The anchor snaps like any wall end: onto a node, or onto a
            // host's centerline — so a rect drawn against a wall starts
            // ON the wall's line, not half a thickness off on its face.
            // The snapped wall's height AND storey are adopted, and the
            // hover borrows the wall tool's presentation (highlight or
            // face-flush stub) instead of burying a ghost box in the host.
            Vector3 point = raw;
            EndSnap? attach = null;
            if (canSnap && TryNodeSnap(raw, null, hoverSnapBase, out EndSnap nodeSnap))
            {
                attach = nodeSnap;
                point = nodeSnap.point;
                if (nodeSnap.edge != null)
                    hoverSnapHeight = SnapHeightOf(nodeSnap.edge, nodeSnap.point);
                AdoptSnapElevation(nodeSnap);
            }
            else if (canSnap && TryFlankSnap(raw, null, hoverSnapBase, out EndSnap flankSnap))
            {
                attach = flankSnap;
                point = flankSnap.point;
                if (flankSnap.edge != null)
                    hoverSnapHeight = SnapHeightOf(flankSnap.edge, flankSnap.point);
                AdoptSnapElevation(flankSnap);
            }
            snapTint = attach.HasValue;
            if (attach.HasValue && attach.Value.node != null)
            {
                ShowNodeEndHighlight(attach.Value.node);
            }
            else if (attach.HasValue)
            {
                BuildSnappedStubPreview(attach.Value, point);
                ShowSplineGhosts();
            }
            else
            {
                ShowStartMarker(point);
            }
            guideCenter = point;
            guideBearing = 0f;
            if (mouse.leftButton.wasPressedThisFrame)
            {
                shapeAnchor = point;
                shapeAnchorSnap = attach;
                shapeSnapHeight = hoverSnapHeight;
                shapeCentered = Shape == BuildShape.Rect && keyboard != null
                    && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
                // The deck under the cursor, or a snapped elevated wall's
                // own storey — either way the figure builds from there.
                shapeBaseY = hoverBaseY;
                // On terrain the anchor's plane is the whole figure's:
                // tops at the anchor's stepped ground plus the dialed
                // height. The ground itself is never moved — a ROOM
                // figure refuses only ground above that top plane.
                // Captured NOW, while the hover snap still feeds
                // EffectiveWallHeight.
                shapePadY = SampleCellGroundY(point.x, point.z);
                shapeTopY = hoverBaseY.HasValue
                    ? float.NegativeInfinity : shapePadY + EffectiveWallHeight;
            }
            return;
        }

        Vector3 anchor = shapeAnchor.Value;

        // Circle keeps its press-drag-release feel: the radius follows
        // the cursor and release commits.
        if (Shape == BuildShape.Circle)
        {
            if (hasPoint)
            {
                float r = FlatDistance(anchor, raw);
                if (!freePlace)
                    r = Mathf.Round(r / lengthStep) * lengthStep;
                r = Mathf.Max(MinCircleRadius, r);
                BuildShapePreview(CircleEdges(anchor, r), anchor, anchor + Vector3.right * r, r);
                Vector3 toward = raw - anchor;
                toward.y = 0f;
                Vector3 rim = anchor + (toward.sqrMagnitude > 0.01f
                    ? toward.normalized : Vector3.right) * r;
                dimGuides.Add((anchor, rim, r));
                ShowShapeGhosts();
                foreach (Vector3 p in shapeCrossings)
                    AddNodeGhost(p);
                guideCenter = anchor;
                guideBearing = 0f;
            }
            else
            {
                ShowShapeGhosts();
            }

            if (mouse.leftButton.wasReleasedThisFrame)
            {
                if (shapeBlocked)
                    BuildLog.Add("Refused: a side partially overlaps a wall — line it up fully (the wall becomes that side) or move clear.");
                else if (shapeSpecs.Count > 0 && shapeEdgesLive.Count > 0)
                    CommitShape(designate);
                CancelShape();
            }
            return;
        }

        // The rect is three clicks — corner, side, width — so it can sit
        // at ANY angle: lay the first side flush along a rotated curtain
        // wall, then pull the width out. Click 2 fixes the side.
        if (!shapeSideEnd.HasValue && shapeCentered)
        {
            // Centered side (Ctrl anchor): the anchor is the side's
            // MIDPOINT. Anchored on a free wall end, the side locks
            // perpendicular to that wall — the face a curtain runs into
            // — with Shift stepping deliberate tilts off it and Alt
            // freeing direction and length. The cursor sets the length,
            // symmetric about the anchor.
            if (hasPoint)
            {
                Vector3 m = anchor;
                Vector3 cur = raw - m;
                cur.y = 0f;
                Vector3? faceDir = null;
                if (shapeAnchorSnap.HasValue && shapeAnchorSnap.Value.node != null
                    && shapeAnchorSnap.Value.node.Valence == 1
                    && shapeAnchorSnap.Value.node.members[0].edge != null)
                {
                    WallNode.Member fm = shapeAnchorSnap.Value.node.members[0];
                    Vector3 outward = fm.edge.EndOutwardDir(fm.atStart);
                    faceDir = new Vector3(-outward.z, 0f, outward.x).normalized;
                }
                Vector3 sideDir;
                if (faceDir.HasValue && !freePlace)
                {
                    sideDir = faceDir.Value;
                    float refB = Mathf.Atan2(sideDir.z, sideDir.x) * Mathf.Rad2Deg;
                    float dragB = cur.sqrMagnitude > 0.0001f
                        ? Mathf.Atan2(cur.z, cur.x) * Mathf.Rad2Deg : refB;
                    if (Mathf.Abs(Mathf.DeltaAngle(refB, dragB)) > 90f)
                    {
                        sideDir = -sideDir;
                        refB = Mathf.Repeat(refB + 180f, 360f);
                    }
                    if (shiftHeld)
                    {
                        float stepped = Mathf.Round(Mathf.DeltaAngle(refB, dragB) / angleStep) * angleStep;
                        float rr = (refB + stepped) * Mathf.Deg2Rad;
                        sideDir = new Vector3(Mathf.Cos(rr), 0f, Mathf.Sin(rr));
                    }
                }
                else
                {
                    sideDir = cur.sqrMagnitude > 0.0001f ? cur.normalized : Vector3.right;
                    if (!freePlace && shiftHeld)
                    {
                        float dragB = Mathf.Atan2(sideDir.z, sideDir.x) * Mathf.Rad2Deg;
                        float rr = Mathf.Round(dragB / angleStep) * angleStep * Mathf.Deg2Rad;
                        sideDir = new Vector3(Mathf.Cos(rr), 0f, Mathf.Sin(rr));
                    }
                }
                float sideLen = Mathf.Abs(Vector3.Dot(cur, sideDir)) * 2f;
                if (!freePlace)
                    sideLen = Mathf.Round(sideLen / lengthStep) * lengthStep;
                sideLen = Mathf.Max(MinRectSide, sideLen);
                // Length solve: when another free wall end projects onto
                // this side's line near the current half-length, the
                // length clicks so the side's END lines up with it — then
                // the width phase can land that wall on the adjacent
                // side's exact midpoint.
                bool lenSnapped = false;
                Vector3 lenSnapNode = default;
                if (!freePlace)
                {
                    float bestOff = endpointSnapRadius;
                    foreach (WallNode node in WallNode.All)
                    {
                        if (node.Valence != 1
                            || (shapeAnchorSnap.HasValue && node == shapeAnchorSnap.Value.node))
                            continue;
                        Vector3 v = node.point - m;
                        v.y = 0f;
                        float candidate = Mathf.Abs(Vector3.Dot(v, sideDir)) * 2f;
                        if (candidate < MinRectSide)
                            continue;
                        float off = Mathf.Abs(candidate - sideLen);
                        if (off < bestOff)
                        {
                            bestOff = off;
                            sideLen = candidate;
                            lenSnapped = true;
                            lenSnapNode = node.point;
                        }
                    }
                }
                Vector3 aC = m - sideDir * (sideLen * 0.5f);
                Vector3 bC = m + sideDir * (sideLen * 0.5f);
                snapTint = faceDir.HasValue || lenSnapped;
                // White tie from the solving wall end to the side end it
                // lined up with.
                if (lenSnapped)
                    alignLines.Add((lenSnapNode,
                        Vector3.Dot(lenSnapNode - m, sideDir) >= 0f ? bC : aC));
                AddExtensionAlignGuides(aC);
                AddExtensionAlignGuides(bC);
                BuildShapePreview(
                    new List<(Vector3 s, Vector3 c, Vector3 e)> { (aC, (aC + bC) * 0.5f, bC) },
                    aC, bC, 0f);
                ShowShapeGhosts();
                foreach (Vector3 p in shapeCrossings)
                    AddNodeGhost(p);
                dimGuides.Add((aC, bC, sideLen));
                guideCenter = m;
                guideBearing = Mathf.Atan2(sideDir.z, sideDir.x) * Mathf.Rad2Deg;
                if (mouse.leftButton.wasPressedThisFrame && sideLen >= MinRectSide)
                {
                    // Bake the derived corners; the width phase runs the
                    // same for centered and cornered sides.
                    shapeAnchor = aC;
                    shapeSideEnd = bC;
                }
            }
            else
            {
                ShowShapeGhosts();
            }
            return;
        }
        if (!shapeSideEnd.HasValue)
        {
            if (hasPoint)
            {
                Vector3 b;
                EndSnap? sideSnap = null;
                // The gesture's own storey, captured at the anchor — not
                // whatever the cursor is over now. A rect started on the
                // ground stays a ground rect when its side runs past a
                // second storey.
                if (canSnap && TryNodeSnap(raw, anchor, shapeBaseY, out EndSnap sideNode))
                {
                    b = sideNode.point;
                    sideSnap = sideNode;
                }
                else if (canSnap && TryFlankSnap(raw, anchor, shapeBaseY, out EndSnap sideFlank))
                {
                    b = sideFlank.point;
                    sideSnap = sideFlank;
                }
                else
                {
                    b = SteppedDragEnd(anchor, raw, freePlace, shiftHeld);
                    // Shift steps the side RELATIVE to the anchor's
                    // masonry — 0° lays it exactly along the host wall.
                    if (!freePlace && shiftHeld && shapeAnchorSnap.HasValue)
                    {
                        Vector3 d = b - anchor;
                        d.y = 0f;
                        if (d.sqrMagnitude > 0.000001f)
                        {
                            float dragB = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
                            b = StepBearingFrom(
                                AngleRefBearing(shapeAnchorSnap.Value, dragB), anchor, b);
                        }
                    }
                    AddExtensionAlignGuides(b);
                }
                snapTint = sideSnap.HasValue;
                BuildShapePreview(
                    new List<(Vector3 s, Vector3 c, Vector3 e)> { (anchor, (anchor + b) * 0.5f, b) },
                    anchor, b, 0f);
                ShowShapeGhosts();
                foreach (Vector3 p in shapeCrossings)
                    AddNodeGhost(p);
                float len = FlatDistance(anchor, b);
                dimGuides.Add((anchor, b, len));
                guideCenter = (anchor + b) * 0.5f;
                guideBearing = Mathf.Atan2(b.z - anchor.z, b.x - anchor.x) * Mathf.Rad2Deg;
                if (shapeAnchorSnap.HasValue && len >= 0.25f)
                    activeAngle = (anchor,
                        AngleRefBearing(shapeAnchorSnap.Value, guideBearing), guideBearing);
                if (mouse.leftButton.wasPressedThisFrame && len >= MinRectSide)
                    shapeSideEnd = b;
            }
            else
            {
                ShowShapeGhosts();
            }
            return;
        }

        // Width phase: the rect extends perpendicular from the fixed side
        // toward the cursor. Shift squares it (width = side), click 3
        // commits — a refusal keeps the gesture alive to adjust.
        Vector3 sideA = anchor;
        Vector3 sideB = shapeSideEnd.Value;
        if (hasPoint)
        {
            Vector3 ab = sideB - sideA;
            ab.y = 0f;
            float sideLen = ab.magnitude;
            Vector3 n = new Vector3(-ab.z, 0f, ab.x) / Mathf.Max(0.001f, sideLen);
            float depth = Vector3.Dot(raw - sideA, n);
            float dir = depth >= 0f ? 1f : -1f;
            float w = Mathf.Abs(depth);
            if (shiftHeld)
                w = sideLen;
            else if (!freePlace)
                w = Mathf.Round(w / lengthStep) * lengthStep;
            w = Mathf.Max(MinRectSide, w);
            // Solve the second curtain: if a free wall end lines up with
            // an adjacent side's midline (on the cursor's side), the
            // width snaps so that side's MIDPOINT lands exactly on it.
            bool widthSnapped = false;
            Vector3 widthSnapRoot = default;
            if (!freePlace)
            {
                Vector3 along = ab / Mathf.Max(0.001f, sideLen);
                float bestErr = endpointSnapRadius;
                foreach (WallNode node in WallNode.All)
                {
                    if (node.Valence != 1)
                        continue;
                    foreach (Vector3 root in new[] { sideA, sideB })
                    {
                        Vector3 v = node.point - root;
                        v.y = 0f;
                        float lateral = Mathf.Abs(Vector3.Dot(v, along));
                        float longi = Vector3.Dot(v, n) * dir;
                        if (lateral < bestErr && longi * 2f >= MinRectSide)
                        {
                            bestErr = lateral;
                            w = longi * 2f;
                            widthSnapped = true;
                            widthSnapRoot = root;
                        }
                    }
                }
            }
            snapTint = widthSnapped;
            Vector3 offset = n * (w * dir);
            // White tie along the adjacent side whose midpoint just
            // caught the wall end, plus extension lines off the far
            // corners.
            if (widthSnapped)
                alignLines.Add((widthSnapRoot, widthSnapRoot + offset));
            AddExtensionAlignGuides(sideA + offset);
            AddExtensionAlignGuides(sideB + offset);
            BuildShapePreview(RectEdges(sideA, sideB, offset), sideA, sideB, w * dir);
            ShowShapeGhosts();
            foreach (Vector3 p in shapeCrossings)
                AddNodeGhost(p);
            dimGuides.Add((sideA, sideB, sideLen));
            dimGuides.Add((sideB, sideB + offset, w));
            guideCenter = (sideA + sideB) * 0.5f + offset * 0.5f;
            guideBearing = Mathf.Atan2(ab.z, ab.x) * Mathf.Rad2Deg;
            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (shapeBlocked)
                {
                    BuildLog.Add("Refused: a side partially overlaps a wall — line it up fully (the wall becomes that side) or move clear.");
                }
                else if (shapeSpecs.Count > 0)
                {
                    CommitShape(designate);
                    CancelShape();
                }
            }
        }
        else
        {
            ShowShapeGhosts();
        }
    }

    // A circle's edges, counter-clockwise (so a designated room traces
    // its interior on the first try). Control points sit at the tangent
    // intersections, so each 45-degree arc hugs the true circle to ~0.3%.
    List<(Vector3 s, Vector3 c, Vector3 e)> CircleEdges(Vector3 center, float r)
    {
        var edges = new List<(Vector3 s, Vector3 c, Vector3 e)>();
        float controlR = r / Mathf.Cos(Mathf.PI / CircleSegments);
        for (int i = 0; i < CircleSegments; i++)
        {
            float a0 = i * 2f * Mathf.PI / CircleSegments;
            float a1 = (i + 1) * 2f * Mathf.PI / CircleSegments;
            float am = (a0 + a1) * 0.5f;
            edges.Add((
                center + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * r,
                center + new Vector3(Mathf.Cos(am), 0f, Mathf.Sin(am)) * controlR,
                center + new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * r));
        }
        return edges;
    }

    // A rect from its first side (a→b, any bearing) and the perpendicular
    // offset to the far side — corner order normalized counter-clockwise
    // so designation traces the interior on the first try.
    static List<(Vector3 s, Vector3 c, Vector3 e)> RectEdges(Vector3 a, Vector3 b, Vector3 offset)
    {
        var corners = new List<Vector3> { a, b, b + offset, a + offset };
        float area = 0f;
        for (int i = 0; i < 4; i++)
        {
            Vector3 p = corners[i];
            Vector3 q = corners[(i + 1) % 4];
            area += p.x * q.z - q.x * p.z;
        }
        if (area < 0f)
            corners.Reverse();
        var edges = new List<(Vector3 s, Vector3 c, Vector3 e)>();
        for (int i = 0; i < 4; i++)
        {
            Vector3 p = corners[i];
            Vector3 q = corners[(i + 1) % 4];
            edges.Add((p, (p + q) * 0.5f, q));
        }
        return edges;
    }

    // The height a shape builds at: a snapped anchor's adopted height,
    // unless Ctrl+scroll explicitly overrode it.
    float ShapeHeight()
    {
        if (!heightOverride && shapeSnapHeight.HasValue)
            return shapeSnapHeight.Value;
        return EffectiveWallHeight;
    }

    void BuildShapePreview(List<(Vector3 s, Vector3 c, Vector3 e)> figure,
        Vector3 keyA, Vector3 keyB, float keyW)
    {
        float height = ShapeHeight();
        float topY = shapeTopY;
        // Terrain figures captured their plane at the anchor; deck figures
        // stand flat on the deck. Set before the cache check.
        ghostTopY = !float.IsNegativeInfinity(topY) ? topY
            : shapeBaseY.HasValue ? shapeBaseY.Value + height : ghostTopY;
        var key = (keyA, keyB, keyW, height, topY);
        if (lastShapeKey.HasValue && lastShapeKey.Value == key)
            return;
        lastShapeKey = key;

        foreach (Mesh m in shapeMeshes)
            Destroy(m);
        shapeMeshes.Clear();
        shapeSpecs.Clear();
        shapeCrossings.Clear();
        shapeBlocked = false;
        shapeEdgesLive.Clear();
        shapeEdgesLive.AddRange(figure);
        foreach ((Vector3 s, Vector3 c, Vector3 e) in figure)
        {
            // A side lying wholly along an existing wall is REUSED: no
            // ghost, no commit — the masonry is already there and the
            // face closes through it. A partial overlap refuses the
            // figure (it would leave a gap or a buried duplicate).
            float shapeBase = shapeBaseY ?? float.NegativeInfinity;
            float coverage = WallGraph.CoincidentCoverage(s, c, e, shapeBase);
            if (coverage >= ReuseCoverage)
                continue;
            if (coverage > 0.05f)
                shapeBlocked = true;
            if (LandLaw.SpanOutside(s, c, e))
                shapeBlocked = true;
            // Terrain figures preview fitted to the real ground — rooms
            // never move earth, so the slope the ghost stands on is the
            // slope the commit builds on.
            float floor = shapeBaseY ?? float.NegativeInfinity;
            shapeSpecs.AddRange(WallEdge.BuildSpecs(s, c, e, height,
                gridSize, gridSize, baseStepSize, BaseHeight, topY, floor));
            foreach (WallGraph.Crossing x in WallGraph.FindCrossings(s, c, e, shapeBase))
                shapeCrossings.Add(x.point);
        }
        foreach (WallEdge.SectionSpec spec in shapeSpecs)
            shapeMeshes.Add(spec.mesh);
    }

    void ShowShapeGhosts()
    {
        Color tint = shapeBlocked ? deletePreviewColor : previewColor;
        int shown = 0;
        foreach (WallEdge.SectionSpec spec in shapeSpecs)
        {
            GameObject ghost = GetPooled(ghostPool, shown++, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            SetGhostTint(ghost, tint);
            ghost.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));
            ghost.transform.localScale = Vector3.one;
        }
        for (int i = shown; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    void CommitShape(bool announce)
    {
        WallEdge lastEdge = null;
        int reused = 0;
        var allCreated = new List<WallEdge>();
        WallGraph.EdgeParams p = EdgeParamsNow(ShapeHeight(), shapeTopY,
            shapeBaseY ?? float.NegativeInfinity);
        foreach ((Vector3 s, Vector3 c, Vector3 e) in shapeEdgesLive)
        {
            // Re-derived rather than read from preview state, so the
            // commit can't disagree with a stale ghost.
            if (WallGraph.CoincidentCoverage(s, c, e, p.baseY) >= ReuseCoverage)
            {
                reused++;
                continue;
            }
            List<WallEdge> created = WallGraph.CommitEdge(splineParent, s, c, e, p);
            allCreated.AddRange(created);
            if (created.Count > 0)
                lastEdge = created[created.Count - 1];
        }
        Estate.Pay(Estate.CostOfEdges(allCreated), Estate.KindLabel(CurrentWallKind));
        if (reused > 0)
            BuildLog.Add(reused == 1
                ? "Reused an existing wall as a side."
                : $"Reused existing walls as {reused} sides.");
        // ShapeEdges runs counter-clockwise, so the interior is on the
        // left of the last edge's own direction.
        if (announce && lastEdge != null)
            Enclosure.AnnounceChainClose(lastEdge, true);
    }

    void CancelShape()
    {
        shapeAnchor = null;
        shapeAnchorSnap = null;
        shapeSideEnd = null;
        shapeCentered = false;
        shapeTopY = float.NegativeInfinity;
        shapeSnapHeight = null;
        shapeBaseY = null;
        lastShapeKey = null;
        shapeBlocked = false;
        shapeEdgesLive.Clear();
        foreach (Mesh m in shapeMeshes)
            Destroy(m);
        shapeMeshes.Clear();
        shapeSpecs.Clear();
        shapeCrossings.Clear();
        HideGhosts();
    }

    // The Room tool: chained clicks lay a pending loop of straight walls
    // — each click fixes a vertex and the next segment grows from it. The
    // whole chain stays a ghost until it CLOSES: the far end lands on the
    // chain's own start, or on existing masonry connected back to the
    // start attachment — then every segment commits through the graph at
    // once and the enclosed face is announced. Escape abandons
    // the chain and leaves nothing. Segments never cross walls: a drag
    // over one clamps to the first crossing point (a T at commit, and
    // often the closure itself).
    void UpdateRoom(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        HandleHeightScroll(mouse, keyboard);
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelRoomChain();
            return;
        }
        // Right click steps the chain back one corner; popping the anchor
        // abandons the chain like Esc.
        if (rightClick && roomChain.Count > 0)
        {
            UndoRoomChainLeg();
            return;
        }

        bool freePlace = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        bool snapAngle = keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        Vector3 raw = default;
        float? deckY = null;
        bool hasPoint = !overUI && TryGetBuildSurface(ray, freePlace, out raw, out deckY);
        // Snapping works on decks too, filtered to that deck's own walls
        // — the masonry a storey down never grabs the gesture. A snap
        // onto an elevated wall adopts its storey (AdoptSnapElevation),
        // so a chain started from a second-storey wall builds beside it,
        // not on the terrain below.
        bool canSnap = !freePlace;
        hoverBaseY = deckY;

        if (roomChain.Count == 0)
        {
            if (!hasPoint)
            {
                HideGhosts();
                return;
            }
            Vector3 point = raw;
            EndSnap? attach = null;
            if (canSnap && TryNodeSnap(raw, null, hoverSnapBase, out EndSnap nodeSnap))
            {
                attach = nodeSnap;
                point = nodeSnap.point;
                if (nodeSnap.edge != null)
                    hoverSnapHeight = SnapHeightOf(nodeSnap.edge, nodeSnap.point);
                AdoptSnapElevation(nodeSnap);
            }
            else if (canSnap && TryFlankSnap(raw, null, hoverSnapBase, out EndSnap flankSnap))
            {
                attach = flankSnap;
                point = flankSnap.point;
                if (flankSnap.edge != null)
                    hoverSnapHeight = SnapHeightOf(flankSnap.edge, flankSnap.point);
                AdoptSnapElevation(flankSnap);
            }
            // The slab rim, same rule as the wall tool: the chain corner
            // pulls onto the outline inset half a thickness, mitered at
            // the outline's corners — walls around a foundation hug its
            // edge face-flush.
            else if (canSnap && hoverSlabTile != null
                && TrySlabRimSnap(hoverSlabTile, raw, out Vector3 rimPoint, out _))
            {
                point = rimPoint;
                snapTint = true;
            }
            snapTint |= attach.HasValue;
            // Snapped hovers borrow the wall tool's presentation — the
            // existing joint highlights, or a stub leaves the face — so
            // no ghost box sits buried inside the host's masonry.
            if (attach.HasValue && attach.Value.node != null)
            {
                ShowNodeEndHighlight(attach.Value.node);
            }
            else if (attach.HasValue)
            {
                BuildSnappedStubPreview(attach.Value, point);
                ShowSplineGhosts();
            }
            else
            {
                ShowStartMarker(point);
            }
            guideCenter = point;
            guideBearing = 0f;
            if (mouse.leftButton.wasPressedThisFrame)
            {
                roomChain.Add(point);
                roomStartAttach = attach;
                roomChainHeight = EffectiveWallHeight;
                // The deck under the cursor, or a snapped elevated wall's
                // own storey — the whole chain builds from there.
                roomChainBaseY = hoverBaseY;
                // On terrain the anchor names the room's plane: tops at
                // this stepped ground plus the dialed height. The ground
                // itself is never moved — the chain refuses only ground
                // standing above that top plane (buried walls); dips and
                // bumps below it are the floor's job.
                roomChainTopY = hoverBaseY.HasValue
                    ? float.NegativeInfinity
                    : SampleCellGroundY(point.x, point.z) + roomChainHeight;
            }
            return;
        }

        Vector3 last = roomChain[roomChain.Count - 1];
        if (hasPoint)
        {
            Vector3 end;
            EndSnap? endAttach = null;
            bool closingToStart = false;
            // With Shift down the BEARING rules. A flank landing would
            // otherwise steer the segment to wherever the cursor grazed the
            // host — the step silently lost — so the stepped aim wins and
            // the segment runs on to MEET the wall, the crossing clamp
            // below landing it on the centerline at the stepped angle. A
            // node snap still wins outright: an existing corner is a
            // stronger intent than an angle, and only one of the two can
            // be honoured at a point.
            bool steppedAim = !freePlace && snapAngle;
            EndSnap flankSnap = default;
            bool flankInReach = canSnap
                && TryFlankSnap(raw, last, roomChainBaseY, out flankSnap);
            float landingReach = 0f;
            if (roomChain.Count >= 3 && FlatDistance(raw, roomChain[0]) < endpointSnapRadius)
            {
                // Back onto the chain's own first vertex: a pure loop.
                end = roomChain[0];
                closingToStart = true;
            }
            else if (canSnap && TryNodeSnap(raw, last, roomChainBaseY, out EndSnap nodeSnap))
            {
                end = nodeSnap.point;
                endAttach = nodeSnap;
            }
            else if (flankInReach && !steppedAim)
            {
                end = flankSnap.point;
                endAttach = flankSnap;
            }
            // The slab rim grabs the chain's next corner too — same rule
            // as the wall tool's drag end, and it yields to Shift's
            // stepped bearing the same way the flank does.
            else if (canSnap && !steppedAim && hoverSlabTile != null
                && TrySlabRimSnap(hoverSlabTile, raw, out Vector3 rimEnd, out _))
            {
                end = rimEnd;
            }
            else
            {
                end = SteppedDragEnd(last, raw, freePlace, snapAngle);
                // Shift steps the segment RELATIVE to the corner it grows
                // from — the previous segment's run, or the snapped
                // start's masonry — so stepped chain corners land on
                // clean 5° readings even off the world grid.
                if (steppedAim)
                {
                    Vector3 d = end - last;
                    d.y = 0f;
                    if (d.sqrMagnitude > 0.000001f)
                    {
                        float dragB = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
                        float? refB = ChainStepRef(dragB);
                        if (refB.HasValue)
                            end = StepBearingFrom(refB.Value, last, end);
                    }
                    if (flankInReach)
                        landingReach = SteppedLandingReach;
                }
                AddExtensionAlignGuides(end);
            }

            // No crossings in room mode: the first wall the segment
            // passes over clamps its end onto that wall's centerline.
            // A stepped aim probes past the cursor so the wall it was
            // grazing still catches the end — the step is kept, the
            // landing is exact, and the run stops at the FIRST wall it
            // meets either way.
            if (!closingToStart)
            {
                Vector3 probe = end;
                if (landingReach > 0f)
                {
                    Vector3 run = end - last;
                    run.y = 0f;
                    if (run.sqrMagnitude > 0.000001f)
                        probe = end + run.normalized * landingReach;
                }
                var crossings = WallGraph.FindCrossings(last, (last + probe) * 0.5f, probe,
                    roomChainBaseY ?? float.NegativeInfinity);
                if (crossings.Count > 0)
                {
                    WallGraph.Crossing x = crossings[0];
                    end = new Vector3(x.point.x, 0f, x.point.z);
                    endAttach = x.reuseNode != null
                        ? MakeNodeSnap(x.reuseNode, roomChainBaseY)
                        : FlankSnapAt(x.edge, end, last);
                }
            }

            bool closing = closingToStart;
            if (!closing && endAttach.HasValue && roomStartAttach.HasValue)
                closing = WallGraph.Connected(AttachNodes(roomStartAttach.Value),
                    new HashSet<WallNode>(AttachNodes(endAttach.Value)));

            // Display-only trims keep the ghost outside host masonry at
            // snapped ends, exactly like the wall tool; the commit still
            // runs centerline to centerline.
            float trimStart = roomChain.Count == 1
                ? TrimInto(roomStartAttach, last, end) : 0f;
            BuildRoomLivePreview(last, end, trimStart, TrimInto(endAttach, end, last));
            roomLiveClosing = closing && !roomLiveBlocked;
            ShowRoomGhosts();
            guideCenter = (last + end) * 0.5f;
            guideBearing = Mathf.Atan2(end.z - last.z, end.x - last.x) * Mathf.Rad2Deg;
            // Corner readout at the vertex the segment grows from, against
            // the previous segment or the snapped start's masonry.
            if (FlatDistance(last, end) >= 0.25f)
            {
                float? arcRef = ChainStepRef(guideBearing);
                if (arcRef.HasValue)
                    activeAngle = (last, arcRef.Value, guideBearing);
            }
            snapTint = roomLiveClosing || endAttach.HasValue;

            if (mouse.leftButton.wasPressedThisFrame && FlatDistance(last, end) >= 0.25f)
            {
                if (roomLiveBlocked)
                {
                    BuildLog.Add("Refused: segment redraws an existing wall — end it ON the wall; the enclosure reuses the rest.");
                }
                else
                {
                    roomChain.Add(end);
                    BakeRoomLiveSegment();
                    if (closing)
                        CommitRoomChain();
                    else
                        ShowRoomGhosts();
                }
            }
        }
        else
        {
            // Off-target frames (HUD, sky) keep the chain visible.
            ShowRoomGhosts();
        }
    }

    // ------------------------------------------------------------------
    // Stairs
    // ------------------------------------------------------------------

    // The stair tool has NO GESTURE: point at a wall's side face and the
    // ghost is already there. The wall answers every question — where the
    // run starts (the hovered section's bottom), where it lands (that same
    // section's top), which face (the cursor's side), which curve (the
    // host's own sub-curve, offset), and which storey (the host's baseY).
    // Length is DERIVED from the climb (WallStair.RunFor), which is why
    // this tool has no refusal: a stair can't be too steep or too shallow
    // when the player never chooses its length. R flips the climb, and
    // latches the decision away from the camera. See Docs/Stairs.md.
    void UpdateStair(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        bool flipPressed = keyboard != null && keyboard.rKey.wasPressedThisFrame;

        if (overUI || !TryGetWallSideHit(ray, out WallEdgeSection sec, out Vector3 hit))
        {
            // Off a wall the camera gets its say back — the latch belongs
            // to the stair you were looking at, not to the tool.
            stairHost = null;
            stairHeld = false;
            CancelStair();
            return;
        }

        // A different wall is a different stair: re-arm the camera.
        if (sec.edge != stairHost)
        {
            stairHost = sec.edge;
            stairHeld = false;
            stairPerp = false;
        }
        // R cycles the run: flip the along-wall climb, then turn it OUT
        // from the face, then back along — every stop reachable within
        // two presses, and each press stays latched off the camera.
        if (flipPressed)
        {
            if (!stairHeld)
            {
                stairForward = !stairForward;
                stairHeld = true;
            }
            else if (!stairPerp)
            {
                stairPerp = true;
            }
            else
            {
                stairPerp = false;
                stairForward = !stairForward;
            }
        }

        HandleStairRiseScroll(mouse, keyboard, sec);
        BuildStairPreview(sec, hit);
        ShowStairGhosts();
        if (stairSpecs.Count > 0)
            ghostTopY = stairPreviewTopY;

        if (mouse.leftButton.wasPressedThisFrame && pendingStair.HasValue)
            CommitStair();
    }

    // Ctrl+scroll dials the stair's CLIMB in quarter steps — a partial
    // flight up a tall wall, for landings mid-storey and tight spaces.
    // The wall still answers the maximum: the dial can only choose less
    // than the hovered wall's own rise. Ignored at a doorway target —
    // the sill IS the point there.
    void HandleStairRiseScroll(Mouse mouse, Keyboard keyboard, WallEdgeSection sec)
    {
        if (keyboard == null || (!keyboard.leftCtrlKey.isPressed && !keyboard.rightCtrlKey.isPressed))
            return;
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f)
            return;
        if (Mathf.Abs(scroll) >= 100f)
            scroll /= 120f;
        float notches = Mathf.Clamp(scroll, -4f, 4f);
        float wallRise = Mathf.Max(0f, sec.topY - MasonryFloorAt(sec));
        float cur = stairRiseDial ?? wallRise;
        // Land ON the quarter grid, then step by it (round half-up — the
        // wall-height dial's convention).
        cur = Mathf.Floor(cur / SlabTile.FloorStep + 0.5f) * SlabTile.FloorStep
            + notches * SlabTile.FloorStep;
        cur = Mathf.Clamp(cur, WallStair.TargetRise * 2f, wallRise);
        stairRiseDial = cur >= wallRise - 0.01f ? (float?)null : cur;
    }

    // The wall a stair or a doorway would go on: the first thing under the
    // cursor, and only if it's a wall section hit on its SIDE. A top-face
    // hit is the ride gesture's territory (a stair crosses a wall-walk, it
    // doesn't run along it; a door through a wall's top is nothing), and
    // first-hit-only keeps the pick honest — you get the wall you can see,
    // never one behind it.
    bool TryGetWallSideHit(Ray ray, out WallEdgeSection section, out Vector3 point)
    {
        section = null;
        point = default;
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            section = hit.collider.GetComponent<WallEdgeSection>();
            bool topFace = hit.normal.y > 0.7f && !CutawayView.IsCutSurface(hit.point);
            if (section == null || section.edge == null || topFace)
            {
                section = null;
                return false;
            }
            point = new Vector3(hit.point.x, 0f, hit.point.z);
            return true;
        }
        return false;
    }

    // Lays the run along the masonry and rebuilds the tread meshes when
    // anything about it actually changed. The cursor marks the stair's
    // BOTTOM and it climbs away, THROUGH the wall's nodes: a circle is
    // eight edges and a wall with tees is several, so a run confined to
    // one edge pins itself to whichever node it started from and the only
    // thing left free to move is its length. Same answer the wall tool's
    // ride gives, for the same reason.
    void BuildStairPreview(WallEdgeSection sec, Vector3 hit)
    {
        WallEdge host = sec.edge;
        float bottomY = MasonryFloorAt(sec);
        float tCursor = host.NearestT(hit, out _);

        // The stair's FOOT reads what it stands on, like a deck: the run
        // sits offset from the wall face, so sample the highest wall top
        // or slab plane under that offset point. Pointed at a tall wall
        // from a half-height landing, the flight starts ON the landing —
        // the second leg of a terraced climb — instead of reaching down
        // for the room floor. The adopted plane is the storey too
        // (baseFrom), so the treads sit on the landing rather than
        // skirting down its face.
        float baseFrom = host.baseY;
        {
            Vector3 centreAt = WallEdge.Evaluate(host.A, host.control, host.B, tCursor);
            centreAt.y = 0f;
            Vector3 tanAt = WallEdge.Tangent(host.A, host.control, host.B, tCursor);
            tanAt.y = 0f;
            tanAt.Normalize();
            Vector3 footN = new Vector3(-tanAt.z, 0f, tanAt.x) * host.SideOf(hit);
            Vector3 foot = centreAt + footN * ((host.thickness + stairWidth) * 0.5f);
            float rest = StairFootRest(foot, bottomY,
                sec.topY - WallStair.TargetRise);
            if (rest > bottomY + 0.01f)
            {
                bottomY = rest;
                baseFrom = rest;
            }
        }

        // Pointing at a DOORWAY's own masonry asks for steps up TO it: the
        // run climbs to that doorway's sill instead of the wall top, and
        // arrives there rather than setting off from there. A raised sill
        // is the only thing that needs this — a doorway sitting on the
        // ground is already reachable.
        stairToDoor = TryDoorTarget(sec, bottomY, out float doorSillY, out float doorCentreArc);
        float target = stairToDoor ? doorSillY : sec.topY;
        // The dialed climb caps the target; the wall still answers the
        // maximum. A doorway's sill is a stated point and outranks it.
        if (!stairToDoor && stairRiseDial.HasValue)
            target = Mathf.Min(sec.topY, bottomY + stairRiseDial.Value);

        bool forward = stairHeld ? stairForward : CameraClimbForward(host, tCursor);
        // Keep the unlatched direction in sync, so the first R press flips
        // away from what's actually on screen rather than from a stale bool.
        stairForward = forward;

        // Which side of the CLIMB the cursor is on. SideOf answers for the
        // host's own A→B, so it flips when the run climbs the other way.
        float sideOfTravel = forward ? host.SideOf(hit) : -host.SideOf(hit);
        // A run arriving at a doorway starts its walk at the doorway's far
        // EDGE, not at the cursor: the landing is the platform outside the
        // door, and one measured from the door's centre would leave half
        // the opening hanging over the drop.
        if (stairToDoor && stairPerp)
        {
            // A perpendicular flight ARRIVES at the doorway, so it wants
            // the opening's centre, not its edge — the run is the
            // classic straight entrance stair.
            float[] tbl = WallEdge.BuildArcTable(host.A, host.control, host.B);
            tCursor = WallEdge.TAtArc(tbl, doorCentreArc);
        }
        else if (stairToDoor)
        {
            float[] tbl = WallEdge.BuildArcTable(host.A, host.control, host.B);
            float span = tbl[tbl.Length - 1];
            float half = doorWidth * 0.5f;
            tCursor = WallEdge.TAtArc(tbl, Mathf.Clamp(
                forward ? doorCentreArc - half : doorCentreArc + half, 0f, span));
        }
        var key = (host, sec.index, Mathf.Round(tCursor * 500f) / 500f, forward, sideOfTravel,
            stairPerp, stairRiseDial ?? -1f, bottomY);
        if (lastStairKey.HasValue && lastStairKey.Value.Equals(key))
            return;
        lastStairKey = key;

        // Sizing settles two things at once, and each can move the other.
        //  - the LANDING: the run is sized off the wall under the cursor,
        //    but it must finish flush on the wall where it actually ends,
        //    which may be a step higher or lower.
        //  - the MITER: welding the corners shortens an inside run and
        //    lengthens an outside one (0.9m at an octagon's corner), so
        //    the walked distance is not the finished distance. Left
        //    uncorrected the pitch drifts ±20%, which would quietly break
        //    the reason this tool needs no refusal — that the length is
        //    derived from the climb and so can't be wrong.
        // Level walls with no corners settle on the first pass.
        float topY = target;
        List<WallStair.Span> spans = null;
        float runArc = 0f;
        if (stairPerp)
        {
            // The perpendicular flight: one straight span from its own
            // foot to the wall face, already ordered bottom→top, arriving
            // flush at the face at the target height — the wall top (or
            // the doorway) IS the landing, so there is no landing run.
            // It consumes no masonry arc, so "not enough wall" cannot
            // happen and nothing is blocked.
            float climb = WallStair.RunFor(topY - bottomY);
            Vector3 centre = WallEdge.Evaluate(host.A, host.control, host.B, tCursor);
            centre.y = 0f;
            Vector3 tan = WallEdge.Tangent(host.A, host.control, host.B, tCursor);
            tan.y = 0f;
            tan.Normalize();
            Vector3 outN = new Vector3(-tan.z, 0f, tan.x) * host.SideOf(hit);
            Vector3 face = centre + outN * (host.thickness * 0.5f);
            Vector3 foot = face + outN * climb;
            var curves = new List<(Vector3 s, Vector3 c, Vector3 e)>
                { (foot, (foot + face) * 0.5f, face) };
            spans = WallStair.ChainSpans(curves);
            runArc = spans.Count > 0 ? spans[spans.Count - 1].arcEnd : 0f;
            ClearStairMeshes();
            stairSpecs.Clear();
            stairBlocked = spans.Count == 0;
            stairShortfall = 0f;
        }
        else
        {
            float climb = 0f, reach = 0f, got = 0f, scale = 1f, covered = 0f;
            for (int pass = 0; pass < 4; pass++)
            {
                climb = WallStair.RunFor(topY - bottomY);
                reach = climb + WallStair.LandingRun;
                spans = StairSpansFrom(host, tCursor, forward, sideOfTravel,
                    reach * scale, climb * scale, out covered);
                if (spans.Count == 0)
                    break;
                got = spans[spans.Count - 1].arcEnd;

                // A doorway's sill — or a dialed climb — is a fixed
                // target: there is no wall top to finish flush with, so
                // only the reach is left to settle.
                float landed = stairToDoor || stairRiseDial.HasValue
                    ? float.NegativeInfinity : TopAtLanding();
                bool topSettled = float.IsNegativeInfinity(landed) || Mathf.Abs(landed - topY) < 0.01f;
                bool runSettled = Mathf.Abs(got - reach) < 0.05f;
                // Out of wall: no amount of scaling conjures more, so stop
                // rather than spin.
                if (covered < reach * scale - 0.05f)
                    break;
                if (topSettled && runSettled)
                    break;
                if (!topSettled)
                    topY = landed;
                if (!runSettled && got > 0.01f)
                    scale *= reach / got;
            }

            ClearStairMeshes();
            stairSpecs.Clear();
            // Arriving at a doorway means the cursor end is the TOP, so the
            // chain is flipped: the climb ends up first and the landing last,
            // which is the order BuildStepSpecs already expects. The landing is
            // then the platform at the door, so it keeps its full length and
            // any shortfall falls on the climb — which is flagged anyway.
            if (stairToDoor && spans != null && spans.Count > 0)
                spans = WallStair.ReverseSpans(spans);
            // The climb is required; the landing takes what's left over. So a
            // wall long enough to climb is never refused for being too short
            // to stand on at the top — it just gives a shorter landing.
            runArc = stairToDoor
                ? Mathf.Min(climb, Mathf.Max(0f, got - WallStair.LandingRun))
                : Mathf.Min(climb, got);
            // Short of masonry, the ghost still draws where it would go and
            // runs red — a refusal is visible in place, never a vanishing act.
            // The question is asked of the FINISHED run, after the weld.
            stairBlocked = spans == null || spans.Count == 0 || got < climb - 0.05f;
            stairShortfall = Mathf.Max(0f, climb - got);
        }
        if (spans != null && spans.Count > 0)
        {
            stairSpecs.AddRange(WallStair.BuildStepSpecs(spans, bottomY, topY,
                runArc, baseFrom, stairWidth, baseStepSize, BaseHeight));
            foreach (WallEdge.SectionSpec spec in stairSpecs)
                stairMeshes.Add(spec.mesh);
        }
        stairPreviewTopY = topY;
        pendingStair = !stairBlocked && stairSpecs.Count > 0
            ? new PendingStair { spans = spans, bottomY = bottomY, topY = topY,
                                 runArc = runArc, baseFrom = baseFrom }
            : null;
    }

    // The highest wall top or slab plane directly under the stair's foot
    // point, when it stands above `floor` (the host wall's own masonry
    // floor) and below `ceiling` (the target, less one riser — you can't
    // start where you're going). Opening sections are skipped: a lintel's
    // top is not a place to stand.
    float StairFootRest(Vector3 foot, float floor, float ceiling)
    {
        float best = floor;
        foreach (WallEdge e in WallEdge.All)
        {
            if (e == null)
                continue;
            float t = e.NearestT(foot, out _);
            Vector3 on = WallEdge.Evaluate(e.A, e.control, e.B, t);
            if (FlatDistance(foot, new Vector3(on.x, 0f, on.z))
                > e.thickness * 0.5f + 0.05f)
                continue;
            foreach (WallEdgeSection s in e.SectionsInOrder())
                if (s.openingIndex < 0
                    && t >= s.tStart - 0.0001f && t <= s.tEnd + 0.0001f
                    && s.topY > best && s.topY <= ceiling)
                    best = s.topY;
        }
        foreach (SlabTile tile in SlabTile.All)
            if (tile != null && SlabTile.Contains(tile.verts, foot)
                && tile.topY > best && tile.topY <= ceiling)
                best = tile.topY;
        return best;
    }

    // The doorway this cursor is asking for steps up to: the section under
    // the cursor belongs to an opening, and that opening's sill stands
    // clear of the ground the wall is founded on. A doorway sitting ON the
    // ground needs nothing, and says so by refusing to be a target.
    bool TryDoorTarget(WallEdgeSection sec, float groundY, out float sillY, out float centreArc)
    {
        sillY = 0f;
        centreArc = 0f;
        if (sec.openingIndex < 0 || sec.edge == null
            || sec.openingIndex >= sec.edge.openings.Count)
            return false;
        WallEdge.Opening o = sec.edge.openings[sec.openingIndex];
        if (o.sill - groundY < DoorStepsNeeded)
            return false;
        sillY = o.sill;
        float[] table = WallEdge.BuildArcTable(sec.edge.A, sec.edge.control, sec.edge.B);
        centreArc = WallEdge.ArcAt(table, Mathf.Clamp01(o.t));
        return true;
    }

    // Walks the masonry from (host, t) in the climb direction, taking
    // `run` metres of arc and offsetting each piece onto the cursor's
    // face. Every piece is the host's OWN sub-curve, so a stair around a
    // circular room traces it. `covered` reports how much wall was
    // actually there.
    List<WallStair.Span> StairSpansFrom(WallEdge host, float tCursor, bool forward,
        float sideOfTravel, float run, float markArc, out float covered)
    {
        var curves = new List<(Vector3 s, Vector3 c, Vector3 e)>();
        var visited = new HashSet<WallEdge>();
        WallEdge cur = host;
        bool towardB = forward;
        float from = tCursor;
        float left = run;
        covered = 0f;
        stairLanding = null;

        while (cur != null && left > 0.01f && visited.Add(cur) && curves.Count < 64)
        {
            float[] table = WallEdge.BuildArcTable(cur.A, cur.control, cur.B);
            float total = table[table.Length - 1];
            float atFrom = WallEdge.ArcAt(table, from);
            float avail = towardB ? total - atFrom : atFrom;
            float take = Mathf.Min(avail, left);
            if (take > 0.01f)
            {
                // Where the CLIMB ends, which is the wall the run must
                // finish flush with. The walk carries on past it for the
                // landing, and the landing may well cross onto a wall at a
                // different height — sizing the stair off that one would
                // aim the treads at a floor the player isn't going to.
                if (!stairLanding.HasValue && covered + take >= markArc - 0.01f)
                {
                    float atMark = towardB ? atFrom + (markArc - covered)
                                           : atFrom - (markArc - covered);
                    stairLanding = (cur, WallEdge.TAtArc(table, atMark));
                }
                float tTo = WallEdge.TAtArc(table, towardB ? atFrom + take : atFrom - take);
                // Directed bottom→top; SubCurve handles a descending range.
                WallGraph.SubCurve(cur.A, cur.control, cur.B, from, tTo,
                    out Vector3 s, out Vector3 c, out Vector3 e);
                // sideOfTravel is left/right of the CLIMB, not of any one
                // edge's A→B, and OffsetCurve's +d is left-of-travel too.
                // Every span travels bottom→top, so one signed distance
                // holds the whole chain on a single face — an edge whose
                // own orientation happens to be reversed can't flip the
                // stair through the wall.
                float signed = sideOfTravel * (cur.thickness + stairWidth) * 0.5f;
                WallEdge.OffsetCurve(new Vector3(s.x, 0f, s.z), new Vector3(c.x, 0f, c.z),
                    new Vector3(e.x, 0f, e.z), signed,
                    out Vector3 s2, out Vector3 c2, out Vector3 e2);
                curves.Add((s2, c2, e2));
                covered += take;
                left -= take;
            }
            if (left <= 0.01f)
                break;
            // Straightest continuation on the same storey — a tee picks
            // the run that carries on, not the branch (WallGraph doctrine,
            // shared with the ride).
            WallNode node = towardB ? cur.nodeB : cur.nodeA;
            WallEdge next = StraightestContinuation(node, cur, towardB);
            if (next == null)
                break;
            towardB = next.nodeA == node;
            from = towardB ? 0f : 1f;
            cur = next;
        }
        // Meet at the corners before the arc bookkeeping is taken, so the
        // treads distribute over the run the stair actually has.
        WallStair.WeldOffsetJoints(curves, (host.thickness + stairWidth) * 0.5f);
        return WallStair.ChainSpans(curves);
    }

    // The wall top where the run just landed — pass two of the sizing.
    // -Infinity when the landing sits on nothing recognisable.
    float TopAtLanding()
    {
        if (!stairLanding.HasValue)
            return float.NegativeInfinity;
        (WallEdge edge, float t) = stairLanding.Value;
        if (edge == null)
            return float.NegativeInfinity;
        foreach (WallEdgeSection s in edge.SectionsInOrder())
            if (t >= s.tStart - 0.0001f && t <= s.tEnd + 0.0001f)
                return s.topY;
        return float.NegativeInfinity;
    }

    // Which way the stair climbs when the player hasn't said: away from
    // the camera along the wall, so it runs up and into the view rather
    // than down out of it.
    bool CameraClimbForward(WallEdge host, float t)
    {
        Vector3 tan = WallEdge.Tangent(host.A, host.control, host.B, t);
        tan.y = 0f;
        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (tan.sqrMagnitude < 0.0001f || fwd.sqrMagnitude < 0.0001f)
            return true;
        return Vector3.Dot(tan.normalized, fwd.normalized) >= 0f;
    }

    void ShowStairGhosts()
    {
        Color tint = stairBlocked ? deletePreviewColor : previewColor;
        for (int i = 0; i < stairSpecs.Count; i++)
        {
            WallEdge.SectionSpec spec = stairSpecs[i];
            GameObject ghost = GetPooled(ghostPool, i, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            SetGhostTint(ghost, tint);
            ghost.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));
            ghost.transform.localScale = Vector3.one;
        }
        for (int i = stairSpecs.Count; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    void CommitStair()
    {
        PendingStair p = pendingStair.Value;
        WallStair stair = WallStair.Create(splineParent, p.spans, p.bottomY, p.topY,
            p.runArc, p.baseFrom, stairWidth, BaseHeight, baseStepSize, wallMaterial);
        float landing = stair.TotalArc - stair.runArc;
        BuildLog.Add($"Stair — {stair.StepCount} steps, {stair.Rise:0.0}m rise,"
            + $" {landing:0.0}m landing.");
        Estate.Pay(Estate.CostOfStair(stair), "Stair");
        // A stair built under an existing floor opens its own way through
        // — the same stamp the floor tool makes when drawn over a stair.
        // A well clipping the floor's edge is skipped, not forced: the
        // stair pokes past the floor there and the hole can't be cut
        // cleanly; redraw the floor over it to cover it properly.
        foreach (SlabTile tile in SlabTile.All)
        {
            if (tile == null || tile.isFoundation)
                continue;
            List<Vector3> fp = StairWellFootprint(stair, tile.topY);
            if (fp == null || PolysCross(fp, tile.verts))
                continue;
            int inside = 0;
            foreach (Vector3 v in fp)
                if (SlabTile.Contains(tile.verts, v))
                    inside++;
            if (inside < fp.Count)
                continue;
            tile.wells.Add(new SlabTile.Well(stair, fp));
            tile.Rebuild();
            BuildLog.Add("Stairwell opened in the floor above.");
        }
        CancelStair();
    }

    void CancelStair()
    {
        ClearStairMeshes();
        stairSpecs.Clear();
        pendingStair = null;
        lastStairKey = null;
        stairLanding = null;
        stairBlocked = false;
        stairShortfall = 0f;
        stairToDoor = false;
        HideGhosts();
    }

    void ClearStairMeshes()
    {
        foreach (Mesh mesh in stairMeshes)
            Destroy(mesh);
        stairMeshes.Clear();
    }

    // ------------------------------------------------------------------
    // Doors
    // ------------------------------------------------------------------

    // Like the stair tool, the door tool has NO GESTURE: point at a wall's
    // side face and the opening is already there. The wall answers
    // everything but where along it you want the hole — which face you
    // approach from doesn't matter, since a doorway goes all the way
    // through.
    void UpdateDoor(Mouse mouse, Ray ray, bool overUI)
    {
        if (overUI || !TryGetWallSideHit(ray, out WallEdgeSection sec, out Vector3 hit))
        {
            CancelDoor();
            return;
        }

        doorHost = sec.edge;
        doorT = doorHost.NearestT(hit, out _);
        // The doorway opens from the floor it serves, not from the ground
        // it happens to stand on. Without this a room's slab stands across
        // the opening — the floor is one level plane at the room's highest
        // interior ground, so on a slope it is metres above the downhill
        // wall's footing.
        float ground = MasonryFloorAt(sec);
        // A wall on a slab bottoms at the slab, so its ground IS the
        // floor it serves — sills no longer need a room to raise them.
        doorSill = ground;
        doorStep = doorSill - ground;
        doorRefusal = DoorRefusal(doorHost, doorT, sec);
        doorBlocked = doorRefusal != null;
        ShowDoorGhost(sec);

        if (mouse.leftButton.wasPressedThisFrame)
        {
            if (doorBlocked)
            {
                BuildLog.Add(doorRefusal);
                return;
            }
            doorHost.openings.Add(new WallEdge.Opening
            {
                t = doorT,
                width = doorWidth,
                head = doorHead,
                sill = doorSill,
            });
            doorHost.Rebuild();
            BuildLog.Add($"Doorway — {doorWidth:0.0}m wide, {doorHead:0.0}m head."
                + (doorStep >= DoorStepsNeeded
                    ? $" Its sill is {doorStep:0.0}m above the ground outside —"
                        + " point the stair tool at the stone under it to build up."
                    : ""));
            // Cutting a doorway is labor, not masonry — a flat mason's fee.
            Estate.Pay(Estate.DoorFee, "Doorway cut");
            // The rebuild destroyed the section the ghost was measured
            // against, so the next frame re-reads the wall from scratch.
            CancelDoor();
        }
    }

    // The ground this masonry stands on at the cursor. A LINTEL's own
    // bottom is a doorway's head, metres up — a stair anchored there would
    // begin in mid-air, and a door ghost would float. Both ask the nearest
    // solid section on the same wall instead.
    static float MasonryFloorAt(WallEdgeSection sec)
    {
        if (sec.openingIndex < 0 || sec.edge == null)
            return sec.bottomY;
        float mid = (sec.tStart + sec.tEnd) * 0.5f;
        float best = sec.bottomY;
        float bestD = float.MaxValue;
        foreach (WallEdgeSection s in sec.edge.SectionsInOrder())
        {
            if (s.openingIndex >= 0)
                continue;
            float d = Mathf.Abs((s.tStart + s.tEnd) * 0.5f - mid);
            if (d < bestD)
            {
                bestD = d;
                best = s.bottomY;
            }
        }
        return best;
    }

    // Why this doorway can't go here, or null. Stated rather than silently
    // refused — same rule as the stair's shortfall.
    string DoorRefusal(WallEdge host, float t, WallEdgeSection sec)
    {
        float[] table = WallEdge.BuildArcTable(host.A, host.control, host.B);
        float total = table[table.Length - 1];
        float mid = WallEdge.ArcAt(table, t);
        // A doorway whose sill sits so high the wall has no head left over
        // it isn't a doorway. Say which floor did it.
        if (sec.topY - doorSill < 0.6f)
            return $"The floor inside stands at {doorSill:0.0}m, leaving no wall"
                + " above it for a doorway.";
        if (total < doorWidth + DoorJamb * 2f)
            return $"Wall too short for a doorway — it needs {doorWidth + DoorJamb * 2f:0.0}m.";
        if (mid - doorWidth * 0.5f < DoorJamb || mid + doorWidth * 0.5f > total - DoorJamb)
            return $"Too close to the wall's end — leave {DoorJamb:0.0}m of jamb.";
        foreach (WallEdge.Opening o in host.openings)
        {
            float other = WallEdge.ArcAt(table, o.t);
            if (Mathf.Abs(other - mid) < (o.width + doorWidth) * 0.5f + DoorJamb)
                return "That would run into the doorway already there.";
        }
        return null;
    }

    // A box standing in the wall where the masonry would go: the opening
    // is a subtraction, so the honest ghost is the volume being removed.
    void ShowDoorGhost(WallEdgeSection sec)
    {
        WallEdge host = doorHost;
        Vector3 p = WallEdge.Evaluate(host.A, host.control, host.B, doorT);
        Vector3 dir = WallEdge.Tangent(host.A, host.control, host.B, doorT).normalized;
        float floor = Mathf.Max(doorSill, host.FloorY);
        float head = Mathf.Max(0.1f, Mathf.Min(doorHead, sec.topY - floor));

        if (doorGhostMesh == null)
            doorGhostMesh = BuildBoxMesh();
        GameObject ghost = GetPooled(ghostPool, 0, previewColor, "PlacementPreview");
        ghost.SetActive(true);
        SetGhostMesh(ghost, doorGhostMesh);
        SetGhostTint(ghost, doorBlocked ? deletePreviewColor : previewColor);
        ghost.transform.SetPositionAndRotation(
            new Vector3(p.x, floor + head * 0.5f, p.z),
            Quaternion.Euler(0f, Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg, 0f));
        // A shade thicker than the wall so the ghost reads as passing all
        // the way through rather than sitting inside the masonry.
        ghost.transform.localScale = new Vector3(doorWidth, head, host.thickness + 0.06f);
        for (int i = 1; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    static Mesh BuildBoxMesh()
    {
        GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh mesh = Object.Instantiate(probe.GetComponent<MeshFilter>().sharedMesh);
        mesh.name = "DoorGhost";
        // Deactivated before it is destroyed: Destroy is deferred, and a
        // stray cube sitting at the world origin for a frame is exactly
        // the sort of thing that reads as a bug.
        probe.SetActive(false);
        Destroy(probe);
        return mesh;
    }

    void CancelDoor()
    {
        doorHost = null;
        doorBlocked = false;
        doorRefusal = null;
        doorStep = 0f;
        HideGhosts();
    }

    // The chain's angle reference at its growing tip: the previous
    // segment's run (pointing back along it, so 180 reads as a straight
    // continuation), or the snapped start's masonry for the first
    // segment. Null for a free-standing first segment — world-absolute
    // stepping applies there.
    float? ChainStepRef(float dragBearing)
    {
        if (roomChain.Count >= 2)
        {
            Vector3 back = roomChain[roomChain.Count - 2] - roomChain[roomChain.Count - 1];
            return Mathf.Atan2(back.z, back.x) * Mathf.Rad2Deg;
        }
        if (roomStartAttach.HasValue)
            return AngleRefBearing(roomStartAttach.Value, dragBearing);
        return null;
    }

    // The masonry a chain end is attached to, as graph nodes — a node
    // snap is that node; a flank landing reaches both its host's ends.
    static IEnumerable<WallNode> AttachNodes(EndSnap snap)
    {
        if (snap.node != null)
        {
            yield return snap.node;
            yield break;
        }
        if (snap.edge != null)
        {
            yield return snap.edge.nodeA;
            yield return snap.edge.nodeB;
        }
    }

    void BuildRoomLivePreview(Vector3 a, Vector3 b, float trimStart = 0f, float trimEnd = 0f)
    {
        // The chain captured its plane at the anchor; a deck chain stands
        // flat on the deck. Set before the cache check.
        ghostTopY = !float.IsNegativeInfinity(roomChainTopY) ? roomChainTopY
            : roomChainBaseY.HasValue ? roomChainBaseY.Value + roomChainHeight : ghostTopY;
        var key = (a, b, roomChainHeight, roomChainTopY, trimStart, trimEnd);
        if (lastRoomKey.HasValue && lastRoomKey.Value == key)
            return;
        lastRoomKey = key;

        foreach (Mesh m in roomLiveMeshes)
            Destroy(m);
        roomLiveMeshes.Clear();
        roomLiveSpecs.Clear();
        AppendRoomSegmentSpecs(a, b, trimStart, trimEnd, roomChainTopY, roomLiveSpecs);
        foreach (WallEdge.SectionSpec spec in roomLiveSpecs)
            roomLiveMeshes.Add(spec.mesh);
        roomLiveBlocked = WallGraph.OverlapsExisting(a, (a + b) * 0.5f, b,
            roomChainBaseY ?? float.NegativeInfinity)
            || LandLaw.SpanOutside(a, (a + b) * 0.5f, b);
    }

    // One chain segment's section specs at the chain's plane. Terrain
    // chains preview fitted to the real ground — rooms never move
    // earth, so the slope the ghost stands on is the slope the commit
    // builds on (a legal footprint is level within the base course).
    void AppendRoomSegmentSpecs(Vector3 a, Vector3 b, float trimStart, float trimEnd,
        float top, List<WallEdge.SectionSpec> into)
    {
        Vector3 da = a, db = b;
        if (trimStart > 0.001f || trimEnd > 0.001f)
        {
            float total = FlatDistance(a, b);
            float budget = Mathf.Max(0.05f, total - 0.1f);
            if (trimStart + trimEnd > budget)
            {
                float scale = budget / (trimStart + trimEnd);
                trimStart *= scale;
                trimEnd *= scale;
            }
            Vector3 dir = (b - a) / Mathf.Max(0.001f, total);
            da = a + dir * trimStart;
            db = b - dir * trimEnd;
        }
        // Same burial a stacked wall gets, so the ghost shows the joint the
        // commit will actually build. Terrain chains sample the ground.
        float segBase = roomChainBaseY ?? float.NegativeInfinity;
        StackFit(null, a, b, roomChainHeight, segBase, out float segSkirt, ref top);
        float floor = roomChainBaseY.HasValue
            ? segBase - segSkirt : float.NegativeInfinity;
        into.AddRange(WallEdge.BuildSpecs(da, (da + db) * 0.5f, db, roomChainHeight,
            gridSize, gridSize, baseStepSize, BaseHeight, top, floor));
    }

    // A click fixed the live segment: its specs and meshes move to the
    // chain's baked set and the live slot starts over.
    void BakeRoomLiveSegment()
    {
        roomBakeCounts.Add((roomLiveSpecs.Count, roomLiveMeshes.Count));
        roomFixedSpecs.AddRange(roomLiveSpecs);
        roomFixedMeshes.AddRange(roomLiveMeshes);
        roomLiveSpecs.Clear();
        roomLiveMeshes.Clear();
        lastRoomKey = null;
        roomLiveBlocked = false;
    }

    // Pops the last baked segment — its vertex off the chain, its specs
    // and meshes off the fixed ghost — so a right click walks the chain
    // back corner by corner. An anchor with no segments cancels outright.
    void UndoRoomChainLeg()
    {
        if (roomChain.Count <= 1 || roomBakeCounts.Count == 0)
        {
            CancelRoomChain();
            return;
        }
        roomChain.RemoveAt(roomChain.Count - 1);
        (int specs, int meshes) = roomBakeCounts[roomBakeCounts.Count - 1];
        roomBakeCounts.RemoveAt(roomBakeCounts.Count - 1);
        for (int i = 0; i < meshes; i++)
        {
            Destroy(roomFixedMeshes[roomFixedMeshes.Count - 1]);
            roomFixedMeshes.RemoveAt(roomFixedMeshes.Count - 1);
        }
        roomFixedSpecs.RemoveRange(roomFixedSpecs.Count - specs, specs);
        lastRoomKey = null;
        roomLiveBlocked = false;
        ShowRoomGhosts();
    }

    // Baked chain in preview blue, live segment gold when the next click
    // would close the loop (red when refused).
    void ShowRoomGhosts()
    {
        // A law warning is about the ROOM, not the segment being drawn —
        // the whole chain reds together.
        Color fixedTint = previewColor;
        int shown = 0;
        foreach (WallEdge.SectionSpec spec in roomFixedSpecs)
        {
            GameObject ghost = GetPooled(ghostPool, shown++, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            SetGhostTint(ghost, fixedTint);
            ghost.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));
            ghost.transform.localScale = Vector3.one;
        }
        Color liveTint = roomLiveBlocked ? deletePreviewColor
            : (roomLiveClosing ? snapColor : previewColor);
        foreach (WallEdge.SectionSpec spec in roomLiveSpecs)
        {
            GameObject ghost = GetPooled(ghostPool, shown++, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            SetGhostTint(ghost, liveTint);
            ghost.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));
            ghost.transform.localScale = Vector3.one;
        }
        for (int i = shown; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // Closes the loop: every chain segment commits through the graph in
    // order (splitting whatever it tees into), then the enclosed face is
    // traced from the last chain edge and ANNOUNCED — the check, without
    // the derivation. The chain's own winding picks the trace side.
    void CommitRoomChain()
    {
        WallEdge lastEdge = null;
        var allCreated = new List<WallEdge>();
        for (int i = 0; i + 1 < roomChain.Count; i++)
        {
            Vector3 a = roomChain[i];
            Vector3 b = roomChain[i + 1];
            // Each segment asks its own support question: a chain can run
            // off one wall's top and onto another's.
            float segBase = roomChainBaseY ?? float.NegativeInfinity;
            float segTop = roomChainTopY;
            StackFit(null, a, b, roomChainHeight, segBase, out float segSkirt, ref segTop);
            List<WallEdge> created = WallGraph.CommitEdge(splineParent, a, (a + b) * 0.5f, b,
                EdgeParamsNow(roomChainHeight, segTop, segBase, segSkirt));
            allCreated.AddRange(created);
            if (created.Count > 0)
                lastEdge = created[created.Count - 1];
        }
        Estate.Pay(Estate.CostOfEdges(allCreated), Estate.KindLabel(CurrentWallKind));
        // Chain wound counter-clockwise → its interior is on the left of
        // travel, which is the side TraceFace walks — trace the last edge
        // along the gesture. Clockwise → trace it reversed.
        if (lastEdge != null)
            Enclosure.AnnounceChainClose(lastEdge, Enclosure.SignedArea(roomChain) > 0f);
        CancelRoomChain();
    }

    void CancelRoomChain()
    {
        roomChain.Clear();
        roomBakeCounts.Clear();
        roomStartAttach = null;
        roomChainBaseY = null;
        roomLiveClosing = false;
        roomLiveBlocked = false;
        lastRoomKey = null;
        foreach (Mesh m in roomFixedMeshes)
            Destroy(m);
        roomFixedMeshes.Clear();
        roomFixedSpecs.Clear();
        foreach (Mesh m in roomLiveMeshes)
            Destroy(m);
        roomLiveMeshes.Clear();
        roomLiveSpecs.Clear();
        HideGhosts();
    }

    // ------------------------------------------------------------------
    // Ground tool: drag a line, get a bench (graded walkable band, or a
    // level one with Shift) or a restore (band returned to the natural
    // hill). Terrain only — walls, decks and cut surfaces are not
    // ground — and every op obeys the deviation law, which TerrainPads
    // enforces per sample.
    // ------------------------------------------------------------------
    void UpdateGround(Mouse mouse, Ray ray, bool overUI)
    {
        HandleGroundSizeScroll(mouse);
        Vector3 point = default;
        bool hasPoint = !overUI && TryGetTerrainPoint(ray, out point);

        // Restore is a PAINT brush: hold and sweep, the ground returns to
        // nature under the brush as you go — the terrain itself is the
        // preview. The circular ghost drapes over the current surface.
        if (GroundTask == GroundAction.Restore)
        {
            if (hasPoint)
                ShowBrushGhost(point, restoreBrushRadius, snapColor);
            else
                HideGhosts();
            if (hasPoint && mouse.leftButton.wasPressedThisFrame)
            {
                restorePaintLast = point;
                restorePainted = 0f;
                // The initial dab: a stamp under the brush before any
                // sweep, so a bare click restores what it touched.
                TerrainPads.RestoreLine(point - new Vector3(0.3f, 0f, 0f),
                    point + new Vector3(0.3f, 0f, 0f), restoreBrushRadius * 2f);
            }
            if (restorePaintLast.HasValue && mouse.leftButton.isPressed && hasPoint)
            {
                float moved = FlatDistance(restorePaintLast.Value, point);
                if (moved >= 1.5f)
                {
                    TerrainPads.RestoreLine(restorePaintLast.Value, point,
                        restoreBrushRadius * 2f);
                    restorePainted += moved;
                    restorePaintLast = point;
                }
            }
            if (restorePaintLast.HasValue && mouse.leftButton.wasReleasedThisFrame)
            {
                if (restorePainted > 0.5f)
                    BuildLog.Add($"Ground restored to nature — {restorePainted:0}m swept.");
                restorePaintLast = null;
            }
            return;
        }

        // Path: circular brush while aiming, then the TERRAIN ITSELF is
        // the preview — each drag frame deforms the real ground to the
        // candidate bench (reverted and reapplied as the endpoint moves,
        // committed as an Op only on release; Esc puts the hill back).
        // The two-tone ribbon rides the deformed surface: blue where
        // earth was raised, red where it was cut. Shift holds the
        // anchor's height for the whole run — a LEVEL path — instead of
        // grading to the far end's ground.
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame
            && groundAnchor.HasValue)
        {
            CancelGround();
            return;
        }
        if (!groundAnchor.HasValue)
        {
            if (hasPoint)
            {
                ShowBrushGhost(point, pathWidth * 0.5f, previewColor);
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    groundAnchor = point;
                    groundAnchorY = ClampToLaw(point, GroundYAt(point));
                }
            }
            else
                HideGhosts();
            return;
        }

        Vector3 a = groundAnchor.Value;
        Vector3 b = hasPoint ? point : a;
        float len = FlatDistance(a, b);
        bool level = keyboard != null
            && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        // End heights the commit will use, law-clamped. The anchor's was
        // captured at the click; the far end samples the PRE-PREVIEW
        // ground (the live deformation must never feed itself), unless
        // Shift levels the whole run at the anchor's height.
        float ya = groundAnchorY;
        float yb = level ? groundAnchorY
            : ClampToLaw(b, TerrainPads.PrePreviewGroundAt(b.x, b.z));

        if (len >= 0.5f)
        {
            if (!groundPreviewLive
                || FlatDistance(b, groundPreviewB) > 0.25f
                || Mathf.Abs(yb - groundPreviewYb) > 0.01f
                || Mathf.Abs(pathWidth - groundPreviewW) > 0.01f)
            {
                TerrainPads.PreviewBench(a, b, pathWidth, ya, yb);
                groundPreviewB = b;
                groundPreviewYb = yb;
                groundPreviewW = pathWidth;
                groundPreviewLive = true;
            }
        }
        else if (groundPreviewLive)
        {
            TerrainPads.PreviewBench(a, a, pathWidth, ya, yb);
            groundPreviewLive = false;
        }
        BuildPathGhost(a, b, pathWidth, ya, yb);
        guideCenter = (a + b) * 0.5f;
        guideBearing = Mathf.Atan2(b.z - a.z, b.x - a.x) * Mathf.Rad2Deg;

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            if (len >= 0.5f)
            {
                // Revert the preview, then walk the same door a direct
                // commit uses — what previewed is exactly what commits.
                TerrainPads.CancelPreview();
                TerrainPads.Bench(a, b, pathWidth, ya, yb);
                if (level)
                    BuildLog.Add($"Path leveled — {len:0}m held at the anchor's height.");
                else
                {
                    float grade = Mathf.Abs(yb - ya) / Mathf.Max(0.5f, len) * 100f;
                    BuildLog.Add($"Path benched — {grade:0}% grade over {len:0}m.");
                }
            }
            CancelGround();
        }
    }

    // Ctrl+scroll sizes the tool in hand, exactly as it dials wall height
    // for the build tools — FreeFlyCamera already stands down from
    // dollying while Ctrl is held, so the wheel means one thing.
    void HandleGroundSizeScroll(Mouse mouse)
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || (!keyboard.leftCtrlKey.isPressed && !keyboard.rightCtrlKey.isPressed))
            return;
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f)
            return;
        if (Mathf.Abs(scroll) >= 100f)
            scroll /= 120f;
        float notches = Mathf.Clamp(scroll, -4f, 4f);
        if (GroundTask == GroundAction.Restore)
            restoreBrushRadius = Mathf.Clamp(restoreBrushRadius + notches * 0.5f,
                MinBrushRadius, MaxBrushRadius);
        else
            pathWidth = Mathf.Clamp(pathWidth + notches * 0.5f,
                MinPathWidth, MaxPathWidth);
    }

    // ------------------------------------------------------------------
    // The slab tools (Docs/BuildingRebuild.md; redrawn as outlines
    // 2026-08-08 — square tiles lost the playtest, squares over an
    // irregular room can't floor it): FOUNDATION outlines author a
    // building's footprint and level on the terrain — the first storey's
    // floor, drawn first, never derived. FLOOR outlines author a
    // between-storey plane, legal only ANCHORED to a wall top or an
    // existing floor. Both are the chain gesture the walls taught: click
    // corners, close on the first one, and the polygon fills as ONE
    // prism.
    //
    // ONE outline, ONE plane: the anchor states it and the whole chain
    // holds it. Ctrl+scroll dials a foundation's plane in quarter steps;
    // the laws (burial, skirt) bound how far the dial can be abused, and
    // where the hill outruns them you terrace — a second foundation at
    // its own plane. A stairwell is drawn AROUND — omission survives as
    // shape, not as a missing tile.
    void UpdateSlab(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        bool foundation = Mode == ToolMode.Foundation;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelSlabChain();
            return;
        }
        // Right click steps the outline back one corner — the anchor
        // included, which abandons the outline like Esc.
        if (rightClick && slabChain.Count > 0)
        {
            slabChain.RemoveAt(slabChain.Count - 1);
            if (slabChain.Count == 0)
                CancelSlabChain();
            return;
        }
        bool free = keyboard != null
            && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        if (foundation)
            HandleSlabPlaneScroll(mouse, keyboard);

        bool has = foundation
            ? ResolveFoundationPoint(ray, free, overUI, out Vector3 point,
                out float plane, out bool snapped)
            : ResolveFloorPoint(ray, free, overUI, out point, out plane,
                out snapped);
        if (!has)
        {
            // Off-target frames (HUD, sky, nothing to anchor to) keep
            // the chain visible where it stands.
            if (slabChain.Count > 0)
            {
                slabTempPoly.Clear();
                slabTempPoly.AddRange(slabChain);
                ghostTopY = slabChainPlane;
                ShowSlabChainGhost(null, slabChainPlane, foundation,
                    slabTempPoly.Count >= 3 && SlabPolyRefusal(slabTempPoly,
                        slabChainPlane, foundation) != null, false);
            }
            else
            {
                HideGhosts();
            }
            return;
        }

        bool closing = slabChain.Count >= 3
            && FlatDistance(point, slabChain[0]) < endpointSnapRadius;
        if (closing)
            point = slabChain[0];

        // The tentative polygon: the chain closed through the cursor. The
        // refusal below IS the commit test run on it, so red at any
        // moment answers "what if you closed here" (one-test doctrine).
        slabTempPoly.Clear();
        slabTempPoly.AddRange(slabChain);
        if (!closing && (slabChain.Count == 0
            || FlatDistance(point, slabChain[slabChain.Count - 1]) >= 0.05f))
            slabTempPoly.Add(point);
        string refusal = slabTempPoly.Count >= 3
            ? SlabPolyRefusal(slabTempPoly, plane, foundation)
            : null;

        ghostTopY = plane;
        guideCenter = point;
        snapTint = snapped || closing;
        ShowSlabChainGhost(point, plane, foundation, refusal != null,
            snapped || closing);

        if (!mouse.leftButton.wasPressedThisFrame || overUI)
            return;
        if (slabChain.Count == 0)
        {
            slabChain.Add(point);
            slabChainPlane = plane;
            slabPlaneDial = 0f;
        }
        else if (closing)
        {
            if (refusal != null)
            {
                BuildLog.Add("Refused: " + refusal);
                return;
            }
            float bottom = foundation
                ? FoundationBottom(slabChain, slabChainPlane)
                : slabChainPlane - SlabTile.Thickness;
            SlabTile made = SlabTile.Create(splineParent, wallMaterial, slabChain,
                slabChainPlane, bottom, foundation,
                foundation ? null : slabWells);
            BuildLog.Add((foundation ? "Foundation" : "Floor")
                + $" — {slabChain.Count} corners,"
                + $" {Mathf.Abs(SlabTile.SignedArea(slabChain)):0.#}m²"
                + $" at {slabChainPlane:0.##}m."
                + (!foundation && slabWells.Count > 0
                    ? $" {slabWells.Count} stairwell"
                        + (slabWells.Count == 1 ? "" : "s") + " cut."
                    : ""));
            Estate.Pay(Estate.CostOfSlab(made), foundation ? "Foundation" : "Floor");
            CancelSlabChain();
        }
        else if (FlatDistance(point, slabChain[slabChain.Count - 1]) >= 0.25f)
        {
            slabChain.Add(point);
        }
    }

    void CancelSlabChain()
    {
        slabChain.Clear();
        slabPlaneDial = 0f;
        HideGhosts();
    }

    // Ctrl+scroll: the foundation's plane, in quarter steps — the same
    // lattice the wall-height dial lands on, so a dialed plane can meet
    // a dialed wall top exactly. Before the anchor it offsets the seated
    // plane; while chaining it moves the stated plane itself. The laws
    // bound it: dial down and the outline buries, up and the skirt
    // refuses.
    void HandleSlabPlaneScroll(Mouse mouse, Keyboard keyboard)
    {
        if (keyboard == null
            || (!keyboard.leftCtrlKey.isPressed && !keyboard.rightCtrlKey.isPressed))
            return;
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f)
            return;
        if (Mathf.Abs(scroll) >= 100f)
            scroll /= 120f;
        float notches = Mathf.Clamp(scroll, -4f, 4f);
        if (slabChain.Count > 0)
            slabChainPlane = Mathf.Round((slabChainPlane
                + notches * SlabTile.FloorStep) / SlabTile.FloorStep)
                * SlabTile.FloorStep;
        else
            slabPlaneDial = Mathf.Round((slabPlaneDial
                + notches * SlabTile.FloorStep) / SlabTile.FloorStep)
                * SlabTile.FloorStep;
    }

    // A foundation corner lives where the terrain is: the cursor names a
    // ground point (the terrain-only ray sees straight through existing
    // slabs, so drawing over the patch still works), snapping onto
    // existing foundation outlines so a new outline extends a level
    // exactly. The ANCHOR names the plane — a snapped anchor adopts its
    // host's (matching a neighbour IS alignment and beats the grid), a
    // free anchor seats at its own ground ceiled to the quarter grid,
    // plus whatever the dial says.
    bool ResolveFoundationPoint(Ray ray, bool free, bool overUI,
        out Vector3 point, out float plane, out bool snapped)
    {
        point = default;
        plane = 0f;
        snapped = false;
        if (overUI || !TryGetTerrainPoint(ray, out Vector3 p))
            return false;
        point = new Vector3(p.x, 0f, p.z);

        float hostPlane = 0f;
        if (!free && TrySlabOutlineSnap(true, ref point, ref hostPlane))
            snapped = true;

        if (slabChain.Count > 0)
        {
            plane = slabChainPlane;
            return true;
        }
        plane = snapped
            ? hostPlane
            : Mathf.Ceil(p.y / SlabTile.FloorStep) * SlabTile.FloorStep
                + slabPlaneDial;
        return true;
    }

    // A floor outline exists only by CONNECTION: the anchor must take its
    // plane from a wall top or an existing floor. After the anchor the
    // cursor maps onto the PLANE itself (the ray meets y = plane), so the
    // outline can cross open air — a stairwell is drawn around, not tiled
    // around. Corners snap to wall nodes carrying masonry at the plane,
    // and to existing floor outlines at it.
    bool ResolveFloorPoint(Ray ray, bool free, bool overUI,
        out Vector3 point, out float plane, out bool snapped)
    {
        point = default;
        plane = slabChain.Count > 0 ? slabChainPlane : 0f;
        snapped = false;
        if (overUI)
            return false;

        if (slabChain.Count == 0)
        {
            if (!TryGetFirstHit(ray, out RaycastHit best))
                return false;
            SlabTile tile = best.collider.GetComponent<SlabTile>();
            if (tile != null && !tile.isFoundation)
            {
                point = new Vector3(best.point.x, 0f, best.point.z);
                plane = tile.topY;
                float hostPlane = plane;
                if (!free)
                    TrySlabOutlineSnap(false, ref point, ref hostPlane);
                snapped = true;
                return true;
            }
            WallEdgeSection section = best.collider.GetComponent<WallEdgeSection>();
            if (section != null && section.edge != null && best.normal.y > 0.7f
                && !CutawayView.IsCutSurface(best.point))
            {
                point = new Vector3(best.point.x, 0f, best.point.z);
                plane = section.topY;
                if (!free && TryFloorNodeSnap(point, plane, out Vector3 nodePoint))
                    point = nodePoint;
                snapped = true;
                return true;
            }
            return false;
        }

        if (Mathf.Abs(ray.direction.y) < 0.0001f)
            return false;
        float t = (plane - ray.origin.y) / ray.direction.y;
        if (t <= 0f)
            return false;
        Vector3 onPlane = ray.origin + ray.direction * t;
        point = new Vector3(onPlane.x, 0f, onPlane.z);
        if (!free)
        {
            if (TryFloorNodeSnap(point, plane, out Vector3 nodePoint))
            {
                point = nodePoint;
                snapped = true;
            }
            else
            {
                Vector3 snapPt = point;
                float hostPlane = plane;
                if (TrySlabOutlineSnap(false, ref snapPt, ref hostPlane)
                    && Mathf.Abs(hostPlane - plane) < 0.25f)
                {
                    point = snapPt;
                    snapped = true;
                }
            }
        }
        return true;
    }

    // The nearest non-trigger hit under the cursor, whatever it is.
    bool TryGetFirstHit(Ray ray, out RaycastHit best)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 3000f);
        best = default;
        float bestDist = float.MaxValue;
        foreach (RaycastHit h in hits)
        {
            if (h.collider == null || h.collider.isTrigger)
                continue;
            if (h.distance < bestDist)
            {
                bestDist = h.distance;
                best = h;
            }
        }
        return bestDist != float.MaxValue;
    }

    // Snap a chain corner onto an existing outline of the same kind — its
    // corners first (weld exactly), then its edges (slide onto the line).
    // Reports the host's plane so a foundation ANCHOR can adopt the level
    // it is extending.
    bool TrySlabOutlineSnap(bool foundations, ref Vector3 point, ref float hostPlane)
    {
        SlabTile bestTile = null;
        Vector3 bestPt = default;
        float best = 0.5f;
        foreach (SlabTile t in SlabTile.All)
        {
            if (t == null || t.isFoundation != foundations)
                continue;
            foreach (Vector3 v in t.verts)
            {
                float d = FlatDistance(point, v);
                if (d < best)
                {
                    best = d;
                    bestTile = t;
                    bestPt = v;
                }
            }
        }
        if (bestTile == null)
        {
            best = 0.4f;
            foreach (SlabTile t in SlabTile.All)
            {
                if (t == null || t.isFoundation != foundations)
                    continue;
                for (int i = 0; i < t.verts.Count; i++)
                {
                    Vector3 q = ClosestOnSegment(t.verts[i],
                        t.verts[(i + 1) % t.verts.Count], point);
                    float d = FlatDistance(point, q);
                    if (d < best)
                    {
                        best = d;
                        bestTile = t;
                        bestPt = q;
                    }
                }
            }
        }
        if (bestTile == null)
            return false;
        point = new Vector3(bestPt.x, 0f, bestPt.z);
        hostPlane = bestTile.topY;
        return true;
    }

    static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        ab.y = 0f;
        float len2 = ab.sqrMagnitude;
        float t = len2 > 0.000001f
            ? Mathf.Clamp01(((p.x - a.x) * ab.x + (p.z - a.z) * ab.z) / len2)
            : 0f;
        return new Vector3(a.x + ab.x * t, 0f, a.z + ab.z * t);
    }

    // How close an aim must be to grab the rim's LINE (its corners grab
    // at endpointSnapRadius, a rim corner being a would-be node). The aim
    // is measured against the outline AND the inset centerline both —
    // pointing at the visible slab edge and pointing at where the wall
    // will stand are the same intent.
    const float RimEdgeSnapRadius = 0.45f;

    // Walls hug a slab's rim: a point over the slab pulls to the outline
    // inset half a wall thickness, so the committed wall's outer FACE
    // lies exactly on the slab edge. Near an outline corner the pull is
    // to the MITERED corner point — the spot where the inset lines of
    // both adjacent edges meet — so walls drawn along adjacent edges
    // close on ONE shared node whatever the corner's angle. Yaw reports
    // the rim's bearing at the snap so the hover stub lies along it.
    // A ray that lands on TERRAIN just past a slab's rim still means that
    // slab. A raised pad's skirt catches those rays, but a near-grade pad
    // (the free-anchor default: plane a fraction above ground) exposes no
    // skirt, and a terraced pad's uphill rim can sit flush with or under
    // the hillside — so without this, the grab zone OUTSIDE the outline
    // simply doesn't exist exactly where foundations usually stand. The
    // nearest-plane outline within rim reach adopts the hover, making
    // the terrain hit a slab hit wholesale. The vertical band is
    // MaxSkirt plus a metre: the skirt law bounds ground under the
    // FOOTPRINT, and the hillside just outside a legal pad can drop
    // past it within the grab radius (verified: a lawful rim missed at
    // dy 2.7). A slab a real storey away still stays out of reach, and
    // where terraces crowd, nearest-plane wins.
    const float RimAdoptBand = SlabTile.MaxSkirt + 1f;

    SlabTile RimSlabFromTerrain(Vector3 raw, float hitY)
    {
        SlabTile best = null;
        float bestDy = float.MaxValue;
        foreach (SlabTile s in SlabTile.All)
        {
            if (s == null)
                continue;
            float dy = Mathf.Abs(s.topY - hitY);
            if (dy > RimAdoptBand || dy >= bestDy)
                continue;
            if (TrySlabRimSnap(s, raw, out _, out _))
            {
                best = s;
                bestDy = dy;
            }
        }
        return best;
    }

    bool TrySlabRimSnap(SlabTile tile, Vector3 raw, out Vector3 point, out float yaw)
    {
        point = default;
        yaw = 0f;
        List<Vector3> verts = tile.verts;
        int n = verts.Count;
        if (n < 3)
            return false;
        float half = gridSize * 0.5f;

        // Every vertex's inset corner. Verts are CCW (SlabTile.Create
        // normalizes), so the interior is to the LEFT of travel.
        var inset = new Vector3[n];
        var valid = new bool[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 inPrev = RimInward(verts[(i + n - 1) % n], verts[i]);
            Vector3 inNext = RimInward(verts[i], verts[(i + 1) % n]);
            float denom = 1f + Vector3.Dot(inPrev, inNext);
            // A near-reversal spike would throw its miter to infinity;
            // no drawable outline has one, but refuse rather than fling.
            valid[i] = denom > 0.05f;
            if (valid[i])
                inset[i] = verts[i] + (inPrev + inNext) * (half / denom);
        }

        // Corners first — aiming at the outline vertex or at its inset
        // corner both mean the corner.
        int bestCorner = -1;
        float best = endpointSnapRadius;
        for (int i = 0; i < n; i++)
        {
            if (!valid[i])
                continue;
            float d = Mathf.Min(FlatDistance(raw, inset[i]),
                FlatDistance(raw, verts[i]));
            if (d < best)
            {
                best = d;
                bestCorner = i;
            }
        }
        if (bestCorner >= 0)
        {
            point = new Vector3(inset[bestCorner].x, 0f, inset[bestCorner].z);
            // The stub lies along whichever adjacent edge the aim favours.
            int prev = (bestCorner + n - 1) % n;
            int next = (bestCorner + 1) % n;
            float dPrev = FlatDistance(raw,
                ClosestOnSegment(verts[prev], verts[bestCorner], raw));
            float dNext = FlatDistance(raw,
                ClosestOnSegment(verts[bestCorner], verts[next], raw));
            Vector3 along = dNext <= dPrev
                ? verts[next] - verts[bestCorner]
                : verts[bestCorner] - verts[prev];
            yaw = Mathf.Atan2(-along.z, along.x) * Mathf.Rad2Deg;
            return true;
        }

        // Then the rim lines, corner-miter to corner-miter.
        best = RimEdgeSnapRadius;
        int bestEdge = -1;
        Vector3 bestPt = default;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (!valid[i] || !valid[j])
                continue;
            Vector3 q = ClosestOnSegment(inset[i], inset[j], raw);
            float d = Mathf.Min(FlatDistance(raw, q),
                FlatDistance(raw, ClosestOnSegment(verts[i], verts[j], raw)));
            if (d < best)
            {
                best = d;
                bestEdge = i;
                bestPt = q;
            }
        }
        if (bestEdge < 0)
            return false;
        point = new Vector3(bestPt.x, 0f, bestPt.z);
        Vector3 dir = verts[(bestEdge + 1) % n] - verts[bestEdge];
        yaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg;
        return true;
    }

    // Unit inward (interior-side) normal of the outline edge a→b. The
    // prism's outward side normal is (dz, -dx) (SlabTile.BuildPrism);
    // this is its negation.
    static Vector3 RimInward(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        d.y = 0f;
        Vector3 inward = new Vector3(-d.z, 0f, d.x);
        return inward.sqrMagnitude > 0.000001f ? inward.normalized : Vector3.zero;
    }

    // A wall corner carrying masonry AT the plane — the floor outline's
    // strongest snap: flooring a room is clicking its corners. The end
    // section's top is the test (a node is an edge END here, always).
    bool TryFloorNodeSnap(Vector3 point, float plane, out Vector3 snapped)
    {
        snapped = default;
        WallNode bestNode = null;
        float best = endpointSnapRadius;
        foreach (WallNode n in WallNode.All)
        {
            if (n == null)
                continue;
            float d = FlatDistance(point, n.point);
            if (d >= best)
                continue;
            foreach (WallNode.Member m in n.members)
            {
                if (m.edge == null)
                    continue;
                var sections = m.edge.SectionsInOrder();
                if (sections.Count == 0)
                    continue;
                WallEdgeSection end = m.atStart
                    ? sections[0] : sections[sections.Count - 1];
                if (end != null && Mathf.Abs(end.topY - plane) < 0.2f)
                {
                    best = d;
                    bestNode = n;
                    break;
                }
            }
        }
        if (bestNode == null)
            return false;
        snapped = new Vector3(bestNode.point.x, 0f, bestNode.point.z);
        return true;
    }

    // THE commit test, run live on the tentative polygon every frame —
    // the returned string doubles as the refusal log line, so what the
    // red ghost means and what the click says are one fact (one-test
    // doctrine).
    string SlabPolyRefusal(List<Vector3> poly, float plane, bool foundation)
    {
        slabWells.Clear();
        if (SlabTile.SelfIntersects(poly))
            return "the outline crosses itself.";
        if (Mathf.Abs(SlabTile.SignedArea(poly)) < 0.25f)
            return "the outline is too small to be a slab.";
        // The castle-grounds law: the lord builds on castle land and
        // nowhere else (LandLaw — stands down when no castle parcel
        // exists, so old saves keep building).
        if (LandLaw.PolyOutside(poly))
            return "outside your castle grounds.";
        foreach (SlabTile t in SlabTile.All)
        {
            if (t == null || t.isFoundation != foundation)
                continue;
            if (Mathf.Abs(t.topY - plane) > 0.25f)
                continue;
            if (SlabTile.PolyOverlaps(poly, t.verts))
                return foundation
                    ? "the outline overlaps a foundation at this level."
                    : "the outline overlaps a floor at this plane.";
        }
        if (foundation)
        {
            SamplePolyGround(poly, plane, out float minG, out float buriedFrac);
            // Mostly clipping through terrain is buried, not built; dips
            // never refuse — the skirt spans them (down to MaxSkirt,
            // past which a plane is a stilt). Together the two laws bound
            // the plane dial, and where the hill outruns them, terrace.
            if (buriedFrac > 0.5f)
                return "mostly buried — dial the plane up, or terrace.";
            if (plane - minG > SlabTile.MaxSkirt + 0.01f)
                return $"the skirt would drop over {SlabTile.MaxSkirt:0}m"
                    + " — dial the plane down, or terrace.";
        }
        else
        {
            // Each stair the floor is drawn over demands its own well —
            // stamped here, shown by the ghost, stored by the commit.
            string wells = CollectStairWells(poly, plane, slabWells);
            if (wells != null)
                return wells;
        }
        return null;
    }

    // Ground extremes and the buried fraction under an outline: a grid
    // over the bounding box clipped to the polygon, plus the vertices
    // themselves so a sliver never goes unsampled.
    void SamplePolyGround(List<Vector3> poly, float plane,
        out float minG, out float buriedFrac)
    {
        minG = float.MaxValue;
        int total = 0, buried = 0;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 v in poly)
        {
            minX = Mathf.Min(minX, v.x);
            maxX = Mathf.Max(maxX, v.x);
            minZ = Mathf.Min(minZ, v.z);
            maxZ = Mathf.Max(maxZ, v.z);
            float g = GroundYAt(v);
            minG = Mathf.Min(minG, g);
            total++;
            if (g > plane + 0.05f)
                buried++;
        }
        float step = Mathf.Max(0.5f,
            Mathf.Max(maxX - minX, maxZ - minZ) / 14f);
        for (float x = minX + step * 0.5f; x < maxX; x += step)
            for (float z = minZ + step * 0.5f; z < maxZ; z += step)
            {
                Vector3 p = new Vector3(x, 0f, z);
                if (!SlabTile.Contains(poly, p))
                    continue;
                float g = GroundYAt(p);
                minG = Mathf.Min(minG, g);
                total++;
                if (g > plane + 0.05f)
                    buried++;
            }
        buriedFrac = total > 0 ? buried / (float)total : 0f;
    }

    // A foundation reaches down past the lowest ground under it so dips
    // near its edges stay closed — the skirt convention, unchanged from
    // the tiles.
    float FoundationBottom(List<Vector3> poly, float plane)
    {
        SamplePolyGround(poly, plane, out float minG, out _);
        float bottom = Mathf.Floor(minG / baseStepSize) * baseStepSize - 0.5f;
        return Mathf.Min(bottom, plane - 0.25f);
    }

    // ------------------------------------------------------------------
    // Stairwells — the law WallRoom's derived decks carried, revived for
    // drawn floors (2026-08-09): each stair demands its own well.
    //
    // How much CLEAR head the well guarantees under the floor's UNDERSIDE
    // — deliberately far more than a body needs (WalkMode stands 1.8m),
    // and deliberately not computed. Sizing this to the minimum was a
    // mistake twice over in the old system; a stairwell bigger than it
    // has to be costs a little deck nobody stands on, one a few
    // centimetres too small costs a head, every single climb.
    const float StairHeadroom = 3f;
    // A stair butts flush against a wall's inner face, and a floor's
    // outline runs to the wall NODES — so the well's natural footprint
    // can touch the outline, and a keyhole bridge needs a hole that is
    // strictly interior. The well is drawn a lip narrower than the
    // treads; the coplanar lip over the landing's outer centimetres
    // reads as a joint line.
    const float WellLip = 0.05f;
    // And the hole opens this much further down the run again, because a
    // climber is a body, not a point: at the well's edge you stand on the
    // tread under your leading foot with the back of your skull still
    // under the floor. Generous for the same reason Headroom is.
    const float WellLead = 1f;
    const int WellSamplesPerSpan = 6;

    // The well each standing stair demands through a floor at `plane`
    // over `poly`, written into `into`. Returns a refusal instead when
    // the outline's edge CROSSES a well — a floor may cover a stair
    // (the well is cut) or stay clear of it, but not clip it. Run by
    // SlabPolyRefusal, so the ghost's red and the closing click are one
    // fact and the wells the ghost shows are the wells the commit stores.
    string CollectStairWells(List<Vector3> poly, float plane,
        List<SlabTile.Well> into)
    {
        into.Clear();
        foreach (WallStair stair in WallStair.All)
        {
            if (stair == null)
                continue;
            List<Vector3> fp = StairWellFootprint(stair, plane);
            if (fp == null)
                continue;
            // Wholly in means a well; wholly out means no business here;
            // anything between — a proper edge crossing, or vertices on
            // both sides (a cut that threads exactly through the well's
            // sample points strictly crosses nothing) — is a clip.
            int inside = 0;
            foreach (Vector3 v in fp)
                if (SlabTile.Contains(poly, v))
                    inside++;
            if (PolysCross(fp, poly) || (inside > 0 && inside < fp.Count))
                return "the outline clips a stairwell — cover the stair"
                    + " or draw around it.";
            if (inside == fp.Count)
                into.Add(new SlabTile.Well(stair, fp));
            else if (SlabTile.Contains(fp, poly[0]))
                return "the outline sits inside a stairwell.";
        }
        return null;
    }

    // A deleted stair takes its wells with it: every floor it opened
    // heals in the same act. Explicit at the delete site (the old
    // NotifyStairGone's job), never a destroy hook — a LOAD also destroys
    // stairs, and healing tiles that are themselves being replaced would
    // be wasted work at best. Ownerless wells (owner already gone, or a
    // save predating owners) stay: delete and redraw the floor.
    void CloseStairWells(WallStair stair)
    {
        foreach (SlabTile tile in SlabTile.All)
        {
            if (tile == null || tile.isFoundation)
                continue;
            if (tile.wells.RemoveAll(w => w.owner == stair) > 0)
            {
                tile.Rebuild();
                BuildLog.Add("Stairwell closed — the floor healed.");
            }
        }
    }

    static bool PolysCross(List<Vector3> a, List<Vector3> b)
    {
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (SlabTile.SegmentsCross(a[i], a[(i + 1) % a.Count],
                        b[j], b[(j + 1) % b.Count]))
                    return true;
        return false;
    }

    // The plan footprint the climb needs open through a floor at `plane`:
    // from the tread that first comes within Headroom of the underside,
    // through the landing at the top. The landing matters as much as the
    // climb — it finishes flush AT the floor plane, so left uncut it
    // would be coplanar with the deck it is supposed to arrive on.
    List<Vector3> StairWellFootprint(WallStair stair, float plane)
    {
        if (stair.spans.Count == 0)
            return null;
        float rise = stair.topY - stair.bottomY;
        if (rise < 0.05f)
            return null;
        // The elevation a climber's feet are at when their head first
        // needs the hole — Headroom of clear air under the SOFFIT.
        float openAt = plane - SlabTile.Thickness - StairHeadroom;
        // Standing on this floor, or never reaching it: no hole either way.
        if (stair.bottomY >= plane - 0.01f || stair.topY <= openAt + 0.01f)
            return null;

        float runArc = stair.runArc > 0.01f ? stair.runArc : stair.TotalArc;
        // Where along the run that elevation falls — counted in TREADS,
        // not along the line between the stair's ends. A stair is not a
        // ramp: the step you stand on is up to a whole riser above that
        // line, and taking the line for the climb is what once let a
        // well be cut a tread and a half too late.
        int steps = WallStair.StepsFor(rise);
        float riser = rise / steps;
        float going = runArc / steps;
        // The last tread whose TOP still leaves full Headroom under the
        // soffit. Everything above it needs the hole.
        int lastClear = Mathf.FloorToInt((openAt - stair.bottomY) / riser);
        float from = Mathf.Clamp(lastClear * going - WellLead, 0f, runArc);
        return StairStrip(stair, from, stair.TotalArc,
            stair.width * 0.5f - WellLip);
    }

    // The stair's own curve, walked between two arc positions and given
    // a width: the run's plan shape, corners and all, rather than a box
    // around it. A stair that turns a corner gets a hole that turns too.
    static List<Vector3> StairStrip(WallStair stair, float from, float to,
        float half)
    {
        var left = new List<Vector3>();
        var right = new List<Vector3>();
        foreach (WallStair.Span sp in stair.spans)
        {
            float lo = Mathf.Max(from, sp.arcStart);
            float hi = Mathf.Min(to, sp.arcEnd);
            if (hi - lo < 0.01f)
                continue;
            float[] table = WallEdge.BuildArcTable(sp.s, sp.c, sp.e);
            for (int i = 0; i <= WellSamplesPerSpan; i++)
            {
                float arc = Mathf.Lerp(lo, hi,
                    (float)i / WellSamplesPerSpan) - sp.arcStart;
                float t = WallEdge.TAtArc(table, arc);
                Vector3 p = WallEdge.Evaluate(sp.s, sp.c, sp.e, t);
                Vector3 d = WallEdge.Tangent(sp.s, sp.c, sp.e, t);
                d.y = 0f;
                if (d.sqrMagnitude < 1e-8f)
                    continue;
                d.Normalize();
                Vector3 side = new Vector3(-d.z, 0f, d.x) * half;
                left.Add(new Vector3(p.x + side.x, 0f, p.z + side.z));
                right.Add(new Vector3(p.x - side.x, 0f, p.z - side.z));
            }
        }
        if (left.Count < 2)
            return null;
        right.Reverse();
        var poly = new List<Vector3>(left);
        poly.AddRange(right);
        poly = DedupeOutline(poly);
        if (poly.Count < 3 || Mathf.Abs(SlabTile.SignedArea(poly)) < 0.05f)
            return null;
        if (SlabTile.SignedArea(poly) < 0f)
            poly.Reverse();
        return poly;
    }

    // Span joints repeat a point, and ear clipping treats a repeat as a
    // zero-area ear it can never remove.
    static List<Vector3> DedupeOutline(List<Vector3> poly)
    {
        var result = new List<Vector3>(poly.Count);
        foreach (Vector3 p in poly)
            if (result.Count == 0
                || (result[result.Count - 1] - p).sqrMagnitude > 4e-4f)
                result.Add(p);
        while (result.Count > 1
            && (result[0] - result[result.Count - 1]).sqrMagnitude <= 4e-4f)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    // The chain on screen: pooled ghost 0 is the FILL — the tentative
    // polygon as the very prism the closing click would commit — and the
    // rest are corner markers, the first corner reading larger once the
    // outline can close on it. One corner plus the cursor draws a thin
    // blade of slab instead: the outline has no area yet, but the plane
    // and the line it will grow from are already honest.
    void ShowSlabChainGhost(Vector3? cursor, float plane, bool foundation,
        bool refused, bool snapped)
    {
        Color tint = refused ? deletePreviewColor
            : snapped ? snapColor : previewColor;
        if (slabPreviewMesh == null)
            slabPreviewMesh = new Mesh { name = "SlabChainPreview" };

        float bottom = foundation && slabTempPoly.Count >= 3
            ? FoundationBottom(slabTempPoly, plane)
            : plane - SlabTile.Thickness;
        bool drewFill = false;
        if (slabTempPoly.Count >= 3)
        {
            // A floor's ghost carries the stairwells the commit would cut
            // (SlabPolyRefusal stamped them this frame) — the hole is
            // visible before the closing click, not a surprise after it.
            SlabTile.BuildPrism(slabPreviewMesh, slabTempPoly,
                plane + 0.002f, bottom, Vector3.zero,
                foundation ? null : slabWells);
            drewFill = slabPreviewMesh.vertexCount > 0;
        }
        else if (slabTempPoly.Count == 2)
        {
            Vector3 d = slabTempPoly[1] - slabTempPoly[0];
            d.y = 0f;
            if (d.sqrMagnitude > 0.0001f)
            {
                Vector3 s = new Vector3(d.z, 0f, -d.x).normalized * 0.05f;
                slabBladePoly.Clear();
                slabBladePoly.Add(slabTempPoly[0] - s);
                slabBladePoly.Add(slabTempPoly[0] + s);
                slabBladePoly.Add(slabTempPoly[1] + s);
                slabBladePoly.Add(slabTempPoly[1] - s);
                SlabTile.BuildPrism(slabPreviewMesh, slabBladePoly,
                    plane + 0.002f, plane - SlabTile.Thickness, Vector3.zero);
                drewFill = slabPreviewMesh.vertexCount > 0;
            }
        }
        GameObject fill = GetPooled(ghostPool, 0, tint, "PlacementPreview");
        fill.SetActive(drewFill);
        if (drewFill)
        {
            SetGhostMesh(fill, slabPreviewMesh);
            SetGhostTint(fill, tint);
            fill.transform.SetPositionAndRotation(Vector3.zero,
                Quaternion.identity);
            fill.transform.localScale = Vector3.one;
        }

        int idx = 1;
        void Marker(Vector3 at, Color c, float side)
        {
            GameObject m = GetPooled(ghostPool, idx++, c, "PlacementPreview");
            m.SetActive(true);
            SetGhostMesh(m, SlabTile.SharedCube());
            SetGhostTint(m, c);
            m.transform.SetPositionAndRotation(
                new Vector3(at.x, plane + 0.1f, at.z), Quaternion.identity);
            m.transform.localScale = new Vector3(side, 0.2f, side);
        }
        for (int i = 0; i < slabChain.Count; i++)
            Marker(slabChain[i],
                i == 0 && slabChain.Count >= 3 ? snapColor : tint,
                i == 0 ? 0.4f : 0.28f);
        if (cursor.HasValue)
            Marker(cursor.Value, tint, 0.28f);
        for (; idx < ghostPool.Count; idx++)
            ghostPool[idx].SetActive(false);
    }

    // The terrain under the cursor and nothing else: not a wall, not a
    // deck, and not a cutaway cross-section (the type filter refuses
    // those before their upward normals can lie about being ground).
    bool TryGetTerrainPoint(Ray ray, out Vector3 point)
    {
        point = default;
        var hits = Physics.RaycastAll(ray, 3000f);
        float best = float.MaxValue;
        foreach (RaycastHit h in hits)
        {
            if (h.collider == null || h.collider.isTrigger)
                continue;
            if (!(h.collider is TerrainCollider))
                continue;
            if (h.distance < best)
            {
                best = h.distance;
                point = h.point;
            }
        }
        return best < float.MaxValue;
    }

    float GroundYAt(Vector3 p)
    {
        return Ground.Any ? Ground.HeightAt(p) : 0f;
    }

    float ClampToLaw(Vector3 p, float y)
    {
        float natural = TerrainPads.PristineGroundAt(p.x, p.z);
        return Mathf.Clamp(y, natural - TerrainPads.MaxDeviation,
            natural + TerrainPads.MaxDeviation);
    }

    // The path drag's ribbon: the future surface at bench height, split
    // into a FILL mesh (bench above current ground — earth raised, blue)
    // and a CUT mesh (bench below it — earth dug, red), so the drag reads
    // as the alteration it is. Each rung drapes toward the current ground
    // at its own station; the profile is the law-clamped grade the commit
    // computes, so the ribbon IS the ground the release builds.
    void BuildPathGhost(Vector3 a, Vector3 b, float width, float ya, float yb)
    {
        if (groundGhostMesh == null)
            groundGhostMesh = new Mesh { name = "GroundGhostFill" };
        if (groundGhostCutMesh == null)
            groundGhostCutMesh = new Mesh { name = "GroundGhostCut" };
        int n = Mathf.Max(1, Mathf.CeilToInt(FlatDistance(a, b)));
        Vector3 dir = b - a;
        dir.y = 0f;
        dir = dir.sqrMagnitude < 0.0001f ? Vector3.forward : dir.normalized;
        var side = new Vector3(-dir.z, 0f, dir.x) * (width * 0.5f);
        var fillV = new List<Vector3>(); var fillT = new List<int>();
        var cutV = new List<Vector3>(); var cutT = new List<int>();
        Vector3 PointAt(int i)
        {
            float t = (float)i / n;
            Vector3 p = Vector3.Lerp(a, b, t);
            p.y = ClampToLaw(p, Mathf.Lerp(ya, yb, t)) + 0.05f;
            return p;
        }
        for (int i = 0; i < n; i++)
        {
            Vector3 p0 = PointAt(i), p1 = PointAt(i + 1);
            Vector3 mid = (p0 + p1) * 0.5f;
            // Against the PRE-preview ground: the live deformation has
            // already moved the surface to the bench, so the current
            // ground would call every rung a wash.
            bool cutting = mid.y - 0.05f
                < TerrainPads.PrePreviewGroundAt(mid.x, mid.z) - 0.05f;
            var V = cutting ? cutV : fillV;
            var T = cutting ? cutT : fillT;
            int v = V.Count;
            V.Add(p0 - side); V.Add(p0 + side); V.Add(p1 - side); V.Add(p1 + side);
            T.Add(v); T.Add(v + 2); T.Add(v + 1);
            T.Add(v + 1); T.Add(v + 2); T.Add(v + 3);
        }
        groundGhostMesh.Clear();
        groundGhostMesh.SetVertices(fillV);
        groundGhostMesh.SetTriangles(fillT, 0);
        groundGhostMesh.RecalculateNormals();
        groundGhostCutMesh.Clear();
        groundGhostCutMesh.SetVertices(cutV);
        groundGhostCutMesh.SetTriangles(cutT, 0);
        groundGhostCutMesh.RecalculateNormals();

        ShowWorldGhost(0, groundGhostMesh, previewColor);
        ShowWorldGhost(1, groundGhostCutMesh, deletePreviewColor);
        for (int i = 2; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // A circular brush cursor draped onto the terrain surface — each rim
    // vertex sits on the ground under it, so the ring molds to the hill
    // instead of floating as a flat disc.
    void ShowBrushGhost(Vector3 center, float radius, Color tint)
    {
        if (brushGhostMesh == null)
            brushGhostMesh = new Mesh { name = "GroundBrush" };
        const int Segs = 28;
        var verts = new Vector3[Segs + 1];
        var tris = new int[Segs * 3];
        verts[0] = new Vector3(center.x, GroundYAt(center) + 0.08f, center.z);
        for (int i = 0; i < Segs; i++)
        {
            float ang = i / (float)Segs * Mathf.PI * 2f;
            var p = new Vector3(center.x + Mathf.Cos(ang) * radius, 0f,
                center.z + Mathf.Sin(ang) * radius);
            p.y = GroundYAt(p) + 0.08f;
            verts[i + 1] = p;
            tris[i * 3] = 0;
            tris[i * 3 + 1] = 1 + (i + 1) % Segs;
            tris[i * 3 + 2] = 1 + i;
        }
        brushGhostMesh.Clear();
        brushGhostMesh.vertices = verts;
        brushGhostMesh.triangles = tris;
        brushGhostMesh.RecalculateNormals();
        ShowWorldGhost(0, brushGhostMesh, tint);
        for (int i = 1; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    void ShowWorldGhost(int slot, Mesh mesh, Color tint)
    {
        GameObject ghost = GetPooled(ghostPool, slot, previewColor, "PlacementPreview");
        ghost.SetActive(true);
        SetGhostMesh(ghost, mesh);
        SetGhostTint(ghost, tint);
        ghost.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        ghost.transform.localScale = Vector3.one;
    }

    void CancelGround()
    {
        // Any live path preview goes back to the hill it borrowed.
        TerrainPads.CancelPreview();
        groundPreviewLive = false;
        groundAnchor = null;
        restorePaintLast = null;
        restorePainted = 0f;
        if (groundGhostMesh != null)
        {
            Destroy(groundGhostMesh);
            groundGhostMesh = null;
        }
        if (groundGhostCutMesh != null)
        {
            Destroy(groundGhostCutMesh);
            groundGhostCutMesh = null;
        }
        if (brushGhostMesh != null)
        {
            Destroy(brushGhostMesh);
            brushGhostMesh = null;
        }
        HideGhosts();
    }

    // Resolves the cursor for both build shapes. Room slabs AND wall top
    // faces are elevated ground, nodes chain, a wall's flank grabs the
    // point onto its centerline (a T-split at commit), terrain resolves
    // to the exact point under the cursor, and Alt returns the raw
    // surface point.
    bool TryGetCurveTarget(Ray ray, bool freePlace, out Vector3 point, out Vector3 rawSurface)
    {
        point = default;
        rawSurface = default;
        hoverRideHost = null;
        hoverSnapBase = null;
        hoverSlabTile = null;

        RaycastHit first = default;
        bool hasHit = false;
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            first = hit;
            hasHit = true;
            break;
        }
        if (!hasHit)
            return false;

        rawSurface = new Vector3(first.point.x, 0f, first.point.z);

        // Elevated ground: build ON it, exactly at the cursor. Two kinds —
        // a room slab, and the TOP FACE of a wall (which is how a storey
        // gets built directly over the one below, rather than inset onto
        // a deck). Snaps below stay live but filter to that base's own
        // walls — the masonry a storey down never grabs the gesture.
        SlabTile slabTile = first.collider.GetComponent<SlabTile>();
        // A terrain hit within rim reach of a slab's outline adopts the
        // slab wholesale — same storey flip as pointing at its top face
        // (RimSlabFromTerrain has the why). Alt keeps terrain meaning
        // terrain: a free-placed point beside a pad is a terrain wall.
        if (slabTile == null && !freePlace && first.collider is TerrainCollider)
            slabTile = RimSlabFromTerrain(rawSurface, first.point.y);
        WallEdgeSection hitSection = first.collider.GetComponent<WallEdgeSection>();
        float? deckY = null;
        // A slab tile is a deck: elevated ground, free-form building,
        // no ride lock.
        if (slabTile != null)
        {
            deckY = slabTile.topY;
            hoverBaseY = deckY;
            hoverSlabTile = slabTile;
        }
        // A cut-open wall's cross-section is not a wall top: it has the
        // same upward normal, but stacking on it would build at the wall's
        // real top, metres above the surface being pointed at.
        else if (hitSection != null && first.normal.y > 0.7f
                 && !CutawayView.IsCutSurface(first.point))
        {
            deckY = hitSection.topY;
            hoverBaseY = deckY;
            hoverRideHost = hitSection.edge;
        }
        // Aiming at a wall's SIDE names that wall's storey even though the
        // gesture isn't standing on anything — the point stays on the
        // terrain (or on whatever deck the wall itself rises from), but the
        // snap search now knows which of a stack's co-located walls the
        // cursor meant. A snapped elevated wall still carries its storey in
        // through AdoptSnapElevation, exactly as before.
        hoverSnapBase = deckY;
        if (!deckY.HasValue && hitSection != null && hitSection.edge != null)
            hoverSnapBase = hitSection.edge.baseY;

        if (freePlace)
        {
            // Alt keeps the storey but drops the wall below as a guide —
            // it is the one way to put masonry somewhere other than
            // directly over the wall it stands on.
            point = rawSurface;
            hoverRideHost = null;
            return true;
        }


        // Node snapping: existing joints outrank everything nearby — the
        // point locks EXACTLY onto the node, so chains and closed loops
        // meet where the network already is.
        if (TryNodeSnap(rawSurface, null, hoverSnapBase, out EndSnap nodeSnap))
        {
            hoverSnap = nodeSnap;
            hoverSnapYaw = nodeSnap.stubYaw;
            if (nodeSnap.edge != null)
                hoverSnapHeight = SnapHeightOf(nodeSnap.edge, nodeSnap.point);
            AdoptSnapElevation(nodeSnap);
            point = nodeSnap.point;
            AdoptRideHostUnder(point, slabTile != null);
            return true;
        }

        // A wall's flank: the anchor lands ON the centerline under the
        // cursor — clicking plants a T-node there (the commit splits the
        // host) and the wall draws away from the face on the cursor's
        // side, at any angle. A parallel run beside a wall is the Offset
        // tool's job.
        if (TryFlankSnap(rawSurface, null, hoverSnapBase, out EndSnap flankSnap))
        {
            hoverSnap = flankSnap;
            hoverSnapYaw = flankSnap.stubYaw;
            if (flankSnap.edge != null)
                hoverSnapHeight = SnapHeightOf(flankSnap.edge, flankSnap.point);
            AdoptSnapElevation(flankSnap);
            point = flankSnap.point;
            AdoptRideHostUnder(point, slabTile != null);
            return true;
        }

        // The slab's rim: nothing built claimed the point, so it pulls to
        // the outline inset half a wall thickness — the wall's outer face
        // lands exactly on the slab edge, and near an outline corner the
        // pull is to the MITERED corner point, so walls drawn along
        // adjacent edges meet at one shared node. Existing masonry (the
        // snaps above) outranks the rim; Alt frees, as everywhere.
        if (slabTile != null
            && TrySlabRimSnap(slabTile, rawSurface, out Vector3 rimPoint, out float rimYaw))
        {
            hoverSnapYaw = rimYaw;
            point = rimPoint;
            return true;
        }

        // Riding a wall top: nothing on THIS storey claimed the point, so
        // it locks onto the host's centerline — masonry built up here sits
        // centred on the masonry below instead of wherever the cursor
        // happened to fall across a metre of stone. Off-centre work on a
        // wall top is what Alt is for.
        if (hoverRideHost != null)
        {
            point = RideOnto(hoverRideHost, rawSurface);
            // Standing on a wall is a snap like any other, so it matches
            // that wall's height too. Grazing a wall's SIDE already did;
            // without this, crossing its top edge with the cursor flipped
            // the ghost between the host's height and the dialed one.
            // Ctrl+scroll still overrides, as everywhere else.
            hoverSnapHeight = hoverSnapHeight ?? SnapHeightOf(hoverRideHost, point);
            // The stub takes the host's own bearing, so the hover reads as
            // "this sits squarely on that wall" instead of pointing off at
            // whatever angle R was left on.
            float t = hoverRideHost.NearestT(point, out _);
            Vector3 dir = WallEdge.Tangent(hoverRideHost.A, hoverRideHost.control,
                hoverRideHost.B, t).normalized;
            hoverSnapYaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg;
            return true;
        }

        // Terrain (or anything else that isn't a wall): the exact point
        // under the cursor — placement is free.
        point = rawSurface;
        return true;
    }

    // Whether an edge belongs to a given storey. `null` is the TERRAIN
    // storey, not a wildcard: snaps compare XZ alone, so a wall and the
    // wall stacked on it are at zero distance from each other and an
    // unfiltered search picks between them on float noise — the hover
    // flickering between the wall you aimed at and the storey above it.
    // Same rule as WallGraph's planar queries (SameBase), which
    // TrySteppedLanding already fed `atBase ?? -inf`.
    static bool BaseMatches(WallEdge edge, float? atBase)
    {
        return WallGraph.SameBase(edge.baseY, atBase ?? float.NegativeInfinity);
    }

    // A snapped point can be standing on a wall too. Continuing a storey
    // from the END of an upper wall snaps to that wall's node — and the
    // node sits ON the masonry below, so the run inherits the same lock a
    // bare wall-top hover gets. Without this, a second storey shorter than
    // the wall under it handed back free direction at its own end, which
    // is exactly where a player goes to extend it. Room decks are exempt:
    // building on a deck is free-form by design.
    void AdoptRideHostUnder(Vector3 point, bool onSlab)
    {
        if (hoverRideHost != null || onSlab || !hoverBaseY.HasValue)
            return;
        hoverRideHost = RideHostUnder(point, hoverBaseY.Value);
    }

    // A snap onto an elevated wall carries its storey with it: the
    // gesture builds from the host's deck, not from the terrain a storey
    // below.
    void AdoptSnapElevation(EndSnap snap)
    {
        if (snap.edge != null && !float.IsNegativeInfinity(snap.edge.baseY))
            hoverBaseY = snap.edge.baseY;
    }

    // The nearest node snap within endpointSnapRadius of the raw point.
    // A wall end (1-valent node) matches from THREE hit points — the
    // node itself and the two side-face points beside it — so hovering
    // any face of the end grabs the joint; the result is always the same
    // snap ON the node. Direction is not part of the presentation: the
    // node's existing end masonry highlights, and the wall leaves at
    // whatever angle the drag takes. Candidates near `exclude` (the drag
    // anchor) are skipped so a drag can't snap its far end back onto its
    // own start.
    bool TryNodeSnap(Vector3 raw, Vector3? exclude, float? atBase, out EndSnap snap)
    {
        EndSnap best = default;
        float bestSq = endpointSnapRadius * endpointSnapRadius;
        bool found = false;
        foreach (WallNode node in WallNode.All)
        {
            EndSnap baseSnap = MakeNodeSnap(node, atBase);
            // No member on this storey means the node isn't on it — an
            // upper wall's ends sit directly above the lower wall's nodes,
            // so without this they'd all answer every search.
            if (baseSnap.edge == null)
                continue;

            void Consider(Vector3 candidate)
            {
                if (exclude.HasValue && FlatDistance(candidate, exclude.Value) < lengthStep)
                    return;
                float dx = candidate.x - raw.x;
                float dz = candidate.z - raw.z;
                float sq = dx * dx + dz * dz;
                if (sq >= bestSq)
                    return;
                bestSq = sq;
                found = true;
                best = baseSnap;
            }

            Consider(node.point);
            if (node.Valence == 1 && node.members[0].edge != null
                && BaseMatches(node.members[0].edge, atBase))
            {
                WallEdge member = node.members[0].edge;
                Vector3 outward = member.EndOutwardDir(node.members[0].atStart);
                Vector3 side = new Vector3(-outward.z, 0f, outward.x);
                float half = member.thickness * 0.5f;
                Consider(node.point + side * half);
                Consider(node.point - side * half);
            }
        }
        snap = best;
        return found;
    }

    EndSnap MakeNodeSnap(WallNode node, float? atBase = null)
    {
        // Representative member: the tallest wall at the node — the one a
        // chained wall most likely wants to match heights with.
        WallEdge repr = null;
        float best = -1f;
        foreach (WallNode.Member m in node.members)
        {
            if (m.edge == null || !BaseMatches(m.edge, atBase))
                continue;
            float h = SnapHeightOf(m.edge, node.point);
            if (h > best)
            {
                best = h;
                repr = m.edge;
            }
        }

        // A sole member has a true outward continuation; a busier node
        // offers the most open direction (bisector of the largest gap
        // between member bodies) for its hover stub.
        Vector3 outDir;
        if (node.Valence == 1 && node.members[0].edge != null)
            outDir = node.members[0].edge.EndOutwardDir(node.members[0].atStart);
        else
            outDir = MostOpenDir(node);

        return new EndSnap
        {
            edge = repr,
            node = node,
            point = node.point,
            backBearing = Mathf.Atan2(-outDir.z, -outDir.x) * Mathf.Rad2Deg,
            stubYaw = Mathf.Atan2(-outDir.z, outDir.x) * Mathf.Rad2Deg,
            outDir = outDir,
            isFlank = false,
        };
    }

    // The corner readout and relative Shift-stepping both measure the
    // ghost against REAL masonry at the snapped joint: of every wall run
    // leaving it (each member at a node, both arms of a flank host), the
    // one forming the tightest corner with the drag is the reference —
    // so the label matches the V you actually see, and stepped angles
    // land on clean multiples even when the host sits off the world grid.
    float AngleRefBearing(EndSnap snap, float dragBearing)
    {
        float best = snap.backBearing;
        float bestDelta = float.MaxValue;
        void Consider(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.0001f)
                return;
            float b = Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
            float d = Mathf.Abs(Mathf.DeltaAngle(b, dragBearing));
            if (d < bestDelta)
            {
                bestDelta = d;
                best = b;
            }
        }
        if (snap.node != null)
        {
            foreach (WallNode.Member m in snap.node.members)
                if (m.edge != null)
                    Consider(-m.edge.EndOutwardDir(m.atStart));
        }
        else if (snap.isFlank)
        {
            Vector3 arm = new Vector3(snap.outDir.z, 0f, -snap.outDir.x);
            Consider(arm);
            Consider(-arm);
        }
        return best;
    }

    // Re-aims `end` so the drag's bearing sits on an angleStep multiple
    // RELATIVE to refBearing, keeping the dragged length.
    Vector3 StepBearingFrom(float refBearing, Vector3 from, Vector3 end)
    {
        Vector3 d = end - from;
        d.y = 0f;
        float len = d.magnitude;
        if (len < 0.001f)
            return end;
        float dragB = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
        float stepped = Mathf.Round(Mathf.DeltaAngle(refBearing, dragB) / angleStep) * angleStep;
        float rad = (refBearing + stepped) * Mathf.Deg2Rad;
        return from + new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * len;
    }

    static Vector3 MostOpenDir(WallNode node)
    {
        var bearings = new List<float>();
        foreach (WallNode.Member m in node.members)
        {
            if (m.edge == null)
                continue;
            Vector3 inward = -m.edge.EndOutwardDir(m.atStart);
            if (inward.sqrMagnitude < 0.01f)
                continue;
            bearings.Add(Mathf.Atan2(inward.z, inward.x) * Mathf.Rad2Deg);
        }
        if (bearings.Count == 0)
            return Vector3.right;
        bearings.Sort();
        float bestGap = -1f;
        float bestMid = bearings[0] + 180f;
        for (int i = 0; i < bearings.Count; i++)
        {
            float a = bearings[i];
            float b = i == bearings.Count - 1 ? bearings[0] + 360f : bearings[i + 1];
            if (b - a > bestGap)
            {
                bestGap = b - a;
                bestMid = (a + b) * 0.5f;
            }
        }
        float rad = bestMid * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
    }

    // Mid-wall flank snap: the point locks onto the host's CENTERLINE
    // under the cursor — the commit splits the host there into a T-node,
    // at any angle, and the wedge rule fills the shoulder. `from` (the
    // drag anchor) picks which side the wall leaves toward and excludes
    // candidates near itself; host end zones are left to node snapping.
    bool TryFlankSnap(Vector3 raw, Vector3? from, float? atBase, out EndSnap snap)
    {
        snap = default;
        float bestSq = float.MaxValue;
        bool found = false;
        foreach (WallEdge edge in WallEdge.All)
        {
            if (!BaseMatches(edge, atBase))
                continue;
            float halfThick = edge.thickness * 0.5f;
            float grabRange = halfThick + endpointSnapRadius;
            float t = edge.NearestT(raw, out float centerSq);
            if (centerSq > grabRange * grabRange || centerSq >= bestSq)
                continue;
            Vector3 cPoint = WallEdge.Evaluate(edge.A, edge.control, edge.B, t);
            cPoint = new Vector3(cPoint.x, 0f, cPoint.z);
            if (FlatDistance(cPoint, edge.A) < 0.9f || FlatDistance(cPoint, edge.B) < 0.9f)
                continue;
            if (from.HasValue && FlatDistance(cPoint, from.Value) < lengthStep)
                continue;
            bestSq = centerSq;
            found = true;
            snap = FlankSnapAt(edge, cPoint, from ?? raw);
        }
        return found;
    }

    // A flank landing on `edge` at a point already known to lie on its
    // centerline. Shared by proximity snapping (above) and by the stepped
    // landing, which finds its point by intersecting the drag's stepped
    // ray with the host rather than by nearest approach.
    static EndSnap FlankSnapAt(WallEdge edge, Vector3 centerPoint, Vector3 from)
    {
        float t = edge.NearestT(centerPoint, out _);
        Vector3 side = edge.SideOf(from) * FlankNormalAt(edge, t);
        return new EndSnap
        {
            edge = edge,
            node = null,
            point = centerPoint,
            backBearing = Mathf.Atan2(-side.z, -side.x) * Mathf.Rad2Deg,
            stubYaw = Mathf.Atan2(-side.z, side.x) * Mathf.Rad2Deg,
            outDir = side,
            isFlank = true,
            stubTrim = edge.thickness * 0.5f,
        };
    }

    // Shift rules the bearing, so a landing can't be found by nearest
    // approach — it has to lie ON the stepped ray. The run probes
    // SteppedLandingReach past the cursor and takes the FIRST wall it
    // meets, which is the one the cursor was already grazing (callers ask
    // only when a flank is in reach). Storey-filtered like every other
    // snap; a crossing that reuses a node lands as a node snap.
    // A shallow approach (under ~40°) needs more run to reach the host
    // than the probe gives it — the segment then stays free at the
    // stepped bearing instead of guessing a landing further along.
    bool TrySteppedLanding(Vector3 from, Vector3 steppedEnd, float? atBase,
        out Vector3 point, out EndSnap snap)
    {
        point = steppedEnd;
        snap = default;
        Vector3 run = steppedEnd - from;
        run.y = 0f;
        if (run.sqrMagnitude < 0.000001f)
            return false;
        Vector3 probe = steppedEnd + run.normalized * SteppedLandingReach;
        foreach (WallGraph.Crossing x in
            WallGraph.FindCrossings(from, (from + probe) * 0.5f, probe,
                atBase ?? float.NegativeInfinity))
        {
            if (x.reuseNode != null)
            {
                EndSnap nodeSnap = MakeNodeSnap(x.reuseNode, atBase);
                if (nodeSnap.edge == null)
                    continue;
                point = x.reuseNode.point;
                snap = nodeSnap;
                return true;
            }
            if (x.edge == null || !BaseMatches(x.edge, atBase))
                continue;
            point = new Vector3(x.point.x, 0f, x.point.z);
            snap = FlankSnapAt(x.edge, point, from);
            return true;
        }
        return false;
    }

    // The host's left-hand flank normal at t, flattened — SideOf's sign
    // convention picks which face by multiplying this by ±1.
    static Vector3 FlankNormalAt(WallEdge edge, float t)
    {
        Vector3 a = WallEdge.Evaluate(edge.A, edge.control, edge.B, Mathf.Clamp01(t - 0.01f));
        Vector3 b = WallEdge.Evaluate(edge.A, edge.control, edge.B, Mathf.Clamp01(t + 0.01f));
        Vector3 dir = b - a;
        dir.y = 0f;
        dir = dir.sqrMagnitude < 0.000001f ? Vector3.right : dir.normalized;
        return new Vector3(-dir.z, 0f, dir.x);
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static Vector3 ControlPointFor(Vector3 s, Vector3 e, float bulge, float apexT)
    {
        Vector3 chord = e - s;
        Vector3 normal = new Vector3(-chord.z, 0f, chord.x).normalized;
        // Control point sits at twice the bulge: a quadratic Bézier only
        // reaches halfway from chord to control at its waist.
        return s + chord * apexT + normal * (bulge * 2f);
    }

    // The point a wall top hands the cursor: ON the host's centerline, so
    // new masonry sits centred over the old instead of wherever the cursor
    // fell across a metre of stone — and pulled to the host's OWN ENDS
    // when it comes near them, so a run laid along a wall top covers
    // exactly the wall below rather than stopping short of its cap.
    Vector3 RideOnto(WallEdge host, Vector3 raw)
    {
        float t = host.NearestT(raw, out _);
        Vector3 on = WallEdge.Evaluate(host.A, host.control, host.B, t);
        on = new Vector3(on.x, 0f, on.z);
        Vector3 a = new Vector3(host.A.x, 0f, host.A.z);
        Vector3 b = new Vector3(host.B.x, 0f, host.B.z);
        if (FlatDistance(on, a) <= endpointSnapRadius)
            return a;
        if (FlatDistance(on, b) <= endpointSnapRadius)
            return b;
        return on;
    }

    // A ride follows the masonry THROUGH its nodes: past the end of the
    // wall it starts on, the run continues onto whatever carries on from
    // that node, one span per host. A circle is eight arcs and a room is
    // four walls — stopping dead at every node meant eight drags to raise
    // a storey over a tower. Each span still becomes its own edge with its
    // host's exact curve; they meet at nodes standing over the nodes
    // below, which is what the graph wants anyway. Fills rideSpans and
    // returns the point the run ends at.
    // Where a ride's far end lands: masonry already standing on this
    // storey wins when the run reaches it — two upper walls built from
    // opposite ends have to close on ONE node, and a node sits on the
    // centerline the run is already locked to, so honouring it costs the
    // lock nothing. A node that isn't actually on the ride is ignored
    // (the chain would end somewhere else, and it says so).
    Vector3 RideDragEnd(WallEdge host, Vector3 start, Vector3 raw, out EndSnap? met)
    {
        met = null;
        if (TryNodeSnap(raw, start, anchorBaseY, out EndSnap node))
        {
            Vector3 landed = RideChainEnd(host, start, node.point, true);
            if (FlatDistance(landed, node.point) <= 0.15f)
            {
                met = node;
                return landed;
            }
        }
        return RideChainEnd(host, start, raw, false);
    }

    Vector3 RideChainEnd(WallEdge host, Vector3 start, Vector3 cursor, bool exact)
    {
        float tStart = host.NearestT(start, out _);
        rideExact = exact;
        Vector3 end = Walk(host, host.NearestT(cursor, out _) >= tStart, tStart, cursor);
        // Anchored ON a node, the run may want the wall on the OTHER side
        // of it — the anchor host's own span is then zero-length and the
        // masonry the player is dragging over belongs to its neighbour.
        if (rideSpans.Count == 0)
        {
            bool atA = tStart <= 0.0001f, atB = tStart >= 0.9999f;
            WallNode node = atA ? host.nodeA : atB ? host.nodeB : null;
            WallEdge back = StraightestContinuation(node, host, atB);
            if (back != null)
            {
                bool towardB = back.nodeA == node;
                end = Walk(back, towardB, towardB ? 0f : 1f, cursor);
            }
        }
        return end;
    }

    // One walk from a starting host and sense, filling rideSpans.
    Vector3 Walk(WallEdge first, bool towardB, float from, Vector3 cursor)
    {
        rideSpans.Clear();
        WallEdge cur = first;
        Vector3 end = WallEdge.Evaluate(first.A, first.control, first.B, from);
        end = new Vector3(end.x, 0f, end.z);
        var visited = new HashSet<WallEdge>();
        while (cur != null && visited.Add(cur) && rideSpans.Count < 64)
        {
            float t = cur.NearestT(cursor, out _);
            float far = towardB ? 1f : 0f;
            // Within a snap of this host's far end, take the whole of it:
            // a run laid along a wall top covers the wall, not almost all
            // of it. Past the end, take the whole of it and keep walking.
            Vector3 farPoint = towardB ? cur.B : cur.A;
            bool consumed = towardB ? t >= 1f - 0.0001f : t <= 0.0001f;
            // rideExact: the end is a node the run has to close on, so it
            // must not be pulled past it onto the host's own cap.
            if (!consumed && !rideExact && FlatDistance(
                    WallEdge.Evaluate(cur.A, cur.control, cur.B, t), farPoint) <= endpointSnapRadius)
            {
                t = far;
                consumed = true;
            }
            if (Mathf.Abs(t - from) > 0.001f)
            {
                rideSpans.Add((cur, Mathf.Min(from, t), Mathf.Max(from, t)));
                Vector3 p = WallEdge.Evaluate(cur.A, cur.control, cur.B, t);
                end = new Vector3(p.x, 0f, p.z);
            }
            if (!consumed)
                break;
            // Straightest continuation out of the far node, on the same
            // storey — a T upstairs picks the run that carries on, not the
            // branch. Nothing there (a free end) simply ends the run.
            WallNode node = towardB ? cur.nodeB : cur.nodeA;
            WallEdge next = StraightestContinuation(node, cur, towardB);
            if (next == null)
                break;
            towardB = next.nodeA == node;
            from = towardB ? 0f : 1f;
            cur = next;
        }
        return end;
    }

    // The member leaving `node` that carries on most nearly straight from
    // `arriving` (which reaches the node at its B end when towardB).
    WallEdge StraightestContinuation(WallNode node, WallEdge arriving, bool towardB)
    {
        if (node == null)
            return null;
        Vector3 travel = arriving.EndOutwardDir(!towardB);
        WallEdge best = null;
        float bestDot = 0.2f;                       // never turn back on itself
        foreach (WallNode.Member m in node.members)
        {
            if (m.edge == null || m.edge == arriving)
                continue;
            if (!WallGraph.SameBase(m.edge.baseY, arriving.baseY))
                continue;
            float dot = Vector3.Dot(travel, -m.edge.EndOutwardDir(m.atStart));
            if (dot > bestDot)
            {
                bestDot = dot;
                best = m.edge;
            }
        }
        return best;
    }

    // The ghost for a ride: one preview edge per host span, each taking
    // its host's exact curve and its own stack fit (adjacent hosts can sit
    // at different heights). Shares splineSpecs and pendingCommits with
    // the single-span path, so release commits what is drawn.
    void BuildRidePreview(Vector3 start, Vector3 end)
    {
        float height = EffectiveWallHeight;
        float baseFloor = (anchorBaseY ?? hoverBaseY) ?? float.NegativeInfinity;
        // The readout reports the ANCHOR span's top (before the cache
        // check — the field clears every frame). Adjacent hosts can sit at
        // different heights, so spans further along may top out elsewhere;
        // the anchor span is the one you dialed against.
        if (rideSpans.Count > 0)
        {
            (WallEdge h0, float tA0, float tB0) = rideSpans[0];
            WallGraph.SubCurve(h0.A, h0.control, h0.B, tA0, tB0,
                out Vector3 s0, out _, out Vector3 e0);
            s0 = new Vector3(s0.x, 0f, s0.z);
            e0 = new Vector3(e0.x, 0f, e0.z);
            float top0 = float.NegativeInfinity;
            StackFit(h0, s0, e0, height, baseFloor, out _, ref top0);
            ghostTopY = !float.IsNegativeInfinity(top0) ? top0
                : !float.IsNegativeInfinity(baseFloor) ? baseFloor + height
                : SampleCellGroundY(s0.x, s0.z) + height;
        }
        var key = (start, end, height, baseFloor, rideSpans.Count,
            rideSpans.Count > 0 ? rideSpans[rideSpans.Count - 1].tB : 0f);
        if (lastRideKey.HasValue && lastRideKey.Value == key)
            return;
        lastRideKey = key;
        // The single-span cache has nothing to say about this shape.
        lastPreviewKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        pendingCommits.Clear();
        previewBlocked = false;
        previewCrossings = new List<WallGraph.Crossing>();
        foreach ((WallEdge host, float tA, float tB) in rideSpans)
        {
            WallGraph.SubCurve(host.A, host.control, host.B, tA, tB,
                out Vector3 s, out Vector3 c, out Vector3 e);
            s = new Vector3(s.x, 0f, s.z);
            e = new Vector3(e.x, 0f, e.z);
            float topY = float.NegativeInfinity;
            StackFit(host, s, e, height, baseFloor, out float skirt, ref topY);
            splineSpecs.AddRange(WallEdge.BuildSpecs(s, c, e, height, gridSize, gridSize,
                baseStepSize, BaseHeight, topY, baseFloor - skirt));
            pendingCommits.Add((s, c, e, height, topY, baseFloor, skirt));
            previewBlocked |= WallGraph.OverlapsExisting(s, c, e, baseFloor)
                || LandLaw.SpanOutside(s, c, e);
            previewCrossings.AddRange(WallGraph.FindCrossings(s, c, e, baseFloor));
        }
        foreach (WallEdge.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
    }

    // The wall a point at this elevation is STANDING ON: an edge of some
    // other storey whose top reaches this base directly under the point.
    // Nothing stores what rests on what (structural rules are deliberately
    // deferred), so support is a geometric question — asked only in the
    // two places the answer changes a gesture: a snapped anchor that is
    // really standing on a wall, and how deep a stacked run's bottom goes.
    WallEdge RideHostUnder(Vector3 point, float baseY)
    {
        if (float.IsNegativeInfinity(baseY))
            return null;
        WallEdge best = null;
        float bestSq = float.MaxValue;
        foreach (WallEdge edge in WallEdge.All)
        {
            // Same storey means neighbour, not support.
            if (edge == null || WallGraph.SameBase(edge.baseY, baseY))
                continue;
            float half = edge.thickness * 0.5f;
            edge.NearestT(point, out float distSq);
            if (distSq > half * half || distSq >= bestSq)
                continue;
            // Against the whole TOP RANGE, not the top at this parameter:
            // an unlocked wall's top steps down a slope, and a storey laid
            // over it takes its base from wherever the gesture started.
            TopRange(edge, 0f, 1f, out float low, out float high);
            if (baseY < low - WallGraph.BaseTolerance || baseY > high + WallGraph.BaseTolerance)
                continue;
            bestSq = distSq;
            best = edge;
        }
        return best;
    }

    // Lowest and highest section top over a slice of an edge.
    static void TopRange(WallEdge edge, float tA, float tB, out float low, out float high)
    {
        low = float.MaxValue;
        high = float.MinValue;
        foreach (WallEdgeSection section in edge.SectionsInOrder())
        {
            if (section.tEnd < tA - 0.0001f || section.tStart > tB + 0.0001f)
                continue;
            low = Mathf.Min(low, section.topY);
            high = Mathf.Max(high, section.topY);
        }
    }

    // Bottom and top for a run laid on a wall top. An unlocked wall's top
    // STEPS with the terrain, so a stacked run with a flat bottom at the
    // top under the cursor shows daylight over every lower step. The run
    // keeps the top its height dialed and grows a SKIRT down to the lowest
    // top it covers: the joint is buried in masonry instead of open. baseY
    // itself never moves — it is the storey, and the graph reads it.
    void StackFit(WallEdge known, Vector3 s, Vector3 e, float height,
        float baseY, out float skirt, ref float topY)
    {
        skirt = 0f;
        if (float.IsNegativeInfinity(baseY))
            return;
        WallEdge host = known ?? RideHostUnder(s, baseY) ?? RideHostUnder(e, baseY);
        if (host == null)
            return;
        float tA = host.NearestT(s, out _);
        float tB = host.NearestT(e, out _);
        if (tA > tB)
            (tA, tB) = (tB, tA);
        TopRange(host, tA, tB, out float lowest, out float highest);
        if (lowest > highest)
            return;
        if (baseY - lowest <= 0.001f && highest - baseY <= 0.001f)
            return;                                 // level host: nothing to fit
        skirt = Mathf.Max(0f, baseY - lowest);
        // The top is pinned to CLEAR the highest stone the run covers by
        // the dialed height. Measuring from the hovered section instead
        // would bury the run in its own host wherever that host stands
        // higher — anchor low on a slope and the wall vanishes into the
        // rise. On a level host the two are the same number.
        if (float.IsNegativeInfinity(topY))
            topY = Mathf.Max(baseY, highest) + height;
    }

    // The control point a run gets from the wall it is riding: both ends
    // must sit on the SAME host's top, and the sub-curve between their two
    // parameters is exact (WallGraph.SubCurve), so a storey laid over a
    // curved wall traces it rather than approximating it. False whenever
    // the gesture left that host — the bulge wheel takes over again.
    bool TryRideControl(Vector3 start, Vector3 end, out Vector3 control)
    {
        control = default;
        WallEdge host = previewRideHost;
        if (host == null)
            return false;
        float tA = host.NearestT(start, out _);
        float tB = host.NearestT(end, out _);
        if (Mathf.Abs(tB - tA) < 0.001f)
            return false;
        WallGraph.SubCurve(host.A, host.control, host.B, tA, tB, out _, out control, out _);
        return true;
    }

    // The absolute top a locked-top wall anchored at this point gets: the
    // anchor cell's stepped ground plus the current wall height.
    // -Infinity (lock off) leaves tops riding the terrain per section.
    float LockedTopFor(Vector3 anchor)
    {
        return lockTopHeight
            ? SampleCellGroundY(anchor.x, anchor.z) + EffectiveWallHeight
            : float.NegativeInfinity;
    }


    // trimStart/trimEnd pull the DISPLAYED wall back from ends that land
    // on a host's flank, so the ghost meets the face instead of clipping
    // half a thickness into the masonry. The commit curve (pendingCommit)
    // and the graph checks always use the full gesture.
    void BuildSplinePreview(Vector3 start, Vector3 end, float bulge, float apexT,
        float trimStart = 0f, float trimEnd = 0f)
    {
        float height = EffectiveWallHeight;
        // On elevated ground (a room slab) the base is already flat, so
        // the top lock is moot — bottom = deck, top = deck + height.
        float? baseY = anchorBaseY ?? hoverBaseY;
        float topY = baseY.HasValue ? float.NegativeInfinity : LockedTopFor(start);
        float baseFloor = baseY ?? float.NegativeInfinity;
        // Standing on a wall: bury the bottom into its stepped top rather
        // than leave a slot. The GRAPH still gets baseFloor — the storey —
        // while only the masonry starts lower.
        StackFit(previewRideHost, start, end, height, baseFloor,
            out float skirt, ref topY);
        // A run anchored on a wall top INHERITS that wall's curve exactly
        // (polar-form subdivision) and nothing overrides it — not the
        // bulge wheel, not Shift. Masonry raised over a wall follows the
        // wall. Straight hosts are unaffected: a sub-curve of a straight
        // edge is straight.
        Vector3 control = TryRideControl(start, end, out Vector3 ridden)
            ? ridden
            : ControlPointFor(start, end, bulge, apexT);
        // Set before the cache check: the field is cleared every frame,
        // and an unchanged preview still needs its readout.
        ghostTopY = !float.IsNegativeInfinity(topY) ? topY
            : !float.IsNegativeInfinity(baseFloor) ? baseFloor + height
            : SampleCellGroundY(start.x, start.z) + height;
        var key = (start, end, control, height, topY, baseFloor - skirt, trimStart, trimEnd);
        if (lastPreviewKey.HasValue && lastPreviewKey.Value == key)
            return;
        lastPreviewKey = key;
        lastRideKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        pendingCommits.Clear();
        pendingCommits.Add((start, control, end, height, topY, baseFloor, skirt));

        Vector3 ds = start, dc = control, de = end;
        if (trimStart > 0.001f || trimEnd > 0.001f)
        {
            float[] arcTable = WallEdge.BuildArcTable(start, control, end);
            float total = arcTable[arcTable.Length - 1];
            // A drag shorter than its trims shows a sliver rather than
            // nothing (or an inverted curve).
            float budget = Mathf.Max(0.05f, total - 0.1f);
            if (trimStart + trimEnd > budget)
            {
                float scale = budget / (trimStart + trimEnd);
                trimStart *= scale;
                trimEnd *= scale;
            }
            float tA = WallEdge.TAtArc(arcTable, trimStart);
            float tB = WallEdge.TAtArc(arcTable, total - trimEnd);
            WallGraph.SubCurve(start, control, end, tA, tB, out ds, out dc, out de);
        }
        splineSpecs.AddRange(WallEdge.BuildSpecs(ds, dc, de,
            height, gridSize, gridSize, baseStepSize, BaseHeight, topY, baseFloor - skirt));
        foreach (WallEdge.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);

        // The graph verdicts the whole gesture: refused for a
        // near-exact redraw along an existing wall, or for leaving the
        // castle grounds (LandLaw — same red, same commit gate);
        // crossings are legal and become shared nodes (gold posts).
        previewBlocked = WallGraph.OverlapsExisting(start, control, end, baseFloor)
            || LandLaw.SpanOutside(start, control, end);
        previewCrossings = WallGraph.FindCrossings(start, control, end, baseFloor);
    }

    void ClearPreviewMeshes()
    {
        foreach (Mesh mesh in previewMeshes)
            Destroy(mesh);
        previewMeshes.Clear();
    }

    // A refused gesture shows red, never vanishes — the refusal is
    // visible in place, and release simply doesn't commit.
    void ShowSplineGhosts()
    {
        Color tint = previewBlocked ? deletePreviewColor : (snapTint ? snapColor : previewColor);
        for (int i = 0; i < splineSpecs.Count; i++)
        {
            WallEdge.SectionSpec spec = splineSpecs[i];
            GameObject ghost = GetPooled(ghostPool, i, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            SetGhostTint(ghost, tint);
            ghost.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));
            ghost.transform.localScale = Vector3.one;
        }
        for (int i = splineSpecs.Count; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // Pooled ghosts change tint per use (refused gestures run red); the
    // cache keeps material writes to actual changes.
    void SetGhostTint(GameObject ghost, Color tint)
    {
        if (ghostTints.TryGetValue(ghost, out Color current) && current == tint)
            return;
        ghostTints[ghost] = tint;
        foreach (var rend in ghost.GetComponentsInChildren<Renderer>())
        {
            foreach (var mat in rend.sharedMaterials)
            {
                if (mat == null)
                    continue;
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", tint);
                else if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", tint);
            }
        }
    }

    // The hover inspect line: nearest thing under the cursor, reported as
    // absolute planes so you know what a rendezvous has to match. A room's
    // slab — roof OR floor — reports both of that room's planes, because a
    // roofed building's floor is hidden inside and the readout shouldn't
    // need the cutaway. A wall reports its stored section top at the
    // hovered point. Bare terrain reports the ground.
    string InspectElevations(Ray ray)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 3000f);
        RaycastHit best = default;
        float bestDist = float.MaxValue;
        foreach (RaycastHit h in hits)
        {
            if (h.collider == null || h.collider.isTrigger)
                continue;
            if (h.distance < bestDist)
            {
                bestDist = h.distance;
                best = h;
            }
        }
        if (bestDist == float.MaxValue)
            return null;

        WallEdgeSection section = best.collider.GetComponent<WallEdgeSection>();
        if (section != null)
            return $"Wall top {section.topY:0.##}m";

        SlabTile slab = best.collider.GetComponent<SlabTile>();
        if (slab != null)
            return (slab.isFoundation ? "Foundation top " : "Floor top ")
                + $"{slab.topY:0.##}m";

        if (best.collider is TerrainCollider)
            return $"Ground {best.point.y:0.##}m";

        return null;
    }

    // A full box ghost marking the curve tool's start point.
    void ShowStartMarker(Vector3 point)
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        previewBlocked = false;
        previewCrossings.Clear();
        lastPreviewKey = null;
        lastRideKey = null;
        pendingCommits.Clear();

        float ground = hoverBaseY ?? SampleCellGroundY(point.x, point.z);
        float height = EffectiveWallHeight;
        ghostTopY = ground + height;
        GameObject ghost = GetPooled(ghostPool, 0, previewColor, "PlacementPreview");
        ghost.SetActive(true);
        SetGhostMesh(ghost, null);
        SetGhostTint(ghost, snapTint ? snapColor : previewColor);
        Vector3 scale = piecePrefab.transform.localScale;
        scale.y *= height / BaseHeight;
        // A snapped marker sits flush with the wall it snapped to instead
        // of staying world-aligned.
        ghost.transform.SetPositionAndRotation(
            new Vector3(point.x, ground + height / 2f, point.z),
            Quaternion.Euler(0f, hoverSnapYaw ?? 0f, 0f));
        ghost.transform.localScale = scale;
        for (int i = 1; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // Queues a gold corner-post ghost for this frame;
    // UpdateNodeGhostVisuals renders the queue at the end of Update.
    void AddNodeGhost(Vector3 p)
    {
        pendingNodeGhosts.Add((p, SampleCellGroundY(p.x, p.z), EffectiveWallHeight));
    }

    void UpdateNodeGhostVisuals()
    {
        for (int i = 0; i < pendingNodeGhosts.Count; i++)
        {
            (Vector3 pos, float ground, float height) = pendingNodeGhosts[i];
            while (nodeGhostMeshes.Count <= i)
            {
                nodeGhostMeshes.Add(null);
                nodeGhostHeights.Add(-1f);
            }
            if (nodeGhostMeshes[i] == null || !Mathf.Approximately(nodeGhostHeights[i], height))
            {
                if (nodeGhostMeshes[i] != null)
                    Destroy(nodeGhostMeshes[i]);
                nodeGhostMeshes[i] = WallNode.BuildGhostMesh(gridSize * 0.5f, height);
                nodeGhostHeights[i] = height;
            }
            GameObject ghost = GetPooled(nodeGhostPool, i, snapColor, "NodeGhost");
            ghost.SetActive(true);
            SetGhostMesh(ghost, nodeGhostMeshes[i]);
            SetGhostTint(ghost, snapColor);
            ghost.transform.SetPositionAndRotation(new Vector3(pos.x, ground, pos.z), Quaternion.identity);
            ghost.transform.localScale = Vector3.one;
        }
        for (int i = pendingNodeGhosts.Count; i < nodeGhostPool.Count; i++)
            nodeGhostPool[i].SetActive(false);
    }

    // Pooled ghosts flip between the shared box mesh and per-slice curve
    // meshes, so the mesh is (re)assigned on every show.
    void SetGhostMesh(GameObject ghost, Mesh mesh)
    {
        MeshFilter filter = ghost.GetComponent<MeshFilter>();
        if (filter != null)
            filter.sharedMesh = mesh != null ? mesh : prefabMesh;
    }

    // The offset tool: click an existing wall and a parallel copy of its
    // curve previews at a locked distance — plain scroll steps the
    // distance (the wheel is claimed while a source is selected), the
    // cursor's side of the source picks which side it lands on, click
    // commits, Escape lets go of the source. The committed wall is a
    // normal graph edit: it steps its own terrain and splits anything it
    // crosses.
    void UpdateOffset(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        HandleHeightScroll(mouse, keyboard);

        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            offsetSource = null;
            CancelCurve();
            return;
        }

        if (offsetSource == null)
        {
            if (!overUI && TryGetSectionUnderCursor(ray, out WallEdgeSection hovered))
            {
                ShowWallHighlight(hovered.edge);
                if (mouse.leftButton.wasPressedThisFrame)
                    offsetSource = hovered.edge;
            }
            else
            {
                HideGhosts();
            }
            return;
        }

        bool ctrlHeld = keyboard != null && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
        float scroll = mouse.scroll.ReadValue().y;
        if (!ctrlHeld && Mathf.Abs(scroll) > 0.01f)
            offsetDistance = Mathf.Clamp(offsetDistance + Mathf.Sign(scroll) * lengthStep, gridSize, 30f);

        if (!overUI && TryGetSurfacePoint(ray, out Vector3 cursorPoint))
            offsetSide = offsetSource.SideOf(cursorPoint);

        WallEdge.OffsetCurve(offsetSource.A, offsetSource.control, offsetSource.B,
            offsetSide * offsetDistance, out Vector3 s2, out Vector3 c2, out Vector3 e2);
        BuildOffsetPreview(s2, c2, e2);
        ShowSplineGhosts();
        foreach (WallGraph.Crossing x in previewCrossings)
            AddNodeGhost(x.point);
        guideCenter = (s2 + e2) * 0.5f;
        guideBearing = Mathf.Atan2(e2.z - s2.z, e2.x - s2.x) * Mathf.Rad2Deg;

        if (mouse.leftButton.wasPressedThisFrame && !previewBlocked && splineSpecs.Count > 0)
        {
            float srcBase = offsetSource.baseY;
            List<WallEdge> created = WallGraph.CommitEdge(splineParent, s2, c2, e2,
                EdgeParamsNow(CurrentWallHeight,
                    float.IsNegativeInfinity(srcBase) ? LockedTopFor(s2) : float.NegativeInfinity,
                    srcBase));
            Estate.Pay(Estate.CostOfEdges(created), Estate.KindLabel(CurrentWallKind));
            splineSpecs.Clear();
            ClearPreviewMeshes();
            lastOffsetKey = null;
            previewCrossings.Clear();
            HideGhosts();
        }
    }

    void BuildOffsetPreview(Vector3 s, Vector3 c, Vector3 e)
    {
        // An elevated source offsets along its own deck (where the base
        // is already flat, so the top lock is moot).
        float srcBase = offsetSource != null ? offsetSource.baseY : float.NegativeInfinity;
        float topY = float.IsNegativeInfinity(srcBase) ? LockedTopFor(s) : float.NegativeInfinity;
        ghostTopY = !float.IsNegativeInfinity(topY) ? topY
            : !float.IsNegativeInfinity(srcBase) ? srcBase + CurrentWallHeight
            : SampleCellGroundY(s.x, s.z) + CurrentWallHeight;
        var key = (s, c, e, CurrentWallHeight, topY);
        if (lastOffsetKey.HasValue && lastOffsetKey.Value == key)
            return;
        lastOffsetKey = key;
        lastPreviewKey = null;
        lastRideKey = null;
        pendingCommits.Clear();

        ClearPreviewMeshes();
        splineSpecs.Clear();
        splineSpecs.AddRange(WallEdge.BuildSpecs(s, c, e,
            CurrentWallHeight, gridSize, gridSize, baseStepSize, BaseHeight, topY, srcBase));
        foreach (WallEdge.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
        previewBlocked = WallGraph.OverlapsExisting(s, c, e, srcBase)
            || LandLaw.SpanOutside(s, c, e);
        previewCrossings = WallGraph.FindCrossings(s, c, e, srcBase);
    }

    // Shells the hovered wall in preview color so the offset tool shows
    // what a click would take as its source.
    void ShowWallHighlight(WallEdge edge)
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        previewBlocked = false;
        previewCrossings.Clear();
        lastPreviewKey = null;
        lastRideKey = null;
        pendingCommits.Clear();
        lastOffsetKey = null;

        int count = 0;
        foreach (WallEdgeSection section in edge.GetComponentsInChildren<WallEdgeSection>())
        {
            GameObject ghost = GetPooled(ghostPool, count++, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            MeshFilter filter = section.GetComponent<MeshFilter>();
            SetGhostMesh(ghost, filter != null ? filter.sharedMesh : null);
            SetGhostTint(ghost, previewColor);
            FitShell(ghost.transform, section.transform, filter != null ? filter.sharedMesh : null);
        }
        for (int i = count; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // Nearest non-trigger surface point under the cursor, flattened —
    // used by the offset tool to read which side of the source wall the
    // cursor is on.
    bool TryGetSurfacePoint(Ray ray, out Vector3 point)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            point = new Vector3(hit.point.x, 0f, hit.point.z);
            return true;
        }
        point = default;
        return false;
    }

    // Deletion mirrors the build flow: hovering shows a red overlay,
    // nothing is destroyed until the button is released, and exactly
    // what's red when the button comes up is removed — as a graph edit.
    // Two gestures share the button: a press ON a wall paints a run that
    // follows the cursor THROUGH nodes (the doomed path re-resolves from
    // anchor to cursor every frame, so backing up shrinks it), and a
    // press on empty ground rubber-bands a screen marquee — every
    // section whose center lands inside dies, and any room whose slab is
    // caught un-rooms. A hole splits an edge at new flat-capped nodes;
    // survivors never rebuild because nothing about them depended on the
    // dead wall.
    void UpdateDelete(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelDelete();
            return;
        }
        // Right click puts back whatever the last delete removed — the
        // pre-delete snapshot, restored wholesale through the load path.
        if (rightClick && deleteUndoJson != null)
        {
            CancelDelete();
            string json = deleteUndoJson;
            deleteUndoJson = null;
            if (CastleSave.RestoreSnapshot(json, splineParent, wallMaterial,
                palisadeMaterial))
                BuildLog.Add("Delete undone.");
            return;
        }

        if (marqueeStart.HasValue)
        {
            marqueeEnd = mouse.position.ReadValue();
            MarkMarqueeDoomed();
        }
        else if (deleteAnchor != null)
        {
            // Off-target frames (HUD, sky, unconnected walls) keep the
            // last preview so release still commits what the overlay
            // showed, matching build.
            if (!overUI && TryGetSectionUnderCursor(ray, out WallEdgeSection under))
                MarkPathDoomed(deleteAnchor, under);
        }
        else
        {
            doomedSections.Clear();
            deleteRanges.Clear();
            if (!overUI && TryGetSectionUnderCursor(ray, out WallEdgeSection hovered))
            {
                doomedSections.Add(hovered.transform);
                // A LINTEL is the masonry over a doorway, and deleting it
                // means filling the doorway in — the opening goes and the
                // wall closes. Deleting it as a section would leave a
                // hole in the wall exactly where a hole already is, which
                // is the one thing the click can't have meant.
                if (hovered.openingIndex >= 0 && hovered.edge != null)
                {
                    if (mouse.leftButton.wasPressedThisFrame)
                    {
                        deleteUndoJson = CastleSave.Snapshot();
                        WallEdge host = hovered.edge;
                        host.openings.RemoveAt(hovered.openingIndex);
                        host.Rebuild();
                        BuildLog.Add("Doorway filled in.");
                        Estate.Pay(Estate.DoorFee, "Doorway filled");
                        doomedSections.Clear();
                    }
                }
                else
                {
                    deleteRanges.Add((hovered.edge, hovered.index, hovered.index));
                    if (mouse.leftButton.wasPressedThisFrame)
                        deleteAnchor = hovered;
                }
            }
            // A stair dies whole — no per-tread surgery. Sections exist
            // because a wall is long and a breach is local; a stair is one
            // object, and its steps aren't WallEdgeSections at all.
            else if (!overUI && TryGetStairUnderCursor(ray, out WallStair hoveredStair))
            {
                foreach (Transform child in hoveredStair.transform)
                    doomedSections.Add(child);
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    deleteUndoJson = CastleSave.Snapshot();
                    // Refund's own line announces the removal with the
                    // money coming back — no second log line needed.
                    Estate.Refund(Estate.CostOfStair(hoveredStair), "Stair removed");
                    CloseStairWells(hoveredStair);
                    Destroy(hoveredStair.gameObject);
                    doomedSections.Clear();
                }
            }
            // A slab tile dies whole, like a stair. Walls standing on it
            // stay standing — structural rules are deferred, deliberately.
            else if (!overUI && TryGetSlabUnderCursor(ray, out SlabTile hoveredSlab))
            {
                doomedSections.Add(hoveredSlab.transform);
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    deleteUndoJson = CastleSave.Snapshot();
                    Estate.Refund(Estate.CostOfSlab(hoveredSlab),
                        hoveredSlab.isFoundation ? "Foundation removed" : "Floor removed");
                    Destroy(hoveredSlab.gameObject);
                    doomedSections.Clear();
                }
            }
            else if (!overUI && mouse.leftButton.wasPressedThisFrame)
            {
                marqueeStart = mouse.position.ReadValue();
                marqueeEnd = marqueeStart;
            }
        }

        ShowDeleteOverlays(doomedSections);

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            if (deleteAnchor != null || marqueeStart.HasValue)
                CommitDelete();
        }
    }

    // Everything red dies in one pass: walls first (rooms whose whole
    // boundary went breach themselves with their own log line), then any
    // rooms the marquee caught directly un-room. Each edge is surgered
    // exactly once — all of its doomed indices in a single graph call.
    void CommitDelete()
    {
        if (deleteRanges.Count > 0 || marqueeSlabs.Count > 0)
            deleteUndoJson = CastleSave.Snapshot();
        var byEdge = new Dictionary<WallEdge, HashSet<int>>();
        foreach ((WallEdge edge, int lo, int hi) in deleteRanges)
        {
            if (edge == null)
                continue;
            if (!byEdge.TryGetValue(edge, out HashSet<int> set))
                byEdge[edge] = set = new HashSet<int>();
            for (int i = lo; i <= hi; i++)
                set.Add(i);
        }
        // The demolition's worth comes back in full (v1: no salvage loss),
        // priced from the doomed sections BEFORE the surgery consumes them.
        long refund = 0;
        foreach (KeyValuePair<WallEdge, HashSet<int>> pair in byEdge)
            if (pair.Key != null)
                refund += Estate.CostOfSections(pair.Key, pair.Value);
        foreach (SlabTile slab in marqueeSlabs)
            if (slab != null)
                refund += Estate.CostOfSlab(slab);
        Estate.Refund(refund, "Demolition");
        foreach (KeyValuePair<WallEdge, HashSet<int>> pair in byEdge)
            if (pair.Key != null)
                WallGraph.DeleteSections(splineParent, pair.Key, pair.Value);
        foreach (SlabTile slab in marqueeSlabs)
        {
            if (slab == null)
                continue;
            Destroy(slab.gameObject);
            BuildLog.Add(slab.isFoundation
                ? "Foundation removed." : "Floor removed.");
        }
        CancelDelete();
    }

    void CancelDelete()
    {
        deleteAnchor = null;
        marqueeStart = null;
        marqueeEnd = null;
        doomedSections.Clear();
        deleteRanges.Clear();
        marqueeSlabs.Clear();
        HideDeleteOverlays();
    }

    // The painted run, re-derived every frame as the path from the
    // anchor section to the section under the cursor: walking the node
    // graph, the first and last edges contribute the span from the
    // touched section to the connecting joint, everything between dies
    // whole. No path (a separate wall, another storey, or the cursor
    // skipped too far) keeps the previous preview.
    void MarkPathDoomed(WallEdgeSection a, WallEdgeSection b)
    {
        if (a.edge == b.edge)
        {
            deleteRanges.Clear();
            deleteRanges.Add((a.edge, Mathf.Min(a.index, b.index), Mathf.Max(a.index, b.index)));
            RebuildDoomedFromRanges();
            return;
        }
        List<WallEdge> path = EdgePath(a.edge, b.edge);
        if (path == null)
            return;

        deleteRanges.Clear();
        for (int i = 0; i < path.Count; i++)
        {
            WallEdge edge = path[i];
            var sections = edge.SectionsInOrder();
            if (sections.Count == 0)
                continue;
            int last = sections[sections.Count - 1].index;
            if (i == 0)
            {
                WallNode exit = SharedNode(edge, path[1]);
                deleteRanges.Add(exit == edge.nodeB
                    ? (edge, a.index, last)
                    : (edge, 0, a.index));
            }
            else if (i == path.Count - 1)
            {
                WallNode entry = SharedNode(edge, path[i - 1]);
                deleteRanges.Add(entry == edge.nodeA
                    ? (edge, 0, b.index)
                    : (edge, b.index, last));
            }
            else
            {
                deleteRanges.Add((edge, 0, last));
            }
        }
        RebuildDoomedFromRanges();
    }

    void RebuildDoomedFromRanges()
    {
        doomedSections.Clear();
        foreach ((WallEdge edge, int lo, int hi) in deleteRanges)
        {
            if (edge == null)
                continue;
            foreach (WallEdgeSection section in edge.SectionsInOrder())
                if (section.index >= lo && section.index <= hi)
                    doomedSections.Add(section.transform);
        }
    }

    // Shortest hop path between two ground edges through shared nodes —
    // breadth-first over the node graph's member lists, read-only.
    static List<WallEdge> EdgePath(WallEdge from, WallEdge to)
    {
        var parent = new Dictionary<WallEdge, WallEdge> { [from] = from };
        var queue = new Queue<WallEdge>();
        queue.Enqueue(from);
        int guard = 0;
        while (queue.Count > 0 && guard++ < 512)
        {
            WallEdge cur = queue.Dequeue();
            if (cur == to)
            {
                var path = new List<WallEdge>();
                for (WallEdge walk = to; ; walk = parent[walk])
                {
                    path.Add(walk);
                    if (walk == from)
                        break;
                }
                path.Reverse();
                return path;
            }
            foreach (WallNode node in new[] { cur.nodeA, cur.nodeB })
            {
                if (node == null)
                    continue;
                foreach (WallNode.Member m in node.members)
                {
                    if (m.edge == null || parent.ContainsKey(m.edge))
                        continue;
                    parent[m.edge] = cur;
                    queue.Enqueue(m.edge);
                }
            }
        }
        return null;
    }

    static WallNode SharedNode(WallEdge a, WallEdge b)
    {
        if (a.nodeA == b.nodeA || a.nodeA == b.nodeB)
            return a.nodeA;
        return a.nodeB;
    }

    // Everything the rubber-band caught: wall sections at any storey by
    // their screen-projected centers, and slab tiles by their centers —
    // the red shells are the confirmation of exactly what release
    // will take.
    void MarkMarqueeDoomed()
    {
        doomedSections.Clear();
        deleteRanges.Clear();
        marqueeSlabs.Clear();
        Rect r = Rect.MinMaxRect(
            Mathf.Min(marqueeStart.Value.x, marqueeEnd.Value.x),
            Mathf.Min(marqueeStart.Value.y, marqueeEnd.Value.y),
            Mathf.Max(marqueeStart.Value.x, marqueeEnd.Value.x),
            Mathf.Max(marqueeStart.Value.y, marqueeEnd.Value.y));

        foreach (WallEdge edge in WallEdge.All)
        {
            if (edge == null)
                continue;
            foreach (WallEdgeSection section in edge.SectionsInOrder())
            {
                Renderer rend = section.GetComponent<Renderer>();
                Vector3 center = rend != null ? rend.bounds.center : section.transform.position;
                Vector3 sp = cam.WorldToScreenPoint(center);
                if (sp.z <= 0f || !r.Contains(new Vector2(sp.x, sp.y)))
                    continue;
                doomedSections.Add(section.transform);
                deleteRanges.Add((edge, section.index, section.index));
            }
        }
        foreach (SlabTile tile in SlabTile.All)
        {
            if (tile == null)
                continue;
            Vector3 sp = cam.WorldToScreenPoint(tile.transform.position);
            if (sp.z <= 0f || !r.Contains(new Vector2(sp.x, sp.y)))
                continue;
            marqueeSlabs.Add(tile);
            doomedSections.Add(tile.transform);
        }
    }

    // The slab tile directly under the cursor — the delete tool's target.
    bool TryGetSlabUnderCursor(Ray ray, out SlabTile tile)
    {
        tile = null;
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            tile = hit.collider.GetComponent<SlabTile>();
            return tile != null;
        }
        return false;
    }

    bool TryGetStairUnderCursor(Ray ray, out WallStair stair)
    {
        stair = null;
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            stair = hit.collider.GetComponentInParent<WallStair>();
            return stair != null;
        }
        return false;
    }

    // Deliberately precise (unlike build targeting) — deletion should only
    // ever affect the exact wall section under the cursor, never something
    // merely nearby.
    bool TryGetSectionUnderCursor(Ray ray, out WallEdgeSection section)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            section = hit.collider.GetComponent<WallEdgeSection>();
            return section != null;
        }

        section = null;
        return false;
    }

    // Ground elevation under a marker footprint: the lowest of its four
    // corners, so a marker on a slope embeds into the hillside instead of
    // leaving a floating corner. Falls back to y = 0 with no terrain.
    float SampleCellGroundY(float x, float z)
    {
        if (!Ground.Any)
            return 0f;

        float half = gridSize * 0.5f;
        float h0 = Ground.HeightAt(x - half, z - half);
        float h1 = Ground.HeightAt(x + half, z - half);
        float h2 = Ground.HeightAt(x - half, z + half);
        float h3 = Ground.HeightAt(x + half, z + half);
        float ground = Mathf.Min(Mathf.Min(h0, h1), Mathf.Min(h2, h3));

        if (baseStepSize > 0f)
            ground = Mathf.Floor(ground / baseStepSize) * baseStepSize;
        return ground;
    }

    // Distance guides: the square around the ghost (guideRange to each
    // side) splits into four sectors from its corners — ahead, behind,
    // left, and right of the pending wall's run — and each sector shows
    // one line to its closest wall. Spacing context in every direction,
    // never more than four lines. Walls being touched (flush joints,
    // snapped endpoints) are noise and get skipped.
    void UpdateDistanceGuides()
    {
        activeGuides.Clear();
        if (guideCenter.HasValue)
        {
            Vector3 center = guideCenter.Value;
            var bestFace = new Vector3[4];
            var bestDist = new float[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
            var found = new bool[4];
            // Only masonry on the gesture's own storey — a ground wall
            // measured against a wall two storeys up is noise.
            float? guideBase = anchorBaseY ?? hoverBaseY;
            foreach (WallEdge edge in WallEdge.All)
            {
                if (!BaseMatches(edge, guideBase))
                    continue;
                Vector3 face = edge.ClosestFacePoint(center, out float dist);
                if (dist > guideRange || dist < 0.05f)
                    continue;
                float bearing = Mathf.Atan2(face.z - center.z, face.x - center.x) * Mathf.Rad2Deg;
                float delta = Mathf.DeltaAngle(guideBearing, bearing);
                int sector = Mathf.Abs(delta) <= 45f ? 0
                    : Mathf.Abs(delta) >= 135f ? 1
                    : delta > 0f ? 2 : 3;
                if (dist < bestDist[sector])
                {
                    bestDist[sector] = dist;
                    bestFace[sector] = face;
                    found[sector] = true;
                }
            }
            for (int i = 0; i < 4; i++)
            {
                if (!found[i])
                    continue;
                Vector3 from = new Vector3(center.x, GuideY(center), center.z);
                Vector3 to = new Vector3(bestFace[i].x, GuideY(bestFace[i]), bestFace[i].z);
                activeGuides.Add((from, to, bestDist[i]));
            }
        }

        while (guidePool.Count < activeGuides.Count)
            guidePool.Add(CreateGuideLine());
        for (int i = 0; i < guidePool.Count; i++)
        {
            bool active = i < activeGuides.Count;
            guidePool[i].gameObject.SetActive(active);
            if (active)
            {
                guidePool[i].SetPosition(0, activeGuides[i].from);
                guidePool[i].SetPosition(1, activeGuides[i].to);
            }
        }
    }

    // Arc radius of the corner-angle readout, and where its label sits.
    const float AngleArcRadius = 1.5f;

    // The corner readout: while the pending wall hangs off a snapped
    // joint, an arc sweeps along the ground from the host wall's run to
    // the ghost's, and OnGUI labels it with the degrees between them — 90
    // is a square corner, 180 a straight continuation.
    void UpdateAngleGuide()
    {
        if (!activeAngle.HasValue)
        {
            if (angleLine != null)
                angleLine.gameObject.SetActive(false);
            return;
        }
        if (angleLine == null)
        {
            angleLine = CreateGuideLine();
            angleLine.gameObject.name = "AngleGuide";
        }
        angleLine.gameObject.SetActive(true);

        (Vector3 center, float fromBearing, float toBearing) = activeAngle.Value;
        float delta = Mathf.DeltaAngle(fromBearing, toBearing);
        const int ArcSteps = 24;
        angleLine.positionCount = ArcSteps + 1;
        for (int i = 0; i <= ArcSteps; i++)
        {
            float rad = (fromBearing + delta * i / ArcSteps) * Mathf.Deg2Rad;
            Vector3 p = center + new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * AngleArcRadius;
            angleLine.SetPosition(i, new Vector3(p.x, GuideY(p), p.z));
        }
    }

    // Guides hover just above the terrain so they read against it.
    float GuideY(Vector3 point)
    {
        if (!Ground.Any)
            return point.y + 0.3f;
        return Ground.HeightAt(point) + 0.3f;
    }

    // A point being placed sits on the EXTENSION of an existing straight
    // wall's line: draw white from that wall's near end to the point, so
    // "these line up" is visible instead of inferred. Curved edges are
    // skipped — an arc's extension is ambiguous.
    void AddExtensionAlignGuides(Vector3 probe)
    {
        const float Lateral = 0.15f;
        const float MaxReach = 30f;
        // (gap, from) per candidate; a graph split leaves collinear
        // pieces that all "align", so only the nearest end per direction
        // survives, and a probe sitting ON any aligned wall's own span
        // is a snap situation, not an alignment hint.
        var candidates = new List<(float gap, Vector3 from, float bearing)>();
        float? alignBase = anchorBaseY ?? hoverBaseY;
        foreach (WallEdge edge in WallEdge.All)
        {
            if (edge == null || !BaseMatches(edge, alignBase))
                continue;
            Vector3 a = edge.A, b = edge.B;
            Vector3 d = b - a;
            d.y = 0f;
            float len = d.magnitude;
            if (len < 0.5f)
                continue;
            d /= len;
            Vector3 bow = edge.control - (a + b) * 0.5f;
            bow.y = 0f;
            if (bow.sqrMagnitude > 0.01f)
                continue;
            Vector3 v = probe - a;
            v.y = 0f;
            float lateral = Mathf.Abs(d.x * v.z - d.z * v.x);
            float along = Vector3.Dot(v, d);
            if (lateral > Lateral)
                continue;
            if (along >= -0.25f && along <= len + 0.25f)
                return;
            bool beyondA = along < -0.25f && along > -MaxReach;
            bool beyondB = along > len + 0.25f && along < len + MaxReach;
            if (!beyondA && !beyondB)
                continue;
            Vector3 from = beyondA ? a : b;
            Vector3 gapV = probe - from;
            gapV.y = 0f;
            candidates.Add((gapV.magnitude, from,
                Mathf.Atan2(gapV.z, gapV.x) * Mathf.Rad2Deg));
        }
        candidates.Sort((x, y) => x.gap.CompareTo(y.gap));
        var kept = new List<float>();
        foreach ((float gap, Vector3 from, float bearing) in candidates)
        {
            bool duplicate = false;
            foreach (float kb in kept)
                if (Mathf.Abs(Mathf.DeltaAngle(kb, bearing)) < 3f)
                {
                    duplicate = true;
                    break;
                }
            if (duplicate)
                continue;
            kept.Add(bearing);
            alignLines.Add((from, probe));
            if (alignLines.Count >= 6)
                return;
        }
    }

    void UpdateAlignGuides()
    {
        while (alignPool.Count < alignLines.Count)
        {
            LineRenderer line = CreateGuideLine();
            line.gameObject.name = "AlignGuide";
            if (alignMaterial == null)
            {
                Shader shader = Shader.Find("HDRP/Unlit");
                alignMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
                if (alignMaterial.HasProperty("_UnlitColor"))
                    alignMaterial.SetColor("_UnlitColor", Color.white);
                else
                    alignMaterial.color = Color.white;
            }
            line.sharedMaterial = alignMaterial;
            line.widthMultiplier = 0.04f;
            alignPool.Add(line);
        }
        for (int i = 0; i < alignPool.Count; i++)
        {
            bool active = i < alignLines.Count;
            alignPool[i].gameObject.SetActive(active);
            if (!active)
                continue;
            Vector3 f = alignLines[i].from;
            Vector3 g = alignLines[i].to;
            alignPool[i].SetPosition(0, new Vector3(f.x, GuideY(f), f.z));
            alignPool[i].SetPosition(1, new Vector3(g.x, GuideY(g), g.z));
        }
    }

    LineRenderer CreateGuideLine()
    {
        GameObject lineObj = new GameObject("DistanceGuide");
        lineObj.transform.SetParent(transform, false);
        LineRenderer line = lineObj.AddComponent<LineRenderer>();
        line.positionCount = 2;
        line.widthMultiplier = 0.05f;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        if (guideMaterial == null)
        {
            Shader shader = Shader.Find("HDRP/Unlit");
            guideMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            if (guideMaterial.HasProperty("_UnlitColor"))
                guideMaterial.SetColor("_UnlitColor", guideColor);
            else
                guideMaterial.color = guideColor;
        }
        line.sharedMaterial = guideMaterial;
        return line;
    }

    static GUIStyle BuildGuideLabelStyle()
    {
        return new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold,
        };
    }

    // Distance labels ride the guide midpoints. IMGUI because it renders
    // identically under any pipeline — this is a measurement readout, not
    // scene art.
    void OnGUI()
    {
        // Walk mode draws its own line and nothing else — no guides, no
        // marquee, and no cutaway readout, because the walk cleared it.
        if (cam == null || WalkMode.Active)
            return;

        // The cutaway carries its own keys in its readout — it's a view
        // control, so it stays out of the modifier panel (which reports
        // what the tool in hand reads) and shows only while it's cutting.
        string cutaway = CutawayView.Readout();
        if (cutaway != null)
        {
            if (guideLabelStyle == null)
                guideLabelStyle = BuildGuideLabelStyle();
            // Centred at the top: the BuildLog owns the left margin, and a
            // view control that covers the log is a worse trade than the
            // one it was solving.
            var boxed = new GUIStyle(guideLabelStyle) { alignment = TextAnchor.MiddleCenter };
            var rect = new Rect((Screen.width - 460f) * 0.5f, 12f, 460f, 22f);
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), cutaway, boxed);
            GUI.color = guideColor;
            GUI.Label(rect, cutaway, boxed);
            GUI.color = Color.white;
        }

        if (activeGuides.Count == 0 && dimGuides.Count == 0
            && !activeAngle.HasValue && !marqueeStart.HasValue)
            return;

        // The delete marquee: a translucent red band in screen space.
        // Mouse coords are bottom-left origin, GUI is top-left — flip Y.
        if (marqueeStart.HasValue && marqueeEnd.HasValue)
        {
            Vector2 a = marqueeStart.Value, b = marqueeEnd.Value;
            float x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x);
            float y0 = Screen.height - Mathf.Max(a.y, b.y);
            float y1 = Screen.height - Mathf.Min(a.y, b.y);
            Rect band = new Rect(x0, y0, x1 - x0, y1 - y0);
            GUI.color = new Color(1f, 0.3f, 0.25f, 0.12f);
            GUI.DrawTexture(band, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.3f, 0.25f, 0.8f);
            GUI.DrawTexture(new Rect(band.x, band.y, band.width, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(band.x, band.yMax - 2f, band.width, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(band.x, band.y, 2f, band.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(band.xMax - 2f, band.y, 2f, band.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        if (guideLabelStyle == null)
            guideLabelStyle = BuildGuideLabelStyle();

        foreach ((Vector3 from, Vector3 to, float distance) guide in activeGuides)
        {
            Vector3 screen = cam.WorldToScreenPoint((guide.from + guide.to) * 0.5f);
            if (screen.z <= 0f)
                continue;
            DrawGuideLabel(screen, guide.distance.ToString("0.0") + "m");
        }

        // The active figure's own dimensions (rect side/width, circle
        // radius) — same styling, riding the figure's midlines.
        foreach ((Vector3 from, Vector3 to, float distance) dim in dimGuides)
        {
            Vector3 screen = cam.WorldToScreenPoint((dim.from + dim.to) * 0.5f);
            if (screen.z <= 0f)
                continue;
            DrawGuideLabel(screen, dim.distance.ToString("0.0") + "m");
        }

        // The corner angle rides just outside its arc's midpoint.
        if (activeAngle.HasValue)
        {
            (Vector3 center, float fromBearing, float toBearing) = activeAngle.Value;
            float delta = Mathf.DeltaAngle(fromBearing, toBearing);
            float midRad = (fromBearing + delta * 0.5f) * Mathf.Deg2Rad;
            Vector3 world = center + new Vector3(Mathf.Cos(midRad), 0f, Mathf.Sin(midRad)) * (AngleArcRadius + 0.55f);
            world.y = GuideY(world);
            Vector3 screen = cam.WorldToScreenPoint(world);
            if (screen.z > 0f)
                DrawGuideLabel(screen, Mathf.Abs(delta).ToString("0") + "°");
        }
        GUI.color = Color.white;
    }

    void DrawGuideLabel(Vector3 screen, string text)
    {
        Rect rect = new Rect(screen.x - 45f, Screen.height - screen.y - 12f, 90f, 24f);
        GUI.color = Color.black;
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, guideLabelStyle);
        GUI.color = guideColor;
        GUI.Label(rect, text, guideLabelStyle);
    }

    // Reuses pooled ghost instances so a long preview doesn't
    // instantiate/destroy objects every frame; extras left over from a
    // shorter previous preview are deactivated rather than destroyed.
    void HideGhosts()
    {
        foreach (GameObject ghost in ghostPool)
            ghost.SetActive(false);
        foreach (GameObject ghost in nodeGhostPool)
            ghost.SetActive(false);
    }

    // Fits a shell ghost over a source mesh, inflated slightly ABOUT THE
    // MESH'S OWN CENTER. Scaling the transform alone would also scale the
    // vertices' absolute coordinates — meshes built in world space on an
    // origin-pivoted object (room slabs, node posts) would drift by 2% of
    // their distance from the origin, floating a rooftop overlay half a
    // meter off the roof.
    static void FitShell(Transform ghost, Transform src, Mesh mesh)
    {
        Vector3 center = src.TransformPoint(mesh != null ? mesh.bounds.center : Vector3.zero);
        ghost.SetPositionAndRotation(
            Vector3.LerpUnclamped(center, src.position, DeleteOverlayScale), src.rotation);
        ghost.localScale = src.localScale * DeleteOverlayScale;
    }

    // Shells each doomed section in a red pooled copy — position, rotation,
    // and scale are taken from the section itself, so the overlay reads as
    // the section turning red rather than a separate marker.
    void ShowDeleteOverlays(List<Transform> sections)
    {
        for (int i = 0; i < sections.Count; i++)
        {
            GameObject overlay = GetPooled(deletePool, i, deletePreviewColor, "DeletePreview");
            overlay.SetActive(true);
            // Shell with the section's actual mesh so curved slices
            // highlight as curves, not as their bounding boxes.
            MeshFilter sectionFilter = sections[i].GetComponent<MeshFilter>();
            Mesh mesh = sectionFilter != null ? sectionFilter.sharedMesh : null;
            SetGhostMesh(overlay, mesh);
            FitShell(overlay.transform, sections[i], mesh);
        }
        for (int i = sections.Count; i < deletePool.Count; i++)
            deletePool[i].SetActive(false);
    }

    void HideDeleteOverlays()
    {
        foreach (GameObject overlay in deletePool)
            overlay.SetActive(false);
    }

    GameObject GetPooled(List<GameObject> pool, int index, Color tint, string name)
    {
        while (pool.Count <= index)
        {
            GameObject ghost = Instantiate(piecePrefab);
            ghost.name = name;
            TintPreview(ghost, tint);
            pool.Add(ghost);
        }
        return pool[index];
    }

    void TintPreview(GameObject preview, Color tint)
    {
        foreach (var col in preview.GetComponentsInChildren<Collider>())
            col.enabled = false;

        foreach (var rend in preview.GetComponentsInChildren<Renderer>())
        {
            foreach (var mat in rend.materials)
            {
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", tint);
                else if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", tint);
            }
        }
    }
}
