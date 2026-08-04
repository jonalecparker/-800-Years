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
    // The lintel over a doorway carries that opening's index; -1 for
    // ordinary masonry. Clicking the lintel is how a doorway is filled
    // back in, so the section has to know it is one.
    public int openingIndex = -1;
    [HideInInspector] public Mesh ownedMesh;

    void OnDestroy()
    {
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }
}
