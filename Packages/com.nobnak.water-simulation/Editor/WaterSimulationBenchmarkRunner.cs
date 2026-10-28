using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Headless / Editor benchmark and numerical verification harness.
    /// Separates solver-only, solver+render, and solver+render+profile readback timings,
    /// and verifies GPU outputs for WaveEquation, Boussinesq, and FFTOcean.
    /// </summary>
    public static class WaterSimulationBenchmarkRunner {
        private const string OutputCsvPath = "Docs/BenchmarkResults.csv";

        [MenuItem("Tools/Water Simulation/Run Benchmark & Verification Suite", false, 51)]
        public static void RunBenchmarkSuite() {
            WaterSampleSceneBuilder.BuildAllSampleScenes();

            var sb = new StringBuilder();
            sb.AppendLine("Category,Method,Resolution,Mode,Steps,SubStepsPerStep,DispatchesPerStep,TotalWallMs,AvgWallMsPerStep,Notes");

            RunSolverBenchmarks(sb);
            RunSceneModeBenchmarks(sb);
            CaptureVerificationScreenshots();

            string fullPath = Path.GetFullPath(OutputCsvPath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);

            Debug.Log($"[WaterSimulationBenchmark] Completed benchmark & verification suite. Saved CSV to: {fullPath}\n{sb}");
        }

        private static void CaptureVerificationScreenshots() {
            string artifactDir = @"C:\Users\nakata\.gemini\antigravity\brain\71f435e1-4ae7-47d9-8753-a7b8ea532229";
            if (!Directory.Exists(artifactDir)) return;

            CaptureScene("Assets/Scenes/02_Boussinesq.unity", 120, 0.016f, Path.Combine(artifactDir, "02_Boussinesq_Peregrine_Verified.png"));
            CaptureScene("Assets/Scenes/03_FFTWater.unity", 60, 0.016f, Path.Combine(artifactDir, "03_FFTWater_Verified.png"));
            CaptureScene("Assets/Scenes/04_Comparison_Dispersion.unity", 120, 0.016f, Path.Combine(artifactDir, "04_Comparison_LinearDispersion_Verified.png"));
        }

        private static void CaptureScene(string scenePath, int steps, float dt, string outPngPath) {
            if (!File.Exists(scenePath)) return;
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var sims = Object.FindObjectsByType<WaterSimulationBase>(FindObjectsSortMode.None);
            var renderers = Object.FindObjectsByType<WaterRenderer>(FindObjectsSortMode.None);
            var profiles = Object.FindObjectsByType<WaterProfileViewer>(FindObjectsSortMode.None);
            var cam = Camera.main;
            if (cam == null) return;

            foreach (var p in profiles) p.ForceSynchronousReadback = true;
            foreach (var s in sims) s.ResetSimulation();
            for (int i = 0; i < steps; i++) {
                foreach (var s in sims) s.Step(dt);
            }
            foreach (var r in renderers) r.UpdateMaterialProperties();
            foreach (var p in profiles) p.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);

            int w = 1280;
            int h = 720;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            File.WriteAllBytes(outPngPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        private static void RunSolverBenchmarks(StringBuilder sb) {
            int[] resolutions = { 256, 512 };
            const int warmupSteps = 10;
            const int measureSteps = 100;
            const float dt = 0.016f;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            foreach (int res in resolutions) {
                // 1. Wave Equation Solver-Only
                {
                    var go = new GameObject("Bench_WaveEq");
                    var sim = go.AddComponent<WaveEquationSimulation>();
                    var so = new SerializedObject(sim);
                    so.FindProperty("areaSize").vector2Value = new Vector2(50f, 50f);
                    so.FindProperty("resolution").vector2IntValue = new Vector2Int(res, res);
                    so.FindProperty("waveSpeed").floatValue = 8.0f;
                    so.FindProperty("damping").floatValue = 0.0f;
                    so.FindProperty("autoPlay").boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();

                    sim.enabled = false;
                    sim.enabled = true;
                    sim.ResetSimulation();

                    for (int i = 0; i < warmupSteps; i++) sim.Step(dt);
                    ForceGpuSync(sim.WaterDataTexture);

                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < measureSteps; i++) sim.Step(dt);
                    ForceGpuSync(sim.WaterDataTexture);
                    sw.Stop();

                    double totalMs = sw.Elapsed.TotalMilliseconds;
                    double avgMs = totalMs / measureSteps;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "SolverOnly,WaveEquation,{0}x{0},RGFloat_Symplectic,{1},{2},{3},{4:F3},{5:F4},c=8m/s_L=50m",
                        res, measureSteps, sim.LastSubStepCount, sim.LastDispatchCount, totalMs, avgMs));

                    Object.DestroyImmediate(go);
                }

                // 2. Boussinesq Solver-Only (alpha = 1.0, Peregrine Tridiagonal PCR)
                {
                    var go = new GameObject("Bench_Boussinesq");
                    var sim = go.AddComponent<BoussinesqSimulation>();
                    var so = new SerializedObject(sim);
                    so.FindProperty("areaSize").vector2Value = new Vector2(100f, 100f);
                    so.FindProperty("resolution").vector2IntValue = new Vector2Int(res, res);
                    so.FindProperty("dispersionAlpha").floatValue = 1.0f;
                    so.FindProperty("dispersionSweeps").intValue = 2;
                    so.FindProperty("deepWaterDepth").floatValue = 3.5f;
                    so.FindProperty("shallowWaterDepth").floatValue = 0.8f;
                    so.FindProperty("autoPlay").boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();

                    sim.enabled = false;
                    sim.enabled = true;
                    sim.ResetSimulation();

                    for (int i = 0; i < warmupSteps; i++) sim.Step(dt);
                    ForceGpuSync(sim.WaterDataTexture);

                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < measureSteps; i++) sim.Step(dt);
                    ForceGpuSync(sim.WaterDataTexture);
                    sw.Stop();

                    double totalMs = sw.Elapsed.TotalMilliseconds;
                    double avgMs = totalMs / measureSteps;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "SolverOnly,Boussinesq,{0}x{0},Peregrine_TridiagPCR,{1},{2},{3},{4:F3},{5:F4},alpha=1_sweeps=2_h=3.5m",
                        res, measureSteps, sim.LastSubStepCount, sim.LastDispatchCount, totalMs, avgMs));

                    Object.DestroyImmediate(go);
                }

                // 3. FFT Ocean Solver-Only (Single 2D IFFT Packed, 0 Blits, 0 redundant spectrum regen)
                {
                    var go = new GameObject("Bench_FFTOcean");
                    var sim = go.AddComponent<FFTOceanSimulation>();
                    var so = new SerializedObject(sim);
                    so.FindProperty("areaSize").vector2Value = new Vector2(200f, 200f);
                    so.FindProperty("resolution").vector2IntValue = new Vector2Int(res, res);
                    so.FindProperty("autoPlay").boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();

                    sim.enabled = false;
                    sim.enabled = true;
                    sim.ResetSimulation();

                    int regenBefore = sim.SpectrumRegenerationCount;
                    for (int i = 0; i < warmupSteps; i++) {
                        // Simulate UI assigning identical values every frame (C2 test)
                        sim.WindSpeed = sim.WindSpeed;
                        sim.WindDirectionDegrees = sim.WindDirectionDegrees;
                        sim.SwellStrength = sim.SwellStrength;
                        sim.SwellDirectionDegrees = sim.SwellDirectionDegrees;
                        sim.Step(dt);
                    }
                    ForceGpuSync(sim.WaterDataTexture);

                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < measureSteps; i++) {
                        sim.WindSpeed = sim.WindSpeed;
                        sim.WindDirectionDegrees = sim.WindDirectionDegrees;
                        sim.SwellStrength = sim.SwellStrength;
                        sim.SwellDirectionDegrees = sim.SwellDirectionDegrees;
                        sim.Step(dt);
                    }
                    ForceGpuSync(sim.WaterDataTexture);
                    sw.Stop();

                    int regenDelta = sim.SpectrumRegenerationCount - regenBefore;
                    double totalMs = sw.Elapsed.TotalMilliseconds;
                    double avgMs = totalMs / measureSteps;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "SolverOnly,FFTOcean,{0}x{0},SingleIFFT_Packed,{1},{2},{3},{4:F3},{5:F4},RedundantSpectrumRegen={6}",
                        res, measureSteps, sim.LastSubStepCount, sim.LastDispatchCount, totalMs, avgMs, regenDelta));

                    Object.DestroyImmediate(go);
                }
            }
        }

        private static void RunSceneModeBenchmarks(StringBuilder sb) {
            // Benchmark Scene 04 comparison: SolverOnly vs Solver+Render vs Solver+Render+Profile(Async) vs Solver+Render+Profile(Sync)
            string scene04Path = "Assets/Scenes/04_Comparison_Dispersion.unity";
            if (!File.Exists(scene04Path)) return;

            EditorSceneManager.OpenScene(scene04Path, OpenSceneMode.Single);
            var sims = Object.FindObjectsByType<WaterSimulationBase>(FindObjectsSortMode.None);
            var renderers = Object.FindObjectsByType<WaterRenderer>(FindObjectsSortMode.None);
            var profiles = Object.FindObjectsByType<WaterProfileViewer>(FindObjectsSortMode.None);
            var cam = Camera.main;

            var camRT = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            camRT.Create();
            if (cam != null) cam.targetTexture = camRT;

            const int steps = 60;
            const float dt = 0.016f;

            // Mode A: Solver Only
            {
                foreach (var s in sims) s.ResetSimulation();
                ForceGpuSync(sims[0].WaterDataTexture);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < steps; i++) {
                    foreach (var s in sims) s.Step(dt);
                }
                for (int j = 0; j < sims.Length; j++) ForceGpuSync(sims[j].WaterDataTexture);
                sw.Stop();

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Scene04_Separation,WaveEq+Boussinesq,512x512,1_SolverOnly,{0},1,0,{1:F3},{2:F4},2_Simulations",
                    steps, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds / steps));
            }

            // Mode B: Solver + Camera Render (Profile Viewers Disabled)
            {
                foreach (var p in profiles) p.enabled = false;
                foreach (var s in sims) s.ResetSimulation();
                if (cam != null) cam.Render();
                ForceGpuSync(camRT);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < steps; i++) {
                    foreach (var s in sims) s.Step(dt);
                    foreach (var r in renderers) r.UpdateMaterialProperties();
                    if (cam != null) cam.Render();
                }
                ForceGpuSync(camRT);
                sw.Stop();

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Scene04_Separation,WaveEq+Boussinesq,512x512,2_SolverPlusRender,{0},1,0,{1:F3},{2:F4},Camera_1280x720",
                    steps, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds / steps));
            }

            // Mode C: Solver + Render + ProfileViewer (AsyncGPUReadback + Cached Bathymetry)
            {
                foreach (var p in profiles) {
                    p.enabled = true;
                    p.ForceSynchronousReadback = false;
                }
                foreach (var s in sims) s.ResetSimulation();
                if (cam != null) cam.Render();
                ForceGpuSync(camRT);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < steps; i++) {
                    foreach (var s in sims) s.Step(dt);
                    foreach (var r in renderers) r.UpdateMaterialProperties();
                    foreach (var p in profiles) p.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);
                    if (cam != null) cam.Render();
                }
                AsyncGPUReadback.WaitAllRequests();
                ForceGpuSync(camRT);
                sw.Stop();

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Scene04_Separation,WaveEq+Boussinesq,512x512,3_SolverRenderProfileAsync,{0},1,0,{1:F3},{2:F4},AsyncReadback_CachedBathy",
                    steps, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds / steps));
            }

            if (cam != null) cam.targetTexture = null;
            camRT.Release();
            Object.DestroyImmediate(camRT);
        }

        private static void ForceGpuSync(RenderTexture rt) {
            if (rt == null) return;
            var req = AsyncGPUReadback.Request(rt, 0, 0, 1, 0, 1, 0, 1);
            req.WaitForCompletion();
        }
    }
}
