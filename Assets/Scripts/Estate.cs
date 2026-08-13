using System.Collections.Generic;
using UnityEngine;

// The kinds of wall the builder can raise — the bottom rungs of the
// wall-type cost ladder (palisade → stone → fortified, design doc "The
// Estate Economy"). A kind is STORED on its edge: it decides material and
// price, never geometry, so surgery copies it and nothing rebuilds over it.
public enum WallKind
{
    Stone = 0,
    Palisade = 1,
}

// The treasury, in real money: pounds, shillings, pence, stored as whole
// PENCE (£1 = 20s = 240d — the research price book's magnitudes,
// Docs/Economy1220.md). This is the "make it cost" slice: every commit
// deducts, and the ghost readout shows the price before the click.
//
// Two deliberate v1 simplifications, both recorded in the design doc:
// construction is INSTANT (testing first — build time is a later feature),
// and there is no refusal and no salvage loss — the treasury can go into
// debt (period-true: the 13th-century credit market ran at 43%/yr), and
// any deletion refunds in full. Cost is therefore the price of what
// STANDS: build-then-delete rounds the treasury back to where it began
// (to within the pennies of re-sectioning), which is the right economy
// for a builder whose construction is still instant and undoable.
public static class Estate
{
    public const long PencePerShilling = 12;
    public const long PencePerPound = 240;

    // The inheritance a new game starts with — a shabby lord's cash on
    // hand. Loads overwrite it (the treasury is a stored fact and rides
    // CastleSave); pre-treasury saves get this same fresh start.
    public const long StartingPence = 100 * PencePerPound;

    public static long TreasuryPence = StartingPence;

    // Masonry rates per cubic meter. Stone at 3s/m³ prices a 100m circuit
    // at standard height around £50 and a serious castle in the hundreds
    // to £1,000+ — the research ladder's shape. Palisade timber is 6× less
    // per volume (£20-a-circuit territory).
    const long StoneRate = 36;
    const long PalisadeRate = 6;
    // Foundations are rubble and fill — unskilled labor, well under even
    // palisade timber per volume, so a pad costs less than the cheapest
    // ring of walls that stands on it (user call, 2026-08-10: at 18 a
    // pad out-priced its own palisade). Floors are timber decks, priced
    // by area not volume.
    const long FoundationRate = 4;
    const long FloorRatePerArea = 6;
    // Cutting a doorway (or walling one back up) is labor, not masonry.
    public const long DoorFee = 2 * PencePerShilling;

    // A fresh play run starts from the inheritance even when the editor
    // skips domain reload; a load then applies whatever the save stored.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetOnPlay() { TreasuryPence = StartingPence; Households = 12; }

    public static long RateFor(WallKind kind)
        => kind == WallKind.Palisade ? PalisadeRate : StoneRate;

    public static string KindLabel(WallKind kind)
        => kind == WallKind.Palisade ? "Palisade" : "Stone wall";

    // Gentle £/s/d display (design doc: "about £3", not "£2 17s 4d"):
    // big sums round to the pound, middling sums drop the pence, small
    // sums say what they are.
    public static string Format(long pence)
    {
        string sign = pence < 0 ? "-" : "";
        long d = System.Math.Abs(pence);
        long pounds = d / PencePerPound;
        long rem = d % PencePerPound;
        long shillings = rem / PencePerShilling;
        long pennies = rem % PencePerShilling;
        if (pounds >= 20)
            return $"{sign}£{pounds + (rem >= PencePerPound / 2 ? 1 : 0)}";
        if (pounds > 0)
            return shillings > 0 ? $"{sign}£{pounds} {shillings}s" : $"{sign}£{pounds}";
        if (shillings > 0)
            return pennies > 0 ? $"{sign}{shillings}s {pennies}d" : $"{sign}{shillings}s";
        return $"{sign}{pennies}d";
    }

    // ------------------------------------------------------------------
    // Pricing. Everything prices from STORED facts (sections, spans,
    // outlines), so a refund computed from the same object equals the
    // charge — the full-refund invariant needs no ledger.
    // ------------------------------------------------------------------

