# Validation record

This source snapshot was built and tested before local publication packaging.

- `dotnet build Source/AutoBlinkCaravenSpeed.csproj -c Release`: succeeded; 0 warnings, 0 errors.
- `dotnet run --project tests/tests.csproj -c Release`: 72 checks passed.
- Built mod DLL SHA-256: `3e5fc5dce223876217622c705120113820d363b66ab50ccfc46fa916145611c1`.
- The candidate mod archive was separately verified to contain the same DLL and pass ZIP integrity checks.
- Tests use controlled boundary seams/stubs; they are not a live-game integration test. No game launch, save, or configuration validation was performed. Live validation remains outstanding.
- This record does not claim compatibility with all mods or guarantee runtime behavior in a live installation.
