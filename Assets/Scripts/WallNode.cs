using System.Collections.Generic;
using UnityEngine;

// A graph node: the point where wall edges meet — and the owner of ALL
// joint geometry. Every edge end caps flat exactly ON the point, and the
// node supplies the joint masonry itself: an implicit round post of
// radius = half wall thickness, which every member's cap and flank faces
// are tangent to by construction. Only the EXPOSED arc wedges between
// adjacent members are meshed — member bearings sorted around the circle,
// each wall's body covering the semicircle behind its cap, so a gap of g
// degrees between neighbours exposes g − 180 of arc. A collinear butt
// joint therefore shows no post at all, an L-corner a quarter barrel, a
// 90° tee stays flush, and an 8° tee shows one shoulder arc — one rule,
// any angle, any valence, and no cut geometry anywhere. A valence-1 node
// renders nothing: the edge's own flat cap IS the finished end.
public class WallNode : MonoBehaviour
{
    public static readonly List<WallNode> All = new List<WallNode>();

    public Vector3 point;
    public float radius;

    // One edge end resting on this node. An edge appears once per end it
    // parks here (a loop edge would appear twice, but placement refuses
    // those). Stacked courses are NOT members — the node climbs stacks
    // itself when reading heights, so posts rise with the courses.
    public struct Member
    {
        public WallEdge edge;
        public bool atStart;
    }

    public readonly List<Member> members = new List<Member>();

    public int Valence => members.Count;

    Material material;
    Mesh ownedMesh;

