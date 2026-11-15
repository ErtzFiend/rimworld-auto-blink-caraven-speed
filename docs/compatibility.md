# Compatibility and limitations

The source targets RimWorld 1.7 and was developed against 1.7.5005 rev1082, Harmony and AutoBlink `rabiosus.autoblink`. AutoBlink Gene (`Noszbytouj.AutoBlinkGene`) and Phasecore (`AZD.Phasecore`) are optional. Their legacy gene and hediff definitions are converted by AutoBlink 1.7 to modern source definitions. Capability checks modern profile-bearing gene extensions, hediff components, pawn components and apparel components; suppressed genes do not count. It does not ask whether a pawn can blink at this moment: toggles, cooldowns, costs and other permission checks are outside this rule. AutoBlink's spawned-only controller lookup cannot establish caravan capability.

The Harmony postfix patches the innermost `GetTicksPerMove(List<Pawn>, float, float, bool, StringBuilder)` overload once, shared by caravan travel and formation preview. Vanilla's explanation is computed before the appended mod line. The patch computes a fresh, non-mutating allocation matching vanilla's ordered, finite one-patient-per-carrier algorithm rather than relying on a potentially stale carry tracker. Only allocated passengers are excluded; downed status alone is not enough. Animals and other walkers must qualify. Caravan `AddPawn` recursively promotes carried pawns into the caravan pawn list; it is incorrect to assume nested carried pawn cargo remains absent from that list.

Harmony composition is a compatibility limit: another mod postfixing the same method may run before or after this postfix, and its changes to the travel-tick result or explanation can affect or be affected by this mod. No universal ordering or compatibility guarantee is claimed. Carry exclusion uses a fresh allocation based on vanilla's ordered, finite one-patient-per-carrier rules; mods that change carry eligibility, allocation order, or tracker semantics may produce a different allocation, so the passenger exclusion may not match that mod's intended behavior. These interactions have not been live-tested.

## Validation and limits

- Other teleport systems that do not use supported AutoBlink source types are not inferred.
- Integer ticks make 1000× approximate, with a one-tick minimum. Other world travel rules may limit practical speed. Shuttles, empty groups, and original results at or below one tick are unchanged.
- This candidate has not been exercised in a live game. Complete integration and in-game acceptance tests, including populated caravans, real carry behavior, animals, formation preview, and optional-mod conversion, before release. Do not infer a passing test from packaging.
-
