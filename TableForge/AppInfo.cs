using System.IO;
using System.Reflection;

namespace TableForge;

/// <summary>What the running build calls itself.</summary>
public static class AppInfo
{
    /// <summary>The product version, e.g. "1.0.0-rc1" (any "+build" suffix added by tooling is dropped).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];

    /// <summary>
    /// When this build's main assembly was actually written to disk. Read from the DLL's own file timestamp rather
    /// than a version number, so it stays accurate without a separate manual bump: two builds that share the same
    /// <see cref="Version"/> (nothing in the .csproj changed) still show which one is actually running.
    /// </summary>
    public static DateTime BuildTimeUtc { get; } =
        File.Exists(typeof(AppInfo).Assembly.Location) ? File.GetLastWriteTimeUtc(typeof(AppInfo).Assembly.Location) : default;

    /// <summary>Version plus build time, for a status bar or About area: "1.0.0-rc7 · built 2026-09-22 14:30 local".</summary>
    public static string VersionAndBuild => BuildTimeUtc == default
        ? Version
        : $"{Version} · built {BuildTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm} local";

    public static string Title => "TableForge — RPG Rollable Tables";
}
