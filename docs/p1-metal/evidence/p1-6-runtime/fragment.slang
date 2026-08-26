// Realistic fragment-stage fixture used by the P1-6 toolchain validation.
cbuffer Constants : register(b0)
{
    float4 tint;
}

Texture2D<float4> tex0 : register(t1);
SamplerState samp0 : register(s1);

struct PSInput
{
    float4 position : SV_Position;
    float2 texCoord : TEXCOORD0;
    float4 color : COLOR0;
};

float4 main(PSInput input) : SV_Target
{
    return tex0.Sample(samp0, input.texCoord) * input.color * tint;
}
