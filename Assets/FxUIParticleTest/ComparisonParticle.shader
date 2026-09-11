Shader "FxTest/ComparisonParticle" {
// UIParticle reads Material.mainTexture while validating renderer bindings.
// The radial particle shape is procedural; keep a white compatibility texture.
Properties { _MainTex ("Texture", 2D) = "white" {} }
SubShader { Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" } Pass {
Blend One One
ZWrite Off
Cull Off
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "UnityCG.cginc"
struct appdata { float4 vertex:POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; };
struct v2f { float4 pos:SV_POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; };
v2f vert(appdata v) { v2f o;o.pos=UnityObjectToClipPos(v.vertex);o.color=v.color;o.uv=v.uv;return o; }
fixed4 frag(v2f i):SV_Target { float a=saturate(1-length(i.uv*2-1));return fixed4(i.color.rgb*i.color.a*a,a*i.color.a); }
ENDCG
} } }
