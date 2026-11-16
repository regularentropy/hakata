using System;
using System.Reflection;

namespace Hakata.Services;

/// <summary>
/// What the About page and the update check need to know about this build. Ported from mpvmux: the
/// links, the owner and the version all come from the project file and the assembly, so a release
/// bumps one number there.
/// </summary>
internal sealed class AppMetadata
{
    /// <summary>GitHub API host; the release feed is derived from it.</summary>
    const string APIHost = "https://api.github.com";
    /// <summary>Display name of the program: the assembly's Product, so the project file names it once.</summary>
    public string Title { get; init; }

    /// <summary>Name of the repository, which is not the display name: the update feed is keyed by it.</summary>
    public string RepositoryName { get; init; }

    /// <summary>Copyright holder: the assembly company, so the project file is the one place it lives.</summary>
    public string Author { get; init; }

    /// <summary>Version of the program, always three components so it prints as one line.</summary>
    public Version Version { get; init; }

    /// <summary>Repository home page; empty disables the link on the About page.</summary>
    public string RepositoryLink { get; init; }

    /// <summary>GitHub "latest release" API the check reads; empty disables the check.</summary>
    public string APILink { get; init; }

    public AppMetadata()
    {
        const string owner = "regularentropy";
        var assembly = Assembly.GetExecutingAssembly();

        // the display name and the repository name are allowed to differ: renaming the app must not
        // silently repoint the release feed, and the assembly name is what the data dir hangs off
        RepositoryName = assembly.GetName().Name ?? "Hakata";
        Title = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product is { Length: > 0 } product
            ? product
            : RepositoryName;
        Author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? owner;
        var version = assembly.GetName().Version ?? new Version(1, 0, 0);
        Version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        RepositoryLink = $"https://github.com/{owner}/";
        APILink = $"{APIHost}/repos/{owner}/{RepositoryName}/releases/latest";
    }
}
