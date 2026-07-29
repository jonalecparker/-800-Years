using System.Collections.Generic;
using UnityEngine;

// A corner node: the point where endpoint-snapped wall ends meet. Member
// walls end in flat caps exactly ON the point (no end extension — see
// SplineWall.nodeAtStart/nodeAtEnd) and the node supplies the corner
// masonry itself: an implicit round post of radius = half wall thickness,
// which every member's cap and flank faces are tangent to by construction.
// Only the EXPOSED arc wedges between adjacent members are meshed —
// member bearings sorted around the circle, each wall's body covering the
// semicircle behind its cap, so a gap of g degrees between neighbours
// exposes g − 180 of arc. A collinear butt joint therefore shows no post
// at all, an L-corner a quarter barrel, a hairpin a rounded tip, and a
// straight-through T keeps its flat far face — one rule, any angle, any
// number of walls, and no cut geometry anywhere at a corner.
public class WallNode : MonoBehaviour
{
    public static readonly List<WallNode> All = new List<WallNode>();

    public Vector3 point;
    public float radius;

    // A member is either a wall ENDING at the node (its cap sits on the
    // point, its node flags set) or a FLANK member — a wall whose side
    // the node sits on (a T-joint at any angle): it contributes its
    // body's half-plane to the wedge rule but its geometry is untouched —
    // no cap, no retraction, no flags.
    public struct Member
    {
        public SplineWall wall;
        public bool flank;
    }

    public readonly List<Member> members = new List<Member>();

    public bool HasMember(SplineWall wall)
    {
        foreach (Member m in members)
            if (m.wall == wall)
                return true;
        return false;
    }

    // The walls joined here — the set the junction machinery stands down
    // for: members never band against or duplicate-block each other, the
    // node owns their meeting.
    public List<SplineWall> MemberWalls
    {
        get
        {
            var walls = new List<SplineWall>();
            foreach (Member m in members)
                if (m.wall != null)
                    walls.Add(m.wall);
            return walls;
        }
    }

    Material material;
    Mesh ownedMesh;

    // Two stored endpoints within this distance are the same corner.
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

    // Commit-side entry: a wall end landed on `p` as a joint. Ensures the
    // node exists, and makes every wall whose stored endpoint lies on the
    // point an end member — walls built before the corner existed have
    // their caps retracted to the point (a rebuild; their end extensions
    // stand down via the node flags), so the post owns the corner space.
    // `flankHost` is the T-joint case: the wall whose FACE the point sits
    // on joins as a flank member, geometry untouched.
    public static WallNode Connect(Transform parent, Vector3 p, float radius, Material material,
        SplineWall flankHost = null)
    {
        WallNode node = FindAt(p);
        if (node == null)
        {
            GameObject obj = new GameObject("WallNode");
            obj.transform.SetParent(parent, false);
            obj.transform.position = new Vector3(p.x, 0f, p.z);
            node = obj.AddComponent<WallNode>();
            node.point = new Vector3(p.x, 0f, p.z);
            node.radius = radius;
            node.material = material;
        }

        // Pass 1: flag every wall ending on the point. Pass 2: retract the
        // newly flagged ones (they were built with overshoot caps — the
        // stacked-course/gapped refusal inside RebuildSections stands, so
        // those keep their old caps). Pass 3: rebuild the REST of the
        // members — a wall committed a moment ago probed the world before
        // the retractions and must re-derive against the retracted caps,
        // or it keeps trims against masonry that no longer exists.
        var joined = new List<SplineWall>();
        var retract = new List<SplineWall>();
        foreach (SplineWall wall in SplineWall.All)
        {
            bool atStart = node.IsAt(wall.curveStart);
            bool atEnd = node.IsAt(wall.curveEnd);
            if (!atStart && !atEnd)
                continue;
            if ((atStart && !wall.nodeAtStart) || (atEnd && !wall.nodeAtEnd))
                retract.Add(wall);
            if (atStart)
                wall.nodeAtStart = true;
            if (atEnd)
                wall.nodeAtEnd = true;
            joined.Add(wall);
            if (!node.HasMember(wall))
                node.members.Add(new Member { wall = wall, flank = false });
        }
        if (flankHost != null && !node.HasMember(flankHost))
            node.members.Add(new Member { wall = flankHost, flank = true });
        foreach (SplineWall wall in retract)
            wall.RebuildSections();
        foreach (SplineWall wall in joined)
            if (!retract.Contains(wall))
                wall.RebuildSections();
        Physics.SyncTransforms();
        node.RebuildMesh();
        return node;
    }

