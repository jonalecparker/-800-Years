using System.Collections.Generic;
using UnityEngine;

// A building slab tile: a FOUNDATION (sets a building's footprint and
// level, and IS its first storey's floor) or a between-storey FLOOR.
// Authored one tile at a time — no plane is ever inferred
// (Docs/BuildingRebuild.md). A tile is a plain vertical prism built by
// transform (unit cube mesh, scaled/rotated/positioned), so the cutaway's
// Y-scale slicing works on it unchanged, and its BoxCollider is for
// cursor raycasts and the walker's feet — never occupancy.
public class SlabTile : MonoBehaviour
{
    public static readonly List<SlabTile> All = new List<SlabTile>();

    public Vector3 center;   // XZ; y carries nothing (tops are topY)
    public float size;       // edge length
    public float yaw;        // degrees, world bearing of the tile's edges
    public float topY;       // the plane — build surface, floor underfoot
    public float bottomY;    // foundation: skirted below ground; floor: topY - Thickness
    public bool isFoundation;

    public const float Thickness = 0.3f;   // between-storey floor slab
    public const float MinSize = 1f;
    public const float MaxSize = 5f;
    public const float SizeStep = 0.5f;
    // The game's one vertical lattice: slab planes seat on it, and the
    // wall-height dial lands on it (GridPlacementSystem.CurrentWallHeight),
    // so planes and wall tops can rendezvous exactly.
    public const float FloorStep = 0.25f;

    static Mesh unitCube;

    public static SlabTile Create(Transform parent, Material material,
        Vector3 center, float size, float yaw, float topY, float bottomY,
        bool isFoundation)
    {
        var go = new GameObject(isFoundation ? "FoundationTile" : "FloorTile");
        go.transform.SetParent(parent, false);
        SlabTile tile = go.AddComponent<SlabTile>();
        tile.center = new Vector3(center.x, 0f, center.z);
        tile.size = size;
        tile.yaw = yaw;
        tile.topY = topY;
        tile.bottomY = bottomY;
        tile.isFoundation = isFoundation;

        // Rendered a hair proud of the stored plane: a floor tile crossing
        // a wall top is SAME-facing coplanar with it, which z-fights. The
        // stored topY stays exact — planes match by data, not by pixels.
        float renderTop = topY + 0.002f;
        go.transform.SetPositionAndRotation(
            new Vector3(center.x, (renderTop + bottomY) * 0.5f, center.z),
            Quaternion.Euler(0f, yaw, 0f));
        go.transform.localScale = new Vector3(size, renderTop - bottomY, size);
        go.AddComponent<MeshFilter>().sharedMesh = SharedCube();
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<BoxCollider>();
        return tile;
    }

    // The tile's four corners at its top plane, world space.
    public void Corners(Vector3[] into)
    {
        float rad = yaw * Mathf.Deg2Rad;
        Vector3 ax = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad)) * (size * 0.5f);
        Vector3 az = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (size * 0.5f);
        Vector3 c = new Vector3(center.x, topY, center.z);
        into[0] = c - ax - az;
        into[1] = c + ax - az;
        into[2] = c + ax + az;
        into[3] = c - ax + az;
    }

    // Do two square tiles overlap in plan (both squares, arbitrary yaw)?
    // Separating-axis test over both squares' edge normals, shrunk by a
    // hair so abutting edges — the whole point of snapping — don't count.
    public static bool Overlaps(Vector3 aCenter, float aSize, float aYaw,
        Vector3 bCenter, float bSize, float bYaw)
    {
        const float shrink = 0.02f;
        foreach (float deg in new[] { aYaw, aYaw + 90f, bYaw, bYaw + 90f })
        {
            float rad = deg * Mathf.Deg2Rad;
            Vector3 axis = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));
            float ca = Vector3.Dot(aCenter, axis);
            float cb = Vector3.Dot(bCenter, axis);
            float ra = Radius(aSize - shrink, aYaw, axis);
            float rb = Radius(bSize - shrink, bYaw, axis);
            if (Mathf.Abs(ca - cb) > ra + rb)
                return false;
        }
        return true;
    }

    // Half-extent of a yawed square projected onto an axis.
    static float Radius(float size, float yaw, Vector3 axis)
    {
        float rad = yaw * Mathf.Deg2Rad;
        Vector3 ax = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));
        Vector3 az = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        return (Mathf.Abs(Vector3.Dot(ax, axis)) + Mathf.Abs(Vector3.Dot(az, axis)))
            * size * 0.5f;
    }

    // Shared unit cube — also the ghost mesh for the slab tools.
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
