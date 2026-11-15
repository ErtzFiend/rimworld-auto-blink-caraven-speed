using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using AutoBlink;
using AutoBlinkCaravenSpeed;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Policy = AutoBlinkCaravenSpeed.Policy;
using Patches = HarmonyLib.Patches;

// Headless test harness for Auto-Blink Caraven Speed.
//
// The harness runs against the real RimWorld/AutoBlink assemblies. Because this
// is not a Unity process, a few vanilla/world services cannot be initialized.
// Those seams are patched explicitly and reported in the output:
//   * BaseHumanlikeTicksPerCell -> fixed 33 (pure def-data lookup)
//   * CaravanBonusUtility.HasCaravanBonus -> false (faction/ideo data)
//   * CaravanRideableUtility.IsCaravanRideable -> false (stat def data)
//   * CaravanCarryUtility.WouldBenefitFromBeingCarried -> true (health data)
//   * PawnCapacitiesHandler.CapableOf -> true (capacity def data)
//   * DefOfHelper.EnsureInitializedInCtor -> no-op (no game Def database)
// Everything else - pawns, genes, hediffs, apparel comps, the real
// Caravan_CarryTracker allocation loop, the real GetTicksPerMove body and the
// real Harmony installation - is exercised as-is.
internal static class Program
{
    private static int passed;

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        catch (Exception) { return false; }
        return false;
    }

    private static object Uninitialized(Type type) => FormatterServices.GetUninitializedObject(type);

    private static void SetField(object target, string field, object value)
    {
        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo info = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (info == null) continue;
            info.SetValue(target, value);
            return;
        }
        throw new MissingFieldException(target.GetType().FullName, field);
    }

    // ---------------------------------------------------------------- fixtures

    private static int nextPawnId;

    private static Pawn MakePawn(Intelligence intelligence = Intelligence.Humanlike)
    {
        Pawn pawn = new Pawn();
        ThingDef def = (ThingDef)Uninitialized(typeof(ThingDef));
        def.defName = "TestPawn_" + intelligence;
        def.race = new RaceProperties { intelligence = intelligence };
        pawn.def = def;
        // Real pawns have distinct IDs; Thing equality is ID-based, so fixtures
        // must be distinct or list.Contains() would treat every pawn as equal.
        pawn.thingIDNumber = ++nextPawnId;
        pawn.health = new Pawn_HealthTracker(pawn);
        pawn.health.capacities = (PawnCapacitiesHandler)Uninitialized(typeof(PawnCapacitiesHandler));
        SetField(pawn.health.capacities, "pawn", pawn);
        // Healthy fixture: the real flag defaults per pawn/story data, and
        // leaving it unset would make every pawn a would-be patient.
        pawn.health.beCarriedByCaravanIfSick = false;
        pawn.genes = new Pawn_GeneTracker(pawn);
        pawn.apparel = new Pawn_ApparelTracker(pawn);
        // Without a mind state Pawn.InMentalState throws, which the fail-closed
        // signal path correctly treats as unknown; give fixtures the empty one.
        pawn.mindState = (Pawn_MindState)Uninitialized(typeof(Pawn_MindState));
        pawn.mindState.mentalStateHandler = new MentalStateHandler();
        return pawn;
    }

    private static AutoBlinkProfileDef DirectProfile(string name) =>
        new AutoBlinkProfileDef { defName = name };

    private static Gene AddGene(Pawn pawn, AutoBlinkProfileDef profile, bool legacyOnly = false)
    {
        GeneDef def = new GeneDef
        {
            defName = "TestGene_" + Guid.NewGuid().ToString("N"),
            modExtensions = new List<DefModExtension>()
        };
        if (legacyOnly)
            def.modExtensions.Add(new GeneExtension_AutoBlink());
        else
            def.modExtensions.Add(new GeneExtension_AutoBlinkSource { profile = profile });
        Gene gene = new Gene { def = def, pawn = pawn };
        pawn.genes.Endogenes.Add(gene);
        InvalidateGeneCache(pawn);
        return gene;
    }

    // The tracker caches a merged gene list; RemoveGene clears it. Calling the
    // real RemoveGene here would drag in ModLister game init that a headless
    // process cannot do, so the two state changes it makes to what Capability
    // reads (list entry + cache) are applied directly.
    private static void RemoveGeneLikeTracker(Pawn pawn, Gene gene)
    {
        pawn.genes.Endogenes.Remove(gene);
        InvalidateGeneCache(pawn);
    }

    private static void InvalidateGeneCache(Pawn pawn) =>
        typeof(Pawn_GeneTracker)
            .GetField("cachedGenes", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(pawn.genes, null);

    private static HediffWithComps AddHediff(Pawn pawn, AutoBlinkProfileDef profile, bool legacyOnly = false)
    {
        HediffWithComps hediff = (HediffWithComps)Uninitialized(typeof(HediffWithComps));
        hediff.def = new HediffDef { defName = "TestHediff_" + Guid.NewGuid().ToString("N") };
        hediff.pawn = pawn;
        List<HediffComp> comps = new List<HediffComp>();
        if (legacyOnly)
        {
            HediffComp_AutoBlink legacy = new HediffComp_AutoBlink
            {
                props = new HediffCompProperties_AutoBlink()
            };
            comps.Add(legacy);
        }
        else
        {
            HediffComp_AutoBlinkSource comp = new HediffComp_AutoBlinkSource
            {
                props = new HediffCompProperties_AutoBlinkSource { profile = profile }
            };
            comps.Add(comp);
        }
        SetField(hediff, "comps", comps);
        pawn.health.hediffSet.hediffs.Add(hediff);
        return hediff;
    }

    private static Apparel AddApparel(Pawn pawn, AutoBlinkProfileDef profile)
    {
        Apparel apparel = (Apparel)Uninitialized(typeof(Apparel));
        // ThingDef's ctor pulls in game assemblies this headless process lacks;
        // only the defName is read by the capability scan.
        ThingDef apparelDef = (ThingDef)Uninitialized(typeof(ThingDef));
        apparelDef.defName = "TestApparel";
        apparel.def = apparelDef;
        CompAutoBlinkSource comp = new CompAutoBlinkSource
        {
            props = new CompProperties_AutoBlinkSource { profile = profile }
        };
        SetField(apparel, "comps", new List<ThingComp> { comp });
        pawn.apparel.WornApparel.Add(apparel);
        return apparel;
    }

    private static void AddPawnComp(Pawn pawn, AutoBlinkProfileDef profile)
    {
        CompAutoBlinkSource comp = new CompAutoBlinkSource
        {
            props = new CompProperties_AutoBlinkSource { profile = profile }
        };
        SetField(pawn, "comps", new List<ThingComp> { comp });
    }

    private static Caravan MakeCaravan(params Pawn[] pawns)
    {
        Caravan caravan = (Caravan)Uninitialized(typeof(Caravan));
        caravan.pawns = new ThingOwner<Pawn>(caravan);
        foreach (Pawn pawn in pawns) caravan.pawns.InnerListForReading.Add(pawn);
        caravan.pather = new Caravan_PathFollower(caravan);
        caravan.carryTracker = new Caravan_CarryTracker(caravan);
        caravan.hasShuttleDirty = false;
        return caravan;
    }

    private static List<Pawn> StaleCarried(Caravan caravan, params Pawn[] carried)
    {
        List<Pawn> list = new List<Pawn>(carried);
        SetField(caravan.carryTracker, "carriedPawns", list);
        return list;
    }

    // ------------------------------------------------------------ fake signals

    private sealed class FakeSignals : ICarrySignals
    {
        public HashSet<Pawn> Humanlike = new HashSet<Pawn>();
        public HashSet<Pawn> Carriers = new HashSet<Pawn>();
        public HashSet<Pawn> Wants = new HashSet<Pawn>();
        public HashSet<Pawn> Downed = new HashSet<Pawn>();
        public HashSet<Pawn> Mental = new HashSet<Pawn>();

        // pawns whose status query is undecidable (simulates a throwing modded pawn)
        public HashSet<Pawn> UnknownState = new HashSet<Pawn>();

        public bool IsHumanlike(Pawn p) => Humanlike.Contains(p);
        public bool CanManipulate(Pawn p) => Carriers.Contains(p);
        public bool TryIsDowned(Pawn p, out bool downed)
        {
            downed = Downed.Contains(p);
            return !UnknownState.Contains(p);
        }
        public bool TryInMentalState(Pawn p, out bool mental)
        {
            mental = Mental.Contains(p);
            return !UnknownState.Contains(p);
        }
        public bool TryWantsToBeCarried(Pawn p, out bool wants)
        {
            wants = Wants.Contains(p);
            return !UnknownState.Contains(p);
        }
    }

    // --------------------------------------------------------- harness patches

    private static class HarnessPatches
    {
        public static bool BaseHumanlikePrefix(ref int __result) { __result = 33; return false; }
        public static bool CaravanBonusPrefix(ref bool __result) { __result = false; return false; }
        public static bool RideablePrefix(ref bool __result) { __result = false; return false; }
        public static bool WouldBenefitPrefix(ref bool __result) { __result = true; return false; }
        public static bool CapablePrefix(ref bool __result) { __result = true; return false; }
        public static bool DefOfInitPrefix() => false;

        public static bool SpawnedPrefix(ref bool __result) { __result = true; return false; }
        public static bool MovingPrefix(ref bool __result) { __result = true; return false; }

        public static HashSet<Pawn> Wants = new HashSet<Pawn>();
        public static HashSet<Pawn> Carriers = new HashSet<Pawn>();

        public static bool WantsPrefix(Pawn p, ref bool __result) { __result = Wants.Contains(p); return false; }

        public static bool CarriersPrefix(Caravan_CarryTracker __instance, List<Pawn> outPawns)
        {
            outPawns.Clear();
            foreach (Pawn pawn in __instance.caravan.PawnsListForReading)
                if (Carriers.Contains(pawn)) outPawns.Add(pawn);
            return false;
        }
    }

    private static void InstallHarnessPatches()
    {
        Harmony harmony = new Harmony("tests.autoblinkcaravenspeed.harness");
        harmony.Patch(AccessTools.Method(typeof(DefOfHelper), nameof(DefOfHelper.EnsureInitializedInCtor)),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.DefOfInitPrefix)));
        harmony.Patch(AccessTools.Method(typeof(CaravanTicksPerMoveUtility), "BaseHumanlikeTicksPerCell"),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.BaseHumanlikePrefix)));
        harmony.Patch(AccessTools.Method(typeof(CaravanBonusUtility), "HasCaravanBonus"),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.CaravanBonusPrefix)));
        harmony.Patch(AccessTools.Method(typeof(CaravanRideableUtility), "IsCaravanRideable", new[] { typeof(Pawn) }),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.RideablePrefix)));
        harmony.Patch(AccessTools.Method(typeof(CaravanCarryUtility), nameof(CaravanCarryUtility.WouldBenefitFromBeingCarried)),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.WouldBenefitPrefix)));
        harmony.Patch(AccessTools.Method(typeof(PawnCapacitiesHandler), nameof(PawnCapacitiesHandler.CapableOf), new[] { typeof(PawnCapacityDef) }),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.CapablePrefix)));
    }

    private static void InstallTrackerSeams()
    {
        Harmony harmony = new Harmony("tests.autoblinkcaravenspeed.tracker");
        harmony.Patch(AccessTools.PropertyGetter(typeof(WorldObject), nameof(WorldObject.Spawned)),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.SpawnedPrefix)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Caravan_PathFollower), nameof(Caravan_PathFollower.MovingNow)),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.MovingPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Caravan_CarryTracker), "WantsToBeCarried"),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.WantsPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Caravan_CarryTracker), "CalculatePawnsWhoCanCarry"),
            prefix: new HarmonyMethod(typeof(HarnessPatches), nameof(HarnessPatches.CarriersPrefix)));
    }

    // ------------------------------------------------------------- test runner

    private static int VanillaTicks(List<Pawn> pawns, bool shuttle = false) =>
        CaravanTicksPerMoveUtility.GetTicksPerMove(pawns, 0f, 100f, shuttle, null);

    public static void Main()
    {
        InstallHarnessPatches();

        // -- pure policy -----------------------------------------------------
        Check("Scale 1 tick", Policy.Scale(1) == 1);
        Check("Scale 499 -> 1", Policy.Scale(499) == 1);
        Check("Scale 500 -> 1", Policy.Scale(500) == 1);
        Check("Scale 1499 -> 1", Policy.Scale(1499) == 1);
        Check("Scale 1500 -> 2", Policy.Scale(1500) == 2);
        Check("Scale 3300 -> 3", Policy.Scale(3300) == 3);
        Check("Scale int.MaxValue stays positive", Policy.Scale(int.MaxValue) == 2147484);
        Check("Scale 0 -> 1", Policy.Scale(0) == 1);
        Check("Scale negative -> 1", Policy.Scale(-7) == 1);

        // -- real capability objects ----------------------------------------
        Pawn genePawn = MakePawn();
        AutoBlinkProfileDef profile = DirectProfile("TestProfile");
        Gene gene = AddGene(genePawn, profile);
        Check("real active gene grants capability", Capability.HasBlink(genePawn));
        gene.overriddenByGene = new Gene { def = new GeneDef(), pawn = genePawn };
        Check("overridden gene does not grant capability", !Capability.HasBlink(genePawn));
        gene.overriddenByGene = null;
        RemoveGeneLikeTracker(genePawn, gene);
        Check("removed gene stops granting capability", !Capability.HasBlink(genePawn));

        Pawn legacyGenePawn = MakePawn();
        AddGene(legacyGenePawn, profile, legacyOnly: true);
        Check("legacy gene extension alone is not capability", !Capability.HasBlink(legacyGenePawn));

        Pawn hediffPawn = MakePawn();
        HediffWithComps hediff = AddHediff(hediffPawn, profile);
        Check("real converted hediff comp grants capability", Capability.HasBlink(hediffPawn));
        hediffPawn.health.hediffSet.hediffs.Clear();
        Check("removed hediff stops granting capability", !Capability.HasBlink(hediffPawn));

        Pawn legacyHediffPawn = MakePawn();
        AddHediff(legacyHediffPawn, profile, legacyOnly: true);
        Check("legacy Phasecore hediff comp alone is not capability", !Capability.HasBlink(legacyHediffPawn));

        Pawn apparelPawn = MakePawn();
        AddApparel(apparelPawn, profile);
        Check("real worn apparel comp grants capability", Capability.HasBlink(apparelPawn));
        apparelPawn.apparel.WornApparel.Clear();
        Check("removed apparel stops granting capability", !Capability.HasBlink(apparelPawn));

        Pawn compPawn = MakePawn();
        AddPawnComp(compPawn, profile);
        Check("real pawn comp grants capability", Capability.HasBlink(compPawn));

        Pawn noProfilePawn = MakePawn();
        AddGene(noProfilePawn, null);
        Check("null profile is not capability", !Capability.HasBlink(noProfilePawn));
        Check("null pawn is not capability", !Capability.HasBlink(null));
        Check("vanilla pawn with no source is not capability", !Capability.HasBlink(MakePawn()));

        // -- carry allocation mirror, controlled signals --------------------
        Pawn a = MakePawn(), b = MakePawn(), c = MakePawn(), d = MakePawn();
        List<Pawn> four = new List<Pawn> { a, b, c, d };
        FakeSignals signals = new FakeSignals();
        foreach (Pawn p in four) signals.Humanlike.Add(p);
        signals.Wants.Add(c);
        signals.Wants.Add(d);
        signals.Carriers.Add(a);
        bool[] carried = CarryAllocation.Compute(four, signals);
        Check("one carrier takes first patient in caravan order", carried[2] && !carried[3]);

        signals.Carriers.Add(b);
        carried = CarryAllocation.Compute(four, signals);
        Check("two carriers take two patients", carried[2] && carried[3]);

        signals.Carriers.Clear();
        carried = CarryAllocation.Compute(four, signals);
        Check("no carrier means nobody is carried", !carried[2] && !carried[3]);

        signals.Carriers.Add(a);
        signals.Wants.Clear();
        signals.Wants.Add(a); // carrier is also a patient -> not a carrier
        carried = CarryAllocation.Compute(four, signals);
        Check("patient cannot carry itself", !carried[0]);

        // Unknown status must fail the whole decision closed: guessing "not
        // downed / does not want carrying" would exempt a real walker.
        FakeSignals unknown = new FakeSignals();
        foreach (Pawn p in four) unknown.Humanlike.Add(p);
        unknown.Carriers.Add(a);
        unknown.Wants.Add(c);
        unknown.UnknownState.Add(b);
        Check("unknown status makes allocation undecidable",
            !CarryAllocation.TryCompute(four, unknown, out _));
        Check("undecidable allocation yields no passengers",
            CarryAllocation.Compute(four, unknown).Length == 0);
        Check("unknown status denies eligibility even if every pawn could blink",
            !Eligibility.AllWalkersBlink(four, unknown, _ => true, out _));
        Check("known status still decides allocation",
            CarryAllocation.TryCompute(four, signals, out bool[] decided) && decided.Length == 4);

        // -- real Caravan_CarryTracker parity --------------------------------
        InstallTrackerSeams();
        HashSet<Pawn> humanlike = new HashSet<Pawn>(four);
        FakeSignals trackSignals = new FakeSignals();
        foreach (Pawn p in four) trackSignals.Humanlike.Add(p);
        VerifyTrackerParity("parity: 1 carrier / 2 patients", four, trackSignals,
            new[] { a }, new[] { c, d });
        VerifyTrackerParity("parity: 2 carriers / 2 patients", four, trackSignals,
            new[] { a, b }, new[] { c, d });
        VerifyTrackerParity("parity: no carriers", four, trackSignals,
            new Pawn[0], new[] { c, d });
        VerifyTrackerParity("parity: patient before carrier in list", four, trackSignals,
            new[] { d }, new[] { a });
        VerifyTrackerParity("parity: two carriers three patients", new List<Pawn> { a, b, c, d },
            trackSignals, new[] { a, b }, new[] { c, d });
        Check("real tracker respects humanlike-only carriers",
            !new FakeSignals { Humanlike = new HashSet<Pawn> { a } }.IsHumanlike(b));

        // The parity seams stubbed private tracker predicates; drop them now so
        // every later check sees unmodified production tracker/patch behavior.
        new Harmony("tests.autoblinkcaravenspeed.tracker").UnpatchAll("tests.autoblinkcaravenspeed.tracker");
        Check("tracker parity seams removed before production checks",
            NoPatches(AccessTools.Method(typeof(Caravan_CarryTracker), "WantsToBeCarried")) &&
            NoPatches(AccessTools.Method(typeof(Caravan_CarryTracker), "CalculatePawnsWhoCanCarry")));

        // -- populated Caravan fixtures + eligibility ------------------------
        Pawn walker1 = MakePawn();
        Pawn walker2 = MakePawn();
        AddGene(walker1, profile);
        AddApparel(walker2, profile);
        Caravan allBlink = MakeCaravan(walker1, walker2);
        Check("populated caravan: all blinkers walk",
            Eligibility.AllWalkersBlink(allBlink.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out int walkers) && walkers == 2);
        Check("populated caravan all-blink grants bonus",
            CaravanTicksPerMovePatch.Applies(allBlink.PawnsListForReading));

        Pawn mixed = MakePawn();
        Caravan mixedCaravan = MakeCaravan(walker1, mixed);
        Check("populated caravan: mixed members deny bonus",
            !Eligibility.AllWalkersBlink(mixedCaravan.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out _));

        Pawn animal = MakePawn(Intelligence.Animal);
        Caravan animalCaravan = MakeCaravan(walker1, animal);
        Check("animal walker without source denies bonus",
            !Eligibility.AllWalkersBlink(animalCaravan.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out _));
        AddGene(animal, profile);
        Check("animal with a real source counts as capable",
            Eligibility.AllWalkersBlink(animalCaravan.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out _));

        Pawn toolUserMech = MakePawn(Intelligence.ToolUser);
        Caravan mechCaravan = MakeCaravan(walker1, toolUserMech);
        Check("mech walker without source denies bonus",
            !Eligibility.AllWalkersBlink(mechCaravan.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out _));
        AddHediff(toolUserMech, profile);
        Check("mech with converted hediff source counts as capable",
            Eligibility.AllWalkersBlink(mechCaravan.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out _));

        // carried passenger excluded, carrier walks
        Pawn patient = MakePawn();
        patient.health.beCarriedByCaravanIfSick = true;
        Pawn carrier = walker1;
        Caravan carriedCaravan = MakeCaravan(carrier, patient);


        Check("carried passenger without source is exempt",
            Eligibility.AllWalkersBlink(carriedCaravan.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out int carriedWalkers) && carriedWalkers == 1);
        Check("would-be patient with no carrier blocks",
            !Eligibility.AllWalkersBlink(new List<Pawn> { patient }.AsReadOnly(), VanillaPawnSignals.Instance,
                Capability.HasBlink, out _));

        // stale tracker must not authorise a walking non-blinking member
        Caravan stale = MakeCaravan(walker1, mixed);
        List<Pawn> staleList = StaleCarried(stale, mixed);
        int before = staleList.Count;
        bool staleEligible = Eligibility.AllWalkersBlink(stale.PawnsListForReading, VanillaPawnSignals.Instance,
            Capability.HasBlink, out _);
        Check("stale tracker cannot exclude a walking non-blinking member", !staleEligible);
        Check("speed query does not mutate stale tracker", staleList.Count == before && staleList[0] == mixed);

        // stale tracker listing an anonymous recovered patient does not grant either
        Caravan staleAll = MakeCaravan(mixed, MakePawn());
        StaleCarried(staleAll, staleAll.PawnsListForReading.ToArray());
        Check("stale all-carried tracker does not grant bonus",
            !Eligibility.AllWalkersBlink(staleAll.PawnsListForReading, VanillaPawnSignals.Instance,
                Capability.HasBlink, out _));

        // -- actual Harmony installation -------------------------------------
        PatchInstaller.Remove();
        Check("patch removed before bootstrap test", !PatchInstaller.IsApplied);
        Type bootstrap = typeof(Policy).Assembly.GetType("AutoBlinkCaravenSpeed.Bootstrap");
        RuntimeHelpers.RunClassConstructor(bootstrap.TypeHandle);
        MethodInfo patched = AccessTools.Method(typeof(CaravanTicksPerMoveUtility), nameof(CaravanTicksPerMoveUtility.GetTicksPerMove),
            new[] { typeof(List<Pawn>), typeof(float), typeof(float), typeof(bool), typeof(StringBuilder) });
        Patches info = Harmony.GetPatchInfo(patched);
        int owned = 0;
        foreach (Patch patch in info.Postfixes)
            if (patch.owner == PatchInstaller.HarmonyId) owned++;
        Check("actual static-constructor PatchAll installs exactly one postfix", owned == 1 && PatchInstaller.IsApplied);
        PatchInstaller.Apply();
        Check("Apply is idempotent (still one postfix)",
            CountOwned(patched) == 1);

        // -- end-to-end real method body -------------------------------------
        List<Pawn> allList = new List<Pawn> { walker1, walker2 };
        PatchInstaller.Remove();
        int baselineAll = VanillaTicks(allList);
        PatchInstaller.Apply();
        int patchedAll = VanillaTicks(allList);
        Check("real method baseline 5610", baselineAll == 5610);
        Check("all-blink patched result is exactly scaled once",
            patchedAll == Policy.Scale(baselineAll) && patchedAll < baselineAll);
        CaravanTicksPerMoveUtility.CaravanInfo caravanInfo = new CaravanTicksPerMoveUtility.CaravanInfo
        {
            pawns = allList, massUsage = 0f, massCapacity = 100f, isShuttle = false
        };
        Check("CaravanInfo/formation entry multiplies exactly once",
            CaravanTicksPerMoveUtility.GetTicksPerMove(caravanInfo, null) == patchedAll);

        int patchedMixed = VanillaTicks(new List<Pawn> { walker1, mixed });
        Check("mixed caravan result unchanged", patchedMixed == 5610);

        List<Pawn> carriedList = new List<Pawn> { carrier, patient };
        Check("end-to-end carried passenger is exempt",
            VanillaTicks(carriedList) == Policy.Scale(5610));

        Check("end-to-end animal walker without source unchanged",
            VanillaTicks(new List<Pawn> { walker1, MakePawn(Intelligence.Animal) }) == 5610);
        Check("end-to-end empty list returns default 3300",
            CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn>(), 0f, 100f, false, null) == 3300);
        Check("end-to-end empty list at zero mass capacity still default 3300",
            CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn>(), 0f, 0f, false, null) == 3300);
        // Verify the precise vanilla exception both without and with the postfix.
        PatchInstaller.Remove();
        bool vanillaNullThrows = Throws<NullReferenceException>(() =>
            CaravanTicksPerMoveUtility.GetTicksPerMove((List<Pawn>)null, 0f, 100f, false, null));
        PatchInstaller.Apply();
        Check("end-to-end null list preserves vanilla NullReferenceException",
            vanillaNullThrows && Throws<NullReferenceException>(() =>
                CaravanTicksPerMoveUtility.GetTicksPerMove((List<Pawn>)null, 0f, 100f, false, null)));
        Check("end-to-end null Caravan returns default 3300",
            CaravanTicksPerMoveUtility.GetTicksPerMove((Caravan)null, null) == 3300);
        Check("end-to-end shuttle result 0 preserved",
            CaravanTicksPerMoveUtility.GetTicksPerMove(allList, 0f, 100f, true, null) == 0);

        // direct postfix boundary/explanation checks (avoid vanilla translator
        // which needs a language worker in this headless process)
        StringBuilder explanation = new StringBuilder("Vanilla explanation");
        int result = 5610;
        CaravanTicksPerMovePatch.Postfix(allList, false, explanation, ref result);
        Check("postfix appends precise explanation", explanation.ToString().Contains("Auto-Blink Caraven Speed") &&
            explanation.ToString().Contains("2 walking member(s)") && explanation.ToString().Contains("x0.001") &&
            explanation.ToString().Contains("pre-mod") && explanation.ToString().Contains("tiles/day"));
        int nullListResult = 5610;
        CaravanTicksPerMovePatch.Postfix(null, false, null, ref nullListResult);
        Check("postfix null list unchanged", nullListResult == 5610);
        int zero = 0, negative = -5, one = 1;
        CaravanTicksPerMovePatch.Postfix(allList, false, null, ref zero);
        CaravanTicksPerMovePatch.Postfix(allList, false, null, ref negative);
        CaravanTicksPerMovePatch.Postfix(allList, false, null, ref one);
        Check("postfix preserves non-positive results", zero == 0 && negative == -5);
        Check("postfix preserves one-tick result", one == 1);
        int shuttleResult = 0;
        CaravanTicksPerMovePatch.Postfix(allList, true, null, ref shuttleResult);
        Check("postfix preserves shuttle result", shuttleResult == 0);

        // -- transitions ------------------------------------------------------
        PatchInstaller.Apply();
        Pawn transitionPawn = MakePawn();
        Gene transitionGene = AddGene(transitionPawn, profile);
        Caravan transitionCaravan = MakeCaravan(transitionPawn);
        Check("transition: source added grants caravan bonus",
            VanillaTicks(transitionCaravan.PawnsListForReading) == Policy.Scale(5610));
        RemoveGeneLikeTracker(transitionPawn, transitionGene);
        Check("transition: source removed denies caravan bonus",
            VanillaTicks(transitionCaravan.PawnsListForReading) == 5610);
        AddHediff(transitionPawn, profile);
        Check("transition: replacement hediff source grants again",
            VanillaTicks(transitionCaravan.PawnsListForReading) == Policy.Scale(5610));

        Check("patch removed cleanly for shutdown", RemoveAndReport(patched));

        Console.WriteLine();
        Console.WriteLine("RESULT " + passed + " checks passed");
    }

    private static bool NoPatches(MethodInfo method)
    {
        Patches info = Harmony.GetPatchInfo(method);
        return info == null ||
            info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count == 0;
    }

    private static int CountOwned(MethodInfo method)
    {
        Patches info = Harmony.GetPatchInfo(method);
        int owned = 0;
        foreach (Patch patch in info.Postfixes) if (patch.owner == PatchInstaller.HarmonyId) owned++;
        return owned;
    }

    private static bool RemoveAndReport(MethodInfo method)
    {
        PatchInstaller.Remove();
        return CountOwned(method) == 0;
    }

    private static void VerifyTrackerParity(string name, List<Pawn> pawns, FakeSignals signals,
        Pawn[] carriers, Pawn[] wants)
    {
        HarnessPatches.Carriers = new HashSet<Pawn>(carriers);
        HarnessPatches.Wants = new HashSet<Pawn>(wants);
        signals.Carriers.Clear();
        signals.Wants.Clear();
        foreach (Pawn p in carriers) signals.Carriers.Add(p);
        foreach (Pawn p in wants) signals.Wants.Add(p);
        Caravan caravan = MakeCaravan(pawns.ToArray());
        caravan.carryTracker.Notify_CaravanSpawned();
        bool[] mirror = CarryAllocation.Compute(pawns, signals);
        int expected = 0;
        for (int i = 0; i < mirror.Length; i++) if (mirror[i]) expected++;
        bool same = caravan.carryTracker.CarriedPawnsListForReading.Count == expected;
        if (same)
            for (int i = 0; i < mirror.Length; i++)
                if (mirror[i] != caravan.carryTracker.IsCarried(pawns[i])) same = false;
        if (!same)
            Console.WriteLine("  parity detail: tracker=" + caravan.carryTracker.CarriedPawnsListForReading.Count +
                " mirror=" + expected + " mirrorFlags=" + string.Join(",", mirror) +
                " trackerFlags=" + string.Join(",", pawns.ConvertAll(p => caravan.carryTracker.IsCarried(p))));
        Check(name, same);
    }
}