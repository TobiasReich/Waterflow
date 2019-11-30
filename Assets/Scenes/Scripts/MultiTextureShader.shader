Shader "Custom/MultiTextureShader"
{
    Properties
    {
        _WaterTex ("Water (RGB)", 2D) = "white" {}
        _HeightTex ("HeightShading (RGB)", 2D) = "white" {}
        _WaterMaskTex ("Water-MASK (RGB)", 2D) = "white" {}
        _MountainTex ("MountainTexture (RGB)", 2D) = "white" {}
        _GrassTex ("GrassTexture (RGB)", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows

        // Use shader model 3.0 target, to get nicer looking lighting
        // If too many interpolators, use 3.5 (loosing compatibility)
		#pragma target 3.5

        sampler2D _WaterTex;
        sampler2D _WaterMaskTex;
        sampler2D _HeightTex;
        sampler2D _MountainTex;
        sampler2D _GrassTex;

        struct Input
        {
			float2 uv_WaterTex;
			float2 uv_WaterMaskTex;
			float2 uv_HeightTex;
			float2 uv_MountainTex;
			float2 uv_GrassTex;
        };
		
        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Albedo comes from a texture tinted by color
            fixed4 WaterColor = tex2D (_WaterTex, IN.uv_WaterTex);
            fixed4 WaterMaskColor = tex2D (_WaterMaskTex, IN.uv_WaterMaskTex);
            fixed4 HeightTexColor = tex2D (_HeightTex, IN.uv_HeightTex);
			fixed4 MountainTexColor = tex2D(_MountainTex, IN.uv_MountainTex);
			fixed4 GrassTexColor = tex2D(_GrassTex, IN.uv_GrassTex);

			if (WaterMaskColor.a == 1) {
				o.Albedo = WaterColor.rgb;
			} else {
				o.Albedo = (GrassTexColor.rgb * HeightTexColor.rgb);
			}
        }
        ENDCG
    }
    FallBack "Diffuse"
}
