Shader "Hidden/Overdraw"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        float _OverdrawBitValue;
        float _OverdrawDisplayMax;
        float4 _BlitScaleBias;
        TEXTURE2D_X(_BlitTexture);
        SAMPLER(sampler_LinearClamp);

        struct Attributes
        {
            uint vertexID : SV_VertexID;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings FullscreenVert(Attributes input)
        {
            Varyings output;
            output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
            output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
            return output;
        }

        half4 CountFrag(Varyings input) : SV_Target
        {
            return half4(_OverdrawBitValue / 255.0, 0.0, 0.0, 0.0);
        }

        half4 DisplayFrag(Varyings input) : SV_Target
        {
            float2 uv = input.uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
            half count = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).r * 255.0;
            half gray = saturate(count / max(_OverdrawDisplayMax, 1.0));
            return half4(gray, gray, gray, 1.0);
        }
        ENDHLSL

        Pass
        {
            Name "StencilBit0"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 1 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "StencilBit1"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 2 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "StencilBit2"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 4 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "StencilBit3"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 8 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "StencilBit4"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 16 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "StencilBit5"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 32 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "StencilBit6"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 64 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "StencilBit7"
            ZTest Always ZWrite Off Cull Off ColorMask R Blend One One
            Stencil { Ref 0 ReadMask 128 WriteMask 0 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment CountFrag
            ENDHLSL
        }

        Pass
        {
            Name "Display"
            ZTest Always ZWrite Off Cull Off Blend One Zero
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment DisplayFrag
            ENDHLSL
        }
    }
}
