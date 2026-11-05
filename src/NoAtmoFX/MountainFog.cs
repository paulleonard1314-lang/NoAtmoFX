// Mountain fog: valley fog and upslope (orographic) cloud, a separate layer that never changes the normal clouds.
//
// Needs the terrain height, which the shader does not have: a 128x128 height map (40 km, ~310 m cells) around the
// camera is read with downward raycasts, a few hundred per frame and only while it is being (re)built, and
// rebuilt in the background when the camera has flown a quarter of it. From it: valley floor (local minimum) and
// ridge level (local maximum).
//
// Both are rare on purpose, they only show up when the weather really makes them:
// - Valley fog (radiation fog): night to mid-morning, light wind, clear-ish sky, moist air, and only in real
//   valleys (at least ~150 m of relief). It fills the valley up to a flat level and burns off after sunrise.
// - Upslope cloud / cap: moist air and a real wind; cloud hugs the windward slopes and the peaks that reach to
//   within a few hundred metres of the cloud base, and evaporates on the lee side.
// Strengths are smoothed over about a minute, so they fade in and out slowly.
using BepInEx.Configuration;
using UnityEngine;

namespace NoAtmoFX
{
    class NOCMountainFog
    {
        const int N = 128;
        const float Size = 40000f;
        const int RaysPerFrame = 512;
        readonly ConfigEntry<float> _valley, _cap;

        Texture2D _tex;
        readonly float[] _h = new float[N * N], _tmp = new float[N * N], _floor = new float[N * N], _ridge = new float[N * N];
        Vector2 _c, _cNew;          // map centres (global xz)
        bool _valid, _building;
        int _next, _frame = -1;
        float _hMin, _hMax;
        float _vS, _cS;
        internal float Valley, Cap;
        internal ConfigEntry<float> ValleyEntry => _valley;
        internal ConfigEntry<float> CapEntry => _cap;

        public NOCMountainFog(ConfigFile C)
        {
            _valley = C.Bind("Atmosphere", "ValleyFog", 1f, new ConfigDescription("Valley fog in the mountains: night to mid-morning, light wind, clear-ish sky, moist air. Fills real valleys up to a flat level and burns off after sunrise. Rare on purpose. 0 = off.", new AcceptableValueRange<float>(0, 2)));
            _cap = C.Bind("Atmosphere", "MountainCloud", 1f, new ConfigDescription("Upslope cloud on mountains: with moist air and wind, cloud hugs windward slopes and peaks that reach close to the cloud base, gone on the lee side. Rare on purpose. 0 = off.", new AcceptableValueRange<float>(0, 2)));
        }

        static float S(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

        public bool Active => _valid && (Valley > 0.002f || Cap > 0.002f);

        /// <summary>Once per frame (main camera): weather strengths and the height map build.</summary>
        public void Tick(Vector3 camG, float tod, float cond, float hum, Vector3 wind)
        {
            if (_frame == Time.frameCount) return;
            float dt = _frame < 0 ? 1e3f : Time.unscaledDeltaTime;
            _frame = Time.frameCount;

            float ws = new Vector2(wind.x, wind.z).magnitude;
            float morning = S(0.5f, 3.5f, tod) * (1 - S(8f, 10.5f, tod));
            float vT = _valley.Value * morning * (1 - S(2.5f, 6f, ws)) * (1 - S(0.35f, 0.6f, cond)) * S(0.5f, 0.65f, hum);
            float cT = _cap.Value * S(0.62f, 0.8f, hum) * S(4f, 9f, ws);
            float k = 1 - Mathf.Exp(-dt / 60f);
            _vS += (vT - _vS) * k; _cS += (cT - _cS) * k;
            Valley = _vS; Cap = _cS;

            if (_valley.Value <= 0 && _cap.Value <= 0) return;
            Vector2 cg = new Vector2(camG.x, camG.z);
            if (!_building && (!_valid || Mathf.Abs(cg.x - _c.x) > Size * 0.25f || Mathf.Abs(cg.y - _c.y) > Size * 0.25f))
            {
                _building = true; _next = 0;
                _cNew = new Vector2(Mathf.Round(cg.x / 1000f) * 1000f, Mathf.Round(cg.y / 1000f) * 1000f);
            }
            if (_building) Build();
        }

        void Build()
        {
            Vector3 o = Datum.originPosition;
            float top = 9000f, cell = Size / N;
            int end = Mathf.Min(_next + RaysPerFrame, N * N);
            for (int i = _next; i < end; i++)
            {
                int x = i % N, z = i / N;
                float gx = _cNew.x + (x + 0.5f - N * 0.5f) * cell, gz = _cNew.y + (z + 0.5f - N * 0.5f) * cell;
                var from = new Vector3(gx + o.x, top + o.y, gz + o.z);
                float h = 0;
                if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, top + 200f, ~0, QueryTriggerInteraction.Ignore))
                    h = Mathf.Max(0, hit.point.y - o.y);
                _h[i] = h;
            }
            _next = end;
            if (_next < N * N) return;

