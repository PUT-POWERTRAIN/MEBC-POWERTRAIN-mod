# MEBC POWERTRAIN Mod

A BepInEx mod for **Energy Boat Simulator** that replaces the stock hull with the
PUT POWERTRAIN boat, loaded from an `.obj` model at runtime.

The custom hull is registered as a real shop item (`POWERTRAIN POWERBoat`) and
integrated into the boat builder, so it can be selected like any native hull.
Native boat meshes and attachment props (props, motors, rudder, etc.) are hidden
while the custom model is shown.

## Features

- Registers `POWERTRAIN POWERBoat` as a native hull in the shop / boat builder.
- Loads geometry and materials from `boat.obj` + `boat.mtl` (no Unity recompile).
- Auto-fits the model to the stock hull footprint.
- Per-model scale, offsets and yaw, live-configurable.
- Hides the original hull and native attachment renderers.
- Works in both the shop preview and races.

## Requirements

**Players (Windows x64):**

- Energy Boat Simulator (base game, unmodified)
- Nothing else — the release pack bundles BepInEx 5.4.23.2 x64.

**Building from source:**

- .NET SDK (tested with 10.x; targets `net48`)
- A local copy of the game, for reference assemblies
  (`BepInEx\core` + `Energy Boat Simulator_Data\Managed`)

## Install (players)

1. Extract the base game to a folder, e.g. `C:\Games\MEBC`.
2. Extract the contents of `MEBC-PowertrainMod-v1.5.0.zip` **into that same
   folder**, merging/overwriting when prompted. You should end up with:

   ```
   Energy Boat Simulator.exe
   winhttp.dll
   doorstop_config.ini
   BepInEx\
   ```

3. Run `Energy Boat Simulator.exe`. A BepInEx console opens and the mod loads.
4. In the boat builder / shop, select the **POWERTRAIN POWERBoat** hull.

To uninstall, delete `winhttp.dll`, `doorstop_config.ini` and `BepInEx\`.

## Build from source

The build needs the game folder for reference assemblies. Point at it with an
environment variable:

```bash
cd plugin
MEBC_GAME_DIR="/path/to/MEBC" dotnet build -c Release
```

or with an MSBuild property:

```bash
dotnet build -c Release -p:GameDir="C:\Games\MEBC"
```

Building without `GameDir` (and without `MEBC_GAME_DIR`) fails with a clear
error.

Output: `plugin/bin/Release/net48/BoatMod.dll`.

### Assemble a release pack

The pack is just a game folder containing BepInEx plus the mod. Stage:

```
winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt
BepInEx/core/*                                  (BepInEx 5.4.23.2 x64)
BepInEx/config/BepInEx.cfg
BepInEx/config/mateusz.energyboatsimulator.custommodel.cfg
BepInEx/plugins/BoatMod.dll                     (Release build)
BepInEx/plugins/BoatMod/boat.obj
BepInEx/plugins/BoatMod/boat.mtl
INSTALL.txt
```

Then zip the *contents* (not the folder) so it extracts straight into the game
directory. Builds land in `dist/` (git-ignored).

## Configuration

`BepInEx/config/mateusz.energyboatsimulator.custommodel.cfg` — edits apply live
in-game.

| Section | Key | Default | Description |
| --- | --- | --- | --- |
| General | `Enabled` | `true` | Master switch. |
| General | `HideOriginal` | `true` | Hide the original hull meshes. |
| General | `PostSwapFallback` | `false` | Post-hoc mesh swap in races (native visual is used instead). |
| General | `Diagnostics` | `false` | Verbose builder/renderer dumps in the log. |
| Model | `AutoFit` | `true` | Auto-scale the model to the stock hull footprint. |
| Model | `Scale` | `1` | Extra scale multiplier on top of `AutoFit`. |
| Model | `OffsetX/Y/Z` | `0, -0.32, 0` | Local position offset in meters. |
| Model | `RotY` | `0` | Yaw rotation in degrees. |
| Model | `Path` | `BepInEx/plugins/BoatMod/boat.obj` | OBJ file to load (expects a matching `.mtl`). Leave unset to auto-resolve. |

Set `Diagnostics = true` for a detailed log of the hull/shop integration.

## Troubleshooting

- **Mod doesn't load / no console** — make sure `winhttp.dll` and
  `doorstop_config.ini` sit next to `Energy Boat Simulator.exe`, and that the
  game is 64-bit.
- **Hull missing from the shop** — check `BepInEx/LogOutput.log` for
  `POWERTRAIN`; enable `Diagnostics` for more detail.
- **Boat misplaced or too big/small** — adjust `OffsetY`, `Scale` and `RotY`.
- **Crash / black boat** — verify `boat.obj` and `boat.mtl` are both present in
  `BepInEx/plugins/BoatMod/`.

## Project layout

```
plugin/
  Plugin.cs           BepInEx entry point, scene scanning, model swapping
  HullIntegration.cs  Native hull/shop integration, visual install, hooks
  ObjLoader.cs        Minimal OBJ/MTL loader
  BoatMod.csproj      net48 build, references the game + BepInEx
model/
  boat.obj, boat.mtl  Model loaded by the mod
dist/                 Release zips (git-ignored)
```

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.2 x64 (bundled in releases).
- Energy Boat Simulator and its assets belong to their respective owners.
- POWERTRAIN model by the PUT POWERTRAIN team.
