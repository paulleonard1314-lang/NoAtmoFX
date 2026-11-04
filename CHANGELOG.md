# Changelog

## 1.1.0 (2026-11-04)

- Multiplayer: all players see the same clouds. Cloud drift, evolution and weather smoothing run on the mission clock that the game shares, so players who join late match too. This also works on servers without the mod.
- When the host runs NoAtmoFX it sends its cloud settings, the exact cloud state and the open nuclear holes to clients with the mod. The client's own settings are restored on leaving. A new *Multiplayer* tab has the options, the sync status and a settings code to compare between players.
- Nuclear rings now use the same random numbers for every player.

## 1.0.1 (2026-11-04)

- Rain near the camera no longer shows up as bright white dots in clear sky. Drops are now dark, like the base of a rain cloud, and only fall when there is cloud overhead.
- Fixed a big frame drop when looking along the direction of travel in rain: drops right next to the camera were stretched over the whole screen.

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
