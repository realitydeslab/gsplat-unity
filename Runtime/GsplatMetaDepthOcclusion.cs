// Copyright (c) 2026 Reality Design Lab (https://reality.design)
// SPDX-License-Identifier: MIT
//
// Drives the Meta Depth API environment-occlusion path inside the gsplat
// fragment shader. Conditionally compiled — only present when both URP
// and the Meta Depth API URP package are in the project's manifest. The
// shader-side multi_compile keyword (HARD_OCCLUSION / SOFT_OCCLUSION) is
// declared in Runtime/Shaders/Gsplat.shader and toggled here.
//
// This is a scene-level controller, not a per-renderer one: enabling it
// turns environment occlusion on for every gsplat in the project's
// shared Gsplat material set. The crystal-ball / wave-reveal style
// per-region masking can be layered on top via the
// _EnvironmentDepthBias global plus shader-graph or further forks.
//
// Companion to:
//   github.com/oculus-samples/Unity-DepthAPI
//   Packages/com.meta.xr.depthapi.urp
//   Packages/com.meta.xr.sdk.core (provides the actual HLSL include)

#if GSPLAT_ENABLE_URP && GSPLAT_ENABLE_META_DEPTH

using UnityEngine;

namespace Gsplat
{
    public enum GsplatOcclusionMode
    {
        Off  = 0,
        Hard = 1,  // crisp depth-test, cheaper
        Soft = 2,  // softened edge, ~1.4× GPU cost vs Hard (Meta Depth API perf docs)
    }

    /// <summary>Toggles HARD_OCCLUSION / SOFT_OCCLUSION on the gsplat shader at scene scope.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Gsplat/Gsplat Meta Depth Occlusion")]
    public class GsplatMetaDepthOcclusion : MonoBehaviour
    {
        const string k_HardKeyword = "HARD_OCCLUSION";
        const string k_SoftKeyword = "SOFT_OCCLUSION";
        static readonly int k_BiasId = Shader.PropertyToID("_EnvironmentDepthBias");

        [SerializeField]
        GsplatOcclusionMode _mode = GsplatOcclusionMode.Hard;

        // Meta recommends ~0.06 to fight z-fighting along real surfaces (see
        // CLAUDE.md / Meta Depth API gotchas). Range chosen to match the
        // upstream OcclusionCutoutURP sample's editor exposure.
        [SerializeField, Range(0f, 0.5f)]
        float _environmentDepthBias = 0.06f;

        public GsplatOcclusionMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                ApplyMode();
            }
        }

        public float EnvironmentDepthBias
        {
            get => _environmentDepthBias;
            set
            {
                _environmentDepthBias = value;
                Shader.SetGlobalFloat(k_BiasId, value);
            }
        }

        void OnEnable()
        {
            Shader.SetGlobalFloat(k_BiasId, _environmentDepthBias);
            ApplyMode();
        }

        void OnDisable()
        {
            Shader.DisableKeyword(k_HardKeyword);
            Shader.DisableKeyword(k_SoftKeyword);
        }

        void OnValidate()
        {
            if (!isActiveAndEnabled) return;
            Shader.SetGlobalFloat(k_BiasId, _environmentDepthBias);
            ApplyMode();
        }

        void ApplyMode()
        {
            switch (_mode)
            {
                case GsplatOcclusionMode.Off:
                    Shader.DisableKeyword(k_HardKeyword);
                    Shader.DisableKeyword(k_SoftKeyword);
                    break;
                case GsplatOcclusionMode.Hard:
                    Shader.EnableKeyword(k_HardKeyword);
                    Shader.DisableKeyword(k_SoftKeyword);
                    break;
                case GsplatOcclusionMode.Soft:
                    Shader.DisableKeyword(k_HardKeyword);
                    Shader.EnableKeyword(k_SoftKeyword);
                    break;
            }
        }
    }
}

#endif // GSPLAT_ENABLE_URP && GSPLAT_ENABLE_META_DEPTH
