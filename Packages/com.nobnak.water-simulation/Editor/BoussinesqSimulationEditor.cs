using UnityEditor;
using UnityEngine;

namespace Nobnak.WaterSimulation.Editor {
    [CustomEditor(typeof(BoussinesqSimulation))]
    public class BoussinesqSimulationEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            var sim = (BoussinesqSimulation)target;

            if (sim.IsIntermediateToDeepWater) {
                EditorGUILayout.HelpBox(
                    $"[Relative Depth / Dispersion Warning]\n" +
                    $"Relative depth kh = {sim.EstimatedKh:F2} (h/λ = {sim.EstimatedHOverLambda:F2}, depth {sim.DeepWaterDepth:F1}m, estimated λ = {sim.EstimatedWavelength:F1}m).\n" +
                    "Water depth exceeds the optimal intermediate Boussinesq regime (kh <= 1.0, h/λ <= 0.16). " +
                    "Peregrine polynomial dispersion loses accuracy in deep water (kh > 1.0, with cutoff at kh = 1.73).",
                    MessageType.Warning);

                if (GUILayout.Button("Adjust Parameters for Intermediate Regime (kh <= 1.0 に調整)")) {
                    Undo.RecordObject(sim, "Adjust Boussinesq Parameters for Intermediate Regime");
                    sim.AdjustParametersForBoussinesqRegime();
                    EditorUtility.SetDirty(sim);
                }
                EditorGUILayout.HelpBox(
                    "Note: Parameter adjustment brings kh into the valid intermediate regime (kh <= 1.0) " +
                    "as a physical guideline, rather than an unconditional safety guarantee against nonlinear steepness breaking.",
                    MessageType.Info);
                EditorGUILayout.Space(2);
            }

            if (sim.IsFrequencyAboveCutoff) {
                EditorGUILayout.HelpBox(
                    $"[Peregrine Cutoff Frequency Warning]\n" +
                    $"Wavemaker frequency ({sim.WavemakerFrequency:F2} Hz) is near or exceeds Peregrine cutoff frequency " +
                    $"({sim.CutoffFrequency:F2} Hz for depth {sim.DeepWaterDepth:F1} m).\n" +
                    "Frequencies above cutoff become evanescent (k^2 < 0) and cannot propagate into the pool, causing local oscillations.",
                    MessageType.Warning);

                if (GUILayout.Button("Auto-Clamp Frequency to Safe Limit (周波数を安全域に自動調整)")) {
                    Undo.RecordObject(sim, "Auto-Clamp Boussinesq Wave Frequency");
                    sim.ClampFrequencyToSafeLimit();
                    EditorUtility.SetDirty(sim);
                }
                EditorGUILayout.Space(2);
            }

            var diag = sim.GetDispersionWavelengthDiagnostics();
            EditorGUILayout.LabelField("Dispersion & Resolution Diagnostics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Grid Spacing: dx = {diag.dx:F2}m, dz = {diag.dz:F2}m");
            EditorGUILayout.LabelField($"Deep Water (h={sim.DeepWaterDepth:F1}m): λ = {diag.lambdaDeep:F1}m ({diag.cellsDeep:F1} cells/wl)");
            EditorGUILayout.LabelField($"Shallow Shoal (h={sim.ShallowWaterDepth:F1}m): λ = {diag.lambdaShallow:F1}m ({diag.cellsShallow:F1} cells/wl)");

            if (!sim.IsResolutionSufficientForPeregrine(out string resWarning)) {
                EditorGUILayout.HelpBox(resWarning, MessageType.Warning);
            } else {
                EditorGUILayout.HelpBox(
                    $"[Resolution Status: OK]\n" +
                    $"Shallow water wavelength has {diag.cellsShallow:F1} cells/wavelength (>= 32 cells/wl provisional recommendation based on CPU reference studies; not an official GPU guarantee).",
                    MessageType.Info);
            }
            EditorGUILayout.Space(4);

            base.OnInspectorGUI();
        }
    }
}
