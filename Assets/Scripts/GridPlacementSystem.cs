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

    public enum ToolMode { Build, Delete, Offset, Room }
    public ToolMode Mode { get; private set; } = ToolMode.Build;

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
    public float CurrentWallHeight => BaseHeight * heightMultiplier;

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
            if (dragEndSnap.HasValue && dragEndSnap.Value.edge != null)
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
        return Mathf.Max(0.25f, edge.fixedTopY - SampleCellGroundY(at.x, at.z));
    }

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
    }

    private readonly List<GameObject> ghostPool = new List<GameObject>();
    private readonly List<GameObject> deletePool = new List<GameObject>();
    private readonly List<Transform> doomedSections = new List<Transform>();
    private readonly List<WallEdge.SectionSpec> splineSpecs = new List<WallEdge.SectionSpec>();
    private readonly List<Mesh> previewMeshes = new List<Mesh>();
    private (Vector3 start, Vector3 end, float bulge, float apexT, float height, float topY, float baseY, float trimStart, float trimEnd)? lastPreviewKey;
    // The TRUE curve a release will commit — the preview may trim its
    // display back to a host's face, but the graph always gets the full
    // centerline-to-centerline gesture.
    private (Vector3 s, Vector3 c, Vector3 e, float height, float topY, float baseY)? pendingCommit;
    private (WallEdge edge, float height)? lastCourseKey;
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
    private Transform splineParent;
    private float currentYRotation;
    private float heightMultiplier = 1f;
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
    private readonly List<WallRoom> marqueeRooms = new List<WallRoom>();
    private WallEdge offsetSource;
    private float offsetDistance = 3f;
    private float offsetSide = 1f;
    private (Vector3 s, Vector3 c, Vector3 e, float height, float topY)? lastOffsetKey;
    private Camera cam;
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
    private float? roomChainBaseY;
    private float? shapeBaseY;
    // Shape gestures adopt a snapped anchor's height like walls do.
    private float? shapeSnapHeight;

    const int CircleSegments = 8;
    const float MinCircleRadius = 1.5f;
    const float MinRectSide = 1f;
    // A figure side at least this coincident with existing masonry is
    // reused instead of redrawn.
    const float ReuseCoverage = 0.9f;

    void Start()
    {
        cam = Camera.main;
        splineParent = new GameObject("WallGraph").transform;

        MeshFilter prefabFilter = piecePrefab != null ? piecePrefab.GetComponent<MeshFilter>() : null;
        prefabMesh = prefabFilter != null ? prefabFilter.sharedMesh : null;
        MeshRenderer prefabRenderer = piecePrefab != null ? piecePrefab.GetComponent<MeshRenderer>() : null;
        wallMaterial = prefabRenderer != null ? prefabRenderer.sharedMaterial : null;
    }

    void Update()
    {
        if (piecePrefab == null || cam == null)
            return;

        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null)
            return;

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        // Clicks meant for the HUD shouldn't also reach through it into the
        // world and start placing or deleting behind the bar.
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

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
        pendingNodeGhosts.Clear();

        if (Mode == ToolMode.Delete)
            UpdateDelete(mouse, keyboard, ray, overUI);
        else if (Mode == ToolMode.Offset)
            UpdateOffset(mouse, keyboard, ray, overUI);
        else if (Shape == BuildShape.Circle || Shape == BuildShape.Rect)
            UpdateShapeGesture(mouse, keyboard, ray, overUI, Mode == ToolMode.Room);
        else if (Mode == ToolMode.Room)
            UpdateRoom(mouse, keyboard, ray, overUI);
        else
            UpdateBuild(mouse, keyboard, ray, overUI);

        // A scrolled height override lives as long as its context — the
        // active drag, or the hovered snap it was scrolled against.
        // Leaving both re-arms the snap height match.
        if (!splineStart.HasValue && !hoverSnapHeight.HasValue)
            heightOverride = false;

        UpdateDistanceGuides();
        UpdateAlignGuides();
        UpdateAngleGuide();
        UpdateNodeGhostVisuals();
    }

    // Both tools share the left mouse button, so switching abandons any
    // in-progress drag rather than letting it commit under the other tool's
    // rules.
    public void SetMode(ToolMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        offsetSource = null;
        CancelCurve();
        CancelShape();
        CancelRoomChain();
        CancelDelete();
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

    void UpdateBuild(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        HandleHeightScroll(mouse, keyboard);

        if (Shape == BuildShape.Curved)
            UpdateCurvedBuild(mouse, keyboard, ray, overUI);
        else
            UpdateStraightBuild(mouse, keyboard, ray, overUI);
    }

    // Straight walls: press, drag along a length-stepped line, release to
    // commit — the commit is a graph edit, so snapped ends join their
    // nodes, a far end on a wall face T-splits the host, and any wall the
    // drag crosses is auto-split into a shared 4-way node (posts preview
    // gold at the crossing points). Holding Shift with both ends snapped
    // opts into the tangent-tied connector (TryTangentControl) — the wall
    // auto-curves to tie in flush at both hosts. Hovering an existing
    // wall's top face previews a course along it instead, and one click
    // lays it; R turns the single-click stub's orientation.
    void UpdateStraightBuild(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelCurve();
            return;
        }
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            currentYRotation += 45f;

        bool freePlace = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        Vector3 point = default;
        Vector3 rawSurface = default;
        WallEdgeSection courseTarget = null;
        bool hasPoint = !overUI && TryGetCurveTarget(ray, freePlace, out point, out rawSurface, out courseTarget);

        if (!splineStart.HasValue)
        {
            if (hasPoint && courseTarget != null)
            {
                BuildCoursePreview(courseTarget.edge);
                snapTint = true;
                ShowSplineGhosts();
                if (mouse.leftButton.wasPressedThisFrame)
                    CommitCourse(courseTarget.edge);
            }
            else if (hasPoint)
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
                {
                    BuildStubPreview(point, currentYRotation);
                    ShowSplineGhosts();
                }
                guideCenter = point;
                guideBearing = -(hoverSnapYaw ?? currentYRotation);
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    splineStart = point;
                    anchorSnap = hoverSnap;
                    anchorSnapYaw = hoverSnapYaw;
                    anchorSnapHeight = hoverSnapHeight;
                    anchorBaseY = hoverBaseY;
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
            if (hasPoint)
            {
                // The far end snaps to nodes first, so closing a loop
                // lands exactly on the corner it started from — the snap
                // outranks length stepping while it holds. Failing that, a
                // wall's centerline grabs the end: the wall terminates
                // there and the commit T-splits the host, at any angle.
                if (!freePlace && TryNodeSnap(rawSurface, splineStart, anchorBaseY, out EndSnap endSnap))
                {
                    splineEnd = endSnap.point;
                    dragEndSnap = endSnap;
                }
                else if (!freePlace && TryFlankSnap(rawSurface, splineStart, anchorBaseY, out EndSnap flankSnap))
                {
                    splineEnd = flankSnap.point;
                    dragEndSnap = flankSnap;
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
                    if (!freePlace && snapAngle && anchorSnap.HasValue)
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
                    AddExtensionAlignGuides(splineEnd.Value);
                }
            }

            Vector3 anchor = splineStart.Value;
            Vector3 end = splineEnd ?? anchor;
            // Ends default to corner nodes on a straight wall — all the
            // turning lives in the posts. Holding Shift with both ends
            // snapped opts into the tangent-tied connector instead: the
            // wall auto-curves to leave one host and arrive at the other
            // straight-on, both joints flush end-on fuses.
            float dragBulge = 0f, dragApexT = 0.5f;
            bool tangentTied = anchorSnap.HasValue && dragEndSnap.HasValue && snapAngle
                && TryTangentControl(anchor, end, out dragBulge, out dragApexT);
            if (!tangentTied)
            {
                dragBulge = 0f;
                dragApexT = 0.5f;
            }
            bool highlightOnly = false;
            if (FlatDistance(anchor, end) >= 0.25f)
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
        pendingCommit = null;
        lastCourseKey = null;

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

    // Shift held with both drag ends snapped: solve the quadratic control
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
    // along the chord — and a final click commits. Hovering an existing
    // wall's top face previews a full course along it at the current
    // height; one click lays it. Escape backs out at any stage.
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
        WallEdgeSection courseTarget = null;
        bool hasPoint = !overUI && TryGetCurveTarget(ray, freePlace, out point, out rawSurface, out courseTarget);

        if (!splineStart.HasValue)
        {
            if (hasPoint && courseTarget != null)
            {
                BuildCoursePreview(courseTarget.edge);
                snapTint = true;
                ShowSplineGhosts();
                if (mouse.leftButton.wasPressedThisFrame)
                    CommitCourse(courseTarget.edge);
            }
            else if (hasPoint)
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
                    splineStart = point;
                    anchorSnap = hoverSnap;
                    anchorSnapYaw = hoverSnapYaw;
                    anchorSnapHeight = hoverSnapHeight;
                    anchorBaseY = hoverBaseY;
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
                if (!freePlace && !chordSnap.HasValue
                    && TryFlankSnap(rawSurface, splineStart, anchorBaseY, out EndSnap chordFlank))
                {
                    chordSnap = chordFlank;
                    point = chordFlank.point;
                }
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

    // Commits exactly what the ghost shows: the preview's own curve key,
    // handed to the graph — which resolves nodes, splits crossed hosts,
    // and chains the edges. Straight drags, curves, and stubs all share
    // this one commit path.
    void CommitSpline()
    {
        if (!previewBlocked && splineSpecs.Count > 0 && pendingCommit.HasValue)
        {
            (Vector3 s, Vector3 c, Vector3 e, float height, float topY, float baseY) = pendingCommit.Value;
            WallGraph.CommitEdge(splineParent, s, c, e, EdgeParamsNow(height, topY, baseY));
        }
        CancelCurve();
    }

    WallGraph.EdgeParams EdgeParamsNow(float height, float topY,
        float baseY = float.NegativeInfinity)
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
            material = wallMaterial,
        };
    }

    // Cursor surface for the room/shape tools: the first non-trigger hit
    // flattened, plus the slab top when that hit is a room's roof deck or
    // floor — elevated ground the gesture builds on.
    bool TryGetBuildSurface(Ray ray, out Vector3 point, out float? deckY)
    {
        point = default;
        deckY = null;
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            point = new Vector3(hit.point.x, 0f, hit.point.z);
            WallRoom room = hit.collider.GetComponentInParent<WallRoom>();
            if (room != null)
                deckY = hit.collider.gameObject.name == "RoomRoof" ? room.roofY : room.floorY;
            return true;
        }
        return false;
    }

    void CommitCourse(WallEdge below)
    {
        if (splineSpecs.Count > 0)
            WallGraph.CommitCourse(splineParent, below, CurrentWallHeight, BaseHeight, wallMaterial);
        CancelCurve();
    }

    void CancelCurve()
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        previewBlocked = false;
        previewCrossings.Clear();
        lastPreviewKey = null;
        pendingCommit = null;
        lastCourseKey = null;
        lastOffsetKey = null;
        splineStart = null;
        splineEnd = null;
        curveBulge = 0f;
        curveApexT = 0.5f;
        anchorSnap = null;
        anchorSnapYaw = null;
        anchorSnapHeight = null;
        anchorBaseY = null;
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
        bool hasPoint = !overUI && TryGetBuildSurface(ray, out raw, out deckY);
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
            if (canSnap && TryNodeSnap(raw, null, deckY, out EndSnap nodeSnap))
            {
                attach = nodeSnap;
                point = nodeSnap.point;
                if (nodeSnap.edge != null)
                    hoverSnapHeight = SnapHeightOf(nodeSnap.edge, nodeSnap.point);
                AdoptSnapElevation(nodeSnap);
            }
            else if (canSnap && TryFlankSnap(raw, null, deckY, out EndSnap flankSnap))
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
                // Plane captured NOW, while the hover snap still feeds
                // EffectiveWallHeight — a snapped figure locks exactly
                // onto the host's plane.
                shapeTopY = hoverBaseY.HasValue
                    ? float.NegativeInfinity : LockedTopFor(point);
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
                if (canSnap && TryNodeSnap(raw, anchor, shapeBaseY ?? deckY, out EndSnap sideNode))
                {
                    b = sideNode.point;
                    sideSnap = sideNode;
                }
                else if (canSnap && TryFlankSnap(raw, anchor, shapeBaseY ?? deckY, out EndSnap sideFlank))
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
            float coverage = WallGraph.CoincidentCoverage(s, c, e);
            if (coverage >= ReuseCoverage)
                continue;
            if (coverage > 0.05f)
                shapeBlocked = true;
            shapeSpecs.AddRange(WallEdge.BuildSpecs(s, c, e, height,
                gridSize, gridSize, baseStepSize, BaseHeight, topY,
                shapeBaseY ?? float.NegativeInfinity));
            foreach (WallGraph.Crossing x in WallGraph.FindCrossings(s, c, e))
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

    void CommitShape(bool designate)
    {
        WallEdge lastEdge = null;
        int reused = 0;
        WallGraph.EdgeParams p = EdgeParamsNow(ShapeHeight(), shapeTopY,
            shapeBaseY ?? float.NegativeInfinity);
        foreach ((Vector3 s, Vector3 c, Vector3 e) in shapeEdgesLive)
        {
            // Re-derived rather than read from preview state, so the
            // commit can't disagree with a stale ghost.
            if (WallGraph.CoincidentCoverage(s, c, e) >= ReuseCoverage)
            {
                reused++;
                continue;
            }
            List<WallEdge> created = WallGraph.CommitEdge(splineParent, s, c, e, p);
            if (created.Count > 0)
                lastEdge = created[created.Count - 1];
        }
        if (reused > 0)
            BuildLog.Add(reused == 1
                ? "Reused an existing wall as a side."
                : $"Reused existing walls as {reused} sides.");
        // ShapeEdges runs counter-clockwise, so the interior is on the
        // left of the last edge's own direction.
        if (designate && lastEdge != null)
            TryDesignateRoom(lastEdge, true);
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
    // once and the enclosed face designates a WallRoom. Escape abandons
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

        bool freePlace = keyboard != null && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        bool snapAngle = keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        Vector3 raw = default;
        float? deckY = null;
        bool hasPoint = !overUI && TryGetBuildSurface(ray, out raw, out deckY);
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
            if (canSnap && TryNodeSnap(raw, null, deckY, out EndSnap nodeSnap))
            {
                attach = nodeSnap;
                point = nodeSnap.point;
                if (nodeSnap.edge != null)
                    hoverSnapHeight = SnapHeightOf(nodeSnap.edge, nodeSnap.point);
                AdoptSnapElevation(nodeSnap);
            }
            else if (canSnap && TryFlankSnap(raw, null, deckY, out EndSnap flankSnap))
            {
                attach = flankSnap;
                point = flankSnap.point;
                if (flankSnap.edge != null)
                    hoverSnapHeight = SnapHeightOf(flankSnap.edge, flankSnap.point);
                AdoptSnapElevation(flankSnap);
            }
            snapTint = attach.HasValue;
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
                roomChainTopY = hoverBaseY.HasValue
                    ? float.NegativeInfinity : LockedTopFor(point);
            }
            return;
        }

        Vector3 last = roomChain[roomChain.Count - 1];
        if (hasPoint)
        {
            Vector3 end;
            EndSnap? endAttach = null;
            bool closingToStart = false;
            if (roomChain.Count >= 3 && FlatDistance(raw, roomChain[0]) < endpointSnapRadius)
            {
                // Back onto the chain's own first vertex: a pure loop.
                end = roomChain[0];
                closingToStart = true;
            }
            else if (canSnap && TryNodeSnap(raw, last, roomChainBaseY ?? deckY, out EndSnap nodeSnap))
            {
                end = nodeSnap.point;
                endAttach = nodeSnap;
            }
            else if (canSnap && TryFlankSnap(raw, last, roomChainBaseY ?? deckY, out EndSnap flankSnap))
            {
                end = flankSnap.point;
                endAttach = flankSnap;
            }
            else
            {
                end = SteppedDragEnd(last, raw, freePlace, snapAngle);
                // Shift steps the segment RELATIVE to the corner it grows
                // from — the previous segment's run, or the snapped
                // start's masonry — so stepped chain corners land on
                // clean 5° readings even off the world grid.
                if (!freePlace && snapAngle)
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
                }
                AddExtensionAlignGuides(end);
            }

            // No crossings in room mode: the first wall the segment
            // passes over clamps its end onto that wall's centerline.
            if (!closingToStart)
            {
                var crossings = WallGraph.FindCrossings(last, (last + end) * 0.5f, end);
                if (crossings.Count > 0)
                {
                    WallGraph.Crossing x = crossings[0];
                    end = new Vector3(x.point.x, 0f, x.point.z);
                    endAttach = x.reuseNode != null
                        ? MakeNodeSnap(x.reuseNode)
                        : new EndSnap { edge = x.edge, point = end, isFlank = true };
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
        var key = (a, b, roomChainHeight, roomChainTopY, trimStart, trimEnd);
        if (lastRoomKey.HasValue && lastRoomKey.Value == key)
            return;
        lastRoomKey = key;

        foreach (Mesh m in roomLiveMeshes)
            Destroy(m);
        roomLiveMeshes.Clear();
        roomLiveSpecs.Clear();
        Vector3 mid = (a + b) * 0.5f;
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
        roomLiveSpecs.AddRange(WallEdge.BuildSpecs(da, (da + db) * 0.5f, db, roomChainHeight,
            gridSize, gridSize, baseStepSize, BaseHeight, roomChainTopY,
            roomChainBaseY ?? float.NegativeInfinity));
        foreach (WallEdge.SectionSpec spec in roomLiveSpecs)
            roomLiveMeshes.Add(spec.mesh);
        roomLiveBlocked = WallGraph.OverlapsExisting(a, mid, b);
    }

    // A click fixed the live segment: its specs and meshes move to the
    // chain's baked set and the live slot starts over.
    void BakeRoomLiveSegment()
    {
        roomFixedSpecs.AddRange(roomLiveSpecs);
        roomFixedMeshes.AddRange(roomLiveMeshes);
        roomLiveSpecs.Clear();
        roomLiveMeshes.Clear();
        lastRoomKey = null;
        roomLiveBlocked = false;
    }

    // Baked chain in preview blue, live segment gold when the next click
    // would close the loop (red when refused).
    void ShowRoomGhosts()
    {
        int shown = 0;
        foreach (WallEdge.SectionSpec spec in roomFixedSpecs)
        {
            GameObject ghost = GetPooled(ghostPool, shown++, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            SetGhostTint(ghost, previewColor);
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
    // traced from the last chain edge and designated a WallRoom. The
    // chain's own winding picks the trace side, so the room is the face
    // the player drew around, not its complement.
    void CommitRoomChain()
    {
        WallEdge lastEdge = null;
        for (int i = 0; i + 1 < roomChain.Count; i++)
        {
            Vector3 a = roomChain[i];
            Vector3 b = roomChain[i + 1];
            List<WallEdge> created = WallGraph.CommitEdge(splineParent, a, (a + b) * 0.5f, b,
                EdgeParamsNow(roomChainHeight, roomChainTopY,
                    roomChainBaseY ?? float.NegativeInfinity));
            if (created.Count > 0)
                lastEdge = created[created.Count - 1];
        }
        // Chain wound counter-clockwise → its interior is on the left of
        // travel, which is the side TraceFace walks — trace the last edge
        // along the gesture. Clockwise → trace it reversed.
        if (lastEdge != null)
            TryDesignateRoom(lastEdge, WallRoom.SignedArea(roomChain) > 0f);
        CancelRoomChain();
    }

    // Traces the enclosed face off one committed boundary edge and
    // designates it a room. The caller's winding guess picks the side;
    // if that walk comes back as the outer face (negative area), the
    // other side is tried before giving up — walls stay either way.
    void TryDesignateRoom(WallEdge lastEdge, bool ccw)
    {
        List<WallGraph.DirEdge> ring = WallGraph.TraceFace(lastEdge, ccw);
        if (ring == null || WallRoom.RingArea(ring) <= 0f)
            ring = WallGraph.TraceFace(lastEdge, !ccw);
        if (ring != null && WallRoom.RingArea(ring) > 0f)
            WallRoom.Create(splineParent, ring, wallMaterial, baseStepSize, BaseHeight);
        else
            BuildLog.Add("No room: the walls committed but no enclosed face could be traced.");
    }

    void CancelRoomChain()
    {
        roomChain.Clear();
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

    // Resolves the cursor for both build shapes. Wall top faces are
    // course-stacking targets, nodes chain, a wall's flank grabs the
    // point onto its centerline (a T-split at commit), terrain resolves
    // to the exact point under the cursor, and Alt returns the raw
    // surface point.
    bool TryGetCurveTarget(Ray ray, bool freePlace, out Vector3 point, out Vector3 rawSurface, out WallEdgeSection courseTarget)
    {
        point = default;
        rawSurface = default;
        courseTarget = null;

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

        // A room slab is elevated ground: build ON it, exactly at the
        // cursor. Snaps below stay live but filter to this deck's own
        // walls — the masonry a storey down never grabs the gesture.
        WallRoom slabRoom = first.collider.GetComponentInParent<WallRoom>();
        float? deckY = null;
        if (slabRoom != null)
        {
            deckY = first.collider.gameObject.name == "RoomRoof"
                ? slabRoom.roofY : slabRoom.floorY;
            hoverBaseY = deckY;
        }

        if (freePlace)
        {
            point = rawSurface;
            return true;
        }

        WallEdgeSection section = first.collider.GetComponent<WallEdgeSection>();
        if (section != null && first.normal.y > 0.7f)
        {
            courseTarget = section;
            point = rawSurface;
            return true;
        }

        // Node snapping: existing joints outrank everything nearby — the
        // point locks EXACTLY onto the node, so chains and closed loops
        // meet where the network already is.
        if (TryNodeSnap(rawSurface, null, deckY, out EndSnap nodeSnap))
        {
            hoverSnap = nodeSnap;
            hoverSnapYaw = nodeSnap.stubYaw;
            if (nodeSnap.edge != null)
                hoverSnapHeight = SnapHeightOf(nodeSnap.edge, nodeSnap.point);
            AdoptSnapElevation(nodeSnap);
            point = nodeSnap.point;
            return true;
        }

        // A wall's flank: the anchor lands ON the centerline under the
        // cursor — clicking plants a T-node there (the commit splits the
        // host) and the wall draws away from the face on the cursor's
        // side, at any angle. A parallel run beside a wall is the Offset
        // tool's job.
        if (TryFlankSnap(rawSurface, null, deckY, out EndSnap flankSnap))
        {
            hoverSnap = flankSnap;
            hoverSnapYaw = flankSnap.stubYaw;
            if (flankSnap.edge != null)
                hoverSnapHeight = SnapHeightOf(flankSnap.edge, flankSnap.point);
            AdoptSnapElevation(flankSnap);
            point = flankSnap.point;
            return true;
        }

        // Terrain (or anything else that isn't a wall): the exact point
        // under the cursor — placement is free.
        point = rawSurface;
        return true;
    }

    // Whether an edge belongs to the storey being hovered: no filter on
    // ground hovers (null — any wall grabs, and the gesture adopts its
    // elevation), but a deck hover only offers walls standing ON that
    // deck — the masonry a storey down keeps its distance.
    static bool BaseMatches(WallEdge edge, float? atBase)
    {
        if (!atBase.HasValue)
            return true;
        return !float.IsNegativeInfinity(edge.baseY)
            && Mathf.Abs(edge.baseY - atBase.Value) <= 0.25f;
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
            if (atBase.HasValue && baseSnap.edge == null)
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
            if (edge.IsCourse || !BaseMatches(edge, atBase))
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
            Vector3 side = edge.SideOf(from ?? raw) * FlankNormalAt(edge, t);

            bestSq = centerSq;
            found = true;
            snap = new EndSnap
            {
                edge = edge,
                node = null,
                point = cPoint,
                backBearing = Mathf.Atan2(-side.z, -side.x) * Mathf.Rad2Deg,
                stubYaw = Mathf.Atan2(-side.z, side.x) * Mathf.Rad2Deg,
                outDir = side,
                isFlank = true,
                stubTrim = halfThick,
            };
        }
        return found;
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
        var key = (start, end, bulge, apexT, height, topY, baseFloor, trimStart, trimEnd);
        if (lastPreviewKey.HasValue && lastPreviewKey.Value == key)
            return;
        lastPreviewKey = key;
        lastCourseKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        Vector3 control = ControlPointFor(start, end, bulge, apexT);
        pendingCommit = (start, control, end, height, topY, baseFloor);

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
            height, gridSize, gridSize, baseStepSize, BaseHeight, topY, baseFloor));
        foreach (WallEdge.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);

        // The graph verdicts the whole gesture: refused only for a
        // near-exact redraw along an existing wall; crossings are legal
        // and become shared nodes (shown as gold posts).
        previewBlocked = WallGraph.OverlapsExisting(start, control, end);
        previewCrossings = WallGraph.FindCrossings(start, control, end);
    }

    void BuildCoursePreview(WallEdge edge)
    {
        var key = (edge, CurrentWallHeight);
        if (lastCourseKey.HasValue && lastCourseKey.Value == key)
            return;
        lastCourseKey = key;
        lastPreviewKey = null;
        pendingCommit = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        previewBlocked = false;
        previewCrossings.Clear();
        // Only the spans not already under a course — a second course
        // never doubles into the first.
        foreach ((float lo, float hi) in edge.UncoveredRanges())
            splineSpecs.AddRange(edge.BuildCourseSpecs(CurrentWallHeight, BaseHeight, lo, hi));
        foreach (WallEdge.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
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

    // A full box ghost marking the curve tool's start point.
    void ShowStartMarker(Vector3 point)
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        previewBlocked = false;
        previewCrossings.Clear();
        lastPreviewKey = null;
        pendingCommit = null;
        lastCourseKey = null;

        float ground = hoverBaseY ?? SampleCellGroundY(point.x, point.z);
        float height = EffectiveWallHeight;
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
            WallGraph.CommitEdge(splineParent, s2, c2, e2,
                EdgeParamsNow(CurrentWallHeight,
                    float.IsNegativeInfinity(srcBase) ? LockedTopFor(s2) : float.NegativeInfinity,
                    srcBase));
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
        var key = (s, c, e, CurrentWallHeight, topY);
        if (lastOffsetKey.HasValue && lastOffsetKey.Value == key)
            return;
        lastOffsetKey = key;
        lastPreviewKey = null;
        pendingCommit = null;
        lastCourseKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        splineSpecs.AddRange(WallEdge.BuildSpecs(s, c, e,
            CurrentWallHeight, gridSize, gridSize, baseStepSize, BaseHeight, topY, srcBase));
        foreach (WallEdge.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
        previewBlocked = WallGraph.OverlapsExisting(s, c, e);
        previewCrossings = WallGraph.FindCrossings(s, c, e);
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
        pendingCommit = null;
        lastCourseKey = null;
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
                deleteRanges.Add((hovered.edge, hovered.index, hovered.index));
                if (mouse.leftButton.wasPressedThisFrame)
                    deleteAnchor = hovered;
            }
            else if (!overUI && TryGetRoomUnderCursor(ray, out WallRoom hoveredRoom))
            {
                // A room's slabs delete as one: the designation lifts,
                // floor and roof go, the walls stay standing.
                foreach (Transform child in hoveredRoom.transform)
                    doomedSections.Add(child);
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    Destroy(hoveredRoom.gameObject);
                    BuildLog.Add("Room removed — walls kept.");
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
        foreach (KeyValuePair<WallEdge, HashSet<int>> pair in byEdge)
            if (pair.Key != null)
                WallGraph.DeleteSections(splineParent, pair.Key, pair.Value);
        foreach (WallRoom room in marqueeRooms)
        {
            if (room == null || room.broken)
                continue;
            Destroy(room.gameObject);
            BuildLog.Add("Room removed — walls kept.");
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
        marqueeRooms.Clear();
        HideDeleteOverlays();
    }

    // The painted run, re-derived every frame as the path from the
    // anchor section to the section under the cursor: walking the node
    // graph, the first and last edges contribute the span from the
    // touched section to the connecting joint, everything between dies
    // whole. Courses live off-graph, so a course paint stays on its own
    // edge. No path (separate wall, or the cursor skipped too far) keeps
    // the previous preview.
    void MarkPathDoomed(WallEdgeSection a, WallEdgeSection b)
    {
        if (a.edge == b.edge)
        {
            deleteRanges.Clear();
            deleteRanges.Add((a.edge, Mathf.Min(a.index, b.index), Mathf.Max(a.index, b.index)));
            RebuildDoomedFromRanges();
            return;
        }
        if (a.edge.IsCourse || b.edge.IsCourse)
            return;
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
                    if (m.edge == null || m.edge.IsCourse || parent.ContainsKey(m.edge))
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

    // Everything the rubber-band caught: wall sections (courses too) by
    // their screen-projected centers, and rooms by either slab's center
    // — the red shells are the confirmation of exactly what release
    // will take.
    void MarkMarqueeDoomed()
    {
        doomedSections.Clear();
        deleteRanges.Clear();
        marqueeRooms.Clear();
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
        foreach (WallRoom room in WallRoom.All)
        {
            if (room == null || room.broken)
                continue;
            foreach (Transform child in room.transform)
            {
                Renderer rend = child.GetComponent<Renderer>();
                if (rend == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(rend.bounds.center);
                if (sp.z <= 0f || !r.Contains(new Vector2(sp.x, sp.y)))
                    continue;
                marqueeRooms.Add(room);
                foreach (Transform slab in room.transform)
                    doomedSections.Add(slab);
                break;
            }
        }
    }

    // The room whose slab (floor or roof deck) is directly under the
    // cursor — the delete tool's un-room target.
    bool TryGetRoomUnderCursor(Ray ray, out WallRoom room)
    {
        room = null;
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            room = hit.collider.GetComponentInParent<WallRoom>();
            return room != null;
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
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return 0f;

        float half = gridSize * 0.5f;
        float h0 = terrain.SampleHeight(new Vector3(x - half, 0f, z - half));
        float h1 = terrain.SampleHeight(new Vector3(x + half, 0f, z - half));
        float h2 = terrain.SampleHeight(new Vector3(x - half, 0f, z + half));
        float h3 = terrain.SampleHeight(new Vector3(x + half, 0f, z + half));
        float ground = terrain.transform.position.y + Mathf.Min(Mathf.Min(h0, h1), Mathf.Min(h2, h3));

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
            foreach (WallEdge edge in WallEdge.All)
            {
                if (edge.IsCourse)
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
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return point.y + 0.3f;
        return terrain.transform.position.y + terrain.SampleHeight(point) + 0.3f;
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
        foreach (WallEdge edge in WallEdge.All)
        {
            if (edge == null || edge.IsCourse)
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

    // Distance labels ride the guide midpoints. IMGUI because it renders
    // identically under any pipeline — this is a measurement readout, not
    // scene art.
    void OnGUI()
    {
        if (cam == null || (activeGuides.Count == 0 && dimGuides.Count == 0
            && !activeAngle.HasValue && !marqueeStart.HasValue))
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
        {
            guideLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
            };
        }

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
