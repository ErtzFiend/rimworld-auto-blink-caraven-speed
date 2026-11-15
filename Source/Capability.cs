using System;
using System.Collections.Generic;
using AutoBlink;
using RimWorld;
using Verse;

namespace AutoBlinkCaravenSpeed;

/// <summary>
/// Runtime AutoBlink source detection for caravan members.
///
/// AutoBlink 1.7 registers controllers only while pawns are spawned on a map,
/// so its controller lookup cannot answer "can this pawn blink in a caravan".
/// Instead this mirrors the profile-bearing source types AutoBlink's own def
/// loading produces: a gene mod extension, a hediff comp, or a thing comp on
/// the pawn / worn apparel. Legacy gene and hediff comp types are converted to
/// these modern types by AutoBlink before gameplay, so accepting only the
/// modern types is deliberate: an unconverted legacy comp grants nothing.
/// "Capable" means an installed active source, not that AutoBlink's toggle,
/// cooldown, resource costs or other policy currently allow a blink.
/// </summary>
public static class Capability
{
    public static bool HasBlink(Pawn pawn)
    {
        if (pawn == null) return false;

        if (HasGeneSource(pawn)) return true;
        if (HasHediffSource(pawn)) return true;
        if (HasThingSource(pawn)) return true;
        return HasWornSource(pawn);
    }

    private static bool HasGeneSource(Pawn pawn)
    {
        List<Gene> genes;
        try { genes = pawn.genes?.GenesListForReading; }
        catch (Exception) { return false; } // partially initialized modded pawn
        if (genes == null) return false;

        foreach (Gene gene in genes)
        {
            if (gene == null || gene.def == null) continue;
            GeneExtension_AutoBlinkSource extension;
            try { extension = gene.def.GetModExtension<GeneExtension_AutoBlinkSource>(); }
            catch (Exception) { continue; } // malformed def extension list
            if (extension == null || extension.profile == null) continue;
            try { if (gene.Active) return true; }
            catch (Exception) { /* malformed gene: try the next source */ }
        }
        return false;
    }

    private static bool HasHediffSource(Pawn pawn)
    {
        List<Hediff> hediffs;
        try { hediffs = pawn.health?.hediffSet?.hediffs; }
        catch (Exception) { return false; }
        if (hediffs == null) return false;

        foreach (Hediff hediff in hediffs)
        {
            if (hediff is not HediffWithComps withComps || withComps.comps == null) continue;
            foreach (HediffComp comp in withComps.comps)
            {
                // Only the modern converted comp type counts; legacy
                // HediffComp_AutoBlink does not carry a profile in 1.7.
                if (comp is HediffComp_AutoBlinkSource source
                    && source.props is HediffCompProperties_AutoBlinkSource properties
                    && properties.profile != null) return true;
            }
        }
        return false;
    }

    private static bool HasWornSource(Pawn pawn)
    {
        List<Apparel> worn;
        try { worn = pawn.apparel?.WornApparel; }
        catch (Exception) { return false; }
        if (worn == null) return false;
        foreach (Apparel apparel in worn)
            if (HasThingSource(apparel)) return true;
        return false;
    }

    private static bool HasThingSource(ThingWithComps thing)
    {
        if (thing == null) return false;
        List<ThingComp> comps;
        try { comps = thing.AllComps; }
        catch (Exception) { return false; }
        if (comps == null) return false;
        foreach (ThingComp comp in comps)
            if (comp is CompAutoBlinkSource source
                && source.props is CompProperties_AutoBlinkSource properties
                && properties.profile != null) return true;
        return false;
    }
}