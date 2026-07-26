using UnityEngine;

// Recorded at placement time on every placed piece: its vertical span in
// world elevations, plus its world-space dimensions. Spans are the
// placement system's source of truth — stacking and deletion read these
// stored elevations rather than assuming any global piece height — so
// pieces of different heights (and eventually pieces based on real
// terrain, or widened for structural rules) all obey the same math.
public class PlacedPiece : MonoBehaviour
{
    public float bottomY;
    public float topY;
    public Vector3 size;

    // Every commit stamps its pieces with a shared group id and their
    // order along the run, so tools can treat one placed wall — straight
    // or curved — as a single thing (delete drags follow the wall itself
    // instead of a straight grid line). 0 means ungrouped.
    public int groupId;
    public int groupIndex;

    // The run's travel direction at placement time. The piece's own
    // transform can't provide this — footprints are square, so its yaw
    // says nothing about which way the wall runs — and junction angle
    // tests need the real direction.
    public float runYaw;

    // Runtime-generated mesh this piece owns (curved wall slices) —
    // destroyed with the piece so deleted walls don't leak meshes.
    [HideInInspector] public Mesh ownedMesh;

    public float Height => topY - bottomY;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }

    // Strict overlap with a little slack, so a piece whose span merely
    // touches the range end-to-end (the course directly above or below)
    // doesn't count as overlapping it.
    public bool SpanOverlaps(float bottom, float top)
    {
        const float epsilon = 0.01f;
        return bottomY < top - epsilon && topY > bottom + epsilon;
    }
}
