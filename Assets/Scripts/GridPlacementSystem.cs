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

    public enum ToolMode { Build, Delete, Offset }
    public ToolMode Mode { get; private set; } = ToolMode.Build;

    public enum BuildShape { Straight, Curved }
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
            if (Mode == ToolMode.Delete || previewBlocked)
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
    private (Vector3 start, Vector3 end, float bulge, float apexT, float height, float topY, float trimStart, float trimEnd)? lastPreviewKey;
    // The TRUE curve a release will commit — the preview may trim its
    // display back to a host's face, but the graph always gets the full
    // centerline-to-centerline gesture.
    private (Vector3 s, Vector3 c, Vector3 e, float height, float topY)? pendingCommit;
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
        snapTint = false;
        hoverSnap = null;
        hoverSnapYaw = null;
        hoverSnapHeight = null;
        pendingNodeGhosts.Clear();

        if (Mode == ToolMode.Delete)
            UpdateDelete(mouse, ray, overUI);
        else if (Mode == ToolMode.Offset)
            UpdateOffset(mouse, keyboard, ray, overUI);
        else
            UpdateBuild(mouse, keyboard, ray, overUI);

        // A scrolled height override lives as long as its context — the
        // active drag, or the hovered snap it was scrolled against.
        // Leaving both re-arms the snap height match.
        if (!splineStart.HasValue && !hoverSnapHeight.HasValue)
            heightOverride = false;

        UpdateDistanceGuides();
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
        deleteAnchor = null;
        offsetSource = null;
        doomedSections.Clear();
        CancelCurve();
        HideDeleteOverlays();
    }

    public void SetShape(BuildShape shape)
    {
        if (Shape == shape)
            return;
        Shape = shape;
        CancelCurve();
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
                if (!freePlace && TryNodeSnap(rawSurface, splineStart, out EndSnap endSnap))
                {
                    splineEnd = endSnap.point;
                    dragEndSnap = endSnap;
                }
                else if (!freePlace && TryFlankSnap(rawSurface, splineStart, out EndSnap flankSnap))
                {
                    splineEnd = flankSnap.point;
                    dragEndSnap = flankSnap;
                }
                else
                {
                    splineEnd = SteppedDragEnd(splineStart.Value, rawSurface, freePlace, snapAngle);
                    dragEndSnap = null;
                    // A flank-anchored drag is fully free by default;
                    // holding Shift steps its direction FROM the face
                    // normal in angleStep increments, so a lazy
                    // Shift-drag lands exactly 90° flush and deliberate
                    // angles land on clean steps off the host's face.
                    if (!freePlace && snapAngle && anchorSnap.HasValue && anchorSnap.Value.isFlank)
                    {
                        Vector3 d = splineEnd.Value - splineStart.Value;
                        d.y = 0f;
                        float len = d.magnitude;
                        if (len > 0.001f)
                        {
                            float rel = Vector3.SignedAngle(anchorSnap.Value.outDir, d, Vector3.up);
                            float snapped = Mathf.Round(rel / angleStep) * angleStep;
                            Vector3 dir = Quaternion.AngleAxis(snapped, Vector3.up) * anchorSnap.Value.outDir;
                            splineEnd = splineStart.Value + dir * len;
                        }
                    }
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
                activeAngle = (anchor, anchorSnap.Value.backBearing, guideBearing);

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
            ghost.transform.SetPositionAndRotation(end.transform.position, end.transform.rotation);
            ghost.transform.localScale = Vector3.one * DeleteOverlayScale;
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
                    && TryFlankSnap(rawSurface, splineStart, out EndSnap chordFlank))
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
                    activeAngle = (splineStart.Value, anchorSnap.Value.backBearing, guideBearing);
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
                activeAngle = (splineStart.Value, anchorSnap.Value.backBearing, guideBearing);

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
            (Vector3 s, Vector3 c, Vector3 e, float height, float topY) = pendingCommit.Value;
            WallGraph.CommitEdge(splineParent, s, c, e, EdgeParamsNow(height, topY));
        }
        CancelCurve();
    }

    WallGraph.EdgeParams EdgeParamsNow(float height, float topY)
    {
        return new WallGraph.EdgeParams
        {
            height = height,
            thickness = gridSize,
            baseWallHeight = BaseHeight,
            targetSectionLength = gridSize,
            baseStep = baseStepSize,
            fixedTopY = topY,
            material = wallMaterial,
        };
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
        heightOverride = false;
        dragEndSnap = null;
        pendingNodeGhosts.Clear();
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
        if (TryNodeSnap(rawSurface, null, out EndSnap nodeSnap))
        {
            hoverSnap = nodeSnap;
            hoverSnapYaw = nodeSnap.stubYaw;
            if (nodeSnap.edge != null)
                hoverSnapHeight = SnapHeightOf(nodeSnap.edge, nodeSnap.point);
            point = nodeSnap.point;
            return true;
        }

        // A wall's flank: the anchor lands ON the centerline under the
        // cursor — clicking plants a T-node there (the commit splits the
        // host) and the wall draws away from the face on the cursor's
        // side, at any angle. A parallel run beside a wall is the Offset
        // tool's job.
        if (TryFlankSnap(rawSurface, null, out EndSnap flankSnap))
        {
            hoverSnap = flankSnap;
            hoverSnapYaw = flankSnap.stubYaw;
            if (flankSnap.edge != null)
                hoverSnapHeight = SnapHeightOf(flankSnap.edge, flankSnap.point);
            point = flankSnap.point;
            return true;
        }

        // Terrain (or anything else that isn't a wall): the exact point
        // under the cursor — placement is free.
        point = rawSurface;
        return true;
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
    bool TryNodeSnap(Vector3 raw, Vector3? exclude, out EndSnap snap)
    {
        EndSnap best = default;
        float bestSq = endpointSnapRadius * endpointSnapRadius;
        bool found = false;
        foreach (WallNode node in WallNode.All)
        {
            EndSnap baseSnap = MakeNodeSnap(node);

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
            if (node.Valence == 1 && node.members[0].edge != null)
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

    EndSnap MakeNodeSnap(WallNode node)
    {
        // Representative member: the tallest wall at the node — the one a
        // chained wall most likely wants to match heights with.
        WallEdge repr = null;
        float best = -1f;
        foreach (WallNode.Member m in node.members)
        {
            if (m.edge == null)
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
    bool TryFlankSnap(Vector3 raw, Vector3? from, out EndSnap snap)
    {
        snap = default;
        float bestSq = float.MaxValue;
        bool found = false;
        foreach (WallEdge edge in WallEdge.All)
        {
            if (edge.IsCourse)
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
        float topY = LockedTopFor(start);
        var key = (start, end, bulge, apexT, height, topY, trimStart, trimEnd);
        if (lastPreviewKey.HasValue && lastPreviewKey.Value == key)
            return;
        lastPreviewKey = key;
        lastCourseKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        Vector3 control = ControlPointFor(start, end, bulge, apexT);
        pendingCommit = (start, control, end, height, topY);

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
            height, gridSize, gridSize, baseStepSize, BaseHeight, topY));
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

        float ground = SampleCellGroundY(point.x, point.z);
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
            WallGraph.CommitEdge(splineParent, s2, c2, e2,
                EdgeParamsNow(CurrentWallHeight, LockedTopFor(s2)));
            splineSpecs.Clear();
            ClearPreviewMeshes();
            lastOffsetKey = null;
            previewCrossings.Clear();
            HideGhosts();
        }
    }

    void BuildOffsetPreview(Vector3 s, Vector3 c, Vector3 e)
    {
        float topY = LockedTopFor(s);
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
            CurrentWallHeight, gridSize, gridSize, baseStepSize, BaseHeight, topY));
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
            ghost.transform.SetPositionAndRotation(section.transform.position, section.transform.rotation);
            ghost.transform.localScale = Vector3.one * DeleteOverlayScale;
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

    // Deletion mirrors the build flow: hovering shows a red overlay on the
    // single section the cursor is on, click-dragging extends that preview
    // along the section's own edge, and nothing is destroyed until the
    // button is released: exactly what's red when the button comes up is
    // removed — as a graph edit. A hole splits the edge into two edges at
    // new flat-capped nodes; survivors never rebuild because nothing about
    // them depended on the dead wall.
    void UpdateDelete(Mouse mouse, Ray ray, bool overUI)
    {
        if (deleteAnchor != null)
        {
            // Re-resolved only while the cursor is on the anchor's own
            // edge; off-target frames (HUD, sky, other walls) keep the
            // last preview so release still commits what the overlay
            // showed, matching build.
            if (!overUI && TryGetSectionUnderCursor(ray, out WallEdgeSection under)
                && under.edge == deleteAnchor.edge)
                MarkEdgeRangeDoomed(deleteAnchor, under);
        }
        else
        {
            doomedSections.Clear();
            if (!overUI && TryGetSectionUnderCursor(ray, out WallEdgeSection hovered))
            {
                doomedSections.Add(hovered.transform);
                if (mouse.leftButton.wasPressedThisFrame)
                    deleteAnchor = hovered;
            }
        }

        ShowDeleteOverlays(doomedSections);

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            if (deleteAnchor != null && doomedSections.Count > 0)
            {
                WallEdge edge = deleteAnchor.edge;
                int min = int.MaxValue, max = int.MinValue;
                foreach (Transform doomed in doomedSections)
                {
                    WallEdgeSection section = doomed != null ? doomed.GetComponent<WallEdgeSection>() : null;
                    if (section == null)
                        continue;
                    min = Mathf.Min(min, section.index);
                    max = Mathf.Max(max, section.index);
                }
                if (edge != null && min <= max)
                    WallGraph.DeleteSections(splineParent, edge, min, max);
                doomedSections.Clear();
                HideDeleteOverlays();
            }
            deleteAnchor = null;
        }
    }

    // Delete run: every section of the edge between the two drag ends, by
    // the edge's own ordering — terrain height plays no part.
    void MarkEdgeRangeDoomed(WallEdgeSection a, WallEdgeSection b)
    {
        doomedSections.Clear();
        int min = Mathf.Min(a.index, b.index);
        int max = Mathf.Max(a.index, b.index);
        foreach (WallEdgeSection section in a.edge.GetComponentsInChildren<WallEdgeSection>())
        {
            if (section.index >= min && section.index <= max)
                doomedSections.Add(section.transform);
        }
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
        if (cam == null || (activeGuides.Count == 0 && !activeAngle.HasValue))
            return;

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
            SetGhostMesh(overlay, sectionFilter != null ? sectionFilter.sharedMesh : null);
            overlay.transform.SetPositionAndRotation(sections[i].position, sections[i].rotation);
            overlay.transform.localScale = sections[i].localScale * DeleteOverlayScale;
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
