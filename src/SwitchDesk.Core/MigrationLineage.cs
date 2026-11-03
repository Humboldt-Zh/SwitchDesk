namespace SwitchDesk.Core;

public static class MigrationLineage
{
    public static SaveReading CarryOriginalTime(SaveReading current, string pairId, SavePlatform platform, IEnumerable<MigrationOperation> history)
    {
        var imported = history.Where(o => o.PairId == pairId && o.Source != platform && o.State == MigrationState.Complete && Fingerprints.Equal(o.OutputFingerprint, current.Fingerprint))
            .OrderByDescending(o => o.CompletedAt ?? o.CreatedAt).FirstOrDefault();
        if (imported?.SourceReading == null) return current;
        return current with { SavedAt = imported.SourceReading.SavedAt, TimeSource = imported.SourceReading.SavedAt == null ? SaveTimeSource.ObservedOnly : SaveTimeSource.MigratedSource, MigratedAt = imported.CompletedAt ?? imported.CreatedAt };
    }
}
