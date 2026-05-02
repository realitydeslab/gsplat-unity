// Copyright (c) 2026 Reality Design Lab (https://reality.design)
// SPDX-License-Identifier: MIT
//
// Pure-C# reader for Niantic's .spz Gaussian-splat format.
//
// Supports the legacy gzip-wrapped headers v1, v2, and v3 — the variants
// shipped by every public SPZ writer prior to Q1 2026. The v4 NGSP/zstd
// format is not yet supported (it would require a managed zstd dependency).
//
// References:
//   - github.com/nianticlabs/spz/blob/main/src/cc/load-spz.cc
//   - github.com/nianticlabs/spz/blob/main/src/cc/splat-types.h
//
// This file uses only System.* types so it can compile both inside the Unity
// Editor and in a standalone .NET console harness for headless tests
// (see Tools~/SpzImporterTests/).

using System;
using System.IO;
using System.IO.Compression;

namespace Gsplat.Editor
{
    /// <summary>Decoded SPZ contents in standard 3DGS-PLY conventions (RDF coordinates).</summary>
    public sealed class SpzGaussianCloud
    {
        public int NumPoints;
        public int ShDegree;        // 0..3 (4 is in spec, but unused in practice)
        public int ShDimPerChannel; // 0, 3, 8, 15, 24
        public bool Antialiased;

        public float[] Positions = Array.Empty<float>(); // N * 3, xyz
        public float[] Scales    = Array.Empty<float>(); // N * 3, log-scale
        public float[] Rotations = Array.Empty<float>(); // N * 4, xyzw
        public float[] Alphas    = Array.Empty<float>(); // N, pre-sigmoid logit
        public float[] Colors    = Array.Empty<float>(); // N * 3, SH DC (wide RGB)
        public float[] Sh        = Array.Empty<float>(); // N * ShDimPerChannel * 3
    }

    public static class SpzReader
    {
        // ---- Constants mirrored from spz/src/cc/load-spz.h ----
        private const uint NgspMagic = 0x5053474eu;        // 'NGSP' little-endian
        private const int  MaxShDegree = 4;
        private const int  MinSmallestThreeQuaternionsVersion = 3;

        // SH DC scale used by SPZ when round-tripping color through 8-bit storage.
        private const float ColorScale = 0.15f;

        // RUB→RDF flips (see spz/src/cc/splat-types.h coordinateConverter).
        // For SPZ→PLY the inputs are always {x=+1, y=-1, z=-1}, so we hard-code
        // the table for the SH coefficients.
        private static readonly float[] FlipShRubToRdf =
        {
            -1f, -1f,  1f, -1f,  1f,
             1f, -1f,  1f, -1f,  1f,
            -1f, -1f,  1f, -1f,  1f,
            -1f,  1f, -1f,  1f,  1f,
            -1f,  1f, -1f, -1f,
        };

        /// <summary>Loads and decodes a .spz file. Output is in 3DGS-PLY (RDF) conventions.</summary>
        public static SpzGaussianCloud Load(string path, Action<float> onProgress = null)
        {
            byte[] fileBytes = File.ReadAllBytes(path);
            return Load(fileBytes, onProgress);
        }

        public static SpzGaussianCloud Load(byte[] fileBytes, Action<float> onProgress = null)
        {
            if (fileBytes.Length < 2)
                throw new InvalidDataException("spz file too short.");

            byte[] decoded;
            if (fileBytes[0] == 0x1f && fileBytes[1] == 0x8b)
            {
                decoded = DecompressGzip(fileBytes);
            }
            else if (BitConverter.ToUInt32(fileBytes, 0) == NgspMagic)
            {
                throw new NotSupportedException(
                    "SPZ NGSP/v4 (zstd) format is not yet supported by this C# reader. " +
                    "Re-save with --version 3 from spz_cli or any pre-v4 SPZ writer.");
            }
            else
            {
                throw new InvalidDataException("spz file does not start with a recognized magic.");
            }

            return Decode(decoded, onProgress);
        }

        // ---------------------------------------------------------------------

