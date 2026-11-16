using System;
using System.Diagnostics;

namespace Hakata.Infrastructure;

/// <summary>Starts a second copy of the app, optionally pinned to another profile (own guard, active-profile pointer untouched).</summary>
public static class Relaunch
{
    /// <summary>Spawns a new process of this executable, optionally pinned to one profile via
    /// <c>--profile</c> (own mutex, no picker, active-profile pointer untouched).
    /// Swallows failure: the caller just keeps running.</summary>
    public static void Self(bool noPicker = false, string? profile = null)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            if (noPicker) start.Environment["HAKATA_NO_PICKER"] = "1";
            if (profile is not null)
                start.ArgumentList.Add("--profile=" + profile);
            // HAKATA_HOME is inherited on purpose: Program ignores --profile when the env var
            // is set, so the child stays in the parent's base dir and the arg cannot misresolve there
            Process.Start(start);
        }
        catch (Exception)
        {
            // a failed relaunch must not crash the app
        }
    }
}
