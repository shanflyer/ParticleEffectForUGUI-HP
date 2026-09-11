Shader "FxTest/ComparisonComposite" {
Properties { _MainTex ("Texture", 2D) = "black" {} }
SubShader { Tags { "Queue"="Transparent" } Pass {
Blend One One
ZWrite Off
ZTest Always
Cull Off
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "UnityCG.cginc"
sampler2D _MainTex;
struct a { float4 vertex:POSITION;float2 uv:TEXCOORD0; };
struct v { float4 pos:SV_POSITION;float2 uv:TEXCOORD0; };
v vert(a i){v o;o.pos=UnityObjectToClipPos(i.vertex);o.uv=i.uv;return o;}
fixed4 frag(v i):SV_Target{return tex2D(_MainTex,i.uv);}
ENDCG
} } }
