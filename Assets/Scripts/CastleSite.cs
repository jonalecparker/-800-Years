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
