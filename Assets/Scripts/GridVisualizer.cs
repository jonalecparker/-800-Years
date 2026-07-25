using System.Collections.Generic;
using UnityEngine;

public class GridVisualizer : MonoBehaviour
{
    public float gridSize = 1f;
    public float extent = 10f;
    public float lineHeight = 0.03f;
    public float lineWidth = 0.05f;
    public Material lineMaterial;

    [Header("Distance Fade")]
    // Lines stay full width out to this distance from the camera, then taper
    // linearly to zero width over fadeDistance beyond it. Width, not alpha,
    // because the unlit grid material isn't set up for transparency — a
    // zero-width line is just as invisible without needing blend-mode setup.
    public float fadeStartDistance = 40f;
    public float fadeDistance = 20f;

    private readonly List<LineRenderer> lineRenderers = new List<LineRenderer>();
    private float currentWidth = -1f;
    private Camera cam;

    void Start()
    {
        cam = Camera.main;
        BuildGrid();
    }

    void Update()
    {
        if (cam == null)
            return;

        float dist = Vector3.Distance(cam.transform.position, transform.position);
        float t = fadeDistance > 0.001f
            ? Mathf.Clamp01((dist - fadeStartDistance) / fadeDistance)
            : (dist > fadeStartDistance ? 1f : 0f);
        float width = Mathf.Lerp(lineWidth, 0f, t);

        if (Mathf.Approximately(width, currentWidth))
            return;

        currentWidth = width;
        foreach (LineRenderer lr in lineRenderers)
        {
            lr.startWidth = width;
            lr.endWidth = width;
        }
    }

    void BuildGrid()
    {
        Transform linesRoot = new GameObject("GridLines").transform;
        linesRoot.SetParent(transform, false);

        int lineCount = Mathf.RoundToInt(extent / gridSize) + 1;
        float half = extent / 2f;

        for (int i = 0; i < lineCount; i++)
        {
            float offset = -half + i * gridSize;
            CreateLine(linesRoot, new Vector3(offset, lineHeight, -half), new Vector3(offset, lineHeight, half));
            CreateLine(linesRoot, new Vector3(-half, lineHeight, offset), new Vector3(half, lineHeight, offset));
        }
    }

    void CreateLine(Transform parent, Vector3 a, Vector3 b)
    {
        GameObject lineObj = new GameObject("GridLine");
        lineObj.transform.SetParent(parent, false);

        LineRenderer lr = lineObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;
        lr.useWorldSpace = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.material = lineMaterial;
        lineRenderers.Add(lr);
    }
}
