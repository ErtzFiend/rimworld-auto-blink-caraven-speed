using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AutoBlinkCaravenSpeed;

/// <summary>
/// Patches the innermost shared travel-time overload:
/// GetTicksPerMove(List&lt;Pawn&gt;, float massUsage, float massCapacity,
/// bool isShuttle, StringBuilder explanation).
///
/// Both public entry points funnel through it:
///   GetTicksPerMove(Caravan)              -> CaravanInfo(Caravan)      -> list overload
///   GetTicksPerMove(CaravanInfo)          -> list overload
///   Dialog_FormCaravan preview            -> CaravanInfo(dialog)       -> list overload
/// Patching exactly one overload therefore gives one multiplication for the
/// live caravan number and for the formation preview, with no re-entrant
/// scaling between overloads. Carrying is modelled from the prospective pawn
/// list because this overload has no Caravan / carry tracker (the preview does
/// not have one either).
///
/// The postfix only ever shortens an already positive, non-shuttle result and
/// leaves the value untouched when the rule fails, so vanilla and sentinel
/// values (0, negative, 1 tick) are preserved.
/// </summary>
[HarmonyPatch(
    typeof(CaravanTicksPerMoveUtility),
    nameof(CaravanTicksPerMoveUtility.GetTicksPerMove),
    new[] { typeof(List<Pawn>), typeof(float), typeof(float), typeof(bool), typeof(StringBuilder) })]
public static class CaravanTicksPerMovePatch
{
    /// <summary>
    /// True when the rule would shorten this pawn list's travel time: at least
    /// one walking member and every walker carries an active AutoBlink source.
    /// Exposed so the postfix and the tests share exactly one predicate.
    /// </summary>
    public static bool Applies(IReadOnlyList<Pawn> pawns) => Applies(pawns, out _);

    public static bool Applies(IReadOnlyList<Pawn> pawns, out int walkers)
    {
        walkers = 0;
        if (pawns == null || pawns.Count == 0) return false;
        return Eligibility.AllWalkersBlink(
            pawns,
            VanillaPawnSignals.Instance,
            Capability.HasBlink,
            out walkers);
    }

    public static void Postfix(
        List<Pawn> pawns,
        bool isShuttle,
        StringBuilder explanation,
        ref int __result)
    {
        if (isShuttle) return;                 // vanilla returns 0 for shuttles
        if (__result <= 1) return;             // nothing meaningful to accelerate

        if (!Applies(pawns, out int walkers))
            return;

        int scaled = Policy.Scale(__result);
        if (scaled >= __result) return;

        __result = scaled;
        if (explanation != null)
        {
            // Vanilla has already appended its own (pre-mod) final speed line.
            // Label the relationship explicitly instead of implying the mod
            // recalculated that earlier line.
            float tilesPerDay = 60000f / __result;
            explanation.Append("\n  Auto-Blink Caraven Speed: ")
                .Append(walkers)
                .Append(" walking member(s), all with an active AutoBlink source; previous estimate x")
                .Append(Policy.Multiplier.ToString("0.###"))
                .Append(" (vanilla total above is pre-mod) -> ")
                .Append(tilesPerDay.ToString("0.#"))
                .Append(" tiles/day");
        }
    }
}