            // valley floor: local minimum (~1.5 km) then smoothed; ridge level: local maximum (~4 km) then smoothed
            Filter(_h, _floor, 2, true); Blur(_floor, 2);
            Filter(_h, _ridge, 6, false); Blur(_ridge, 3);
            if (_tex == null) _tex = new Texture2D(N, N, TextureFormat.RGBAHalf, false, true) { name = "NOC_Oro", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[N * N];
            _hMin = float.MaxValue; _hMax = 0;
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color(_h[i], _floor[i], Mathf.Max(_ridge[i], _floor[i]), 0);
                _hMin = Mathf.Min(_hMin, _h[i]); _hMax = Mathf.Max(_hMax, _h[i]);
            }
            _tex.SetPixels(px); _tex.Apply(false);
            _c = _cNew; _valid = true; _building = false;
        }

        void Filter(float[] src, float[] dst, int r, bool min)
        {
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    float v = src[z * N + x];
                    for (int d = -r; d <= r; d++) { int xx = Mathf.Clamp(x + d, 0, N - 1); float s = src[z * N + xx]; v = min ? Mathf.Min(v, s) : Mathf.Max(v, s); }
                    _tmp[z * N + x] = v;
                }
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    float v = _tmp[z * N + x];
                    for (int d = -r; d <= r; d++) { int zz = Mathf.Clamp(z + d, 0, N - 1); float s = _tmp[zz * N + x]; v = min ? Mathf.Min(v, s) : Mathf.Max(v, s); }
                    dst[z * N + x] = v;
                }
        }

        void Blur(float[] a, int r)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int z = 0; z < N; z++)
                    for (int x = 0; x < N; x++)
                    {
                        float s = 0; int n = 0;
                        for (int d = -r; d <= r; d++)
                        {
                            int xx = pass == 0 ? Mathf.Clamp(x + d, 0, N - 1) : x, zz = pass == 0 ? z : Mathf.Clamp(z + d, 0, N - 1);
                            s += a[zz * N + xx]; n++;
                        }
                        _tmp[z * N + x] = s / n;
                    }
                System.Array.Copy(_tmp, a, a.Length);
            }
        }

        /// <summary>Shader parameters (per camera).</summary>
        public void Apply(Material m, Vector3 camG)
        {
            bool on = Active && _tex != null;
            m.SetFloat(ID._OroOn, on ? 1 : 0);
            m.SetFloat(ID._MarchBottom, on ? Mathf.Min(200f, Mathf.Max(0, _hMin - 20f)) : 200f);
            if (!on) return;
            m.SetTexture(ID._OroTex, _tex);
            m.SetVector(ID._OroC, new Vector4(camG.x - _c.x, camG.z - _c.y, 1f / Size, 0));
            // world-fixed noise coordinates (wrapped at 80 km, a multiple of the noise periods)
            m.SetVector(ID._OroG, new Vector4((float)(camG.x - 80000.0 * System.Math.Floor(camG.x / 80000.0)), (float)(camG.z - 80000.0 * System.Math.Floor(camG.z / 80000.0)), 0, 0));
            m.SetFloat(ID._OroTop, _hMax + 700f);
            m.SetFloat(ID._VFog, Valley);
            m.SetFloat(ID._CapFog, Cap);
        }

        public void SetWind(Material m, Vector3 wind)
        {
            var w = new Vector2(wind.x, wind.z);
            w = w.sqrMagnitude > 1e-4f ? w.normalized : Vector2.right;
            m.SetVector(ID._OroW, new Vector4(w.x, w.y, Size / N, 0));
        }
    }
}
