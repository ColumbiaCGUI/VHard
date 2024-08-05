Shader "Custom/UnlitMoonVerticesDoubleSided"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _AmbientColor ("Ambient Color", Color) = (0.2, 0.2, 0.2, 1)
        _LightDirection ("Light Direction", Vector) = (0.5, 0.5, 0.5, 0)
    }

    SubShader
    {
        Tags {"RenderType"="Opaque"}
        LOD 100

        // Disable culling
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float3 worldNormal : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _BaseColor;
            float4 _AmbientColor;
            float4 _LightDirection;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                //o.uv = v.uv;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Normalize light direction
                float3 lightDir = normalize(_LightDirection.xyz);
                
                // Basic diffuse lighting calculation
                float ndotl = max(0, dot(i.worldNormal, lightDir));
                float3 lighting = _AmbientColor.rgb + ndotl;

                // Blend the base color with the vertex color and apply lighting
                fixed4 finalColor = i.color * _BaseColor * fixed4(lighting, 1);
                return finalColor;
            }
            ENDCG
        }
    }
}