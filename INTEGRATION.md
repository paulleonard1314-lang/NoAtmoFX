# Integrating with NoAtmoFX

NoAtmoFX stays a stand-alone BepInEx plugin (GUID `paul.no.clouds`), released from this repository. Other mods,
for example an effects pack with its own menu and weather system, can drive it through a small public API
(`NoAtmoFX.NoAtmoFXApi`, API version 1, NoAtmoFX 1.3.0 and later). Nothing here is required: without any
other mod NoAtmoFX simply follows the mission weather.

## Two ways to call it

1. **No compile reference (recommended):** copy [`api/NoAtmoFXBridge.cs`](api/NoAtmoFXBridge.cs) into your
   plugin. It finds NoAtmoFX through the BepInEx chainloader and uses reflection; when NoAtmoFX is not
   installed every call is a no-op and `NoAtmoFXBridge.Available` is false.
2. **Normal reference:** reference `NoAtmoFX.dll`, add
   `[BepInDependency("paul.no.clouds", BepInDependency.DependencyFlags.SoftDependency)]`, and only touch
   `NoAtmoFXApi` after checking `Chainloader.PluginInfos.ContainsKey("paul.no.clouds")` (keep those calls in a
   separate method so the JIT never loads the type when the DLL is missing).

## Weather: easiest path first

NoAtmoFX reads the game's own weather from `LevelInfo`: `conditions` (0 fair .. 1 storm), `cloudHeight`
(cloud base, m) and `windVelocity`. Those are Mirage SyncVars, so **a weather system that writes them on the
server is followed by every client automatically**, including clients whose host or server has no
NoAtmoFX. If your server-side weather option already works that way, you need no API call for the clouds.

Use the API when you want more than those three numbers, or when your weather lives client side:

| Call | Meaning |
|---|---|
| `SetConditions(float 0..1)` | Overall weather, same scale as the game's `conditions`. |
| `SetCloudBase(float m)` | Cloud base above sea level. |
| `SetWind(Vector3 m/s)` / `ClearWind()` | Wind the clouds drift with (capped by Wind > MaxCloudSpeed). |
| `SetHumidityBias(float)` | More/less and lower/higher cloud; stratus when humid and stable. About -0.3..0.3. |
| `SetInstabilityBias(float)` | Taller cumulus and cumulonimbus. About -0.3..0.3. |
| `SetCoverageScale(float 0..2)` | Multiplies the final cloud cover. |
| `SetFront(int)` | -1 automatic, 0 none, 1 cold, 2 warm. |
| `SetTimeOfDay(float h)` | Hour for the day cycle of convection (not the sun). |
| `SetLightningScale(float)` | Multiplies the lightning rate; 0 = none. |
| `SetWeather(c, base, hum, ins, cov, front)` | All at once. |
| `ClearWeather()` | Back to following the mission. |

Pass `float.NaN` (or -1 for the int ones) to leave one value to the mission.

**Everything you set is a target, never a jump.** Conditions, base, humidity, instability, cover, front and the
day cycle glide to it over Weather > ChangeTime (10 minutes by default, player setting); the drift wind turns
and speeds up over about 2 minutes. Real clouds never change at once, and NoAtmoFX keeps that rule whatever the
input. So send your weather whenever it changes (or every few seconds); there is no need to ramp it yourself.

Priority: the player's own F11 override > the host's API input (multiplayer) > local API input > mission.
`WeatherSource` tells you which one is active (`menu`, `host`, `api`, `mission`).

## Multiplayer

- The cloud field runs on the mission clock the game shares, so every player with NoAtmoFX sees the same
  clouds, on any server, also dedicated ones without mods.
- When the host runs NoAtmoFX, it sends its cloud settings and weather state to clients that have it. Since 1.3.0
  that includes the host's API weather input: **call the weather setters on the host**; on clients that follow
  the host (`FollowingHost == true`) the host's input replaces their local one. Calling them on clients too is
  harmless.
- On/off and feature toggles are per player (display choices), never synced.

## Menu: switch and features

```csharp
NoAtmoFXBridge.SetEnabled(true);                 // "Volumetric" clouds on (F10 does the same)
NoAtmoFXBridge.SetEnabled(false);                // game clouds back ("Vanilla"/"Enhanced")
NoAtmoFXBridge.SetFeature("Lightning", false);
bool on = NoAtmoFXBridge.GetFeature("WetGlass");
```

Feature names: `Clouds`, `Shadows`, `RainShafts`, `NearRain`, `Lightning`, `Turbulence`, `GodRays`,
`LightShafts`, `Fog`, `WetGlass`, `NuclearClouds`, `KeepSun`. Strength effects return to the player's last
non-zero value when switched back on. `persist: true` (default) writes the player's NoAtmoFX config, like the
F11 menu; use `false` for temporary changes. Switching is immediate.

## State and events

Read-only: `IsEnabled`, `Conditions`, `CloudBase`, `CloudTop`, `Coverage`, `Humidity`, `Instability`, `Front`,
`CloudWind`, `CloudAtCamera` (0..1, GPU probe), `RainAtCamera` (0..1), `MultiplayerSynced`, `IsHost`,
`FollowingHost`, `Version`, `ApiVersion`.

Events (main thread): `EnabledChanged(bool)`, `Lightning(Vector3 worldPos, bool reachedGround)`,
`NuclearBlast(Vector3 worldPos, float kilotons)`. With the bridge: `OnEnabledChanged`, `OnLightning`,
`OnNuclearBlast`.

## Example: weather system driving the clouds

```csharp
void OnWeatherChanged(MyWeather w)   // on the host / server
{
    if (!NoAtmoFXBridge.Available) return;
    NoAtmoFXBridge.SetConditions(w.Severity01);
    NoAtmoFXBridge.SetCloudBase(w.CloudBaseMetres);
    NoAtmoFXBridge.SetWind(w.WindVelocity);
    NoAtmoFXBridge.SetHumidityBias(w.Humidity01 - 0.5f);
}
```

## Compatibility promise

`ApiVersion` only goes up when an existing call changes meaning. New calls can be added without raising it;
the bridge ignores calls the installed NoAtmoFX does not have.
