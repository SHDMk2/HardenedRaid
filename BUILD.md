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
| `PointsPatches.cs` | 10,000 points cap (transpiler) + wealth curves extended beyond 1M (colony mech / subhuman factors stay vanilla, flat at 0.4) |
| `UncapPatches.cs` | off-raid point curves (distant fights, sites, relic hunts, complexes, sleeping mechs, Anomaly…): vanilla up to 10,000 threat, then extended keeping the ratio at the 10,000 anchor (obelisk duplicator cost curve left vanilla), deep drill infestation cap |
| `SettlementDefense.cs` | attacked faction bases (hostile, or neutral / allied being attacked): defenders at a share of the player's threat (at most 2 × the per-wave cap), modded perimeter turrets and their crews, vanilla mortars manned by Hardened defenders; per-map-generation context stack |
| `SettlementVehicles.cs` | attacked faction bases: Vehicle Framework vehicles parked around the base (touches VF types, only called when VF is active) |
| `Tiers.cs` | tiers T0-T6 (industrial+) and N0-N5 (neolithic/medieval), points → tier curves |
| `Pricing.cs` | ranks (1 NCO / 10, 1 officer / 100), truncated gaussian quality per rank, price = body + gear value ÷ rate, armor / armored mechanoid gate and pricing |
| `RaidPlanner.cs` | raid plan (core + troops), pawnkinds, roles, generation, vehicle and transport purchase (within the per-wave caps of the final plan), off-raid caps (bases 2 ×, other groups 1 ×) |
| `Loadouts.cs` | per-faction item tables, uniform loadout (armor, weapons per role, implants, GiTS cyberbrains, anti-pollution mask candidates) |
| `PawnTuning.cs` | health, implants (no adaptation phase), prostheses (cheapest one at T1), skills, gear, anti-pollution mask on polluted cells (like vanilla, unless the outfit already protects), sidearms, biocoding + acidifier, tier marker (updated, never duplicated) |
| `GenerationPatches.cs` | Harmony hooks into vanilla pawn generation |
| `CECompat.cs` | Combat Extended ammo (reflection, optional) and AI ammo type weights |
| `Waves.cs` | delayed waves (saved GameComponent; waves of a removed map dropped before saving, vehicle cost returned to the infantry of a wave launched without its vehicles) |
| `AIPatches.cs` | smart/breach/sapper strategy weights, turret avoid grid, out-of-service turrets |
| `SiegePatches.cs` | siege camps: modded artillery (building / weapon tags or indirect fire; at least one piece with the vanilla `Artillery` tag so NoArtillery does not fire early) and turrets (manned or automatic, gunner cell kept clear; no anti-aircraft guns or spotlights), sandbags facing the base (Fortifications 1×2 / 2×2 included, trenches excluded: no cover), sandbag walls / embrasures in front of the artillery, builders' construction level from the camp's actual plans |
| `SiegeCamp.cs` | siege camp plan (tier 2+, support teams): deterministic layout within 15 cells of the flag, facing the target — artillery park at the back (explosive pieces ≥ 4.5 cells apart), turret arc at the front, 1-cell alley, gunner cells kept; manned pieces ≤ 55 % of the group, camp grows with headcount beyond tier 6, support camps 1 artillery + 1 turret; cover (parapets, then 1-2 sandbag rows) only once the pieces are built, within the builders' one-day work budget, exact materials dropped; camp spot (vanilla search + validator: clear, unroofed, heavy ground without water / marsh over the plan radius, beyond the largest artillery minimum range + 5), supplies dropped at the back on the flank away from explosive pieces (Init transpiler + DropSupplies); nothing saved (camp state read from the map) |
| `SiegeCrews.cs` | siege camps that hold: wounds no longer launch the assault (setting); on the assault, artillery and turrets keep their faction and their dedicated gunners in a `LordJob_HRTurretCrew` (resupplied; pieces neutral when that lord ends, never packed by a convoy retreat); two TransitionActions added, LordJob_Siege toils / triggers unchanged |
| `SupportTeams.cs` | support teams (forward camp during an assault, only on a clear spot: none at the map edge) + enemy turret resupply (ammo crates next to the gunner, outside the other pieces) |
| `TurretManning.cs` | enemy turret crews: one dedicated gunner per built manned piece in siege / support camps (vanilla ManClosestTurret duty), AI may man an already loaded turret without ammo on the ground (vanilla / CE job givers), occupant-only bunkers (Fortifications) left out of the catalogs |
| `Convoy.cs` | enemy convoy: approach, dismount, truck turrets, fire support, retreat and re-boarding (`Defs/HR_AI.xml`) |
| `VehicleCompat.cs` | enemy vehicles through Vehicle Framework (with `SettlementVehicles.cs`, the only files touching VF types) |
| `RaidContext.cs` | one context per raid (stack), safe for nested raids; planned waves and vehicles of the main group |
| `WaveCaps.cs` | per-wave cap of the vanilla fallback, of human raids with tiers disabled and of non-human raids (mechanoids…) |
| `InfestationWaves.cs` | infestations: hive cap per wave, extra hives dig in later |
| `EntityWaves.cs` | Anomaly entity groups outside raids (sightstealers, gorehulks…): non-human cap per wave, extra entities walk in later in waves (or are discarded, setting), entity incidents rarer when the threat exceeds one wave |
| `PawnGenFixes.cs` | generation depth (relatives keep vanilla gear), redressed world pawns (implants, prostheses), corpse groups excluded, previews (`GeneratePawnKindsExample`) |
| `EventPatches.cs` | manhunter packs (cap = low-tech wave cap, species weight), animal events rarer when the threat exceeds what they can spend |
| `MechClusters.cs` | mech clusters: budget per cluster, split / delayed clusters (at most 10 per event, delayed ones retried for a day), size cap, linear mech placement, fast position search |
| `MechPricingPatches.cs` | armored mechanoid pricing for capsules, assemblers, sleeping mechs (gate on the threat, not the local budget), random mech groups |
| `Uninstall.cs` | "Prepare to remove the mod" button: Hardened lords → vanilla lords (siege crews' pieces made neutral, as vanilla does on the assault; a raid in progress continues: infantry and crews assault, armed vehicles under VF's raid AI, other vehicles leave, stuck ones left in place, captives dropped), waves / markers / registries cleared |
| `Strings.cs` | player-facing strings (English / French), selected by the mod's language setting |
| `HRSettings.cs` | settings window, startup |
| `DebugActions.cs` | dev mode → "Hardened Raids" → raid plan preview / pending waves |

Source comments and the detailed (debug) log are in French.
