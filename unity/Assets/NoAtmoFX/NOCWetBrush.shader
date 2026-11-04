// Wet glass brush: draws drops, trails and fading into the angular textures (512x256).
// Meshes arrive in clip coordinates (-1..1); uv = position inside the quad (-1..1); color = data.
Shader "Hidden/NOClouds/WetBrush"
{
    Properties { _Fade ("Fade", Float) = 0.98 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        float _Fade;
        struct a2v { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
        v2f vert (a2v v)
        {
            v2f o;
            o.pos = float4(v.vertex.xy, 0.5, 1);
            #if UNITY_UV_STARTS_AT_TOP
            o.pos.y = -o.pos.y;
            #endif
            o.uv = v.uv; o.color = v.color;
            return o;
        }
        // drop: flattened lens, slightly irregular outline (seed in g/b)
        float4 fragDrop (v2f i) : SV_Target
        {
            // moving (a < 1): tadpole shape, wide head in front (+y) and thin tail behind
            if (i.color.a < 0.9) i.uv.x /= lerp(0.35, 1.0, saturate(i.uv.y * 0.5 + 0.5));
            float a = atan2(i.uv.y, i.uv.x);
            // irregular outline: each drop has its own lobes (seeds g/b), never a perfect circle
            float wob = 1 + 0.11 * sin(a * 2 + i.color.b * 9.1) + 0.07 * sin(a * 3 + i.color.g * 6.283)
                          + 0.035 * sin(a * 5 + i.color.b * 6.283) + 0.015 * sin(a * 7 + i.color.g * 11.3);
            float r2 = dot(i.uv, i.uv) / (wob * wob);
            if (r2 >= 1) discard;
            float h = sqrt(1 - r2) * i.color.r * (0.8 + 0.4 * frac(i.color.g * 7.31));   // height varies too
            return float4(h, i.color.g, i.color.b, 1);
        }
        // glass mask in the angular map: the aircraft's glass meshes projected from the pilot's eye
        float4x4 _AcW2L; float3 _EyeL;
        struct v2m { float4 pos : SV_POSITION; };
        v2m vertMask (float4 vertex : POSITION)
        {
            v2m o;
            float3 w = mul(unity_ObjectToWorld, vertex).xyz;
            float3 d = mul(_AcW2L, float4(w, 1)).xyz - _EyeL;
            float az = degrees(atan2(d.x, d.z)), el = degrees(atan2(d.y, length(d.xz)));
            float2 uv = float2((az + 120) / 240, (el + 30) / 110);
            o.pos = float4(uv * 2 - 1, 0.5, 1);
            #if UNITY_UV_STARTS_AT_TOP
            o.pos.y = -o.pos.y;
            #endif
            return o;
        }
        float4 fragMask (v2m i) : SV_Target { return 1; }
        // mask blur (9 samples, step _BlurStep in uv) -> drops below 1 near the edge
        sampler2D _MainTex; float2 _BlurStep;
        float4 fragBlur (v2f i) : SV_Target
        {
            float s = 0;
            [unroll] for (int y = -2; y <= 2; y++) [unroll] for (int x = -2; x <= 2; x++)
                s += tex2D(_MainTex, i.uv + float2(x, y) * _BlurStep).r;
            return s / 25;
        }
        float4 fragFade (v2f i) : SV_Target { return float4(_Fade, _Fade, _Fade, _Fade); }
        // trail: thin wet strip (rounded profile across its width)
        float4 fragTrail (v2f i) : SV_Target
        {
            float w = saturate(1 - i.uv.y * i.uv.y);
            return float4(i.color.r * w, 0, 0, 1);
        }
        ENDCG
        Pass { BlendOp Max  Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDrop
            ENDCG }
        Pass { Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragFade
            ENDCG }
        Pass { BlendOp Max  Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragTrail
            ENDCG }
        Pass { Blend One Zero
            CGPROGRAM
            #pragma vertex vertMask
            #pragma fragment fragMask
            ENDCG }
        Pass { Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlur
            ENDCG }
    }
}
