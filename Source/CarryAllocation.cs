using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AutoBlinkCaravenSpeed;

/// <summary>
/// The pawn facts needed to reproduce 1.7's caravan carry allocation.
/// Split out from Pawn so the allocation mirror can be tested with controlled
/// signals; <see cref="VanillaPawnSignals"/> is the production implementation.
/// </summary>
public interface ICarrySignals
{
    bool IsHumanlike(Pawn pawn);
    bool CanManipulate(Pawn pawn);

    // These three can be *unknown* for a partially initialised or modded pawn,
    // and a wrong "false" is the dangerous direction: it would turn a patient
    // (or an unavailable pawn) into a carrier and wrongly exempt a walker.
    // They therefore report whether the answer is actually known.
    bool TryIsDowned(Pawn pawn, out bool downed);
    bool TryInMentalState(Pawn pawn, out bool inMentalState);
    bool TryWantsToBeCarried(Pawn pawn, out bool wants);
}

/// <summary>
/// Reads the same predicates Caravan_CarryTracker reads from a live pawn.
/// Every method is public so the headless harness can patch individual
/// predicates when it cannot construct a full health/def environment.
/// Failures are conservative: an unknown manipulation/humanlike fact never
/// turns a walking member into a passenger, and the three status predicates
/// that could wrongly create a passenger report "unknown" so the whole
/// eligibility decision can fail closed.
/// </summary>
public sealed class VanillaPawnSignals : ICarrySignals
{
    public static readonly VanillaPawnSignals Instance = new VanillaPawnSignals();

    public bool IsHumanlike(Pawn pawn)
    {
        try { return pawn?.RaceProps?.Humanlike == true; }
        catch (Exception) { return false; }
    }

    public bool TryIsDowned(Pawn pawn, out bool downed)
    {
        downed = false;
        try { downed = pawn?.Downed == true; return true; }
        catch (Exception) { return false; }
    }

    public bool TryInMentalState(Pawn pawn, out bool inMentalState)
    {
        inMentalState = false;
        try { inMentalState = pawn?.InMentalState == true; return true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Manipulation is required to *carry* a patient, not to walk. Unknown
    /// capability therefore removes a candidate carrier (which keeps the
    /// patient a walker) instead of silently granting the bonus.
    /// </summary>
    public bool CanManipulate(Pawn pawn)
    {
        try
        {
            return pawn?.health?.capacities != null
                && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Exactly Caravan_CarryTracker.WantsToBeCarried: the player-facing
    /// "be carried if sick" setting AND vanilla's bed-rest benefit predicate.
    /// </summary>
    public bool TryWantsToBeCarried(Pawn pawn, out bool wants)
    {
        wants = false;
        try
        {
            if (pawn?.health == null) return true; // known: no health tracker, never a patient
            if (!pawn.health.beCarriedByCaravanIfSick) return true;
            wants = CaravanCarryUtility.WouldBenefitFromBeingCarried(pawn);
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// Faithful, non-mutating mirror of Caravan_CarryTracker.RecalculateCarriedPawns.
///
/// Vanilla builds the list of pawns who can carry in caravan order, then walks
/// the caravan once and assigns each would-be patient to the *last* still free
/// carrier (RemoveLast). That finite, ordered, one-patient-per-carrier
/// allocation is reproduced here without touching the real tracker, which is
/// cleared while a caravan is stationary and can be stale at movement start.
/// </summary>
public static class CarryAllocation
{
    /// <summary>
    /// Allocation only, for callers that want to inspect it. Returns an empty
    /// array when a fact is unknown; use <see cref="TryCompute"/> to tell an
    /// empty caravan apart from an undecidable one.
    /// </summary>
    public static bool[] Compute(IReadOnlyList<Pawn> pawns, ICarrySignals signals) =>
        TryCompute(pawns, signals, out bool[] carried) ? carried : Array.Empty<bool>();

    /// <summary>
    /// Allocation that reports whether it could be decided. False means at
    /// least one downed/mental/wants fact threw, and the caller must treat the
    /// caravan as ineligible rather than guess a passenger.
    /// </summary>
    public static bool TryCompute(IReadOnlyList<Pawn> pawns, ICarrySignals signals, out bool[] carried)
    {
        carried = Array.Empty<bool>();
        if (pawns == null || signals == null) return false;

        bool[] wants = new bool[pawns.Count];
        List<int> carriers = new List<int>();

        // Pass 1: who can carry? (humanlike, conscious, able, not themselves a patient)
        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            if (pawn == null) continue;
            if (!signals.TryWantsToBeCarried(pawn, out wants[i])) return false;
            if (!signals.IsHumanlike(pawn)) continue;
            if (!signals.TryIsDowned(pawn, out bool downed)) return false;
            if (downed) continue;
            if (!signals.TryInMentalState(pawn, out bool inMentalState)) return false;
            if (inMentalState) continue;
            if (!signals.CanManipulate(pawn)) continue;
            if (wants[i]) continue;
            carriers.Add(i);
        }

        // Pass 2: assign patients in caravan order to the last free carrier.
        carried = new bool[pawns.Count];
        for (int i = 0; i < pawns.Count; i++)
        {
            if (!wants[i]) continue;
            if (carriers.Count == 0) break; // vanilla stops assigning once nobody can carry
            carried[i] = true;
            carriers.RemoveAt(carriers.Count - 1);
        }

        return true;
    }
}

/// <summary>Combines carry allocation with the all-walkers-blink rule.</summary>
public static class Eligibility
{
    public static bool AllWalkersBlink(
        IReadOnlyList<Pawn> pawns,
        ICarrySignals signals,
        Func<Pawn, bool> hasBlink,
        out int walkers)
    {
        walkers = 0;
        if (pawns == null || signals == null || hasBlink == null) return false;

        if (!CarryAllocation.TryCompute(pawns, signals, out bool[] carried)) return false;
        bool anyWalker = false;
        for (int i = 0; i < pawns.Count; i++)
        {
            if (i < carried.Length && carried[i]) continue;
            anyWalker = true;
            walkers++;
            Pawn pawn = pawns[i];
            if (pawn == null) return false;
            bool capable;
            try { capable = hasBlink(pawn); }
            catch (Exception) { capable = false; }
            if (!capable) return false;
        }
        return anyWalker;
    }
}