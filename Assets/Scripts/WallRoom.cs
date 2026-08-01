using System.Collections.Generic;
using UnityEngine;

// A designated room: one enclosed face of the wall graph, STORED — the
// boundary is a directed ring of ground edges captured at designation,
// never re-inferred. The room owns its floor slab (flat, snapped up to
// the base step above the highest ground it covers) and, once every
// boundary wall crowns level, a flat stone roof slab resting on that
// shared top. Splitting a boundary wall (a new tee or crossing) just
// re-points the ring at the sub-edges — the slabs never rebuild.
// Deleting boundary masonry breaches the room: the roof goes, the floor
// stays, and the record marks itself broken. The roof's elevation is
// frozen at creation, so courses stacked on the walls afterwards rise
// past the roofline as parapets.
public class WallRoom : MonoBehaviour
{
    public static readonly List<WallRoom> All = new List<WallRoom>();

    public readonly List<WallGraph.DirEdge> boundary = new List<WallGraph.DirEdge>();
    public float floorY;
    public float roofY = float.NegativeInfinity;
    public bool broken;

    public bool HasRoof => roofObj != null;

    Material material;
    float baseStep = 0.5f;
    float baseWallHeight = 3.5f;
    // The face outline sampled once at designation (flattened, CCW). Both
    // slabs are cut from this polygon; boundary splits don't change it.
    List<Vector3> outline;
    GameObject floorObj;
    GameObject roofObj;
    Mesh floorMesh;
    Mesh roofMesh;

