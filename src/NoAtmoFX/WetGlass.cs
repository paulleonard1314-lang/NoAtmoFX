// Wet canopy and frost (based on XR2WetGlass for Flight of Nova, rewritten for URP / Nuclear Option).
// - Drops: simulated on the CPU in an angular map around the airflow axis (see Drop), seen from the pilot's eye
//   in cockpit space. They spawn from cloud at the aircraft (GPU probe) and from rain under
//   thick clouds, merge, run with the relative wind leaving trails, and dry outside the cloud.
// - Frost: above the freezing level, inside cloud; grows from the edges of the glass.
// - Glass: the aircraft's own glass meshes (Canopy.glassRenderers) drawn again with the
//   WetGlass shader after the clouds, refracting a copy of the screen.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NoAtmoFX
{
    class NOCWetGlass
    {
        // drop position as an angle around the airflow axis: th = how far it has moved from the stagnation point
        // (0 = facing the wind, grows going back over the top/sides), ph = which side (0 = top).
        // This way the wind always pushes towards +th: a drop climbs the windscreen, passes over the pilot's head and runs
        // down the canopy to the end, instead of stopping at the top of an azimuth/elevation map.
        class Drop { public float th, ph, r, s1, s2, rot, wob, lat, sp, mth, mph; public bool mv; }

        const int TW = 1536, TH = 768;
        internal const float THMAX = 160f * Mathf.Deg2Rad;      // how far the map goes (almost straight back)
        const float PX = 0.225f * Mathf.Deg2Rad;                 // angular "pixel" of the simulation (drop size)
        static readonly Vector3 AX = new Vector3(0, -0.05234f, 0.99863f);   // axis: stagnation point (3° below eye level)
        static readonly Vector3 AU = new Vector3(0, 0.99863f, 0.05234f), AR = Vector3.right;
        readonly NOCloudsPlugin _p;
        internal Material Glass, Brush;
        internal RenderTexture DropRT, TrailRT, SceneRT, MaskRT, MaskTmp, EdgeRT;
        float _edgeT; readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        internal readonly List<Renderer> Renderers = new List<Renderer>();
        internal bool Active;                  // something to draw
        internal Matrix4x4 W2L; internal Vector3 EyeL; internal bool HaveEye;
        Aircraft _ac; float _scanT; bool _loggedNone;
        readonly List<Drop> _drops = new List<Drop>(), _born = new List<Drop>();
        readonly System.Random _rng = new System.Random(7);
        float _mergeT, _wet, _frost, _logT; int _trailActive;
        Mesh _mDrops, _mTrail, _mQuad;
        readonly List<Vector3> _v = new List<Vector3>(); readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<Color> _c = new List<Color>(); readonly List<int> _ix = new List<int>();
        static FieldInfo F_glass;

        // probe (filled by the GPU readback)
        internal float ProbeCloud, ProbeTau, ProbeCb, ProbeAlt; internal bool ProbeValid; internal float ProbeTime = -10;

        public NOCWetGlass(NOCloudsPlugin p) { _p = p; }

        float Rnd() => (float)_rng.NextDouble();
        int Count(float x) { int n = (int)x; if (Rnd() < x - n) n++; return n; }   // fractional count per frame

        public bool Init(AssetBundle ab)
        {
            Shader g = null, b = null;
            foreach (var n in ab.GetAllAssetNames())
            {
                if (n.EndsWith("nocwetglass.shader")) g = ab.LoadAsset<Shader>(n);
                if (n.EndsWith("nocwetbrush.shader")) b = ab.LoadAsset<Shader>(n);
            }
            if (g == null || b == null || !g.isSupported || !b.isSupported)
            {
                NOCloudsPlugin.Log.LogWarning("Wet glass: shaders missing from the bundle (run NoAtmoFX > Build bundle).");
                return false;
            }
            Glass = new Material(g); Brush = new Material(b);
            DropRT = NewRT("NOC drops"); TrailRT = NewRT("NOC trails");
            Glass.SetTexture("_DropTex", DropRT); Glass.SetTexture("_TrailTex", TrailRT); Glass.SetTexture("_EdgeTex", Texture2D.whiteTexture);
            _mDrops = new Mesh { name = "NOC drops" }; _mDrops.MarkDynamic();
            _mTrail = new Mesh { name = "NOC trails" }; _mTrail.MarkDynamic();
            _mQuad = new Mesh { name = "NOC quad" };
            _mQuad.SetVertices(new List<Vector3> { new Vector3(-1, -1), new Vector3(1, -1), new Vector3(1, 1), new Vector3(-1, 1) });
            _mQuad.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            _mQuad.SetColors(new List<Color> { Color.white, Color.white, Color.white, Color.white });
            _mQuad.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
            _mQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
            NOCloudsPlugin.Log.LogInfo("Wet glass: shaders ok.");
            return true;
        }

        static RenderTexture NewRT(string n)
        {
            var rt = new RenderTexture(TW, TH, 0, RenderTextureFormat.ARGBHalf) { name = n, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            rt.Create();
            var a = RenderTexture.active; RenderTexture.active = rt; GL.Clear(false, true, Color.clear); RenderTexture.active = a;
            return rt;
        }

        public void Release()
        {
            foreach (var r in new[] { DropRT, TrailRT, SceneRT, MaskRT, MaskTmp, EdgeRT }) if (r != null) r.Release();
        }

        // ---------- aircraft, glass and eye ----------
        void FindGlass(Aircraft ac)
        {
            Renderers.Clear(); HaveEye = false; _loggedNone = false;
            if (ac == null) return;
            if (F_glass == null) F_glass = typeof(Canopy).GetField("glassRenderers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var names = new List<string>();
            int nCan = 0, nRef = 0;
            foreach (var cn in ac.GetComponentsInChildren<Canopy>(true))
            {
                nCan++;
                if (F_glass?.GetValue(cn) is Renderer[] rs)
                    foreach (var r in rs) { nRef++; if (r != null && !Renderers.Contains(r)) { Renderers.Add(r); names.Add(Desc(r)); } }
            }
            int nRend = ac.GetComponentsInChildren<Renderer>(true).Length;
            if (Renderers.Count == 0 && ac.cockpit != null)
            {
                // the canopy may not be parented to the Aircraft object: search the scene for the one closest to the cockpit
                Vector3 cp = ac.cockpit.xform.position;
                foreach (var cn in UnityEngine.Object.FindObjectsOfType<Canopy>(true))
                {
                    if ((cn.transform.position - cp).sqrMagnitude > 20f * 20f) continue;
                    nCan++;
                    if (F_glass?.GetValue(cn) is Renderer[] rs)
                        foreach (var r in rs) { nRef++; if (r != null && !Renderers.Contains(r)) { Renderers.Add(r); names.Add(Desc(r)); } }
                }
            }
            if (Renderers.Count == 0)
            {
                // fallback: any glass material (game crack property, name, shader) or transparent material on the aircraft
                foreach (var r in ac.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.GetType().Name == "ParticleSystemRenderer" || r.GetType().Name == "LineRenderer" || r.GetType().Name == "TrailRenderer") continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        string n = (m.name + " " + r.name + " " + (m.shader != null ? m.shader.name : "")).ToLowerInvariant();
                        bool glassy = m.HasProperty("_Cracked_Amount") || n.Contains("glass") || n.Contains("canopy") || n.Contains("vidro")
                                      || n.Contains("windscreen") || n.Contains("windshield") || n.Contains("canopi");
                        bool hud = n.Contains("hud") || n.Contains("light") || n.Contains("lamp") || n.Contains("lens") || n.Contains("flare") || n.Contains("afterburner") || n.Contains("flame");
                        if (glassy && !hud) { Renderers.Add(r); names.Add(Desc(r)); break; }
                    }
                }
            }
            if (Renderers.Count == 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var r in ac.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (m != null && (m.renderQueue >= 2450 || r.name.ToLowerInvariant().Contains("cock") || r.name.ToLowerInvariant().Contains("can")))
                            sb.Append($" | {r.name} [{m.name} / {m.shader?.name} q{m.renderQueue}]");
                NOCloudsPlugin.Log.LogInfo($"Wet glass: {ac.name}: Canopy {nCan}, refs {nRef}, renderers on aircraft {nRend}, field {(F_glass != null ? F_glass.FieldType.Name : "MISSING")}. Candidates:{sb}");
            }
            NOCloudsPlugin.Log.LogInfo($"Wet glass: {ac.name}: {Renderers.Count} glass mesh(es): {string.Join("; ", names)}");
        }

        static string Desc(Renderer r) => $"{r.name} [{(r.sharedMaterial != null ? r.sharedMaterial.name + " / " + r.sharedMaterial.shader.name : "-")}]";

        internal Camera CockpitCam()
        {
            var csm = SceneSingleton<CameraStateManager>.i;
            if (csm == null || CameraStateManager.cameraMode != CameraMode.cockpit) return null;
            if (csm.cockpitCamRender != null && csm.cockpitCamRender.isActiveAndEnabled) return csm.cockpitCamRender;
            return csm.mainCamera;
        }

        // ---------- per frame ----------
        internal float DropSize = 0.5f, Airflow = 2.5f;
        internal float GameRain; internal bool RainNeedsCloud;   // rain from mission weather (high conditions, below the base), set by the plugin
        public void Step(float dt, bool enabled, float intensity, float freezeAlt, float rainTau, int test,
                         float frostReach, float frostWhite, float refract, Light sun)
        {
            if (Glass == null) { Active = false; return; }
            dt = Mathf.Min(dt, 0.05f);
            GameManager.GetLocalAircraft(out Aircraft ac);
            if (ac != _ac || (Time.time > _scanT && Renderers.Count == 0))
            {
                _ac = ac; _scanT = Time.time + 5; FindGlass(ac);
            }
            Renderers.RemoveAll(r => r == null);
            Camera cc = CockpitCam();
            Transform frame = ac != null && ac.cockpit != null ? ac.cockpit.xform : null;
            if (frame != null && cc != null && !HaveEye)
            {
                EyeL = frame.InverseTransformPoint(cc.transform.position); HaveEye = true;
                NOCloudsPlugin.Log.LogInfo($"Wet glass: pilot eye at {EyeL} (camera '{cc.name}', type {cc.GetUniversalAdditionalCameraData()?.renderType}).");
            }
            if (!enabled || ac == null || frame == null || Renderers.Count == 0)
            {
                if (Renderers.Count == 0 && ac != null && !_loggedNone) { _loggedNone = true; NOCloudsPlugin.Log.LogInfo("Wet glass: no glass found on this aircraft."); }
                Active = false; return;
            }
            W2L = frame.worldToLocalMatrix;
            Vector3 gl = frame.InverseTransformDirection(Vector3.down);   // gravity in cockpit space

            // inputs: cloud at the aircraft, ice from altitude, rain under a thick column
            bool valid = ProbeValid && Time.time - ProbeTime < 1f;
            float cloud = valid ? ProbeCloud : 0, tau = valid ? ProbeTau : 0, alt = valid ? ProbeAlt : 0;
            float ice = Mathf.Clamp01((alt - freezeAlt) / 800f);
            float rain = rainTau > 0 ? Mathf.Clamp01((tau - rainTau) / rainTau) : 0;
            // bad mission weather: rain below the base even if the measured column is thin
            rain = Mathf.Max(rain, GameRain * (!RainNeedsCloud || (valid && tau > 5f) ? 1f : 0f));
            if (test == 1) { cloud = 0.8f; ice = 0; }
            else if (test == 2) { cloud = 0.8f; ice = 1; }
            else if (test == 3) { cloud = 0; rain = 1; ice = 0; }
            float num4 = Mathf.Clamp01(cloud * 2f);
            float liquid = Mathf.Max(num4, rain) * (1 - ice);
            float num5 = liquid * intensity;
            float num6 = num4 * ice * intensity;
            var rb = ac.CockpitRB();
            float spd = rb != null ? rb.velocity.magnitude : 0f;
            float q = (spd / 60f) * (spd / 60f) * Airflow;   // Airflow: how early the wind drags the drops

            Active = _drops.Count > 0 || _wet > 0.01f || _frost > 0.005f || num5 > 0 || num6 > 0 || _trailActive > 0;
            if (!Active) return;

            // spawning
            // cloud mist: small droplets all over the glass
            float mist = num4 * (1 - ice) * intensity;
            int nb = Count(mist * (25f + spd * 0.45f) * dt * 3f);
            for (int i = 0; i < nb; i++)
                _drops.Add(new Drop { th = Mathf.Acos(1f - Rnd() * (1f - Mathf.Cos(THMAX))), ph = (Rnd() * 2f - 1f) * Mathf.PI,   // uniform on the sphere
                                      r = 0.4f + 5f * Mathf.Pow(Rnd(), 3.2f), s1 = Rnd(), s2 = Rnd(), rot = Rnd() * 6.283f, wob = Rnd() * 6.283f });
            // rain: larger drops, landing where the rain hits (parked = top of the glass; moving = front)
            float rainAmt = rain * (1 - ice) * intensity;
            if (rainAmt > 0.001f)
            {
                Vector3 vel = rb != null ? rb.velocity : Vector3.zero;
                Vector3 rv = frame.InverseTransformDirection(new Vector3(0, -9f, 0) - vel).normalized;   // where the rain comes from, in cockpit space
                Vector3 upL = frame.InverseTransformDirection(Vector3.up);
                int nr = Count(rainAmt * (60f + spd * 0.6f) * dt * 3f * 2f);
                for (int i = 0; i < nr; i++)
                {
                    float th = Mathf.Acos(1f - Rnd() * (1f - Mathf.Cos(THMAX))), ph = (Rnd() * 2f - 1f) * Mathf.PI;
                    Vector3 n = (Dir(th, ph) + upL * 0.7f).normalized;   // sloped windscreen/canopy: normal pulled upwards
                    float exp = 0.3f + 0.7f * Mathf.Max(0, -Vector3.Dot(n, rv));
                    if (Rnd() > exp) continue;
                    _drops.Add(new Drop { th = th, ph = ph, r = 0.8f + 5f * Mathf.Pow(Rnd(), 2.2f), s1 = Rnd(), s2 = Rnd(), rot = Rnd() * 6.283f, wob = Rnd() * 6.283f });
                }
            }
            if (_drops.Count > 1200) _drops.RemoveRange(0, _drops.Count - 1200);
            if (Time.time > _logT)
            {
                _logT = Time.time + 5f;
                NOCloudsPlugin.Log.LogInfo($"Wet glass: cloud {cloud:F2} (probe {(valid ? "ok" : "NONE")}), tau {tau:F0}, alt {alt:F0}, ice {ice:F2}, rain {rain:F2} (game {GameRain:F2}), drops {_drops.Count}, wet {_wet:F2}, speed {spd:F0} m/s");
            }

            // merging
            if ((_mergeT += dt) > 0.1f)
            {
                _mergeT = 0;
                _drops.Sort((a, b) => a.th.CompareTo(b.th));
                for (int j = 0; j < _drops.Count; j++)
                {
                    var a = _drops[j]; if (a.r <= 0) continue;
                    Vector3 pa = Dir(a.th, a.ph);
                    for (int k = j + 1; k < _drops.Count && _drops[k].th - a.th < 12f * PX; k++)
                    {
                        var b = _drops[k]; if (b.r <= 0) continue;
                        float dd = Vector3.Angle(pa, Dir(b.th, b.ph)) * Mathf.Deg2Rad / PX;
                        if (dd * dd < (a.r + b.r) * (a.r + b.r) * 0.64f)
                        {
                            var big = a.r >= b.r ? a : b; var small = big == a ? b : a;
                            big.r = Mathf.Sqrt(a.r * a.r + b.r * b.r); big.s1 = Rnd(); small.r = 0;
                        }
                    }
                }
                _drops.RemoveAll(d => d.r <= 0);
            }

            // running + trails
            float dry = num5 > 0.05f ? 0.008f : 0.12f + q * 0.05f;
            Brush.SetFloat("_Fade", Mathf.Clamp01(1f - dry * dt * 3f));
            BeginMesh();
            const float thr = 0.5f;   // adhesion: gradual start (2.4 used to make it on/off at high speed)
            _trailActive = 0;
            foreach (var d in _drops)
            {
                // relative wind: leaves the stagnation point and runs along the canopy to the end (always +th);
                // gravity projected on the glass at that point (x = side/ph, y = backwards/th)
                float aero = 0.4f * q * Mathf.Pow(d.r, 1.5f);           // bigger drops feel the wind more
                float grav = 0.35f * d.r / (1f + q);                      // gravity (only wins at low speed)
                Basis(d.th, d.ph, out Vector3 eth, out Vector3 eph);
                float fx = Vector3.Dot(gl, eph) * grav, fy = aero + Vector3.Dot(gl, eth) * grav;
                float f = Mathf.Sqrt(fx * fx + fy * fy);
                if (f > thr)
                {
                    d.mv = true; _trailActive++;
                    d.wob += dt * (1.5f + Rnd() * 2f);
                    d.lat = (d.lat + (Rnd() - 0.5f) * 0.4f) * 0.95f;
                    float sp = (f - thr) * 16f * (1f + q * 0.15f) / (1f + d.r * 0.05f);   // starts slow; at high speed it is gone in a fraction of a second
                    float ux = fx / f, uy = fy / f, w = Mathf.Sin(d.wob) * 0.07f + d.lat * 0.1f;
                    float th0 = d.th, ph0 = d.ph;
                    d.th += (uy + ux * w) * sp * dt * PX;
                    d.ph += (ux - uy * w) * sp * dt * PX / Mathf.Max(Mathf.Sin(d.th), 0.05f);
                    if (d.th < 0) { d.th = -d.th; d.ph += Mathf.PI; }
                    if (d.ph > Mathf.PI) d.ph -= 2 * Mathf.PI; else if (d.ph < -Mathf.PI) d.ph += 2 * Mathf.PI;
                    d.mph = ux; d.mth = uy;   // stretched along the direction it runs
                    d.sp = sp;
                    if (Rnd() < 0.8f) Segment(th0, ph0, d.th, d.ph, Mathf.Max(0.75f, d.r * DropSize * (0.25f + Rnd() * 0.45f)), 0.35f + Rnd() * 0.3f);
                    d.r -= dt * (0.05f + sp * 0.0006f);   // loses little water in the trail, so it travels the whole canopy
                    if (Rnd() < dt * 6f && d.r > 1f)
                        _born.Add(new Drop { th = th0, ph = ph0, r = 0.3f + Rnd() * 0.5f, s1 = Rnd(), s2 = Rnd(), rot = Rnd() * 6.283f });
                }
                else { d.mv = false; d.sp = 0; }
                if (num5 < 0.05f) d.r -= dt * (0.12f + q * 0.06f);
            }
            EndMesh(_mTrail);
            _drops.AddRange(_born); _born.Clear();
            _drops.RemoveAll(d => d.r < 0.3f || d.th > THMAX);   // left through the end of the canopy

            // drops
            BeginMesh();
            foreach (var d in _drops)
            {
                // parked: round; moving: stretches with speed (drop -> tadpole -> streak), thins out to keep the volume
                float st = d.mv ? 1.15f + Mathf.Min(d.sp / 20f, 7f) : 1.12f;
                float sx = d.mv ? 1f / Mathf.Sqrt(st) : 1f, sy = st;
                float hx = d.r * sx * DropSize, hy = d.r * sy * DropSize, hgt = Mathf.Clamp01(0.35f + d.r * 0.18f);
                // a drop that was too thin fell between map pixels and vanished instead of becoming a streak:
                // minimum width of ~1 pixel, height reduced by the same ratio (same amount of water)
                const float MINW = 0.9f;
                if (hx < MINW) { hgt *= Mathf.Max(0.55f, Mathf.Sqrt(hx / MINW)); hx = MINW; }
                if (hy < MINW) hy = MINW;
                var c = new Color(hgt, d.s1, d.s2, d.mv ? 0.5f : 1f);
                float ux = d.mv ? d.mph : -Mathf.Sin(d.rot), uy = d.mv ? d.mth : Mathf.Cos(d.rot);
                DropQuad(d.th, d.ph, hx, hy, ux, uy, c);
            }
            EndMesh(_mDrops);

            var cmd = CommandBufferPool.Get("NOClouds glass (brush)");
            cmd.SetRenderTarget(TrailRT);
            cmd.DrawMesh(_mQuad, Matrix4x4.identity, Brush, 0, 1);
            cmd.DrawMesh(_mTrail, Matrix4x4.identity, Brush, 0, 2);
            cmd.SetRenderTarget(DropRT);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.DrawMesh(_mDrops, Matrix4x4.identity, Brush, 0, 0);
            // glass edge (for frost): project the glass meshes into the angular map and blur
            if (_frost > 0.002 && Time.time > _edgeT)
            {
                _edgeT = Time.time + 0.25f;
                if (MaskRT == null)
                {
                    MaskRT = new RenderTexture(256, 128, 0, RenderTextureFormat.R8) { name = "NOC glass", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; MaskRT.Create();
                    MaskTmp = new RenderTexture(256, 128, 0, RenderTextureFormat.R8) { name = "NOC glass tmp", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; MaskTmp.Create();
                    EdgeRT = new RenderTexture(256, 128, 0, RenderTextureFormat.R8) { name = "NOC edge", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; EdgeRT.Create();
                    Glass.SetTexture("_EdgeTex", EdgeRT);
                }
                Brush.SetMatrix("_AcW2L", W2L); Brush.SetVector("_EyeL", EyeL);
                cmd.SetRenderTarget(MaskRT);
                cmd.ClearRenderTarget(false, true, Color.clear);
                foreach (var r in Renderers)
                {
                    if (r == null || !r.gameObject.activeInHierarchy) continue;
                    int n = Mathf.Max(1, r.sharedMaterials.Length);
                    for (int s = 0; s < n; s++) cmd.DrawRenderer(r, Brush, s, 3);
                }
                _mpb.Clear(); _mpb.SetTexture("_MainTex", MaskRT); _mpb.SetVector("_BlurStep", new Vector4(2f / 256, 2f / 128));
                cmd.SetRenderTarget(MaskTmp);
                cmd.DrawMesh(_mQuad, Matrix4x4.identity, Brush, 0, 4, _mpb);
                _mpb.Clear(); _mpb.SetTexture("_MainTex", MaskTmp); _mpb.SetVector("_BlurStep", new Vector4(3f / 256, 3f / 128));
                cmd.SetRenderTarget(EdgeRT);
                cmd.DrawMesh(_mQuad, Matrix4x4.identity, Brush, 0, 4, _mpb);
            }
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            _wet = Mathf.Clamp01(_wet + (num5 * 0.35f - (1f - liquid) * (0.08f + q * 0.03f)) * dt);
            _frost = Mathf.Clamp01(_frost + (num6 * 0.09f - (1f - num4 * ice) * 0.02f) * dt);
            Glass.SetFloat("_Wetness", _wet);
            Glass.SetFloat("_Film", _wet * Mathf.Clamp01((spd - 90f) / 110f));   // from 320 km/h it turns into a film
            Glass.SetFloat("_Frost", _frost * 0.35f * frostReach);
            Glass.SetFloat("_FrostWhite", frostWhite);
            Glass.SetFloat("_Refract", refract);
            Glass.SetMatrix("_AcW2L", W2L);
            Glass.SetMatrix("_AcL2W", frame.localToWorldMatrix);
            Glass.SetVector("_EyeL", EyeL);
            Color sc = sun != null && sun.isActiveAndEnabled ? sun.color * sun.intensity : Color.black;
            Vector3 sl = sun != null ? frame.InverseTransformDirection(-sun.transform.forward) : Vector3.up;
            Glass.SetVector("_SunL", sl);
            Glass.SetColor("_SunCol", sc);
            Color amb = RenderSettings.ambientSkyColor;
            Glass.SetColor("_FrostCol", new Color(0.86f, 0.9f, 0.95f) * Mathf.Clamp((sc.grayscale * 0.35f + amb.grayscale * 0.8f), 0.03f, 1.2f));
        }

        // ---------- brush meshes (clip coordinates) ----------
        void BeginMesh() { _v.Clear(); _uv.Clear(); _c.Clear(); _ix.Clear(); }
        void EndMesh(Mesh m)
        {
            m.Clear();
            m.SetVertices(_v); m.SetUVs(0, _uv); m.SetColors(_c);
            m.SetIndices(_ix, MeshTopology.Triangles, 0);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
        }
        void Add(float px, float py, float u, float v, Color c)
        {
            _v.Add(new Vector3(px / TW * 2f - 1f, py / TH * 2f - 1f, 0)); _uv.Add(new Vector2(u, v)); _c.Add(c);
        }
        void Tri4()
        {
            int b = _v.Count - 4;
            _ix.Add(b); _ix.Add(b + 1); _ix.Add(b + 2); _ix.Add(b); _ix.Add(b + 2); _ix.Add(b + 3);
        }
        // ---------- map (angle around the airflow axis) ----------
        static Vector3 Dir(float th, float ph) => AX * Mathf.Cos(th) + (AU * Mathf.Cos(ph) + AR * Mathf.Sin(ph)) * Mathf.Sin(th);
        static void Basis(float th, float ph, out Vector3 eth, out Vector3 eph)
        {
            Vector3 rad = AU * Mathf.Cos(ph) + AR * Mathf.Sin(ph);
            eth = rad * Mathf.Cos(th) - AX * Mathf.Sin(th);       // backwards (away from the stagnation point)
            eph = AR * Mathf.Cos(ph) - AU * Mathf.Sin(ph);        // sideways
        }
        static float MX(float ph) => (ph / (2f * Mathf.PI) + 0.5f) * TW;
        static float MY(float th) => th / THMAX * TH;
        // factor: 1 angular simulation "pixel" in map pixels (sideways and backwards)
        static float KX(float th) => PX / (2f * Mathf.PI * Mathf.Max(Mathf.Sin(th), 0.05f) / TW);
        const float KY = PX / (THMAX / TH);
        // drop quad: hx wide, hy along (ux,uy) = direction (side, back) it runs
        void DropQuad(float th, float ph, float hx, float hy, float ux, float uy, Color c)
        {
            float cx = MX(ph), cy = MY(th), kx = KX(th);
            float sx = uy, sy = -ux;   // drop side
            float ext = (Mathf.Abs(sx) * hx + Mathf.Abs(ux) * hy) * kx;
            for (int rep = -1; rep <= 1; rep++)
            {
                float ox = rep * TW;
                if (rep != 0 && (cx + ox + ext < 0 || cx + ox - ext > TW)) continue;   // only repeat if it crosses the bottom seam
                void V(float lx, float ly, float u, float v) => Add(cx + ox + (lx * sx + ly * ux) * kx, cy + (lx * sy + ly * uy) * KY, u, v, c);
                V(-hx, -hy, -1, -1); V(hx, -hy, 1, -1); V(hx, hy, 1, 1); V(-hx, hy, -1, 1); Tri4();
            }
        }
        void Segment(float th0, float ph0, float th1, float ph1, float w, float a)
        {
            float x0 = MX(ph0), y0 = MY(th0), x1 = MX(ph1), y1 = MY(th1);
            if (Mathf.Abs(x1 - x0) > TW * 0.5f) return;   // crossed the seam
            float kx = KX((th0 + th1) * 0.5f);
            // direction in equal angular units, for the correct width
            float ax = (x1 - x0) / kx, ay = (y1 - y0) / KY, l = Mathf.Sqrt(ax * ax + ay * ay);
            if (l < 0.01f) return;
            float ox = -ay / l * w * kx, oy = ax / l * w * KY;
            var c = new Color(a, 0, 0, 1);
            Add(x0 - ox, y0 - oy, 0, -1, c); Add(x1 - ox, y1 - oy, 1, -1, c); Add(x1 + ox, y1 + oy, 1, 1, c); Add(x0 + ox, y0 + oy, 0, 1, c); Tri4();
        }
    }

    // Draws the glass meshes with the wet glass shader, after the clouds.
    class WetGlassPass : ScriptableRenderPass
    {
        readonly NOCWetGlass _w;
        readonly ProfilingSampler _prof = new ProfilingSampler("NOClouds wet glass");
        public WetGlassPass(NOCWetGlass w) { _w = w; renderPassEvent = RenderPassEvent.AfterRenderingTransparents; }
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!_w.Active || _w.Glass == null || !_w.HaveEye) return;
            ref var cd = ref renderingData.cameraData;
            var desc = cd.cameraTargetDescriptor;
            int w = Mathf.Max(1, desc.width / 2), h = Mathf.Max(1, desc.height / 2);
            var cmd = CommandBufferPool.Get("NOClouds wet glass");
            using (new ProfilingScope(cmd, _prof))
            {
                if (_w.SceneRT == null || _w.SceneRT.width != w || _w.SceneRT.height != h)
                {
                    if (_w.SceneRT != null) _w.SceneRT.Release();
                    _w.SceneRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { name = "NOC screen", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    _w.SceneRT.Create();
                }
                var color = cd.renderer.cameraColorTargetHandle;
                cmd.Blit(color, _w.SceneRT);
                _w.Glass.SetTexture("_SceneTex", _w.SceneRT);
                _w.Glass.SetVector("_SceneTex_TS", new Vector4(1f / w, 1f / h, w, h));
                cmd.SetRenderTarget(color, cd.renderer.cameraDepthTargetHandle);
                foreach (var r in _w.Renderers)
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    int n = Mathf.Max(1, r.sharedMaterials.Length);
                    for (int s = 0; s < n; s++) cmd.DrawRenderer(r, _w.Glass, s, 0);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
