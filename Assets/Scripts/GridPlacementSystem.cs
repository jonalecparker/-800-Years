using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// The wall-building tool. Every wall is a SplineWall — a straight wall is
// just a bulge-0 spline — so junctions, terrain stepping, course stacking,
// texturing, and deletion are each implemented exactly once. There is no
// grid: placement is free, and alignment comes from assists — endpoint
// chaining, drag stepping relative to the anchor, distance guides, and
// the offset tool. (The class keeps its historical name so scene
// references survive.)
public class GridPlacementSystem : MonoBehaviour
{
    [Header("Placement")]
    // Source of the wall material, the box ghost mesh, and the preview
    // pool instances — no pieces of it are placed anymore.
    public GameObject piecePrefab;
    // Wall thickness and target masonry section length. (Historically
    // also the grid cell size; the grid is gone and nothing snaps to
    // world-space cells anymore.)
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
    // come from shared increments anymore but from snapping, which adopts
    // the snapped wall's exact height.
    public float minHeightMultiplier = 0.25f;
    public float maxHeightMultiplier = 3f;
    // Meters of wall height per wheel notch.
    public float heightScrollRate = 0.25f;

    [Header("Curve")]
    // Curvature moves in fixed steps so two separately-built curves over
    // the same chord can be made genuinely identical.
    public float curveBulgeStep = 0.5f;
    // Existing wall endpoints within this range of the cursor grab a new
    // wall's endpoint, so walls chain into networks. Hold Alt to place
    // freely.
    public float endpointSnapRadius = 0.75f;

    [Header("Preview")]
    public Color previewColor = new Color(0.3f, 0.9f, 1f, 1f);
    public Color deletePreviewColor = new Color(1f, 0.3f, 0.25f, 1f);
    // The ghost runs gold while an end snap is deciding its position —
    // hovering a snapped start point, or a drag end landing on a wall end
    // — and blue the rest of the time.
    public Color snapColor = new Color(1f, 0.78f, 0.12f, 1f);