    bool IsAt(Vector3 p)
    {
        float dx = p.x - point.x, dz = p.z - point.z;
        return dx * dx + dz * dz < PointSq;
    }

    // Called from a dying member's OnDisable. With one member left there
    // is no corner anymore: the survivor's end un-nodes (its cap regrows
    // on its next rebuild — the delete flow rebuilds linked survivors)
    // and the post goes away. Removed from the registry immediately so a
    // same-frame commit can't join a corpse.
    public void Unregister(SplineWall wall)
    {
        if (members.RemoveAll(m => m.wall == wall) == 0)
            return;
        members.RemoveAll(m => m.wall == null);
        if (members.Count >= 2)
        {
            RebuildMesh();
            return;
        }
        foreach (Member last in members)
        {
            if (last.flank || last.wall == null)
                continue;
            if (IsAt(last.wall.curveStart))
                last.wall.nodeAtStart = false;
            if (IsAt(last.wall.curveEnd))
                last.wall.nodeAtEnd = false;
        }
        All.Remove(this);
        Destroy(gameObject);
    }

    // One member end at this node: the direction its body runs off in,
    // and the vertical span of its masonry here (its end section).
    struct MemberEnd
    {
        public float bearing;
        public float bottomY;
        public float topY;
    }

    public void RebuildMesh()
    {
        members.RemoveAll(m => m.wall == null);

        var ends = new List<MemberEnd>();
        foreach (Member m in members)
        {
            if (m.flank)
            {
                AddMemberFlank(ends, m.wall);
                continue;
            }
            if (IsAt(m.wall.curveStart))
                AddMemberEnd(ends, m.wall, true);
            if (IsAt(m.wall.curveEnd))
                AddMemberEnd(ends, m.wall, false);
        }

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

    void AddMemberEnd(List<MemberEnd> ends, SplineWall wall, bool atStart)
    {
        // Inward: the direction from the node INTO the wall's body.
        Vector3 inward = -wall.EndOutwardDir(atStart);
        if (inward.sqrMagnitude < 0.01f)
            return;

        // The masonry span at this end: the section closest to the end
        // parameter (end pieces can be dropped by junction bands — the
        // nearest survivor still reads the right heights).
        float bottom = 0f, top = 0f, bestD = float.MaxValue;
        foreach (SplineWallSection section in wall.GetComponentsInChildren<SplineWallSection>())
        {
            float d = atStart ? Mathf.Abs(section.tSweepStart) : Mathf.Abs(section.tSweepEnd - 1f);
            if (d < bestD)
            {
                bestD = d;
                bottom = section.bottomY;
                top = section.topY;
            }
        }
        if (bestD == float.MaxValue)
            return;

        ends.Add(new MemberEnd
        {
            bearing = Mathf.Atan2(inward.z, inward.x) * Mathf.Rad2Deg,
            bottomY = bottom,
            topY = top,
        });
    }

    // A flank member: the node sits ON the host's face, and the host's
    // body covers the half-plane behind it. Inward = from the node toward
    // the host's centerline — its flank normal, pointing into the body —
    // which is all the wedge rule needs; heights come from the host's
    // section at the contact.
    void AddMemberFlank(List<MemberEnd> ends, SplineWall wall)
    {
        float bestT = 0f, bestSq = float.MaxValue;
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f;
            Vector3 q = SplineWall.Evaluate(wall.curveStart, wall.curveControl, wall.curveEnd, t);
            float dx = q.x - point.x, dz = q.z - point.z;
            float sq = dx * dx + dz * dz;
            if (sq < bestSq) { bestSq = sq; bestT = t; }
        }
        Vector3 c = SplineWall.Evaluate(wall.curveStart, wall.curveControl, wall.curveEnd, bestT);
        Vector3 inward = new Vector3(c.x - point.x, 0f, c.z - point.z);
        if (inward.sqrMagnitude < 0.0001f)
            return;

        float bottom = 0f, top = 0f, bestD = float.MaxValue;
        foreach (SplineWallSection section in wall.GetComponentsInChildren<SplineWallSection>())
        {
            float d = Mathf.Abs((section.tSweepStart + section.tSweepEnd) * 0.5f - bestT);
            if (d < bestD)
            {
                bestD = d;
                bottom = section.bottomY;
                top = section.topY;
            }
        }
        if (bestD == float.MaxValue)
            return;

        ends.Add(new MemberEnd
        {
            bearing = Mathf.Atan2(inward.z, inward.x) * Mathf.Rad2Deg,
            bottomY = bottom,
            topY = top,
        });
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
            if (m.wall != null) { baseWall = m.wall.baseWallHeight; break; }
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
