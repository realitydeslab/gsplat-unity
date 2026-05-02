// Copyright (c) 2026 Reality Design Lab (https://reality.design)
// SPDX-License-Identifier: MIT
//
// Unity ScriptedImporter that turns a .spz file into a GsplatAsset.
// The transform is: SPZ binary → SpzGaussianCloud (RDF) → temporary 3DGS-PLY
// on disk → reuse the existing GsplatAsset.LoadFromPly path.
//
// Reusing the PLY path keeps this importer additive and does not duplicate
// the field-parsing logic in GsplatAsset/GsplatAssetSpark/GsplatAssetUncompressed.

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Gsplat.Editor
{
    [ScriptedImporter(1, "spz")]
    public class SpzImporter : ScriptedImporter
    {
        public CompressionMode Compression = CompressionMode.Spark;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string spzPath = ctx.assetPath;
            string tempPly = Path.Combine(Path.GetTempPath(),
                $"spz2ply_{Guid.NewGuid():N}.ply");

            try
            {
                EditorUtility.DisplayProgressBar("Importing SPZ",
                    "Decoding " + Path.GetFileName(spzPath), 0f);

                SpzGaussianCloud cloud = SpzReader.Load(
                    spzPath,
                    p => EditorUtility.DisplayProgressBar(
                        "Importing SPZ", "Decoding splat data", p * 0.3f));

                EditorUtility.DisplayProgressBar("Importing SPZ", "Writing temp PLY", 0.35f);
                WritePly(cloud, tempPly);

                EditorUtility.DisplayProgressBar("Importing SPZ", "Building GsplatAsset", 0.5f);
                GsplatAsset asset = Compression switch
                {
                    CompressionMode.Uncompressed => ScriptableObject.CreateInstance<GsplatAssetUncompressed>(),
                    CompressionMode.Spark        => ScriptableObject.CreateInstance<GsplatAssetSpark>(),
                    _ => throw new ArgumentOutOfRangeException(nameof(Compression)),
                };

                asset.LoadFromPly(tempPly,
                    (info, p) => EditorUtility.DisplayProgressBar(
                        "Importing SPZ", info, 0.5f + p * 0.5f));

                ctx.AddObjectToAsset("gsplatAsset", asset);
                ctx.SetMainObject(asset);

                Debug.Log($"[Gsplat] Imported {Path.GetFileName(spzPath)} as {Compression} " +
                          $"({cloud.NumPoints:N0} splats, SH degree {cloud.ShDegree})");
            }
            catch (Exception e)
            {
                if (GsplatSettings.Instance != null && GsplatSettings.Instance.ShowImportErrors)
                    Debug.LogError($"{ctx.assetPath} import error: {e}");
                else
                    Debug.LogError($"{ctx.assetPath} import error: {e.Message}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (File.Exists(tempPly))
                {
                    try { File.Delete(tempPly); } catch { /* best effort */ }
                }
            }
        }

        // Writes a standard 3DGS PLY in RDF coordinates so GsplatAsset.LoadFromPly
        // can consume it unchanged. Layout matches Niantic's saveSplatToPly.
        private static void WritePly(SpzGaussianCloud c, string path)
        {
            int n = c.NumPoints;
            int shDim = c.ShDimPerChannel;
            int floatsPerVertex = 17 + shDim * 3;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

            var sb = new StringBuilder(1024);
            sb.Append("ply\n");
            sb.Append("format binary_little_endian 1.0\n");
            sb.Append("element vertex ").Append(n).Append('\n');
            sb.Append("property float x\nproperty float y\nproperty float z\n");
            sb.Append("property float nx\nproperty float ny\nproperty float nz\n");
            sb.Append("property float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\n");
            for (int i = 0; i < shDim * 3; i++)
                sb.Append("property float f_rest_").Append(i).Append('\n');
            sb.Append("property float opacity\n");
            sb.Append("property float scale_0\nproperty float scale_1\nproperty float scale_2\n");
            sb.Append("property float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\n");
            sb.Append("end_header\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            fs.Write(headerBytes, 0, headerBytes.Length);

            float[] row = new float[floatsPerVertex];
            byte[] rowBytes = new byte[floatsPerVertex * 4];

            for (int i = 0; i < n; i++)
            {
                int idx = 0;
                int p3 = i * 3;
                int q4 = i * 4;

                row[idx++] = c.Positions[p3 + 0];
                row[idx++] = c.Positions[p3 + 1];
                row[idx++] = c.Positions[p3 + 2];
                row[idx++] = 0f; row[idx++] = 0f; row[idx++] = 0f;
                row[idx++] = c.Colors[p3 + 0];
                row[idx++] = c.Colors[p3 + 1];
                row[idx++] = c.Colors[p3 + 2];

                // 3DGS-PLY uses channel-then-coefficient (all R coeffs, then G, then B).
                // SPZ stored coefficient-then-channel, so de-interleave here.
                if (shDim > 0)
                {
                    int srcBase = i * shDim * 3;
                    for (int j = 0; j < shDim; j++) row[idx++] = c.Sh[srcBase + j * 3 + 0];
                    for (int j = 0; j < shDim; j++) row[idx++] = c.Sh[srcBase + j * 3 + 1];
                    for (int j = 0; j < shDim; j++) row[idx++] = c.Sh[srcBase + j * 3 + 2];
                }

                row[idx++] = c.Alphas[i];
                row[idx++] = c.Scales[p3 + 0];
                row[idx++] = c.Scales[p3 + 1];
                row[idx++] = c.Scales[p3 + 2];

                // rot_0 = w (real); rot_1..3 = x, y, z (imag)
                row[idx++] = c.Rotations[q4 + 3];
                row[idx++] = c.Rotations[q4 + 0];
                row[idx++] = c.Rotations[q4 + 1];
                row[idx++] = c.Rotations[q4 + 2];

                Buffer.BlockCopy(row, 0, rowBytes, 0, rowBytes.Length);
                fs.Write(rowBytes, 0, rowBytes.Length);
            }
        }
    }

    /// <summary>
    /// When .spz files are reimported, refresh any GsplatRenderers that hold
    /// stale references — mirrors GsplatReferenceRestorer for .ply.
    /// </summary>
    public class SpzReferenceRestorer : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
                                           string[] movedAssets, string[] movedFromAssetPaths)
        {
            bool spzReimported = importedAssets.Any(p => p.EndsWith(".spz", StringComparison.OrdinalIgnoreCase));
            if (!spzReimported) return;

            var renderers = UnityEngine.Object.FindObjectsByType<GsplatRenderer>(FindObjectsSortMode.None);
            foreach (var renderer in renderers)
            {
                if (renderer.GsplatAsset || string.IsNullOrEmpty(renderer.AssetGuid)) continue;
                var path = AssetDatabase.GUIDToAssetPath(renderer.AssetGuid);
                if (string.IsNullOrEmpty(path)) continue;
                var asset = AssetDatabase.LoadAssetAtPath<GsplatAsset>(path);
                if (!asset) continue;
                renderer.GsplatAsset = asset;
                renderer.ReloadAsset();
                EditorUtility.SetDirty(renderer);
            }
        }
    }
}
