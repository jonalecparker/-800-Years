using UnityEngine;

// One uniform section of a WallEdge: the wall's delete/targeting
// granularity. Carries its slice of the curve (tStart..tEnd — a stacked
// course shares its base's curve, so course sections speak the same
// parameter as the sections below them), its vertical span, and its
// runtime mesh, which dies with it.
public class WallEdgeSection : MonoBehaviour
{
    public WallEdge edge;
    public int index;
    public float tStart;
    public float tEnd;
    public float bottomY;
    public float topY;
    [HideInInspector] public Mesh ownedMesh;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }
}
