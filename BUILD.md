# Hardened Raids — build

RimWorld 1.7 runs on .NET 10 (Waylith engine): the mod targets `net10.0`.

```bash
cd Source/HardenedRaids
dotnet build -c Release
```

The DLL is written directly to `Assemblies/`. References: `Krafs.Rimworld.Ref 1.7.5000-unstable`
and `Lib.Harmony` (provided at runtime by the Harmony mod, not copied).
`Vehicles.dll` / `SmashTools.dll` are referenced from the Steam Workshop copy of Vehicle Framework (id 3014915404);
other location: `dotnet build -c Release -p:VFDir=/path/to/Assemblies`. Vehicle Framework stays optional at runtime.

Clean upload copy (no .git/bin/obj): `./make-release.sh [destination]` (default `~/HardenedRaidsRelease`, never inside `Mods/`).

## Structure

| File | Role |
|---|---|
| `PointsPatches.cs` | 10,000 points cap (transpiler) + wealth curves extended beyond 1M |
| `Tiers.cs` | tiers T0-T6 (industrial+) and N0-N5 (neolithic/medieval), points → tier curves |
| `Pricing.cs` | ranks (1 NCO / 10, 1 officer / 100), truncated gaussian quality per rank, price = body + gear value ÷ rate, armor / armored mechanoid gate and pricing |
| `RaidPlanner.cs` | raid plan (core + troops), pawnkinds, roles, generation, vehicle and transport purchase |
| `Loadouts.cs` | per-faction item tables, uniform loadout (armor, weapons per role, implants, GiTS cyberbrains) |
| `PawnTuning.cs` | health, implants (no adaptation phase), skills, gear, sidearms, biocoding + acidifier, tier marker |
| `GenerationPatches.cs` | Harmony hooks into vanilla pawn generation |
| `CECompat.cs` | Combat Extended ammo (reflection, optional) and AI ammo type weights |
| `Waves.cs` | delayed waves (saved GameComponent) |
| `AIPatches.cs` | smart/breach/sapper strategy weights, turret avoid grid, out-of-service turrets |
| `SiegePatches.cs` | siege camps: modded artillery and turrets (manned or automatic), cover facing the base |
| `SupportTeams.cs` | support teams (forward camp during an assault) + enemy turret resupply |
| `Convoy.cs` | enemy convoy: approach, dismount, truck turrets, fire support, retreat and re-boarding (`Defs/HR_AI.xml`) |
| `VehicleCompat.cs` | enemy vehicles through Vehicle Framework (the only file touching VF types) |
| `Strings.cs` | player-facing strings (English / French), selected by the mod's language setting |
| `HRSettings.cs` | settings window, startup |
| `DebugActions.cs` | dev mode → "Hardened Raids" → raid plan preview / pending waves |

Source comments and the detailed (debug) log are in French.
