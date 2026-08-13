Shader "VHard/Contact Patch Overlay"
{
    Properties
    {
        _AffordanceColor ("Affordance Color", Color) = (0.1, 0.85, 0.2, 1)
        _AffordanceAlpha ("Affordance Alpha", Range(0, 1)) = 0
        _AffordanceRimPower ("Affordance Rim Power", Range(0.5, 8)) = 3
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

            CBUFFER_START(UnityPerMaterial)
                float4 _AffordanceColor;
                float _AffordanceAlpha;
                float _AffordanceRimPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirectionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                return output;
            }

            // Face-on surface returns rim 0 and blends to nothing, so the hold keeps its own scanned
            // colour and only the silhouette carries the cue. No clip/discard: the renderer is
            // switched off when there is no affordance, and a zero-alpha blend costs a tile-based GPU
            // less than losing early-Z for the whole pass.
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float facing = saturate(dot(normalize(input.normalWS), normalize(input.viewDirectionWS)));
                float rim = pow(1.0 - facing, _AffordanceRimPower);
                return half4(_AffordanceColor.rgb, rim * _AffordanceAlpha);
            }
            ENDHLSL
        }
    }
}
