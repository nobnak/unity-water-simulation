using UnityEditor;
using UnityEngine;

namespace Nobnak.WaterSimulation.Editor {
    public static class WaterSimulationMenu {
        [MenuItem("GameObject/3D Object/Water Simulation/2D Wave Equation", false, 10)]
        public static void CreateWaveEquationWater(MenuCommand menuCommand) {
            var go = new GameObject("WaveEquationWater");
            var sim = go.AddComponent<WaveEquationSimulation>();
            var renderer = go.AddComponent<WaterRenderer>();
            renderer.TargetSimulation = sim;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute");
            var serializedSim = new SerializedObject(sim);
            serializedSim.FindProperty("waveShader").objectReferenceValue = compute;
            serializedSim.ApplyModifiedProperties();

            var shader = Shader.Find("Nobnak/WaterSimulation/WaterSurfaceCommon");
            if (shader != null) {
                var mat = new Material(shader);
                renderer.WaterMaterial = mat;
            }

            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Wave Equation Water");
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/3D Object/Water Simulation/Boussinesq System", false, 11)]
        public static void CreateBoussinesqWater(MenuCommand menuCommand) {
            var go = new GameObject("BoussinesqWater");
            var sim = go.AddComponent<BoussinesqSimulation>();
            var renderer = go.AddComponent<WaterRenderer>();
            renderer.TargetSimulation = sim;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            var serializedSim = new SerializedObject(sim);
            serializedSim.FindProperty("boussinesqShader").objectReferenceValue = compute;
            serializedSim.ApplyModifiedProperties();

            var shader = Shader.Find("Nobnak/WaterSimulation/WaterSurfaceCommon");
            if (shader != null) {
                var mat = new Material(shader);
                renderer.WaterMaterial = mat;
            }

            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Boussinesq Water");
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/3D Object/Water Simulation/FFT Ocean Surface", false, 12)]
        public static void CreateFFTOceanWater(MenuCommand menuCommand) {
            var go = new GameObject("FFTOceanWater");
            var sim = go.AddComponent<FFTOceanSimulation>();
            var renderer = go.AddComponent<WaterRenderer>();
            renderer.TargetSimulation = sim;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute");
            var serializedSim = new SerializedObject(sim);
            serializedSim.FindProperty("fftShader").objectReferenceValue = compute;
            serializedSim.ApplyModifiedProperties();

            var shader = Shader.Find("Nobnak/WaterSimulation/WaterSurfaceCommon");
            if (shader != null) {
                var mat = new Material(shader);
                renderer.WaterMaterial = mat;
            }

            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create FFT Ocean Water");
            Selection.activeObject = go;
        }
    }
}
