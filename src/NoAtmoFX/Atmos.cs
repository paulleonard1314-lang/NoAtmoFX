// Atmospheric effects for NOClouds (independent of the Nuclear Rain mod):
// - lightning in actual cumulonimbus (Cb map read back from the GPU): in-cloud flash, cloud-to-ground bolt,
//   scene light and thunder (the game's own sounds, delayed by distance through ExplosionAudioManager)
// - rain streaks near the camera, amount taken from the GPU probe (same math as the rain curtains)
// - turbulence inside cumulus / cumulonimbus (forces on the player's aircraft)
// - sun rays between clouds (shader passes 10/11)
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NoAtmoFX
{
    class NOCAtmos
    {
        readonly NOCloudsPlugin _p;
        internal Material RainMat;
        Mesh _rainMesh;
        const int RAIN_N = 7000;

        // probe (second pixel): rain at the camera and regional Cb
        internal float ProbeRain, ProbeCb; internal bool ProbeRainValid;
        // Cb map (CPU)
        internal float CbMapTime = -10;
        float[] _cbTow = new float[0], _cbBase = new float[0]; int _cbN; float _cbExt; Vector2 _cbGD; float _cbSum;
        Vector3 _cbCamAtRead;

        // lightning
        float _nextStrike = 5, _flash, _flashPeak; Vector3 _flashW; readonly List<float> _pulses = new List<float>(); float _strikeT;
        Light _light; LineRenderer[] _bolts; Material _boltMat; float _boltT;
        AudioClip[] _thunder; bool _thunderSearched;
        readonly System.Random _rng = new System.Random(11);
        float R() => (float)_rng.NextDouble();

        // turbulence
        Aircraft _tac; Rigidbody[] _rbs = new Rigidbody[0]; float _tT; float _turbI;

        internal float RainHere;   // 0..1 rain at the camera (for drops and glass)

        public NOCAtmos(NOCloudsPlugin p) { _p = p; }

        public void Init(AssetBundle ab)
        {
            foreach (var n in ab.GetAllAssetNames())
                if (n.EndsWith("nocrain.shader")) { var sh = ab.LoadAsset<Shader>(n); if (sh != null && sh.isSupported) RainMat = new Material(sh); }
            if (RainMat == null) NOCloudsPlugin.Log.LogWarning("Rain: NOCRain shader missing from the bundle (run NoAtmoFX > Build bundle).");
            BuildRainMesh();
        }

        void BuildRainMesh()
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var sd = new List<Vector4>(); var ix = new List<int>();
            var r = new System.Random(5);
            for (int i = 0; i < RAIN_N; i++)
            {
                var s = new Vector4((float)r.NextDouble(), (float)r.NextDouble(), (float)r.NextDouble(), i % 2 == 0 ? 1 : 0);
                int b = v.Count;
                for (int k = 0; k < 4; k++) { v.Add(Vector3.zero); sd.Add(s); }
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(0, 1));
                ix.Add(b); ix.Add(b + 1); ix.Add(b + 2); ix.Add(b); ix.Add(b + 2); ix.Add(b + 3);
            }
            _rainMesh = new Mesh { name = "NOC rain", indexFormat = IndexFormat.UInt32 };
            _rainMesh.SetVertices(v); _rainMesh.SetUVs(0, uv); _rainMesh.SetUVs(1, sd);
            _rainMesh.SetIndices(ix, MeshTopology.Triangles, 0);
            _rainMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e7f);
        }

        // ---------- Cb map (readback) ----------
        internal void OnCbMap(NativeArray<Color> data, int n, float ext, Vector2 gd, Vector3 camAtRead)
        {
            if (_cbTow.Length != n * n) { _cbTow = new float[n * n]; _cbBase = new float[n * n]; }
            float sum = 0;
            for (int i = 0; i < n * n; i++) { float t = data[i].r; _cbTow[i] = t > 0.15f ? t : 0; _cbBase[i] = data[i].g * 8000f; sum += _cbTow[i]; }
            _cbN = n; _cbExt = ext; _cbGD = gd; _cbSum = sum; _cbCamAtRead = camAtRead;
        }

        bool PickCb(out Vector3 rel, out float baseAlt)
        {
            rel = Vector3.zero; baseAlt = 0;
            if (_cbSum <= 0) return false;
            float x = R() * _cbSum;
            for (int i = 0; i < _cbTow.Length; i++)
            {
                x -= _cbTow[i];
                if (x > 0) continue;
                int ix = i % _cbN, iy = i / _cbN;
                float u = (ix + R()) / _cbN, v = (iy + R()) / _cbN;
                float px = (u - 0.5f) * 2 * _cbExt - _cbGD.x, pz = (v - 0.5f) * 2 * _cbExt - _cbGD.y;
                rel = new Vector3(px, 0, pz); baseAlt = _cbBase[i];
                return true;
            }
            return false;
        }

        // ---------- per frame ----------
        public void Step(float dt, Camera cam, LevelInfo lvl, float cbTop, float lightningAmt, bool weatherLightning,
                         float rainHere)
        {
            RainHere = rainHere;
            // decaying flash (short pulses, like real lightning)
            _strikeT += dt;
            _flash = 0;
            for (int k = 0; k < _pulses.Count; k++)
            {
                float t = _strikeT - _pulses[k];
                if (t >= 0 && t < 0.35f) _flash = Mathf.Max(_flash, _flashPeak * Mathf.Exp(-t * 14f) * (k == 0 ? 1f : 0.8f));
            }
            if (_light != null)
            {
                _light.enabled = _flash > 0.01f;
                _light.intensity = _flash * 0.6f;
            }
            if (_bolts != null && _boltT > 0)
            {
                _boltT -= dt;
                float a = Mathf.Clamp01(_flash / Mathf.Max(_flashPeak, 0.01f) * 1.5f);
                foreach (var b in _bolts) if (b != null && b.enabled) { var c = new Color(0.85f, 0.9f, 1f, a); b.startColor = c; b.endColor = c; }
                if (_boltT <= 0) foreach (var b in _bolts) if (b != null) b.enabled = false;
            }

            if (cam == null || lvl == null || lightningAmt <= 0) return;
            // rate from the amount of Cb around (sum of the map); game weather with lightning raises it
            float rate = Mathf.Min(1.5f, _cbSum * 0.004f) * lightningAmt * (weatherLightning ? 1.6f : 1f);
            if (rate <= 0.001f) return;
            _nextStrike -= dt * rate;
            if (_nextStrike > 0) return;
            _nextStrike = -Mathf.Log(Mathf.Max(R(), 1e-3f)) * 6f;   // Poisson: mean of 6/rate s
            Strike(cam, cbTop, lightningAmt);
        }

        void Strike(Camera cam, float cbTop, float amt)
        {
            if (!PickCb(out Vector3 rel, out float baseAlt)) return;
            // map was read on an earlier frame: correct for how far the camera moved
            Vector3 cp = cam.transform.position;
            rel -= new Vector3(cp.x - _cbCamAtRead.x, 0, cp.z - _cbCamAtRead.z);
            float seaY = Datum.LocalSeaY;
            float camAlt = cp.y - seaY;
            bool ground = R() < 0.3f;
            float fAlt = ground ? baseAlt + 300 : baseAlt + (0.2f + 0.45f * R()) * (cbTop - baseAlt);
            _flashW = new Vector3(cp.x + rel.x, seaY + fAlt, cp.z + rel.z);
            float dist = (_flashW - cp).magnitude;
            _flashPeak = (ground ? 9f : 6f) * amt;
            _pulses.Clear(); _strikeT = 0;
            int np = 1 + (int)(R() * 3.5f);
            float t = 0;
            for (int k = 0; k < np; k++) { _pulses.Add(t); t += 0.06f + R() * 0.18f; }
            // scene light (terrain/aircraft), weak at range
            if (_light == null)
            {
                var go = new GameObject("NOC lightning light");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _light = go.AddComponent<Light>(); _light.type = LightType.Point; _light.shadows = LightShadows.None;
                _light.color = new Color(0.75f, 0.82f, 1f);
            }
            _light.transform.position = _flashW;
            _light.range = 14000f;
            if (ground) Bolt(_flashW, seaY, dist);
            Thunder(_flashW, cp, dist, ground);
        }

        void Bolt(Vector3 top, float seaY, float dist)
        {
            if (_boltMat == null)
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) return;
                _boltMat = new Material(sh); _boltMat.color = new Color(6f, 6.5f, 8f, 1f);   // HDR so it catches bloom
            }
            if (_bolts == null)
            {
                _bolts = new LineRenderer[3];
                for (int i = 0; i < 3; i++)
                {
                    var go = new GameObject("NOC bolt " + i); UnityEngine.Object.DontDestroyOnLoad(go);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.sharedMaterial = _boltMat; lr.useWorldSpace = true; lr.numCapVertices = 0;
                    lr.shadowCastingMode = ShadowCastingMode.Off; lr.receiveShadows = false; lr.enabled = false;
                    _bolts[i] = lr;
                }
            }
            // ground: bolt down to the terrain (or the sea)
            float groundY = seaY;
            if (Physics.Raycast(top, Vector3.down, out RaycastHit hit, top.y - seaY + 50f, ~0, QueryTriggerInteraction.Ignore)) groundY = hit.point.y;
            float w = Mathf.Max(5f, dist * 0.0009f);
            var main = Jagged(top, new Vector3(top.x, groundY, top.z), 40, 0.09f);
            Set(_bolts[0], main, w);
            for (int b = 1; b < 3; b++)
            {
                if (R() < 0.35f) { _bolts[b].enabled = false; continue; }
                int at = 4 + (int)(R() * (main.Length * 0.6f));
                Vector3 s = main[at];
                Vector3 dir = new Vector3(R() - 0.5f, -0.8f - R() * 0.4f, R() - 0.5f).normalized;
                float len = (top.y - groundY) * (0.15f + 0.25f * R());
                Set(_bolts[b], Jagged(s, s + dir * len, 16, 0.12f), w * 0.5f);
            }
            _boltT = 0.6f;
        }

        Vector3[] Jagged(Vector3 a, Vector3 b, int n, float rough)
        {
            var pts = new Vector3[n + 1];
            float L = (b - a).magnitude;
            Vector3 off = Vector3.zero;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                if (i > 0 && i < n) off += new Vector3(R() - 0.5f, 0, R() - 0.5f) * (L * rough / Mathf.Sqrt(n)) * 2f;
                off *= 0.92f;
                pts[i] = Vector3.Lerp(a, b, t) + off * Mathf.Sin(t * Mathf.PI);
            }
            return pts;
        }
        static void Set(LineRenderer lr, Vector3[] p, float w)
        {
            lr.positionCount = p.Length; lr.SetPositions(p); lr.startWidth = w; lr.endWidth = w * 0.6f; lr.enabled = true;
        }

        static FieldInfo F_thunder;
        void Thunder(Vector3 at, Vector3 cam, float dist, bool ground)
        {
            if (dist > 25000f) return;
            if (!_thunderSearched)
            {
                _thunderSearched = true;
                try
                {
                    F_thunder = typeof(Lightning).GetField("thunderClips", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    foreach (var l in Resources.FindObjectsOfTypeAll<Lightning>())
                        if (F_thunder?.GetValue(l) is AudioClip[] c && c.Length > 0) { _thunder = c; break; }
                    NOCloudsPlugin.Log.LogInfo(_thunder != null ? $"Lightning: {_thunder.Length} thunder sounds found in the game." : "Lightning: game thunder sounds not found (no sound).");
                }
                catch (Exception e) { NOCloudsPlugin.Log.LogWarning("Lightning: thunder: " + e.Message); }
            }
            if (_thunder == null || _thunder.Length == 0) return;
            var mgr = SceneSingleton<ExplosionAudioManager>.i;
            if (mgr == null) return;
            var go = new GameObject("NOC thunder");
            go.transform.SetParent(Datum.origin);
            go.transform.position = new Vector3(at.x, Mathf.Lerp(at.y, cam.y, 0.7f), at.z);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.clip = _thunder[_rng.Next(_thunder.Length)];
            src.spatialBlend = 1f; src.dopplerLevel = 0f; src.spread = 60f;
            src.rolloffMode = AudioRolloffMode.Linear; src.minDistance = 500f; src.maxDistance = 26000f;
            src.volume = ground ? 1f : 0.7f;
            src.pitch = 0.85f + R() * 0.25f;
            var lp = go.AddComponent<AudioLowPassFilter>();
            mgr.AddExplosionAudio(src, lp, 0f);   // the game plays it when the sound arrives (340 m/s) and muffles it with distance
            UnityEngine.Object.Destroy(go, dist / 340f + 40f);
        }

        // ---------- turbulence ----------
        public void Fixed(float dt, float cloudAtCam, float cbAtCam, float strength)
        {
            if (strength <= 0) return;
            GameManager.GetLocalAircraft(out Aircraft ac);
            if (ac == null) { _tac = null; return; }
            if (ac != _tac) { _tac = ac; _rbs = ac.GetComponentsInChildren<Rigidbody>(); }
            var crb = ac.CockpitRB();
            if (crb == null) return;
            float spd = crb.velocity.magnitude;
            if (spd < 25f) return;
            // the probe samples at the camera, so this only applies with the camera on the aircraft (cockpit / close)
            var mc = Camera.main;
            if (mc != null && (mc.transform.position - crb.position).sqrMagnitude > 150f * 150f) { _turbI = Mathf.MoveTowards(_turbI, 0, dt); return; }
            // inside cloud: cumulus shakes, cumulonimbus shakes a lot (updrafts up to ~15 m/s)
            float target = Mathf.Clamp01(cloudAtCam * 1.5f) * (0.5f + 1.2f * Mathf.Clamp01(cbAtCam * 3f));
            _turbI = Mathf.MoveTowards(_turbI, target, dt * 0.8f);
            if (_turbI < 0.01f) return;
            // gusts: the aircraft crosses 40-150 m gusts, so frequency grows with speed
            _tT += dt * spd / 90f;
            float gz = (Mathf.PerlinNoise(_tT, 0.3f) - 0.5f) * 2f + (Mathf.PerlinNoise(_tT * 2.7f, 5.1f) - 0.5f) * 0.8f;
            float gr = (Mathf.PerlinNoise(_tT * 1.3f, 9.7f) - 0.5f) * 2f;
            float gy = (Mathf.PerlinNoise(_tT * 0.9f, 13.3f) - 0.5f) * 2f;
            float qf = Mathf.Clamp(spd / 150f, 0.4f, 1.6f);
            float accZ = gz * 6f * _turbI * strength * qf;                  // m/s² along the aircraft's vertical
            float roll = gr * 1.6f * _turbI * strength * qf;                // rad/s² of roll
            float yaw = gy * 0.4f * _turbI * strength * qf;
            Transform fr = crb.transform;
            Vector3 up = fr.up, rt = fr.right, fw = fr.forward;
            Vector3 c = crb.worldCenterOfMass;
            foreach (var rb in _rbs)
            {
                if (rb == null || rb.isKinematic) continue;
                Vector3 r = rb.worldCenterOfMass - c;
                float lat = Vector3.Dot(r, rt), lon = Vector3.Dot(r, fw);
                // roll = vertical force proportional to lateral offset; yaw = lateral force proportional to longitudinal offset
                Vector3 a = up * (accZ + roll * lat) + rt * (yaw * lon);
                rb.AddForce(a * rb.mass, ForceMode.Force);
            }
        }

        // ---------- drawing (pass after transparents) ----------
        internal void DrawRain(CommandBuffer cmd, Camera cam, float amt, Vector3 camVel, Vector3 wind, Color light)
        {
            if (RainMat == null || amt < 0.01f) return;
            Vector3 rainW = wind + Vector3.down * 9f;
            RainMat.SetVector("_RainCam", cam.transform.position);
            RainMat.SetVector("_RainVelW", rainW);
            RainMat.SetVector("_RainVelRel", rainW - camVel);
            RainMat.SetFloat("_RainBox", 60f);
            RainMat.SetFloat("_RainAmt", Mathf.Clamp01(amt) * 0.55f);
            RainMat.SetFloat("_RainTime", Time.time % 1000f);
            RainMat.SetFloat("_RainExp", 1f / 60f);
            Color fl = new Color(0.75f, 0.82f, 1f) * (_flash * 0.25f);
            RainMat.SetColor("_RainCol", light + fl);
            cmd.DrawMesh(_rainMesh, Matrix4x4.identity, RainMat, 0, 0);
        }

        // lightning uniforms for the cloud shader (camera-relative position)
        internal void Apply(Material m, Camera cam)
        {
            Vector3 rel = _flashW - cam.transform.position;
            m.SetVector("_FlashP", new Vector4(rel.x, rel.y, rel.z, _flash));
            m.SetVector("_FlashCol", new Vector4(0.8f, 0.86f, 1f, 1f));
        }

        internal void Release()
        {
            if (_light != null) UnityEngine.Object.Destroy(_light.gameObject);
            if (_bolts != null) foreach (var b in _bolts) if (b != null) UnityEngine.Object.Destroy(b.gameObject);
        }
    }

    // After transparents, on the main camera: rain near the camera and sun rays.
    class AtmosPass : ScriptableRenderPass
    {
        readonly NOCloudsPlugin _p;
        readonly ProfilingSampler _prof = new ProfilingSampler("NOClouds atmosphere");
        internal RenderTexture RaysRT, ShaftRT, ShaftH0, ShaftH1; int _shFrame = -10; Vector3 _shCamFwd;
        public AtmosPass(NOCloudsPlugin p) { _p = p; renderPassEvent = RenderPassEvent.AfterRenderingTransparents; ConfigureInput(ScriptableRenderPassInput.Depth); }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ref var cd = ref renderingData.cameraData;
            var cmd = CommandBufferPool.Get("NOClouds atmosphere");
            using (new ProfilingScope(cmd, _prof))
            {
                var color = cd.renderer.cameraColorTargetHandle;
                // sun rays: mask (sky x cloud transmittance) blurred towards the sun, added to the screen
                if (_p.RaysActive && _p.CloudRT != null && _p.RTFrame == Time.frameCount && _p.Mat != null)
                {
                    int w = Mathf.Max(1, cd.cameraTargetDescriptor.width / 4), h = Mathf.Max(1, cd.cameraTargetDescriptor.height / 4);
                    if (RaysRT == null || RaysRT.width != w || RaysRT.height != h)
                    {
                        if (RaysRT != null) RaysRT.Release();
                        RaysRT = new RenderTexture(w, h, 0, RenderTextureFormat.RHalf) { name = "NOC rays", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                        RaysRT.Create();
                    }
                    cmd.Blit(_p.CloudRT, RaysRT, _p.Mat, 10);
                    cmd.Blit(RaysRT, color, _p.Mat, 11);
                }
                // volumetric light shafts (cloud shadow in the air), 1/4 resolution
                if (_p.ShaftsActive && _p.Mat != null && _p.RTFrame == Time.frameCount)
                {
                    // same resolution as the clouds (Quality > Downscale); it used to be fixed at 1/4 and looked blocky
                    int ds = _p.DownscaleNow;
                    int w = Mathf.Max(1, cd.cameraTargetDescriptor.width / ds), h = Mathf.Max(1, cd.cameraTargetDescriptor.height / ds);
                    bool fresh = false;
                    if (ShaftRT == null || ShaftRT.width != w || ShaftRT.height != h)
                    {
                        foreach (var old in new[] { ShaftRT, ShaftH0, ShaftH1 }) if (old != null) old.Release();
                        RenderTexture Mk(string n) { var t = new RenderTexture(w, h, 0, RenderTextureFormat.RHalf) { name = n, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; t.Create(); return t; }
                        ShaftRT = Mk("NOC feixes"); ShaftH0 = Mk("NOC feixes h0"); ShaftH1 = Mk("NOC feixes h1");
                        fresh = true;
                    }
                    cmd.Blit((Texture)null, ShaftRT, _p.Mat, 12);
                    // temporal average (~6 frames); resets after skipped frames or a large camera turn
                    var fwd = cd.camera.transform.forward;
                    if (fresh || Time.frameCount - _shFrame > 2) fresh = true;
                    // the history is not reprojected: any head/camera turn smears it (ghosting around the aircraft),
                    // so the blend goes to 1 quickly with the turn rate (0.15 deg per frame = no history)
                    float turn = Vector3.Angle(fwd, _shCamFwd);
                    float shBlend = fresh ? 1f : Mathf.Clamp01(0.25f + turn / 0.15f);
                    _shFrame = Time.frameCount; _shCamFwd = fwd;
                    var tmp = ShaftH0; ShaftH0 = ShaftH1; ShaftH1 = tmp;      // H1 = previous history, H0 = new
                    cmd.SetGlobalTexture("_ShHist", ShaftH1);
                    cmd.SetGlobalFloat("_ShBlend", shBlend);
                    cmd.Blit(ShaftRT, ShaftH0, _p.Mat, 15);
                    cmd.Blit(ShaftH0, color, _p.Mat, 13);
                }
                cmd.SetRenderTarget(color, cd.renderer.cameraDepthTargetHandle);
                _p.DrawNearRain(cmd, cd.camera);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
