using System.Collections.Generic;
using UnityEngine;

// A battlement: the notched parapet along the top of a stretch of wall,
// merlons standing and embrasures cut between them.
//
// STORED AS A SPAN on the edge (WallEdge.Crenel), never a per-edge flag. A
// flag could only say "this whole wall", and a chained run is many edges
// through no fault of the player's, so what a flag meant would depend on
// where the graph put its nodes. A span means the same stretch whatever the
// nodes do — and it is what lets a split CLIP a battlement rather than
// destroy one.
//
// A MERLON THAT STANDS ON A NODE BELONGS TO THE NODE (WallNode.Crenel), the
// clasping buttress's rule and this graph's oldest one: a block sitting on
// a joint belongs to both walls at once and so to neither. It takes the
// node's OWN plan — the members' widths and the turn between them — so a
// run dragged around a corner turns the corner as masonry instead of two
// straight blocks crossing at an angle.
//
// The merlons are DECORATION as far as every cursor resolver is concerned:
// a wall's build surface is section.topY and must stay section.topY, or a
// floor drawn off a crenellated wall would sit on the parapet. They are
// ordinary stone to everything else.
//
// One object per span (or per node), carrying every merlon of it in ONE
// world-space mesh on a unit transform — the wall convention, and what the
// cutaway needs. Deleting is one list entry gone, never a cut.
public class WallCrenel : MonoBehaviour
{
    // Exactly one of these is set, exactly as WallButtress does it.
    public WallEdge edge;
    public WallNode node;
    // Index into the owner's crenel list — what the delete tool removes.
    public int index;
    [HideInInspector] public Mesh ownedMesh;

    // A merlon is a fixed block of masonry. Its WIDTH is what a battlement
    // is made of and is not dialed; its HEIGHT and the SPACING between
    // blocks are, because those are the two things that make one parapet
    // read differently from another.
    public const float MerlonWidth = 1.1f;

    public const float DefaultHeight = 1.2f;
    // A quarter of full height — a low kerb rather than cover, which is
    // still a thing a player might want along a garden wall.
    public const float MinHeight = DefaultHeight * 0.25f;
    public const float HeightStep = 0.1f;

    public const float DefaultGap = 0.7f;
    public const float MinGap = 0.2f;
    public const float MaxGap = 4f;
    public const float GapStep = 0.1f;

    // A stretch too short for a single block is not a battlement.
    public const float MinSpan = MerlonWidth;

