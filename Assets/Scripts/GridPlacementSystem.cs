using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public class GridPlacementSystem : MonoBehaviour
{
    [Header("Placement")]
    public GameObject piecePrefab;
    public float gridSize = 1f;
    // Half the prefab's natural (×1) height. The prefab pivot is centered,
    // not base-anchored, so a piece's center sits half its height above its
    // base; this is also the anchor the height multiplier scales from.
    public float placementYOffset = 1.75f;
    // Ground-course bases snap DOWN to this increment, so neighbouring
    // pieces on gentle slopes share a base (long level top runs) and real
    // drops read as clean, uniform foundation steps instead of a ragged
    // edge shifting at every cell.
    public float baseStepSize = 0.5f;

    [Header("Height")]
    // Wall height is stepped, not continuous — snapped heights are what let
    // separately-built structures actually line up.
    public float minHeightMultiplier = 0.25f;
    public float maxHeightMultiplier = 3f;
    public float heightMultiplierStep = 0.25f;

    [Header("Curve")]
    // Curvature moves in fixed steps so two separately-built curves over
    // the same chord can be made genuinely identical.
    public float curveBulgeStep = 0.5f;
    // Existing wall endpoints within this range of the cursor grab a new
    // wall's endpoint, so walls chain into networks; otherwise endpoints
    // snap to the grid. Hold Alt to place freely.
    public float endpointSnapRadius = 0.75f;

    [Header("Preview")]
    public Color previewColor = new Color(0.3f, 0.9f, 1f, 1f);
    public Color deletePreviewColor = new Color(1f, 0.3f, 0.25f, 1f);

    public enum ToolMode { Build, Delete }
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

    // How many pieces the active placement preview will lay down — 0 when
    // idle, so the HUD counter can show/hide off this alone.
    public int ActiveDragCount
    {
        get
        {
            if (Mode != ToolMode.Build)
                return 0;
            if (Shape == BuildShape.Curved)
                return splineSpecs.Count;
            return dragStartPosition.HasValue ? lastGhostCells.Count : 0;
        }
    }

    // Slightly larger than the piece it covers so the overlay shell draws
    // over the piece's own surface instead of z-fighting with it.
    const float DeleteOverlayScale = 1.02f;

    private readonly List<GameObject> ghostPool = new List<GameObject>();
    private readonly List<GameObject> deletePool = new List<GameObject>();
    private readonly List<Transform> doomedPieces = new List<Transform>();
    private readonly List<SplineWall.SectionSpec> splineSpecs = new List<SplineWall.SectionSpec>();
    private readonly List<Mesh> previewMeshes = new List<Mesh>();
    private (Vector3 start, Vector3 end, float bulge, float apexT, float height)? lastPreviewKey;
    private (SplineWall wall, float height)? lastCourseKey;
    private Mesh prefabMesh;
    private Material wallMaterial;
    private Transform splineParent;
    private List<Vector3> lastGhostCells = new List<Vector3>();
    private Transform placedParent;
    private float currentYRotation;
    private float lastCourseYaw;
    private float heightMultiplier = 1f;
    private float curveBulge;
    private float curveApexT = 0.5f;
    private Vector3? dragStartPosition;
    private Vector3? splineStart;
    private Vector3? splineEnd;
    private Transform deleteAnchorPiece;
    private Camera cam;

    // Each commit takes the next id, so every placed run — straight or
    // curved — is identifiable as one wall afterwards.
    private static int nextWallGroupId = 1;

    void Start()
    {
        cam = Camera.main;
        placedParent = new GameObject("PlacedWalls").transform;
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

        ClaimingScrollWheel = Mode == ToolMode.Build && Shape == BuildShape.Curved && splineEnd.HasValue;

        if (Mode == ToolMode.Delete)
            UpdateDelete(mouse, ray, overUI);
        else
            UpdateBuild(mouse, keyboard, ray, overUI);
    }

    // Both tools share the left mouse button, so switching abandons any
    // in-progress drag rather than letting it commit under the other tool's
    // rules.
    public void SetMode(ToolMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        dragStartPosition = null;
        deleteAnchorPiece = null;
        doomedPieces.Clear();
        CancelCurve();
        HideGhosts();
        HideDeleteOverlays();
    }

    public void SetShape(BuildShape shape)
    {
        if (Shape == shape)
            return;
        Shape = shape;
        dragStartPosition = null;
        CancelCurve();
        HideGhosts();
    }

    void UpdateBuild(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        HandleHeightScroll(mouse, keyboard);

        if (Shape == BuildShape.Curved)
            UpdateCurvedBuild(mouse, keyboard, ray, overUI);
        else
            UpdateStraightBuild(mouse, keyboard, ray, overUI);
    }

    void UpdateStraightBuild(Mouse mouse, Keyboard keyboard, Ray ray, bool overUI)
    {
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            currentYRotation += 90f;

        Vector3 rawSnapped = default;
        bool hasTarget = !overUI && TryGetPlacementCell(ray, out rawSnapped);

        if (hasTarget)
        {
            if (mouse.leftButton.wasPressedThisFrame)
                dragStartPosition = rawSnapped;

            // Nothing is placed while dragging — only a ghost line from the
            // anchor to the cursor's current 45°-locked cell. Wandering past a
            // new 45° direction just re-previews the whole run from the same
            // anchor instead of leaving stray pieces behind from the old one.
            if (dragStartPosition.HasValue)
            {
                // One drag lays one course. A ground course (anchor on open
                // ground) follows the terrain — each empty cell builds from
                // its own ground, the way a real curtain wall steps along a
                // slope. A stacking course (anchor on existing pieces)
                // stays dead level instead: only cells whose column top
                // lines up exactly may continue it. Anything else — cells
                // occupied at the wrong elevation — drops out of the line.
                Vector3 anchorCell = dragStartPosition.Value;
                bool anchorOnGround = !TryGetHighestPieceTop(anchorCell.x, anchorCell.z, out float anchorStackTop);
                float anchorBase = anchorOnGround
                    ? SampleCellGroundY(anchorCell.x, anchorCell.z)
                    : anchorStackTop;

                // The run's own direction, for the junction angle test —
                // a single-cell drag falls back to the piece's rotation.
                Vector3 dragDelta = rawSnapped - anchorCell;
                float courseYaw = dragDelta.sqrMagnitude > 0.01f
                    ? Mathf.Atan2(-dragDelta.z, dragDelta.x) * Mathf.Rad2Deg
                    : currentYRotation;
                lastCourseYaw = courseYaw;

                lastGhostCells = new List<Vector3>();
                foreach (Vector3 lineCell in ComputeDragLineCells(anchorCell, rawSnapped))
                {
                    if (TryGetCourseBaseY(lineCell.x, lineCell.z, anchorOnGround, anchorBase, courseYaw, out float cellBase))
                        lastGhostCells.Add(new Vector3(lineCell.x, cellBase + CurrentWallHeight / 2f, lineCell.z));
                }
            }
            else
            {
                // Hover height reflects whatever's already stacked in the
                // cell, so the ghost shows exactly where a click would land.
                float baseY = GetColumnTopY(rawSnapped.x, rawSnapped.z);
                lastGhostCells = new List<Vector3>
                {
                    new Vector3(rawSnapped.x, baseY + CurrentWallHeight / 2f, rawSnapped.z)
                };
            }

            ShowGhosts(lastGhostCells, currentYRotation);
        }
        else if (!dragStartPosition.HasValue)
        {
            // Only hide when idle. Mid-drag, an off-target cursor (HUD, sky)
            // keeps the last ghost line visible — it's exactly what release
            // will commit, so it shouldn't vanish while the button is held.
            HideGhosts();
        }

        // Checked unconditionally (not just when hasTarget) so releasing the
        // button while the cursor has wandered off target still commits the
        // last line the ghost showed, instead of getting stuck dragging.
        if (mouse.leftButton.wasReleasedThisFrame && dragStartPosition.HasValue)
        {
            int groupId = nextWallGroupId++;
            for (int i = 0; i < lastGhostCells.Count; i++)
            {
                PlacedPiece piece = PlacePiece(lastGhostCells[i], currentYRotation);
                piece.groupId = groupId;
                piece.groupIndex = i;
                piece.runYaw = lastCourseYaw;
            }
            dragStartPosition = null;
            HideGhosts();
        }
    }

    // Ctrl+scroll steps the wall height for subsequent placements and the
    // live ghost line. FreeFlyCamera stands down from scroll-dollying while
    // Ctrl is held, so the wheel only means one thing at a time.
    void HandleHeightScroll(Mouse mouse, Keyboard keyboard)
    {
        if (keyboard == null || (!keyboard.leftCtrlKey.isPressed && !keyboard.rightCtrlKey.isPressed))
            return;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f)
            return;

        heightMultiplier = Mathf.Clamp(
            heightMultiplier + Mathf.Sign(scroll) * heightMultiplierStep,
            minHeightMultiplier, maxHeightMultiplier);
    }

    // Spline wall placement — walls are first-class objects, not grid
    // cells. Three clicks: start point, end point (both snapping to
    // existing wall endpoints first, then the grid; Alt places freely),
    // then bend — plain scroll steps the bulge while the cursor drags the
    // apex along the chord — and a final click commits. Hovering an
    // existing wall instead previews a full course along it at the current
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
        SplineWallSection courseTarget = null;
        bool hasPoint = !overUI && TryGetCurveTarget(ray, freePlace, out point, out rawSurface, out courseTarget);

        if (!splineStart.HasValue)
        {
            if (hasPoint && courseTarget != null)
            {
                BuildCoursePreview(courseTarget.wall);
                ShowSplineGhosts();
                if (mouse.leftButton.wasPressedThisFrame)
                    CommitCourse(courseTarget.wall);
            }
            else if (hasPoint)
            {
                ShowStartMarker(point);
                if (mouse.leftButton.wasPressedThisFrame)
                    splineStart = point;
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
                BuildSplinePreview(splineStart.Value, point, 0f, 0.5f);
                ShowSplineGhosts();
                Vector3 flat = point - splineStart.Value;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.25f
                    && (mouse.leftButton.wasPressedThisFrame || mouse.leftButton.wasReleasedThisFrame))
                    splineEnd = point;
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

            BuildSplinePreview(splineStart.Value, splineEnd.Value, curveBulge, curveApexT);
            ShowSplineGhosts();

            if (mouse.leftButton.wasPressedThisFrame)
                CommitSpline();
        }
    }

    void CommitSpline()
    {
        if (splineSpecs.Count > 0 && splineStart.HasValue && splineEnd.HasValue)
        {
            Vector3 control = ControlPointFor(splineStart.Value, splineEnd.Value, curveBulge, curveApexT);
            SplineWall.Create(splineParent, splineStart.Value, control, splineEnd.Value,
                CurrentWallHeight, gridSize, BaseHeight, wallMaterial, splineSpecs);
            // Mesh ownership just moved to the wall's sections — clear the
            // preview list so CancelCurve doesn't destroy them.
            previewMeshes.Clear();
        }
        CancelCurve();
    }

    void CommitCourse(SplineWall below)
    {
        if (splineSpecs.Count > 0)
        {
            SplineWall.Create(splineParent, below.curveStart, below.curveControl, below.curveEnd,
                CurrentWallHeight, below.thickness, BaseHeight, wallMaterial, splineSpecs);
            previewMeshes.Clear();
        }
        CancelCurve();
    }

    void CancelCurve()
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        lastPreviewKey = null;
        lastCourseKey = null;
        splineStart = null;
        splineEnd = null;
        curveBulge = 0f;
        curveApexT = 0.5f;
        HideGhosts();
    }

    // Resolves the cursor for the curve tool. Pointing at a wall's flank
    // OR at the cell beside it resolves to that neighbouring cell — the
    // same assist helpers the grid tool uses, so "next to that wall" is
    // one gesture and the wall's own end extension closes the joint. Wall
    // top faces are course-stacking targets, existing spline endpoints
    // chain, and Alt returns the raw surface point.
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

        // Chaining: existing spline endpoints outrank everything nearby.
        foreach (SplineWall wall in SplineWall.All)
        {
            if (FlatDistance(rawSurface, wall.curveStart) <= endpointSnapRadius)
            {
                point = new Vector3(wall.curveStart.x, 0f, wall.curveStart.z);
                return true;
            }
            if (FlatDistance(rawSurface, wall.curveEnd) <= endpointSnapRadius)
            {
                point = new Vector3(wall.curveEnd.x, 0f, wall.curveEnd.z);
                return true;
            }
        }

        if (section != null)
        {
            // A spline wall's flank: the cell beside the face being
            // pointed at, mirroring the grid pieces' assist boxes.
            point = SnapToGrid(first.point + first.normal * (gridSize * 0.5f));
            return true;
        }

        // Terrain, grid pieces, and their assist boxes: the shared grid
        // helper already resolves all of these to the intended cell.
        if (TryGetPlacementCell(ray, out Vector3 cell))
        {
            point = cell;
            return true;
        }

        point = SnapToGrid(rawSurface);
        return true;
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

    void BuildSplinePreview(Vector3 start, Vector3 end, float bulge, float apexT)
    {
        var key = (start, end, bulge, apexT, CurrentWallHeight);
        if (lastPreviewKey.HasValue && lastPreviewKey.Value == key)
            return;
        lastPreviewKey = key;
        lastCourseKey = null;

        ClearPreviewMeshes();
        splineSpecs.Clear();
        Vector3 control = ControlPointFor(start, end, bulge, apexT);
        splineSpecs.AddRange(SplineWall.BuildSpecs(start, control, end,
            CurrentWallHeight, gridSize, gridSize, baseStepSize, BaseHeight));
        foreach (SplineWall.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
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
        splineSpecs.AddRange(wall.BuildCourseSpecs(CurrentWallHeight, BaseHeight));
        foreach (SplineWall.SectionSpec spec in splineSpecs)
            previewMeshes.Add(spec.mesh);
    }

    void ClearPreviewMeshes()
    {
        foreach (Mesh mesh in previewMeshes)
            Destroy(mesh);
        previewMeshes.Clear();
    }

    void ShowSplineGhosts()
    {
        for (int i = 0; i < splineSpecs.Count; i++)
        {
            SplineWall.SectionSpec spec = splineSpecs[i];
            GameObject ghost = GetPooled(ghostPool, i, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, spec.mesh);
            ghost.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(0f, spec.yaw, 0f));
            ghost.transform.localScale = Vector3.one;
        }
        for (int i = splineSpecs.Count; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // A full box ghost marking the start cell — honest again now that the
    // sweep's end extension makes the wall genuinely cover this cell.
    void ShowStartMarker(Vector3 point)
    {
        ClearPreviewMeshes();
        splineSpecs.Clear();
        lastPreviewKey = null;
        lastCourseKey = null;

        float ground = SampleCellGroundY(point.x, point.z);
        GameObject ghost = GetPooled(ghostPool, 0, previewColor, "PlacementPreview");
        ghost.SetActive(true);
        SetGhostMesh(ghost, null);
        Vector3 scale = piecePrefab.transform.localScale;
        scale.y *= heightMultiplier;
        ghost.transform.SetPositionAndRotation(
            new Vector3(point.x, ground + CurrentWallHeight / 2f, point.z), Quaternion.identity);
        ghost.transform.localScale = scale;
        for (int i = 1; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    // Shared course rule for straight grid runs: a ground-anchored course
    // follows the terrain through empty cells; anything else must line up
    // exactly with the anchor's build elevation. Cells inside spline walls
    // are blocked via physics, standing in for the occupancy those walls
    // no longer register on the grid.
    bool TryGetCourseBaseY(float x, float z, bool anchorOnGround, float anchorBase, float courseYaw, out float baseY)
    {
        bool cellOnGround = !TryGetHighestPieceTop(x, z, out float stackTop);
        baseY = cellOnGround ? SampleCellGroundY(x, z) : stackTop;
        bool aligned = (anchorOnGround && cellOnGround) || Mathf.Approximately(baseY, anchorBase);
        return aligned && !SplineWallBlocksSpan(x, z, baseY, baseY + CurrentWallHeight, courseYaw);
    }

    bool SplineWallBlocksSpan(float x, float z, float bottom, float top, float courseYaw)
    {
        Vector3 center = new Vector3(x, (bottom + top) * 0.5f, z);
        Vector3 halfExtents = new Vector3(gridSize * 0.45f, (top - bottom) * 0.45f, gridSize * 0.45f);
        foreach (Collider c in Physics.OverlapBox(center, halfExtents))
        {
            if (c.isTrigger || c.GetComponent<SplineWallSection>() == null)
                continue;

            // Same junction rule spline walls use: a grid course crossing a
            // spline wall at a real angle pushes through and connects; only
            // a near-parallel run through the same space is blocked.
            float delta = Mathf.Abs(Mathf.DeltaAngle(courseYaw, c.transform.eulerAngles.y));
            delta = Mathf.Min(delta, 180f - delta);
            if (delta < 25f)
                return true;
        }
        return false;
    }

    // Pooled ghosts flip between the shared box mesh and per-slice curve
    // meshes, so the mesh is (re)assigned on every show.
    void SetGhostMesh(GameObject ghost, Mesh mesh)
    {
        MeshFilter filter = ghost.GetComponent<MeshFilter>();
        if (filter != null)
            filter.sharedMesh = mesh != null ? mesh : prefabMesh;
    }

    // Deletion mirrors the build flow: hovering shows a red overlay on the
    // single piece the cursor is on, click-dragging extends that preview
    // along the same 45°-locked line build uses, and nothing is destroyed
    // until the button is released — exactly what's red when the button
    // comes up is what's removed.
    void UpdateDelete(Mouse mouse, Ray ray, bool overUI)
    {
        if (deleteAnchorPiece != null)
        {
            // Re-resolved only while there's a real target under the cursor;
            // off-target frames (HUD, sky) keep the last preview so release
            // still commits what the overlay showed, matching build.
            PlacedPiece anchor = deleteAnchorPiece.GetComponent<PlacedPiece>();
            SplineWallSection anchorSection = deleteAnchorPiece.GetComponent<SplineWallSection>();
            if (!overUI)
            {
                if (TryGetPlacedPieceUnderCursor(ray, out Transform under))
                {
                    SplineWallSection underSection = under.GetComponent<SplineWallSection>();
                    if (anchorSection != null)
                    {
                        // Spline anchors extend only along their own wall —
                        // wall membership, not grid lines, defines the run.
                        if (underSection != null && underSection.wall == anchorSection.wall)
                            MarkWallRangeDoomed(anchorSection, underSection);
                    }
                    else if (anchor != null && underSection == null)
                    {
                        PlacedPiece underPiece = under.GetComponent<PlacedPiece>();
                        // Dragging within the anchor's own column flips the
                        // run vertical: it climbs the stack instead of
                        // walking the grid.
                        if (SameColumn(under.position, deleteAnchorPiece.position))
                            MarkColumnDoomed(anchor, underPiece);
                        // Both ends on the same committed run: follow its
                        // own piece order instead of a straight grid line.
                        else if (underPiece != null && anchor.groupId != 0 && underPiece.groupId == anchor.groupId)
                            MarkGroupRangeDoomed(anchor, underPiece);
                        else
                            MarkLineDoomed(anchor, under.position);
                    }
                    // Mixed grid/spline drags keep the last preview.
                }
                // The cell fallback (assist boxes / ground) can't express a
                // height, so it only drives horizontal grid runs; a
                // same-column cell hit keeps the previous preview instead.
                else if (anchor != null && TryGetPlacementCell(ray, out Vector3 cellTarget)
                    && !SameColumn(cellTarget, deleteAnchorPiece.position))
                {
                    MarkLineDoomed(anchor, cellTarget);
                }
            }
        }
        else
        {
            doomedPieces.Clear();
            if (!overUI && TryGetPlacedPieceUnderCursor(ray, out Transform hovered))
            {
                doomedPieces.Add(hovered);
                if (mouse.leftButton.wasPressedThisFrame)
                    deleteAnchorPiece = hovered;
            }
        }

        ShowDeleteOverlays(doomedPieces);

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            if (deleteAnchorPiece != null)
            {
                // Spline walls losing every remaining section lose the
                // wall object too.
                var doomedPerWall = new Dictionary<SplineWall, int>();
                foreach (Transform piece in doomedPieces)
                {
                    SplineWallSection section = piece.GetComponent<SplineWallSection>();
                    if (section != null && section.wall != null)
                        doomedPerWall[section.wall] = doomedPerWall.TryGetValue(section.wall, out int n) ? n + 1 : 1;
                    Destroy(piece.gameObject);
                }
                foreach (KeyValuePair<SplineWall, int> pair in doomedPerWall)
                {
                    if (pair.Key.GetComponentsInChildren<SplineWallSection>().Length <= pair.Value)
                        Destroy(pair.Key.gameObject);
                }
                doomedPieces.Clear();
                HideDeleteOverlays();
            }
            deleteAnchorPiece = null;
        }
    }

    // Spline delete run: every section of the wall between the two drag
    // ends, by the wall's own ordering — terrain height plays no part.
    void MarkWallRangeDoomed(SplineWallSection a, SplineWallSection b)
    {
        doomedPieces.Clear();
        int min = Mathf.Min(a.index, b.index);
        int max = Mathf.Max(a.index, b.index);
        foreach (SplineWallSection section in a.wall.GetComponentsInChildren<SplineWallSection>())
        {
            if (section.index >= min && section.index <= max)
                doomedPieces.Add(section.transform);
        }
    }

    // Resolves the cell the cursor is targeting. Each placed piece carries
    // invisible trigger boxes over its four side cells and the cell above
    // it (see AddPlacementAssist), so a ray through that empty space — not
    // just one that hits a piece dead-on — resolves to the neighbouring
    // cell. Each assist box covers exactly one cell, so the box's own
    // center names the target cell directly; snapping the raw hit point
    // instead would let a hit on the box's outer face round into the next
    // cell over. Direct piece hits are nudged along the surface normal so a
    // side face resolves to the cell beside it and the top face to the
    // piece's own column (stacking picks the Y). Only when nothing is hit
    // at all does this fall back to the raw ground plane.
    bool TryGetPlacementCell(Ray ray, out Vector3 cell)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            // A real surface that isn't ours — the terrain — in front of
            // any remaining pieces: the ray lands there, so the cell under
            // that landing point is the target.
            if (!hit.transform.IsChildOf(placedParent))
            {
                cell = SnapToGrid(hit.point);
                return true;
            }

            if (hit.transform.name == "PlacementAssist")
            {
                // Entering an assist box through its top face means the ray
                // is dropping past this cell from above, headed for
                // something the player can actually see beyond it — the
                // floor of a slot between two walls, say. Without this,
                // walls flanking a slot cap it with an invisible ceiling at
                // wall height, and a tilted ray gets caught there and snaps
                // to a nearer cell than the one under the cursor. Side
                // entries are the assist's real job — a ray brushing past a
                // wall at wall height snaps to the cell it flew through —
                // and they keep claiming the target as before.
                if (hit.normal.y > 0.5f)
                    continue;
                cell = SnapToGrid(hit.collider.bounds.center);
                return true;
            }

            cell = SnapToGrid(hit.point + hit.normal * (gridSize * 0.5f));
            return true;
        }

        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        if (groundPlane.Raycast(ray, out float enter) && enter > 0f && enter <= 500f)
        {
            cell = SnapToGrid(ray.GetPoint(enter));
            return true;
        }

        cell = Vector3.zero;
        return false;
    }

    // Snaps to the center of whichever grid cell the point falls in, so a
    // placed piece's footprint lines up flush with the surrounding grid
    // lines instead of straddling a corner where four cells meet. XZ only —
    // vertical placement is span math, not grid math, so cells carry y = 0
    // until a caller resolves a real elevation.
    Vector3 SnapToGrid(Vector3 worldPoint)
    {
        float x = (Mathf.Floor(worldPoint.x / gridSize) + 0.5f) * gridSize;
        float z = (Mathf.Floor(worldPoint.z / gridSize) + 0.5f) * gridSize;
        return new Vector3(x, 0f, z);
    }

    // Walks the drag from start to target along the nearest 45° increment
    // (N/S/E/W + diagonals), returning every grid cell along that line —
    // matches how most wall-drawing tools keep a drag straight instead of
    // following raw input.
    List<Vector3> ComputeDragLineCells(Vector3 start, Vector3 target)
    {
        float gx = (target.x - start.x) / gridSize;
        float gz = (target.z - start.z) / gridSize;

        float angle = Mathf.Atan2(gz, gx) * Mathf.Rad2Deg;
        float snappedAngle = Mathf.Round(angle / 45f) * 45f;
        int dirX = Mathf.RoundToInt(Mathf.Cos(snappedAngle * Mathf.Deg2Rad));
        int dirZ = Mathf.RoundToInt(Mathf.Sin(snappedAngle * Mathf.Deg2Rad));

        int steps = 0;
        if (dirX != 0 || dirZ != 0)
            steps = Mathf.RoundToInt((gx * dirX + gz * dirZ) / (dirX * dirX + dirZ * dirZ));

        var cells = new List<Vector3>();
        for (int i = 0; i <= steps; i++)
        {
            Vector3 cell = start;
            cell.x += i * dirX * gridSize;
            cell.z += i * dirZ * gridSize;
            cells.Add(cell);
        }
        return cells;
    }

    // The elevation the next piece in this column builds up from: the
    // highest span top among pieces already there, or the ground itself
    // when the column is empty.
    float GetColumnTopY(float x, float z)
    {
        return TryGetHighestPieceTop(x, z, out float top) ? top : SampleCellGroundY(x, z);
    }

    bool TryGetHighestPieceTop(float x, float z, out float top)
    {
        top = float.MinValue;
        bool found = false;
        Vector3 column = new Vector3(x, 0f, z);
        foreach (Transform child in placedParent)
        {
            if (!SameColumn(child.position, column))
                continue;
            PlacedPiece piece = child.GetComponent<PlacedPiece>();
            if (piece == null)
                continue;
            found = true;
            if (piece.topY > top)
                top = piece.topY;
        }
        return found;
    }

    // Ground elevation for a cell: the lowest of its four corners, so a
    // wall on a slope embeds into the hillside like a foundation instead
    // of leaving a floating corner. Falls back to y = 0 with no terrain.
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

        // Snapping down keeps the piece embedded (never floating) while
        // giving nearby cells identical bases wherever the slope is gentle.
        if (baseStepSize > 0f)
            ground = Mathf.Floor(ground / baseStepSize) * baseStepSize;
        return ground;
    }

    static bool SameColumn(Vector3 a, Vector3 b)
    {
        return Mathf.Approximately(a.x, b.x) && Mathf.Approximately(a.z, b.z);
    }

    // Horizontal delete run: walks the same 45°-locked line build uses and,
    // in each cell, marks every piece whose vertical span overlaps the
    // anchor's span. With mixed heights there is no shared "layer" to match
    // — a tall anchor sweeps away the several short pieces beside it, and a
    // short anchor takes just the one tall piece its slice passes through.
    void MarkLineDoomed(PlacedPiece anchor, Vector3 target)
    {
        doomedPieces.Clear();
        foreach (Vector3 cell in ComputeDragLineCells(anchor.transform.position, target))
            AddOverlappingPiecesAt(cell.x, cell.z, anchor.bottomY, anchor.topY);
    }

    // Group delete run: both ends sit on the same committed wall, so mark
    // every piece between them along that wall's own order. Height plays
    // no part — group membership already proves they're one wall.
    void MarkGroupRangeDoomed(PlacedPiece anchor, PlacedPiece target)
    {
        doomedPieces.Clear();
        int min = Mathf.Min(anchor.groupIndex, target.groupIndex);
        int max = Mathf.Max(anchor.groupIndex, target.groupIndex);
        foreach (Transform child in placedParent)
        {
            PlacedPiece piece = child.GetComponent<PlacedPiece>();
            if (piece != null && piece.groupId == anchor.groupId
                && piece.groupIndex >= min && piece.groupIndex <= max)
                doomedPieces.Add(child);
        }
    }

    // Vertical delete run: every piece in the anchor's column whose span
    // overlaps the range from the anchor to the targeted piece, inclusive.
    void MarkColumnDoomed(PlacedPiece anchor, PlacedPiece target)
    {
        doomedPieces.Clear();
        float bottom = target != null ? Mathf.Min(anchor.bottomY, target.bottomY) : anchor.bottomY;
        float top = target != null ? Mathf.Max(anchor.topY, target.topY) : anchor.topY;
        AddOverlappingPiecesAt(anchor.transform.position.x, anchor.transform.position.z, bottom, top);
    }

    void AddOverlappingPiecesAt(float x, float z, float bottom, float top)
    {
        Vector3 column = new Vector3(x, 0f, z);
        foreach (Transform child in placedParent)
        {
            PlacedPiece piece = child.GetComponent<PlacedPiece>();
            if (piece != null && SameColumn(child.position, column)
                && piece.SpanOverlaps(bottom, top) && !doomedPieces.Contains(child))
                doomedPieces.Add(child);
        }
    }

    PlacedPiece PlacePiece(Vector3 cell, float yRotation, float lengthScale = 1f, Mesh customMesh = null)
    {
        // Base elevation is recomputed here too (not just for the ghost
        // preview) so placement is correct regardless of the Y passed in.
        float bottom = GetColumnTopY(cell.x, cell.z);
        float height = CurrentWallHeight;
        Vector3 position = new Vector3(cell.x, bottom + height / 2f, cell.z);

        GameObject placed = Instantiate(piecePrefab, position, Quaternion.Euler(0f, yRotation, 0f), placedParent);
        placed.name = piecePrefab.name;
        Vector3 scale = piecePrefab.transform.localScale;
        scale.x *= lengthScale;
        scale.y *= heightMultiplier;
        placed.transform.localScale = scale;

        if (customMesh != null)
        {
            MeshFilter filter = placed.GetComponent<MeshFilter>();
            if (filter != null)
                filter.sharedMesh = customMesh;
        }

        PlacedPiece data = placed.AddComponent<PlacedPiece>();
        data.bottomY = bottom;
        data.topY = bottom + height;
        data.size = new Vector3(gridSize * lengthScale, height, gridSize);
        data.ownedMesh = customMesh;

        AddPlacementAssist(placed, height);
        return data;
    }

    // Adds invisible trigger boxes around a placed piece — one per cell it
    // should help target: the four cardinal sides plus the cell directly
    // above, deliberately not the diagonals, so rays through the corner
    // cells fall through to the ground plane instead of snapping sideways.
    // Each box covers exactly one cell so TryGetPlacementCell can read the
    // target cell straight off the box's center. Local scale cancels out
    // the piece's own visual scale so the box dimensions below are exact
    // grid units regardless of how the piece itself is stretched.
    void AddPlacementAssist(GameObject piece, float pieceHeight)
    {
        GameObject assist = new GameObject("PlacementAssist");
        assist.transform.SetParent(piece.transform, false);

        Vector3 pieceScale = piece.transform.localScale;
        assist.transform.localScale = new Vector3(1f / pieceScale.x, 1f / pieceScale.y, 1f / pieceScale.z);

        Vector3[] cellOffsets =
        {
            new Vector3(gridSize, 0f, 0f),
            new Vector3(-gridSize, 0f, 0f),
            new Vector3(0f, 0f, gridSize),
            new Vector3(0f, 0f, -gridSize),
            new Vector3(0f, pieceHeight, 0f),
        };

        foreach (Vector3 offset in cellOffsets)
        {
            BoxCollider box = assist.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = offset;
            box.size = new Vector3(gridSize, pieceHeight, gridSize);
        }
    }

    // Deliberately precise (unlike TryGetPlacementCell) — deletion should
    // only ever affect the exact piece or wall section under the cursor,
    // never something merely nearby, so hits on the oversized trigger
    // assist boxes are skipped in favor of the nearest real collider.
    bool TryGetPlacedPieceUnderCursor(Ray ray, out Transform piece)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.isTrigger)
                continue;

            bool ours = hit.transform.IsChildOf(placedParent)
                || hit.collider.GetComponent<SplineWallSection>() != null;
            piece = ours ? hit.transform : null;
            return piece != null;
        }

        piece = null;
        return false;
    }

    // Reuses pooled ghost instances so a long line doesn't instantiate/destroy
    // objects every frame; extras left over from a shorter previous line are
    // deactivated rather than destroyed. Scale is re-copied from the live
    // prefab every call, not just at creation — otherwise a pooled ghost
    // keeps showing whatever shape the prefab was when it was first spawned,
    // silently drifting out of sync if the prefab is edited mid-session.
    void ShowGhosts(List<Vector3> cells, float yRotation)
    {
        Quaternion rotation = Quaternion.Euler(0f, yRotation, 0f);
        Vector3 scale = piecePrefab.transform.localScale;
        scale.y *= heightMultiplier;
        for (int i = 0; i < cells.Count; i++)
        {
            GameObject ghost = GetPooled(ghostPool, i, previewColor, "PlacementPreview");
            ghost.SetActive(true);
            SetGhostMesh(ghost, null);
            ghost.transform.SetPositionAndRotation(cells[i], rotation);
            ghost.transform.localScale = scale;
        }
        for (int i = cells.Count; i < ghostPool.Count; i++)
            ghostPool[i].SetActive(false);
    }

    void HideGhosts()
    {
        foreach (GameObject ghost in ghostPool)
            ghost.SetActive(false);
    }

    // Shells each doomed piece in a red pooled copy — position, rotation,
    // and scale are taken from the piece itself, so the overlay reads as
    // the piece turning red rather than a separate marker.
    void ShowDeleteOverlays(List<Transform> pieces)
    {
        for (int i = 0; i < pieces.Count; i++)
        {
            GameObject overlay = GetPooled(deletePool, i, deletePreviewColor, "DeletePreview");
            overlay.SetActive(true);
            // Shell with the piece's actual mesh so curved slices highlight
            // as curves, not as their bounding boxes.
            MeshFilter pieceFilter = pieces[i].GetComponent<MeshFilter>();
            SetGhostMesh(overlay, pieceFilter != null ? pieceFilter.sharedMesh : null);
            overlay.transform.SetPositionAndRotation(pieces[i].position, pieces[i].rotation);
            overlay.transform.localScale = pieces[i].localScale * DeleteOverlayScale;
        }
        for (int i = pieces.Count; i < deletePool.Count; i++)
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
