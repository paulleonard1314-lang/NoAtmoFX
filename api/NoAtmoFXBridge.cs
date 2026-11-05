// NoAtmoFXBridge.cs - drop this file into your own BepInEx plugin to talk to NoAtmoFX WITHOUT referencing
// NoAtmoFX.dll. If NoAtmoFX is not installed every call is a harmless no-op and Available is false.
// Needs only UnityEngine and BepInEx. API version 1 (NoAtmoFX 1.3.0+). MIT, copy freely.
//
// Optional: add [BepInDependency("paul.no.clouds", BepInDependency.DependencyFlags.SoftDependency)] to your plugin
// class so NoAtmoFX loads first. Call NoAtmoFXBridge.Init() after that (Awake or Start) or just call anything,
// it initialises itself on first use.
using System;
using System.Reflection;
using UnityEngine;

public static class NoAtmoFXBridge
{
    public const string Guid = "paul.no.clouds";
    static bool _init;
    static Type _t;

    /// <summary>NoAtmoFX is installed and its API is at least version 1.</summary>
    public static bool Available { get { Init(); return _t != null; } }
    public static int ApiVersion { get { Init(); return _t == null ? 0 : (int)_t.GetField("ApiVersion").GetRawConstantValue(); } }

    public static void Init()
    {
        if (_init) return;
        _init = true;
        try
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null) return;
            _t = info.Instance.GetType().Assembly.GetType("NoAtmoFX.NoAtmoFXApi");
            if (_t == null) Debug.Log("[NoAtmoFXBridge] NoAtmoFX found but it is older than 1.3.0 (no integration API).");
        }
        catch (Exception e) { Debug.LogWarning("[NoAtmoFXBridge] " + e.Message); _t = null; }
    }

    static object Call(string name, params object[] args)
    {
        Init();
        if (_t == null) return null;
        try
        {
            var types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();
            var m = _t.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, types, null);
            return m?.Invoke(null, args);
        }
        catch (Exception e) { Debug.LogWarning("[NoAtmoFXBridge] " + name + ": " + e.Message); return null; }
    }
    static T Get<T>(string prop)
    {
        Init();
        if (_t == null) return default;
        try { var p = _t.GetProperty(prop, BindingFlags.Public | BindingFlags.Static); return p != null ? (T)p.GetValue(null) : default; }
        catch { return default; }
    }

    // ---- switch / features (persist = write to the player's NoAtmoFX config) ----
    public static void SetEnabled(bool on, bool persist = true) => Call("SetEnabled", on, persist);
    public static bool IsEnabled => Get<bool>("IsEnabled");
    /// <summary>Clouds, Shadows, RainShafts, NearRain, Lightning, Turbulence, GodRays, LightShafts, Fog, WetGlass, NuclearClouds, KeepSun, Rainbow, Glory</summary>
    public static bool SetFeature(string name, bool on, bool persist = true) => Call("SetFeature", name, on, persist) is bool b && b;
    public static bool GetFeature(string name) => Call("GetFeature", name) is bool b && b;

    // ---- weather input: targets, the clouds glide to them over minutes. NaN / -1 = keep following the mission ----
    public static void SetConditions(float c) => Call("SetConditions", c);          // 0 fair .. 1 storm
    public static void SetCloudBase(float metres) => Call("SetCloudBase", metres);  // above sea level
    public static void SetWind(Vector3 v) => Call("SetWind", v);                    // m/s
    public static void ClearWind() => Call("ClearWind");
    public static void SetHumidityBias(float b) => Call("SetHumidityBias", b);
    public static void SetInstabilityBias(float b) => Call("SetInstabilityBias", b);
    public static void SetCoverageScale(float s) => Call("SetCoverageScale", s);
    public static void SetFront(int f) => Call("SetFront", f);                      // -1 auto, 0 none, 1 cold, 2 warm
    public static void SetTimeOfDay(float h) => Call("SetTimeOfDay", h);
    public static void SetLightningScale(float s) => Call("SetLightningScale", s);
    public static void ClearWeather() => Call("ClearWeather");

    // ---- state ----
    public static string WeatherSource => Get<string>("WeatherSource");   // mission / api / host / menu
    public static float Conditions => Get<float>("Conditions");
    public static float CloudBase => Get<float>("CloudBase");
    public static float CloudTop => Get<float>("CloudTop");
    public static float Coverage => Get<float>("Coverage");
    public static Vector3 CloudWind => Get<Vector3>("CloudWind");
    public static float CloudAtCamera => Get<float>("CloudAtCamera");
    public static float RainAtCamera => Get<float>("RainAtCamera");
    public static bool MultiplayerSynced => Get<bool>("MultiplayerSynced");
    public static bool IsHost => Get<bool>("IsHost");
    public static bool FollowingHost => Get<bool>("FollowingHost");

    // ---- events ----
    static bool Subscribe(string ev, Delegate d, bool add)
    {
        Init();
        if (_t == null) return false;
        try { var e = _t.GetEvent(ev, BindingFlags.Public | BindingFlags.Static); if (e == null) return false; if (add) e.AddEventHandler(null, d); else e.RemoveEventHandler(null, d); return true; }
        catch (Exception x) { Debug.LogWarning("[NoAtmoFXBridge] " + ev + ": " + x.Message); return false; }
    }
    public static bool OnEnabledChanged(Action<bool> h, bool add = true) => Subscribe("EnabledChanged", h, add);
    public static bool OnLightning(Action<Vector3, bool> h, bool add = true) => Subscribe("Lightning", h, add);
    public static bool OnNuclearBlast(Action<Vector3, float> h, bool add = true) => Subscribe("NuclearBlast", h, add);
}