    // One wall section's masonry volume — arc length × height × thickness.
    static float SectionVolume(WallEdge edge, WallEdgeSection sec, float[] arcTable)
    {
        float arc = WallEdge.ArcAt(arcTable, sec.tEnd) - WallEdge.ArcAt(arcTable, sec.tStart);
        return Mathf.Max(0f, arc) * Mathf.Max(0f, sec.topY - sec.bottomY) * edge.thickness;
    }

    public static long CostOfEdge(WallEdge edge)
    {
        if (edge == null)
            return 0;
        float[] table = WallEdge.BuildArcTable(edge.A, edge.control, edge.B);
        float volume = 0f;
        foreach (WallEdgeSection sec in edge.SectionsInOrder())
            volume += SectionVolume(edge, sec, table);
        return (long)System.Math.Round(volume * RateFor(edge.kind));
    }

    public static long CostOfEdges(IEnumerable<WallEdge> edges)
    {
        long total = 0;
        if (edges != null)
            foreach (WallEdge e in edges)
                total += CostOfEdge(e);
        return total;
    }

    // The doomed subset of an edge's sections — the delete tool's refund.
    public static long CostOfSections(WallEdge edge, ICollection<int> indices)
    {
        if (edge == null || indices == null || indices.Count == 0)
            return 0;
        float[] table = WallEdge.BuildArcTable(edge.A, edge.control, edge.B);
        float volume = 0f;
        foreach (WallEdgeSection sec in edge.SectionsInOrder())
            if (indices.Contains(sec.index))
                volume += SectionVolume(edge, sec, table);
        return (long)System.Math.Round(volume * RateFor(edge.kind));
    }

    // A preview's price, from the same specs the ghosts show — ghost =
    // commit extends to the cost readout.
    public static long CostOfSpecs(IEnumerable<WallEdge.SectionSpec> specs,
        float thickness, WallKind kind)
    {
        if (specs == null)
            return 0;
        float volume = 0f;
        foreach (WallEdge.SectionSpec spec in specs)
            volume += Mathf.Max(0f, spec.arcLength)
                * Mathf.Max(0f, spec.topY - spec.bottomY) * thickness;
        return (long)System.Math.Round(volume * RateFor(kind));
    }

    // A stair is a stone wedge: the run rises from nothing to the full
    // climb (half a prism), the landing carries the full climb flat.
    public static long CostOfStair(WallStair stair)
    {
        if (stair == null)
            return 0;
        float rise = Mathf.Max(0f, stair.Rise);
        float run = Mathf.Max(0f, stair.runArc);
        float landing = Mathf.Max(0f, stair.TotalArc - stair.runArc);
        float volume = stair.width * (run * rise * 0.5f + landing * rise);
        return (long)System.Math.Round(volume * StoneRate);
    }

    // A foundation is priced by its prism volume (rubble to the skirted
    // bottom), a floor by its deck area less its stairwells.
    public static long CostOfSlab(SlabTile tile)
    {
        if (tile == null || tile.verts == null || tile.verts.Count < 3)
            return 0;
        float area = Mathf.Abs(SlabTile.SignedArea(tile.verts));
        if (tile.isFoundation)
            return (long)System.Math.Round(
                area * Mathf.Max(0f, tile.topY - tile.bottomY) * FoundationRate);
        foreach (SlabTile.Well well in tile.wells)
            if (well != null && well.verts != null && well.verts.Count >= 3)
                area -= Mathf.Abs(SlabTile.SignedArea(well.verts));
        return (long)System.Math.Round(Mathf.Max(0f, area) * FloorRatePerArea);
    }

    // ------------------------------------------------------------------
    // The two transactions. Both announce in the BuildLog with the new
    // balance, so money is legible without a spreadsheet in sight.
    // ------------------------------------------------------------------

    public static void Pay(long pence, string what)
    {
        if (pence <= 0)
            return;
        bool wasSolvent = TreasuryPence >= 0;
        TreasuryPence -= pence;
        BuildLog.Add($"{what} — {Format(pence)}. Treasury {Format(TreasuryPence)}.");
        if (wasSolvent && TreasuryPence < 0)
            BuildLog.Add("The treasury is empty — the masons build on credit.");
    }

