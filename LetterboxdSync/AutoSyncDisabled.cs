namespace LetterboxdSync;

/// <summary>
/// Fork: automatic posting (playback events, rating pushes, the scheduled "sync watched movies"
/// and Serializd catch-up runs) is compiled out of reach. Constant on purpose: there is no setting
/// that turns it back on.
/// </summary>
internal static class AutoSyncDisabled
{
    internal static readonly bool Value = true;
}
