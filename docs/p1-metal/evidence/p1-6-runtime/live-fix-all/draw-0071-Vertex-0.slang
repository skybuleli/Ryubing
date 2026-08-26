// Generated from Structured IR; semantic lowering source is the shared GLSL backend.

struct SlangInput
{
    float4 in_attr0 : TEXCOORD0;
    float4 in_attr1 : TEXCOORD1;
    float4 in_attr2 : TEXCOORD2;
    uint vertexId : SV_VertexID;
    uint instanceId : SV_InstanceID;
};

struct SlangOutput
{
    float4 position : SV_Position;
    float pointSize : PSIZE;
    float4 out_attr0 : TEXCOORD0;
    float4 out_attr1 : TEXCOORD1;
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

cbuffer vp_c3 : register(b1)
{
precise float4 data[4096];
};









SlangOutput main(SlangInput input)
{
    SlangOutput output = (SlangOutput)0;
    #define in_attr0 input.in_attr0
    #define in_attr1 input.in_attr1
    #define in_attr2 input.in_attr2
    #define out_attr0 output.out_attr0
    #define out_attr1 output.out_attr1
    #define gl_Position output.position
    #define gl_PointSize output.pointSize
    #define gl_VertexID input.vertexId
    #define gl_VertexIndex input.vertexId
    #define gl_InstanceID input.instanceId
    #define gl_InstanceIndex input.instanceId

    precise float temp_0;
    precise float temp_1;
    precise float temp_2;
    precise float temp_3;
    precise float temp_4;
    precise float temp_5;
    precise float temp_6;
    precise float temp_7;
    precise float temp_8;
    precise float temp_9;
    precise float temp_10;
    precise float temp_11;
    precise float temp_12;
    precise float temp_13;
    precise float temp_14;
    precise float temp_15;
    precise float temp_16;
    precise float temp_17;
    precise float temp_18;
    precise float temp_19;
    precise float temp_20;
    precise float temp_21;
    precise float temp_22;
    precise float temp_23;
    precise float temp_24;
    gl_PointSize = 1.0;
    gl_Position.x = 0.0;
    gl_Position.y = 0.0;
    gl_Position.z = 0.0;
    gl_Position.w = 1.0;
    out_attr0.x = 0.0;
    out_attr0.y = 0.0;
    out_attr0.z = 0.0;
    out_attr0.w = 1.0;
    out_attr1.x = 0.0;
    out_attr1.y = 0.0;
    // 0x000008: 0xEFD87F800807FF03 Ald
    temp_0 = in_attr0.x;
    // 0x000010: 0xEFD87F800847FF04 Ald
    temp_1 = in_attr0.y;
    // 0x000018: 0xEFD87F800887FF05 Ald
    temp_2 = in_attr0.z;
    // 0x000028: 0xEFD87F800907FF06 Ald
    temp_3 = in_attr1.x;
    // 0x000030: 0xEFD87F800947FF07 Ald
    temp_4 = in_attr1.y;
    // 0x000038: 0xEFD87F800987FF08 Ald
    temp_5 = in_attr1.z;
    // 0x000048: 0xEFD87F8009C7FF09 Ald
    temp_6 = in_attr1.w;
    // 0x000050: 0x4C68100C00C70300 Fmul
    temp_7 = temp_0 * data[3].x;
    // 0x000058: 0xEFD87F800A07FF0A Ald
    temp_8 = in_attr2.x;
    // 0x000068: 0x4C68100C00870301 Fmul
    temp_9 = temp_0 * data[2].x;
    // 0x000070: 0xEFD87F800A47FF0B Ald
    temp_10 = in_attr2.y;
    // 0x000078: 0x4C68100C00470302 Fmul
    temp_11 = temp_0 * data[1].x;
    // 0x000088: 0xEFF07F800807FF06 Ast
    out_attr0.x = temp_3;
    // 0x000090: 0x4C68100C00070303 Fmul
    temp_12 = temp_0 * data[0].x;
    // 0x000098: 0xEFF07F800847FF07 Ast
    out_attr0.y = temp_4;
    // 0x0000A8: 0x49A0000C00D70400 Ffma
    temp_13 = fma(temp_1, data[3].y, temp_7);
    // 0x0000B0: 0xEFF07F800887FF08 Ast
    out_attr0.z = temp_5;
    // 0x0000B8: 0x49A0008C00970401 Ffma
    temp_14 = fma(temp_1, data[2].y, temp_9);
    // 0x0000C8: 0xEFF07F8008C7FF09 Ast
    out_attr0.w = temp_6;
    // 0x0000D0: 0x49A0010C00570402 Ffma
    temp_15 = fma(temp_1, data[1].y, temp_11);
    // 0x0000D8: 0xEFF07F800907FF0A Ast
    out_attr1.x = temp_8;
    // 0x0000E8: 0x49A0018C00170403 Ffma
    temp_16 = fma(temp_1, data[0].y, temp_12);
    // 0x0000F0: 0xEFF07F800947FF0B Ast
    out_attr1.y = temp_10;
    // 0x0000F8: 0x49A0000C00E70500 Ffma
    temp_17 = fma(temp_2, data[3].z, temp_13);
    // 0x000108: 0x49A0008C00A70501 Ffma
    temp_18 = fma(temp_2, data[2].z, temp_14);
    // 0x000110: 0x49A0010C00670502 Ffma
    temp_19 = fma(temp_2, data[1].z, temp_15);
    // 0x000118: 0x49A0018C00270503 Ffma
    temp_20 = fma(temp_2, data[0].z, temp_16);
    // 0x000128: 0x4C58100C00F70000 Fadd
    temp_21 = temp_17 + data[3].w;
    // 0x000130: 0x4C58100C00B70101 Fadd
    temp_22 = temp_18 + data[2].w;
    // 0x000138: 0xEFF07F8007C7FF00 Ast
    gl_Position.w = temp_21;
    // 0x000148: 0x4C58100C00770202 Fadd
    temp_23 = temp_19 + data[1].w;
    // 0x000150: 0xEFF07F800787FF01 Ast
    gl_Position.z = temp_22;
    // 0x000158: 0x4C58100C00370303 Fadd
    temp_24 = temp_20 + data[0].w;
    // 0x000168: 0xEFF07F800747FF02 Ast
    gl_Position.y = temp_23;
    // 0x000170: 0xEFF07F800707FF03 Ast
    gl_Position.x = temp_24;
    // 0x000178: 0xE30000000007000F Exit
    return output;
}

