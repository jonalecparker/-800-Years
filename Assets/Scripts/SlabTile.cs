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
    // A stairwell: a hole cut through the slab, a CCW polygon strictly
    // inside the outline, owned by the stair that demanded it. STAMPED at
    // commit time (floor drawn over a stair, or a stair committed under a
    // floor) and stored — never re-derived. The OWNER is a stored
    // relationship, not an inference: deleting the stair closes its wells
    // and the floor heals (the delete tool's job, like the old
    // NotifyStairGone). An ownerless well (a save that predates owners)
    // just never heals that way — delete and redraw the floor.
    public class Well
    {
        public WallStair owner;
        public readonly List<Vector3> verts = new List<Vector3>();
        public Well(WallStair owner, List<Vector3> verts)
        {
            this.owner = owner;
            if (verts != null)
                this.verts.AddRange(verts);
        }
    }

    // Foundations never carry wells.
    public readonly List<Well> wells = new List<Well>();
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
        List<Vector3> outline, float topY, float bottomY, bool isFoundation,
        List<Well> wells = null)
    {
        var go = new GameObject(isFoundation ? "Foundation" : "Floor");
        go.transform.SetParent(parent, false);
        SlabTile tile = go.AddComponent<SlabTile>();
        foreach (Vector3 v in outline)
            tile.verts.Add(new Vector3(v.x, 0f, v.z));
        if (SignedArea(tile.verts) < 0f)
            tile.verts.Reverse();
        if (wells != null)
            foreach (Well w in wells)
                if (w != null && w.verts.Count >= 3)
                    tile.wells.Add(new Well(w.owner, w.verts));
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
            go.transform.position, tile.wells);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        return tile;
    }

    // Re-meshes the prism in place after the stored facts changed (a
    // stair committed under this floor stamping a new well, or a deleted
    // stair closing one). Immediate, not deferred: the well is cut
    // mid-commit and the old collider would otherwise stay in the ray's
    // way for the rest of the frame.
    //
    // The origin is RECOMPUTED FROM STORED FACTS, never read off the
    // transform: the cutaway may be holding this very slab sliced or
    // hidden right now (placing a stair inside a roofed room is exactly
    // that), and a mesh baked against the cut-frame transform renders
    // metres off once the cut clears. Same numbers Create used, so an
    // unsliced rebuild is byte-identical.
    public void Rebuild()
    {
        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null)
            return;
        Vector3 centroid = Centroid(verts);
        Vector3 origin = new Vector3(
            centroid.x, (topY + bottomY) * 0.5f, centroid.z);
        BuildPrism(filter.sharedMesh, verts, topY + 0.002f, bottomY,
            origin, wells);
        MeshCollider col = GetComponent<MeshCollider>();
        if (col != null)
        {
            // Re-cook ENABLED, whatever state the cutaway left it in: a
            // disabled MeshCollider skips the re-bake silently and stays
            // ray-transparent forever after re-enabling — the roof you
            // can see but whose delete ghost lands on the foundation.
            // The cutaway re-asserts its own state next frame.
            bool was = col.enabled;
            col.enabled = true;
            col.sharedMesh = null;
            col.sharedMesh = filter.sharedMesh;
            col.enabled = was;
        }
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
    // Two segments crossing PROPERLY — by a real distance, not by float
    // noise. The world sits on the National Grid, so a coordinate near
    // 14,458m has a float ULP of about a millimetre, and a cross product
    // of differences of such numbers carries a few tenths of a millimetre
    // of error once normalized. Comparing the raw products against exact
    // zero therefore made a segment that merely TOUCHES another read as a
    // crossing, which is what refused a floor slab drawn flush against
    // its neighbour: its edge ended exactly on the neighbour's edge and
    // the sign of a zero landed wherever the last bit fell. Dividing by
    // the segment length turns each product into the perpendicular
    // DISTANCE of an endpoint from the other's line, in metres, so the
    // tolerance below is a real length — an order of magnitude above the
    // noise floor and far under anything a player could aim at.
    public const float CrossEpsilon = 0.01f;

    public static bool SegmentsCross(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        float abLen = Mathf.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
        float cdLen = Mathf.Sqrt((d.x - c.x) * (d.x - c.x) + (d.z - c.z) * (d.z - c.z));
        if (abLen < 0.0001f || cdLen < 0.0001f)
            return false;
        float d1 = Cross2(c, d, a) / cdLen;
        float d2 = Cross2(c, d, b) / cdLen;
        float d3 = Cross2(a, b, c) / abLen;
        float d4 = Cross2(a, b, d) / abLen;
        return ((d1 > CrossEpsilon && d2 < -CrossEpsilon)
                || (d1 < -CrossEpsilon && d2 > CrossEpsilon))
            && ((d3 > CrossEpsilon && d4 < -CrossEpsilon)
                || (d3 < -CrossEpsilon && d4 > CrossEpsilon));
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
                    // A bridged well repeats its two seam vertices, and a
                    // repeat sits exactly ON the ear it duplicates — count
                    // it and no ear near the seam is ever clippable.
                    Vector3 q = poly[other];
                    if (Coincident(q, a) || Coincident(q, b) || Coincident(q, c))
                        continue;
                    if (PointInTriangle(q, a, b, c))
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
    // Wells are holes cut through both caps (bridged into the cap polygon
    // keyhole-style) with their own inward-facing side bands — the well
    // shaft. A well the bridge can't reach cleanly is left uncut rather
    // than tearing the cap along a bad seam.
    public static void BuildPrism(Mesh mesh, List<Vector3> outline,
        float top, float bottom, Vector3 origin,
        List<Well> wells = null)
    {
        mesh.Clear();
        var poly = outline;
        if (SignedArea(poly) < 0f)
        {
            poly = new List<Vector3>(outline);
            poly.Reverse();
        }

        // The cap: the outline with every well bridged in. Track which
        // wells actually merged so only those get shaft bands.
        List<Vector3> cap = poly;
        var cut = new List<List<Vector3>>();
        if (wells != null)
            foreach (Well w in wells)
            {
                if (w == null || w.verts.Count < 3)
                    continue;
                List<Vector3> merged = BridgeHole(cap, w.verts);
                if (merged != cap)
                {
                    cap = merged;
                    cut.Add(w.verts);
                }
            }

        var tris = new List<int>();
        if (!Triangulate(cap, tris))
        {
            // A tangled bridged cap: fall back to the whole slab — a
            // filled well beats no floor at all. (A tangled OUTLINE still
            // returns empty; the placement refusal catches it first.)
            if (cut.Count == 0)
                return;
            cap = poly;
            cut.Clear();
            if (!Triangulate(cap, tris))
                return;
        }

        var v = new List<Vector3>();
        var n = new List<Vector3>();
        var uv = new List<Vector2>();
        var t = new List<int>();
        Vector3 At(Vector3 p, float y) =>
            new Vector3(p.x - origin.x, y - origin.y, p.z - origin.z);

        // Top cap (up), then bottom cap (down, reversed winding). Cap UVs
        // are world XZ in metres — the wall TOP-face density (one tile per
        // metre both ways), world-anchored so abutting slabs pattern-match.
        int b0 = v.Count;
        foreach (Vector3 p in cap)
        {
            v.Add(At(p, top));
            n.Add(Vector3.up);
            uv.Add(new Vector2(p.x, p.z));
        }
        for (int i = 0; i < tris.Count; i += 3)
        {
            t.Add(b0 + tris[i]);
            t.Add(b0 + tris[i + 2]);
            t.Add(b0 + tris[i + 1]);
        }
        b0 = v.Count;
        foreach (Vector3 p in cap)
        {
            v.Add(At(p, bottom));
            n.Add(Vector3.down);
            uv.Add(new Vector2(p.x, p.z));
        }
        for (int i = 0; i < tris.Count; i += 3)
        {
            t.Add(b0 + tris[i]);
            t.Add(b0 + tris[i + 1]);
            t.Add(b0 + tris[i + 2]);
        }

        // Side bands: the outer rim, then each cut well's shaft. A well
        // is wound CCW like the outline, but its solid is OUTSIDE it —
        // walking it reversed makes the same side formula face the void.
        // Side UVs follow the wall SIDE convention, and that convention is
        // now METRES on both axes (2026-08-16): U runs along the ring, V
        // is world height. Courses line up with adjoining masonry and
        // never stretch with the skirt, exactly as before — the change is
        // that a texture states its physical size once, in the material,
        // instead of every mesh dividing by a height constant of its own.
        void Band(List<Vector3> ring, bool reversed)
        {
            int count = ring.Count;
            float run = 0f;
            float vB = bottom;
            float vT = top;
            for (int i = 0; i < count; i++)
            {
                Vector3 a = reversed ? ring[(count - i) % count] : ring[i];
                Vector3 b = reversed
                    ? ring[count - 1 - i] : ring[(i + 1) % count];
                Vector3 side = new Vector3(b.z - a.z, 0f, -(b.x - a.x)).normalized;
                float len = Vector3.Distance(
                    new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
                int s0 = v.Count;
                v.Add(At(a, bottom));
                v.Add(At(a, top));
                v.Add(At(b, top));
                v.Add(At(b, bottom));
                for (int k = 0; k < 4; k++)
                    n.Add(side);
                uv.Add(new Vector2(run, vB));
                uv.Add(new Vector2(run, vT));
                uv.Add(new Vector2(run + len, vT));
                uv.Add(new Vector2(run + len, vB));
                run += len;
                t.AddRange(new[] { s0, s0 + 1, s0 + 2, s0, s0 + 2, s0 + 3 });
            }
        }
        Band(poly, false);
        foreach (List<Vector3> well in cut)
            Band(well, true);

        mesh.SetVertices(v);
        mesh.SetNormals(n);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(t, 0);
        // Same stone material, same parallax march — see the note in
        // WallEdge.BuildSectionMesh.
        mesh.RecalculateTangents();
    }

    static bool Coincident(Vector3 a, Vector3 b)
    {
        return (a - b).sqrMagnitude < 1e-6f;
    }

    // Merges a hole into a cap polygon keyhole-style: the shortest clear
    // segment between the two rings becomes a doubled bridge edge, and
    // the hole is walked in REVERSE (CW against the cap's CCW) so the
    // merged polygon stays simple for the ear clipper. Returns the cap
    // unchanged when no bridge is clear — the hole isn't cleanly inside.
    static List<Vector3> BridgeHole(List<Vector3> outer, List<Vector3> hole)
    {
        int n = outer.Count, m = hole.Count;
        if (n < 3 || m < 3)
            return outer;
        int bi = -1, bj = -1;
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                float d = (outer[i] - hole[j]).sqrMagnitude;
                if (d >= best || !BridgeClear(outer, hole, i, j))
                    continue;
                best = d;
                bi = i;
                bj = j;
            }
        if (bi < 0)
            return outer;

        var merged = new List<Vector3>(n + m + 2);
        for (int i = 0; i <= bi; i++)
            merged.Add(outer[i]);
        for (int k = 0; k <= m; k++)
            merged.Add(hole[((bj - k) % m + m) % m]);
        merged.Add(outer[bi]);
        for (int i = bi + 1; i < n; i++)
            merged.Add(outer[i]);
        return merged;
    }

    static bool BridgeClear(List<Vector3> outer, List<Vector3> hole, int i, int j)
    {
        Vector3 a = outer[i], b = hole[j];
        int n = outer.Count, m = hole.Count;
        for (int k = 0; k < n; k++)
            if (k != i && (k + 1) % n != i
                && SegmentsCross(a, b, outer[k], outer[(k + 1) % n]))
                return false;
        for (int k = 0; k < m; k++)
            if (k != j && (k + 1) % m != j
                && SegmentsCross(a, b, hole[k], hole[(k + 1) % m]))
                return false;
        return true;
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