    const float FloorSkirt = 0.5f;
    const float RoofThickness = 0.3f;
    const float LevelEps = 0.05f;
    const int EdgeSamples = 8;
    const float InteriorSampleStep = 2f;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (floorMesh != null)
            Destroy(floorMesh);
        if (roofMesh != null)
            Destroy(roofMesh);
    }

    // Designates a traced face as a room: floor immediately, roof if the
    // boundary tops are level. Returns null for degenerate rings.
    public static WallRoom Create(Transform parent, List<WallGraph.DirEdge> ring,
        Material material, float baseStep, float baseWallHeight)
    {
        List<Vector3> outline = SampleOutline(ring);
        if (outline.Count < 3 || Mathf.Abs(SignedArea(outline)) < 0.5f)
        {
            BuildLog.Add("Room not created: enclosure too small.");
            return null;
        }
        if (SignedArea(outline) < 0f)
            outline.Reverse();

        GameObject obj = new GameObject("WallRoom");
        obj.transform.SetParent(parent, false);
        WallRoom room = obj.AddComponent<WallRoom>();
        room.material = material;
        room.baseStep = baseStep;
        room.baseWallHeight = baseWallHeight;
        room.boundary.AddRange(ring);
        room.outline = outline;
        room.BuildFloor();
        string roofFail = room.TryBuildRoof();
        BuildLog.Add(roofFail == null
            ? $"Room designated — {ring.Count} walls, roofed."
            : $"Room designated — {ring.Count} walls, floor only: {roofFail}.");
        return room;
    }

    // The enclosed area a candidate ring would designate — the placement
    // tool uses the sign to tell an interior face from the outer walk.
    public static float RingArea(List<WallGraph.DirEdge> ring)
    {
        return SignedArea(SampleOutline(ring));
    }

    // ------------------------------------------------------------------
    // Graph notifications
    // ------------------------------------------------------------------

    // A boundary edge was split with full coverage (a tee or crossing
    // landed on it): the ring re-points at the sub-edges, in ring order.
    // Geometry is untouched — the face is the same face.
    public static void NotifySplit(WallEdge old, List<WallEdge> subsAscendingT)
    {
        foreach (WallRoom room in All)
        {
            if (room.broken)
                continue;
            for (int i = 0; i < room.boundary.Count; i++)
            {
                if (room.boundary[i].edge != old)
                    continue;
                bool fwd = room.boundary[i].forward;
                room.boundary.RemoveAt(i);
                for (int k = 0; k < subsAscendingT.Count; k++)
                {
                    WallEdge sub = subsAscendingT[fwd ? k : subsAscendingT.Count - 1 - k];
                    room.boundary.Insert(i + k, new WallGraph.DirEdge { edge = sub, forward = fwd });
                }
                break;
            }
        }
    }

    // Boundary masonry was deleted (or an edge died without a split
    // remap): the enclosure is breached and the room dissolves — floor,
    // roof, and record together.
    public static void NotifyBreach(WallEdge edge)
    {
        foreach (WallRoom room in new List<WallRoom>(All))
            if (!room.broken && room.ContainsEdge(edge))
                room.Break();
    }

    // A course landed somewhere: a roofless room whose boundary just got
    // taller may have become level — retry the roof. Roofed rooms keep
    // their frozen roofY, so the new course rises past it as a parapet.
    public static void NotifyCourseLaid(WallEdge below)
    {
        while (below != null && below.stackBase != null)
            below = below.stackBase;
        if (below == null)
            return;
        foreach (WallRoom room in All)
            if (!room.broken && room.roofObj == null && room.ContainsEdge(below)
                && room.TryBuildRoof() == null)
                BuildLog.Add("Roof added — walls now level.");
    }

    // A breached room doesn't linger as a half-object: the same rule as
    // the roof applies to the floor, and the whole record dissolves.
    // (OnDestroy releases the slab meshes.)
    public void Break()
    {
        if (broken)
            return;
        broken = true;
        boundary.Clear();
        BuildLog.Add("Room breached — floor and roof removed.");
        Destroy(gameObject);
    }

    bool ContainsEdge(WallEdge edge)
    {
        foreach (WallGraph.DirEdge d in boundary)
            if (d.edge == edge)
                return true;
        return false;
    }

    // ------------------------------------------------------------------
    // Slabs
    // ------------------------------------------------------------------

    // Floor top: the base step at or above the highest ground the room
    // covers, so terrain never pokes through the slab; the skirt drops
    // below the lowest ground so dips near the walls stay closed. A room
    // whose walls stand on a deck (an upper storey) gets NO floor of its
    // own — the slab it stands on already is one, and a coplanar copy
    // would only z-fight it.
    void BuildFloor()
    {
        float maxGround = float.MinValue;
        float minGround = float.MaxValue;
        foreach (Vector3 p in GroundSamplePoints())
        {
            float g = GroundAt(p);
            maxGround = Mathf.Max(maxGround, g);
            minGround = Mathf.Min(minGround, g);
        }

        float wallBase = float.MinValue;
        foreach (WallGraph.DirEdge d in boundary)
        {
            if (d.edge == null)
                continue;
            foreach (WallEdgeSection s in d.edge.SectionsInOrder())
                wallBase = Mathf.Max(wallBase, s.bottomY);
        }

        floorY = baseStep > 0f ? Mathf.Ceil(maxGround / baseStep) * baseStep : maxGround;
        if (wallBase > floorY + 0.25f)
        {
            // Elevated room: its walls stand well above the terrain, on a
            // slab that serves as its floor.
            floorY = wallBase;
            return;
        }
        float bottom = (baseStep > 0f ? Mathf.Floor(minGround / baseStep) * baseStep : minGround)
            - FloorSkirt;
        floorObj = BuildSlab("RoomFloor", outline, bottom, floorY, out floorMesh);
    }

    // The roof requires a level crown: every boundary section's masonry
    // stack (base wall plus any courses) must top out at one elevation.
    // Uneven tops leave the room floor-only until the player levels them
    // — usually by stacking courses, which retries this automatically.
    // The deck sits FLUSH: its top at the shared wall top, its footprint
    // inset to the walls' inner faces, so the wall-top rim stays walkable
    // beside it and a course stacked later rises as a parapet around it.
    // Returns null on success (or an existing roof), else the reason.
    public string TryBuildRoof()
    {
        if (broken || roofObj != null)
            return null;

        float lo = float.MaxValue;
        float hi = float.MinValue;
        Vector3 loPos = default;
        foreach (WallGraph.DirEdge d in boundary)
        {
            if (d.edge == null)
                return "a boundary wall is missing";
            List<WallEdgeSection> sections = d.edge.SectionsInOrder();
            if (sections.Count == 0)
                return "a boundary wall has no masonry";
            foreach (WallEdgeSection s in sections)
            {
                float top = StackTopOver(d.edge, (s.tStart + s.tEnd) * 0.5f, s.topY);
                if (top < lo)
                {
                    lo = top;
                    loPos = s.transform.position;
                }
                hi = Mathf.Max(hi, top);
            }
        }
        if (hi - lo > LevelEps)
            return $"wall tops uneven by {hi - lo:0.0}m — lowest on the {CompassFrom(loPos)} side"
                + " (level with courses or lock-top)";

        float inset = 0.5f;
        foreach (WallGraph.DirEdge d in boundary)
            if (d.edge != null)
            {
                inset = d.edge.thickness * 0.5f;
                break;
            }
        List<Vector3> deck = InsetOutline(outline, inset);
        if (deck.Count < 3 || SignedArea(deck) < 0.5f)
            return "room too narrow for the roof slab";

        roofY = hi;
        roofObj = BuildSlab("RoomRoof", deck, roofY - RoofThickness, roofY, out roofMesh);
        return null;
    }

    // The outline pushed inward by `inset` (mitered per vertex) — the
    // roof deck's footprint, meeting the walls at their inner faces.
    static List<Vector3> InsetOutline(List<Vector3> poly, float inset)
    {
        int n = poly.Count;
        var result = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 prev = poly[(i + n - 1) % n];
            Vector3 cur = poly[i];
            Vector3 next = poly[(i + 1) % n];
            Vector3 d1 = cur - prev;
            Vector3 d2 = next - cur;
            d1.y = 0f;
            d2.y = 0f;
            if (d1.sqrMagnitude < 1e-8f || d2.sqrMagnitude < 1e-8f)
                continue;
            d1.Normalize();
            d2.Normalize();
            // Left of travel is inward on a CCW outline; the miter keeps
            // corner offsets true, clamped so near-spikes can't explode.
            Vector3 n1 = new Vector3(-d1.z, 0f, d1.x);
            Vector3 n2 = new Vector3(-d2.z, 0f, d2.x);
            Vector3 m = n1 + n2;
            if (m.sqrMagnitude < 1e-6f)
                continue;
            m.Normalize();
            result.Add(cur + m * (inset / Mathf.Max(0.25f, Vector3.Dot(m, n1))));
        }
        return result;
    }

    // Which side of the room a point sits on, as a compass octant from
    // the outline's centroid — so a failed roof can name its culprit.
    string CompassFrom(Vector3 p)
    {
        Vector3 center = Vector3.zero;
        foreach (Vector3 v in outline)
            center += v;
        if (outline.Count > 0)
            center /= outline.Count;
        Vector3 d = p - center;
        if (new Vector2(d.x, d.z).sqrMagnitude < 0.01f)
            return "center";
        float bearing = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
        string[] octants = { "east", "northeast", "north", "northwest",
            "west", "southwest", "south", "southeast" };
        return octants[Mathf.RoundToInt(Mathf.Repeat(bearing, 360f) / 45f) % 8];
    }

    // Top of the masonry stack over one span of a ground edge: climb the
    // stacked courses covering that span and take the highest top.
    static float StackTopOver(WallEdge ground, float mid, float baseTop)
    {
        float top = baseTop;
        WallEdge cur = ground;
        bool climbed = true;
        while (climbed)
        {
            climbed = false;
            foreach (WallEdge e in WallEdge.All)
            {
                if (e.stackBase != cur || mid < e.tMin - 0.001f || mid > e.tMax + 0.001f)
                    continue;
                foreach (WallEdgeSection s in e.SectionsInOrder())
                {
                    if (s.tStart - 0.001f <= mid && mid <= s.tEnd + 0.001f)
                    {
                        top = Mathf.Max(top, s.topY);
                        break;
                    }
                }
                cur = e;
                climbed = true;
                break;
            }
        }
        return top;
    }

    GameObject BuildSlab(string name, List<Vector3> poly, float y0, float y1, out Mesh mesh)
    {
        mesh = BuildSlabMesh(poly, y0, y1, baseWallHeight);
        mesh.name = name;
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(transform, false);
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = material;
        // Cursor raycasts only (targeting reads the surface point) —
        // never occupancy.
        obj.AddComponent<MeshCollider>().sharedMesh = mesh;
        return obj;
    }

    // Outline points plus an interior grid, so a terrain bump in the
    // middle of a large room still lifts the floor above it.
    IEnumerable<Vector3> GroundSamplePoints()
    {
        Vector3 min = outline[0];
        Vector3 max = outline[0];
        foreach (Vector3 p in outline)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            yield return p;
        }
        for (float x = min.x; x <= max.x; x += InteriorSampleStep)
            for (float z = min.z; z <= max.z; z += InteriorSampleStep)
            {
                Vector3 p = new Vector3(x, 0f, z);
                if (PointInPolygon(p, outline))
                    yield return p;
            }
    }

    static float GroundAt(Vector3 p)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return 0f;
        return terrain.transform.position.y + terrain.SampleHeight(p);
    }

    // ------------------------------------------------------------------
    // Geometry helpers
    // ------------------------------------------------------------------

    // The ring's world outline: each directed edge sampled along its own
    // curve, start point included, end point left to the next edge (the
    // shared node), so joints contribute exactly one point.
    static List<Vector3> SampleOutline(List<WallGraph.DirEdge> ring)
    {
        var pts = new List<Vector3>();
        foreach (WallGraph.DirEdge d in ring)
        {
            if (d.edge == null)
                continue;
            Vector3 s = d.edge.A, c = d.edge.control, e = d.edge.B;
            for (int j = 0; j < EdgeSamples; j++)
            {
                float t = (float)j / EdgeSamples;
                if (!d.forward)
                    t = 1f - t;
                Vector3 p = WallEdge.Evaluate(s, c, e, t);
                pts.Add(new Vector3(p.x, 0f, p.z));
            }
        }
        return pts;
    }

    // Shoelace over XZ: positive = counter-clockwise in the ground plane.
    public static float SignedArea(List<Vector3> poly)
    {
        float sum = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % poly.Count];
            sum += a.x * b.z - b.x * a.z;
        }
        return sum * 0.5f;
    }

    static bool PointInPolygon(Vector3 p, List<Vector3> poly)
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

    // The slab: triangulated caps (top faces up, bottom down) and an
    // outward side band. Cap UVs are world-planar and side V is
    // world-height anchored, matching the walls' masonry scale.
    static Mesh BuildSlabMesh(List<Vector3> poly, float y0, float y1, float baseWallHeight)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        List<int> capTris = Triangulate(poly);

        for (int side = 0; side < 2; side++)
        {
            bool isTop = side == 0;
            float y = isTop ? y1 : y0;
            int baseIndex = vertices.Count;
            foreach (Vector3 p in poly)
            {
                vertices.Add(new Vector3(p.x, y, p.z));
                uvs.Add(new Vector2(p.x / baseWallHeight, p.z / baseWallHeight));
            }
            for (int i = 0; i < capTris.Count; i += 3)
            {
                if (isTop)
                {
                    triangles.Add(baseIndex + capTris[i]);
                    triangles.Add(baseIndex + capTris[i + 2]);
                    triangles.Add(baseIndex + capTris[i + 1]);
                }
                else
                {
                    triangles.Add(baseIndex + capTris[i]);
                    triangles.Add(baseIndex + capTris[i + 1]);
                    triangles.Add(baseIndex + capTris[i + 2]);
                }
            }
        }

        float u = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % poly.Count];
            float len = Vector3.Distance(a, b);
            int q = vertices.Count;
            vertices.Add(new Vector3(a.x, y0, a.z));
            uvs.Add(new Vector2(u, y0 / baseWallHeight));
            vertices.Add(new Vector3(a.x, y1, a.z));
            uvs.Add(new Vector2(u, y1 / baseWallHeight));
            vertices.Add(new Vector3(b.x, y0, b.z));
            uvs.Add(new Vector2(u + len, y0 / baseWallHeight));
            vertices.Add(new Vector3(b.x, y1, b.z));
            uvs.Add(new Vector2(u + len, y1 / baseWallHeight));
            triangles.Add(q); triangles.Add(q + 1); triangles.Add(q + 2);
            triangles.Add(q + 1); triangles.Add(q + 3); triangles.Add(q + 2);
            u += len;
        }

        Mesh mesh = new Mesh { name = "RoomSlab" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Ear clipping over a CCW polygon. Collinear runs (the outline is
    // dense with them along straight walls) are skipped as ears; if the
    // remainder degenerates to a collinear ring, fan it and stop rather
    // than loop.
    static List<int> Triangulate(List<Vector3> poly)
    {
        var tris = new List<int>();
        var idx = new List<int>();
        for (int i = 0; i < poly.Count; i++)
            idx.Add(i);

        int guard = poly.Count * poly.Count + 16;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i + idx.Count - 1) % idx.Count];
                int i1 = idx[i];
                int i2 = idx[(i + 1) % idx.Count];
                Vector3 a = poly[i0], b = poly[i1], c = poly[i2];
                float cross = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
                if (cross <= 1e-5f)
                    continue;
                bool contains = false;
                foreach (int j in idx)
                {
                    if (j == i0 || j == i1 || j == i2)
                        continue;
                    if (PointInTriangle(poly[j], a, b, c))
                    {
                        contains = true;
                        break;
                    }
                }
                if (contains)
                    continue;
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                for (int i = 1; i + 1 < idx.Count; i++)
                {
                    tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]);
                }
                return tris;
            }
        }
        if (idx.Count == 3)
        {
            tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
        }
        return tris;
    }

    static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float s1 = (b.x - a.x) * (p.z - a.z) - (b.z - a.z) * (p.x - a.x);
        float s2 = (c.x - b.x) * (p.z - b.z) - (c.z - b.z) * (p.x - b.x);
        float s3 = (a.x - c.x) * (p.z - c.z) - (a.z - c.z) * (p.x - c.x);
        return s1 >= -1e-6f && s2 >= -1e-6f && s3 >= -1e-6f;
    }
}
