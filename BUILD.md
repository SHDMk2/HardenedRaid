# Hardened Raids — build

RimWorld 1.7 tourne sur .NET 10 (moteur Waylith) : le mod cible `net10.0`.

SDK installé en local (sans sudo) dans `~/.dotnet` :

```bash
export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH
cd Source/HardenedRaids
dotnet build -c Release
```

La DLL sort directement dans `Assemblies/`. Références : `Krafs.Rimworld.Ref 1.7.5000-unstable`
(même build que le jeu) et `Lib.Harmony` (fourni à l'exécution par le mod Harmony, non copié).
`Vehicles.dll` / `SmashTools.dll` sont référencés depuis le Workshop (Vehicle Framework, id 3014915404) ;
autre emplacement : `dotnet build -c Release -p:VFDir=/chemin/vers/Assemblies`.

## Structure

| Fichier | Rôle |
|---|---|
| `PointsPatches.cs` | plafond de 10 000 (transpiler) + courbes richesse prolongées au-delà de 1M |
| `Tiers.cs` | paliers T0-T6 (industriel+) et N0-N5 (néolithique/médiéval), courbes points → palier |
| `RaidPlanner.cs` | modèle C (noyau + troupe), choix des pawnkinds, rôles, génération |
| `Loadouts.cs` | tables d'objets par faction, dotation uniforme (armure, armes par rôle, implants, GiTS) |
| `PawnTuning.cs` | santé, implants, skills, port de l'équipement, marqueur de palier |
| `GenerationPatches.cs` | branchements Harmony dans la génération vanilla |
| `CECompat.cs` | munitions Combat Extended (par réflexion, optionnel) |
| `Waves.cs` | vagues différées (GameComponent sauvegardé) |
| `AIPatches.cs` | poids des stratégies smart/brèche/sape, grille d'évitement des tourelles |
| `SiegePatches.cs` | camps de siège : artillerie des mods, tranchées/sandbags, tourelles autonomes |
| `VehicleCompat.cs` | véhicules ennemis via Vehicle Framework (seule classe qui touche aux types VF) |
| `DebugActions.cs` | mode dev → « Hardened Raids » → aperçu des plans / vagues en attente |
