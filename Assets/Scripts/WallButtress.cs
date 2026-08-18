using System.Collections.Generic;
using UnityEngine;

// One buttress: a pier of masonry standing against a wall's face, or
// clasping a corner.
//
// A buttress is STORED DATA on whatever owns the masonry it dresses —
// never a rider. A face pier belongs to one wall, so it is edge data
// (WallEdge.Buttress) and travels through every split the graph makes. A
// CORNER pier belongs to two walls at once, so it belongs to neither: it
// is node data (WallNode.Buttress), which is exactly where this graph has
// always put joint geometry. Nodes own all joints; a clasping buttress is
// the heaviest joint dressing there is.
//
// This component is only the RENDER PART, the job WallEdgeSection does for
// a slice of masonry: it tags the mesh so the cursor can find it, and it
// dies and is rebuilt with its owner. Exactly one of `edge` and `node` is
// set.
public class WallButtress : MonoBehaviour
{
    public WallEdge edge;
    public WallNode node;
    // Index into the owner's buttress list — what the delete tool removes.
    public int index;
    [HideInInspector] public Mesh ownedMesh;

    // Fixed dimensions, exactly like the doorway's width and head: dialing
    // them would need another meaning for the wheel, and the one dial that
    // earns its place here is the TOP — a low pilaster and a full pier are
    // different things, and the wall can't answer which was meant.
    public const float Width = 1.4f;
    // How far the pier stands proud of the wall's FACE (or, at a corner,
    // proud of the masonry corner).
    public const float Projection = 0.9f;
    // The sloped set-off capping it, shedding water out and away.
    public const float Weathering = 0.7f;
    // How far the footing is driven BELOW the lowest ground under the
    // pier's own footprint, so all four corners are buried and none can
    // float over a dip. Buried stone costs nothing; daylight under a
    // corner costs the whole illusion — the trade the wall's skirt makes.
    public const float Bury = 0.3f;
    // How far below the wall's own top a buttress stops by default: a pier
    // reaching the wall-walk reads as a thickening, not a buttress, and
    // the crenellations to come want the top clear.
    public const float SetOff = 0.5f;
    // Nothing shorter than this is a buttress.
    public const float MinHeight = 1f;
    // The sharpest a corner may be before the clasping block's arithmetic
    // runs away — a spike gets the widest pier the law allows, not an
    // infinite one.
    public const float MinCornerTurn = 40f;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }

    // ------------------------------------------------------------------
    // The founding rule
    // ------------------------------------------------------------------

    // What a pier STANDS ON, given the four corners of its own footprint
    // and the floor of the masonry it dresses.
    //
    // A buttress rests on the first solid thing beneath it and runs
    // continuously up from there — that is the whole of what makes one
    // real, and the reason no face restriction is needed (the user's call,
    // 2026-08-16). Two cases, and they are NOT the same clamp:
    //
    //  - A level platform under the WHOLE footprint (a foundation, a
    //    courtyard, a floor deck) carries it: the pier stands on the
    //    platform, exactly like the walls around it.
    //  - Nothing under it — the footprint hangs off the slab's rim over
    //    open ground, which on a terraced pad is the usual case — and it
    //    goes down to the EARTH, driven Bury below the lowest corner.
    //
    // Ground.HeightAt reads the terrain and cannot see slabs, so a clamp
    // against the wall's floor got the platform case right only by
    // accident and the rim case wrong; asking what is actually underneath
    // gets both right for the same reason.
    public static float FootAt(IList<Vector3> corners, float floorY, float baseStep)
    {
        // Only a masonry storey can rest on a platform. A wall riding the
        // terrain (floorY = -inf) is founded in the earth by definition,
        // and slabs belong to other storeys it never meets.
        if (!float.IsNegativeInfinity(floorY))
        {
            float rest = float.NegativeInfinity;
            foreach (SlabTile tile in SlabTile.All)
            {
                if (tile == null || tile.verts == null || tile.verts.Count < 3)
                    continue;
                if (tile.topY > floorY + 0.05f || tile.topY <= rest)
                    continue;
                // EVERY corner, not the centre: a platform that carries
                // only half the footprint would leave the other half
                // hanging, which is the float this rule exists to end.
                bool carries = true;
                foreach (Vector3 c in corners)
                    if (!SlabTile.Contains(tile.verts, c)) { carries = false; break; }
                if (carries)
                    rest = tile.topY;
            }
            if (!float.IsNegativeInfinity(rest))
                return rest;
        }

        float lowest = 0f;
        if (Ground.Any)
        {
            lowest = float.MaxValue;
            foreach (Vector3 c in corners)
                lowest = Mathf.Min(lowest, Ground.HeightAt(c));
            lowest -= Bury;
        }
        // A pad cut INTO a hill stands below the ground outside it: the
        // pier still starts at the masonry's own floor there, or it would
        // begin halfway up the wall.
        if (!float.IsNegativeInfinity(floorY))
            lowest = Mathf.Min(lowest, floorY);
        if (baseStep > 0f)
            lowest = Mathf.Floor(lowest / baseStep) * baseStep;
        return lowest;
    }

    // ------------------------------------------------------------------
    // A face pier, on one wall
    // ------------------------------------------------------------------

    // The frame a face pier stands in: its centre on the wall's
    // centreline, the outward bearing folded into the yaw, and how far it
    // reaches. Rooted at the CENTRELINE rather than the face so the buried
    // half can never open a gap where a curved wall bends away from it —
    // and that half is not priced, since the wall paid for it already.
    public static void FaceFrame(WallEdge edge, float t, float side,
        out Vector3 centre, out float yaw, out float depth)
    {
        float tc = Mathf.Clamp01(t);
        Vector3 p = WallEdge.Evaluate(edge.A, edge.control, edge.B, tc);
        Vector3 dir = WallEdge.Tangent(edge.A, edge.control, edge.B, tc).normalized;
        centre = new Vector3(p.x, 0f, p.z);
        yaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg + (side < 0f ? 180f : 0f);
        depth = edge.thickness * 0.5f + Projection;
    }

    public static float BottomAt(WallEdge edge, float t, float side)
    {
        if (edge == null)
            return 0f;
        FaceFrame(edge, t, side, out Vector3 centre, out float yaw, out float depth);
        return FootAt(Footprint(centre, yaw, Width * 0.5f, 0f, depth),
            edge.FloorY, edge.baseStep);
    }

    public static bool TryBuild(WallEdge edge, WallEdge.Buttress b, out Mesh mesh,
        out Vector3 position, out float yaw, out float bottomY, out float topY)
    {
        mesh = null;
        position = default;
        yaw = 0f;
        bottomY = 0f;
        topY = 0f;
        if (edge == null)
            return false;
        FaceFrame(edge, b.t, b.side, out Vector3 centre, out yaw, out float depth);
        bottomY = BottomAt(edge, b.t, b.side);
        topY = b.topY;
        if (topY - bottomY < 0.1f)
            return false;
        position = new Vector3(centre.x, (bottomY + topY) * 0.5f, centre.z);
        mesh = BuildPrism(Width * 0.5f, 0f, depth, bottomY, topY);
        return true;
    }

    // ------------------------------------------------------------------
    // A corner pier, on one node
    // ------------------------------------------------------------------

    // The clasping block's plan, from the TURN the two walls make. It is
    // the face pier's box, widened and pushed out by however far the
    // masonry corner itself sticks out — so at a 180 degree "corner" the
    // arithmetic degenerates EXACTLY to a face pier, with no special case
    // anywhere. reach = (t/2)/sin(turn/2) is the distance from the node to
    // where the two outer faces meet; the same figure backwards is how far
    // behind the node the inner corner sits, and the block is buried to
    // 90% of it so its back corners are always inside stone.
    public static void CornerShape(float turn, float thickness,
        out float halfWidth, out float zBack, out float zFront)
    {
        float half = Mathf.Clamp(turn, MinCornerTurn, 180f) * 0.5f * Mathf.Deg2Rad;
        float corner = thickness * 0.5f / Mathf.Max(0.01f, Mathf.Sin(half));
        halfWidth = Width * 0.5f + thickness * 0.5f / Mathf.Max(0.01f, Mathf.Tan(half));
        zFront = corner + Projection;
        zBack = -corner * 0.9f;
    }

    // Everything about a corner pier except its mesh — so the tool can
    // read its footing and its price every frame without allocating a
    // mesh every frame.
    public static bool TryCornerFrame(WallNode node, float bearing, out float yaw,
        out float halfWidth, out float zBack, out float zFront, out float bottomY)
    {
        yaw = 0f;
        halfWidth = 0f;
        zBack = 0f;
        zFront = 0f;
        bottomY = 0f;
        if (node == null || !node.TryCornerAt(bearing, out _, out float turn, out float thick))
            return false;
        CornerShape(turn, thick, out halfWidth, out zBack, out zFront);
        // Local +Z must point along the stored bearing: the prism is built
        // with +Z outward, and yaw is measured the way every wall here
        // measures it.
        yaw = 90f - bearing;
        bottomY = FootAt(Footprint(node.point, yaw, halfWidth, zBack, zFront),
            node.FloorY, node.BaseStep);
        return true;
    }

    public static bool TryBuildCorner(WallNode node, WallNode.Buttress b, out Mesh mesh,
        out Vector3 position, out float yaw, out float bottomY, out float topY)
    {
        mesh = null;
        position = default;
        topY = b.topY;
        if (!TryCornerFrame(node, b.bearing, out yaw, out float halfWidth,
                out float zBack, out float zFront, out bottomY))
            return false;
        if (topY - bottomY < 0.1f)
            return false;
        position = new Vector3(node.point.x, (bottomY + topY) * 0.5f, node.point.z);
        mesh = BuildPrism(halfWidth, zBack, zFront, bottomY, topY);
        return true;
    }

    // ------------------------------------------------------------------
    // Shared geometry
    // ------------------------------------------------------------------

    // The four plan corners of a pier's footprint, in world XZ.
    public static List<Vector3> Footprint(Vector3 centre, float yaw,
        float halfWidth, float zBack, float zFront)
    {
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        var corners = new List<Vector3>(4);
        foreach (float x in new[] { -halfWidth, halfWidth })
            foreach (float z in new[] { zBack, zFront })
                corners.Add(centre + rot * new Vector3(x, 0f, z));
        return corners;
    }

    // A pier as a prism in its own frame: +X across, +Z out from the wall,
    // capped by a weathering that slopes down and out.
    //
    // EVERY face is wound AND mapped with the same UV handedness —
    // sign(dot(cross(dP/du, dP/dv), N)) is +1 on all six. That is not
    // housekeeping: the wall material does parallax occlusion mapping,
    // which ray-marches in TANGENT space, so a mirrored tangent frame
    // marches the ray the wrong way and EATS THE FACE WHOLE. It shipped
    // once as one see-through flank whose side jumped when the pier was
    // placed on the far face — that is the 180 degree yaw swapping which
    // flank was the mirrored one.
    static Mesh BuildPrism(float halfWidth, float zBack, float zFront,
        float bottomY, float topY)
    {
        float midY = (bottomY + topY) * 0.5f;
        float span = topY - bottomY;
        float yLo = bottomY - midY;
        float yHi = topY - midY;
        float weather = Mathf.Min(Weathering, span * 0.5f);
        float yOut = yHi - weather;
        float w = halfWidth * 2f;
        float d = zFront - zBack;

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 e,
            Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ue)
        {
            int at = vertices.Count;
            vertices.Add(a); uvs.Add(ua);
            vertices.Add(b); uvs.Add(ub);
            vertices.Add(c); uvs.Add(uc);
            vertices.Add(e); uvs.Add(ue);
            triangles.Add(at); triangles.Add(at + 1); triangles.Add(at + 2);
            triangles.Add(at); triangles.Add(at + 2); triangles.Add(at + 3);
        }

        Vector3 B0 = new Vector3(-halfWidth, yLo, zBack);
        Vector3 B1 = new Vector3(halfWidth, yLo, zBack);
        Vector3 B2 = new Vector3(halfWidth, yLo, zFront);
        Vector3 B3 = new Vector3(-halfWidth, yLo, zFront);
        Vector3 T0 = new Vector3(-halfWidth, yHi, zBack);
        Vector3 T1 = new Vector3(halfWidth, yHi, zBack);
        Vector3 T2 = new Vector3(halfWidth, yOut, zFront);
        Vector3 T3 = new Vector3(-halfWidth, yOut, zFront);

        // Both UV axes are metres of real masonry, the wall's own
        // convention: V is world height on every vertical face, so the
        // pier's courses line up with the wall it stands against.
        Vector2 UV(float u, float y) => new Vector2(u, y + midY);

        Quad(B3, B2, T2, T3, UV(0f, yLo), UV(w, yLo), UV(w, yOut), UV(0f, yOut));
        Quad(B1, B0, T0, T1, UV(0f, yLo), UV(w, yLo), UV(w, yHi), UV(0f, yHi));
        Quad(B1, T1, T2, B2, UV(d, yLo), UV(d, yHi), UV(0f, yOut), UV(0f, yLo));
        Quad(B0, B3, T3, T0, UV(0f, yLo), UV(d, yLo), UV(d, yOut), UV(0f, yHi));
        Quad(T0, T3, T2, T1, new Vector2(0f, d), new Vector2(0f, 0f),
            new Vector2(w, 0f), new Vector2(w, d));
        Quad(B0, B1, B2, B3, new Vector2(0f, 0f), new Vector2(w, 0f),
            new Vector2(w, d), new Vector2(0f, d));

        var mesh = new Mesh { name = "Buttress" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        // The tangent frame the parallax march needs, stated rather than
        // left to whatever the shader can guess from screen derivatives.
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Builds the render object for a pier its owner has already sized.
    public static WallButtress Spawn(Transform parent, Material material, Mesh mesh,
        Vector3 position, float yaw, float halfWidth, float zBack, float zFront,
        float bottomY, float topY)
    {
        GameObject obj = new GameObject("WallButtress");
        obj.transform.SetParent(parent, false);
        obj.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = material;

        BoxCollider box = obj.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0f, (zBack + zFront) * 0.5f);
        box.size = new Vector3(halfWidth * 2f, topY - bottomY, zFront - zBack);

        WallButtress pier = obj.AddComponent<WallButtress>();
        pier.ownedMesh = mesh;
        return pier;
    }
}