        private static byte[] DecompressGzip(byte[] gz)
        {
            using var input  = new MemoryStream(gz);
            using var gzip   = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(gz.Length * 4);
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private static SpzGaussianCloud Decode(byte[] data, Action<float> onProgress)
        {
            // 16-byte LegacyPackedGaussiansHeader.
            if (data.Length < 16) throw new InvalidDataException("spz payload too short for header.");
            uint magic          = BitConverter.ToUInt32(data, 0);
            uint version        = BitConverter.ToUInt32(data, 4);
            uint numPoints      = BitConverter.ToUInt32(data, 8);
            byte shDegree       = data[12];
            byte fractionalBits = data[13];
            byte flags          = data[14];
            // data[15] reserved
            const int header = 16;

            if (magic != NgspMagic)
                throw new InvalidDataException($"spz: bad magic 0x{magic:x8}, expected 0x{NgspMagic:x8}.");
            if (version < 1 || version > 3)
                throw new NotSupportedException(
                    $"spz: legacy reader supports versions 1..3 (saw {version}). v4+ requires zstd.");
            if (numPoints == 0)
                throw new InvalidDataException("spz: numPoints == 0.");
            // 10M cap mirrors the upstream C++ reader's hard limit; guards against bogus headers
            // that would otherwise allocate gigabytes before failing.
            if (numPoints > 10_000_000u)
                throw new InvalidDataException($"spz: numPoints {numPoints} exceeds 10M cap.");
            if (shDegree > MaxShDegree)
                throw new InvalidDataException($"spz: SH degree {shDegree} exceeds max {MaxShDegree}.");
            if (fractionalBits > 24)
                throw new InvalidDataException($"spz: fractionalBits {fractionalBits} out of range (max 24).");

            int n = (int)numPoints;
            int shDim = DimForDegree(shDegree);
            bool usesFloat16 = version == 1;
            bool usesQuatSmallestThree = version >= MinSmallestThreeQuaternionsVersion;
            const byte FlagAntialiased = 0x1;

            int positionStride = usesFloat16 ? 6 : 9;
            int rotationStride = usesQuatSmallestThree ? 4 : 3;

            int posBytes   = n * positionStride;
            int alphaBytes = n;
            int colorBytes = n * 3;
            int scaleBytes = n * 3;
            int rotBytes   = n * rotationStride;
            int shBytes    = n * shDim * 3;

            int totalNeeded = header + posBytes + alphaBytes + colorBytes + scaleBytes + rotBytes + shBytes;
            if (data.Length < totalNeeded)
                throw new InvalidDataException(
                    $"spz: payload too short. Need {totalNeeded} bytes, have {data.Length}.");

            int o = header;
            ReadOnlySpan<byte> positions = new ReadOnlySpan<byte>(data, o, posBytes);   o += posBytes;
            ReadOnlySpan<byte> alphas    = new ReadOnlySpan<byte>(data, o, alphaBytes); o += alphaBytes;
            ReadOnlySpan<byte> colors    = new ReadOnlySpan<byte>(data, o, colorBytes); o += colorBytes;
            ReadOnlySpan<byte> scales    = new ReadOnlySpan<byte>(data, o, scaleBytes); o += scaleBytes;
            ReadOnlySpan<byte> rotations = new ReadOnlySpan<byte>(data, o, rotBytes);   o += rotBytes;
            ReadOnlySpan<byte> sh        = new ReadOnlySpan<byte>(data, o, shBytes);

            var cloud = new SpzGaussianCloud
            {
                NumPoints = n,
                ShDegree = shDegree,
                ShDimPerChannel = shDim,
                Antialiased = (flags & FlagAntialiased) != 0,
                Positions = new float[n * 3],
                Scales    = new float[n * 3],
                Rotations = new float[n * 4],
                Alphas    = new float[n],
                Colors    = new float[n * 3],
                Sh        = shDim > 0 ? new float[n * shDim * 3] : Array.Empty<float>(),
            };

            // RUB→RDF flips
            const float flipPx =  1f, flipPy = -1f, flipPz = -1f;
            const float flipQx =  1f, flipQy = -1f, flipQz = -1f;

            // ---- Positions ----
            float scale = 1f / (1 << fractionalBits);
            int progressEvery = Math.Max(1, n / 100);

            for (int i = 0; i < n; i++)
            {
                int p3 = i * 3;
                if (usesFloat16)
                {
                    int b = i * 6;
                    cloud.Positions[p3 + 0] = flipPx * HalfToFloat(BitConverter.ToUInt16(positions.Slice(b + 0, 2)));
                    cloud.Positions[p3 + 1] = flipPy * HalfToFloat(BitConverter.ToUInt16(positions.Slice(b + 2, 2)));
                    cloud.Positions[p3 + 2] = flipPz * HalfToFloat(BitConverter.ToUInt16(positions.Slice(b + 4, 2)));
                }
                else
                {
                    int b = i * 9;
                    cloud.Positions[p3 + 0] = flipPx * Decode24FixedPoint(positions, b + 0) * scale;
                    cloud.Positions[p3 + 1] = flipPy * Decode24FixedPoint(positions, b + 3) * scale;
                    cloud.Positions[p3 + 2] = flipPz * Decode24FixedPoint(positions, b + 6) * scale;
                }

                if ((i % progressEvery) == 0) onProgress?.Invoke(i / (float)n * 0.5f);
            }

            // ---- Scales (log-scale, no axis flip) ----
            for (int i = 0; i < n * 3; i++)
                cloud.Scales[i] = scales[i] / 16f - 10f;

            // ---- Rotations ----
            if (usesQuatSmallestThree)
            {
                for (int i = 0; i < n; i++)
                    UnpackQuatSmallestThree(rotations.Slice(i * 4, 4), cloud.Rotations, i * 4, flipQx, flipQy, flipQz);
            }
            else
            {
                for (int i = 0; i < n; i++)
                    UnpackQuatFirstThree(rotations.Slice(i * 3, 3), cloud.Rotations, i * 4, flipQx, flipQy, flipQz);
            }

            // ---- Alphas (pre-sigmoid) ----
            for (int i = 0; i < n; i++)
                cloud.Alphas[i] = InvSigmoid(alphas[i] / 255f);

            // ---- Colors (SH DC, wide-RGB) ----
            for (int i = 0; i < n * 3; i++)
                cloud.Colors[i] = ((colors[i] / 255f) - 0.5f) / ColorScale;

            // ---- Spherical harmonics ----
            if (shDim > 0)
            {
                int perPoint = shDim * 3;
                for (int i = 0; i < n; i++)
                {
                    int srcBase = i * perPoint;
                    int dstBase = i * perPoint;
                    for (int j = 0; j < shDim; j++)
                    {
                        float flip = FlipShRubToRdf[j];
                        int s = srcBase + j * 3;
                        int d = dstBase + j * 3;
                        cloud.Sh[d + 0] = flip * UnquantizeSh(sh[s + 0]);
                        cloud.Sh[d + 1] = flip * UnquantizeSh(sh[s + 1]);
                        cloud.Sh[d + 2] = flip * UnquantizeSh(sh[s + 2]);
                    }

                    if ((i % progressEvery) == 0) onProgress?.Invoke(0.5f + i / (float)n * 0.5f);
                }
            }

            onProgress?.Invoke(1f);
            return cloud;
        }

        private static int DimForDegree(int degree) => degree switch
        {
            0 => 0, 1 => 3, 2 => 8, 3 => 15, 4 => 24,
            _ => throw new InvalidDataException($"unsupported SH degree {degree}"),
        };

        private static int Decode24FixedPoint(ReadOnlySpan<byte> src, int offset)
        {
            // Little-endian 24-bit signed → int32 with sign extension.
            int v = src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xff000000);
            return v;
        }

