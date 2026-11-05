// NO Clouds: weather-driven volumetric clouds for Nuclear Option (URP 2022.3).
// WEATHER MODEL: humidity and instability per region (from the mission weather, time of day and fronts)
//   base  = condensation level (humid air -> low base)      cumulus top = instability
//   types = shallow/congestus cumulus, cumulonimbus (very unstable + humid), stratus/stratocumulus (humid + stable),
//           altocumulus/altostratus (humidity aloft), cirrus (high 2D streaks)
// Passes: 0 raymarch | 1 composite | 2 map A | 3 warp | 4 map B | 5 map C | 6 shadow | 7 shadow depth | 8 probe | 9 Cb map | 10 rays | 11 rays (add) | 12 light shafts (volume) | 13 shafts (add) | 14 rain fog | 15 shafts (temporal average)
// Anti-tiling: each noise layer has its own rotation and period, plus domain warping and 3D turbulence.
Shader "Hidden/NOClouds"
{
    Properties
    {
        _ShapeTex ("Shape", 3D) = "white" {}
        _DetailTex ("Detail", 3D) = "white" {}
        _CirrusTex ("Cirrus", 2D) = "black" {}
        _MainTex ("Cloud RT", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
        sampler3D _ShapeTex;
        sampler3D _DetailTex;
        sampler2D _CirrusTex;
        float _CiTile, _PixAng, _FarSteps, _FarShadow;
        float _Blur, _FarSoft, _ShDark;
        float4 _NOCTexel;
        float3 _ShO, _ShR, _ShU;          // shadow map plane (perpendicular to the sun, camera-relative)
        float _ShSteps, _ShOpacity, _ShBelow, _ShProt;
        sampler2D _NOCShD;                // opacity depth along the sun direction (cloud-on-cloud)
        float _ShDOn;
        sampler2D _MainTex; float4 _MainTex_TexelSize;
        sampler2D _WxA;     // r cumulus coverage, g cumulus thickness/8000, b cumulonimbus, a clustering
        sampler2D _WxB;     // r base/8000, g stratus, b stratus noise, a size mix
        sampler2D _WxC;     // r altocumulus/altostratus, g cirrus, b mid base/8000, a -
        sampler2D _WarpTex; // rg warp (m)
        float4 _WTS, _PTS;

        float3 _CornerBL, _CornerTL, _CornerTR, _CornerBR;
        float3 _PlanetC;
        float _R;
        float _Bottom, _VolTop, _CbTop, _CiAlt;
        float2 _G;
        float2 _GD;   // _G minus the weather map center (the map moves in texel steps, otherwise clouds 'boil' while flying)
        float _WExt;
        float3 _OffS, _OffD;
        float _PerHum, _PerIns, _PerCl, _PerCell, _PerWarp, _PerTurb, _PerCb, _PerS, _PerD;
        float _Hum, _Ins, _BaseRef, _BaseVar, _CoverScale, _Cirrus, _MidAmt;
        float _FrontType, _FrontPos, _FrontWidth;   // 0 none, 1 cold, 2 warm; position along the wind (m, relative to _G)
        float _Density, _Erosion, _Dome, _Billow, _Soft, _BaseRough;
        float _Warp, _Turb, _Stretch, _SizeMix, _Anvil, _AnvilShift, _Shear, _Veer;
        float2 _WindDir;
        float _Steps, _LightSteps, _MinStep, _StepGrowth, _MaxStep, _MaxDist;
        float _Absorb, _LightAbsorb, _G1, _G2, _GMix, _Powder, _PowderK;
        float _MsA, _MsB, _Exposure, _Tonemap;
        float3 _SunDir; float4 _SunCol; float4 _AmbTop; float4 _AmbBottom;
        float _FogMode, _FogStart, _FogEnd, _FogDensity, _FogAmt; float4 _FogCol;
        float _Debug, _Frame, _Jitter, _UseDepth;
        // atmosphere: lightning (camera-relative position + intensity), rain, god rays
        float4 _FlashP; float4 _FlashCol;
        float _RainOn, _RainVis; float3 _WindVel; float _WetDark; float _Deck, _Supercell;
        float2 _SunUV; float _RaysAmt, _RaysOn;

        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 ray : TEXCOORD1; };

        v2f vert (appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord.xy;
            float3 b = lerp(_CornerBL, _CornerBR, o.uv.x);
            float3 t = lerp(_CornerTL, _CornerTR, o.uv.x);
            o.ray = lerp(b, t, o.uv.y);
            return o;
        }

        float remap(float v, float a, float b, float c, float d) { return c + (v - a) / max(b - a, 1e-5) * (d - c); }
        float2 rot(float2 v, float a) { float c = cos(a), s = sin(a); return float2(v.x * c - v.y * s, v.x * s + v.y * c); }
        float4 t2(float2 uv, float y) { return tex3Dlod(_ShapeTex, float4(uv.x, y, uv.y, 0)); }
        // non-repeating noise: two offset samples blended by a wide isotropic noise (bf = blend scale)
        float antiTile(float2 x, float2 uv, float2 per, float y, int ch, float k, float bf)
        {
            float bl = saturate((t2(rot(x, 0.7 + k) / (per.x * bf), frac(0.4 + k)).r - 0.6) / 0.12);
            float4 a0 = t2(uv / per, y), a1 = t2(uv / per + float2(0.37, 0.61), frac(y + 0.29));
            float n0 = ch == 0 ? a0.r : (ch == 1 ? a0.g : a0.b);
            float n1 = ch == 0 ? a1.r : (ch == 1 ? a1.g : a1.b);
            float c = ch == 0 ? 0.67 : 0.52;
            return c + (lerp(n0, n1, bl) - c) * (1 + 2.4 * bl * (1 - bl));
        }
        float4 hash42(float2 p)
        {
            float4 p4 = frac(p.xyxy * float4(0.1031, 0.1030, 0.0973, 0.1099));
            p4 += dot(p4, p4.wzxy + 33.33);
            return frac((p4.xxyz + p4.yzzw) * p4.zywx);
        }
        float nR(float v) { return saturate((v - 0.55) / 0.25); }
        float nW(float v) { return saturate((v - 0.30) / 0.45); }

        float2 sph(float3 d, float3 c, float r)
        {
            float b = dot(d, c);
            float k = dot(c, c) - r * r;
            float h = b * b - k;
            if (h < 0) return float2(1, -1);
            h = sqrt(h);
            return float2(b - h, b + h);
        }
        float hg(float c, float g) { float g2 = g * g; return (1 - g2) / (12.566370 * pow(abs(1 + g2 - 2 * g * c), 1.5)); }
        float phase(float c, float k) { return lerp(hg(c, _G1 * k), hg(c, _G2 * k), _GMix); }
        float altOf(float3 p) { return length(p - _PlanetC) - _R; }

        float2 smoothUV(float2 uv, float4 ts)
        {
            float2 p = uv * ts.zw + 0.5;
            float2 i = floor(p), f = p - i;
            f = f * f * f * (f * (f * 6 - 15) + 10);
            return (i + f - 0.5) * ts.xy;
        }

        // ---------- weather model (2D) ----------
        float2 mapXZ(float2 uv) { return _G - _GD + (uv - 0.5) * 2 * _WExt; }

        // front influence: d = distance to the front (m, + = ahead of it, downwind)
        float _FrontAmt;   // 0..1: the front fades in/out slowly (coverage never switches in one frame)
        void frontMod0(float2 x, inout float hum, inout float ins, inout float humAloft, inout float ci);
        void frontMod(float2 x, inout float hum, inout float ins, inout float humAloft, inout float ci)
        {
            if (_FrontType < 0.5 || _FrontAmt < 0.001) return;
            float h0 = hum, i0 = ins, a0 = humAloft, c0 = ci;
            frontMod0(x, hum, ins, humAloft, ci);
            hum = lerp(h0, hum, _FrontAmt); ins = lerp(i0, ins, _FrontAmt); humAloft = lerp(a0, humAloft, _FrontAmt); ci = lerp(c0, ci, _FrontAmt);
        }
        void frontMod0(float2 x, inout float hum, inout float ins, inout float humAloft, inout float ci)
        {
            float d = dot(x, _WindDir) - _FrontPos;
            // irregular front line at several scales (a single wave made a long smooth wall)
            d += (t2(rot(x, 0.4) / 80000, 0.6).r - 0.67) * 30000
               + (t2(rot(x, 1.9) / 21000, 0.27).r - 0.67) * 9000
               + (t2(rot(x, -0.8) / 6000, 0.81).r - 0.67) * 2500;
            float W = _FrontWidth;
            // front strength varies along it: strong parts, weak parts and gaps
            float2 pf = float2(-_WindDir.y, _WindDir.x);
            float seg = saturate((t2(float2(dot(x, pf) / 70000, dot(x, _WindDir) / 140000), 0.45).r - 0.5) / 0.3);
            seg = 0.25 + 0.75 * seg;
            if (_FrontType < 1.5)
            {   // cold front: narrow band of strong convection on the line; behind it, dry clear air
                float band = exp(-(d / W) * (d / W)) * seg;
                ins += 0.40 * band; hum += 0.25 * band;
                hum -= 0.18 * saturate(-d / (3 * W) - 0.3) * seg;
                humAloft += 0.25 * band * 0.6;
            }
            else
            {   // warm front: ahead of it (d>0) air rises slowly in layers -> cirrus far out, altostratus, then stratus/rain
                // no step at the front line (step gave a straight stratus edge tens of km long)
                float ahead = saturate(1 - d / (8 * W)) * smoothstep(-1.5 * W, 0.5 * W, d) * seg;
                float near = saturate(1 - d / (3 * W)) * smoothstep(-1.5 * W, 0.5 * W, d) * seg;
                humAloft += 0.45 * ahead;
                ci += 0.6 * saturate(d / (3 * W)) * saturate(1 - d / (10 * W));
                hum += 0.25 * near; ins -= 0.25 * near;
                float behind = saturate(-d / W) * seg;
                hum += 0.05 * behind; ins += 0.05 * behind;
            }
        }

        void weather(float2 x, out float hum, out float ins, out float humAloft, out float ci)
        {
            float2 a = rot(x, 0.61), c = rot(x, 1.9), e = rot(x, -1.13);
            float nh = nR(0.5 * t2(a / _PerHum, 0.83).r + 0.5 * t2(c / (_PerHum * 1.414), 0.07).r + 0.04);
            float ni = nR(0.5 * t2(e / _PerIns, 0.13).r + 0.5 * t2(c / (_PerIns * 0.773), 0.57).r + 0.04);
            float na = nR(t2(rot(x, 2.7) / (_PerHum * 1.27), 0.31).r + 0.02);
            hum = _Hum + (nh - 0.5) * 0.35;
            ins = _Ins + (ni - 0.5) * 0.45;
            humAloft = _Hum * 0.75 + (na - 0.5) * 0.5;
            ci = _Cirrus;
            frontMod(x, hum, ins, humAloft, ci);
            hum = saturate(hum); ins = saturate(ins); humAloft = saturate(humAloft);
        }

        float4 fragWxA (v2f i) : SV_Target
        {
            float2 x = mapXZ(i.uv);
            float hum, ins, ha, ci; weather(x, hum, ins, ha, ci);
            float cov = saturate((hum - 0.35) / 0.55) * 0.75 * saturate((ins - 0.08) / 0.25) * _CoverScale;
            float th = 250 + pow(ins, 1.6) * 4500;
            float cb = saturate((ins - 0.74) / 0.12) * saturate((hum - 0.6) / 0.2);
            float2 r1 = rot(x, 0.61), r4 = rot(x, -0.47);
            float cl = saturate((0.5 * t2(r1 / _PerCl, 0.5).r + 0.5 * t2(r4 / (_PerCl * 1.618), 0.21).r - 0.57) / 0.18);
            return float4(cov, th / 8000, cb, cl);
        }

        float4 fragWxB (v2f i) : SV_Target
        {
            float2 x = mapXZ(i.uv);
            float hum, ins, ha, ci; weather(x, hum, ins, ha, ci);
            float base = clamp(_BaseRef + (0.6 - hum) * _BaseVar, 250, 4500);
            float st = saturate((hum - 0.72) / 0.15) * saturate((0.5 - ins) / 0.25);
            float ns = nR(t2(rot(x, 1.2) / 6000, 0.44).r);
            float mk = saturate((t2(rot(x, 0.61) / (_PerHum * 0.41), 0.5).a - 0.35) / 0.3);
            return float4(base / 8000, st, ns, mk);
        }

        float4 fragWxC (v2f i) : SV_Target
        {
            float2 x = mapXZ(i.uv);
            float hum, ins, ha, ci; weather(x, hum, ins, ha, ci);
            float mid = saturate((ha - 0.55) / 0.3) * _MidAmt;
            // mid layer base is almost flat (about ±100 m over 50 km); the top/thickness is what varies
            // (it used to be ±1 km and the base moved up and down in "waves")
            float midBase = 3500 + (t2(rot(x, -2.2) / 50000, 0.77).r - 0.67) * 700;
            return float4(mid, saturate(ci), midBase / 8000, 0);
        }

        float4 fragWarp (v2f i) : SV_Target
        {
            float2 x = mapXZ(i.uv);
            float2 w = rot(x, 2.3) / _PerWarp;
            float dx = (t2(w, 0.37).g - 0.48) * _Warp * _PerCell;
            float dz = (t2(w.yx, 0.71).b - 0.48) * _Warp * _PerCell;
            return float4(dx, dz, 0, 1);
        }

        // ---------- 3D density ----------
        float densityBase(float3 p, bool cheap, out float hAmb)
        {
            hAmb = 0;
            float alt = altOf(p);
            if (alt < _Bottom || alt > _VolTop) return 0;
            float2 uvW = (p.xz + _GD) / (2 * _WExt) + 0.5;
            float2 su = smoothUV(uvW, _WTS);
            float4 A = tex2Dlod(_WxA, float4(su, 0, 0));
            float4 B = tex2Dlod(_WxB, float4(su, 0, 0));
            float4 C = tex2Dlod(_WxC, float4(su, 0, 0));
            float base = B.r * 8000;
            // supercell wall cloud reaches ~600 m below the base
            if (alt < base - (A.b > 0.3 && _Supercell > 0 ? 650 : 0)) return 0;
            float cov = A.r, cuTh = A.g * 8000, cb = A.b, cl = A.a;
            float st = B.g, ns = B.b, mk = B.a;
            // overcast/storm: the mid layer becomes a sheet (altostratus/nimbostratus) covering the whole sky,
            // with few gaps, thicker and lower the worse the weather
            float mid = max(C.r, _Deck * (0.8 + 0.2 * ns)), midBase = lerp(C.b * 8000, max(base + cuTh * 0.6, 2400), _Deck * 0.5);
            float cuTop = base + cuTh;
            float stTh = 250 + 700 * st;
            float midTh = (250 + 650 * mid + 1500 * _Deck * _Deck) * (0.7 + 0.6 * ns);   // altocumulus/altostratus: thin layer (0.2-1 km), top with higher and lower spots
            bool inCu = alt <= cuTop && cov > 0.01;
            bool inSt = st > 0.01 && alt <= base + stTh;
            bool inMid = mid > 0.01 && alt >= midBase && alt <= midBase + midTh;
            bool inCb = cb > 0.01;
            if (!(inCu || inSt || inMid || inCb)) return 0;

            float2 x = _G + p.xz;
            float2 dw = tex2Dlod(_WarpTex, float4(smoothUV(uvW, _PTS), 0, 0)).rg;
            // turbulence: the cloud base stays put and only the upper part leans a little
            // (it used to shift up to ~1.7 km between heights and made thin stretched sheets)
            float3 tq = float3(x.x, alt * 0.3, x.y) / _PerTurb;
            float tw = saturate((alt - base) / 4000) * 0.25;
            dw += (float2(tex3Dlod(_ShapeTex, float4(tq, 0)).g, tex3Dlod(_ShapeTex, float4(tq + 0.37, 0)).b) - 0.48) * _Turb * tw;

            float ha = saturate((alt - base) / 8000);
            float2 sd = rot(_WindDir, _Veer * ha);
            float2 sh = sd * (_Shear * pow(ha, 1.5) * 8);
            float3 q = p - float3(sh.x, 0, sh.y);
            float3 qs = q + _OffS;
            float4 s = lerp(tex3Dlod(_ShapeTex, float4(qs / _PerS, 0)),
                            tex3Dlod(_ShapeTex, float4(float3(rot(qs.xz, -0.53), qs.y).xzy / (_PerS * 1.61) + 0.41, 0)), 0.35);
            float bil = s.g * 0.625 + s.b * 0.25 + s.a * 0.125;

            float d = 0, an = 0, post = 1, wallD = 0;

            // cumulus: cells stretched by the wind, dome, billows
            if (inCu)
            {
                // three cloud sizes (many small, few large); each cloud has its own height,
                // proportional to its width (rounded top), flat base; cells never repeat
                float2 perp = float2(-_WindDir.y, _WindDir.x);
                float along = dot(x, _WindDir), across = dot(x, perp);
                float2 cx = _WindDir * (along / _Stretch) + perp * across;
                float covL = cov * (0.55 + 0.45 * cl);
                float bl0 = ((bil - 0.48) * 0.5 + (s.r - 0.67) * 0.25) * _Billow;
                float z0 = alt - base;
                const float3 PER = float3(1.0, 0.43, 0.19), ANG = float3(0.27, 1.71, -0.9), YY = float3(0.25, 0.66, 0.12);
                const float3 KK = float3(0.1, 0.6, 0.37), FR = float3(0.7, 0.6, 0.45), HS = float3(1.0, 0.6, 0.35);
                float fs = 1 + length(p) * 1e-4 * _FarSoft;   // softer edge at range (FarSoftness)
                [loop] for (int ic = 0; ic < 3; ic++)   // not unrolled: keeps the shader compile time down
                {
                    // SizeMix: how much medium/small clouds show up (0 = large only; 0.7 = default)
                    float fr = FR[ic] * (ic == 0 ? 1 : saturate(_SizeMix / (ic == 2 ? 0.7 : 0.4)));
                    if (fr <= 0.01) continue;
                    // each cloud is its own cell: center, radius, height and presence are random (no texture,
                    // so no wisps or sheets); coverage decides how many cells hold a cloud
                    float per = _PerCell * PER[ic];
                    float2 uv = rot(cx + dw * sqrt(PER[ic]), ANG[ic]) / per + float2(7.1, 3.3) * ic;
                    float2 cu = floor(uv), fu = uv - cu;
                    float2 o0 = float2(fu.x < 0.5 ? -1 : 0, fu.y < 0.5 ? -1 : 0);
                    float pr = saturate(covL * fr * 1.5);
                    float rs = 0.7 + 0.5 * covL;
                    [unroll] for (int k = 0; k < 4; k++)
                    {
                        float2 g = cu + o0 + float2(k & 1, k >> 1);
                        float4 h = hash42(g + 31.7 * ic);
                        // smooth presence: when coverage changes the cloud grows/shrinks instead of popping
                        float pw = saturate((pr - h.z) / 0.15);
                        if (pw <= 0) continue;
                        float4 h2 = hash42(g + float2(17.3, 5.1) + 31.7 * ic);
                        float r = (0.22 + 0.3 * h2.y) * rs * sqrt(pw);
                        float q = length(uv - (g + 0.15 + 0.7 * h.xy)) / r - bl0 * 1.5;
                        if (q >= 1) continue;
                        float Hc = min(min(cuTh * HS[ic], 0.8 * r * per) * (0.5 + 0.8 * h.w * h2.x), cuTh);
                        float skn = 1 - q;
                        float Htop = Hc * pow(saturate(1 - q * q), 0.8);           // bell-shaped dome
                        float Fm = Htop - z0 + (bil - 0.48) * _Billow * Hc * 0.5 * saturate(skn * 3);
                        // underside of THIS cloud: a little above the layer base, curving up toward the edge, soft
                        // (per cloud, so overlapping clouds do not cut each other into slices)
                        float lift = _BaseRough * (min(0.12 * Hc, 180) * h2.z + 0.22 * Hc * q * q * q);
                        float dk = saturate(Fm / ((_Soft * Hc * 2 + 1) * fs)) * saturate(skn * 4 / fs)
                                 * saturate((z0 - lift) / ((20 + _BaseRough * 0.05 * Hc) * fs));
                        if (dk > d) { d = dk; hAmb = saturate(z0 / max(Hc, 1)); }
                    }
                }
            }
            // stratus / stratocumulus: thin flat-topped layer
            if (inSt)
            {
                float hs = (alt - base) / stTh;
                float prof = saturate(hs / 0.15) * saturate((1 - hs) / 0.3);
                float Fs = st * prof + (ns - 0.5) * 0.5 + (bil - 0.48) * 0.25 - 0.3;
                float ds = saturate(Fs / 0.15) * 0.55;
                if (ds > d) { d = ds; hAmb = hs; }
            }
            // altocumulus / altostratus: mid layer (low coverage = patches; high = sheet)
            if (inMid)
            {
                float hm = (alt - midBase) / midTh;
                float prof = saturate(hm / 0.2) * saturate((1 - hm) / 0.35);
                // wide soft patches (6-15 km) with billowed edges/top, instead of loose flakes
                // (the speckled look came from the fine cell texture at 2.5 km)
                float2 xm = rot(x + dw * 0.3, 0.9);
                float nm = nR(0.6 * t2(xm / 9000, 0.15).r + 0.4 * t2(rot(xm, 1.3) / 15000, 0.62).r);
                // edge and base cut by noise (it used to be a smooth oval), and less dense: from below it is light grey and translucent
                // at range the edge noise turns into dots: it fades with distance and the edge gets softer
                float fsm = 1 + length(p) * 1e-4 * _FarSoft;
                float Fm = mid * prof + (nm - 0.5) * 0.9 + ((bil - 0.48) * 0.5 + (s.r - 0.67) * 0.3) / fsm - 0.45;
                // shape at density 0.45 (fine erosion cuts into that, as before) and only AFTER that it gets
                // thinner (x0.55): starting directly at 0.25, erosion ate almost everything and left loose dots
                float dm = saturate(Fm / (0.1 * fsm)) * 0.45;
                if (dm > d) { d = dm; hAmb = 0.8; post = lerp(0.55, 1, _Deck); }
            }
            // cumulonimbus: each storm is its own cell (random, no repeating texture):
            // round tower with a domed top + flat anvil at the top, stretched downwind
            if (inCb)
            {
                float4 sb = tex3Dlod(_ShapeTex, float4(qs / (_PerS * 3), 0));
                float bilb = sb.g * 0.625 + sb.b * 0.25 + sb.a * 0.125;
                float eN = (bilb - 0.48) * 0.9 + (sb.r - 0.67) * 0.5 + (bil - 0.48) * 0.4;   // billowed edge
                float P = _PerCb;
                float2 wu = rot(_WindDir, 0.93);
                float2 wp = float2(-wu.y, wu.x);
                float2 uvc = rot(x - sh, 0.93) / P + (dw - 0.5) * 0.12 + float2(3.7, 8.1);
                float2 cc = floor(uvc), fc = uvc - cc;
                float2 oc = float2(fc.x < 0.5 ? -1 : 0, fc.y < 0.5 ? -1 : 0);
                float z0 = alt - base;
                float Hmax = max(_CbTop - base, 1500);
                float pc = saturate(cb * 1.6);
                [loop] for (int kc = 0; kc < 4; kc++)   // not unrolled: keeps the shader compile time down
                {
                    float2 g = cc + oc + float2(kc & 1, kc >> 1);
                    float4 h = hash42(g + 91.3);
                    float pw = saturate((pc - h.z * 0.85) / 0.15);
                    if (pw <= 0) continue;
                    float4 h2 = hash42(g + float2(41.7, 13.9));
                    float2 dv = (uvc - (g + 0.2 + 0.6 * h.xy)) * P;           // metres to the center
                    float Ht = Hmax * (0.78 + 0.22 * h.w) * (0.75 + 0.25 * pw);
                    float rT = (3200 + 3300 * h2.y) * (0.6 + 0.4 * pw);           // tower radius
                    // supercell: some strong storms become one huge rotating cell (mesocyclone)
                    float S = (frac(h2.w * 13.7 + h.z * 3.1) < _Supercell * saturate((cb - 0.45) * 3)) ? 1 : 0;
                    rT *= lerp(1, 2.3, S);
                    Ht = lerp(Ht, Hmax * 1.05, S);
                    float hz = z0 / Ht;
                    // tower: widens a little going up, dome on top, overshoot above the anvil
                    float wz = rT * (0.85 + 0.3 * saturate(hz));
                    // rotation striations (spiral bands climbing the tower)
                    float angT = atan2(dv.y, dv.x);
                    wz *= 1 + S * (0.07 * sin(3 * angT + z0 / 450) + 0.04 * sin(7 * angT - z0 / 260));
                    float qT = length(dv) / wz - eN * 0.35;
                    float topT = Ht * (0.93 + (0.1 + 0.08 * S) * sqrt(saturate(1 - qT * qT * (2.2 + 4 * S))));
                    float dt = saturate((1 - qT) / 0.12) * saturate((topT - z0) / 250);
                    // feeder towers (congestus) against the main tower, lower
                    [unroll] for (int kf = 0; kf < 2; kf++)
                    {
                        float af = 6.2832 * frac(h2.z + 0.43 * kf);
                        float2 cf = float2(cos(af), sin(af)) * rT * (1.15 + 0.3 * kf);
                        float rf = rT * (0.55 - 0.12 * kf);
                        float Hf = Ht * (0.55 - 0.15 * kf) * (0.8 + 0.4 * frac(h.w * 7.3 + kf * 0.5));
                        float qf = length(dv - cf) / rf - eN * 0.4;
                        float tf = Hf * pow(saturate(1 - qf * qf), 0.6);
                        dt = max(dt, saturate((1 - qf) / 0.15) * saturate((tf - z0) / 200));
                    }
                    if (S > 0)
                    {
                        // flanking line: progressively lower towers leaving the rear side (upwind, rotated 50°)
                        float2 fd = rot(-wu, 0.87);
                        [unroll] for (int kl = 1; kl <= 4; kl++)
                        {
                            float2 cl2 = fd * rT * (0.85 + 0.5 * kl) + wp * rT * 0.15 * sin(kl * 2.1 + h.x * 6);
                            float rl = rT * (0.36 - 0.05 * kl);
                            float Hl = Ht * (0.6 - 0.1 * kl);
                            float ql = length(dv - cl2) / rl - eN * 0.4;
                            float tl = Hl * pow(saturate(1 - ql * ql), 0.6);
                            dt = max(dt, saturate((1 - ql) / 0.15) * saturate((tl - z0) / 200));
                        }
                        // wall cloud: a step that hangs under the updraft, on the rear side
                        float2 cw = fd * rT * 0.35;
                        float qw = length(dv - cw) / (rT * 0.38) - eN * 0.3;
                        float drop = 600 * saturate(1 - qw * qw);
                        wallD = max(wallD, saturate((1 - qw) / 0.2) * saturate((z0 + drop) / 80) * saturate(-z0 / 30 + 1) * 0.9);
                    }
                    // anvil: flat sheet between 72% and 100% of the height, stretched downwind
                    float da = 0, ap = 0;
                    float aB = Ht * (0.70 + 0.04 * h2.z);
                    if (z0 > aB - 600 && z0 < Ht + 200)
                    {
                        float L = (rT + _AnvilShift * (1.2 + 1.3 * h2.x) * saturate(_Anvil / 0.3 + 0.2)) * lerp(1, 2.2, S);
                        float Wd = rT * (1.4 + 3 * _Anvil) * (0.8 + 0.4 * h2.w) * lerp(1, 1.4, S);
                        float al = dot(dv, wu), ac = dot(dv, wp);
                        float u = saturate(al / L);                                 // 0 at the tower, 1 at the tip
                        float wA = Wd * (0.6 + 0.6 * u) ;                           // fans out
                        float qa = length(float2(max(al - L, 0) + min(al + rT * 1.1, 0), ac) / float2(rT, wA)) ;
                        qa = max(qa, 0) - eN * 0.4;
                        float tA = Ht - (Ht - aB) * 0.15 * u;                      // almost flat top (tropopause)
                        float bA = aB + (Ht - aB) * (0.15 + 0.45 * u);             // thins towards the tip
                        float sl = saturate((tA - z0) / 200) * saturate((z0 - bA + eN * 300) / 300);
                        ap = sl;
                        da = saturate((1 - qa) / 0.25) * sl * saturate(_Anvil * 4 + 0.2);
                    }
                    float db = max(dt, da) * saturate(pw * 2);
                    if (db > d) { d = db; an = (da > dt) ? 1 : 0; hAmb = saturate(hz); post = 1; }
                }
            }
            d *= saturate((alt - base) / 60);
            if (wallD > d) { d = wallD; hAmb = 0; post = 1; }
            if (d <= 0) return 0;

            if (!cheap)
            {
                float3 qd = q + _OffD;
                float3 dd1 = tex3Dlod(_DetailTex, float4(qd / _PerD, 0)).rgb;
                float3 qr = float3(rot(qd.xz, 0.77), qd.y).xzy;
                float3 dd2 = tex3Dlod(_DetailTex, float4(qr / (_PerD * 1.37) + 0.29, 0)).rgb;
                float3 dd = lerp(dd1, dd2, 0.5);
                float dfbm = saturate((dd.r * 0.625 + dd.g * 0.25 + dd.b * 0.125 - 0.2) / 0.6);
                float dn = lerp(dfbm, 1 - dfbm, saturate(hAmb * 6));
                d = saturate(remap(d, dn * _Erosion * (1 + an), 1, 0, 1));
            }
            return d * post * _Density * saturate(hAmb * 4 + 0.4);
        }

        // ---------- nuclear blasts ----------
        // The shock front (same speed as the game's Shockwave: 0.5*bp + 340 m/s) pushes the cloud outward:
        // a hole opens behind it and the pushed cloud piles up in a dense rim that travels with the front.
        // Material map (area preserving): d0^2 = d^2 - Rh^2 * W, W = 1 behind the hole, 0 at the front,
        // so nothing ahead of the front moves. Condensation (Wilson cloud, pileus caps and velum skirts around
        // the mushroom cap) is added on top. bp = blast power = (kg TNT)^(1/3) m.
        float _NkN;            // active blasts (0..4)
        float4 _NkP[4];        // xyz: blast center (camera-relative, drifts with the clouds), w: strength (fades out)
        float4 _NkS[4];        // x: shock front radius, y: hole radius, z: bp, w: Wilson cloud strength
        float4 _NkC[4];        // xyz: mushroom cap center (camera-relative), w: cap radius
        float4 _NkRg[16];      // rings, blast i ring j at [i*4+j]: x height (camera-relative y), y outer radius (0 = none)
        float4 _NkL[4];        // fireball light: xyz position (camera-relative), w intensity
        float4 _NkLC;          // fireball light colour
        float _NkRing;         // ring density (amount x humidity)

        float3 nukeWarp(float3 p, out float boost)
        {
            boost = 1;
            [loop] for (int i = 0; i < (int)_NkN; i++)
            {
                float4 P = _NkP[i], S = _NkS[i];
                float3 v = p - P.xyz;
                float d = length(v);
                float R = S.x;
                if (d >= R || S.y < 1) continue;                        // the front has not got here yet
                // ragged hole edge (big lobes, not a perfect circle)
                float2 hn = v.xz / max(S.y * 0.45, 50) + float2(3.1, 7.7) * i;
                float lob = t2(hn, 0.33).r * 0.6 + t2(hn * 2.3, 0.71).r * 0.4;
                float Rh = S.y * P.w * (1 + (lob - 0.67) * 0.55);
                float u = saturate((d - Rh) / max(R - Rh, 1));
                float W = 1 - u * u * u;
                // area preserving (the layers are thin: the cloud moves sideways): d0^2 = d^2 - Rh^2 W
                float d2 = d * d - Rh * Rh * W;
                if (d2 <= 0) { boost = 0; return p; }                    // inside the hole
                float d0 = sqrt(d2);
                // pile-up just behind the front (shock compression) + radially squashed rim (permanent)
                float J = 1 + Rh * Rh * 3 * u * u / max(R - Rh, 1) / (2 * d);
                float sq = d / max(d0, 1);
                boost *= min(min(J, 2.2) * (1 + 0.6 * saturate(sq - 1)), 2.6);
                // the cloud that was near the center went into the fireball and evaporated: soft hole edge
                boost *= saturate((d0 - 0.3 * Rh) / (0.3 * Rh));
                float3 p0 = P.xyz + v * (d0 / max(d, 1));
                p0.y = lerp(p.y, p0.y, 0.4);                             // mostly sideways (layers stay layers)
                p = p0;
            }
            return p;
        }

        float nukeCond(float3 p, out float hN)
        {
            hN = 0.8;
            float dm = 0;
            [loop] for (int i = 0; i < (int)_NkN; i++)
            {
                float4 P = _NkP[i], S = _NkS[i], C = _NkC[i];
                // Wilson cloud: thin condensation shell right behind the front, a few seconds
                if (S.w > 0.001)
                {
                    float3 v = p - P.xyz;
                    float R = S.x, w = 0.05 * R + 40;
                    float dd = abs(length(v) - (R - w));
                    if (dd < w)
                    {
                        float2 wn = v.xz / (0.3 * R + 100) + v.y / (0.5 * R + 100);
                        float n = t2(wn, frac(0.17 * i + 0.4)).r;
                        float sh = saturate(1 - dd / w) * saturate((n - 0.5) / 0.25);
                        dm = max(dm, S.w * sh * 0.6);
                    }
                }
                // pileus caps / velum skirts: flat discs at the heights the rising cap pushed through
                float capR = max(C.w, 50);
                float2 rv = p.xz - C.xz;
                float r = length(rv);
                [loop] for (int j = 0; j < 4; j++)
                {
                    float2 rg = _NkRg[i * 4 + j].xy;
                    float rO = rg.y;
                    if (rO < 1 || r > rO * 1.3) continue;
                    float hj = rg.x;
                    float rel = (C.y - hj) / capR;                       // cap center relative to the layer, in cap radii
                    // pileus: the layer bulges over the cap as it comes up; once the cap is through it is
                    // pierced and relaxes into a flat skirt (velum) around the stem
                    float lift = capR * 0.75 * smoothstep(-2.2, 0, rel) * (rel > 0.3 ? exp(-(rel - 0.3) * 1.2) : 1);
                    float hs = hj + lift * exp(-(r * r) / (2.6 * capR * capR));
                    float hole = capR * 1.05 * saturate((rel - 0.3) / 0.6);
                    float2 nq = rv / (rO * 0.22 + 200) + float2(1.7 * j, 4.3 * i);
                    float n = t2(nq, frac(0.13 + 0.21 * j)).r * 0.6 + t2(rot(nq, 0.8) * 2.7, frac(0.57 + 0.11 * j)).r * 0.4;
                    float q = r / rO + (n - 0.67) * 0.45;
                    float th = (0.22 * capR + 120) * (0.55 + 0.9 * saturate((n - 0.5) / 0.3)) * sqrt(saturate(1 - q * q));
                    float dy = abs(p.y - hs);
                    float sl = saturate((th - dy) / (0.5 * th + 1));
                    float dr = saturate((1 - q) / 0.18) * sl * saturate((r - hole) / (0.2 * capR));
                    dr *= smoothstep(-2.6, -1.6, rel) * P.w;
                    dm = max(dm, dr * _NkRing);
                }
            }
            return dm;
        }

        float density(float3 p, bool cheap, out float hAmb)
        {
            // one densityBase call site only (it is huge: every extra call site multiplies the compile time)
            bool nk = _NkN >= 0.5;
            float boost = 1;
            float3 p0 = p;
            if (nk) p0 = nukeWarp(p, boost);
            float d = 0; hAmb = 0;
            if (boost > 0.001) d = densityBase(p0, cheap, hAmb) * boost;
            if (nk)
            {
                float hN;
                float dn = nukeCond(p, hN) * _Density;
                if (dn > d) { d = dn; hAmb = hN; }
            }
            return d;
        }

        // ---------- rain ----------
        // how much rain (0..1) falls under column x: strong cumulonimbus, very thick cumulus (shower), humid stratus (drizzle).
        // Rain leaves the BASE of its own cloud (samples the cloud just above the base), leans with the wind and
        // evaporates before reaching the ground if the base is high and the air is dry (virga).
        float rainAt(float3 p, out float base, bool shafts)
        {
            base = 0;
            float alt = altOf(p);
            float2 uvW = (p.xz + _GD) / (2 * _WExt) + 0.5;
            if (any(uvW < 0.002) || any(uvW > 0.998)) return 0;
            float4 A = tex2Dlod(_WxA, float4(uvW, 0, 0));
            float4 B = tex2Dlod(_WxB, float4(uvW, 0, 0));
            base = B.r * 8000;
            if (alt > base) return 0;
            float cov = A.r, cuTh = A.g * 8000, cb = A.b, st = B.g;
            // visible curtains (shafts): only under cumulonimbus; no general grey veil.
            // at the camera (glass/drops): also showers from thick cumulus and drizzle from stratus
            float reg = shafts ? saturate((cb - 0.1) * 4)
                               : saturate(cb * 3) + 0.7 * saturate((cuTh - 2200) / 2500) * saturate(cov * 2) + 0.12 * st * saturate((_Hum - 0.72) / 0.15);
            if (reg < 0.02) return 0;
            float drop = base - alt;
            // rain falls at ~9 m/s and the wind carries it: the column leans
            float3 up = normalize(p - _PlanetC);
            // only rains under real cloud: samples the cloud 400 m above the base (thin/eroded edges do not rain)
            float3 q = p + up * (drop + 400) - float3(_WindVel.x, 0, _WindVel.z) * (drop / 9);
            float hA;
            // two heights averaged: the eroded cloud edge alone made the mask flicker on and off (speckles)
            float dB = 0;
            [loop] for (int kr = 0; kr < 2; kr++) dB += densityBase(q + up * (500 * kr), true, hA);   // one call site
            dB *= 0.5 / max(_Density, 1e-4);
            // far away the march steps are km long: wider, softer mask so it does not turn into speckled drizzle
            float far = saturate(length(p) / 12000);
            // curtain like the reference photos: thin, almost vertical streaks (slightly slanted with height)
            // plus broad bands; several shafts under one tower; ragged, streaky edges; lighter near the ground
            float2 xg = _G + p.xz;
            float fall = alt + _Time.y * 9;
            float s1 = saturate((t2(rot(xg, 0.3) / 140 + alt / 2600, frac(0.58 + fall / 12000)).r - 0.51) / 0.29);
            float s2 = saturate((t2(rot(xg, 1.2) / 800, frac(0.21 + fall / 30000)).r - 0.51) / 0.29);
            float stk = lerp(0.6 * s1 + 0.4 * s2, 0.55, far * far);           // far away the streaks are smaller than a pixel
            float coreN = saturate((t2(xg / 1400, 0.37).r - 0.51) / 0.29);
            float mask = saturate((dB * (0.35 + 0.9 * coreN) - lerp(0.15, 0.08, far) + 0.5 * (stk - 0.5)) * 2.2);
            mask = mask * mask * (3 - 2 * mask);
            if (mask <= 0) return 0;
            float virga = lerp(700, 6000, saturate((_Hum - 0.45) / 0.4)) * (0.6 + 0.8 * saturate(cb * 3));
            float f = saturate(1.3 - drop / virga) * saturate(drop / 60) * (0.5 + 0.5 * saturate(alt / 900));
            float strand = lerp(0.12 + 1.7 * pow(saturate((stk - 0.4) / 0.35), 1.5), 0.9, far);
            return saturate(reg) * mask * f * strand;
        }

        // how much water the cloud in that column carries (0..~1.5): cumulonimbus, thick cumulus, humid stratus,
        // overcast. Laden clouds are dark grey underneath and on the sides; the sunlit top stays bright.
        float wetness(float3 p)
        {
            float2 uvW = (p.xz + _GD) / (2 * _WExt) + 0.5;
            if (any(uvW < 0.002) || any(uvW > 0.998)) return 0.3 * saturate((_Hum - 0.6) / 0.25);
            float4 A = tex2Dlod(_WxA, float4(uvW, 0, 0));
            float4 B = tex2Dlod(_WxB, float4(uvW, 0, 0));
            float cov = A.r, cuTh = A.g * 8000, cb = A.b, st = B.g;
            return saturate(cb * 3) + 0.8 * saturate((cuTh - 1800) / 3000) * saturate(cov * 1.6)
                 + 0.6 * st * saturate((_Hum - 0.65) / 0.2) + 0.5 * saturate((cov - 0.55) / 0.3);
        }

        // planet shadow: sun (or moon) below the horizon at that point -> no direct light
        // (high clouds still catch the sunset, low ones no longer do)
        float planetShadow(float3 p)
        {
            float2 h = sph(_SunDir, _PlanetC - p, _R - 300);
            return (h.y > h.x && h.x > 0) ? 1 : 0;
        }
        float lightMarch(float3 p)
        {
            if (planetShadow(p) > 0.5) return 50;
            float tau = 0, hA;
            float3 q = p;
            [loop] for (int i = 0; i < (int)_LightSteps; i++)
            {
                float stp = 60 * (1 + i * 1.2);
                q += _SunDir * stp;
                tau += density(q, i > 1, hA) * stp;
            }
            // cloud-on-cloud through the opacity depth map: is the point "behind" (away from the sun)
            // the opaque surface of its column? The first ~670 m are already covered by the short steps above.
            if (_ShDOn > 0.5)
            {
                float3 d0 = p - _ShO;
                float2 uv = float2(dot(d0, _ShR) / dot(_ShR, _ShR), dot(d0, _ShU) / dot(_ShU, _ShU));
                if (all(uv > 0.01) && all(uv < 0.99))
                {
                    float sp = dot(d0, normalize(_SunDir));
                    float sh = tex2Dlod(_NOCShD, float4(uv, 0, 0)).r;
                    float occ = saturate((sh - sp - 650) / 400);
                    return tau * _LightAbsorb + occ * _FarShadow * 6;
                }
            }
            // outside the map: long sparse steps (approximate cloud-on-cloud)
            float far = 0, sl = 500;
            [loop] for (int j = 0; j < (int)_FarSteps; j++)
            {
                q += _SunDir * sl;
                if (altOf(q) > _VolTop) break;
                far += density(q, true, hA) * sl;
                sl *= 2;
            }
            return (tau + far * _FarShadow) * _LightAbsorb;
        }

        // cirrus: thin 2D shell at _CiAlt, streaks stretched by the wind
        float4 cirrus(float3 rd, float tMax, out float tcOut)
        {
            tcOut = 0;
            if (_Cirrus <= 0.001) return 0;
            float2 hit = sph(rd, _PlanetC, _R + _CiAlt);
            float camAlt = length(_PlanetC) - _R;
            float tc = camAlt < _CiAlt ? hit.y : hit.x;
            if (hit.y < hit.x || tc <= 0 || tc > tMax) return 0;
            tcOut = tc;
            float3 p = rd * tc;
            float2 uvW = (p.xz + _GD) / (2 * _WExt) + 0.5;
            float ci = (any(saturate(uvW) != uvW)) ? _Cirrus : tex2Dlod(_WxC, float4(uvW, 0, 0)).g;
            float2 x = _G + p.xz;
            // flow-generated streak texture (R streaks, G tails/hooks, B veil, A streaks 2), always aligned with the wind
            float2 wq = rot(x, 0.9) / 90000;
            float2 xw = x + (float2(t2(wq, 0.33).r, t2(wq.yx, 0.61).r) - 0.67) * 20000;   // wide meander
            float2 uv = float2(dot(xw, _WindDir), dot(xw, float2(-_WindDir.y, _WindDir.x))) / _CiTile;
            float3 nrm = normalize(p - _PlanetC);
            float cosv = max(abs(dot(rd, nrm)), 0.05);
            float lod = log2(max(tc * _PixAng / cosv / (_CiTile / 1024), 1));
            float bl = saturate((t2(rot(x, 1.7) / 70000, 0.27).r - 0.6) / 0.12);         // anti-tiling
            float4 T = lerp(tex2Dlod(_CirrusTex, float4(uv, 0, lod)), tex2Dlod(_CirrusTex, float4(uv + float2(0.43, 0.71), 0, lod)), bl);
            float cov = ci + (nR(t2(rot(x, -0.8) / 60000, 0.71).r) - 0.5) * 0.7;          // regional variation
            float reg = saturate(nR(t2(rot(x, 0.4) / 45000, 0.12).r) * 0.8 + 0.4);
            float fr = (T.r - 0.55) * 0.5 + (T.a - 0.55) * 0.3;
            float e = max(saturate((T.b + fr * 0.5 - 0.74 + cov * 0.2) / 0.12), saturate(T.g * 1.6 + fr - 0.25 - (0.5 - ci)));
            float a = saturate(e * (0.3 + 0.7 * T.r) * 1.1) * reg * 0.85;
            a *= sqrt(saturate(0.3 / cosv));   // thicker at grazing angles
            float cosT = dot(rd, _SunDir);
            float3 c = _SunCol.rgb * (phase(cosT, 0.7) * 12.566 * 0.6) + _AmbTop.rgb * 0.9;
            return float4(c * a, a);
        }

        float ign(float2 px) { return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715)))); }

        float fogFactor(float dist)
        {
            float f = 1;
            if (_FogMode == 1) f = saturate((_FogEnd - dist) / max(_FogEnd - _FogStart, 1));
            else if (_FogMode == 2) f = exp(-_FogDensity * dist);
            else if (_FogMode == 3) { float x = _FogDensity * dist; f = exp(-x * x); }
            return lerp(1, f, saturate(_FogAmt));
        }

        float4 fragMarch (v2f i) : SV_Target
        {
            float rl = length(i.ray);
            float3 rd = i.ray / rl;
            float sceneT = 1e9;
            if (_UseDepth > 0)
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float eye = LinearEyeDepth(raw);
                if (eye < _ProjectionParams.z * 0.999) sceneT = eye * rl;
            }

            float2 outer = sph(rd, _PlanetC, _R + _VolTop);
            float2 inner = sph(rd, _PlanetC, _R + _Bottom);
            float camAlt = length(_PlanetC) - _R;
            float T = 1;
            float3 col = 0;
            float depthW = 0, wsum = 0;
            float t1 = -1;
            if (!(outer.y < 0 || outer.x > outer.y))
            {
                float t0 = max(outer.x, 0);
                t1 = outer.y;
                if (inner.y > inner.x)
                {
                    if (camAlt < _Bottom) t0 = max(t0, inner.y);
                    else if (inner.x > 0) t1 = min(t1, inner.x);
                }
                t1 = min(min(t1, sceneT), _MaxDist);

                float cosT = dot(rd, _SunDir);
                float jit = ign(i.pos.xy + (_Jitter > 0 ? _Frame * 5.588238 : 0));
                float t = t0 + clamp(t0 * _StepGrowth, _MinStep, _MaxStep) * jit;
                bool skipped = false;
                int fine = 0;
                int maxS = (int)_Steps;
                [loop] for (int k = 0; k < maxS; k++)
                {
                    if (t >= t1 || T < 0.01) break;
                    float stp = clamp(t * _StepGrowth, _MinStep, _MaxStep);
                    float3 p = rd * t;
                    float hA;
                    float dens = density(p, false, hA);
                    if (dens > 0.0001 && skipped) { t -= stp; skipped = false; fine = 3; continue; }
                    if (dens > 0.0001)
                    {
                        float tauL = lightMarch(p) * (0.5 + _ShDark);   // ShadowDarkness: 0.5 = original look
                        float powder = lerp(1, 1 - exp(-dens * _PowderK), _Powder);
                        float3 L = 0; float a = 1, b = 1, c = 1;
                        [unroll] for (int o = 0; o < 3; o++)
                        {
                            L += a * phase(cosT, b) * exp(-tauL * c);
                            a *= _MsA * (1.5 - _ShDark); b *= 0.5; c *= _MsB;
                        }
                        float hh = saturate((altOf(p) - _Bottom) / 2500);
                        float ao = lerp(1, 0.25 + 0.75 * saturate(hA), saturate(_ShDark * 1.4));   // darker cloud base
                        // water-laden cloud: dark underneath and on the sides (sunlit top stays bright)
                        float wd = min(wetness(p), 1) * _WetDark * pow(1 - 0.9 * saturate(hA), 1.5);
                        float3 S = _SunCol.rgb * L * powder * 12.566 * exp(-wd * 1.2) + lerp(_AmbBottom.rgb, _AmbTop.rgb, hh) * ao * lerp(1, 0.5, saturate(wd));
                        if (_FlashP.w > 0)   // lightning: the cloud lights up from inside around the flash
                        {
                            float fd = length(p - _FlashP.xyz);
                            S += _FlashCol.rgb * (_FlashP.w * exp(-fd / 1300) * (0.35 + 0.65 * ao));
                        }
                        [loop] for (int nl = 0; nl < (int)_NkN; nl++)   // nuclear fireball light
                        {
                            float4 NL = _NkL[nl];
                            float3 fv = p - NL.xyz; float bp3 = 3 * _NkS[nl].z;
                            S += _NkLC.rgb * (NL.w * (0.35 + 0.65 * ao) / (1 + dot(fv, fv) / (bp3 * bp3)));
                        }
                        float tr = exp(-dens * _Absorb * stp);
                        col += T * S * (1 - tr);
                        float wgt = T * (1 - tr);
                        depthW += t * wgt; wsum += wgt;
                        T *= tr;
                        t += stp;
                        fine = 2;
                    }
                    else if (fine > 0) { t += stp; fine--; }
                    else { t += stp * 2; skipped = true; }
                }
            }

            // rain curtains: separate march in the layer below the bases (from the ground to the highest base)
            if (_RainOn > 0.5)
            {
                float tEnd = min(min(sceneT, 45000), _MaxDist);
                float rT = 1; float3 rc = 0;
                const int RN = 36;
                float jr = 0.5;   // same steps for every pixel: per-pixel jitter turned the curtains into bright speckles
                float tr0 = 0;
                [loop] for (int r = 0; r < RN; r++)
                {
                    // steps grow with distance (fine up close, wide far away)
                    float u0 = (r + jr) / RN, u1 = (r + 1 + jr) / RN;
                    float ta = tEnd * u0 * u0, tb = tEnd * u1 * u1;
                    float3 p = rd * ((ta + tb) * 0.5);
                    float a = altOf(p);
                    if (a > 6000) continue;
                    float bse;
                    float rr = rainAt(p, bse, true);
                    if (rr <= 0.001) continue;
                    float sig = rr * 0.0008 * _RainVis;   // heavy rain: ~3-4 km visibility
                    float trn = exp(-sig * (tb - ta));
                    float hh = saturate(a / max(bse, 1));
                    float3 lc = lerp(_AmbBottom.rgb, _AmbTop.rgb, saturate(bse / 2500)) * lerp(1, 0.25, saturate(_ShDark * 1.4)) * lerp(1, 0.5, saturate(_WetDark))
                              + _SunCol.rgb * 0.01 * (1 - planetShadow(p));   // same colour as the darkest (wet) cloud base
                    if (_FlashP.w > 0) lc += _FlashCol.rgb * _FlashP.w * 0.5 * exp(-length(p - _FlashP.xyz) / 2500);
                    rc += rT * lc * (1 - trn);
                    rT *= trn;
                    if (rT < 0.02) break;
                }
                if (_Tonemap > 0) rc = 1 - exp(-rc * _Exposure); else rc *= _Exposure * 0.5;
                // below the base the rain is in front of the clouds; above it, behind
                float camBase = tex2Dlod(_WxB, float4(_GD / (2 * _WExt) + 0.5, 0, 0)).r * 8000;
                float fr = saturate((camBase - camAlt) / 300);   // rain/cloud order changes smoothly near the base
                col = lerp(col + T * rc, rc + rT * col, fr);
                T *= rT;
            }

            // cirrus behind the clouds (camera below them) or in front (above)
            float tci;
            float4 ci = cirrus(rd, min(sceneT, 400000), tci);
            float fd = wsum > 0 ? depthW / wsum : (ci.a > 0 ? tci : 1);
            if (camAlt < _CiAlt) { col += T * ci.rgb; T *= (1 - ci.a); }
            else { col = ci.rgb + (1 - ci.a) * col; T *= (1 - ci.a); }

            if (_Debug > 0) return float4(1 - T, 0, 0, 0.0);
            if (_Tonemap > 0) col = 1 - exp(-col * _Exposure); else col *= _Exposure * 0.5;
            float f = fogFactor(fd);
            col = lerp(_FogCol.rgb * (1 - T), col, f);
            return float4(col, T);
        }


        // ---------- shadow (sun cookie) ----------
        float4 fragShadow (v2f i) : SV_Target
        {
            float3 P = _ShO + i.uv.x * _ShR + i.uv.y * _ShU;     // point on the sun plane (camera-relative)
            float3 sd = normalize(_SunDir);
            float camAlt = length(_PlanetC) - _R;
            // clouds below the camera also shade the ground; only near the sun line through
            // the camera (your aircraft) do only the clouds above it count
            float dperp = length(P - sd * dot(P, sd));
            float prot = saturate(1 - (dperp - _ShProt) / max(_ShProt, 1)) * (1 - _ShBelow);
            float lowAlt = max(_Bottom, lerp(_Bottom, camAlt, prot));
            float3 c = _PlanetC - P;                               // planet center relative to P
            float2 o = sph(sd, c, _R + _VolTop);
            float2 l = sph(sd, c, _R + lowAlt);
            if (o.y < o.x) return 1;
            float s0 = o.x, s1 = o.y;
            if (l.y > l.x) s0 = max(s0, l.y);                      // only the part above the minimum height
            if (s1 <= s0) return 1;
            float n = _ShSteps, ds = (s1 - s0) / n, tau = 0, hA;
            [loop] for (int k = 0; k < (int)n; k++)
            {
                float3 q = P + sd * (s0 + (k + 0.5) * ds);
                tau += density(q, true, hA) * ds;
            }
            float T = exp(-tau * _Absorb * 0.6);
            float2 e = min(i.uv, 1 - i.uv);
            float edge = saturate(min(e.x, e.y) * 20);              // fades near the map edge
            return lerp(1, lerp(1 - _ShOpacity, 1, T), edge);
        }

        // depth (along the sun, from the plane) at which the column becomes opaque, coming from the sun side
        float4 fragShadowDepth (v2f i) : SV_Target
        {
            float3 P = _ShO + i.uv.x * _ShR + i.uv.y * _ShU;
            float3 sd = normalize(_SunDir);
            float3 c = _PlanetC - P;
            float2 o = sph(sd, c, _R + _VolTop);
            float2 l = sph(sd, c, _R + _Bottom);
            const float NONE = -1e6;
            if (o.y < o.x) return NONE;
            float s0 = o.x, s1 = o.y;
            if (l.y > l.x) s0 = max(s0, l.y);
            if (s1 <= s0) return NONE;
            const int N = 48;
            float ds = (s1 - s0) / N, tau = 0, hA;
            [loop] for (int k = 0; k < N; k++)
            {
                float s = s1 - (k + 0.5) * ds;                    // from the sun side downwards
                float dt = density(P + sd * s, true, hA) * ds * _LightAbsorb;
                if (tau + dt >= 2.5) return s + ds * 0.5 - ds * saturate((2.5 - tau) / max(dt, 1e-5));
                tau += dt;
            }
            return NONE;
        }

        // probe at the aircraft (camera): r = cloud at the point (0..1), g = optical depth above (rain),
        // b = cumulonimbus on the map, a = camera altitude
        float4 fragProbe (v2f i) : SV_Target
        {
            float hA;
            float d0 = 0;
            [loop] for (int k = 0; k < 4; k++)                 // average over a ~30 m cube (no flicker at the edge)
            {
                float3 o = float3((k & 1) ? 15 : -15, (k & 2) ? 8 : -8, (k == 1 || k == 2) ? 15 : -15);
                d0 += density(o, true, hA);
            }
            d0 = d0 * 0.25 / max(_Density, 1e-5);
            float camAlt = length(_PlanetC) - _R;
            float tau = 0;
            float top = _VolTop;
            if (camAlt < top)
            {
                float ds = (top - max(camAlt, _Bottom)) / 24;
                float a0 = max(camAlt, _Bottom);
                [loop] for (int j = 0; j < 24; j++)
                {
                    float3 q = float3(0, a0 - camAlt + (j + 0.5) * ds, 0);
                    tau += density(q, true, hA) * ds * _Absorb;
                }
            }
            float2 uvW = (_GD) / (2 * _WExt) + 0.5;
            float cb = tex2Dlod(_WxA, float4(uvW, 0, 0)).b;
            if (i.uv.x > 0.5)
            {
                // second pixel: rain at the camera (average of 4 nearby points) and nearby cumulonimbus
                float rb, rr = 0;
                [loop] for (int kk = 0; kk < 4; kk++) rr += rainAt(float3((kk & 1) ? 40 : -40, 0, (kk & 2) ? 40 : -40), rb, false);
                return float4(rr * 0.25, cb, 0, 0);
            }
            return float4(saturate(d0), tau, cb, camAlt);
        }

        // cumulonimbus map for lightning (read by the CPU): r = Cb tower, g = base/8000
        float4 fragCbMap (v2f i) : SV_Target
        {
            float2 uvW = i.uv;
            float4 A = tex2Dlod(_WxA, float4(uvW, 0, 0));
            float4 B = tex2Dlod(_WxB, float4(uvW, 0, 0));
            float cb = A.b;
            if (cb < 0.03) return float4(0, B.r, 0, 0);
            float base = B.r * 8000;
            float2 xz = (uvW - 0.5) * 2 * _WExt - _GD;
            float camAlt = length(_PlanetC) - _R;
            float aM = base + 0.35 * (_CbTop - base);
            float hA;
            float d = density(float3(xz.x, aM - camAlt, xz.y), true, hA) / max(_Density, 1e-4);
            return float4(saturate(d * 2) * saturate(cb * 4), B.r, 0, 0);
        }

        // ---------- sun rays (crepuscular) ----------
        // mask: open sky (no terrain) times cloud transmittance; radial blur towards the sun
        float4 fragRays (v2f i) : SV_Target
        {
            float2 uv = i.uv;
            float2 dlt = (_SunUV - uv);
            const int NS = 40;
            dlt /= NS;
            float jit = ign(i.pos.xy + _Frame * 1.7);
            float2 u = uv + dlt * jit;
            float acc = 0, w = 1, wsum = 0;
            [loop] for (int k = 0; k < NS; k++)
            {
                float T = tex2Dlod(_MainTex, float4(u, 0, 0)).a;
                float raw = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(u, 0, 0));
                float eye = LinearEyeDepth(raw);
                float sky = eye > _ProjectionParams.z * 0.999 ? 1 : 0;
                acc += T * sky * w; wsum += w;
                w *= 0.97;
                u += dlt;
            }
            return float4(acc / wsum, 0, 0, 1);
        }
        // volumetric light shafts: the air (haze/humidity) below the bases is lit by the sun only where the cloud
        // shadow (opacity depth map) lets it through -> shafts through the gaps, from any angle
        float _ShaftAmt, _ShaftSig;
        float4 fragShafts (v2f i) : SV_Target
        {
            if (_ShDOn < 0.5) return 0;
            float rl = length(i.ray);
            float3 rd = i.ray / rl;
            float sceneT = 1e9;
            float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
            if (eye < _ProjectionParams.z * 0.999) sceneT = eye * rl;
            float tEnd = min(min(sceneT, 30000), _MaxDist);
            float3 sd = normalize(_SunDir);
            float cosT = dot(rd, sd);
            const float g = 0.6;
            float hg = (1 - g * g) / (12.566 * pow(1 + g * g - 2 * g * cosT, 1.5));
            float phase = 0.8 * hg + 0.2 * 0.0796;
            const int N = 40;
            float jit = ign(i.pos.xy + _Frame * 2.3);
            float acc = 0;
            float rr = dot(_ShR, _ShR), uu = dot(_ShU, _ShU);
            [loop] for (int k = 0; k < N; k++)
            {
                float u0 = (k + jit) / N, u1 = (k + 1 + jit) / N;
                float ta = tEnd * u0 * u0, tb = tEnd * u1 * u1;
                float t = (ta + tb) * 0.5;
                float3 p = rd * t;
                float alt = altOf(p);
                if (alt > _VolTop || alt < -50) continue;
                float2 uvW = (p.xz + _GD) / (2 * _WExt) + 0.5;
                float baseL = tex2Dlod(_WxB, float4(uvW, 0, 0)).r * 8000;
                float inAir = saturate((baseL + 50 - alt) / 150);          // only the air below the clouds
                if (inAir <= 0) continue;
                float3 d0 = p - _ShO;
                float2 uv = float2(dot(d0, _ShR) / rr, dot(d0, _ShU) / uu);
                float lit = 1;
                if (all(uv > 0.01) && all(uv < 0.99))
                {
                    float sh = tex2Dlod(_NOCShD, float4(uv, 0, 0)).r;
                    lit = 1 - saturate((sh - dot(d0, sd)) / 300);
                }
                lit *= 1 - planetShadow(p);
                acc += lit * inAir * exp(-_ShaftSig * t) * (1 - exp(-_ShaftSig * (tb - ta)));
            }
            return float4(acc * phase, 0, 0, 1);
        }
        // temporal average of the shafts: jitter changes every frame and without this the haze shimmered like heat haze
        sampler2D _ShHist; float _ShBlend;
        float4 fragShaftsTA (v2f i) : SV_Target
        {
            float n = tex2D(_MainTex, i.uv).r, h = tex2D(_ShHist, i.uv).r;
            return float4(lerp(h, n, _ShBlend), 0, 0, 1);
        }
        float4 fragShaftsComp (v2f i) : SV_Target
        {
            // light blur (the ray uses per-pixel jitter at 1/4 resolution)
            // disc blur (12 samples rotating per pixel): removes jitter noise without turning blocky
            // only blends samples at the same distance (otherwise the air behind leaks around the aircraft)
            float r = 0, ws = 0;
            float a0 = ign(i.pos.xy) * 6.2832;
            float dC = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
            [unroll] for (int k = 0; k < 12; k++)
            {
                float ang = a0 + k * 2.39996, rad = sqrt((k + 0.5) / 12) * 2.5;
                float2 uk = i.uv + float2(cos(ang), sin(ang)) * rad * _MainTex_TexelSize.xy;
                float dk = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uk, 0, 0)));
                float w = exp(-abs(dk - dC) / max(min(dk, dC), 1e-3) * 12);
                r += tex2Dlod(_MainTex, float4(uk, 0, 0)).r * w; ws += w;
            }
            r /= max(ws, 1e-4);
            float3 c = _SunCol.rgb * r * _ShaftAmt * 16;
            if (_Tonemap > 0) c = 1 - exp(-c * _Exposure * 0.5); else c *= _Exposure * 0.5;
            return float4(c, 0);
        }
        // rain/humidity fog: a layer that is dense near the ground and thins with height (exponential height fog,
        // analytic integral along the ray), only below the base. Color = diffuse light under the cloud, white-grey
        // (droplets scatter all colors equally, unlike pollution), + a glow towards the sun when it shows.
        float _RFogSig, _RFogH, _RFogTop, _RFogSunVis;
        float4 fragRainFog (v2f i) : SV_Target
        {
            float rl = length(i.ray);
            float3 rd = i.ray / rl;
            float sceneT = 60000;
            float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
            if (eye < _ProjectionParams.z * 0.999) sceneT = min(eye * rl, 60000);
            float camAlt = length(_PlanetC) - _R;
            float ry = dot(rd, normalize(-_PlanetC));
            float t0 = 0, t1 = sceneT;
            if (camAlt > _RFogTop) { if (ry >= -1e-4) return float4(0, 0, 0, 1); t0 = (camAlt - _RFogTop) / -ry; }
            else if (ry > 1e-4) t1 = min(t1, (_RFogTop - camAlt) / ry);
            if (t1 <= t0) return float4(0, 0, 0, 1);
            float H = _RFogH;
            float h0 = camAlt + ry * t0, h1 = camAlt + ry * t1;
            float F = abs(ry) < 1e-3 ? _RFogSig * exp(-max(h0, 0) / H) * (t1 - t0)
                                     : _RFogSig * H / ry * (exp(-max(h0, 0) / H) - exp(-max(h1, 0) / H));
            float Tf = exp(-max(F, 0));
            float3 amb = _AmbTop.rgb * 0.6 + _AmbBottom.rgb * 0.4;
            float gl = dot(amb, float3(0.3, 0.55, 0.15));
            float3 fc = lerp(amb, gl.xxx * float3(0.97, 1.0, 1.05), 0.6) * 0.95;
            const float g = 0.7;
            float hg = (1 - g * g) / (12.566 * pow(1 + g * g - 2 * g * dot(rd, normalize(_SunDir)), 1.5));
            fc += _SunCol.rgb * hg * 0.5 * _RFogSunVis;
            if (_Tonemap > 0) fc = 1 - exp(-fc * _Exposure); else fc *= _Exposure * 0.5;
            return float4(fc * (1 - Tf), Tf);
        }
        float4 fragRaysComp (v2f i) : SV_Target
        {
            float r = tex2D(_MainTex, i.uv).r;
            float2 d = (i.uv - _SunUV) * float2(_NOCTexel.z / _NOCTexel.w, 1);
            float fall = exp(-length(d) * 2.2);
            // only boosts where there is contrast (light coming through gaps): r alone in a clear sky is just glare
            float3 c = _SunCol.rgb * r * fall * _RaysAmt;
            if (_Tonemap > 0) c = 1 - exp(-c * _Exposure * 0.5);
            return float4(c, 0);
        }

        float _NOCWater;   // 0 = before transparents (skip water), 1 = after (water only), -1 = everything
        float4 fragComposite (v2f i) : SV_Target
        {
            // depth-aware upsampling (bilateral): at aircraft/building edges only use the
            // low-res pixels that "saw" the same distance, otherwise the cloud behind leaks in blocks
            float2 lt = _NOCTexel.xy;
            float2 pc = i.uv * _NOCTexel.zw - 0.5;
            float2 fq = frac(pc);
            float2 b0 = (floor(pc) + 0.5) * lt;
            float dC = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
            float4 acc = 0; float ws = 0, maxRel = 0, bestW = -1; float4 best = 0;
            [unroll] for (int k = 0; k < 4; k++)
            {
                float2 o2 = float2(k & 1, k >> 1);
                float2 uk = b0 + o2 * lt;
                float dk = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uk, 0, 0)));
                float rel = abs(dk - dC) / max(min(dk, dC), 1e-3);
                maxRel = max(maxRel, rel);
                float wb = lerp(1 - fq.x, fq.x, o2.x) * lerp(1 - fq.y, fq.y, o2.y);
                float wd = exp(-rel * 12);
                float4 sk = tex2Dlod(_MainTex, float4(uk, 0, 0));
                acc += sk * wb * wd; ws += wb * wd;
                if (wd > bestW) { bestW = wd; best = sk; }
            }
            float4 c = ws > 1e-4 ? acc / ws : best;
            bool edge = maxRel > 0.05;
            if (_Blur > 0.01 && !edge)
            {
                // light blur (3x3 tent + 4 wide diagonals) on the cloud image (premultiplied rgb + transmittance)
                float2 o = _NOCTexel.xy * _Blur;
                float4 a = c * 4;
                a += (tex2D(_MainTex, i.uv + float2(o.x, 0)) + tex2D(_MainTex, i.uv - float2(o.x, 0)) +
                      tex2D(_MainTex, i.uv + float2(0, o.y)) + tex2D(_MainTex, i.uv - float2(0, o.y))) * 2;
                a += tex2D(_MainTex, i.uv + o) + tex2D(_MainTex, i.uv - o) +
                     tex2D(_MainTex, i.uv + float2(o.x, -o.y)) + tex2D(_MainTex, i.uv + float2(-o.x, o.y));
                c = a / 16;
            }
            if (_NOCWater > -0.5)
            {
                // does the ray hit sea level before the terrain? then the pixel is water (transparent, drawn later)
                float rl = length(i.ray);
                float3 rd = i.ray / rl;
                float sceneT = 1e9;
                float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));
                if (eye < _ProjectionParams.z * 0.999) sceneT = eye * rl;
                float2 sea = sph(rd, _PlanetC, _R);
                float camAlt = length(_PlanetC) - _R;
                bool water = camAlt > 0 && sea.y > sea.x && sea.x > 0 && sea.x < sceneT - 3;
                if (water != (_NOCWater > 0.5)) return float4(0, 0, 0, 1);
            }
            if (_Debug > 0) return float4(c.r, 0, 0, 1 - c.r * 0.7);
            return c;
        }
        ENDCG

        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMarch
            #pragma target 4.5
            ENDCG }
        Pass { Blend One SrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragComposite
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWxA
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWarp
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWxB
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWxC
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShadow
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShadowDepth
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragProbe
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCbMap
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragRays
            #pragma target 4.5
            ENDCG }
        Pass { Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragRaysComp
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShafts
            #pragma target 4.5
            ENDCG }
        Pass { Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShaftsComp
            #pragma target 4.5
            ENDCG }
        Pass { Blend One SrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragRainFog
            #pragma target 4.5
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShaftsTA
            #pragma target 4.5
            ENDCG }
    }
}
