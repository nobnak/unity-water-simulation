Shader "Nobnak/WaterSimulation/WaterSurfaceCommon" {
    Properties {
        [Header(Water Surface)]
        _BaseColor ("Shallow Color", Color) = (0.1, 0.45, 0.55, 0.85)
        _DeepColor ("Deep Color", Color) = (0.02, 0.15, 0.28, 0.95)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.96
        _Metallic ("Metallic", Range(0, 1)) = 0.05
        _DisplacementScale ("Displacement Scale", Float) = 1.0

        [Header(Textures)]
        [NoScaleOffset] _WaterDataTex ("Water Data Texture (RGB: Disp, A: Foam)", 2D) = "black" {}
        [NoScaleOffset] _NormalTex ("Normal Texture (RGB: Normal, A: Foam)", 2D) = "black" {}
        [NoScaleOffset] _BathymetryTex ("Bathymetry Texture (R: Depth h, G: Bed z_b)", 2D) = "black" {}
        _HasBathymetry ("Has Bathymetry", Float) = 0.0

        [Header(Foam)]
        _FoamColor ("Foam Color", Color) = (0.85, 0.92, 0.98, 0.8)
        _FoamIntensity ("Foam Intensity", Range(0, 5)) = 0.6

        [Header(Visualization Mode)]
        [Enum(Realistic,0,HeightMap,1,NormalVisual,2,Wireframe,3,Bathymetry,4)] _VisualMode ("Visualization Mode", Float) = 0
        _HeightMapRange ("Height Map Range (+/- m)", Float) = 2.0
        _NormalExaggeration ("Normal XZ Exaggeration", Range(0.5, 4.0)) = 2.0
    }

    SubShader {
        Tags {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 displacement : TEXCOORD3;
            };

            TEXTURE2D(_WaterDataTex);
            SAMPLER(sampler_WaterDataTex);
            TEXTURE2D(_NormalTex);
            SAMPLER(sampler_NormalTex);
            TEXTURE2D(_BathymetryTex);
            SAMPLER(sampler_BathymetryTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _DeepColor;
                float4 _FoamColor;
                float _Smoothness;
                float _Metallic;
                float _DisplacementScale;
                float _FoamIntensity;
                float _VisualMode;
                float _HeightMapRange;
                float _NormalExaggeration;
                float _HasBathymetry;
            CBUFFER_END

            Varyings Vert(Attributes input) {
                Varyings output;
                float4 disp = SAMPLE_TEXTURE2D_LOD(_WaterDataTex, sampler_WaterDataTex, input.uv, 0);
                output.displacement = disp;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                // Apply 3D displacement (R: dX, G: dY, B: dZ)
                worldPos += float3(disp.r, disp.g, disp.b) * _DisplacementScale;

                output.positionWS = worldPos;
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float3 HeatmapColor(float t) {
                // Blue (-1) -> Cyan (-0.5) -> Green (0) -> Yellow (0.5) -> Red (1)
                t = clamp(t, -1.0, 1.0);
                float val = t * 0.5 + 0.5;
                float r = clamp(1.5 - abs(val * 4.0 - 3.0), 0.0, 1.0);
                float g = clamp(1.5 - abs(val * 4.0 - 2.0), 0.0, 1.0);
                float b = clamp(1.5 - abs(val * 4.0 - 1.0), 0.0, 1.0);
                return float3(r, g, b);
            }

            half4 Frag(Varyings input) : SV_Target {
                float2 uv = input.uv;
                float4 disp = input.displacement;
                float4 normalSample = SAMPLE_TEXTURE2D(_NormalTex, sampler_NormalTex, uv);

                float3 rawNormalWS = normalize(input.normalWS);
                float3 normalWS = rawNormalWS;
                if (length(normalSample.xyz) > 0.1) {
                    rawNormalWS = normalize(normalSample.xyz);
                    float3 perturbed = rawNormalWS;
                    float exag = (_NormalExaggeration > 0.01) ? _NormalExaggeration : 1.0;
                    perturbed.xz *= exag;
                    normalWS = normalize(perturbed);
                }

                int vmode = (int)(_VisualMode + 0.5);
                if (vmode == 1) {
                    float normHeight = disp.g / max(0.01, _HeightMapRange);
                    float3 col = HeatmapColor(normHeight);
                    float contour = frac(disp.g * 5.0);
                    col *= (contour > 0.9 || contour < 0.1) ? 0.8 : 1.0;
                    return half4(col, 1.0);
                } else if (vmode == 2) {
                    return half4(rawNormalWS * 0.5 + 0.5, 1.0);
                } else if (vmode == 3) {
                    float2 grid = frac(uv * 64.0);
                    float edge = min(min(grid.x, 1.0 - grid.x), min(grid.y, 1.0 - grid.y));
                    float wire = smoothstep(0.0, 0.05, edge);
                    float3 base = lerp(float3(0.1, 0.7, 0.9), float3(0.02, 0.05, 0.1), wire);
                    return half4(base, 1.0);
                } else if (vmode == 4) {
                    if (_HasBathymetry > 0.5) {
                        float h = max(0.0, SAMPLE_TEXTURE2D(_BathymetryTex, sampler_BathymetryTex, uv).r);
                        // Map depth h [0.5m .. 15m] -> warm shallow (+0.85) to deep blue (-0.9)
                        float normDepth = 1.0 - saturate((h - 1.0) / 14.0) * 1.9;
                        float3 col = HeatmapColor(normDepth);
                        // 1m depth contours
                        float contour = frac(h);
                        col *= (contour > 0.92 || contour < 0.08) ? 0.72 : 1.0;
                        // Subtle surface wave overlay so wave crests remain visible on bathymetry map
                        col += disp.g * 0.12;
                        return half4(saturate(col), 1.0);
                    }
                    return half4(0.08, 0.22, 0.35, 1.0);
                }

                // Realistic PBR Water Rendering
                Light mainLight = GetMainLight();
                float3 viewDirWS = normalize(GetCameraPositionWS() - input.positionWS);

                // Fresnel (Schlick)
                float nDotV = saturate(dot(normalWS, viewDirWS));
                float fresnel = 0.02 + 0.98 * pow(1.0 - nDotV, 5.0);

                // Water color depth blend with heightened contrast + choppy crest compression
                float crestPinch = saturate(normalSample.a);
                float depthFactor = saturate(disp.g * 0.35 + crestPinch * 0.25 + 0.45);
                float3 waterCol = lerp(_DeepColor.rgb, _BaseColor.rgb, depthFactor);

                // Bathymetry shallow water transmittance (shoal & cape emerald tint + depth contours)
                if (_HasBathymetry > 0.5) {
                    float h = max(0.1, SAMPLE_TEXTURE2D(_BathymetryTex, sampler_BathymetryTex, uv).r);
                    float shoal = saturate(exp(-(h - 1.2) * 0.26));
                    float3 shoalColor = float3(0.16, 0.62, 0.56);
                    float bedContour = smoothstep(0.08, 0.0, abs(frac(h * 0.5) - 0.5));
                    shoalColor *= (1.0 - bedContour * 0.14 * shoal);
                    waterCol = lerp(waterCol, shoalColor, shoal * 0.68);
                }

                // Lighting
                float3 lightDir = normalize(mainLight.direction);
                float nDotL = saturate(dot(normalWS, lightDir));
                float3 halfDir = normalize(lightDir + viewDirWS);
                float nDotH = saturate(dot(normalWS, halfDir));
                float spec = pow(nDotH, 64.0) * _Smoothness;

                float3 diffuse = waterCol * (mainLight.color * (nDotL * 0.55 + 0.45));
                float3 specular = mainLight.color * (spec * (fresnel * 0.85 + 0.15) * 2.0);
                float3 ambient = float3(0.05, 0.12, 0.18);

                // Crest Subsurface Scattering (SSS): thin choppy wave ridges transmit turquoise-jade light
                float crestHeightMask = saturate(disp.g * 0.45 + 0.35);
                float backLight = saturate(dot(viewDirWS, -lightDir) * 0.5 + 0.5);
                float sssFactor = crestPinch * crestHeightMask * (0.45 + 0.55 * backLight);
                float3 sssColor = float3(0.14, 0.72, 0.68) * mainLight.color * sssFactor * 0.55;

                float3 finalColor = diffuse + specular + sssColor + ambient;

                // Foam blending: only blend foam if disp.a is active
                float foam = saturate(disp.a * _FoamIntensity);
                finalColor = lerp(finalColor, _FoamColor.rgb, foam);

                float baseAlpha = _BaseColor.a;
                if (_HasBathymetry > 0.5) {
                    float h = max(0.1, SAMPLE_TEXTURE2D(_BathymetryTex, sampler_BathymetryTex, uv).r);
                    float shallowClarity = saturate(1.0 - (h - 0.4) / 4.0);
                    baseAlpha = lerp(_BaseColor.a, 0.55, shallowClarity * 0.65);
                }
                float alpha = lerp(baseAlpha, 1.0, fresnel * 0.4 + foam * 0.6);
                return half4(finalColor, alpha);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
