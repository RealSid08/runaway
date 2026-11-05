Shader "Runaway/Plume"
{
    Properties
    {
        _Conc ("Concentration (ppm)", 2D) = "black" {}
        _Opacity ("Opacity", Range(0,1)) = 0.85
        _Low ("Low", Color) = (0.95, 0.78, 0.36, 1)
        _Mid ("Mid", Color) = (0.98, 0.52, 0.22, 1)
        _High ("High", Color) = (0.93, 0.25, 0.22, 1)
        _Severe ("Severe", Color) = (0.86, 0.22, 0.62, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-10" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Offset -1, -1
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_Conc); SAMPLER(sampler_Conc);
            CBUFFER_START(UnityPerMaterial)
                float _Opacity;
                half4 _Low, _Mid, _High, _Severe;
            CBUFFER_END
            struct A { float4 pos : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; };
            V vert (A i)
            {
                V o;
                VertexPositionInputs p = GetVertexPositionInputs(i.pos.xyz);
                o.pos = p.positionCS; o.uv = i.uv; o.world = p.positionWS;
                return o;
            }
            float hash (float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float noise (float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3 - 2 * f);
                return lerp(lerp(hash(i), hash(i + float2(1,0)), u.x), lerp(hash(i + float2(0,1)), hash(i + float2(1,1)), u.x), u.y);
            }
            half4 frag (V i) : SV_Target
            {
                float c = SAMPLE_TEXTURE2D(_Conc, sampler_Conc, i.uv).r;
                float l = log10(max(c, 1e-4));
                float body = smoothstep(-1.1, -0.3, l);
                half3 col = lerp(_Low.rgb, _Mid.rgb, smoothstep(-0.4, 0.3, l));
                col = lerp(col, _High.rgb, smoothstep(0.4, 1.1, l));
                col = lerp(col, _Severe.rgb, smoothstep(1.2, 1.45, l));
                float w = max(fwidth(l) * 1.4, 1e-4);
                float iso1 = (1 - smoothstep(0, w, abs(l - 0.0))) * step(-0.2, l + 0.2);
                float iso24 = 1 - smoothstep(0, w, abs(l - 1.3802));
                float n = noise(i.world.xz * 0.035 + _Time.y * float2(0.18, 0.11)) * 0.6 + noise(i.world.xz * 0.11 - _Time.y * 0.3) * 0.4;
                float edge = smoothstep(0, 0.06, i.uv.x) * smoothstep(0, 0.06, i.uv.y) * smoothstep(1, 0.94, i.uv.x) * smoothstep(1, 0.94, i.uv.y);
                float a = body * (0.12 + 0.12 * n + 0.22 * smoothstep(-0.2, 1.3, l)) * _Opacity;
                a = max(a, (iso1 * 0.75 + iso24 * 0.9) * _Opacity);
                col = lerp(col, half3(1, 0.96, 0.9), iso1 * 0.35);
                return half4(col, saturate(a * edge));
            }
            ENDHLSL
        }
    }
}
