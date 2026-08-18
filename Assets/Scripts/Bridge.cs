using System.Collections.Generic;
using UnityEngine;

// A bridge: a stone deck spanning two stated ends, carried on piers.
//
// It is a RIDER on the graph the way WallStair is: registered here,
// member of no WallNode, invisible to every planar query. It never
// splits a wall and no wall splits it. Geometry is BAKED AT COMMIT and
// nothing it was aimed at is stored: the ends read what they stood on
// (ground, a wall top, a slab) the way a stair's foot does, and the
// pier footings are STAMPED from the ground of that day — later
// earthworks never move a bridge's legs (stored, never inferred).
//
// Deliberate v1 calls (2026-08-16, the user's): stone only, the deck is
// NOT a build surface (no resolver treats its top as ground), and there
// is no pier-height refusal — a tall crossing is priced, not forbidden.
// LandLaw deliberately does not gate bridges: crossing OUT of the castle
// grounds — over the river, over the ditch — is what a bridge is FOR,
// the same reasoning that leaves the Ground tool free.
public class Bridge : MonoBehaviour
{
    public static readonly List<Bridge> All = new List<Bridge>();

    // Stored facts. start/end are centerline points (XZ, y carries
    // nothing); startY/endY are the deck TOP at each end, so the deck
    // grades linearly between them.
    public Vector3 start, end;
    public float startY, endY;
    public float width = 3f;

    [System.Serializable]
    public struct Pier
    {
        public Vector3 pos;     // centerline point (XZ)
        public float bottomY;   // stamped footing bottom
    }
    public readonly List<Pier> piers = new List<Pier>();

    [HideInInspector] public Material material;

    public const float DeckThickness = 0.5f;
    // Pier size along the span. Across the span a pier carries the full
    // deck width — a bridge pier, not a stilt.
    public const float PierDepth = 1.2f;
    // Longest deck run allowed to hang free between supports — a stone
    // arch's practical span. The pier count derives from it.
    public const float MaxClearSpan = 8f;
    public const float MinWidth = 1.5f;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public float Length => Flat(end - start).magnitude;

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // Deck top / underside at a point, by projection onto the span.
    public float DeckTopAt(Vector3 p)
    {
        Vector3 run = Flat(end - start);
        float len2 = run.sqrMagnitude;
        float t = len2 > 0.0001f
            ? Mathf.Clamp01(Vector3.Dot(Flat(p - start), run) / len2) : 0f;
        return Mathf.Lerp(startY, endY, t);
    }

    public float DeckBottomAt(Vector3 p) => DeckTopAt(p) - DeckThickness;

    // Evenly spaced pier centerline points for a span — static so the
    // ghost and the commit place the same piers (one-test doctrine).
    public static List<Vector3> PierCenters(Vector3 s, Vector3 e)
    {
        var centers = new List<Vector3>();
        Vector3 run = Flat(e - s);
        float len = run.magnitude;
        int count = Mathf.Max(0, Mathf.CeilToInt(len / MaxClearSpan) - 1);
        for (int i = 1; i <= count; i++)
            centers.Add(Flat(s) + run * ((float)i / (count + 1)));
        return centers;
    }

    public static Bridge Create(Transform parent, Material material,
        Vector3 start, Vector3 end, float startY, float endY, float width,
        List<Pier> piers)
    {
        var go = new GameObject("Bridge");
        go.transform.SetParent(parent, false);
        Bridge bridge = go.AddComponent<Bridge>();
        bridge.start = Flat(start);
        bridge.end = Flat(end);
        bridge.startY = startY;
        bridge.endY = endY;
        bridge.width = width;
        if (piers != null)
            bridge.piers.AddRange(piers);
        bridge.material = material;

        // The transform sits at mid-span (a sensible point for the delete
        // marquee's screen test); the mesh is world-space relative to it
        // on unit scale — the wall convention, which is what the cutaway
        // slices.
        Vector3 mid = (bridge.start + bridge.end) * 0.5f;
        float bottom = startY - DeckThickness;
        foreach (Pier p in bridge.piers)
            bottom = Mathf.Min(bottom, p.bottomY);
        go.transform.position = new Vector3(
            mid.x, (Mathf.Max(startY, endY) + bottom) * 0.5f, mid.z);

        var mesh = new Mesh { name = "BridgeMesh" };
        BuildMesh(mesh, bridge.start, bridge.end, startY, endY, width,
            bridge.piers, go.transform.position);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        // Cursor raycasts and the walker's feet — never occupancy.
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        return bridge;
    }

    void OnDestroy()
    {
        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
            Destroy(filter.sharedMesh);
    }

