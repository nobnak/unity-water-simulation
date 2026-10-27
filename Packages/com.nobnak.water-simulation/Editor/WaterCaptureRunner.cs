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
