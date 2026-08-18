using System.Collections.Generic;
using UnityEngine;

// A castle site: a scene component on the georeferenced marker objects
// (Docs/Territories.md). Name comes from the GameObject, position from
// the transform, and the player's home is set in the scene — Grosmont
// on the Marches. Sites are scene facts like the terrain, NOT save
// facts: saves reference them by index into the name-sorted list
// (Ordered), the same way they reference terrain by Ground.BaseName.
// A scene with no sites (Spiš) gets one synthetic player-home site at
// the map center through LandParcels' fallback, not here.
public class CastleSite : MonoBehaviour
{
    public static readonly List<CastleSite> All = new List<CastleSite>();

    public bool isPlayerHome;

    // "Grosmont Castle Site" → "Grosmont Castle". The full castle name
    // is the lordship's name — "White Castle" can't shed its surname.
    public string SiteName => gameObject.name.Replace(" Site", "");
    public Vector2 PositionXZ =>
        new Vector2(transform.position.x, transform.position.z);

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // A marker is a GEOREFERENCE, not a building: it must never catch a
    // ray or trip the walker. The scene authored these as boxes to be
    // draggable in the editor, and their colliders survived their
    // renderers being hidden — leaving three invisible 6 x 30 x 6m solids,
    // one of them standing in the middle of Grosmont, blocking the walker
    // on his own stairs and swallowing the wall tools' first hit. Killed
    // here rather than in the scene so a marker added later cannot bring
    // one back; edit mode keeps its collider, which is what makes the
    // marker selectable in the Scene view.
    void Awake()
    {
        foreach (Collider c in GetComponentsInChildren<Collider>(true))
            c.enabled = false;
    }

    // The marker's editor Y is approximate; the terrain's is the truth.
    void Start()
    {
        if (Ground.Any)
        {
            Vector3 p = transform.position;
            p.y = Ground.HeightAt(p) + transform.localScale.y * 0.5f;
            transform.position = p;
        }
    }

    // Name-sorted for stable indices — LandPlot.territory stores an
    // index into this list, so the order must not depend on scene
    // load order or hierarchy shuffles.
    public static List<CastleSite> Ordered()
    {
        var sites = new List<CastleSite>();
        foreach (CastleSite s in All)
            if (s != null)
                sites.Add(s);
        sites.Sort((a, b) => string.CompareOrdinal(a.SiteName, b.SiteName));
        return sites;
    }
}
