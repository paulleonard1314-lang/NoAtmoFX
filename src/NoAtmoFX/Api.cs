// Public integration API for other mods (for example an effects pack with its own menu and weather system).
//
// Stable surface: everything here is public static, uses only float/int/bool/string/Vector3/Action, and is
// versioned by ApiVersion, so another mod can call it either with a normal reference to NoAtmoFX.dll (soft
// dependency: check NoAtmoFXApi.Ready / Chainloader first) or with no reference at all through reflection
// (see api/NoAtmoFXBridge.cs in the repository, a drop-in file).
//
// Rules the API keeps:
// - The cloud field never jumps. Weather sent here is a TARGET: conditions, base, humidity, instability, front
//   and time of day follow it over Weather > ChangeTime (10 min by default), the drift wind over ~2 min.
//   Only the on/off switch and feature toggles are immediate (they are a player's display choice).
// - In multiplayer the host is the authority: the weather the host's mods send here is passed to every client
//   that has NoAtmoFX, and on those clients the host's input replaces their local one.
// - Values not set here keep following the mission (LevelInfo.conditions / cloudHeight / windVelocity), which
//   the game already syncs to every client. A weather system that writes those on the server needs no API at all.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NoAtmoFX
{
    public static class NoAtmoFXApi
    {
        /// <summary>Raised only when the surface changes in a way old callers would notice.</summary>
        public const int ApiVersion = 1;
        public const string PluginGuid = "paul.no.clouds";

        internal static NOCloudsPlugin P;

        /// <summary>The plugin is loaded and its bundle/shaders are in.</summary>
        public static bool Ready => P != null;
        public static string Version => NOCloudsPlugin.Version;

        // ------------------------------------------------------------------ switch and features

        /// <summary>Volumetric clouds on/off (same as F10 / General > Enabled). Off gives the game's own clouds back.
        /// persist = also write it to the player's config file (use true for a menu choice).</summary>
        public static void SetEnabled(bool on, bool persist = true)
        {
            if (P == null) return;
            P.SetEntry(P._on, on, persist);
        }
        public static bool IsEnabled => P != null && P._on.Value;

        /// <summary>Fired on the main thread whenever the clouds are switched on or off (by anyone: API, F10, menu).</summary>
        public static event Action<bool> EnabledChanged;
        internal static void RaiseEnabled(bool on) { try { EnabledChanged?.Invoke(on); } catch (Exception e) { NOCloudsPlugin.Log.LogWarning("API EnabledChanged handler: " + e.Message); } }

        /// <summary>Names accepted by SetFeature/GetFeature.</summary>
        public static readonly string[] Features =
        {
            "Clouds", "Shadows", "RainShafts", "NearRain", "Lightning", "Turbulence",
            "GodRays", "LightShafts", "Fog", "WetGlass", "NuclearClouds", "KeepSun"
        };

        /// <summary>Turns one effect on or off. Strength effects (Lightning, GodRays...) go back to the player's
        /// last non-zero value when turned on again. Returns false for an unknown name.</summary>
        public static bool SetFeature(string name, bool on, bool persist = true)
        {
            if (P == null || string.IsNullOrEmpty(name)) return false;
            return P.SetFeature(name, on, persist);
        }
        public static bool GetFeature(string name) => P != null && P.GetFeature(name);

        // ------------------------------------------------------------------ weather input
        // NaN (or -1 for the int ones) = "not set by me, keep following the mission".

        /// <summary>Overall weather 0 = fair sky ... 0.6 overcast ... 1 = storm (the game's 'conditions' scale).</summary>
        public static void SetConditions(float conditions) => In.Conditions = Clamp01OrNaN(conditions);
        /// <summary>Cloud base above sea level, metres.</summary>
        public static void SetCloudBase(float metres) => In.Base = float.IsNaN(metres) ? float.NaN : Mathf.Max(0, metres);
        /// <summary>Wind the clouds drift with, m/s, world axes (y ignored). Capped by Wind > MaxCloudSpeed.</summary>
        public static void SetWind(Vector3 velocity) { In.WindX = velocity.x; In.WindZ = velocity.z; In.HasWind = true; }
        public static void ClearWind() { In.HasWind = false; }
        /// <summary>Added to the model's humidity (more/lower cloud, stratus when stable). About -0.3..0.3.</summary>
        public static void SetHumidityBias(float bias) => In.Hum = bias;
        /// <summary>Added to the model's instability (taller cumulus, cumulonimbus). About -0.3..0.3.</summary>
        public static void SetInstabilityBias(float bias) => In.Ins = bias;
        /// <summary>Multiplies the final cloud cover (1 = as the model says).</summary>
        public static void SetCoverageScale(float scale) => In.Cov = float.IsNaN(scale) ? float.NaN : Mathf.Clamp(scale, 0, 2);
        /// <summary>Front: 0 none, 1 cold, 2 warm; -1 = automatic/config.</summary>
        public static void SetFront(int front) => In.Front = front < 0 || front > 2 ? -1 : front;
        /// <summary>Local hour 0..24 used by the day cycle of convection (not the sun); NaN = the mission's.</summary>
        public static void SetTimeOfDay(float hour) => In.Tod = float.IsNaN(hour) ? float.NaN : Mathf.Repeat(hour, 24f);
        /// <summary>Multiplies the lightning rate (0 = none even in storms).</summary>
        public static void SetLightningScale(float scale) => In.Lightning = float.IsNaN(scale) ? float.NaN : Mathf.Max(0, scale);

        /// <summary>Sets everything at once; NaN / -1 entries are left as "not set".</summary>
        public static void SetWeather(float conditions, float cloudBase, float humidityBias, float instabilityBias, float coverageScale, int front)
        {
            SetConditions(conditions); SetCloudBase(cloudBase); SetHumidityBias(humidityBias);
            SetInstabilityBias(instabilityBias); SetCoverageScale(coverageScale); SetFront(front);
        }

        /// <summary>Back to following the mission for everything set through the API. The clouds glide back.</summary>
        public static void ClearWeather() { In = WeatherInput.None; }

        /// <summary>Who drives the weather right now: "mission", "api", "host" (multiplayer, host's input) or "menu" (F11 override).</summary>
        public static string WeatherSource => P == null ? "none" : P.WeatherSourceName();

        // ------------------------------------------------------------------ state (read only, smoothed values the clouds use)

        public static float Conditions => P != null ? P._cond : 0;
        public static float CloudBase => P != null ? P._effBottom : 0;
        public static float CloudTop => P != null ? P._effTop : 0;
        public static float Coverage => P != null ? P._effCoverage : 0;
        public static float Humidity => P != null ? P._H : 0;
        public static float Instability => P != null ? P._I : 0;
        public static int Front => P != null ? P._front : 0;
        /// <summary>Wind the clouds are drifting with now (smoothed, capped), m/s.</summary>
        public static Vector3 CloudWind => P != null ? P._windS : Vector3.zero;
        /// <summary>Cloud density at the camera 0..1 (GPU probe, a frame or two late).</summary>
        public static float CloudAtCamera => P != null && P.ProbeAtValid ? P.ProbeCloudAt : 0;
        /// <summary>Rain at the camera 0..1 from these clouds.</summary>
        public static float RainAtCamera => P?.Atmos != null && P.Atmos.ProbeRainValid ? Mathf.Clamp01(P.Atmos.ProbeRain * 1.4f) : 0;
        /// <summary>Multiplayer: true when the clouds run on the shared mission clock.</summary>
        public static bool MultiplayerSynced => P?.Sync != null && P.Sync.Shared;
        public static bool IsHost => P?.Sync != null && P.Sync.Shared && P.Sync.IsHost;
        /// <summary>Client: the host runs NoAtmoFX and its clouds/weather are being followed.</summary>
        public static bool FollowingHost => P?.Sync != null && P.Sync.HostLive;

        // ------------------------------------------------------------------ events (main thread)

        /// <summary>Lightning: world position (floating origin space, like transform.position) and whether it reached the ground.</summary>
        public static event Action<Vector3, bool> Lightning;
        /// <summary>A nuclear blast reached the clouds: world position and yield in kilotons.</summary>
        public static event Action<Vector3, float> NuclearBlast;
        internal static void RaiseLightning(Vector3 w, bool ground) { try { Lightning?.Invoke(w, ground); } catch (Exception e) { NOCloudsPlugin.Log.LogWarning("API Lightning handler: " + e.Message); } }
        internal static void RaiseNuke(Vector3 w, float kt) { try { NuclearBlast?.Invoke(w, kt); } catch (Exception e) { NOCloudsPlugin.Log.LogWarning("API NuclearBlast handler: " + e.Message); } }

        // ------------------------------------------------------------------ internals

        static float Clamp01OrNaN(float v) => float.IsNaN(v) ? float.NaN : Mathf.Clamp01(v);

        internal struct WeatherInput
        {
            public float Conditions, Base, Hum, Ins, Cov, Tod, Lightning, WindX, WindZ;
            public int Front; public bool HasWind;
            public static WeatherInput None => new WeatherInput
            {
                Conditions = float.NaN, Base = float.NaN, Hum = float.NaN, Ins = float.NaN, Cov = float.NaN,
                Tod = float.NaN, Lightning = float.NaN, Front = -1, HasWind = false
            };
            public bool Any => !float.IsNaN(Conditions) || !float.IsNaN(Base) || !float.IsNaN(Hum) || !float.IsNaN(Ins) || !float.IsNaN(Cov)
                            || !float.IsNaN(Tod) || !float.IsNaN(Lightning) || Front >= 0 || HasWind;

            public void Write(System.IO.BinaryWriter w)
            {
                w.Write(Conditions); w.Write(Base); w.Write(Hum); w.Write(Ins); w.Write(Cov); w.Write(Tod); w.Write(Lightning);
                w.Write(WindX); w.Write(WindZ); w.Write(Front); w.Write(HasWind);
            }
            public static WeatherInput Read(System.IO.BinaryReader r) => new WeatherInput
            {
                Conditions = r.ReadSingle(), Base = r.ReadSingle(), Hum = r.ReadSingle(), Ins = r.ReadSingle(), Cov = r.ReadSingle(),
                Tod = r.ReadSingle(), Lightning = r.ReadSingle(), WindX = r.ReadSingle(), WindZ = r.ReadSingle(), Front = r.ReadInt32(), HasWind = r.ReadBoolean()
            };
        }

        /// <summary>Local input from other mods.</summary>
        internal static WeatherInput In = WeatherInput.None;
        /// <summary>Input the clouds use this frame: the host's in multiplayer when it sends one, else the local one.</summary>
        internal static WeatherInput Effective => P?.Sync != null && P.Sync.HostLive && P.Sync.HostInputValid ? P.Sync.HostInput : In;
    }
}
