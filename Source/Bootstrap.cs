using System.Reflection;
using HarmonyLib;
using Verse;

namespace AutoBlinkCaravenSpeed;

/// <summary>
/// Installs the single caravan-travel result patch at game startup.
///
/// Kept separate from <see cref="Bootstrap"/> so tests can exercise the exact
/// PatchAll code path without relying on a running game to invoke the
/// [StaticConstructorOnStartup] constructor.
/// </summary>
public static class PatchInstaller
{
    public const string HarmonyId = "ertzfiend.autoblinkcaravenspeed";

    private static readonly Harmony Harmony = new(HarmonyId);
    private static bool applied;

    public static bool IsApplied => applied;

    /// <summary>Idempotent: a second call is a no-op, so a test/startup race cannot double-patch.</summary>
    public static void Apply()
    {
        if (applied) return;
        Harmony.PatchAll(Assembly.GetExecutingAssembly());
        applied = true;
    }

    /// <summary>Removes only this mod's patches. Used by the headless test harness.</summary>
    public static void Remove()
    {
        Harmony.UnpatchAll(HarmonyId);
        applied = false;
    }
}

[StaticConstructorOnStartup]
internal static class Bootstrap
{
    // RimWorld runs static constructors decorated with this attribute after
    // defs and mods are loaded, i.e. after AutoBlink has converted legacy defs.
    static Bootstrap() => PatchInstaller.Apply();
}