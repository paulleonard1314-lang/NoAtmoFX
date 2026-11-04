# Building

There are two parts: the plugin DLL and the shader bundle (`noatmofx`). Both are needed at runtime.

## Plugin

Needs the .NET SDK (6 or newer) and a copy of the game with BepInEx installed, since the project references the game's assemblies.

```
cd src/NoAtmoFX
dotnet build -c Release
```

The project looks for the game in the default Steam folder. If yours is elsewhere:

```
dotnet build -c Release -p:NO="D:\Games\Nuclear Option"
```

The output is `src/NoAtmoFX/bin/NoAtmoFX.dll`. Copy it to `BepInEx/plugins/NoAtmoFX/`.

## Shader bundle

The bundle has to be built with the same Unity version as the game: **Unity 2022.3.62f2** with the Universal Render Pipeline package (any URP project works, for example the Blueprinter editor project).

1. Copy the contents of `unity/Assets/NoAtmoFX` into the project's `Assets/NoAtmoFX` folder.
2. Wait for Unity to import, then run **NoAtmoFX > Build bundle** from the menu bar.
3. The bundle is written to `<project>/NoAtmoFX_Build/noatmofx`. If the game is in the default Steam folder it is also copied to `BepInEx/plugins/NoAtmoFX/` automatically.

The `.bytes` files are raw RGBA8 noise volumes (Shape 128³, Detail 32³) and the cirrus texture (1024²). The build script turns them into Unity textures before building the bundle. `tools/bake.py` regenerates the cirrus texture (Python 3 with numpy, scipy and Pillow); it is only used when *Weather > OwnCirrus* is enabled.

Any change to a `.shader` file needs a new bundle. Changes to the C# code only need a new DLL. Restart the game after replacing either.

## Releasing

Tag each release `v<version>` (the plugin version, e.g. `v1.3.0`) and attach `NoAtmoFX_v<version>.zip`. The DLL carries a DOORMAN preview (`src/NoAtmoFX/NoAtmoFX.doorman.json`, embedded as `doorman.json`) whose update check looks for that tag and asset name.
