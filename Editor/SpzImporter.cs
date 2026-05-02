// Copyright (c) 2026 Reality Design Lab (https://reality.design)
// SPDX-License-Identifier: MIT
//
// Unity ScriptedImporter for the Niantic / Scaniverse .spz Gaussian-splat
// format. Mirrors the structure of GsplatImporter.cs (the PLY ScriptedImporter):
// constructs the appropriate GsplatAsset subclass for the chosen Compression
// mode and asks it to LoadFromSpz directly. SpzReader (in Runtime/) handles
// the binary format; this file is just the Editor glue.

using System;
using System.Linq;
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
            GsplatAsset asset = Compression switch
            {
                CompressionMode.Uncompressed => ScriptableObject.CreateInstance<GsplatAssetUncompressed>(),
                CompressionMode.Spark        => ScriptableObject.CreateInstance<GsplatAssetSpark>(),
                _ => throw new ArgumentOutOfRangeException(nameof(Compression)),
            };

            try
            {
                asset.LoadFromSpz(ctx.assetPath, (info, p) =>
                    EditorUtility.DisplayProgressBar("Importing SPZ Asset", info, p));
            }
            catch (Exception e)
            {
                if (GsplatSettings.Instance.ShowImportErrors)
                    Debug.LogError($"{ctx.assetPath} import error: {e.Message}");
                EditorUtility.ClearProgressBar();
                return;
            }
            EditorUtility.ClearProgressBar();

            ctx.AddObjectToAsset("gsplatAsset", asset);
            ctx.SetMainObject(asset);
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
