using System;
using System.Net.Http;

namespace Hakata.Services;

/// <summary>The URL derivation rules of the OpenAI-compatible surface: a base URL that already
/// carries a path is a full compat surface, a bare host gets the OpenAI-style /v1 prefix.</summary>
public static class ProviderUrls
{
    public static string CompatBase(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? b[..^"/chat/completions".Length]
            : b;
    }

    /// <summary>True when the base URL already carries a path, i.e. it is a full compat surface.</summary>
    static bool IsPathedBase(string b) =>
        Uri.TryCreate(b, UriKind.Absolute, out var u) && u.AbsolutePath != "/";
    public static string Endpoint(string baseUrl)
    {
        // A base that already has a path (e.g. https://gateway.example/v3/compat) is a full
        // compatibility surface: only /chat/completions is missing. A bare host (Ollama,
        // LM Studio defaults) expects the OpenAI-style /v1 prefix.
        // A trimmed base that already ends in /chat/completions is returned unchanged: a
        // root-level full endpoint (http://host:8080/chat/completions) would otherwise lose
        // its only path segment in CompatBase and get the /v1 prefix re-inserted.
        var t = baseUrl.TrimEnd('/');
        if (t.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return RequireAbsolute(t);
        return CompatUrl(baseUrl, "/chat/completions");
    }

    public static string ModelsUrl(string baseUrl) => CompatUrl(baseUrl, "/models");

    /// <summary>The compat surface of a base URL with one suffix appended: a pathed base or one
    /// already ending in /v1 is taken as-is, a bare host gets the OpenAI-style /v1 prefix.</summary>
    public static string CompatUrl(string baseUrl, string suffix)
    {
        var b = RequireAbsolute(CompatBase(StripQuery(baseUrl)));
        return IsPathedBase(b) || b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? b + suffix
            : b + "/v1" + suffix;
    }

    // an empty or relative base would build a relative request URI, which only throws at SendAsync
    // as InvalidOperationException — past every HttpRequestException handler; fail here instead
    static string RequireAbsolute(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out _)
            ? url
            : throw new HttpRequestException($"The provider base URL '{url}' is not an absolute URL");

    // A pasted base URL may carry a query (?token=...) or a fragment: appending a suffix after it
    // would land the suffix inside the query, so every request would 404 and the pasted token
    // would leak into the request URLs. Both are cleared before anything is appended, and so is a
    // percent-encoded '?'/'#' inside the path, which would ride in the request line the same way;
    // a URL without any of them is returned untouched.
    internal static string StripQuery(string url)
    {
        var t = url.TrimEnd('/');
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return url;
        var cut = CutAtEncodedDelimiter(u.AbsolutePath);
        var b = new UriBuilder(u) { Query = "", Fragment = "" };
        if (cut >= 0) b.Path = u.AbsolutePath[..cut];
        return cut >= 0 || u.Query.Length > 0 || u.Fragment.Length > 0 ? b.Uri.ToString().TrimEnd('/') : url;
    }

    // the first percent-encoded '?' or '#' in a path, or -1 when there is none
    static int CutAtEncodedDelimiter(string path)
    {
        var q = path.IndexOf("%3f", StringComparison.OrdinalIgnoreCase);
        var h = path.IndexOf("%23", StringComparison.OrdinalIgnoreCase);
        return q < 0 ? h : h < 0 ? q : Math.Min(q, h);
    }

    /// <summary>A URL named in settings for a request line (a speech/sound endpoint override):
    /// trimmed and passed through <see cref="StripQuery"/> — a pasted console URL carrying
    /// ?api_key=… must not put its query into the request line, where the target host and every
    /// on-path proxy would log it.</summary>
    internal static string OverrideUrl(string url) => StripQuery(url.Trim());

    /// <summary>Speech-to-text endpoint beside the chat surface; a bare host gets the OpenAI-style /v1 prefix.</summary>
    public static string AudioUrl(string baseUrl) => AudioBase(baseUrl) + "/audio/transcriptions";

    /// <summary>Text-to-speech endpoint beside it, same prefix rules.</summary>
    public static string SpeakUrl(string baseUrl) => AudioBase(baseUrl) + "/audio/speech";

    /// <summary>The audio model catalog beside it, when the provider publishes one (`/audio/models`, optional
    /// ?type= filter).</summary>
    public static string AudioModelsUrl(string baseUrl, string? type = null) =>
        AudioBase(baseUrl) + "/audio/models" + (type is null ? "" : "?type=" + Uri.EscapeDataString(type));

    static string AudioBase(string baseUrl)
    {
        // a `/compat` surface mirrors chat completions only; the audio routes sit on the native base
        // beside it, so the segment has to go before anything is appended
        var b = CompatBase(baseUrl);
        if (b.EndsWith("/compat", StringComparison.OrdinalIgnoreCase)) b = b[..^"/compat".Length];
        return CompatUrl(b, "");
    }
}
