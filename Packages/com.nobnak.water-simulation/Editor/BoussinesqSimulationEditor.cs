using UnityEditor;
using UnityEngine;

namespace Nobnak.WaterSimulation.Editor {
    [CustomEditor(typeof(BoussinesqSimulation))]
    public class BoussinesqSimulationEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            var sim = (BoussinesqSimulation)target;

            if (sim.IsDepthTooDeepForShallowWater) {
                float minDomain = Mathf.Min(sim.AreaSize.x, sim.AreaSize.y);
                float maxRecommendedDepth = minDomain * 0.05f;
                EditorGUILayout.HelpBox(
                    $"[Shallow Water Ratio Warning]\n" +
                    $"Deep water depth ({sim.DeepWaterDepth:F1} m) exceeds 5% of domain size ({minDomain:F0} m, ratio {sim.DepthToDomainRatio * 100f:F1}%).\n" +
                    $"Peregrine Boussinesq equations require shallow water (h / L <= 0.03 - 0.05, max ~{maxRecommendedDepth:F1} m) " +
                    "to prevent frequency saturation and wave trapping.",
                    MessageType.Warning);
            }

            if (sim.IsFrequencyAboveCutoff) {
                EditorGUILayout.HelpBox(
                    $"[Peregrine Cutoff Frequency Warning]\n" +
                    $"Wavemaker frequency ({sim.WavemakerFrequency:F2} Hz) is near or exceeds Peregrine cutoff frequency " +
                    $"({sim.CutoffFrequency:F2} Hz for depth {sim.DeepWaterDepth:F1} m).\n" +
                    "Frequencies above cutoff become evanescent (k^2 < 0) and cannot propagate into the pool, causing local oscillations.",
                    MessageType.Warning);
            }

            base.OnInspectorGUI();
        }
    }
}
