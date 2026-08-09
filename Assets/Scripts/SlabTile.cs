using System.Collections.Generic;
using UnityEngine;

// A building slab: a FOUNDATION (sets a building's footprint and level,
// and IS its first storey's floor) or a between-storey FLOOR. Authored
// as a DRAWN OUTLINE — the player chains vertices exactly like the wall
// tools and the closed polygon fills as one prism (2026-08-08; square
// tiles shipped first and lost the playtest: squares over an irregular
// room can't floor it). No plane is ever inferred: the outline's anchor
// states it (Docs/BuildingRebuild.md).
//
// The prism is a world-space mesh on a unit transform — the wall-section
// convention — so the cutaway's Y-scale slicing works on it unchanged.
// Its MeshCollider is for cursor raycasts and the walker's feet — never
// occupancy.
public class SlabTile : MonoBehaviour
{
    public static readonly List<SlabTile> All = new List<SlabTile>();

    // The outline at the top plane, world XZ (y carries nothing), CCW.
    public readonly List<Vector3> verts = new List<Vector3>();
    public float topY;       // the plane — build surface, floor underfoot
    public float bottomY;    // foundation: skirted below ground; floor: topY - Thickness
    public bool isFoundation;

    public const float Thickness = 0.3f;   // between-storey floor slab
    // The game's one vertical lattice: slab planes seat on it, and the
    // wall-height dial lands on it (GridPlacementSystem.CurrentWallHeight),
    // so planes and wall tops can rendezvous exactly.
    public const float FloorStep = 0.25f;
    // A foundation's skirt may reach down at most this far to find
    // ground; a deeper dip under the plane refuses placement (live on
    // the ghost — one-test doctrine). Provisional, tuned by feel.
    public const float MaxSkirt = 2f;

    static Mesh unitCube;

    public static SlabTile Create(Transform parent, Material material,
        List<Vector3> outline, float topY, float bottomY, bool isFoundation)
    {
        var go = new GameObject(isFoundation ? "Foundation" : "Floor");
        go.transform.SetParent(parent, false);
        SlabTile tile = go.AddComponent<SlabTile>();
        foreach (Vector3 v in outline)
            tile.verts.Add(new Vector3(v.x, 0f, v.z));
        if (SignedArea(tile.verts) < 0f)
            tile.verts.Reverse();
        tile.topY = topY;
        tile.bottomY = bottomY;
        tile.isFoundation = isFoundation;

        // The transform sits at the centroid so screen-space tests (the
        // delete marquee) have a sensible point; the mesh is world-space
        // relative to it on unit scale, which is what the cutaway slices.
        Vector3 centroid = Centroid(tile.verts);
        go.transform.position = new Vector3(
            centroid.x, (topY + bottomY) * 0.5f, centroid.z);

        var mesh = new Mesh { name = go.name + "Mesh" };
        // Rendered a hair proud of the stored plane: a floor crossing a
        // wall top is SAME-facing coplanar with it, which z-fights. The
        // stored topY stays exact — planes match by data, not by pixels.
        BuildPrism(mesh, tile.verts, topY + 0.002f, bottomY,
            go.transform.position);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        return tile;
    }

    void OnDestroy()
    {
        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
            Destroy(filter.sharedMesh);
    }

    // ------------------------------------------------------------------
    // Polygon geometry — XZ, y ignored. Static so the placement ghost and
    // the laws run the same math as the committed slab (one-test doctrine).

