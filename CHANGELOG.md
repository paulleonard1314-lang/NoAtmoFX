# Changelog

## 1.4.1 (unreleased)

- Default quality is now the **High** preset (Ultra's look with temporal 2x2). Configs still on the 1.4 factory values move to High once; settings changed by hand are kept.

## 1.4.0 (2026-11-07)

Faster raymarch, same look. Needs the new shader bundle (with the old bundle the DLL simply skips the new parts).

- Empty-space skipping (*Quality > EmptySkip*, on): a 512x512 map of how far each spot is from the nearest place a cloud could be (cumulus cells grown by the largest billow, warp and turbulence, plus every column with stratus, mid layer or cumulonimbus), turned into distances by a jump flood. The raymarch jumps over clear air between clouds instead of walking through it. The map is conservative, so no cloud is cut. Mountain fog: jumps only above its top. Nuclear blasts: jumps stay outside the shock front and the ring discs instead of being switched off everywhere for 20 minutes.
- Cheaper sunlight where it cannot be told apart (*Quality > LightLOD*, on in Low/Medium/High, off in Ultra): beyond ~18-28 km and deep inside a cloud (less than ~25% of the view left), half the light samples (same total length) and no fine detail. Dithered, so there is no visible line.
- The benchmark gains a step with both off.
- Blasts below 50 t are ignored (conventional bombs with a mushroom effect made the clouds several times slower for 20 minutes) (1.3.1).

## 1.3.0 (included in 1.4.0)

Integration API for other mods (effects packs, weather systems). See [INTEGRATION.md](INTEGRATION.md). Only the plugin DLL changes; without another mod nothing behaves differently.

- `NoAtmoFX.NoAtmoFXApi`: switch the clouds and single effects on/off, feed weather (conditions, cloud base, wind, humidity, instability, cover, front, day cycle, lightning), read the cloud state, and listen to lightning, nuclear blasts and on/off changes.
- Weather from the API is a target: the clouds still glide to it over *Weather > ChangeTime*, never at once.
- Multiplayer: the host's API weather is sent to clients with NoAtmoFX and replaces theirs (host is the authority).
- `api/NoAtmoFXBridge.cs`: drop-in file to call NoAtmoFX without referencing its DLL.
- Built-in adapter for the Unified Client Effects (Boscali) weather: the clouds follow its live, uncapped weather in single player, and our fog and near-camera rain step aside when its matching effects are on (*Integration* section).
- The F11 weather line shows the weather source (mission, api, boscali, host, menu).
- Cumulus undersides are no longer cut flat at the layer base: each cloud sits a little higher, its bottom curves up toward the edges and is softer (*Shape > BaseRoughness*, 0 = old look). Needs the new shader bundle.
- Quality presets (*Quality > Preset*: Low, Medium, High, Ultra, Custom), also in the API (`Presets`, `SetPreset`, `CurrentPreset`, `PresetChanged`) for other mods' menus. Ultra keeps the look of previous versions.
- Temporal reprojection (*Quality > Temporal*, off by default, on in High/Medium/Low): only a quarter (2x2) or half (checkerboard) of the cloud pixels is raymarched per frame; the rest is carried over from the previous frames, moved with the camera and the wind, and kept within the colours of the fresh pixels around it at the same distance, so nothing ghosts around the aircraft. Lightning, nuclear flashes and camera cuts are drawn in full. The benchmark gains a step with temporal on/off. Needs the new shader bundle; with the old one, clouds are simply drawn in full.
- DOORMAN preview and GitHub update check embedded in the DLL. Releases are tagged `vX.Y.Z` with the asset `NoAtmoFX_vX.Y.Z.zip`.

## 1.2.0 (2026-11-04)

Faster, same look. Only the plugin DLL changes (the rain fix from 1.0.1 still needs a new shader bundle if you have not built it yet).

- Sea reflection: the clouds drawn into the map's reflection probe (refreshed by the game every 5 s) now reuse the camera's weather maps. Before, each of the 6 probe faces rebuilt all of them, and the camera rebuilt them again afterwards, which could cause a hitch every 5 seconds.
- Weather maps stay fixed in cloud space and are refreshed one map per frame; all of them are rebuilt only when the camera has moved about 1 km (*Quality > MapSlicing*).
- Cloud shadow maps: the ground shadow and the opacity depth map now take turns, each redrawn every other frame (*Quality > ShadowUpdateEvery*, 1 = old behaviour).
- Less CPU work per frame (shader property names are no longer hashed on every call, no per-probe garbage).
- New *Quality > PerformanceMeter*: shows the time taken by each part of the mod in the Quality tab.

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
