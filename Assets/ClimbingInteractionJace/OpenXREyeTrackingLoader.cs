using System;
using System.IO;
using UnityEditor;

#if UNITY_EDITOR
using UnityEditor.XR.OpenXR.Features;
#endif

namespace UnityEngine.XR.OpenXR.Features.OpenXREyeTrackers
{
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "OpenXR-Eye-Trackers API layer",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.WSA },
        Company = "Unity",
        Desc = "Enables the OpenXR-Eye-Trackers API layer.",
        DocumentationLink = "",
        FeatureId = "com.unity.openxr.features.openxr_eye_trackers",
        OpenxrExtensionStrings = "",
        Version = "1")]
#endif
    public class OpenXREyeTrackersFeature : OpenXRFeature
    {
        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            Environment.SetEnvironmentVariable("XR_ENABLE_API_LAYERS", "XR_APILAYER_MBUCCHIA_eye_trackers");
            Environment.SetEnvironmentVariable("XR_API_LAYER_PATH", Path.Combine(Application.dataPath, "API-Layers"));
            return base.HookGetInstanceProcAddr(func);
        }
    }
}