    // Two stored points within this distance are the same node.
    const float PointSq = 0.0025f;
    // Wedge columns roughly every this many degrees of arc.
    const float SegmentDegrees = 12f;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }

    public static WallNode FindAt(Vector3 p)
    {
        foreach (WallNode node in All)
        {
            float dx = node.point.x - p.x, dz = node.point.z - p.z;
            if (dx * dx + dz * dz < PointSq)
                return node;
        }
        return null;
    }

    // The node at p, existing or new. Graph code is the only caller —
    // nodes are never created as a side effect of geometry.
    public static WallNode Create(Transform parent, Vector3 p, float radius, Material material)
    {
        WallNode node = FindAt(p);
        if (node != null)
            return node;
        GameObject obj = new GameObject("WallNode");
        obj.transform.SetParent(parent, false);
        obj.transform.position = new Vector3(p.x, 0f, p.z);
        node = obj.AddComponent<WallNode>();
        node.point = new Vector3(p.x, 0f, p.z);
        node.radius = radius;
        node.material = material;
        return node;
    }

    // Membership only — meshes are rebuilt explicitly by the graph ops
    // once a whole mutation (with all its attaches) is done.
    public void Attach(WallEdge edge, bool atStart)
    {
        foreach (Member m in members)
            if (m.edge == edge && m.atStart == atStart)
                return;
        members.Add(new Member { edge = edge, atStart = atStart });
    }

    // Called from a dying edge's OnDisable. A node left with no members
    // has no reason to exist and dissolves immediately (removed from the
    // registry first, so a same-frame commit can't join a corpse).
    public void OnMemberGone(WallEdge edge)
    {
        if (members.RemoveAll(m => m.edge == edge) == 0)
            return;
        if (members.Count == 0)
        {
            All.Remove(this);
            Destroy(gameObject);
        }
    }

    // One member end at this node: the direction its body runs off in,
    // and the vertical span of its masonry here (its end section, plus
    // any stacked courses reaching this end — the post rises with them).
    struct MemberEnd
    {
        public float bearing;
        public float bottomY;
        public float topY;
    }

    public void RebuildMesh()
    {
        members.RemoveAll(m => m.edge == null);

        var ends = new List<MemberEnd>();
        foreach (Member m in members)
            AddMemberEnd(ends, m.edge, m.atStart);

        Mesh mesh = ends.Count >= 2 ? BuildWedgeMesh(ends) : null;

        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter == null)
        {
            filter = gameObject.AddComponent<MeshFilter>();
            gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
        filter.sharedMesh = mesh;
        if (ownedMesh != null)
            Destroy(ownedMesh);
        ownedMesh = mesh;
    }

    void AddMemberEnd(List<MemberEnd> ends, WallEdge edge, bool atStart)
    {
        // Inward: the direction from the node INTO the wall's body.
        Vector3 inward = -edge.EndOutwardDir(atStart);
        if (inward.sqrMagnitude < 0.01f)
            return;
        if (!TryEndSpan(edge, atStart, out float bottom, out float top))
            return;

        // Climb the course stack: each course that reaches this end lifts
        // the post's top to its own.
        WallEdge below = edge;
        bool climbed = true;
        while (climbed)
        {
            climbed = false;
            foreach (WallEdge course in WallEdge.All)
            {
                if (course.stackBase != below)
                    continue;
                bool reaches = atStart
                    ? course.tMin <= below.tMin + 0.02f
                    : course.tMax >= below.tMax - 0.02f;
                if (!reaches)
                    continue;
                if (TryEndSpan(course, atStart, out _, out float courseTop))
                    top = Mathf.Max(top, courseTop);
                below = course;
                climbed = true;
                break;
            }
        }

        ends.Add(new MemberEnd
        {
            bearing = Mathf.Atan2(inward.z, inward.x) * Mathf.Rad2Deg,
            bottomY = bottom,
            topY = top,
        });
    }

    // The masonry span at one end of an edge: its section nearest that
    // end (a locked-top edge can lose buried end sections — the nearest
    // survivor still reads the right heights).
    static bool TryEndSpan(WallEdge edge, bool atStart, out float bottom, out float top)
    {
        bottom = 0f;
        top = 0f;
        float target = atStart ? edge.tMin : edge.tMax;
        float bestD = float.MaxValue;
        foreach (WallEdgeSection section in edge.GetComponentsInChildren<WallEdgeSection>())
        {
            float d = atStart ? Mathf.Abs(section.tStart - target) : Mathf.Abs(section.tEnd - target);
            if (d < bestD)
            {
                bestD = d;
                bottom = section.bottomY;
                top = section.topY;
            }
        }
        return bestD != float.MaxValue;
    }

    // The exposed wedges: sort member bearings, and for each gap between
    // neighbours emit the arc it leaves uncovered. Each wedge spans the
    // vertical union of its two bounding walls — a corner between a tall
    // and a short wall rises to the tall one, showing a rounded quoin
    // edge above the short roof (the radial faces cover that reveal; with
    // equal heights they sit buried inside the walls).
    Mesh BuildWedgeMesh(List<MemberEnd> ends)
    {
        ends.Sort((a, b) => a.bearing.CompareTo(b.bearing));

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        for (int i = 0; i < ends.Count; i++)
        {
            MemberEnd a = ends[i];
            MemberEnd b = ends[(i + 1) % ends.Count];
            float gap = b.bearing - a.bearing;
            if (i == ends.Count - 1)
                gap += 360f;
            float exposed = gap - 180f;
            if (exposed < 0.5f)
                continue;

            float from = a.bearing + 90f;
            float bottom = Mathf.Min(a.bottomY, b.bottomY) - 0.02f;
            float top = Mathf.Max(a.topY, b.topY);
            if (top - bottom < 0.05f)
                continue;
            EmitWedge(vertices, uvs, triangles, from, from + exposed, bottom, top);
        }

        if (triangles.Count == 0)
            return null;

        Mesh mesh = new Mesh { name = "WallNode" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // One wedge: the outward barrel strip, top and bottom pie slices, and
    // the two radial faces (buried inside the bounding walls when heights
    // match, honestly exposed when they don't). Vertices are local to the
    // node's transform (at the point, y = 0), so Y is world elevation.
    // Texture V is world-height anchored like the walls'; barrel U runs
    // in circumferential meters.
    void EmitWedge(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles,
        float fromDeg, float toDeg, float bottom, float top)
    {
        int columns = Mathf.Max(2, Mathf.CeilToInt((toDeg - fromDeg) / SegmentDegrees));
        float baseWall = 3.5f;
        foreach (Member m in members)
            if (m.edge != null) { baseWall = m.edge.baseWallHeight; break; }
        float vBottom = bottom / baseWall;
        float vTop = top / baseWall;

        Vector3 Rim(float deg, float y)
        {
            float rad = deg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(rad) * radius, y, Mathf.Sin(rad) * radius);
        }

        // Barrel: columns advance with increasing bearing; shared columns
        // keep the shading smooth around the curve.
        int barrel = vertices.Count;
        for (int c = 0; c <= columns; c++)
        {
            float deg = Mathf.Lerp(fromDeg, toDeg, (float)c / columns);
            float u = (deg - fromDeg) * Mathf.Deg2Rad * radius;
            vertices.Add(Rim(deg, bottom));
            uvs.Add(new Vector2(u, vBottom));
            vertices.Add(Rim(deg, top));
            uvs.Add(new Vector2(u, vTop));
        }
        for (int c = 0; c < columns; c++)
        {
            int b0 = barrel + c * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
            triangles.Add(b0); triangles.Add(t0); triangles.Add(b1);
            triangles.Add(t0); triangles.Add(t1); triangles.Add(b1);
        }

        // Top and bottom pies. Separate vertices from the barrel so
        // RecalculateNormals keeps the rim edge hard.
        for (int side = 0; side < 2; side++)
        {
            bool isTop = side == 0;
            float y = isTop ? top : bottom;
            int center = vertices.Count;
            vertices.Add(new Vector3(0f, y, 0f));
            uvs.Add(new Vector2(0.5f, 0.5f));
            for (int c = 0; c <= columns; c++)
            {
                float deg = Mathf.Lerp(fromDeg, toDeg, (float)c / columns);
                Vector3 p = Rim(deg, y);
                vertices.Add(p);
                uvs.Add(new Vector2(p.x / (2f * radius) + 0.5f, p.z / (2f * radius) + 0.5f));
            }
            for (int c = 0; c < columns; c++)
            {
                if (isTop)
                {
                    triangles.Add(center); triangles.Add(center + 2 + c); triangles.Add(center + 1 + c);
                }
                else
                {
                    triangles.Add(center); triangles.Add(center + 1 + c); triangles.Add(center + 2 + c);
                }
            }
        }

        // Radial faces: axis-to-rim quads at each boundary bearing, facing
        // away from the wedge.
        for (int side = 0; side < 2; side++)
        {
            bool atFrom = side == 0;
            float deg = atFrom ? fromDeg : toDeg;
            Vector3 rimB = Rim(deg, bottom);
            Vector3 rimT = Rim(deg, top);
            int quad = vertices.Count;
            vertices.Add(new Vector3(0f, bottom, 0f)); uvs.Add(new Vector2(0f, vBottom));
            vertices.Add(new Vector3(0f, top, 0f)); uvs.Add(new Vector2(0f, vTop));
            vertices.Add(rimB); uvs.Add(new Vector2(radius, vBottom));
            vertices.Add(rimT); uvs.Add(new Vector2(radius, vTop));
            if (atFrom)
            {
                triangles.Add(quad); triangles.Add(quad + 1); triangles.Add(quad + 2);
                triangles.Add(quad + 1); triangles.Add(quad + 3); triangles.Add(quad + 2);
            }
            else
            {
                triangles.Add(quad); triangles.Add(quad + 2); triangles.Add(quad + 1);
                triangles.Add(quad + 1); triangles.Add(quad + 2); triangles.Add(quad + 3);
            }
        }
    }

    // A full plain post for placement ghosts — the preview stand-in for
    // whatever wedges the commit will actually carve. Pivot at the bottom
    // center.
    public static Mesh BuildGhostMesh(float radius, float height)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        const int Segments = 24;
        for (int i = 0; i <= Segments; i++)
        {
            float rad = i * Mathf.PI * 2f / Segments;
            Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
            float u = i * Mathf.PI * 2f * radius / Segments;
            vertices.Add(dir * radius);
            uvs.Add(new Vector2(u, 0f));
            vertices.Add(dir * radius + Vector3.up * height);
            uvs.Add(new Vector2(u, 1f));
        }
        for (int i = 0; i < Segments; i++)
        {
            int b0 = i * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
            triangles.Add(b0); triangles.Add(t0); triangles.Add(b1);
            triangles.Add(t0); triangles.Add(t1); triangles.Add(b1);
        }

        int center = vertices.Count;
        vertices.Add(Vector3.up * height);
        uvs.Add(new Vector2(0.5f, 0.5f));
        for (int i = 0; i <= Segments; i++)
        {
            float rad = -i * Mathf.PI * 2f / Segments;
            vertices.Add(new Vector3(Mathf.Cos(rad) * radius, height, Mathf.Sin(rad) * radius));
            uvs.Add(new Vector2(Mathf.Cos(rad) * 0.5f + 0.5f, Mathf.Sin(rad) * 0.5f + 0.5f));
        }
        for (int i = 0; i < Segments; i++)
        {
            triangles.Add(center); triangles.Add(center + 1 + i); triangles.Add(center + 2 + i);
        }

        Mesh mesh = new Mesh { name = "WallNodeGhost" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
