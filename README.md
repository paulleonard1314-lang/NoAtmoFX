# NoAtmoFX

Volumetric clouds, weather and atmosphere for Nuclear Option, as a BepInEx 5 plugin. Formerly NO Clouds.

The clouds are driven by the mission weather. Humidity and instability decide where clouds form, how high the base sits, how tall they grow and when storms show up, so the sky changes from mission to mission and over the course of a flight.

## What it does

- Volumetric cumulus with flat bases and rounded tops that grow during the afternoon and settle down at night.
- Stratus, altocumulus/altostratus and an overcast deck that covers the sky in bad weather.
- Cold and warm fronts that move in with the wind.
- Cumulonimbus with feeder towers and an anvil stretched downwind. Some strong storms become supercells with a wall cloud and a flanking line.
- Cloud shadows on the ground and on aircraft, including clouds shading other clouds.
- Light shafts through gaps in the clouds and sun rays on screen.
- Rain curtains under storms, rain around the camera, and a height fog below the base in rain and high humidity.
- Lightning inside cumulonimbus, with thunder delayed by distance.
- Turbulence inside clouds, much stronger in storms.
- Wet canopy: droplets that streak with speed in cloud and rain, and frost above the freezing level.
- Clouds lit by the moon at night.
- Clouds are also drawn into the reflection probe, so the sea reflects them.
- The game's own cirrus is kept.
- Weather changes are smoothed over several minutes so the sky never jumps.

## Requirements

- Nuclear Option
- BepInEx 5.4.x

## Installing

1. Download `NoAtmoFX_v1.0.0.zip` from the [Releases](../../releases) page.
2. Extract it into the game folder. You should end up with:
   ```
   Nuclear Option/BepInEx/plugins/NoAtmoFX/NoAtmoFX.dll
   Nuclear Option/BepInEx/plugins/NoAtmoFX/noatmofx
   ```
3. Start the game.

If you had NO Clouds installed before, delete the old `BepInEx/plugins/NOClouds` folder first. Your settings are kept.

To uninstall, delete the `BepInEx/plugins/NoAtmoFX` folder.

## Keys

| Key | Action |
|---|---|
| F10 | Clouds on/off |
| F11 | Tuning menu (settings are saved when it closes) |
| Shift+F10 | Write a diagnostic to `BepInEx/LogOutput.log` |

Keys can be changed in the config file.

## Settings

Everything is in `BepInEx/config/paul.no.clouds.cfg` (the name is kept from NO Clouds) and can be changed live from the F11 menu. "Reset this tab" restores the defaults of the open tab. The menu can also be shown in Portuguese with *General > Language*.

If performance is a problem, look at the *Quality* tab first:

- *Downscale*: 1 = full resolution, 2 = half (default), 4 = quarter.
- *Steps*: fewer steps run faster but show more banding in the distance.
- Turning off *Shadows > Shadows* and *Atmosphere > LightShafts* gives the biggest gain.

## Nuclear Rain

NoAtmoFX works with or without the Nuclear Rain mod. When Nuclear Rain is installed, rain on the canopy follows it and the rain around the camera is turned off so it is not doubled. Without it, NoAtmoFX uses its own rain under its own clouds. Either behaviour can be forced with *Glass > RainSource* and *Atmosphere > NearRain*.

## Known limitations

- Multiplayer: every player renders clouds locally, so cloud shapes do not match between players, and hiding in a cloud only hides you on your own screen.
- Radar and IR seekers still use the game's own cloud layer.

## Building from source

See [BUILDING.md](BUILDING.md).

## License

GPL-3.0. See [LICENSE](LICENSE).

The wet canopy effect is based on my XR2WetGlass mod for Flight of Nova, and the cloud system started as a port of my FoN Clouds mod for the same game. Thunder uses the game's own sounds; no game assets are included in this repository or in the release.
