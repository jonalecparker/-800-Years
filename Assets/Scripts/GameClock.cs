using UnityEngine;

// The estate's calendar. Real time geared down — a game day passes in a
// real minute at 1× — with a player-facing speed dial for impatient
// testing (the user's call, 2026-08-10: slow base rate, slider to hurry
// it). Income arrives on the four quarter days, Michaelmas the climax;
// the clock's job is only to know the date and announce those days.
// DaysElapsed is the ONE stored fact (CastleSave v6); the date, the
// season and the next payday all derive from it. Deliberately does NOT
// stand down in walk mode or under the Saves panel — time passes while
// you walk your lands. BuildMenu drives Tick and owns the readout.
public static class GameClock
{
    // Real seconds per game day at 1×. Sixty makes a season a sitting
    // and the year's rhythm legible without being a wait.
    public const float SecondsPerDay = 60f;
    public const float MaxSpeed = 120f;
    public static float Speed = 1f;

    // Days since 25 March 1220 — Lady Day, the medieval new year. It is
    // itself a quarter day, but day zero pays nothing: the inheritance
    // IS Lady Day's settlement, and the first payday is Midsummer.
    public static double DaysElapsed;

    const int StartYear = 1220;
    const int StartDayOfYear = 84; // 25 March; 365-day calendar, no leap

    // Day-of-year of the four quarter days: Lady Day (25 Mar),
    // Midsummer (24 Jun), Michaelmas (29 Sep), Christmas (25 Dec).
    static readonly int[] QuarterDoy = { 84, 175, 272, 359 };
    static readonly string[] QuarterName =
        { "Lady Day", "Midsummer", "Michaelmas", "Christmas" };

    static readonly string[] MonthName = { "January", "February", "March",
        "April", "May", "June", "July", "August", "September", "October",
        "November", "December" };
    static readonly int[] MonthLen = { 31, 28, 31, 30, 31, 30, 31, 31,
        30, 31, 30, 31 };

    // A fresh play run starts at Lady Day 1220 even when the editor
    // skips domain reload; a load then applies whatever the save stored.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ResetOnPlay() { DaysElapsed = 0; Speed = 1f; }

    // A load sets the day directly — no back-payments for the quarters
    // the save skipped over; they were paid (or not) in the saved run.
    public static void SetElapsed(double days)
    {
        DaysElapsed = days < 0 ? 0 : days;
    }

    // Advance and pay every quarter day the elapsed span crossed. A
    // fast-forwarded frame can cross more than one; each pays in order,
    // so cranking the dial never skips a payday.
    public static void Tick(float realDelta)
    {
        double before = DaysElapsed;
        DaysElapsed += realDelta * Speed / SecondsPerDay;
        for (long day = (long)before + 1; day <= (long)DaysElapsed; day++)
        {
            int doy = DoyOf(day);
            for (int q = 0; q < QuarterDoy.Length; q++)
                if (doy == QuarterDoy[q])
                    Estate.CollectQuarter(QuarterName[q], michaelmas: q == 2);
        }
    }

    static int DoyOf(long daysElapsed)
    {
        long absolute = StartDayOfYear - 1 + daysElapsed; // 0-based from 1 Jan 1220
        return (int)(absolute % 365) + 1;
    }

    public static string DateLine()
    {
        long whole = (long)DaysElapsed;
        long absolute = StartDayOfYear - 1 + whole;
        int year = StartYear + (int)(absolute / 365);
        int doy = (int)(absolute % 365) + 1;
        int month = 0;
        while (doy > MonthLen[month])
        {
            doy -= MonthLen[month];
            month++;
        }
        return $"{doy} {MonthName[month]} {year}";
    }

    // "Michaelmas in 12 days" — the countdown that makes the seasonal
    // rhythm legible on the HUD.
    public static string NextQuarterLine()
    {
        long whole = (long)DaysElapsed;
        int doy = DoyOf(whole);
        int bestWait = int.MaxValue;
        string name = "";
        for (int q = 0; q < QuarterDoy.Length; q++)
        {
            int wait = QuarterDoy[q] - doy;
            if (wait <= 0)
                wait += 365;
            if (wait < bestWait)
            {
                bestWait = wait;
                name = QuarterName[q];
            }
        }
        return bestWait == 1 ? name + " tomorrow" : $"{name} in {bestWait} days";
    }
}