    // Deck (a graded box) plus one vertical box per pier, in one mesh.
    // Static and origin-parameterized so the placement ghost builds the
    // exact solid the commit will (ghost = commit). UVs follow the
    // masonry conventions, which are METRES on both axes: horizontal
    // faces are world XZ, vertical faces run U along the face and V as
    // world height — so the bridge's courses line up with any wall
    // beside it and abutments pattern-match.
    public static void BuildMesh(Mesh mesh, Vector3 start, Vector3 end,
        float startY, float endY, float width, List<Pier> piers,
        Vector3 origin)
    {
        mesh.Clear();
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        Vector3 s = Flat(start);
        Vector3 e = Flat(end);
        Vector3 run = e - s;
        float len = run.magnitude;
        if (len < 0.01f)
            return;
        Vector3 axis = run / len;
        Vector3 side = new Vector3(-axis.z, 0f, axis.x) * (width * 0.5f);

        // One face, wound (p0,p1,p2,p3) counter-clockwise seen from
        // outside; per-face verts give hard edges and honest UVs.
        void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
            Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
        {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
            int i0 = verts.Count;
            verts.Add(p0 - origin); verts.Add(p1 - origin);
            verts.Add(p2 - origin); verts.Add(p3 - origin);
            for (int k = 0; k < 4; k++)
                norms.Add(n);
            uvs.Add(uv0); uvs.Add(uv1); uvs.Add(uv2); uvs.Add(uv3);
            tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
        }

        Vector2 Cap(Vector3 p) => new Vector2(p.x, p.z);

        void Box(Vector3 aLo, Vector3 aHi, Vector3 bLo, Vector3 bHi,
            Vector3 half)
        {
            Vector3 aLoM = aLo - half, aLoP = aLo + half;
            Vector3 aHiM = aHi - half, aHiP = aHi + half;
            Vector3 bLoM = bLo - half, bLoP = bLo + half;
            Vector3 bHiM = bHi - half, bHiP = bHi + half;
            float runLen = Vector3.Distance(Flat(aLo), Flat(bLo));
            float wide = half.magnitude * 2f;
            // Top and bottom: world-anchored XZ.
            Quad(aHiM, aHiP, bHiP, bHiM,
                Cap(aHiM), Cap(aHiP), Cap(bHiP), Cap(bHiM));
            Quad(aLoM, bLoM, bLoP, aLoP,
                Cap(aLoM), Cap(bLoM), Cap(bLoP), Cap(aLoP));
            // Long sides: U along the run, V world height in courses.
            Quad(aLoM, aHiM, bHiM, bLoM,
                new Vector2(0f, aLoM.y), new Vector2(0f, aHiM.y),
                new Vector2(runLen, bHiM.y), new Vector2(runLen, bLoM.y));
            Quad(aLoP, bLoP, bHiP, aHiP,
                new Vector2(0f, aLoP.y), new Vector2(runLen, bLoP.y),
                new Vector2(runLen, bHiP.y), new Vector2(0f, aHiP.y));
            // Ends: U across the width.
            Quad(aLoM, aLoP, aHiP, aHiM,
                new Vector2(0f, aLoM.y), new Vector2(wide, aLoP.y),
                new Vector2(wide, aHiP.y), new Vector2(0f, aHiM.y));
            Quad(bLoM, bHiM, bHiP, bLoP,
                new Vector2(0f, bLoM.y), new Vector2(0f, bHiM.y),
                new Vector2(wide, bHiP.y), new Vector2(wide, bLoP.y));
        }

        // The deck.
        Box(new Vector3(s.x, startY - DeckThickness, s.z),
            new Vector3(s.x, startY, s.z),
            new Vector3(e.x, endY - DeckThickness, e.z),
            new Vector3(e.x, endY, e.z),
            side);

        // The piers: vertical boxes from just above the deck underside
        // down to their stamped bottoms. A pier the ground already
        // reaches (no daylight under the deck) is skipped — a bridge
        // hugging terrain degenerates into a causeway.
        foreach (Pier p in piers)
        {
            Vector3 c = Flat(p.pos);
            float t = Mathf.Clamp01(Vector3.Dot(c - s, axis) / len);
            float underside = Mathf.Lerp(startY, endY, t) - DeckThickness;
            if (underside - p.bottomY < 0.01f)
                continue;
            Vector3 d = axis * (PierDepth * 0.5f);
            Box(new Vector3(c.x - d.x, p.bottomY, c.z - d.z),
                new Vector3(c.x - d.x, underside + 0.05f, c.z - d.z),
                new Vector3(c.x + d.x, p.bottomY, c.z + d.z),
                new Vector3(c.x + d.x, underside + 0.05f, c.z + d.z),
                side);
        }

        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        // Same stone material, same parallax march — see the note in
        // WallEdge.BuildSectionMesh.
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
    }
}
