using System;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata;

public static partial class SelfTest
{
    /// <summary>Security-audit entries C1/C2 (2026-11-16): a URL taken from a remote response may
    /// only reach the OS handler after LinkGuard validation. C1: UpdateViewModel.OpenUpdateLink
    /// on the feed's html_url. C2's window wiring is checked in UpdaterTest (--updatertest), which
    /// owns the headless window. The invariant: LinkGuard.OpenExternal refuses non-http(s) schemes
    /// and non-public hosts before any Process.Start.</summary>
    static void SecurityLinks()
    {
        // refused before any dispatch: hostile schemes, local targets, malformed values
        string?[] hostile =
        {
            "file:///etc/hostname", "FILE:///etc/hostname", "file:/etc/hostname", "file://localhost/etc/hostname",
            "javascript:alert(1)", "data:text/html,<script>1</script>", @"\\evil\share\x.exe",
            "https://127.0.0.1/x", "https://[::1]/x", "https://0.0.0.0/", "https://localhost/x",
            "https://host.local/x", "https://host.internal/x", " file:///etc/hostname",
            "file:///etc/hostname\r\nhttps://github.com", "", null, "   ",
        };
        foreach (var h in hostile)
            Check(!LinkGuard.OpenExternal(h), $"OpenExternal refuses [{h ?? "<null>"}] without dispatching");

        // the guard itself accepts the legit shape (public http(s)); no dispatch attempted here
        Check(LinkGuard.IsFetchableLink("https://github.com/regularentropy/hakata", out _),
            "IsFetchableLink accepts a public https URL");
        Check(!LinkGuard.IsFetchableLink("https://127.0.0.1", out _), "IsFetchableLink rejects loopback");
        Check(!LinkGuard.IsFetchableLink("https://user:pass@127.0.0.1/", out _), "IsFetchableLink rejects loopback behind userinfo");

        // C1: the update window's button refuses a feed html_url that is not a public http(s) URL
        var vm = new UpdateViewModel(new UpdateResponse("9.9.9", "file:///etc/hostname", "notes"), new AppMetadata());
        Check(!vm.HasStatus, "update vm: no status before the first open attempt");
        vm.OpenUpdateLinkCommand.Execute(null);
        Check(vm.HasStatus && vm.Status.Length > 0,
            "update vm: a non-http html_url is refused with a status message and never dispatched");
    }
}
