// Generated from Structured IR; semantic lowering source is the shared GLSL backend.

struct SlangInput
{
    float4 in_attr0 : TEXCOORD0;
    float4 position : SV_Position;
    bool frontFacing : SV_IsFrontFace;
};

struct SlangOutput
{
    float4 out_attr0 : SV_Target0;
};


static const int undef = 0;

cbuffer support_buffer : register(b0)
{
uint alpha_test;
    uint is_bgra[8];
    precise float4 viewport_inverse;
    precise float4 viewport_size;
    int frag_scale_count;
    precise float render_scale[73];
    int4 tfe_offset;
    int tfe_vertex_count;
};






SlangOutput main(SlangInput input)
{
    SlangOutput output = (SlangOutput)0;
    #define in_attr0 input.in_attr0
    #define out_attr0 output.out_attr0
    #define gl_FragCoord input.position
    #define gl_FragDepth output.depth
    #define gl_FrontFacing input.frontFacing

    precise float temp_0;
    precise float temp_1;
    precise float temp_2;
    precise float temp_3;
    // 0x000008: 0xE003FF87CFF7FF04 Ipa
    // 0x000010: 0x5080000000470404 Mufu
    // 0x000018: 0xE043FF880047FF00 Ipa
    temp_0 = in_attr0.x;
    // 0x000028: 0xE043FF884047FF01 Ipa
    temp_1 = in_attr0.y;
    // 0x000030: 0xE043FF888047FF02 Ipa
    temp_2 = in_attr0.z;
    // 0x000038: 0xE043FF88C047FF03 Ipa
    temp_3 = in_attr0.w;
    // 0x000048: 0xE30000000007000F Exit
    out_attr0.x = temp_0;
    out_attr0.y = temp_1;
    out_attr0.z = temp_2;
    out_attr0.w = temp_3;
    return output;
}

