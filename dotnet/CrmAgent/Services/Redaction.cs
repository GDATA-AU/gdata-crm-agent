namespace CrmAgent.Services;

/// <summary>Helpers for stripping sensitive data from strings before logging or reporting.</summary>
internal static class Redaction
{
    /// <summary>
    /// Strip query parameters from a URL to avoid logging sensitive values
    /// (API keys, tokens, PII) embedded in query strings.
    /// </summary>
    public static string RedactUrl(string url)
    {
        var idx = url.IndexOf('?');
        return idx >= 0 ? url[..idx] + "?[REDACTED]" : url;
    }
}
