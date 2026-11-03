namespace SwitchDesk.Core;

/// <summary>Observes a Steam restart; never claims that an arbitrary game-session change came from the cloud.</summary>
public sealed class SteamCloudGuard
{
    private bool initialized, steamWasRunning;
    private readonly HashSet<string> observedGame = new(StringComparer.Ordinal);
    private bool trackedRestart;
    public void Observe(bool steamRunning, IReadOnlyDictionary<string, bool> games)
    {
        if (!initialized) { initialized = true; steamWasRunning = steamRunning; return; }
        if (steamRunning && !steamWasRunning) { trackedRestart = true; observedGame.Clear(); }
        foreach (var pair in games) if (pair.Value) observedGame.Add(pair.Key);
        steamWasRunning = steamRunning;
    }
    public bool NeedsReview(string adapterId, bool gameCurrentlyRunning, string previousFingerprint, string currentFingerprint)
        => trackedRestart && !gameCurrentlyRunning && !observedGame.Contains(adapterId) && !Fingerprints.Equal(previousFingerprint, currentFingerprint);
}
