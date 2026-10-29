Shader "Nobnak/WaterSimulation/Seabed" {
    Properties {
        [Header(Seabed Surface)]
        _ShallowColor ("Shallow Sand Color", Color) = (0.82, 0.74, 0.60, 1.0)
        _DeepColor ("Deep Sediment Color", Color) = (0.23, 0.29, 0.32, 1.0)
        _WallColor ("Basin Wall Color", Color) = (0.18, 0.20, 0.22, 1.0)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.20
        _Metallic ("Metallic", Range(0, 1)) = 0.05
        _ContourInterval ("Depth Contour Interval (m)", Float) = 1.0
        _BasinBottomDepth ("Basin Bottom Depth (m)", Float) = 12.0

        [Header(Textures & Domain)]
        [NoScaleOffset] _BathymetryTex ("Bathymetry Texture", 2D) = "black" {}
        _AreaSize ("Area Size (X, Z)", Vector) = (100, 100, 0, 0)

        [Header(Visualization Mode)]
        [Enum(RealisticSand,0,BathymetryHeatmap,1,TopographicWireframe,2)] _VisualMode ("Visualization Mode", Float) = 0
    }

    SubShader {
        Tags {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend One Zero
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
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 color : TEXCOORD3;
                float depth : TEXCOORD4;
            };

            TEXTURE2D(_BathymetryTex);
            SAMPLER(sampler_BathymetryTex);
            float4 _BathymetryTex_TexelSize;

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float4 _WallColor;
                float _Smoothness;
                float _Metallic;
                float _ContourInterval;
                float _BasinBottomDepth;
                float _VisualMode;
                float4 _AreaSize;
            CBUFFER_END

            Varyings Vert(Attributes input) {
                Varyings output;
                float2 uv = input.uv;
                float4 bathy = SAMPLE_TEXTURE2D_LOD(_BathymetryTex, sampler_BathymetryTex, uv, 0);
                float h = max(0.01, bathy.r);
                float zb = bathy.g;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                if (input.color.r > 0.75) {
                    // Seabed terrain surface: displace vertically by bed elevation zb = -h
                    worldPos.y += zb;

                    // Analytical terrain normal from bathymetry gradients: grad(y - zb) = (dh/dx, 1, dh/dz)
                    float2 texel = (_BathymetryTex_TexelSize.z > 0.0) ? _BathymetryTex_TexelSize.xy : float2(0.002, 0.002);
                    float hR = SAMPLE_TEXTURE2D_LOD(_BathymetryTex, sampler_BathymetryTex, uv + float2(texel.x, 0.0), 0).r;
                    float hL = SAMPLE_TEXTURE2D_LOD(_BathymetryTex, sampler_BathymetryTex, uv - float2(texel.x, 0.0), 0).r;
                    float hU = SAMPLE_TEXTURE2D_LOD(_BathymetryTex, sampler_BathymetryTex, uv + float2(0.0, texel.y), 0).r;
                    float hD = SAMPLE_TEXTURE2D_LOD(_BathymetryTex, sampler_BathymetryTex, uv - float2(0.0, texel.y), 0).r;

                    float spanX = max(0.1, 2.0 * texel.x * _AreaSize.x);
                    float spanZ = max(0.1, 2.0 * texel.y * _AreaSize.y);
                    float dhdx = (hR - hL) / spanX;
                    float dhdz = (hU - hD) / spanZ;
                    normalWS = normalize(float3(dhdx, 1.0, dhdz));
                } else if (input.color.r > 0.25) {
                    // Skirt top: follows bed elevation zb
                    worldPos.y += zb;
                } else {
                    // Skirt bottom: drops to fixed bottom depth
                    worldPos.y -= _BasinBottomDepth;
                }

                output.positionWS = worldPos;
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = uv;
                output.normalWS = normalWS;
                output.color = input.color;
                output.depth = h;
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
                float h = input.depth;
                bool isSeabed = (input.color.r > 0.75);

                // Lighting
                Light mainLight = GetMainLight();
                float3 normalWS = normalize(input.normalWS);
                float3 lightDir = normalize(mainLight.direction);
                float nDotL = saturate(dot(normalWS, lightDir));
                float3 diffuseLight = mainLight.color * (nDotL * 0.65 + 0.35);
                float3 ambient = float3(0.06, 0.10, 0.14);

                if (!isSeabed) {
                    // Basin wall: dark architectural slate with horizontal depth groove lines
                    float wallY = input.positionWS.y;
                    float groove = smoothstep(0.05, 0.0, abs(frac(wallY) - 0.5) - 0.45);
                    float3 wallCol = _WallColor.rgb * (1.0 - groove * 0.3);
                    float3 finalWall = wallCol * diffuseLight + ambient;
                    return half4(finalWall, 1.0);
                }

                int vmode = (int)(_VisualMode + 0.5);

                if (vmode == 1) {
                    // Mode 1: Hypsometric Bathymetry Heatmap
                    float normDepth = 1.0 - saturate((h - 0.5) / 10.0) * 1.9;
                    float3 col = HeatmapColor(normDepth);
                    float contour = frac(h / max(0.1, _ContourInterval));
                    float contourLine = smoothstep(0.06, 0.0, abs(contour - 0.5) - 0.44);
                    col = lerp(col, float3(0.08, 0.08, 0.08), contourLine * 0.65);
                    return half4(col * (nDotL * 0.4 + 0.6), 1.0);
                } else if (vmode == 2) {
                    // Mode 2: Topographic Wireframe / Grid
                    float2 grid = frac(uv * 64.0);
                    float edge = min(min(grid.x, 1.0 - grid.x), min(grid.y, 1.0 - grid.y));
                    float wire = smoothstep(0.0, 0.06, edge);
                    float3 base = lerp(float3(0.85, 0.75, 0.5), float3(0.15, 0.20, 0.25), wire);
                    float contour = frac(h / max(0.1, _ContourInterval));
                    float contourLine = smoothstep(0.06, 0.0, abs(contour - 0.5) - 0.44);
                    base = lerp(base, float3(1.0, 0.9, 0.3), contourLine * 0.8);
                    return half4(base, 1.0);
                }

                // Mode 0: Realistic Sea Floor (Sand, Sediment & Subtle Depth Contours)
                float tDepth = saturate((h - 0.5) / 4.0);
                float3 bedCol = lerp(_ShallowColor.rgb, _DeepColor.rgb, tDepth);

                // Subtle sand ripple micro-texture
                float2 rippleUV = uv * _AreaSize.xy * 0.8;
                float ripple = sin(rippleUV.x * 2.5 + rippleUV.y * 1.8) * cos(rippleUV.y * 2.2);
                bedCol += ripple * 0.03 * (1.0 - tDepth * 0.6);

                // Depth contour lines (1m intervals)
                float contour = frac(h / max(0.1, _ContourInterval));
                float contourLine = smoothstep(0.05, 0.0, abs(contour - 0.5) - 0.45);
                bedCol = lerp(bedCol, bedCol * 0.72, contourLine * 0.55);

                // Specular highlight
                float3 viewDirWS = normalize(GetCameraPositionWS() - input.positionWS);
                float3 halfDir = normalize(lightDir + viewDirWS);
                float nDotH = saturate(dot(normalWS, halfDir));
                float spec = pow(nDotH, 32.0) * _Smoothness;
                float3 specular = mainLight.color * spec * _Metallic * 0.5;

                float3 finalColor = bedCol * diffuseLight + specular + ambient;
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
