// Copyright (c) 2026 Yize Wu
// SPDX-License-Identifier: MIT

#ifndef GSPLAT_UNCOMPRESSED_INCLUDED
#define GSPLAT_UNCOMPRESSED_INCLUDED

#include "Gsplat.hlsl"
StructuredBuffer<float3> _PositionBuffer;
StructuredBuffer<float3> _ScaleBuffer;
StructuredBuffer<float4> _RotationBuffer;
StructuredBuffer<float4> _ColorBuffer;

bool InitSplatData(SplatSource source, float4x4 modelView, out SplatCenter center, out SplatCorner corner,
                   out float4 color)
{
    float3 modelCenter = _PositionBuffer[source.id];
    if (!InitCenter(modelView, modelCenter, center))
        return false;

    // Read colour (with stored sigmoid'd alpha in .w) BEFORE the expensive covariance work.
    // Splats that would die in the fragment-shader alpha discard anyway never reach the
    // QuatToMat3 / Jacobian projection / eigenvalue dance below.
    color = _ColorBuffer[source.id];
    if (color.w < _MinSplatAlpha)
        return false;
    color.rgb = color.rgb * SH_C0 + 0.5;

    float4 quat = _RotationBuffer[source.id];
    float3 scale = _ScaleBuffer[source.id];
    SplatCovariance cov = CalcCovariance(quat, scale);
    if (!InitCorner(source, cov, center, corner))
        return false;
    return true;
}

#ifndef SH_BANDS_0
StructuredBuffer<float3> _SHBuffer;

void InitSH(uint id, out float3 sh[SH_COEFFS])
{
    for (int i = 0; i < SH_COEFFS; i++)
        sh[i] = _SHBuffer[id * SH_COEFFS + i];
}
#endif

#endif