    public static void Refund(long pence, string what)
    {
        if (pence <= 0)
            return;
        TreasuryPence += pence;
        BuildLog.Add($"{what} — {Format(pence)} back. Treasury {Format(TreasuryPence)}.");
    }

    // ------------------------------------------------------------------
    // The income side (2026-08-10). Owned parcels pay on the quarter
    // days: produce from the land by type and area, dues from the people
    // by household. Michaelmas carries the harvest's weight. Calibrated
    // so the starting demesne yields a knight's £10-20 a year — the
    // research ladder's bottom rung.
    // ------------------------------------------------------------------

    // The home manor's population, one number by design (the user's
    // call): it absorbs growth, attraction and flight until any of them
    // needs its own machinery. Saved in v6.
    public static int Households = 12;

    // Pence per m² per quarter at even weighting. A ~3,600 m² farm
    // parcel is worth ~9s a quarter; timber is standing wealth, not
    // rent, so it trickles.
    const float FarmQuarterRate = 0.030f;
    const float PastureQuarterRate = 0.020f;
    const float TimberQuarterRate = 0.008f;
    // Dues per household per quarter (living parcels host them).
    const long HouseholdQuarterDues = 18;
    // Michaelmas takes the fat quarter; the other three split lean.
    // Weights sum to 4, so the year's total is rate × 4.
    const float MichaelmasWeight = 1.6f;
    const float LeanWeight = 0.8f;

    public static long QuarterIncome(bool michaelmas)
    {
        float weight = michaelmas ? MichaelmasWeight : LeanWeight;
        double produce = 0;
        bool anyLiving = false;
        foreach (LandPlot p in LandPlot.All)
        {
            if (p == null || p.owner != LandOwner.Player)
                continue;
            switch (p.type)
            {
                case LandType.Farm: produce += p.AreaM2 * FarmQuarterRate; break;
                case LandType.Pasture: produce += p.AreaM2 * PastureQuarterRate; break;
                case LandType.Timber: produce += p.AreaM2 * TimberQuarterRate; break;
                // Castle grounds yield nothing — only a VILLAGE brings
                // the households' dues.
                case LandType.Living: anyLiving = true; break;
            }
        }
        long dues = anyLiving ? Households * HouseholdQuarterDues : 0;
        return (long)System.Math.Round((produce + dues) * weight);
    }

    public static void CollectQuarter(string name, bool michaelmas)
    {
        long pence = QuarterIncome(michaelmas);
        if (pence <= 0)
        {
            BuildLog.Add($"{name} — the estate owes you nothing yet.");
            return;
        }
        TreasuryPence += pence;
        BuildLog.Add($"{name} — {Format(pence)} in rents and produce."
            + $" Treasury {Format(TreasuryPence)}.");
    }

    // What a parcel is worth a year at even weighting — the base for
    // both prices. Buying peaceful land runs six years' purchase (kind
    // to the player against the period's ten-to-twenty); driving
    // bandits off the frontier is cheaper in coin, and will cost blood
    // instead once combat exists.
    public static long AnnualYieldOf(LandPlot plot)
    {
        double year = plot.type switch
        {
            LandType.Farm => plot.AreaM2 * FarmQuarterRate * 4,
            LandType.Pasture => plot.AreaM2 * PastureQuarterRate * 4,
            LandType.Timber => plot.AreaM2 * TimberQuarterRate * 4,
            // Castle grounds earn nothing and are priced by nothing —
            // they change hands with the castle (the click refuses).
            LandType.Castle => 0,
            _ => Households * HouseholdQuarterDues * 4.0,
        };
        return (long)System.Math.Round(year);
    }

    public static long PriceOf(LandPlot plot)
        => plot.owner == LandOwner.Bandits
            ? AnnualYieldOf(plot) * 3 / 2
            : AnnualYieldOf(plot) * 6;
}
