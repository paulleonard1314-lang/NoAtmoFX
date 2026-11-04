// Nuclear blasts and the clouds: the shock front pushes the clouds out (a hole that grows with the front and a
// compressed rim), a short Wilson condensation shell, and pileus caps / velum skirts that form at the heights the
// rising mushroom cap pushes through. Fireball light on the clouds.
// Hook: Harmony postfix on the game's Shockwave.Start (yield in kt, position); the mushroom cap comes from the
// MushroomCloud in the same effect (renderer bounds), with a simple model as fallback.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using NuclearOption.Networking;

namespace NoAtmoFX
{
    class NOCNukes
    {
        const int MAX = 4;
        readonly NOCloudsPlugin _p;
        internal static NOCNukes I;

        internal ConfigEntry<bool> On, Unlock;
        internal ConfigEntry<float> HoleSize, Wilson, Rings, RingSize, Flash, Duration, MinYield, TestYield, TestDist;
        internal ConfigEntry<KeyboardShortcut> TestKey;

        class Blast
        {
            public Vector3 g;            // global position at detonation (y above sea level)
            public double wx, wz;        // cloud drift at detonation
            public float t0, kt, bp;
            public double tm;            // mission time of the blast (shared clock in multiplayer)
            public int seed;
            public Renderer cap;         // the game's mushroom cap (may be null -> model)
            public Vector3 capG;         // model cap: global position, carried by the game wind
            public float lastCapY, lastCapT;
            public readonly float[] ringH = new float[4], ringT = new float[4], ringMax = new float[4];
            public int nRings;
            public System.Random rng;
            public bool logged, hadCap;
            public Vector3 capNow; public float capRNow, tNow;
        }
        readonly List<Blast> _b = new List<Blast>();
        static readonly List<(Vector3 pos, float kt, Component src)> _pending = new List<(Vector3, float, Component)>();
        float _top;   // highest ring/cap altitude (for _VolTop)

        readonly Vector4[] _P = new Vector4[MAX], _S = new Vector4[MAX], _C = new Vector4[MAX], _L = new Vector4[MAX], _Rg = new Vector4[MAX * 4];

        public NOCNukes(NOCloudsPlugin p, ConfigFile C)
        {
            _p = p; I = this;
            AcceptableValueBase R(float a, float b) => new AcceptableValueRange<float>(a, b);
            On = C.Bind("Nuclear", "Enabled", true, "Nuclear blasts push the clouds: a hole opens behind the shock front, the cloud piles up at its rim, and condensation rings form around the mushroom cap.");
            HoleSize = C.Bind("Nuclear", "HoleSize", 7f, new ConfigDescription("Size of the hole the blast opens in the clouds, in blast radii ((kg TNT)^(1/3) m: 1 Mt = 1 km). 7 = 7 km for 1 Mt.", R(0, 20)));
            Wilson = C.Bind("Nuclear", "WilsonCloud", 0.7f, new ConfigDescription("Condensation shell behind the shock front in the first seconds (more in humid air). 0 = off.", R(0, 2)));
            Rings = C.Bind("Nuclear", "Rings", 1f, new ConfigDescription("Pileus caps and velum skirts around the rising mushroom cap. 0 = off.", R(0, 3)));
            RingSize = C.Bind("Nuclear", "RingSize", 8f, new ConfigDescription("How far the velum skirts spread, in blast radii (8 = 8 km for 1 Mt).", R(1, 25)));
            Flash = C.Bind("Nuclear", "FireballLight", 1f, new ConfigDescription("Light of the fireball on the clouds. 0 = off.", R(0, 4)));
            Duration = C.Bind("Nuclear", "Duration", 20f, new ConfigDescription("Minutes until the hole closes and the rings fade (slowly, over the second half).", R(2, 90)));
            MinYield = C.Bind("Nuclear", "MinYield", 0.5f, new ConfigDescription("Smallest blast (kilotons) that affects the clouds when the effect has no mushroom cloud.", R(0.001f, 100)));
            Unlock = C.Bind("Nuclear", "UnlockNukes", false, "Testing: the player can pick nuclear weapons in any mission (Free Flight too), ignoring escalation, rank and warhead stock. Only works in single player or when you are the host. AI is not affected.");
            TestKey = C.Bind("Nuclear", "TestBlastKey", new KeyboardShortcut(KeyCode.F8), "Test: a blast (clouds only, no damage) ahead of the camera.");
            TestYield = C.Bind("Nuclear", "TestYield", 1000f, new ConfigDescription("Test blast yield in kilotons.", R(1, 50000)));
            TestDist = C.Bind("Nuclear", "TestDistance", 25f, new ConfigDescription("Test blast distance ahead of the camera (km), 500 m above the sea.", R(3, 120)));
        }

