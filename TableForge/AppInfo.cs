using System.Reflection;

namespace TableForge;

/// <summary>What the running build calls itself.</summary>
public static class AppInfo
{
    /// <summary>The product version, e.g. "1.0.0-rc1" (any "+build" suffix added by tooling is dropped).</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];

    public static string Title => "TableForge — RPG Rollable Tables";
}