        private static void UnpackQuatFirstThree(
            ReadOnlySpan<byte> src, float[] dst, int dstOffset,
            float flipX, float flipY, float flipZ)
        {
            float x = (src[0] / 127.5f - 1f) * flipX;
            float y = (src[1] / 127.5f - 1f) * flipY;
            float z = (src[2] / 127.5f - 1f) * flipZ;
            float wSq = 1f - (x * x + y * y + z * z);
            float w = wSq > 0f ? MathF.Sqrt(wSq) : 0f;
            dst[dstOffset + 0] = x;
            dst[dstOffset + 1] = y;
            dst[dstOffset + 2] = z;
            dst[dstOffset + 3] = w;
        }

        private static void UnpackQuatSmallestThree(
            ReadOnlySpan<byte> src, float[] dst, int dstOffset,
            float flipX, float flipY, float flipZ)
        {
            // 32-bit packed: top 2 bits = index of largest component,
            // then 3 × (1-bit sign + 9-bit magnitude) for the other three.
            uint comp = (uint)src[0] | ((uint)src[1] << 8) | ((uint)src[2] << 16) | ((uint)src[3] << 24);
            const uint mask9 = (1u << 9) - 1u;
            const float invSqrt2 = 0.70710678118654752440f;

            int iLargest = (int)(comp >> 30);
            float sumSq = 0f;
            for (int i = 3; i >= 0; i--)
            {
                if (i == iLargest) continue;
                uint mag    = comp & mask9;
                uint negbit = (comp >> 9) & 0x1u;
                comp >>= 10;
                float v = invSqrt2 * mag / mask9;
                if (negbit == 1) v = -v;
                dst[dstOffset + i] = v;
                sumSq += v * v;
            }
            dst[dstOffset + iLargest] = MathF.Sqrt(MathF.Max(0f, 1f - sumSq));

            dst[dstOffset + 0] *= flipX;
            dst[dstOffset + 1] *= flipY;
            dst[dstOffset + 2] *= flipZ;
        }

        private static float UnquantizeSh(byte b) => (b - 128f) / 128f;

        private static float InvSigmoid(float p)
        {
            // alpha bytes 0 and 255 map to ±∞ pre-sigmoid; clamp to a finite range
            // so downstream consumers (PLY writers, GsplatAsset) don't see NaN/Inf.
            const float eps = 1e-6f;
            p = MathF.Max(eps, MathF.Min(1f - eps, p));
            return MathF.Log(p / (1f - p));
        }

        private static float HalfToFloat(ushort h)
        {
            // IEEE 754 half-precision → single-precision. Used only for v1 SPZ files.
            uint sign = (uint)((h >> 15) & 0x1) << 31;
            uint exp  = (uint)((h >> 10) & 0x1f);
            uint mant = (uint)(h & 0x3ff);

            uint f;
            if (exp == 0)
            {
                if (mant == 0) { f = sign; }
                else
                {
                    while ((mant & 0x400) == 0) { mant <<= 1; exp = unchecked(exp - 1); }
                    exp += 1;
                    mant &= 0x3ff;
                    f = sign | ((exp + 112) << 23) | (mant << 13);
                }
            }
            else if (exp == 31) { f = sign | 0x7f800000u | (mant << 13); }
            else                { f = sign | ((exp + 112) << 23) | (mant << 13); }

            return BitConverter.Int32BitsToSingle(unchecked((int)f));
        }
    }
}
