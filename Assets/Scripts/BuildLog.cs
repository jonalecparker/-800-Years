using System.Collections.Generic;

// Tiny on-screen feed of build-system outcomes: why a room came out
// floor-only, why a gesture was refused, what a breach did. Static and
// renderer-agnostic — BuildMenu draws it (collapsible, top-left) by
// polling Version.
public static class BuildLog
{
    public static readonly List<string> Entries = new List<string>();
    public static int Version { get; private set; }

    const int MaxEntries = 30;

    public static void Add(string message)
    {
        Entries.Add(message);
        if (Entries.Count > MaxEntries)
            Entries.RemoveAt(0);
        Version++;
    }

    public static void Clear()
    {
        Entries.Clear();
        Version++;
    }
}
