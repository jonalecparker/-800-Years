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

    // Stored at y = 0: a node is an XZ point. Its elevation comes from
    // baseY (and, below that, the terrain).
    public Vector3 point;
    public float radius;
    // The base its members stand on — terrain (-inf) or the masonry/slab
    // top of a storey. Part of the node's IDENTITY: a wall built on
    // another wall's top ends directly above that wall's node and must NOT
    // weld onto it, or one post would try to render two storeys of joint.
    public float baseY = float.NegativeInfinity;

    // One edge end resting on this node. An edge appears once per end it
    // parks here (a loop edge would appear twice, but placement refuses
    // those).
    public struct Member
    {
        public WallEdge edge;
        public bool atStart;
    }

    public readonly List<Member> members = new List<Member>();

    public int Valence => members.Count;

    // A clasping buttress at this corner. STORED ON THE NODE because that
    // is what it is: a pier belonging to two walls at once belongs to
    // neither, and nodes own every joint in this graph. `bearing` is the
    // world direction it faces (degrees, atan2(z, x)) — a stated fact, not
    // a gap index, so it survives members joining and leaving. `topY` is
    // absolute, like a face pier's.
    [System.Serializable]
    public struct Buttress
    {
        public float bearing;
        public float topY;
    }
    public List<Buttress> buttresses = new List<Buttress>();

    // A merlon standing ON this joint. Node data for the clasping
    // buttress's reason, which is this graph's oldest rule: a block sitting
    // on a joint belongs to both walls at once, and therefore to neither.
    //
    // Only the HEIGHT is stored. The seat and the plan are re-derived from
    // the members every rebuild — and that is right, unlike the buttress's
    // absolute top: a pier is founded in the ground and must not drift when
    // the ground moves, while a merlon SITS ON the wall and must follow it
    // wherever the wall's top goes.
    //
    // One to a node, like the clasping block: two would stand at the same
    // point and could only intersect.
    [System.Serializable]
    public struct Crenel
    {
        public float height;
    }
    public List<Crenel> crenels = new List<Crenel>();

    // The masonry floor and footing grid at this joint, borrowed from the
    // members — a node stores its storey, not its stonework.
    public float FloorY
    {
        get
        {
            float floor = float.NegativeInfinity;
            foreach (Member m in members)
                if (m.edge != null)
                    floor = Mathf.Max(floor, m.edge.FloorY);
            return floor;
        }
    }

    public float BaseStep
    {
        get
        {
            foreach (Member m in members)
                if (m.edge != null && m.edge.baseStep > 0f)
                    return m.edge.baseStep;
            return 0.5f;
        }
    }

    // The gap in this joint's masonry that a bearing points into, and the
    // TURN the two walls make through it — but only where that gap is a
    // real corner. Same arithmetic the wedge mesh runs: member bearings
    // sorted around the circle, a gap of g exposing g - 180 of arc, so a
    // gap of 180 or less is no corner at all and QuoinMinArc is the
    // smallest turn this graph already agrees is worth dressing.
    //
    // Asking it of the CURSOR's bearing is what makes the tool need no new
    // concept: standing on the outer face near an L picks the outside gap
    // and gets a clasping pier, while the inner face falls in a gap with
    // no exposed arc and gets an ordinary face pier instead.
    public bool TryCornerAt(float bearing, out float mid, out float turn, out float thick)
    {
        mid = 0f;
        turn = 180f;
        thick = 0f;
        var ends = new List<(float bearing, float thick)>();
        foreach (Member m in members)
        {
            if (m.edge == null)
                continue;
            Vector3 inward = -m.edge.EndOutwardDir(m.atStart);
            if (inward.sqrMagnitude < 0.01f)
                continue;
            ends.Add((Mathf.Repeat(Mathf.Atan2(inward.z, inward.x) * Mathf.Rad2Deg, 360f),
                m.edge.thickness));
        }
        if (ends.Count < 2)
            return false;
        ends.Sort((a, b) => a.bearing.CompareTo(b.bearing));

        float want = Mathf.Repeat(bearing, 360f);
        for (int i = 0; i < ends.Count; i++)
        {
            int next = (i + 1) % ends.Count;
            float gap = Mathf.Repeat(ends[next].bearing - ends[i].bearing, 360f);
            if (ends.Count == 2 && gap < 0.01f)
                gap = 180f;
            if (Mathf.Repeat(want - ends[i].bearing, 360f) > gap)
                continue;
            if (gap - 180f < QuoinMinArc)
                return false;
            mid = Mathf.Repeat(ends[i].bearing + gap * 0.5f, 360f);
            turn = 360f - gap;
            thick = Mathf.Max(ends[i].thick, ends[next].thick);
            return true;
        }
        return false;
    }

    Material material;
    Mesh ownedMesh;

    // Two stored points within this distance are the same node.
    const float PointSq = 0.0025f;
    // Wedge columns roughly every this many degrees of arc.
    const float SegmentDegrees = 12f;
    // How far a corner stands proud of the walls it joins. Small on
    // purpose: a quoin is a change of plane that catches light, not a
    // buttress.
    public const float QuoinProud = 0.08f;
    // The smallest turn worth dressing. Exposed arc IS the turn, so this
    // is "walls meeting at 155 degrees or sharper"; anything lazier reads
    // as a straight wall and gets a flush joint.
    public const float QuoinMinArc = 25f;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }

    public static WallNode FindAt(Vector3 p, float baseY)
    {
        foreach (WallNode node in All)
        {
            if (!WallGraph.SameBase(node.baseY, baseY))
                continue;
            float dx = node.point.x - p.x, dz = node.point.z - p.z;
            if (dx * dx + dz * dz < PointSq)
                return node;
        }
        return null;
    }

    // The node at p on the given base, existing or new. Graph code is the
    // only caller — nodes are never created as a side effect of geometry.
    public static WallNode Create(Transform parent, Vector3 p, float baseY, float radius,
        Material material)
    {
        WallNode node = FindAt(p, baseY);
        if (node != null)
            return node;
        GameObject obj = new GameObject("WallNode");
        obj.transform.SetParent(parent, false);
        obj.transform.position = new Vector3(p.x, 0f, p.z);
        node = obj.AddComponent<WallNode>();
        node.point = new Vector3(p.x, 0f, p.z);
        node.baseY = baseY;
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
    // and the vertical span of its masonry here (its end section).
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

        // Clasping buttresses are rebuilt with the joint they dress: their
        // plan comes from the TURN the members make, so a member joining
        // or leaving genuinely changes their shape. A corner that stops
        // being a corner drops its pier — TryBuildCorner refuses, and the
        // stored fact simply renders nothing until a corner returns.
        foreach (WallButtress old in GetComponentsInChildren<WallButtress>())
            DestroyImmediate(old.gameObject);
        for (int i = 0; i < buttresses.Count; i++)
        {
            if (!WallButtress.TryBuildCorner(this, buttresses[i], out Mesh pierMesh,
                    out Vector3 position, out float yaw, out float bottomY, out float topY))
                continue;
            WallButtress.CornerShape(TurnAt(buttresses[i].bearing), ThickAt(buttresses[i].bearing),
                out float halfWidth, out float zBack, out float zFront);
            WallButtress pier = WallButtress.Spawn(transform, material, pierMesh,
                position, yaw, halfWidth, zBack, zFront, bottomY, topY);
            pier.node = this;
            pier.index = i;
        }

        // And the merlon standing on the joint, for the same reason and by
        // the same route: its plan comes from the members, so a member
        // joining or leaving genuinely changes its shape.
        foreach (WallCrenel old in GetComponentsInChildren<WallCrenel>())
            DestroyImmediate(old.gameObject);
        for (int i = 0; i < crenels.Count; i++)
        {
            if (!WallCrenel.TryBuildNode(this, crenels[i], out Mesh blockMesh))
                continue;
            WallCrenel block = WallCrenel.Spawn(transform, material, blockMesh);
            block.node = this;
            block.index = i;
        }
    }

    // The plan a merlon standing on this joint takes, in world XZ, and the
    // elevation it sits at.
    //
    // A RADIAL function of the node's own members: in every direction, the
    // block reaches as far as the furthest thing the joint's masonry
    // actually covers there — each member's own width, out to half a
    // merlon along it, and never less than the post's radius. That makes
    // the answer right for every joint without a single case: a run passing
    // straight THROUGH a node gets a plain merlon of exactly the usual
    // width; an L gets a block that turns the corner; a T or a cross gets
    // the three or four arms it has. It is star-shaped by construction, so
    // it can never self-intersect however the walls meet.
    //
    // The exact corner bearings of each member's rectangle are sampled
    // alongside the uniform ring, so straight edges come out straight
    // rather than chorded.
    public bool TryCrenelPlan(out List<Vector3> plan, out float seatY)
    {
        plan = null;
        seatY = 0f;
        var ends = new List<MemberEnd>();
        var widths = new List<float>();
        foreach (Member m in members)
        {
            int before = ends.Count;
            AddMemberEnd(ends, m.edge, m.atStart);
            if (ends.Count > before)
                widths.Add(m.edge.thickness * 0.5f);
        }
        if (ends.Count == 0)
            return false;

        float post = 0f;
        seatY = float.NegativeInfinity;
        for (int i = 0; i < ends.Count; i++)
        {
            post = Mathf.Max(post, widths[i]);
            seatY = Mathf.Max(seatY, ends[i].topY);
        }
        if (float.IsNegativeInfinity(seatY))
            return false;

        float reach = WallCrenel.NodeReach;
        var angles = new List<float>();
        for (int i = 0; i < 32; i++)
            angles.Add(i * (360f / 32f));
        for (int i = 0; i < ends.Count; i++)
        {
            float b = ends[i].bearing;
            float corner = Mathf.Atan2(widths[i], reach) * Mathf.Rad2Deg;
            angles.Add(b + corner);
            angles.Add(b - corner);
            angles.Add(b + 90f);
            angles.Add(b - 90f);
        }
        for (int i = 0; i < angles.Count; i++)
        {
            angles[i] %= 360f;
            if (angles[i] < 0f)
                angles[i] += 360f;
        }
        angles.Sort();

        plan = new List<Vector3>(angles.Count);
        float last = -999f;
        foreach (float deg in angles)
        {
            if (deg - last < 0.05f)
                continue;
            last = deg;
            float r = post;
            for (int i = 0; i < ends.Count; i++)
            {
                float d = (deg - ends[i].bearing) * Mathf.Deg2Rad;
                float cos = Mathf.Cos(d), sin = Mathf.Abs(Mathf.Sin(d));
                if (cos <= 0.0001f)
                    continue;
                // Where this ray leaves the member's rectangle: the far end
                // or a long side, whichever it meets first.
                float hitEnd = reach / cos;
                float hitSide = sin > 0.0001f ? widths[i] / sin : float.MaxValue;
                r = Mathf.Max(r, Mathf.Min(hitEnd, hitSide));
            }
            float rad = deg * Mathf.Deg2Rad;
            plan.Add(new Vector3(point.x + Mathf.Cos(rad) * r, 0f,
                point.z + Mathf.Sin(rad) * r));
        }
        // Ascending bearing walks counter-clockwise in world XZ, which is
        // the winding every polygon here is stored in.
        return plan.Count >= 3;
    }

    float TurnAt(float bearing) => TryCornerAt(bearing, out _, out float turn, out _) ? turn : 180f;

    float ThickAt(float bearing)
        => TryCornerAt(bearing, out _, out _, out float thick) ? thick : 1f;

    void AddMemberEnd(List<MemberEnd> ends, WallEdge edge, bool atStart)
    {
        // Inward: the direction from the node INTO the wall's body.
        Vector3 inward = -edge.EndOutwardDir(atStart);
        if (inward.sqrMagnitude < 0.01f)
            return;
        if (!TryEndSpan(edge, atStart, out float bottom, out float top))
            return;

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
        float target = atStart ? 0f : 1f;
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

    // The exposed wedges, computed PER VERTICAL BAND (2026-08-16): the
    // joint a node renders is not one configuration but one per height
    // range its members actually occupy. A short wall butting collinear
    // into a tall L-corner reads flush at ITS height — three members,
    // nothing exposed — while ABOVE its top only the L remains and the
    // quarter barrel must return; computing arcs once from all bearings
    // left that corner notch open. Bands are cut at every member top and
    // bottom, each band's arcs come from the members PRESENT in it, and
    // a band with fewer than two members borrows the nearest jointed
    // band's arcs (below first, else above) — which is what keeps the
    // old deliberate look: a corner between a tall and a short wall
    // still rises to the tall one, showing a rounded quoin edge above
    // the short roof. Contiguous bands with identical arcs merge back
    // into one wedge, so equal-height joints mesh exactly as before.
    Mesh BuildWedgeMesh(List<MemberEnd> ends)
    {
        var cuts = new List<float>();
        foreach (MemberEnd e in ends)
        {
            cuts.Add(e.bottomY);
            cuts.Add(e.topY);
        }
        cuts.Sort();
        var bounds = new List<float>();
        foreach (float c in cuts)
            if (bounds.Count == 0 || c - bounds[bounds.Count - 1] > 0.01f)
                bounds.Add(c);

        var bandArcs = new List<List<(float from, float to)>>();
        var bandCounts = new List<int>();
        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            float y0 = bounds[i], y1 = bounds[i + 1];
            var present = new List<MemberEnd>();
            foreach (MemberEnd e in ends)
                if (e.bottomY <= y0 + 0.011f && e.topY >= y1 - 0.011f)
                    present.Add(e);
            bandCounts.Add(present.Count);
            bandArcs.Add(present.Count >= 2 ? ExposedArcs(present) : null);
        }
        for (int i = 0; i < bandArcs.Count; i++)
        {
            if (bandCounts[i] >= 2)
                continue;
            List<(float from, float to)> borrow = null;
            for (int j = i - 1; j >= 0 && borrow == null; j--)
                if (bandCounts[j] >= 2)
                    borrow = bandArcs[j];
            for (int j = i + 1; j < bandArcs.Count && borrow == null; j++)
                if (bandCounts[j] >= 2)
                    borrow = bandArcs[j];
            bandArcs[i] = borrow;
        }

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        for (int i = 0; i < bandArcs.Count; i++)
        {
            int j = i;
            while (j + 1 < bandArcs.Count && SameArcs(bandArcs[i], bandArcs[j + 1]))
                j++;
            if (bandArcs[i] != null)
            {
                float bottom = bounds[i] - (i == 0 ? 0.02f : 0f);
                float top = bounds[j + 1];
                if (top - bottom >= 0.05f)
                    foreach ((float from, float to) in bandArcs[i])
                    {
                        // The joint itself: proud only where the turn is
                        // sharp enough to be a corner worth dressing.
                        float quoin = to - from >= QuoinMinArc ? QuoinProud : 0f;
                        EmitWedge(vertices, uvs, triangles, from, to, bottom, top, quoin);
                        // ...and the string course carried ACROSS the
                        // joint. Without this the band ran up to the post
                        // and stopped: the post stands at the plain
                        // radius wherever the turn is lazy, so it sat
                        // BEHIND a band 120mm proud and left a
                        // post-shaped hole in every course that crossed a
                        // joint. It rides at the band's own projection,
                        // not the quoin's, so the two meet flush.
                        foreach ((float lo, float hi) in
                            WallEdge.StringCourseBands(bottom, top))
                            EmitWedge(vertices, uvs, triangles, from, to, lo, hi,
                                WallEdge.StringCourseProud);
                    }
            }
            i = j;
        }

        if (triangles.Count == 0)
            return null;

        Mesh mesh = new Mesh { name = "WallNode" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        // Posts and quoins wear the same parallax material as the walls —
        // see the note in WallEdge.BuildSectionMesh.
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    // The uncovered arcs for one set of member bearings — the wedge rule
    // unchanged: sorted around the circle, each gap of g degrees between
    // neighbours exposes g − 180 of arc.
    static List<(float from, float to)> ExposedArcs(List<MemberEnd> present)
    {
        present.Sort((a, b) => a.bearing.CompareTo(b.bearing));
        var arcs = new List<(float from, float to)>();
        for (int i = 0; i < present.Count; i++)
        {
            MemberEnd a = present[i];
            MemberEnd b = present[(i + 1) % present.Count];
            float gap = b.bearing - a.bearing;
            if (i == present.Count - 1)
                gap += 360f;
            float exposed = gap - 180f;
            if (exposed < 0.5f)
                continue;
            float from = a.bearing + 90f;
            arcs.Add((from, from + exposed));
        }
        return arcs;
    }

    static bool SameArcs(List<(float from, float to)> a, List<(float from, float to)> b)
    {
        if (a == null || b == null)
            return a == b;
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (Mathf.Abs(a[i].from - b[i].from) > 0.1f
                || Mathf.Abs(a[i].to - b[i].to) > 0.1f)
                return false;
        return true;
    }

    // One wedge: the outward barrel strip, top and bottom pie slices, and
    // the two radial faces (buried inside the bounding walls when heights
    // match, honestly exposed when they don't). Vertices are local to the
    // node's transform (at the point, y = 0), so Y is world elevation.
    // Texture V is world-height anchored like the walls'; barrel U runs
    // in circumferential meters.
    void EmitWedge(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles,
        float fromDeg, float toDeg, float bottom, float top, float rimExtra)
    {
        int columns = Mathf.Max(2, Mathf.CeilToInt((toDeg - fromDeg) / SegmentDegrees));
        // Metres on both axes, matching the walls this joint welds (the
        // posts were still dividing V by baseWallHeight after the walls
        // stopped, which stretched their stone 3.5x against the masonry
        // they sit between).
        float vBottom = bottom;
        float vTop = top;
        // THE QUOIN: the corner stands proud of the faces it joins, the
        // way dressed cornerstones do. It is deliberately hung on the
        // EXPOSED ARC and nothing else — which means it appears exactly
        // where the post already shows stone. A node dropped mid-wall by
        // accident is collinear, exposes no arc, renders no post and so
        // grows no quoin: invisible before, invisible now. A 90 degree
        // tee is flush for the same reason and stays flush, which is also
        // right — quoins dress external corners, not junctions. Free ends
        // (valence 1) render nothing at all, so a delete-hole boundary
        // can never come out looking deliberately finished.
        //
        // ...but only where there is a real CORNER to dress — the caller
        // decides that and hands the extra radius in, because this same
        // sweep also carries the string course across the joint.
        float rim = radius + rimExtra;

        Vector3 Rim(float deg, float y)
        {
            float rad = deg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(rad) * rim, y, Mathf.Sin(rad) * rim);
        }

        // Barrel: columns advance with increasing bearing; shared columns
        // keep the shading smooth around the curve.
        int barrel = vertices.Count;
        for (int c = 0; c <= columns; c++)
        {
            float deg = Mathf.Lerp(fromDeg, toDeg, (float)c / columns);
            float u = (deg - fromDeg) * Mathf.Deg2Rad * rim;
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
                // Metres, like every other horizontal face.
                uvs.Add(new Vector2(p.x, p.z));
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
        // Posts and quoins wear the same parallax material as the walls —
        // see the note in WallEdge.BuildSectionMesh.
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }
}