        public void Patch()
        {
            try
            {
                var h = new Harmony("paul.no.clouds.nukes");
                var m = AccessTools.Method(typeof(Shockwave), "Start");
                h.Patch(m, postfix: new HarmonyMethod(typeof(NOCNukes).GetMethod(nameof(PostStart), BindingFlags.Static | BindingFlags.NonPublic)));
                NOCloudsPlugin.Log.LogInfo("Nuclear: hooked Shockwave.Start.");
                h.Patch(AccessTools.Method(typeof(WeaponChecker), "MountAllowedNuclear"), prefix: new HarmonyMethod(typeof(NOCNukes).GetMethod(nameof(PreAllowed), BindingFlags.Static | BindingFlags.NonPublic)));
                h.Patch(AccessTools.Method(typeof(Airbase), "RemoveWarheads"), prefix: new HarmonyMethod(typeof(NOCNukes).GetMethod(nameof(PreRemove), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { NOCloudsPlugin.Log.LogWarning("Nuclear: hook failed: " + e.Message); }
        }

        // UnlockNukes: the player's loadout ignores escalation/rank/stock (the AI asks with player == null and is untouched)
        static bool PreAllowed(Player player, ref bool __result)
        {
            if (I == null || !I.Unlock.Value || player == null) return true;
            __result = true; return false;
        }
        // ...and spawning with nukes the base does not have must not drive its stock negative
        static void PreRemove(Airbase __instance, ref int number)
        {
            if (I == null || !I.Unlock.Value || __instance == null) return;
            number = Mathf.Min(number, Mathf.Max(__instance.GetWarheads(), 0));
        }

        static FieldInfo F_kt;
        static void PostStart(Shockwave __instance)
        {
            try
            {
                if (I == null || !I.On.Value || __instance == null) return;
                if (F_kt == null) F_kt = AccessTools.Field(typeof(Shockwave), "yieldKilotons");
                float kt = (float)F_kt.GetValue(__instance);
                _pending.Add((__instance.transform.position, kt, __instance));
            }
            catch (Exception e) { NOCloudsPlugin.Log.LogWarning("Nuclear: " + e.Message); }
        }

        void Add(Vector3 world, float kt, Component src, double wx, double wz, bool test = false)
        {
            Renderer cap = null;
            if (src != null)
            {
                // the effect object is a child of Datum.origin (every effect is): search only inside it
                Transform root = src.transform;
                while (root.parent != null && root.parent != Datum.origin) root = root.parent;
                var mc = root.GetComponentInChildren<MushroomCloud>(true);
                if (mc != null)
                {
                    // MushroomCloud.cloud.meshRenderer = the cap
                    var fc = typeof(MushroomCloud).GetField("cloud", BindingFlags.Instance | BindingFlags.NonPublic);
                    var cl = fc?.GetValue(mc);
                    var fr = cl?.GetType().GetField("meshRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
                    cap = fr?.GetValue(cl) as Renderer;
                }
                if (mc == null && kt < MinYield.Value) { NOCloudsPlugin.Log.LogInfo($"Nuclear: blast of {kt * 1000:F1} t ignored (no mushroom cloud, below MinYield)."); return; }
            }
            Vector3 g0 = world - Datum.originPosition;
            var sy = _p.Sync;
            bool mp = sy != null && sy.Shared;
            double tm = mp ? sy.T : Time.timeSinceLevelLoad;
            // the same blast seen by another player: already there (the host also sends the blasts it sees)
            if (mp) { var seen = Near(g0, tm); if (seen != null) { if (seen.cap == null && cap != null) seen.cap = cap; return; } }
            // rings: same random numbers for every player (seed from the place, not the local time)
            int seed = Mathf.RoundToInt(g0.x / 250f) * 73856093 ^ Mathf.RoundToInt(g0.z / 250f) * 19349663 ^ Mathf.RoundToInt(kt * 10f);
            var b = New(g0, kt, wx, wz, tm, seed, 0f);
            b.cap = cap;
            if (mp && sy.IsHost && (src != null || test)) sy.HostBlast(Pack(new List<Blast> { b }, sy.T));
            NOCloudsPlugin.Log.LogInfo($"Nuclear: {kt:F2} kt at altitude {b.g.y:F0} m, blast radius {b.bp:F0} m, hole {b.bp * HoleSize.Value / 1000:F1} km, mushroom cap {(cap != null ? "found" : "model")}.");
        }

        Blast New(Vector3 g, float kt, double wx, double wz, double tm, int seed, float age)
        {
            var b = new Blast
            {
                g = g, wx = wx, wz = wz, t0 = Time.time - age, kt = kt, tm = tm, seed = seed,
                bp = Mathf.Pow(Mathf.Max(kt, 1e-4f) * 1e6f, 1f / 3f), rng = new System.Random(seed)
            };
            b.capG = b.g; b.lastCapY = b.g.y; b.lastCapT = b.t0;
            _b.Add(b);
            if (_b.Count > MAX) _b.RemoveAt(0);
            return b;
        }

        Blast Near(Vector3 g, double tm)
        {
            foreach (var x in _b) if (Math.Abs(x.tm - tm) < 20 && (x.g - g).sqrMagnitude < 1500f * 1500f) return x;
            return null;
        }

        // ---- multiplayer: blasts the host knows, for players who join later or did not see them ----
        internal byte[] Pack(double now, bool all) => Pack(_b, now);
        byte[] Pack(List<Blast> list, double now)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write((byte)list.Count);
            foreach (var b in list)
            {
                w.Write(b.g.x); w.Write(b.g.y); w.Write(b.g.z); w.Write(b.kt);
                w.Write(b.wx); w.Write(b.wz); w.Write(b.tm); w.Write(b.seed);
                w.Write(now - b.tm);   // age at the host
            }
            return ms.ToArray();
        }

        internal void Unpack(byte[] d, double now)
        {
            var r = new BinaryReader(new MemoryStream(d));
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var g = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); float kt = r.ReadSingle();
                double wx = r.ReadDouble(), wz = r.ReadDouble(), tm = r.ReadDouble(); int seed = r.ReadInt32(); double age = r.ReadDouble();
                if (!On.Value || age > Duration.Value * 60f) continue;
                var have = Near(g, tm);
                if (have != null) { have.wx = wx; have.wz = wz; continue; }   // seen here too: take the host's drift anchor
                var b = New(g, kt, wx, wz, tm, seed, (float)Math.Max(0, age));
                FastForward(b, (float)age);
                NOCloudsPlugin.Log.LogInfo($"Nuclear: {kt:F2} kt blast from the host, {age:F0} s ago.");
            }
        }

        // a blast that started before we joined: run the cap/ring model up to now (the game's mushroom is not here)
        void FastForward(Blast b, float age)
        {
            const float h = 0.5f;
            var lvl = NetworkSceneSingleton<LevelInfo>.i;
            Vector3 wind = lvl != null ? lvl.windVelocity : Vector3.zero; wind.y = 0;
            b.capG = b.g + wind * age;
            for (float t = h; t <= age; t += h)
            {
                ModelCap(b, t, out Vector3 cG, out float cR);
                float rise = (cG.y - b.lastCapY) / h; b.lastCapY = cG.y;
                TryRing(b, t, cG, cR, rise);
                b.capNow = cG; b.capRNow = cR; b.tNow = t;
            }
            b.lastCapT = Time.time;
        }

        void TryRing(Blast b, float t, Vector3 cG, float cR, float rise)
        {
            // a new layer every ~1.2-2 blast radii above the last one, ahead of the rising cap (pileus first)
            if (b.nRings < 4 && t > 3 && rise > 2f)
            {
                float sp = b.bp * (1.2f + 0.8f * (float)b.rng.NextDouble());
                float hh = cG.y + 2.3f * cR;
                if ((b.nRings == 0 && hh > b.g.y + 2.5f * b.bp) || (b.nRings > 0 && hh > b.ringH[b.nRings - 1] + sp))
                {
                    b.ringH[b.nRings] = hh; b.ringT[b.nRings] = t;
                    b.ringMax[b.nRings] = RingSize.Value * b.bp * (0.6f + 0.6f * (float)b.rng.NextDouble());
                    b.nRings++;
                }
            }
        }

        // model cap: rises to ~Hc above the burst in a couple of minutes and widens
        void ModelCap(Blast b, float t, out Vector3 cG, out float cR)
        {
            float Hc = Mathf.Min(1200 + 9.5f * b.bp, 18000);
            float k = 1 - Mathf.Exp(-t / 50f);
            cG = new Vector3(b.capG.x, b.g.y + Hc * k + 100, b.capG.z);
            cR = 0.18f * Hc * (0.25f + 0.75f * (1 - Mathf.Exp(-t / 70f)));
        }

        public void Step(float dt, LevelInfo level, Camera cam, double wx, double wz)
        {
            foreach (var e in _pending) Add(e.pos, e.kt, e.src, wx, wz);
            _pending.Clear();
            if (On.Value && (TestKey.Value.IsDown() || TestNow) && cam != null)
            {
                Vector3 f = cam.transform.forward; f.y = 0; f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
                Vector3 w = cam.transform.position + f * TestDist.Value * 1000f;
                w.y = Datum.originPosition.y + 500f;
                Add(w, TestYield.Value, null, wx, wz, true);
                TestNow = false;
            }
            float life = Duration.Value * 60f;
            for (int i = _b.Count - 1; i >= 0; i--)
            {
                var b = _b[i];
                float t = Time.time - b.t0;
                if (t > life) { _b.RemoveAt(i); continue; }
                Vector3 wind = level != null ? level.windVelocity : Vector3.zero; wind.y = 0;
                b.capG += wind * dt;
                // cap and rings
                Vector3 cG; float cR;
                if (b.cap != null && b.cap.enabled && b.cap.gameObject.activeInHierarchy)
                {
                    Bounds bb = b.cap.bounds;
                    cG = bb.center - Datum.originPosition; cR = Mathf.Max(bb.extents.x, bb.extents.z); b.hadCap = true;
                    if (!b.logged && t > 20) { b.logged = true; NOCloudsPlugin.Log.LogInfo($"Nuclear: cap at {cG.y - b.g.y:F0} m above the burst, radius {cR:F0} m (t={t:F0}s)."); }
                }
                else if (b.hadCap) { b.cap = null; cG = b.capNow; cR = b.capRNow; }   // the game's cloud is gone: rings stay where they are
                else ModelCap(b, t, out cG, out cR);
                float rise = (cG.y - b.lastCapY) / Mathf.Max(Time.time - b.lastCapT, 1e-3f);
                if (Time.time - b.lastCapT > 0.5f) { b.lastCapY = cG.y; b.lastCapT = Time.time; }
                TryRing(b, t, cG, cR, rise);
                b.capNow = cG; b.capRNow = cR; b.tNow = t;
            }
        }

        // uniforms (per camera)
        public void Apply(Material m, Camera cam, Vector3 gCam, double wx, double wz, float hum)
        {
            int n = (On.Value) ? Mathf.Min(_b.Count, MAX) : 0;
            m.SetFloat("_NkN", n);
            _top = 0;
            if (n == 0) return;
            float life = Duration.Value * 60f;
            float bestL = 0; Color lc = Color.white;
            for (int i = 0; i < n; i++)
            {
                var b = _b[i];
                float t = b.tNow, bp = b.bp;
                Vector3 c = b.g - gCam + new Vector3((float)(wx - b.wx), 0, (float)(wz - b.wz));   // drifts with the clouds
                float str = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.5f * life, life, t));
                float R = 0.5f * bp + 340f * t;
                float RhMax = HoleSize.Value * bp;
                float Rh = RhMax * (1 - Mathf.Exp(-R / Mathf.Max(RhMax, 1)));
                float wil = Wilson.Value * (0.4f + 0.6f * hum) * Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.8f * bp, 1.6f * bp, R)) * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(2.5f * bp, 5f * bp, R)));
                _P[i] = new Vector4(c.x, c.y, c.z, str);
                _S[i] = new Vector4(R, Rh, bp, wil);
                Vector3 cc = b.capNow - gCam;
                _C[i] = new Vector4(cc.x, cc.y, cc.z, b.capRNow);
                for (int j = 0; j < 4; j++)
                {
                    if (j >= b.nRings) { _Rg[i * 4 + j] = Vector4.zero; continue; }
                    float age = t - b.ringT[j], r0 = b.capRNow * 1.6f;
                    float rO = Mathf.Max(r0, r0 + (b.ringMax[j] - r0) * (1 - Mathf.Exp(-age / 45f)));
                    _Rg[i * 4 + j] = new Vector4(b.ringH[j] - gCam.y, rO, 0, 0);
                    _top = Mathf.Max(_top, b.ringH[j] + 0.9f * b.capRNow + 400);
                }
                // fireball light: bright flash, then a long orange glow
                float tau = 2f + 4f * Mathf.Pow(bp / 1000f, 0.4f);
                float L = Flash.Value * (14f * Mathf.Exp(-t / (0.25f * tau)) + 3f * Mathf.Exp(-t / tau) + 0.4f * Mathf.Exp(-t / (6 * tau)));
                Vector3 fp = Vector3.Lerp(c, cc, Mathf.Clamp01(t / (3 * tau)));
                _L[i] = new Vector4(fp.x, fp.y, fp.z, L);
                if (L > bestL) { bestL = L; lc = Color.Lerp(new Color(1f, 0.93f, 0.82f), new Color(1f, 0.42f, 0.16f), Mathf.Clamp01(t / (2.5f * tau))); }
            }
            m.SetVectorArray("_NkP", _P); m.SetVectorArray("_NkS", _S); m.SetVectorArray("_NkC", _C);
            m.SetVectorArray("_NkRg", _Rg); m.SetVectorArray("_NkL", _L);
            m.SetVector("_NkLC", lc);
            m.SetFloat("_NkRing", Rings.Value * (0.45f + 0.35f * hum));
        }

        internal bool TestNow;   // button in the F11 menu
        internal float TopAlt => _top;
        internal bool Active => On.Value && _b.Count > 0;
    }
}
