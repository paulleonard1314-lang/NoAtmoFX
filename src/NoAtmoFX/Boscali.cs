// Adapter for the Boscali weather of the Unified Client Effects pack (plugin "aaron.boscali.weather-unified").
//
// That weather runs its own weather field (fronts, storm cells, moving "tape") and writes the result into the
// game's LevelInfo, but it caps LevelInfo.conditions at 0.6 (except in its extreme storm) because the game's own
// sky turns into a patterned grey sheet above that. So following LevelInfo would never show its storms.
// This adapter reads its live, uncapped values instead (WeatherManager.Live: CurrentConditions,
// CurrentCloudHeight, CurrentWindVelocity) and feeds them in as weather input. The clouds still glide to them
// over Weather > ChangeTime. Nothing is needed on the other side; everything is read by reflection and the adapter
// switches itself off if the names change.
//
// It also avoids doubled effects: while that weather is on, our own fog and near-camera rain step aside when the
// pack's matching effects are on (Integration > YieldToBoscali).
using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace NoAtmoFX
{
    class NOCBoscali
    {
        const string Guid = "aaron.boscali.weather-unified";
        readonly ConfigEntry<bool> _follow, _yield;
        ConfigFile _theirs;
        PropertyInfo _live, _cond, _base, _wind, _enabled;
        bool _looked, _broken;
        float _nextRead;
        internal bool Present;      // plugin loaded and its weather manager found
        internal bool Active;       // its weather is on and feeding us this frame
        internal string Status = "not installed";

        public NOCBoscali(ConfigFile C)
        {
            _follow = C.Bind("Integration", "FollowBoscaliWeather", true, "If the Unified Client Effects weather (Boscali) is installed and on, the clouds follow its weather (conditions, cloud base, wind) instead of the mission's. Still slow: Weather > ChangeTime.");
            _yield = C.Bind("Integration", "YieldToBoscali", true, "While that weather is on, our fog and near-camera rain step aside when the pack's rain atmosphere / rain visuals are on, so they are not doubled.");
        }

        void Look()
        {
            _looked = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null) return;
                _theirs = info.Instance.Config;
                var t = info.Instance.GetType().Assembly.GetType("BoscaliSummer.Modules.Weather.Runtime.WeatherManager");
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _live = t?.GetProperty("Live", S);
                _cond = t?.GetProperty("CurrentConditions", I);
                _base = t?.GetProperty("CurrentCloudHeight", I);
                _wind = t?.GetProperty("CurrentWindVelocity", I);
                _enabled = t?.GetProperty("IsEnabled", I);
                Present = _live != null && _cond != null && _base != null && _wind != null;
                Status = Present ? "found" : "installed, but its weather could not be read (different version)";
                NOCloudsPlugin.Log.LogInfo("Boscali weather: " + Status + ".");
            }
            catch (Exception e) { Present = false; Status = "error: " + e.Message; NOCloudsPlugin.Log.LogWarning("Boscali weather: " + e.Message); }
        }

        bool Theirs(string key)
        {
            if (_theirs == null) return false;
            return _theirs.TryGetEntry<bool>(new ConfigDefinition("Weather", key), out var e) && e.Value;
        }

        /// <summary>Their weather is on and its fog/ambient writer runs: ours would fight it every frame.</summary>
        internal bool YieldFog => Active && _yield.Value && Theirs("RainAtmosphereEnabled");
        /// <summary>Their falling rain is on: ours near the camera would double it.</summary>
        internal bool YieldNearRain => Active && _yield.Value && Theirs("RainVisualsEnabled");

        /// <summary>Called every frame; writes the adapter input (read twice a second).</summary>
        public void Tick()
        {
            if (!_looked) Look();
            if (!Present || _broken || !_follow.Value) { Active = false; NoAtmoFXApi.Adapter = NoAtmoFXApi.WeatherInput.None; return; }
            if (Time.unscaledTime < _nextRead) return;
            _nextRead = Time.unscaledTime + 0.5f;
            try
            {
                object m = _live.GetValue(null);
                bool on = m != null && (_enabled == null || (bool)_enabled.GetValue(m)) && Theirs("Enabled")
                          && GameManager.gameState != GameState.Multiplayer;   // its weather is single-player only (values go stale in MP)
                if (!on) { Active = false; NoAtmoFXApi.Adapter = NoAtmoFXApi.WeatherInput.None; Status = GameManager.gameState == GameState.Multiplayer ? "installed, idle in multiplayer (its weather is single-player only)" : "installed, weather off or not running"; return; }
                float c = (float)_cond.GetValue(m), b = (float)_base.GetValue(m);
                Vector3 w = (Vector3)_wind.GetValue(m);
                if (float.IsNaN(c) || float.IsNaN(b)) return;
                var inp = NoAtmoFXApi.WeatherInput.None;
                inp.Conditions = Mathf.Clamp01(c);
                inp.Base = Mathf.Max(0, b);
                if (!float.IsNaN(w.x) && !float.IsNaN(w.z)) { inp.WindX = w.x; inp.WindZ = w.z; inp.HasWind = true; }
                NoAtmoFXApi.Adapter = inp;
                Active = true;
                Status = $"following (conditions {c:F2}, base {b:F0} m, wind {new Vector2(w.x, w.z).magnitude:F1} m/s)";
            }
            catch (Exception e)
            {
                _broken = true; Active = false; NoAtmoFXApi.Adapter = NoAtmoFXApi.WeatherInput.None;
                Status = "stopped: " + e.Message;
                NOCloudsPlugin.Log.LogWarning("Boscali weather: reading failed, adapter off (" + e.Message + ").");
            }
        }
    }
}
