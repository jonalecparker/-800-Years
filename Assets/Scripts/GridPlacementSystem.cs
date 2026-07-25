using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class GridPlacementSystem : MonoBehaviour
{
    [Header("Placement")]
    public GameObject piecePrefab;
    public float gridSize = 1f;
    // Prefab pivot is centered, not base-anchored, so placed/preview instances
    // need to be lifted by half the piece height to sit on the ground.
    public float placementYOffset = 1.75f;

    [Header("Preview")]
    public Color previewColor = new Color(0.3f, 0.9f, 1f, 1f);

    private readonly List<GameObject> ghostPool = new List<GameObject>();
    private List<Vector3> lastGhostCells = new List<Vector3>();
    private Transform placedParent;
    private float currentYRotation;
    private Vector3? dragStartPosition;
    private Vector3? lastDeletedPosition;
    private Camera cam;

    void Start()
    {
        cam = Camera.main;
        placedParent = new GameObject("PlacedWalls").transform;
    }

    void Update()
    {
        if (piecePrefab == null || cam == null)
            return;

        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null)
            return;

        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            currentYRotation += 90f;

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        bool hasTarget = TryGetPlacementCell(ray, out Vector3 rawSnapped);

        if (hasTarget)
        {
            if (mouse.leftButton.wasPressedThisFrame)
                dragStartPosition = rawSnapped;

            // Nothing is placed while dragging — only a ghost line from the
            // anchor to the cursor's current 45°-locked cell. Wandering past a
            // new 45° direction just re-previews the whole run from the same
            // anchor instead of leaving stray pieces behind from the old one.
            lastGhostCells = dragStartPosition.HasValue
                ? ComputeDragLineCells(dragStartPosition.Value, rawSnapped)
                : new List<Vector3> { rawSnapped };

            // Each cell's height reflects whatever's already stacked there, so
            // the ghost shows exactly where the piece will land instead of
            // floating at ground level over an occupied cell.
            for (int i = 0; i < lastGhostCells.Count; i++)
            {
                Vector3 cell = lastGhostCells[i];
                cell.y = GetNextStackY(cell.x, cell.z);
                lastGhostCells[i] = cell;
            }

            ShowGhosts(lastGhostCells, currentYRotation);
        }
        else
        {
            HideGhosts();
        }

        // Checked unconditionally (not just when hasTarget) so releasing the
        // button while the cursor has wandered off target still commits the
        // last line the ghost showed, instead of getting stuck dragging.
        if (mouse.leftButton.wasReleasedThisFrame && dragStartPosition.HasValue)
        {
            foreach (Vector3 cell in lastGhostCells)
                PlacePiece(cell, currentYRotation);
            dragStartPosition = null;
            HideGhosts();
        }

        // A fresh right-press claims the drag for deletion (via SuppressOrbit)
        // when it starts on a placed piece, so FreeFlyCamera won't hijack it
        // into an orbit.
        if (mouse.rightButton.wasPressedThisFrame)
        {
            FreeFlyCamera.SuppressOrbit = TryGetPlacedPieceUnderCursor(ray, out _);
            lastDeletedPosition = null;
        }

        // Held (not just clicked) so dragging across several pieces erases the
        // whole run in one stroke, mirroring the left-click-drag placement
        // flow — but only once per piece. Without lastDeletedPosition, holding
        // still over a stack would keep hitting whatever's now closest each
        // frame and clear the entire stack from a single click instead of
        // stopping at the one piece the cursor is actually on.
        if (mouse.rightButton.isPressed && !FreeFlyCamera.IsOrbiting
            && TryGetPlacedPieceUnderCursor(ray, out Transform hitPiece)
            && lastDeletedPosition != hitPiece.position)
        {
            lastDeletedPosition = hitPiece.position;
            Destroy(hitPiece.gameObject);
        }
    }

    // Resolves the cell the cursor is targeting with a single raycast. Each
    // placed piece carries an oversized invisible trigger (see
    // AddPlacementAssist) covering its own cell plus one full cell of empty
    // space in every direction, so a ray passing through any of that empty
    // space — not just a ray that hits the piece dead-on — resolves to the
    // adjacent cell via the same hit-point-to-grid rounding used for open
    // ground. Only when nothing (piece or assist zone) is hit at all does
    // this fall back to the raw ground plane.
    bool TryGetPlacementCell(Ray ray, out Vector3 cell)
    {
        if (Physics.Raycast(ray, out RaycastHit hit, 500f) && hit.transform.IsChildOf(placedParent))
        {
            cell = SnapToGrid(hit.point);
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
    // lines instead of straddling a corner where four cells meet.
    Vector3 SnapToGrid(Vector3 worldPoint)
    {
        float x = (Mathf.Floor(worldPoint.x / gridSize) + 0.5f) * gridSize;
        float z = (Mathf.Floor(worldPoint.z / gridSize) + 0.5f) * gridSize;
        return new Vector3(x, placementYOffset, z);
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

    // Finds the top of any existing stack at this XZ cell so a new piece
    // lands on top of it instead of overlapping at ground height — lets
    // walls build upward, one placement action per layer.
    float GetNextStackY(float x, float z)
    {
        float pieceHeight = placementYOffset * 2f;
        float highestY = placementYOffset - pieceHeight;
        bool found = false;
        foreach (Transform child in placedParent)
        {
            if (Mathf.Approximately(child.position.x, x) && Mathf.Approximately(child.position.z, z))
            {
                found = true;
                if (child.position.y > highestY)
                    highestY = child.position.y;
            }
        }
        return found ? highestY + pieceHeight : placementYOffset;
    }

    void PlacePiece(Vector3 position, float yRotation)
    {
        // Recomputed here too (not just for the ghost preview) so placement
        // is correct regardless of what Y the caller passed in.
        position.y = GetNextStackY(position.x, position.z);
        GameObject placed = Instantiate(piecePrefab, position, Quaternion.Euler(0f, yRotation, 0f), placedParent);
        placed.name = piecePrefab.name;
        AddPlacementAssist(placed);
    }

    // Adds an oversized, invisible trigger collider around a placed piece so
    // a single raycast aimed at the empty grid space beside it — not just
    // directly at the piece — still resolves to that adjacent cell. Local
    // scale cancels out the piece's own visual scale so the box size below
    // is exact grid units regardless of how the piece itself is stretched.
    void AddPlacementAssist(GameObject piece)
    {
        GameObject assist = new GameObject("PlacementAssist");
        assist.transform.SetParent(piece.transform, false);

        Vector3 pieceScale = piece.transform.localScale;
        assist.transform.localScale = new Vector3(1f / pieceScale.x, 1f / pieceScale.y, 1f / pieceScale.z);

        BoxCollider box = assist.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(gridSize * 3f, placementYOffset * 2f, gridSize * 3f);
    }

    // Deliberately precise (unlike TryGetPlacementCell) — deletion should
    // only ever affect the exact piece under the cursor, never something
    // merely nearby, so hits on the oversized PlacementAssist triggers are
    // skipped in favor of whatever real collider is actually closest.
    bool TryGetPlacedPieceUnderCursor(Ray ray, out Transform piece)
    {
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            if (hit.transform.name == "PlacementAssist")
                continue;

            piece = hit.transform.IsChildOf(placedParent) ? hit.transform : null;
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
        for (int i = 0; i < cells.Count; i++)
        {
            GameObject ghost = GetGhost(i);
            ghost.SetActive(true);
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

    GameObject GetGhost(int index)
    {
        while (ghostPool.Count <= index)
        {
            GameObject ghost = Instantiate(piecePrefab);
            ghost.name = "PlacementPreview";
            TintPreview(ghost);
            ghostPool.Add(ghost);
        }
        return ghostPool[index];
    }

    void TintPreview(GameObject preview)
    {
        foreach (var col in preview.GetComponentsInChildren<Collider>())
            col.enabled = false;

        foreach (var rend in preview.GetComponentsInChildren<Renderer>())
        {
            foreach (var mat in rend.materials)
            {
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", previewColor);
                else if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", previewColor);
            }
        }
    }
}
