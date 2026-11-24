// Local modification (not upstream's): the mod names its own build in the server log at start, so a
// host still running an older copy of the mod folder shows it. The mod reaches a server as a folder
// copied by hand, and a fix merged here does nothing until that folder is replaced (on 2026-11-20
// stage and prod both still ran a build from before the `/dn` command was dropped).
//
// The build is the assembly's informational version: the csproj's <Version> plus `+<commit sha>`,
// which the .NET SDK appends from git (Source Link) whenever the build runs inside a git checkout,
// as CI's does. A pull-request CI artifact carries the sha of the test merge commit GitHub built,
// not the PR head; push, schedule and dispatch artifacts carry the commit itself.

using System.Reflection;

namespace Dereth.Network;

public sealed partial class Mod
{
    /// <summary>This build's version and commit, e.g. `0.1.5-fork.1+8f1b0128...`; the bare version when no commit was recorded.</summary>
    internal static string BuildVersion =>
        typeof(Mod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Mod).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