    public static float SignedArea(List<Vector3> poly)
    {
        float a = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 p = poly[i];
            Vector3 q = poly[(i + 1) % poly.Count];
            a += p.x * q.z - q.x * p.z;
        }
        return a * 0.5f;
    }

    public static Vector3 Centroid(List<Vector3> poly)
    {
        Vector3 c = Vector3.zero;
        foreach (Vector3 v in poly)
            c += v;
        return poly.Count > 0 ? c / poly.Count : c;
    }

    // Even-odd rule; points exactly on the boundary are not reliably
    // classified, which every caller here tolerates.
    public static bool Contains(List<Vector3> poly, Vector3 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[j];
            if (a.z > p.z != b.z > p.z
                && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x)
                inside = !inside;
        }
        return inside;
    }

    // Does any pair of non-adjacent outline edges properly cross?
    public static bool SelfIntersects(List<Vector3> poly)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                // Adjacent edges share a vertex; the wrap pair (last,
                // first) is adjacent too.
                if (j == i || j == (i + 1) % n || (j + 1) % n == i)
                    continue;
                if (SegmentsCross(poly[i], poly[(i + 1) % n],
                        poly[j], poly[(j + 1) % n]))
                    return true;
            }
        return false;
    }

    // Strict crossing: shared endpoints and collinear touching (abutting
    // outlines — the whole point of vertex snapping) don't count.
    public static bool SegmentsCross(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        float d1 = Cross2(c, d, a);
        float d2 = Cross2(c, d, b);
        float d3 = Cross2(a, b, c);
        float d4 = Cross2(a, b, d);
        return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f))
            && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
    }

    static float Cross2(Vector3 o, Vector3 a, Vector3 p)
    {
        return (a.x - o.x) * (p.z - o.z) - (a.z - o.z) * (p.x - o.x);
    }

    // Do two outlines overlap in AREA? Abutting along an edge or sharing
    // vertices is legal contact; a proper edge crossing or a vertex
    // strictly inside the other is a burial.
    public static bool PolyOverlaps(List<Vector3> a, List<Vector3> b)
    {
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (SegmentsCross(a[i], a[(i + 1) % a.Count],
                        b[j], b[(j + 1) % b.Count]))
                    return true;
        foreach (Vector3 v in a)
            if (Contains(b, v) && DistToBoundary(b, v) > 0.02f)
                return true;
        foreach (Vector3 v in b)
            if (Contains(a, v) && DistToBoundary(a, v) > 0.02f)
                return true;
        return false;
    }

    static float DistToBoundary(List<Vector3> poly, Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % poly.Count];
            Vector3 ab = b - a;
            float len2 = ab.x * ab.x + ab.z * ab.z;
            float t = len2 > 0.000001f
                ? Mathf.Clamp01(((p.x - a.x) * ab.x + (p.z - a.z) * ab.z) / len2)
                : 0f;
            Vector3 q = a + ab * t;
            float dx = p.x - q.x, dz = p.z - q.z;
            best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dz * dz));
        }
        return best;
    }

    // Ear clipping over a CCW simple polygon. Returns false on a tangled
    // outline (the placement refusal should have caught it first).
    public static bool Triangulate(List<Vector3> poly, List<int> into)
    {
        into.Clear();
        var idx = new List<int>();
        for (int i = 0; i < poly.Count; i++)
            idx.Add(i);
        int guard = poly.Count * poly.Count + 8;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i + idx.Count - 1) % idx.Count];
                int ib = idx[i];
                int ic = idx[(i + 1) % idx.Count];
                Vector3 a = poly[ia], b = poly[ib], c = poly[ic];
                if (Cross2(a, b, c) <= 0.0001f)
                    continue;   // reflex or degenerate corner — not an ear
                bool holds = false;
                foreach (int other in idx)
                {
                    if (other == ia || other == ib || other == ic)
                        continue;
                    if (PointInTriangle(poly[other], a, b, c))
                    {
                        holds = true;
                        break;
                    }
                }
                if (holds)
                    continue;
                into.Add(ia);
                into.Add(ib);
                into.Add(ic);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
                return false;
        }
        if (idx.Count == 3)
        {
            into.Add(idx[0]);
            into.Add(idx[1]);
            into.Add(idx[2]);
        }
        return idx.Count == 3 && guard > 0;
    }

    static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = Cross2(a, b, p);
        float d2 = Cross2(b, c, p);
        float d3 = Cross2(c, a, p);
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }

    // Fills a mesh with the outline swept from bottom to top — flat caps,
    // vertical sides, hard normals — with vertex positions relative to
    // origin. The commit and the placement ghost both build through here.
    public static void BuildPrism(Mesh mesh, List<Vector3> outline,
        float top, float bottom, Vector3 origin)
    {
        mesh.Clear();
        var poly = outline;
        if (SignedArea(poly) < 0f)
        {
            poly = new List<Vector3>(outline);
            poly.Reverse();
        }
        var tris = new List<int>();
        if (!Triangulate(poly, tris))
            return;

        var v = new List<Vector3>();
        var n = new List<Vector3>();
        var t = new List<int>();
        Vector3 At(Vector3 p, float y) =>
            new Vector3(p.x - origin.x, y - origin.y, p.z - origin.z);

        // Top cap (up), then bottom cap (down, reversed winding).
        int b0 = v.Count;
        foreach (Vector3 p in poly)
        {
            v.Add(At(p, top));
            n.Add(Vector3.up);
        }
        for (int i = 0; i < tris.Count; i += 3)
        {
            t.Add(b0 + tris[i]);
            t.Add(b0 + tris[i + 2]);
            t.Add(b0 + tris[i + 1]);
        }
        b0 = v.Count;
        foreach (Vector3 p in poly)
        {
            v.Add(At(p, bottom));
            n.Add(Vector3.down);
        }
        for (int i = 0; i < tris.Count; i += 3)
        {
            t.Add(b0 + tris[i]);
            t.Add(b0 + tris[i + 1]);
            t.Add(b0 + tris[i + 2]);
        }

        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % poly.Count];
            Vector3 side = new Vector3(b.z - a.z, 0f, -(b.x - a.x)).normalized;
            int s0 = v.Count;
            v.Add(At(a, bottom));
            v.Add(At(a, top));
            v.Add(At(b, top));
            v.Add(At(b, bottom));
            for (int k = 0; k < 4; k++)
                n.Add(side);
            t.AddRange(new[] { s0, s0 + 1, s0 + 2, s0, s0 + 2, s0 + 3 });
        }

        mesh.SetVertices(v);
        mesh.SetNormals(n);
        mesh.SetTriangles(t, 0);
    }

    // Shared unit cube — the slab chain's vertex markers.
    public static Mesh SharedCube()
    {
        if (unitCube != null)
            return unitCube;
        var m = new Mesh { name = "SlabUnitCube" };
        var v = new List<Vector3>();
        var n = new List<Vector3>();
        var t = new List<int>();
        void Face(Vector3 normal, Vector3 up, Vector3 right)
        {
            int b = v.Count;
            Vector3 c = normal * 0.5f;
            v.Add(c - up * 0.5f - right * 0.5f);
            v.Add(c + up * 0.5f - right * 0.5f);
            v.Add(c + up * 0.5f + right * 0.5f);
            v.Add(c - up * 0.5f + right * 0.5f);
            for (int i = 0; i < 4; i++) n.Add(normal);
            t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        Face(Vector3.up, Vector3.forward, Vector3.right);
        Face(Vector3.down, Vector3.back, Vector3.right);
        Face(Vector3.forward, Vector3.up, Vector3.left);
        Face(Vector3.back, Vector3.up, Vector3.right);
        Face(Vector3.right, Vector3.up, Vector3.forward);
        Face(Vector3.left, Vector3.up, Vector3.back);
        m.SetVertices(v);
        m.SetNormals(n);
        m.SetTriangles(t, 0);
        return unitCube = m;
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }
}
