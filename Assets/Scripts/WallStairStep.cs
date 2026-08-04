using UnityEngine;

// One tread of a WallStair — the stair's cursor-picking granularity, and
// deliberately NOT a WallEdgeSection: a stair is deleted whole (sections
// exist because a wall is long and a breach is local), and every delete
// path keyed on WallEdgeSection would otherwise reach for a null edge.
public class WallStairStep : MonoBehaviour
{
    public WallStair stair;
    public int index;
    public float bottomY;
    public float topY;
    [HideInInspector] public Mesh ownedMesh;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }
}