    [Header("Assists")]
    // While placing, each free endpoint of the pending wall shows a line
    // to the nearest wall within this range, labelled with the
    // face-to-face distance — relationships, where the grid only shows
    // rhythm.
    public float guideRange = 5f;
    public Color guideColor = new Color(1f, 0.85f, 0.2f, 1f);
    // Free placement is assisted, not gridded: drag direction is fully
    // freeform (hold Shift to snap it to angleStep degrees of bearing),
    // while length steps in lengthStep increments RELATIVE to the anchor
    // so strokes stay commensurable. Alt disables all stepping.
    public float angleStep = 15f;
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
            if (dragEndSnap.HasValue && dragEndSnap.Value.wall != null)
                return SnapHeightOf(dragEndSnap.Value.wall, dragEndSnap.Value.point);
            if (hoverSnapHeight.HasValue)
                return hoverSnapHeight.Value;
            return CurrentWallHeight;
        }
    }

    // What "match the snapped wall's height" means: its stored height —
    // except for a locked-top wall, whose stored height says nothing
    // about its profile on a slope. There the ghost takes the span that
    // puts its own top at the host's locked top at the snap point, so
    // the joint lines up (and with the lock toggle on, the new wall
    // continues that exact level top).
    float SnapHeightOf(SplineWall wall, Vector3 at)
    {
        if (float.IsNegativeInfinity(wall.fixedTopY))
            return wall.height;
        return Mathf.Max(0.25f, wall.fixedTopY - SampleCellGroundY(at.x, at.z));
    }

    // How many sections the active preview will actually lay down —
    // blocked (red) specs don't count, and 0 when idle, so the HUD
    // counter can show/hide off this alone.
    public int ActiveDragCount
    {
        get
        {
            if (Mode == ToolMode.Delete)
                return 0;
            int count = 0;
            foreach (SplineWall.SectionSpec spec in splineSpecs)
                if (!spec.blocked)
                    count++;
            return count;
        }
    }

    // Slightly larger than the section it covers so the overlay shell
    // draws over the section's own surface instead of z-fighting with it.
    const float DeleteOverlayScale = 1.02f;

    // One exact end-snap result: where the point locked, the bearing
    // pointing from that end back along its wall (the angle guide's
    // zero), and the yaw a pre-click stub takes there — perpendicular to
    // the host's run, so corner and T previews trim clean instead of
    // burying collinearly.
    struct EndSnap
    {
        public SplineWall wall;
        public Vector3 point;
        public float backBearing;
        public float stubYaw;
        // The direction a wall LEAVING this snap point should run to meet
        // the host cleanly: outward along the run for endpoint snaps,
        // sideways off the face for flank snaps. Feeds the
        // tangent-matched connector when both ends of a drag are snapped.
        public Vector3 outDir;
        // The endpoint candidate sits on the host's centerline, where a
        // carved stub preview trims away to nothing — the hover ghost
        // shows a solid marker there instead.
        public bool isEndpoint;
        // Mid-wall flank termination: the point sits ON the host's face
        // and the commit makes a T-node there, the host joining as a
        // flank member. Like isEndpoint, this end builds as a node end.
        public bool isFlank;
    }

    private readonly List<GameObject> ghostPool = new List<GameObject>();
    private readonly List<GameObject> deletePool = new List<GameObject>();
    private readonly List<Transform> doomedSections = new List<Transform>();
    private readonly List<SplineWall.SectionSpec> splineSpecs = new List<SplineWall.SectionSpec>();
    private readonly List<Mesh> previewMeshes = new List<Mesh>();
    private (Vector3 start, Vector3 end, float bulge, float apexT, float height, float topY, bool nodeStart, bool nodeEnd)? lastPreviewKey;
    private (SplineWall wall, float height)? lastCourseKey;
    private EndSnap? hoverSnap;
    private EndSnap? anchorSnap;
    private EndSnap? dragEndSnap;
    private bool snapTint;
    // The host-derived yaw of the current hover snap (end OR flank) —
    // snapped stubs sit flush with the wall they snapped to instead of
    // keeping their own R rotation. Captured at click for the drag stub.
    private float? hoverSnapYaw;
    private float? anchorSnapYaw;
    // The snapped wall's height (end OR flank snap) — the ghost matches
    // it exactly instead of using the scrolled height. Captured at click
    // for the drag, same as the yaw.
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
    private SplineWallSection deleteAnchor;
    private SplineWall offsetSource;
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
    // Corner-post ghosts: gold posts at endpoint-snapped ends while
    // placing — the corner masonry the commit will actually create.
    private readonly List<(Vector3 pos, float ground, float height)> pendingNodeGhosts = new List<(Vector3, float, float)>();
    private readonly List<GameObject> nodeGhostPool = new List<GameObject>();
    private readonly List<Mesh> nodeGhostMeshes = new List<Mesh>();
    private readonly List<float> nodeGhostHeights = new List<float>();

    void Start()
    {
        cam = Camera.main;
        splineParent = new GameObject("SplineWalls").transform;

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

    // Straight walls: press, drag along an angle-stepped line, release to
    // commit a bulge-0 SplineWall, identical in kind to a curved one.
    // Endpoint-snapped ends commit as CORNER NODES: the wall stays
    // straight and the turning happens in the node's round post
    // (WallNode), at any angle. Holding Shift with both ends snapped opts
    // into the tangent-tied connector instead (TryTangentControl) — the
    // wall auto-curves to tie in flush at both hosts. Hovering an
    // existing wall's top face previews a full course along it instead,
    // and one click lays it; R turns the single-click stub's orientation.
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
        SplineWallSection courseTarget = null;
        bool hasPoint = !overUI && TryGetCurveTarget(ray, freePlace, out point, out rawSurface, out courseTarget);

        if (!splineStart.HasValue)
        {
            if (hasPoint && courseTarget != null)
            {
                BuildCoursePreview(courseTarget.wall);
                snapTint = true;
                ShowSplineGhosts();
                if (mouse.leftButton.wasPressedThisFrame)
                    CommitCourse(courseTarget.wall);
            }
            else if (hasPoint)
            {
                float stubYaw = hoverSnapYaw ?? currentYRotation;
                snapTint = hoverSnapYaw.HasValue;
                // A node-forming snap previews as a rectangular stub
                // leaving the point outward — flush outside the host,
                // never a half-buried post marker.
                if (hoverSnap.HasValue && (hoverSnap.Value.isEndpoint || hoverSnap.Value.isFlank))
                {
                    BuildSnappedStubPreview(hoverSnap.Value, point);
                    ShowSplineGhosts();
                }
                else
                {
                    BuildStubPreview(point, stubYaw);
                    ShowSplineGhosts();
                }
                guideCenter = point;
                guideBearing = -stubYaw;
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
                // The far end snaps to wall ends too, so closing a loop
                // lands exactly on the corner it started from — the snap
                // outranks length stepping while it holds. Failing an end
                // snap, a wall FACE grabs the end instead: the wall
                // terminates there as a T-joint node, at any angle.
                if (!freePlace && TryEndSnap(rawSurface, splineStart, out EndSnap endSnap))
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
                    // A flank-anchored drag measures its direction FROM the
                    // face normal, always stepped — a lazy short drag lands
                    // exactly 90° flush, deliberate angles land on clean
                    // 15° increments. Alt frees as everywhere.
                    if (!freePlace && anchorSnap.HasValue && anchorSnap.Value.isFlank)
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
            // Endpoint-snapped ends default to corner nodes on a straight
            // wall — all the turning lives in the posts. Holding Shift
            // with both ends snapped opts into the tangent-tied connector
            // instead: the wall auto-curves to leave one host and arrive
            // at the other straight-on, both joints flush end-on fuses.
            float dragBulge = 0f, dragApexT = 0.5f;
            bool tangentTied = anchorSnap.HasValue && dragEndSnap.HasValue && snapAngle
                && TryTangentControl(anchor, end, out dragBulge, out dragApexT);
            if (!tangentTied)
            {
                dragBulge = 0f;
                dragApexT = 0.5f;
            }
            bool nodeStart = anchorSnap.HasValue
                && (anchorSnap.Value.isEndpoint || anchorSnap.Value.isFlank);
            bool nodeEnd = dragEndSnap.HasValue
                && (dragEndSnap.Value.isEndpoint || dragEndSnap.Value.isFlank);
            if (FlatDistance(anchor, end) >= 0.25f)
            {
                BuildSplinePreview(anchor, end, dragBulge, dragApexT, nodeStart, nodeEnd,
                    PendingIgnores(anchorSnap, anchor, dragEndSnap, end));
                // Post ghosts only where a real corner post will stand —
                // flank ends cap flat ON the host's face, no cylinder.
                if (nodeStart && anchorSnap.Value.isEndpoint)
                    AddNodeGhost(anchor);
                if (nodeEnd && dragEndSnap.Value.isEndpoint)
                    AddNodeGhost(end);
            }
            else if (nodeStart)
            {
                BuildSnappedStubPreview(anchorSnap.Value, anchor);
            }
            else
            {
                BuildStubPreview(anchor, anchorSnapYaw ?? currentYRotation);
            }
            snapTint = dragEndSnap.HasValue;
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

    // A zero-length run still means a wall: a token 10cm chord through the
    // clicked point, oriented by R — or by the snapped host wall — that
    // the sweep's own half-section end extensions grow into a short stub,
    // and bury into any wall standing beside it, same as a long wall's
    // ends would.
    void BuildStubPreview(Vector3 center, float yawDegrees)
    {
        float rad = yawDegrees * Mathf.Deg2Rad;
        Vector3 halfChord = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad)) * 0.05f;
        BuildSplinePreview(center - halfChord, center + halfChord, 0f, 0.5f);
    }

    // The ghost for a node-forming snap (endpoint or flank): a token
    // chord LEAVING the snap point along its outward direction, node-
    // capped flat ON the point — a rectangle sitting wholly outside the
    // host, flush with its face or cap, never a half-buried marker.
    void BuildSnappedStubPreview(EndSnap snap, Vector3 point)
    {
        Vector3 end = point + snap.outDir * 0.1f;
        BuildSplinePreview(point, end, 0f, 0.5f, true, false,
            PendingIgnores(snap, point, null, end));
    }

    // Shift held with both drag ends snapped to wall ends: solve the
    // quadratic control point that makes the connector LEAVE the anchor's
    // host straight-on and ARRIVE at the far host straight-on — the
    // intersection of the two snaps' departure rays. Each joint then
    // meets its host collinear (the flush end-on fuse) instead of turning
    // at a corner node, for the deliberate sweeping run between towers.
    // False — stay straight — when the tangents are parallel (an S no
    // single quadratic can make), diverge, or demand an extreme curve.
    bool TryTangentControl(Vector3 anchor, Vector3 end, out float bulge, out float apexT)
    {
        bulge = 0f;
        apexT = 0.5f;
        Vector3 d0 = anchorSnap.Value.outDir;
        Vector3 d1 = dragEndSnap.Value.outDir;
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
    // snapping to existing wall endpoints first, then the grid; Alt places
    // freely), then bend — plain scroll steps the bulge while the cursor
    // drags the apex along the chord — and a final click commits. Hovering
    // an existing wall instead previews a full course along it at the
    // current height; one click lays it. Escape backs out at any stage.
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
        SplineWallSection courseTarget = null;
        bool hasPoint = !overUI && TryGetCurveTarget(ray, freePlace, out point, out rawSurface, out courseTarget);

        if (!splineStart.HasValue)
        {
            if (hasPoint && courseTarget != null)
            {
                BuildCoursePreview(courseTarget.wall);
                snapTint = true;
                ShowSplineGhosts();
                if (mouse.leftButton.wasPressedThisFrame)
                    CommitCourse(courseTarget.wall);
            }
            else if (hasPoint)
            {
                snapTint = hoverSnapYaw.HasValue;
                if (hoverSnap.HasValue && (hoverSnap.Value.isEndpoint || hoverSnap.Value.isFlank))
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
                // Failing an end snap, a wall face grabs the chord's far
                // end for a T-node, same as the straight tool. The hover's
                // flank pick is discarded first: it chose the face by
                // cursor side (the anchor gesture) — a far end approaching
                // a host must land on the face the ANCHOR is on, so it is
                // re-derived here with the anchor picking the side.
                EndSnap? chordSnap = hoverSnap;
                if (chordSnap.HasValue && chordSnap.Value.isFlank)
                    chordSnap = null;
                if (!freePlace && !chordSnap.HasValue
                    && TryFlankSnap(rawSurface, splineStart, out EndSnap chordFlank))
                {
                    chordSnap = chordFlank;
                    point = chordFlank.point;
                }
                bool chordNodeStart = anchorSnap.HasValue
                    && (anchorSnap.Value.isEndpoint || anchorSnap.Value.isFlank);
                bool chordNodeEnd = chordSnap.HasValue
                    && (chordSnap.Value.isEndpoint || chordSnap.Value.isFlank);
                BuildSplinePreview(splineStart.Value, point, 0f, 0.5f, chordNodeStart, chordNodeEnd,
                    PendingIgnores(anchorSnap, splineStart.Value, chordSnap, point));
                if (chordNodeStart && anchorSnap.Value.isEndpoint)
                    AddNodeGhost(splineStart.Value);
                if (chordNodeEnd && chordSnap.Value.isEndpoint)
                    AddNodeGhost(point);
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
                    // Remember the snap here — the commit turns it into a
                    // corner or T node.
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

            bool bendNodeStart = anchorSnap.HasValue
                && (anchorSnap.Value.isEndpoint || anchorSnap.Value.isFlank);
            bool bendNodeEnd = dragEndSnap.HasValue
                && (dragEndSnap.Value.isEndpoint || dragEndSnap.Value.isFlank);
            BuildSplinePreview(splineStart.Value, splineEnd.Value, curveBulge, curveApexT, bendNodeStart, bendNodeEnd,
                PendingIgnores(anchorSnap, splineStart.Value, dragEndSnap, splineEnd.Value));
            if (bendNodeStart && anchorSnap.Value.isEndpoint)
                AddNodeGhost(splineStart.Value);
            if (bendNodeEnd && dragEndSnap.Value.isEndpoint)
                AddNodeGhost(splineEnd.Value);
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

    // A preview with only blocked (red) specs commits nothing.
    bool HasPlaceableSpecs()
    {
        foreach (SplineWall.SectionSpec spec in splineSpecs)
            if (!spec.blocked)
                return true;
        return false;
    }

    // Commits exactly what the ghost shows: the preview's own curve key
    // and section specs — straight drags, curves, and stub walls all
    // share this one commit path. Blocked specs ride along; Create skips
    // and disposes them.
    void CommitSpline()
    {
        if (HasPlaceableSpecs() && lastPreviewKey.HasValue)
        {
            (Vector3 s, Vector3 e, float bulge, float apexT, float height, float topY, bool nodeStart, bool nodeEnd) = lastPreviewKey.Value;
            Vector3 control = ControlPointFor(s, e, bulge, apexT);
            SplineWall.Create(splineParent, s, control, e,
                height, gridSize, BaseHeight, wallMaterial, splineSpecs,
                gridSize, baseStepSize, false, topY, nodeStart, nodeEnd);
            // Mesh ownership just moved to the wall's sections — clear the
            // preview list so CancelCurve doesn't destroy them.
            previewMeshes.Clear();
            // Snapped ends become nodes: every wall ending on the point
            // joins, hosts retract their caps to it, and the node's post
            // carves the corner. A flank-snapped end passes its host in
            // as a flank member (the T-joint — host geometry untouched).
            if (nodeStart)
                WallNode.Connect(splineParent, s, gridSize * 0.5f, wallMaterial,
                    anchorSnap.HasValue && anchorSnap.Value.isFlank ? anchorSnap.Value.wall : null);
            if (nodeEnd)
                WallNode.Connect(splineParent, e, gridSize * 0.5f, wallMaterial,
                    dragEndSnap.HasValue && dragEndSnap.Value.isFlank ? dragEndSnap.Value.wall : null);
        }
        CancelCurve();
    }

    void CommitCourse(SplineWall below)
    {
        if (HasPlaceableSpecs())
        {
            SplineWall.Create(splineParent, below.curveStart, below.curveControl, below.curveEnd,
                CurrentWallHeight, below.thickness, BaseHeight, wallMaterial, splineSpecs,
                gridSize, baseStepSize, true);
            previewMeshes.Clear();
            // A course over a node-ended wall joins the node, so the post
            // rises with the corner (the copied sections already cap at
            // the point).
            if (below.nodeAtStart)
                WallNode.Connect(splineParent, below.curveStart, gridSize * 0.5f, wallMaterial);
            if (below.nodeAtEnd)
                WallNode.Connect(splineParent, below.curveEnd, gridSize * 0.5f, wallMaterial);
        }
        CancelCurve();
    }

    void CancelCurve()
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        lastPreviewKey = null;
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
    // course-stacking targets, existing wall endpoints chain, a wall's
    // flank resolves to the cell beside the face being pointed at, terrain
    // resolves to the cell under the hit point, and Alt returns the raw
    // surface point.
    bool TryGetCurveTarget(Ray ray, bool freePlace, out Vector3 point, out Vector3 rawSurface, out SplineWallSection courseTarget)
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

        SplineWallSection section = first.collider.GetComponent<SplineWallSection>();
        if (section != null && first.normal.y > 0.7f)
        {
            courseTarget = section;
            point = rawSurface;
            return true;
        }

        // End snapping: wall ends outrank everything nearby, and the point
        // locks EXACTLY to one of three spots per end — the endpoint
        // itself (a corner-node joint) or one of the two flank points
        // beside it — the joints a wall layout is made of.
        if (TryEndSnap(rawSurface, null, out EndSnap endSnap))
        {
            hoverSnap = endSnap;
            hoverSnapYaw = endSnap.stubYaw;
            if (endSnap.wall != null)
                hoverSnapHeight = SnapHeightOf(endSnap.wall, endSnap.point);
            point = endSnap.point;
            return true;
        }

        // A wall's face: the anchor lands ON the face point, on whichever
        // side of the wall the cursor is — clicking plants a T-node there
        // and the wall draws away from that face at any angle (the commit
        // makes the host a flank member; the wedge rule fills the
        // shoulder). This replaces the old lay-alongside gesture — a
        // parallel run against a face is the Offset tool's job.
        if (TryFlankSnap(rawSurface, null, out EndSnap flankSnap))
        {
            hoverSnap = flankSnap;
            hoverSnapYaw = flankSnap.stubYaw;
            if (flankSnap.wall != null)
                hoverSnapHeight = SnapHeightOf(flankSnap.wall, flankSnap.point);
            point = flankSnap.point;
            return true;
        }

        // Terrain (or anything else that isn't a wall): the exact point
        // under the cursor — placement is free.
        point = rawSurface;
        return true;
    }

    // The nearest exact end-snap candidate within endpointSnapRadius of
    // the raw point, across every wall end: the endpoint itself (a
    // corner-node joint) and the two flank points beside it (each exactly
    // on a face plane, so snapped stubs fuse flush). The old cap
    // candidate is gone — with corner nodes, continuing past an end is
    // just an endpoint snap drawn onward. Candidates near `exclude` (the
    // drag anchor) are skipped so a drag can't snap its far end back
    // onto its own start.
    bool TryEndSnap(Vector3 raw, Vector3? exclude, out EndSnap snap)
    {
        snap = default;
        float bestSq = endpointSnapRadius * endpointSnapRadius;
        bool found = false;
        foreach (SplineWall wall in SplineWall.All)
        {
            for (int endIndex = 0; endIndex < 2; endIndex++)
            {
                bool atStart = endIndex == 0;
                Vector3 endPos = wall.EndPosition(atStart);
                endPos.y = 0f;
                Vector3 outDir = wall.EndOutwardDir(atStart);
                Vector3 side = new Vector3(-outDir.z, 0f, outDir.x);
                float halfThick = wall.thickness * 0.5f;
                Vector3[] candidates =
                {
                    endPos,
                    endPos + side * halfThick,
                    endPos - side * halfThick,
                };
                Vector3[] candidateDirs = { outDir, side, -side };
                for (int ci = 0; ci < candidates.Length; ci++)
                {
                    Vector3 candidate = candidates[ci];
                    if (exclude.HasValue && FlatDistance(candidate, exclude.Value) < lengthStep)
                        continue;
                    float dx = candidate.x - raw.x;
                    float dz = candidate.z - raw.z;
                    float sq = dx * dx + dz * dz;
                    if (sq >= bestSq)
                        continue;
                    bestSq = sq;
                    found = true;
                    snap = new EndSnap
                    {
                        wall = wall,
                        point = candidate,
                        backBearing = Mathf.Atan2(-outDir.z, -outDir.x) * Mathf.Rad2Deg,
                        stubYaw = Mathf.Atan2(-outDir.z, outDir.x) * Mathf.Rad2Deg + 90f,
                        outDir = candidateDirs[ci],
                        isEndpoint = ci == 0,
                    };
                }
            }
        }
        return found;
    }

    // Mid-wall flank termination snap: the drag's far end lands ON a
    // host's face, ending the wall there as a T-joint node at ANY angle —
    // the host joins the node as a flank member and the wedge rule fills
    // the shoulder (nothing at 90°, an arc at shallow angles). `from` is
    // the drag anchor: it picks which face of the host to land on, and
    // candidates near it are skipped like TryEndSnap's. The host's end
    // zones are left to end snapping.
    bool TryFlankSnap(Vector3 raw, Vector3? from, out EndSnap snap)
    {
        snap = default;
        float bestSq = endpointSnapRadius * endpointSnapRadius;
        bool found = false;
        foreach (SplineWall wall in SplineWall.All)
        {
            float bestT = 0f, bestCSq = float.MaxValue;
            for (int i = 0; i <= 64; i++)
            {
                float t = i / 64f;
                Vector3 q = SplineWall.Evaluate(wall.curveStart, wall.curveControl, wall.curveEnd, t);
                float dx = q.x - raw.x, dz = q.z - raw.z;
                float sq = dx * dx + dz * dz;
                if (sq < bestCSq) { bestCSq = sq; bestT = t; }
            }
            float halfThick = wall.thickness * 0.5f;
            if (bestCSq > (halfThick + endpointSnapRadius) * (halfThick + endpointSnapRadius))
                continue;

            Vector3 c = SplineWall.Evaluate(wall.curveStart, wall.curveControl, wall.curveEnd, bestT);
            Vector3 side = wall.SideOf(from ?? raw) * FlankNormalAt(wall, bestT);
            Vector3 facePoint = new Vector3(c.x + side.x * halfThick, 0f, c.z + side.z * halfThick);
            if (FlatDistance(facePoint, wall.EndPosition(true)) < 0.9f
                || FlatDistance(facePoint, wall.EndPosition(false)) < 0.9f)
                continue;
            if (from.HasValue && FlatDistance(facePoint, from.Value) < lengthStep)
                continue;
            float dxf = facePoint.x - raw.x, dzf = facePoint.z - raw.z;
            float sqf = dxf * dxf + dzf * dzf;
            if (sqf >= bestSq)
                continue;
            bestSq = sqf;
            found = true;
            snap = new EndSnap
            {
                wall = wall,
                point = facePoint,
                backBearing = Mathf.Atan2(side.z, side.x) * Mathf.Rad2Deg + 180f,
                // Maps to a stub chord running ALONG side — perpendicular
                // out of the face, the direction a wall leaves a T flush.
                stubYaw = Mathf.Atan2(-side.z, side.x) * Mathf.Rad2Deg,
                outDir = side,
                isEndpoint = false,
                isFlank = true,
            };
        }
        return found;
    }

    // The host's left-hand flank normal at t, flattened — SideOf's sign
    // convention picks which face by multiplying this by ±1.
    static Vector3 FlankNormalAt(SplineWall wall, float t)
    {
        Vector3 a = SplineWall.Evaluate(wall.curveStart, wall.curveControl, wall.curveEnd, Mathf.Clamp01(t - 0.01f));
        Vector3 b = SplineWall.Evaluate(wall.curveStart, wall.curveControl, wall.curveEnd, Mathf.Clamp01(t + 0.01f));
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

    void BuildSplinePreview(Vector3 start, Vector3 end, float bulge, float apexT,
        bool nodeStart = false, bool nodeEnd = false, List<SplineWall> ignoreWalls = null)
    {
        float height = EffectiveWallHeight;
        float topY = LockedTopFor(start);
        var key = (start, end, bulge, apexT, height, topY, nodeStart, nodeEnd);
        if (lastPreviewKey.HasValue && lastPreviewKey.Value == key)
            return;
        lastPreviewKey = key;
        lastCourseKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        Vector3 control = ControlPointFor(start, end, bulge, apexT);
        splineSpecs.AddRange(SplineWall.BuildSpecs(start, control, end,
            height, gridSize, gridSize, baseStepSize, BaseHeight,
            int.MaxValue, true, topY, nodeStart, nodeEnd, ignoreWalls));
        foreach (SplineWall.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
    }

    // The walls the pending drag is joining at its snapped ends — the set
    // spec building stands the junction machinery down for: the snap
    // hosts themselves plus every existing member of nodes at the points.
    List<SplineWall> PendingIgnores(EndSnap? startSnap, Vector3 start, EndSnap? endSnap, Vector3 end)
    {
        List<SplineWall> ignore = null;
        void Add(SplineWall w)
        {
            if (w == null || (ignore != null && ignore.Contains(w)))
                return;
            (ignore ??= new List<SplineWall>()).Add(w);
        }
        void AddEnd(EndSnap? snap, Vector3 p, Vector3 other)
        {
            if (!snap.HasValue || !(snap.Value.isEndpoint || snap.Value.isFlank))
                return;
            // A flank host only stands down for a wall that leaves on its
            // snapped face's side. A drag crossing to the far side is an
            // intersection — no stand-down, so the machinery refuses it
            // red instead of letting the wall run through its own host.
            if (snap.Value.isFlank)
            {
                Vector3 leave = other - p;
                leave.y = 0f;
                if (leave.sqrMagnitude > 0.0001f
                    && Vector3.Dot(leave, snap.Value.outDir) < -0.001f)
                    return;
            }
            Add(snap.Value.wall);
            WallNode node = WallNode.FindAt(p);
            if (node != null)
                foreach (SplineWall w in node.MemberWalls)
                    Add(w);
        }
        AddEnd(startSnap, start, end);
        AddEnd(endSnap, end, start);
        return ignore;
    }

    void BuildCoursePreview(SplineWall wall)
    {
        var key = (wall, CurrentWallHeight);
        if (lastCourseKey.HasValue && lastCourseKey.Value == key)
            return;
        lastCourseKey = key;
        lastPreviewKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        splineSpecs.AddRange(wall.BuildCourseSpecs(CurrentWallHeight, BaseHeight, true));
        foreach (SplineWall.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
    }

    void ClearPreviewMeshes()
    {
        foreach (Mesh mesh in previewMeshes)
            Destroy(mesh);
        previewMeshes.Clear();
    }

    // Blocked pieces show red instead of vanishing — the refusal is
    // visible in place, and release simply doesn't lay those pieces.
    void ShowSplineGhosts()
    {
        for (int i = 0; i < splineSpecs.Count; i++)
        {
            SplineWall.SectionSpec spec = splineSpecs[i];
            GameObject ghost = GetPooled(ghostPool, i, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            SetGhostTint(ghost, spec.blocked ? deletePreviewColor : (snapTint ? snapColor : previewColor));
            ghost.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));
            ghost.transform.localScale = Vector3.one;
        }
        for (int i = splineSpecs.Count; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // Pooled ghosts change tint per use (blocked pieces run red); the
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

    // A full box ghost marking the curve tool's start cell — the sweep's
    // end extension makes the committed wall genuinely cover this cell.
    void ShowStartMarker(Vector3 point)
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        lastPreviewKey = null;
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

    // Queues a gold corner-post ghost at an endpoint-snapped end for this
    // frame; UpdateNodeGhostVisuals renders the queue at the end of
    // Update.
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
    // normal wall: it steps its own terrain and cuts its own junctions.
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
            if (!overUI && TryGetSectionUnderCursor(ray, out SplineWallSection hovered))
            {
                ShowWallHighlight(hovered.wall);
                if (mouse.leftButton.wasPressedThisFrame)
                    offsetSource = hovered.wall;
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

        SplineWall.OffsetCurve(offsetSource.curveStart, offsetSource.curveControl, offsetSource.curveEnd,
            offsetSide * offsetDistance, out Vector3 s2, out Vector3 c2, out Vector3 e2);
        BuildOffsetPreview(s2, c2, e2);
        ShowSplineGhosts();
        guideCenter = (s2 + e2) * 0.5f;
        guideBearing = Mathf.Atan2(e2.z - s2.z, e2.x - s2.x) * Mathf.Rad2Deg;

        if (mouse.leftButton.wasPressedThisFrame && HasPlaceableSpecs())
        {
            SplineWall.Create(splineParent, s2, c2, e2, CurrentWallHeight, gridSize, BaseHeight,
                wallMaterial, splineSpecs, gridSize, baseStepSize, false, LockedTopFor(s2));
            previewMeshes.Clear();
            splineSpecs.Clear();
            lastOffsetKey = null;
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
        lastCourseKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        splineSpecs.AddRange(SplineWall.BuildSpecs(s, c, e,
            CurrentWallHeight, gridSize, gridSize, baseStepSize, BaseHeight,
            int.MaxValue, true, topY));
        foreach (SplineWall.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
    }

    // Shells the hovered wall in preview color so the offset tool shows
    // what a click would take as its source.
    void ShowWallHighlight(SplineWall wall)
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        lastPreviewKey = null;
        lastCourseKey = null;
        lastOffsetKey = null;

        int count = 0;
        foreach (SplineWallSection section in wall.GetComponentsInChildren<SplineWallSection>())
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
    // along the section's own wall — wall membership, not grid lines,
    // defines the run — and nothing is destroyed until the button is
    // released: exactly what's red when the button comes up is removed.
    void UpdateDelete(Mouse mouse, Ray ray, bool overUI)
    {
        if (deleteAnchor != null)
        {
            // Re-resolved only while the cursor is on the anchor's own
            // wall; off-target frames (HUD, sky, other walls) keep the
            // last preview so release still commits what the overlay
            // showed, matching build.
            if (!overUI && TryGetSectionUnderCursor(ray, out SplineWallSection under)
                && under.wall == deleteAnchor.wall)
                MarkWallRangeDoomed(deleteAnchor, under);
        }
        else
        {
            doomedSections.Clear();
            if (!overUI && TryGetSectionUnderCursor(ray, out SplineWallSection hovered))
            {
                doomedSections.Add(hovered.transform);
                if (mouse.leftButton.wasPressedThisFrame)
                    deleteAnchor = hovered;
            }
        }

        ShowDeleteOverlays(doomedSections);

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            if (deleteAnchor != null)
            {
                // Connections are modeled: gather the affected walls and
                // everything linked to them BEFORE destroying, then let
                // the survivors re-resolve — a neighbour that buried its
                // end into a deleted wall retracts instead of leaving a
                // stub hanging in the air. Destruction is immediate so
                // the survivors' probes see the cleared space.
                var affected = new HashSet<SplineWall>();
                foreach (Transform doomed in doomedSections)
                {
                    SplineWallSection section = doomed.GetComponent<SplineWallSection>();
                    if (section != null && section.wall != null)
                        affected.Add(section.wall);
                }
                var candidates = new HashSet<SplineWall>(affected);
                foreach (SplineWall wall in affected)
                    candidates.UnionWith(wall.Links);

                foreach (Transform doomed in doomedSections)
                    DestroyImmediate(doomed.gameObject);

                // Walls losing every remaining section lose the wall
                // object too; survivors that lost some are gapped and
                // keep their remaining geometry as-is from here on.
                foreach (SplineWall wall in affected)
                {
                    if (wall == null)
                        continue;
                    if (wall.GetComponentsInChildren<SplineWallSection>().Length == 0)
                        DestroyImmediate(wall.gameObject);
                    else
                        wall.hasGaps = true;
                }
                Physics.SyncTransforms();

                foreach (SplineWall wall in candidates)
                {
                    if (wall == null)
                        continue;
                    wall.RebuildSections();
                    wall.ResolveLinks();
                }
                doomedSections.Clear();
                HideDeleteOverlays();
            }
            deleteAnchor = null;
        }
    }

    // Delete run: every section of the wall between the two drag ends, by
    // the wall's own ordering — terrain height plays no part.
    void MarkWallRangeDoomed(SplineWallSection a, SplineWallSection b)
    {
        doomedSections.Clear();
        int min = Mathf.Min(a.index, b.index);
        int max = Mathf.Max(a.index, b.index);
        foreach (SplineWallSection section in a.wall.GetComponentsInChildren<SplineWallSection>())
        {
            if (section.index >= min && section.index <= max)
                doomedSections.Add(section.transform);
        }
    }

    // Deliberately precise (unlike build targeting) — deletion should only
    // ever affect the exact wall section under the cursor, never something
    // merely nearby.
    bool TryGetSectionUnderCursor(Ray ray, out SplineWallSection section)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;
            section = hit.collider.GetComponent<SplineWallSection>();
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
            foreach (SplineWall wall in SplineWall.All)
            {
                Vector3 face = wall.ClosestFacePoint(center, out float dist);
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

    // The corner readout: while the pending wall hangs off a snapped wall
    // end, an arc sweeps along the ground from the host wall's run to the
    // ghost's, and OnGUI labels it with the degrees between them — 90 is
    // a square corner, 180 a straight continuation.
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
