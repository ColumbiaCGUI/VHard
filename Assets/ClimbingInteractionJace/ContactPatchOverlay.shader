Shader "VHard/Contact Patch Overlay"
{
    Properties
    {
        _ContactThreshold ("Solid Contact Distance", Float) = 0.008
        _ProximityThreshold ("Patch Falloff Distance", Float) = 0.025
        _PatchAlpha ("Patch Alpha", Range(0, 1)) = 0.82
        _ProximityColor ("Whole-Hand Proximity Color", Color) = (0.2, 0.75, 1, 1)
        _ProximityAlpha ("Whole-Hand Proximity Alpha", Range(0, 1)) = 0.24
        _GripScore ("Grip Score", Range(0, 1)) = 0
        _RimGlowEnabled ("Rim Glow Enabled", Range(0, 1)) = 0
        _RimGlowAlpha ("Rim Glow Alpha", Range(0, 1)) = 0.35
        _RimGlowPower ("Rim Glow Power", Float) = 3
        _RimColor ("Rim Color", Color) = (0.1, 0.85, 0.2, 1)
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+20"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "ContactPatch"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _ContactData;

            CBUFFER_START(UnityPerMaterial)
                float _ContactThreshold;
                float _ProximityThreshold;
                float _PatchAlpha;
                float4 _ProximityColor;
                float _ProximityAlpha;
                float _GripScore;
                float _RimGlowEnabled;
                float _RimGlowAlpha;
                float _RimGlowPower;
                float4 _RimColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                nointerpolation float tipId : TEXCOORD0;
                nointerpolation float distanceToTip : TEXCOORD1;
                float handDistance : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float3 viewDirectionWS : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionHCS = TransformWorldToHClip(positionWS);
                float4 contactData = _ContactData[input.vertexID];
                output.tipId = contactData.z;
                output.distanceToTip = contactData.w;
                output.handDistance = min(contactData.x, contactData.y);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                return output;
            }

            half3 FingerColor(int finger)
            {
                if (finger == 0) return half3(0.94, 0.24, 0.20);
                if (finger == 1) return half3(1.00, 0.62, 0.10);
                if (finger == 2) return half3(0.20, 0.82, 0.36);
                if (finger == 3) return half3(0.18, 0.58, 1.00);
                return half3(0.72, 0.30, 0.95);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                int tipId = (int)round(input.tipId);
                float distanceToTip = input.distanceToTip;
                half3 patchColor = half3(0, 0, 0);
                float patchAlpha = 0;
                if (tipId >= 0 && tipId <= 9 && distanceToTip <= _ProximityThreshold)
                {
                    float falloff = saturate(
                        (_ProximityThreshold - distanceToTip) /
                        max(_ProximityThreshold - _ContactThreshold, 0.0001));
                    falloff = distanceToTip <= _ContactThreshold ? 1.0 : falloff * falloff;
                    patchColor = FingerColor(tipId >= 5 ? tipId - 5 : tipId);
                    patchAlpha = _PatchAlpha * falloff;
                }
                else if (input.handDistance <= _ProximityThreshold)
                {
                    float proximityFalloff = saturate(
                        (_ProximityThreshold - input.handDistance) /
                        max(_ProximityThreshold - _ContactThreshold, 0.0001));
                    patchColor = _ProximityColor.rgb;
                    patchAlpha = _ProximityAlpha * proximityFalloff;
                }

                float rim = pow(
                    1.0 - saturate(dot(normalize(input.normalWS), normalize(input.viewDirectionWS))),
                    _RimGlowPower);
                float rimAlpha = rim * _RimGlowAlpha * _GripScore * _RimGlowEnabled;
                float combinedAlpha = max(patchAlpha, rimAlpha);
                if (combinedAlpha <= 0.0001)
                {
                    discard;
                }

                float patchWeight = patchAlpha / max(patchAlpha + rimAlpha, 0.0001);
                half3 color = lerp(_RimColor.rgb, patchColor, patchWeight);
                return half4(color, combinedAlpha);
            }
            ENDHLSL
        }
    }
}
