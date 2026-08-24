// Auto-generated for fragment
// Auto-generated Slang/HLSL for Vertex - P1-2
// Common resources placeholder
cbuffer Constants : register(b0) { float4 cb0[64]; }
cbuffer Constants1 : register(b1) { float4 cb1[64]; }
StructuredBuffer<float4> storage0 : register(t0);
RWStructuredBuffer<float4> storageRW0 : register(u0);
Texture2D tex0 : register(t1); Texture2D tex1 : register(t2);
SamplerState samp0 : register(s0); SamplerState samp1 : register(s1);
struct PSInput { float4 position : SV_Position; float4 color : COLOR0; }; float4 main(PSInput IN) : SV_Target { return IN.color; }
[numthreads(32,1,1)] void main(uint3 id: SV_DispatchThreadID) {}
