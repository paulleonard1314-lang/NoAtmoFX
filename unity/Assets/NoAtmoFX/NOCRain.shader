// Rain near the camera: streaks falling in a box around the camera (world-locked, wrapping around the box),
// stretched by the relative velocity (rain - aircraft). Each quad in the mesh carries a seed in uv2.
Shader "Hidden/NOClouds/Rain"
{
    Properties { }
    SubShader
    {
        Tags { "Queue" = "Transparent+50" "RenderType" = "Transparent" }
        Cull Off ZWrite Off ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            float3 _RainCam;      // camera position (world)
            float3 _RainVelRel;   // rain velocity relative to the camera (m/s, world)
            float3 _RainVelW;     // rain velocity in the world (wind + fall)
            float _RainBox, _RainAmt, _RainTime, _RainExp;
            float4 _RainCol;      // ambient light + flash

            struct a2v { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 seed : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float a : TEXCOORD1; };

            v2f vert (a2v v)
            {
                v2f o;
                float3 s = v.seed.xyz;
                float B = _RainBox;
                // world-locked drop falling at rain speed; the box follows the camera
                float3 wp = s * B + _RainVelW * _RainTime;
                float3 rel = wp - _RainCam;
                rel = (frac(rel / B + 0.5) - 0.5) * B;
                // denser up close (half of the drops in a smaller box)
                if (v.seed.w > 0.5) { rel *= 0.35; }
                float3 c = _RainCam + rel;
                float sp = length(_RainVelRel);
                float3 ax = sp > 0.01 ? _RainVelRel / sp : float3(0, -1, 0);
                // streak = distance travelled during the exposure (minimum 0.25 m), thin
                float d = length(rel);
                // never longer than half the distance to the eye: a drop right at the camera stretched to 6 m
                // covered the whole screen (invisible, but every pixel was shaded again = big frame drops)
                float len = clamp(sp * _RainExp, 0.1, min(6, d * 0.5));
                float3 vd = normalize(c - _WorldSpaceCameraPos);
                float3 sd = normalize(cross(ax, vd) + 1e-5);
                float w = 0.006 + 0.0012 * length(rel);
                float3 p = c + ax * (v.uv.y - 0.5) * len + sd * (v.uv.x - 0.5) * w;
                o.pos = mul(UNITY_MATRIX_VP, float4(p, 1));
                // drops too close to see (alpha 0 below 1.5 m) or past the fade are collapsed, nothing is drawn
                if (d < 1.5 || d > B * 0.5 || _RainAmt <= 0) o.pos = float4(0, 0, -2, 1);
                o.uv = v.uv;
                o.a = _RainAmt * saturate(d / 1.5) * saturate(1 - d / (B * 0.5)) * (0.5 + 0.5 * frac(s.x * 17.3)) * saturate(0.35 / max(len, 0.25) + 0.25);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float e = 1 - abs(i.uv.x - 0.5) * 2;
                float l = saturate(1 - abs(i.uv.y - 0.5) * 2);
                float a = i.a * e * sqrt(l);
                if (a < 0.002) discard;
                return float4(_RainCol.rgb, a);
            }
            ENDCG
        }
    }
}
