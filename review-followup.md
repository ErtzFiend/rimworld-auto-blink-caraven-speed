# Independent review follow-up

Two independent review passes identified issues in earlier snapshots. The final implementation and documentation were revised; this note records dispositions without including review excerpts, private paths, decompiled material, or third-party proprietary content.

- **Carry allocation and stale tracker state:** replaced reliance on a possibly stale carry tracker with a fresh, non-mutating ordered allocation; added relevant allocation and stale-state checks.
- **Unknown carrier status:** fail closed when required status is unknown; covered by checks.
- **Harmony patch scope and formation preview:** use one shared low-level travel-tick entry point and verify exactly-once behavior in the harness. Interactions with other Harmony owners remain unverified.
- **Null/error parity and test seams:** corrected exception/default expectations and unpatch test seams before production tracker checks. The harness is not equivalent to live-game integration.
- **Public documentation and packaging:** source checkout commands and candidate archive contents were aligned; user-facing limits and dependency requirements are documented.
- **Outstanding:** no live game/save/config validation; no universal compatibility claim. See `validation.md` and `docs/compatibility.md`.