    // How far a node's merlon reaches along each of its members. Half a
    // merlon each way, so a node the run passes straight through carries
    // exactly one ordinary merlon's worth of stone.
    public const float NodeReach = MerlonWidth * 0.5f;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }

    // ------------------------------------------------------------------
    // Layout along an edge
    // ------------------------------------------------------------------

    public struct Block
    {
        public float arcStart, arcEnd;
        public float bottomY, topY;
    }

    // Every merlon of a span, measured from the edge's own stored facts —
    // the one door the mesh, the price and the refusal all go through, so
    // the ghost, the charge and the refund cannot disagree.
    //
    // The MERLON keeps its exact width and the EMBRASURES absorb the fit:
    // the dialed gap chooses how many blocks the stretch holds, and the
    // spacing then divides what is left. The other way round — scaling both
    // to fit — makes every wall's blocks a slightly different size, which
    // reads as sloppy stonework the moment two walls meet.
    //
    // Nothing about the count is stored. The rule is "a whole number of
    // merlons, one at each end", so a span clipped by a split re-lays out
    // and is still correct at its new corner. A stored phase would be a
    // fact that could drift.
    //
    // A merlon's foot reaches DOWN to the LOWEST section top it covers and
    // its head rises above the HIGHEST: the doorway's rule, for the
    // doorway's reason. One figure for both leaves a notch of daylight
    // under the parapet on every step of a terrain wall, on the high side.
    public static List<Block> Layout(WallEdge edge, WallEdge.Crenel c)
    {
        var blocks = new List<Block>();
        if (edge == null)
            return blocks;
        float lo = Mathf.Clamp01(Mathf.Min(c.t0, c.t1));
        float hi = Mathf.Clamp01(Mathf.Max(c.t0, c.t1));
        float[] table = WallEdge.BuildArcTable(edge.A, edge.control, edge.B);
        float a0 = WallEdge.ArcAt(table, lo);
        float length = WallEdge.ArcAt(table, hi) - a0;
        if (length < MinSpan)
            return blocks;

        float gap = c.gap > 0.01f ? c.gap : DefaultGap;
        float height = c.height > 0.01f ? c.height : DefaultHeight;
        // Round half-up, never Mathf.Round: ties here are reachable, and
        // banker's rounding on a reachable tie is the height dial's old bug.
        int n = Mathf.FloorToInt((length + gap) / (MerlonWidth + gap) + 0.5f);
        n = Mathf.Clamp(n, 1, Mathf.FloorToInt(length / MerlonWidth));
        if (n < 1)
            return blocks;
        float spacing = n > 1 ? (length - n * MerlonWidth) / (n - 1) : 0f;
        // A LONE merlon is CENTRED, because "begins and ends solid" is a
        // promise it cannot keep: with nothing between, there is no
        // embrasure to absorb the remainder, and laying it from the start
        // dumps every millimetre of slack at the far end. That is what made
        // a chain's corners uneven — a short leg between two joints holds
        // exactly one block, so one corner got the dialled embrasure and
        // the other got the embrasure plus the leftover (measured 0.80m
        // against 1.30m at the two ends of the same 3.93m wall). Centring
        // splits the remainder and both corners read alike. For n >= 2 the
        // spacing already absorbs it exactly and the first and last blocks
        // land ON the span's ends.
        float start = n > 1 ? a0 : a0 + (length - MerlonWidth) * 0.5f;

        List<WallEdgeSection> sections = edge.SectionsInOrder();
        for (int i = 0; i < n; i++)
        {
            float s = start + i * (MerlonWidth + spacing);
            float e = s + MerlonWidth;
            if (!TopRange(edge, sections, WallEdge.TAtArc(table, s),
                    WallEdge.TAtArc(table, e), out float bottomY, out float topY))
                continue;
            blocks.Add(new Block
            {
                arcStart = s,
                arcEnd = e,
                bottomY = bottomY,
                topY = topY + height,
            });
        }
        return blocks;
    }

    // The lowest and highest wall top under a merlon's own footprint.
    // Lintels count deliberately: a lintel's TOP is the wall's top like any
    // other section's — it is the BOTTOM of one that means something else,
    // and that is the field this never reads.
    static bool TopRange(WallEdge edge, List<WallEdgeSection> sections,
        float ta, float tb, out float bottomY, out float topY)
    {
        bottomY = float.MaxValue;
        topY = float.MinValue;
        float lo = Mathf.Min(ta, tb) - 0.0001f;
        float hi = Mathf.Max(ta, tb) + 0.0001f;
        foreach (WallEdgeSection s in sections)
        {
            if (s == null || s.tEnd < lo || s.tStart > hi)
                continue;
            bottomY = Mathf.Min(bottomY, s.topY);
            topY = Mathf.Max(topY, s.topY);
        }
        return bottomY <= topY;
    }

    // The solid stone a battlement adds, for the treasury. Derived from the
    // same layout the mesh is built from, so a refund equals its charge.
    public static float Volume(WallEdge edge, WallEdge.Crenel c)
    {
        if (edge == null)
            return 0f;
        float total = 0f;
        foreach (Block b in Layout(edge, c))
            total += (b.arcEnd - b.arcStart) * edge.thickness
                * Mathf.Max(0f, b.topY - b.bottomY);
        return total;
    }

    public static float NodeVolume(WallNode node, WallNode.Crenel c)
    {
        if (node == null || !node.TryCrenelPlan(out List<Vector3> plan, out float seatY))
            return 0f;
        float height = c.height > 0.01f ? c.height : DefaultHeight;
        return Mathf.Abs(SlabTile.SignedArea(plan)) * height;
    }

    // ------------------------------------------------------------------
    // Geometry
    // ------------------------------------------------------------------

    public static bool TryBuild(WallEdge edge, WallEdge.Crenel c, out Mesh mesh)
    {
        mesh = null;
        var v = new List<Vector3>();
        var uv = new List<Vector2>();
        var tri = new List<int>();
        EmitSpan(v, uv, tri, edge, c);
        if (tri.Count == 0)
            return false;
        mesh = Finish(v, uv, tri, "Crenellations");
        return true;
    }

    public static bool TryBuildNode(WallNode node, WallNode.Crenel c, out Mesh mesh)
    {
        mesh = null;
        var v = new List<Vector3>();
        var uv = new List<Vector2>();
        var tri = new List<int>();
        EmitNode(v, uv, tri, node, c);
        if (tri.Count == 0)
            return false;
        mesh = Finish(v, uv, tri, "CrenelJoint");
        return true;
    }

    // The whole of a dragged run in one mesh, for the ghost — built from
    // the SAME emitters the committed pieces use, so the preview and the
    // masonry cannot differ.
    public static Mesh BuildRun(List<(WallEdge edge, WallEdge.Crenel span)> spans,
        List<(WallNode node, WallNode.Crenel joint)> joints)
    {
        var v = new List<Vector3>();
        var uv = new List<Vector2>();
        var tri = new List<int>();
        foreach ((WallEdge edge, WallEdge.Crenel span) in spans)
            EmitSpan(v, uv, tri, edge, span);
        foreach ((WallNode node, WallNode.Crenel joint) in joints)
            EmitNode(v, uv, tri, node, joint);
        return tri.Count == 0 ? null : Finish(v, uv, tri, "CrenelGhost");
    }

    static void EmitSpan(List<Vector3> v, List<Vector2> uv, List<int> tri,
        WallEdge edge, WallEdge.Crenel c)
    {
        List<Block> blocks = Layout(edge, c);
        if (blocks.Count == 0)
            return;
        float[] table = WallEdge.BuildArcTable(edge.A, edge.control, edge.B);
        float half = edge.thickness * 0.5f;
        foreach (Block b in blocks)
        {
            // Each merlon is a straight block placed ON the curve rather
            // than swept along it. A merlon is barely a metre long, so on
            // any wall a player would curve the sagitta is a centimetre or
            // two — and the battlement as a whole still follows the wall,
            // because every block sits on its own piece of it.
            float tMid = WallEdge.TAtArc(table, (b.arcStart + b.arcEnd) * 0.5f);
            Vector3 p = WallEdge.Evaluate(edge.A, edge.control, edge.B, tMid);
            Vector3 dir = WallEdge.Tangent(edge.A, edge.control, edge.B, tMid).normalized;
            Vector3 nrm = new Vector3(-dir.z, 0f, dir.x);
            Vector3 along = dir * ((b.arcEnd - b.arcStart) * 0.5f);
            Vector3 across = nrm * half;

            Vector3 Corner(float s, float t2, float y) =>
                new Vector3(p.x, y, p.z) + along * s + across * t2;

            Vector3 B0 = Corner(-1f, -1f, b.bottomY);
            Vector3 B1 = Corner(1f, -1f, b.bottomY);
            Vector3 B2 = Corner(1f, 1f, b.bottomY);
            Vector3 B3 = Corner(-1f, 1f, b.bottomY);
            Vector3 T0 = Corner(-1f, -1f, b.topY);
            Vector3 T1 = Corner(1f, -1f, b.topY);
            Vector3 T2 = Corner(1f, 1f, b.topY);
            Vector3 T3 = Corner(-1f, 1f, b.topY);

            // U is metres of arc from the edge's own start and V is world
            // height — the wall's convention exactly, so a merlon's courses
            // run on from the masonry it stands on instead of restarting.
            float u0 = b.arcStart, u1 = b.arcEnd, d = edge.thickness;
            // Five faces, wound so every normal points OUT. There is no
            // underside: a merlon's foot is the lowest wall top it covers,
            // so its bottom is buried in masonry at every point along it.
            // Winding is easy to mirror and expensive to get wrong (the
            // LandPlot lesson): raycasts ignore backfaces, so an inverted
            // face is invisible AND untouchable from the one side that
            // matters. This shipped inside-out once — the parapet was a
            // hole seen from above, and the tell was a downward ray
            // finding nothing.
            Quad(v, uv, tri, B1, B0, T0, T1, new Vector2(u1, b.bottomY),
                new Vector2(u0, b.bottomY), new Vector2(u0, b.topY), new Vector2(u1, b.topY));
            Quad(v, uv, tri, B3, B2, T2, T3, new Vector2(u0, b.bottomY),
                new Vector2(u1, b.bottomY), new Vector2(u1, b.topY), new Vector2(u0, b.topY));
            Quad(v, uv, tri, B2, B1, T1, T2, new Vector2(0f, b.bottomY),
                new Vector2(d, b.bottomY), new Vector2(d, b.topY), new Vector2(0f, b.topY));
            Quad(v, uv, tri, B0, B3, T3, T0, new Vector2(0f, b.bottomY),
                new Vector2(d, b.bottomY), new Vector2(d, b.topY), new Vector2(0f, b.topY));
            Quad(v, uv, tri, T0, T3, T2, T1, new Vector2(u0, d), new Vector2(u0, 0f),
                new Vector2(u1, 0f), new Vector2(u1, d));
        }
    }

    // The block that stands on a joint: a prism of the NODE'S own plan, so
    // an angle in the wall becomes an angle in the parapet. Star-shaped
    // about the node by construction (TryCrenelPlan is a radial function),
    // so the caps are a plain fan and need no ear clipping.
    static void EmitNode(List<Vector3> v, List<Vector2> uv, List<int> tri,
        WallNode node, WallNode.Crenel c)
    {
        if (node == null || !node.TryCrenelPlan(out List<Vector3> plan, out float seatY))
            return;
        float height = c.height > 0.01f ? c.height : DefaultHeight;
        float top = seatY + height;
        int n = plan.Count;
        if (n < 3)
            return;

        // Side wall, one quad per plan edge, U running in metres round the
        // perimeter and V in world height — the walls' convention again.
        float u = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 a = plan[i];
            Vector3 b = plan[(i + 1) % n];
            float run = Vector3.Distance(a, b);
            Vector3 A0 = new Vector3(a.x, seatY, a.z);
            Vector3 B0 = new Vector3(b.x, seatY, b.z);
            Vector3 A1 = new Vector3(a.x, top, a.z);
            Vector3 B1 = new Vector3(b.x, top, b.z);
            // Up the near edge, along the top, down the far one. For a
            // counter-clockwise plan that is the order whose normal points
            // AWAY from the node; the obvious A0-B0-B1-A1 is its mirror and
            // faces every side inward, which is invisible from outside and
            // ignored by every raycast.
            Quad(v, uv, tri, A0, A1, B1, B0, new Vector2(u, seatY),
                new Vector2(u, top), new Vector2(u + run, top), new Vector2(u + run, seatY));
            u += run;
        }

        // The cap. No floor, for the merlon's reason: the block's underside
        // sits on the joint's own masonry.
        int centre = v.Count;
        Vector3 mid = new Vector3(node.point.x, top, node.point.z);
        v.Add(mid);
        uv.Add(new Vector2(mid.x, mid.z));
        for (int i = 0; i < n; i++)
        {
            v.Add(new Vector3(plan[i].x, top, plan[i].z));
            uv.Add(new Vector2(plan[i].x, plan[i].z));
        }
        for (int i = 0; i < n; i++)
        {
            int a = centre + 1 + i;
            int b = centre + 1 + (i + 1) % n;
            // Counter-clockwise plan seen from above winds clockwise in
            // Unity's left-handed frame, so the fan is emitted reversed to
            // face UP. An upward face is not cosmetic: a downward one is
            // invisible from above and ignored by every raycast.
            tri.Add(centre); tri.Add(b); tri.Add(a);
        }
    }

    static void Quad(List<Vector3> v, List<Vector2> uv, List<int> tri,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
    {
        int at = v.Count;
        v.Add(a); uv.Add(ua);
        v.Add(b); uv.Add(ub);
        v.Add(c); uv.Add(uc);
        v.Add(d); uv.Add(ud);
        tri.Add(at); tri.Add(at + 1); tri.Add(at + 2);
        tri.Add(at); tri.Add(at + 2); tri.Add(at + 3);
    }

    static Mesh Finish(List<Vector3> v, List<Vector2> uv, List<int> tri, string name)
    {
        var mesh = new Mesh { name = name };
        // A long run can pass 65k vertices; nothing here should ever be the
        // thing that decides how far a player may drag.
        mesh.indexFormat = v.Count > 60000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(v);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(tri, 0);
        mesh.RecalculateNormals();
        // The tangent stream the stone material's parallax march reads. A
        // runtime mesh carries none unless it is asked for, and without it
        // the march runs on nothing and eats whole faces.
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    // The render object for a battlement its owner has already laid out.
    // World-space mesh on a unit transform, so the cutaway's Y-scale slice
    // works on it the way it works on a wall.
    public static WallCrenel Spawn(Transform parent, Material material, Mesh mesh)
    {
        GameObject obj = new GameObject("WallCrenel");
        obj.transform.SetParent(parent, false);
        obj.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = material;
        // Ordinary stone to the world; invisible to the cursor resolvers,
        // which skip it so the wall top under the merlons stays the build
        // surface it always was. Walls here are not walkways — a floor slab
        // spans two wall tops when you want to walk them — so a parapet
        // standing in the way costs nothing.
        obj.AddComponent<MeshCollider>().sharedMesh = mesh;

        WallCrenel crenel = obj.AddComponent<WallCrenel>();
        crenel.ownedMesh = mesh;
        return crenel;
    }
}
