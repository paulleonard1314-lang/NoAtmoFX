// Wet glass / frost on the canopy: drawn over the clouds using the aircraft's own glass meshes.
// Drops live in an angular map around the airflow axis (x = which side, y = how far it has run back
// from the stagnation point), seen from the pilot's eye in aircraft space: they stick to the glass
// whatever the mesh is and run to the end of the canopy. Frost uses the older azimuth/elevation map.
Shader "Hidden/NOClouds/WetGlass"
{
    Properties { }
    SubShader
    {
        Tags { "Queue" = "Transparent+100" "RenderType" = "Transparent" }
        Cull Off ZWrite Off ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            sampler2D _DropTex, _TrailTex, _SceneTex, _EdgeTex;   // _EdgeTex: blurred glass mask (1 in the middle, falls off near the edge)
            float4 _SceneTex_TS;            // 1/w, 1/h, w, h
            float4x4 _AcW2L, _AcL2W;        // world <-> aircraft space
            float3 _EyeL;                   // pilot eye in aircraft space
            float _Wetness, _Frost, _Refract, _FrostWhite, _Film;   // _Film: water film at high speed
            float3 _FrostCol, _SunL;        // lit frost color; sun direction in aircraft space
            float3 _SunCol;

            struct a2v { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 wp : TEXCOORD0; float4 sp : TEXCOORD1; };

            v2f vert (a2v v)
            {
                v2f o;
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = mul(UNITY_MATRIX_VP, float4(o.wp, 1));
                o.sp = ComputeScreenPos(o.pos);
                return o;
            }

            float hash12(float2 p) { float3 p3 = frac(p.xyx * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.x + p3.y) * p3.z); }
            float vnoise(float2 x)
            {
                float2 i = floor(x), f = x - i; f = f * f * (3 - 2 * f);
                float a = hash12(i), b = hash12(i + float2(1, 0)), c = hash12(i + float2(0, 1)), d = hash12(i + 1);
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }
            float fbm(float2 x) { float s = 0, a = 0.5; [unroll] for (int k = 0; k < 5; k++) { s += a * vnoise(x); x = x * 2.07 + 13.1; a *= 0.5; } return s; }

            float3 scene(float2 uv) { return tex2D(_SceneTex, uv).rgb; }
            float3 sceneBlur(float2 uv, float r)
            {
                float2 o = _SceneTex_TS.xy * r;
                return (scene(uv) * 2 + scene(uv + float2(o.x, o.y)) + scene(uv - float2(o.x, o.y)) + scene(uv + float2(o.x, -o.y)) + scene(uv + float2(-o.x, o.y))) / 6;
            }

            static const float3 AX = float3(0, -0.05234, 0.99863), AU = float3(0, 0.99863, 0.05234), AR = float3(1, 0, 0);
            static const float THMAX = 2.79253;   // 160°
            static const float PXA = 0.003927;    // angular pixel of the simulation (0.225°)
            // vector in aircraft space -> screen direction (x right, y up)
            float2 toS(float3 v) { return mul((float3x3)UNITY_MATRIX_V, mul((float3x3)_AcL2W, v)).xy; }

            float4 frag (v2f i) : SV_Target
            {
                float3 d = mul(_AcW2L, float4(i.wp, 1)).xyz - _EyeL;
                float az = degrees(atan2(d.x, d.z)), el = degrees(atan2(d.y, length(d.xz)));
                float2 ouv = float2((az + 120) / 240, (el + 30) / 110);   // old map: mist and frost noise
                float3 dn = normalize(d);
                float th = acos(clamp(dot(dn, AX), -1, 1)), ph = atan2(dot(dn, AR), dot(dn, AU));
                float2 uv = float2(ph / 6.28319 + 0.5, th / THMAX);
                float sth = max(sin(th), 0.05);
                float3 rad = AU * cos(ph) + AR * sin(ph);
                float2 sTh = toS(rad * cos(th) - AX * sin(th));   // "backwards" on screen
                float2 sPh = toS(AR * cos(ph) - AU * sin(ph));    // "sideways" on screen
                // map gradient (per map pixel) -> per angular pixel -> screen direction
                float kx = min(PXA / (6.28319 * sth / 1536), 4), ky = PXA / (THMAX / 768);
                float2 suv = i.sp.xy / i.sp.w;
                bool inMap = uv.y < 1;

                float3 col = 0; float alpha = 0;   // premultiplied accumulation (over)

                // wet film (light fogging inside cloud/rain)
                if (_Wetness > 0.001)
                {
                    float hz = _Wetness * (0.10 + 0.08 * fbm(ouv * float2(30, 15)));
                    col = sceneBlur(suv, 3) * 1.03 * hz; alpha = hz;
                }
                // high speed: water becomes a film running backwards; you barely see water, only distortion
                if (_Film > 0.001 && inMap)
                {
                    // water strands running back along the airflow (sideways = ph, backwards = th)
                    float2 q2 = float2(uv.x * 120, degrees(th) * 0.13 - _Time.y * 9);
                    float n = vnoise(q2) * 0.6 + vnoise(q2 * 2.3 + 7.1) * 0.4;
                    float2 off = sPh * (n - 0.5) * _Refract * 0.5 * _Film * saturate(degrees(th) / 8);
                    float3 c = scene(suv + off);
                    float a = _Film * 0.85;
                    col = c * a + col * (1 - a); alpha = a + alpha * (1 - a);
                }
                if (inMap)
                {
                    float2 t = float2(1.0 / 1536, 1.0 / 768);
                    // trails: water that ran off, weak refraction
                    float tr = tex2D(_TrailTex, uv).r;
                    if (tr > 0.01)
                    {
                        float tx = tex2D(_TrailTex, uv + float2(t.x, 0)).r - tex2D(_TrailTex, uv - float2(t.x, 0)).r;
                        float ty = tex2D(_TrailTex, uv + float2(0, t.y)).r - tex2D(_TrailTex, uv - float2(0, t.y)).r;
                        float3 c = scene(suv - (sPh * tx * kx + sTh * ty * ky) * _Refract * 0.6) * 1.02;
                        float a = saturate(tr * 0.8);
                        col = c * a + col * (1 - a); alpha = a + alpha * (1 - a);
                    }
                    // drops: lens that flips/shifts the image, dark rim, sun glint
                    float h = tex2D(_DropTex, uv).r;
                    if (h > 0.005)
                    {
                        float hx = tex2D(_DropTex, uv + float2(t.x, 0)).r - tex2D(_DropTex, uv - float2(t.x, 0)).r;
                        float hy = tex2D(_DropTex, uv + float2(0, t.y)).r - tex2D(_DropTex, uv - float2(0, t.y)).r;
                        float2 g = sPh * hx * kx + sTh * hy * ky;   // on screen
                        float3 n = normalize(float3(-g * 3, 1));
                        float3 c = scene(suv - n.xy * _Refract * 0.3 * (0.5 + h));   // small lens: water is transparent, so it only shifts/flips the image inside the drop, no brightening
                        float rim = saturate(length(g) * 2.0) * (1 - smoothstep(0.04, 0.25, h));   // thin rim only
                        c *= 1 - 0.30 * rim;
                        float3 sv = mul((float3x3)UNITY_MATRIX_V, mul((float3x3)_AcL2W, _SunL));
                        float2 sl = normalize(sv.xy + 1e-4);
                        float spec = pow(saturate(dot(n.xy, sl) * 1.6 - 0.2), 8) * saturate(-sv.z + 0.3);
                        // glint only on large parked drops; small/moving drops are just distortion (they used to become white dots)
                        float big = smoothstep(0.25, 0.6, h);
                        c += (_SunCol * spec * 0.35 + pow(saturate(1 - length(n.xy - float2(-0.25, 0.35) * 0.6) * 2.2), 6) * 0.08) * big;
                        float a = smoothstep(0.0, 0.10, h);
                        col = c * a + col * (1 - a); alpha = a + alpha * (1 - a);
                    }
                }
                // frost: grows from the edges of the glass (away from the front), feather-shaped crystals
                if (_Frost > 0.001)
                {
                    // only near the glass edges/frame (blurred mask < 1), advancing in irregular fingers
                    float e = saturate((1 - tex2D(_EdgeTex, ouv).r) * 2.2);
                    float n1 = fbm(ouv * float2(36, 18));
                    float n2 = fbm(ouv * float2(260, 130) + n1 * 2);         // fine ice grain
                    float grow = e * 0.85 + (n1 - 0.5) * 0.35 + n2 * 0.06;
                    float m = smoothstep(1.05 - _Frost, 1.05 - _Frost + 0.10, grow) * (0.75 + 0.25 * n2);
                    if (m > 0.001)
                    {
                        float3 c = lerp(sceneBlur(suv, 6 + 10 * m), _FrostCol * (0.85 + 0.3 * n2), _FrostWhite * (0.45 + 0.4 * m));
                        col = c * m + col * (1 - m); alpha = m + alpha * (1 - m);
                    }
                }
                if (alpha < 0.002) discard;
                return float4(col / alpha, alpha);   // col is premultiplied
            }
            ENDCG
        }
    }
}
