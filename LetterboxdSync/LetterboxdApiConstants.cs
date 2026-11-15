namespace LetterboxdSync;

internal static class LetterboxdApiConstants
{
    // Fork: JELLYSCRIBE_LETTERBOXD_API_URL points the official-API client at another server
    // (used to test against a local fake that records requests instead of a real account).
    internal static readonly string BaseUrl =
        (System.Environment.GetEnvironmentVariable("JELLYSCRIBE_LETTERBOXD_API_URL") is { Length: > 0 } u ? u.TrimEnd('/') : null)
        ?? "https://api.letterboxd.com/api/v0";

    internal const string ApiKey = "ebe3d27ec52a35fc8d1835c6531c37bd72b7a54337666d5bd759379b72ae16f0";

    internal const string ApiSecret = "c60ce045d25bc90cb56026a8dd621eebeef995cbecc51951192da75348c977cd";
}
