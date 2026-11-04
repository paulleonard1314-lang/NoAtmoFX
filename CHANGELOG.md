# Changelog

## 1.2.0 (2026-11-04)

Faster, same look. Only the plugin DLL changes; the shader bundle from 1.1.0 still works.

- Sea reflection: the clouds drawn into the map's reflection probe (refreshed by the game every 5 s) now reuse the camera's weather maps. Before, each of the 6 probe faces rebuilt all of them, and the camera rebuilt them again afterwards, which could cause a hitch every 5 seconds.
- Weather maps stay fixed in cloud space and are refreshed one map per frame; all of them are rebuilt only when the camera has moved about 1 km (*Quality > MapSlicing*).
- Cloud shadow maps: the ground shadow and the opacity depth map now take turns, each redrawn every other frame (*Quality > ShadowUpdateEvery*, 1 = old behaviour).
- Less CPU work per frame (shader property names are no longer hashed on every call, no per-probe garbage).
- New *Quality > PerformanceMeter*: shows the time taken by each part of the mod in the Quality tab.

## 1.1.0 (2026-11-04)

- Multiplayer: all players see the same clouds. Cloud drift, evolution and weather smoothing run on the mission clock that the game shares, so players who join late match too. This also works on servers without the mod.
- When the host runs NoAtmoFX it sends its cloud settings, the exact cloud state and the open nuclear holes to clients with the mod. The client's own settings are restored on leaving. A new *Multiplayer* tab has the options, the sync status and a settings code to compare between players.
- Nuclear rings now use the same random numbers for every player.

## 1.0.0 (2026-11-03)

First public release. The mod was called NO Clouds during development; it is now NoAtmoFX. The config file name and settings stay the same.

- Volumetric cumulus, stratus, mid-level layer, overcast deck, fronts, cumulonimbus and supercells driven by the mission weather.
- Cloud shadows, cloud-on-cloud shadowing, light shafts and sun rays.
- Rain curtains, near-camera rain, height fog, lightning with thunder, in-cloud turbulence.
- Wet canopy and frost.
- Nuclear blasts open a hole in the clouds and form condensation caps and skirts around the mushroom cloud (*Nuclear* tab, F8 test blast, optional UnlockNukes).
- Clouds in the sea reflection; the game's cirrus is kept.
- Weather changes smoothed over *Weather > ChangeTime* (10 min by default).
- Works with or without Nuclear Rain.
- English config, menu in English or Portuguese. Settings saved by the older Portuguese versions are migrated on first start.
