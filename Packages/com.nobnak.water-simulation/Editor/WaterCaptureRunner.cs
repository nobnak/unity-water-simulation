using System.Collections;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Executes visual rendering captures across representative scenes for validation.
    /// Runs via CLI:
    /// -executeMethod Nobnak.WaterSimulation.Editor.WaterCaptureRunner.CaptureAllRepresentativeScenes
    /// </summary>
    public static class WaterCaptureRunner {
        private const string CaptureDir = "Captures";

        [MenuItem("Tools/Water Simulation/Build And Capture All", false, 55)]
        public static void BuildAndCaptureAll() {
            WaterSampleSceneBuilder.BuildAllSampleScenes();
            CaptureAllRepresentativeScenes();
        }

        [MenuItem("Tools/Water Simulation/Build And Capture Scene 02", false, 56)]
        public static void BuildAndCaptureScene02() {
            WaterSampleSceneBuilder.BuildAllSampleScenes();
            CaptureScene02();
        }

        [MenuItem("Tools/Water Simulation/Capture All Representative Scenes", false, 60)]
        public static void CaptureAllRepresentativeScenes() {
            if (!Directory.Exists(CaptureDir)) Directory.CreateDirectory(CaptureDir);

            CaptureScene("Assets/Scenes/01_WaveEquation.unity", "01_WaveEquation_Initial.png", "01_WaveEquation_Active.png", 60);
            CaptureScene("Assets/Scenes/02_Boussinesq.unity", "02_Boussinesq_Initial.png", "02_Boussinesq_Active.png", 90);
            CaptureScene("Assets/Scenes/03_FFTWater.unity", "03_FFTWater_Overview.png", "03_FFTWater_Active.png", 45);
            CaptureScene("Assets/Scenes/04_Comparison_Dispersion.unity", "04_Comparison_Initial.png", "04_Comparison_Active.png", 80);
            CaptureScene("Assets/Scenes/05_Comparison_Overview.unity", "05_Overview_Initial.png", "05_Overview_Active.png", 60);

            Debug.Log("[WaterSimulation] Completed all representative visual captures!");
        }

        [MenuItem("Tools/Water Simulation/Capture Scene 02 Boussinesq", false, 61)]
        public static void CaptureScene02() {
            if (!Directory.Exists(CaptureDir)) Directory.CreateDirectory(CaptureDir);
            CaptureScene("Assets/Scenes/02_Boussinesq.unity", "02_Boussinesq_Initial.png", "02_Boussinesq_Active.png", 240);

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/02_Boussinesq.unity", OpenSceneMode.Single);
            var cam = Camera.main;
            if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return;

            int width = 1280;
            int height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;

            var boussSim = Object.FindFirstObjectByType<BoussinesqSimulation>();
            var renderer = Object.FindFirstObjectByType<WaterRenderer>();

            if (boussSim != null && renderer != null) {
                // 1. Capture Bathymetry visual mode
                boussSim.ResetSimulation();
                float dt = 0.033f;
                for (int i = 0; i < 180; i++) boussSim.Step(dt);

                renderer.SetVisualMode(WaterRenderer.VisualMode.Bathymetry);
                renderer.UpdateMaterialProperties();
                cam.Render();
                SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, "02_Boussinesq_Bathymetry.png"));

                // 2. Capture Periodic Planar Soliton mode in Realistic view
                renderer.SetVisualMode(WaterRenderer.VisualMode.Realistic);
                boussSim.Condition = BoussinesqSimulation.InitialConditionType.PeriodicPulses;
                boussSim.ImpulseShape = BoussinesqSimulation.ImpulseShapeType.PlanarSoliton;
                boussSim.PulseInterval = 1.8f;
                boussSim.WavemakerAmplitude = 1.0f;
                boussSim.ResetSimulation();

                for (int i = 0; i < 180; i++) boussSim.Step(dt);
                renderer.UpdateMaterialProperties();
                cam.Render();
                SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, "02_Boussinesq_PlanarSoliton.png"));
            }

            cam.targetTexture = prevTarget;
            rt.Release();
            Object.DestroyImmediate(rt);
            Debug.Log("[WaterSimulation] Completed Scene 02 Boussinesq captures (Active, Bathymetry, PlanarSoliton)!");
        }

        [MenuItem("Tools/Water Simulation/Capture Periodic Pulses", false, 63)]
        public static void CapturePeriodicPulses() {
            if (!Directory.Exists(CaptureDir)) Directory.CreateDirectory(CaptureDir);

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/01_WaveEquation.unity", OpenSceneMode.Single);
            var cam = Camera.main;
            if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return;

            int width = 1280;
            int height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;

            var waveSim = Object.FindFirstObjectByType<WaveEquationSimulation>();
            var renderer = Object.FindFirstObjectByType<WaterRenderer>();

            if (waveSim != null) {
                waveSim.SourceMode = WaveEquationSimulation.InputMode.ContinuousOscillator;
                waveSim.PulseInterval = 1.0f;
                waveSim.ResetSimulation();

                float dt = 0.033f;
                for (int i = 0; i < 90; i++) {
                    waveSim.Step(dt);
                }
            }

            if (renderer != null) renderer.UpdateMaterialProperties();

            cam.Render();
            SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, "01_WaveEquation_PeriodicPulses.png"));

            cam.targetTexture = prevTarget;
            rt.Release();
            Object.DestroyImmediate(rt);
            Debug.Log("[WaterSimulation] Completed Periodic Pulses capture!");
        }

        [MenuItem("Tools/Water Simulation/Capture Visual Modes", false, 62)]
        public static void CaptureVisualModes() {
            if (!Directory.Exists(CaptureDir)) Directory.CreateDirectory(CaptureDir);

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/01_WaveEquation.unity", OpenSceneMode.Single);
            var cam = Camera.main;
            if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return;

            int width = 1280;
            int height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;

            var sims = Object.FindObjectsByType<WaterSimulationBase>(FindObjectsSortMode.None);
            var renderers = Object.FindObjectsByType<WaterRenderer>(FindObjectsSortMode.None);

            foreach (var sim in sims) sim.ResetSimulation();

            float dt = 0.033f;
            for (int i = 0; i < 60; i++) {
                foreach (var sim in sims) sim.Step(dt);
            }

            // 1. Realistic
            foreach (var r in renderers) {
                r.SetVisualMode(WaterRenderer.VisualMode.Realistic);
                r.UpdateMaterialProperties();
            }
            cam.Render();
            SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, "01_WaveEquation_Realistic.png"));

            // 2. HeightMap
            foreach (var r in renderers) {
                r.SetVisualMode(WaterRenderer.VisualMode.HeightMap);
                r.UpdateMaterialProperties();
            }
            cam.Render();
            SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, "01_WaveEquation_HeightMap.png"));

            // 3. NormalVisual
            foreach (var r in renderers) {
                r.SetVisualMode(WaterRenderer.VisualMode.NormalVisual);
                r.UpdateMaterialProperties();
            }
            cam.Render();
            SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, "01_WaveEquation_Normal.png"));

            // 4. Wireframe
            foreach (var r in renderers) {
                r.SetVisualMode(WaterRenderer.VisualMode.Wireframe);
                r.UpdateMaterialProperties();
            }
            cam.Render();
            SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, "01_WaveEquation_Wireframe.png"));

            cam.targetTexture = prevTarget;
            rt.Release();
            Object.DestroyImmediate(rt);
            Debug.Log("[WaterSimulation] Completed Visual Modes captures (Realistic, HeightMap, Normal, Wireframe)!");
        }

        private static void CaptureScene(string scenePath, string fileNameInit, string fileNameActive, int activeSteps) {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var cam = Camera.main;
            if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return;

            int width = 1280;
            int height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;

            // Find simulations and renderers in scene
            var sims = Object.FindObjectsByType<WaterSimulationBase>(FindObjectsSortMode.None);
            var renderers = Object.FindObjectsByType<WaterRenderer>(FindObjectsSortMode.None);

            foreach (var sim in sims) {
                sim.ResetSimulation();
            }

            foreach (var r in renderers) {
                r.UpdateMaterialProperties();
            }

            // Step 1: Initial state capture
            cam.Render();
            SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, fileNameInit));

            // Step 2: Simulate forward by activeSteps
            float dt = 0.033f;
            for (int i = 0; i < activeSteps; i++) {
                foreach (var sim in sims) {
                    sim.Step(dt);
                }
            }

            foreach (var r in renderers) {
                r.UpdateMaterialProperties();
            }

            cam.Render();
            SaveRenderTextureToPNG(rt, Path.Combine(CaptureDir, fileNameActive));

            cam.targetTexture = prevTarget;
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        private static void SaveRenderTextureToPNG(RenderTexture rt, string filePath) {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;

            byte[] bytes = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);
            File.WriteAllBytes(filePath, bytes);
            Debug.Log($"[WaterSimulation] Captured: {filePath}");
        }
    }
}
