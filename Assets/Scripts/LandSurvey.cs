using UnityEngine;

// The survey's tuning dials, in the inspector (the user's ask,
// 2026-08-12). These feed LandParcels.EnsureGenerated at generation
// time ONLY — parcels are stored facts, so turning a dial changes
// nothing until the next fresh survey (a new play run with no save
// loaded, or the Resurvey context menu below). A scene without this
// component gets the script defaults.
//
// surveyReach 0 means SURVEY EVERYTHING: every cell on the map gets a
// parcel and one lord's land butts against the next at the Voronoi
// seam. Mind the cell size when you do — 60m cells over the 15km
// Marches is ~62,000 parcels, which is GameObject soup; ~150–200m
// cells (a furlong — the real open-field unit) keep full coverage
// under ~10,000. EnsureGenerated refuses over MaxPlots and says so.
public class LandSurvey : MonoBehaviour
{
    public static LandSurvey Instance;

    [Header("Fresh surveys only — Resurvey to apply now")]
    public float cellSize = 60f;
    [Tooltip("Parcels exist within this reach of each castle. 0 = survey the whole map; lordships butt at the seams.")]
    public float surveyReach = 1000f;

    [Tooltip("Tree spacing when planting Timber parcels (TimberWoods). Under 2 disables planting.")]
    public float timberTreeSpacing = 12f;

    [Header("Castle grounds — terrain-fitted polygon around each site")]
    [Tooltip("The grounds reach at least this far from the site whatever the terrain.")]
    public float castleGroundsMin = 60f;
    [Tooltip("The grounds never reach farther than this.")]
    public float castleGroundsMax = 140f;
    [Tooltip("The grounds end where the land rises or falls this many meters from the site's own ground — the platform edge.")]
    public float castleGroundsRelief = 6f;

    [Header("Settled demesne (village in acres; fields & pasture in plots)")]
    [Tooltip("The town grows out from the castle gate until it holds this many acres — the village flood's budget.")]
    public float villageAcres = 15f;
    public int farmPlots = 5;
    public int pasturePlots = 3;

    void OnEnable() { Instance = this; }
    void OnDisable() { if (Instance == this) Instance = null; }

    // Tears the survey down and runs it fresh with the dials as they
    // stand. Play mode only (the survey needs the terrain and the
    // sites). DESTROYS ownership — cleared and bought land goes back
    // to its generated state; this is a design-tuning tool, not a
    // gameplay one. DestroyImmediate because EnsureGenerated runs in
    // the same breath and deferred Destroy would leave LandPlot.All
    // populated (the play-mode delete-then-rebuild gotcha).
    [ContextMenu("Resurvey (play mode)")]
    void Resurvey()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Resurvey is a play-mode tool.");
            return;
        }
        foreach (LandPlot p in LandPlot.All.ToArray())
            if (p != null)
                DestroyImmediate(p.gameObject);
        // The disk cache would hand back the identical survey —
        // Resurvey means "run it fresh with the dials as they stand".
        LandParcels.InvalidateCache();
        LandParcels.EnsureGenerated();
        BuildLog.Add("Resurveyed — ownership reset to the fresh state."
            + " Reopen the Survey to see it.");
    }
}
