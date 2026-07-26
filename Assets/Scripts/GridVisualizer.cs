using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// A cursor-local patch of grid lines draped over the terrain: a fixed pool
// of segmented LineRenderers whose vertices sample the ground height, so
// the grid follows slopes instead of floating on a flat plane. The patch
// re-centers whenever the cursor moves to a new cell and hides when the
// cursor is off-world. Line width tapers to zero at each line's ends,
// which fades the patch out softly without any transparency setup.
public class GridVisualizer : MonoBehaviour
{
    public float gridSize = 1f;
    // Cells visible on each side of the cursor.
    public int patchCells = 12;
    public float lineHeight = 0.06f;
    public float lineWidth = 0.05f;
    // Samples per cell along each line — 2 keeps lines hugging the ground
    // across slope breaks without bloating the vertex count.
    public int segmentsPerCell = 2;
    public Material lineMaterial;

    private readonly List<LineRenderer> lineRenderers = new List<LineRenderer>();
    private Camera cam;
    private Transform linesRoot;
    private Vector2Int currentCenterCell = new Vector2Int(int.MinValue, int.MinValue);
    private bool linesVisible;

    void Start()
    {
        cam = Camera.main;
        linesRoot = new GameObject("GridLines").transform;
        linesRoot.SetParent(transform, false);

        int lineCount = (patchCells * 2 + 2) * 2;
        int pointsPerLine = (patchCells * 2 + 1) * segmentsPerCell + 1;
        for (int i = 0; i < lineCount; i++)
            CreateLine(pointsPerLine);
        SetVisible(false);
    }

    void Update()
    {
        var mouse = Mouse.current;
        if (cam == null || mouse == null)
        {
            SetVisible(false);
            return;
        }

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 500f))
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);
        Vector2Int centerCell = new Vector2Int(
            Mathf.FloorToInt(hit.point.x / gridSize),
            Mathf.FloorToInt(hit.point.z / gridSize));
        if (centerCell == currentCenterCell)
            return;

        currentCenterCell = centerCell;
        RebuildPatch(centerCell);
    }

    void RebuildPatch(Vector2Int centerCell)
    {
        int lineIndex = 0;
        // Lines sit on cell boundaries: covering the patch's cells takes
        // one more boundary than there are cells.
        int boundaries = patchCells * 2 + 2;
        float minX = (centerCell.x - patchCells) * gridSize;
        float maxX = (centerCell.x + patchCells + 1) * gridSize;
        float minZ = (centerCell.y - patchCells) * gridSize;
        float maxZ = (centerCell.y + patchCells + 1) * gridSize;

        for (int b = 0; b < boundaries; b++)
        {
            float x = (centerCell.x - patchCells + b) * gridSize;
            DrapeLine(lineRenderers[lineIndex++], new Vector3(x, 0f, minZ), new Vector3(x, 0f, maxZ));

            float z = (centerCell.y - patchCells + b) * gridSize;
            DrapeLine(lineRenderers[lineIndex++], new Vector3(minX, 0f, z), new Vector3(maxX, 0f, z));
        }
    }

    // Lays a line from a to b with every vertex dropped onto the ground.
    void DrapeLine(LineRenderer line, Vector3 a, Vector3 b)
    {
        int points = line.positionCount;
        for (int i = 0; i < points; i++)
        {
            Vector3 p = Vector3.Lerp(a, b, (float)i / (points - 1));
            p.y = SampleGroundY(p) + lineHeight;
            line.SetPosition(i, p);
        }
    }

    float SampleGroundY(Vector3 worldPos)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return 0f;
        return terrain.SampleHeight(worldPos) + terrain.transform.position.y;
    }

    void SetVisible(bool visible)
    {
        if (visible == linesVisible)
            return;
        linesVisible = visible;
        if (linesRoot != null)
            linesRoot.gameObject.SetActive(visible);
    }

    void CreateLine(int pointsPerLine)
    {
        GameObject lineObj = new GameObject("GridLine");
        lineObj.transform.SetParent(linesRoot, false);

        LineRenderer lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = pointsPerLine;
        lr.useWorldSpace = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.material = lineMaterial;

        // Taper to nothing at the ends so the patch edge fades out instead
        // of stopping on a hard square border.
        lr.widthCurve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.2f, 1f),
            new Keyframe(0.8f, 1f),
            new Keyframe(1f, 0f));
        lr.widthMultiplier = lineWidth;

        lineRenderers.Add(lr);
    }
}
