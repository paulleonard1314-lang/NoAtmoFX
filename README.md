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
- Nuclear blasts push the clouds away: a hole opens behind the shock front, cloud piles up at its rim, and condensation caps and skirts form around the rising mushroom cloud. The fireball lights up the clouds around it.
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
| F8 | Test nuclear blast (clouds only, no damage, 25 km ahead of the camera) |

Keys can be changed in the config file.

## Settings

Everything is in `BepInEx/config/paul.no.clouds.cfg` (the name is kept from NO Clouds) and can be changed live from the F11 menu. "Reset this tab" restores the defaults of the open tab. The menu can also be shown in Portuguese with *General > Language*.

If performance is a problem, look at the *Quality* tab first:

- *Downscale*: 1 = full resolution, 2 = half (default), 4 = quarter.
- *Steps*: fewer steps run faster but show more banding in the distance.
- Turning off *Shadows > Shadows* and *Atmosphere > LightShafts* gives the biggest gain.
- *PerformanceMeter* shows how many milliseconds each part of the mod takes, right in the Quality tab. Please include those numbers when reporting a slowdown.
- *MapSlicing* and *ShadowUpdateEvery* (on by default since 1.2.0) save GPU without changing the look; turn them off only to compare.

## Nuclear blasts

The *Nuclear* tab controls how blasts affect the clouds: hole size, condensation rings, fireball light and how long the hole takes to close (20 minutes by default). F8 or the button on that tab sets off a test blast that only touches the clouds.

*Nuclear > UnlockNukes* (off by default) lets you pick nuclear weapons in any mission, including Free Flight, so you can see the effect without playing through escalation. It only works offline or as host, and the AI is not affected.

## Multiplayer

In multiplayer every player with NoAtmoFX sees the same clouds in the same places, players who join late included.

- The cloud field runs on the mission clock and weather the game already shares between players. This works on any server, including dedicated servers and hosts without mods, as long as all players use the same cloud settings. The *Multiplayer* tab shows a short settings code: if two players have the same code, they see the same clouds.
- When the host also runs NoAtmoFX, clients with the mod receive the host's cloud settings (Weather, Layer, Shape, Scale, Variation, Wind, Storm and Nuclear tabs), the exact drift and weather state, and the nuclear holes that are still open. Your own settings come back when you leave the game, and the config file is never overwritten with the host's values. Lighting, quality, glass and atmosphere settings stay your own. Turn this off with *Multiplayer > FollowHostSettings*.
- An F8 test blast set off by the host appears for every client with the mod. A test blast set off by a client is only shown to that client.
- A player or server without NoAtmoFX is not affected. The mod only sends its messages to players that announced they have it.

## Nuclear Rain

NoAtmoFX works with or without the Nuclear Rain mod. When Nuclear Rain is installed, rain on the canopy follows it and the rain around the camera is turned off so it is not doubled. Without it, NoAtmoFX uses its own rain under its own clouds. Either behaviour can be forced with *Glass > RainSource* and *Atmosphere > NearRain*.

## For mod authors

Other mods can switch NoAtmoFX and its effects, feed it weather and read its state through a small public API, with or without referencing the DLL. See [INTEGRATION.md](INTEGRATION.md).

## Known limitations

- Multiplayer: clouds match between players, but hiding in a cloud only hides you on your own screen (radar and IR use the game's cloud layer). Lightning flashes are random for each player.
- Multiplayer without NoAtmoFX on the host: if a mission trigger changes the wind mid-mission, players who join after the change see the clouds shifted. Weather changes that happen after you joined stay in step.
- Radar and IR seekers still use the game's own cloud layer.

## Building from source

See [BUILDING.md](BUILDING.md).

## License

GPL-3.0. See [LICENSE](LICENSE).

The wet canopy effect is based on my XR2WetGlass mod for Flight of Nova, and the cloud system started as a port of my FoN Clouds mod for the same game. Thunder uses the game's own sounds; no game assets are included in this repository or in the release.
