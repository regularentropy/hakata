using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hakata.Services;

/// <summary>A published release newer than the running build.</summary>
internal sealed record UpdateResponse(string Tag, string Url, string ReleaseNotes);

internal interface IUpdateService
{
    /// <summary>The newer release, or null when this build is current or no feed is configured.</summary>
    Task<UpdateResponse?> CheckForUpdates();
}

/// <summary>
/// Ported from mpvmux: reads the GitHub "latest release" endpoint and compares its tag with the
/// running version. Nothing is downloaded or installed — the release page opens in the browser.
/// </summary>
internal sealed class UpdateService : IUpdateService
{
    readonly AppMetadata metadata;
    readonly HttpClient http;

    public UpdateService(AppMetadata metadata, HttpClient http)
    {
        this.metadata = metadata;
        this.http = http;
    }

    public async Task<UpdateResponse?> CheckForUpdates()
    {
        if (metadata.APILink.Length == 0) return null;

        // the client is the app-wide one shared with provider traffic, so the User-Agent travels on
        // this request instead of becoming a default header every other call would inherit
        using var request = new HttpRequestMessage(HttpMethod.Get, metadata.APILink);
        request.Headers.UserAgent.ParseAdd(metadata.Title);
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The update server returned an unexpected response ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString()?.Trim() ?? "";
        var version = TagVersion(tag);
        // an unreadable tag claims no update instead of failing the whole check
        if (version is null || metadata.Version >= version) return null;

        var notes = doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var url = doc.RootElement.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new UpdateResponse(tag.TrimStart('v', 'V'), url, notes);
    }

    /// <summary>"v1.2.3" or "1.2.3-rc1": only the leading numbers compare, a suffix never throws.</summary>
    static Version? TagVersion(string tag)
    {
        var text = tag.TrimStart('v', 'V');
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        return Version.TryParse(text[..end].TrimEnd('.'), out var version) ? version : null;
    }
}
