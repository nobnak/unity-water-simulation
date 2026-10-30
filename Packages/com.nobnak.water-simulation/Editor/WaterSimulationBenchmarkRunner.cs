using System;
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
using Object = UnityEngine.Object;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Headless / Editor benchmark and numerical verification harness.
    /// Separates solver-only, solver+render, and solver+render+profile readback timings,
    /// verifies GPU dispersion output for Sweeps 1..4 against CPU reference,
    /// and records execution statistics (mean, std dev, dispatches, GPU, API).
    /// </summary>
    public static class WaterSimulationBenchmarkRunner {
        private const string OutputCsvPath = "Docs/BenchmarkResults.csv";
        private static string CommitHash => GetGitCommitHash();

        [MenuItem("Tools/Water Simulation/Run Benchmark & Verification Suite", false, 51)]
        public static void RunBenchmarkSuite() {
            WaterSampleSceneBuilder.BuildAllSampleScenes();

            // 1. Verify GPU Compute Shader dispersion against CPU continuum solution
            VerifyComputeShaderDispersionVsCpu();

            // 2. Performance benchmark suite
            var sb = new StringBuilder();
            sb.AppendLine("Category,Method,Resolution,Mode,Steps,SubStepsPerStep,Sweeps,DispatchesPerStep,MeanTotalWallMs,StdDevTotalWallMs,AvgWallMsPerStep,GPU,API,UnityVersion,Commit,Notes");

            RunSolverBenchmarks(sb);
            RunSceneModeBenchmarks(sb);
            CaptureVerificationScreenshots();

            string fullPath = Path.GetFullPath(OutputCsvPath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);

            Debug.Log($"[WaterSimulationBenchmark] Completed benchmark & verification suite. Saved CSV to: {fullPath}\n{sb}");
        }

        public static void RunAllBenchmarksCommandLine() {
            try {
                RunBenchmarkSuite();
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                Debug.LogError($"[WaterSimulationBenchmark] Benchmark failed: {ex}");
                EditorApplication.Exit(1);
            }
        }

        private static void VerifyComputeShaderDispersionVsCpu() {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            if (shader == null) {
                Debug.LogWarning("[WaterSimulationBenchmark] Boussinesq.compute not found for verification.");
                return;
            }

            int kernelTridiagonalX = shader.FindKernel("CSTridiagonalX");
            int kernelTridiagonalZ = shader.FindKernel("CSTridiagonalZ");
            int kernelDefectCorrection = shader.FindKernel("CSDefectCorrection");

            int N = 64;
            float depth = 3.5f;
            float kh = 1.0f;
            float k = kh / depth;
            float kx = k * math.SQRT2 * 0.5f;
            float kz = k * math.SQRT2 * 0.5f;
            float dx = 100f / N;
            float dz = 100f / N;

            var forceOrigRT = new RenderTexture(N, N, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            forceOrigRT.enableRandomWrite = true;
            forceOrigRT.Create();

            var forceRT = new RenderTexture(N, N, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            forceRT.enableRandomWrite = true;
            forceRT.Create();

            var accelPingRT = new RenderTexture(N, N, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            accelPingRT.enableRandomWrite = true;
            accelPingRT.Create();

            var accelPongRT = new RenderTexture(N, N, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            accelPongRT.enableRandomWrite = true;
            accelPongRT.Create();

            var bathymetryRT = new RenderTexture(N, N, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            bathymetryRT.enableRandomWrite = true;
            bathymetryRT.Create();

            var initForce = new Texture2D(N, N, TextureFormat.RGFloat, false);
            var initBathy = new Texture2D(N, N, TextureFormat.RGFloat, false);
            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    float phase = kx * (x * dx) + kz * (y * dz);
                    float fVal = math.cos(phase);
                    initForce.SetPixel(x, y, new Color(fVal, fVal, 0f, 0f));
                    initBathy.SetPixel(x, y, new Color(depth, -depth, 0f, 0f));
                }
            }
            initForce.Apply();
            initBathy.Apply();
            Graphics.Blit(initForce, forceOrigRT);
            Graphics.Blit(initForce, forceRT);
            Graphics.Blit(initBathy, bathymetryRT);
            Object.DestroyImmediate(initForce);
            Object.DestroyImmediate(initBathy);

            shader.SetInt("_Width", N);
            shader.SetInt("_Height", N);
            shader.SetFloat("_GridSpacing", dx);
            shader.SetFloat("_GridSpacingZ", dz);
            shader.SetFloat("_DispersionAlpha", 1.0f);
            shader.SetInt("_SideWallSponge", 0);

            // Theoretical continuum gains for kh=1.0 at 45 deg:
            // target = 1 / (1 + kh^2/3) = 0.75
            // Sweep 1: 0.73469388
            // Sweep 2: 0.74968763
            // Sweep 3: 0.74999362
            // Sweep 4: 0.74999987
            double[] cpuGains = { 0.7346938775510202, 0.7496876301541023, 0.7499936251051858, 0.7499998699001059 };
            var resultsJson = new StringBuilder();
            resultsJson.AppendLine("{");
            resultsJson.AppendLine("  \"test\": \"GPU Compute Shader Peregrine Dispersion vs CPU Reference\",");
            resultsJson.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  \"kh\": {0:F1}, \"angle_deg\": 45, \"depth_m\": {1:F1}, \"resolution\": {2}, \"gpu\": \"{3}\", \"api\": \"{4}\",",
                kh, depth, N, SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceType));
            resultsJson.AppendLine("  \"sweeps\": [");

            for (int s = 1; s <= 4; s++) {
                Graphics.Blit(forceOrigRT, forceRT);

                for (int cur = 0; cur < s; cur++) {
                    if (cur > 0) {
                        shader.SetTexture(kernelDefectCorrection, "_AccelPing", accelPingRT);
                        shader.SetTexture(kernelDefectCorrection, "_ForceOrigTex", forceOrigRT);
                        shader.SetTexture(kernelDefectCorrection, "_ForceTex", forceRT);
                        shader.SetTexture(kernelDefectCorrection, "_BathymetryTex", bathymetryRT);
                        shader.Dispatch(kernelDefectCorrection, N / 8, N / 8, 1);
                    }

                    shader.SetTexture(kernelTridiagonalX, "_BathymetryTex", bathymetryRT);
                    shader.SetTexture(kernelTridiagonalX, "_ForceTex", forceRT);
                    shader.SetTexture(kernelTridiagonalX, "_AccelPong", accelPongRT);
                    shader.Dispatch(kernelTridiagonalX, N, 1, 1);

                    shader.SetTexture(kernelTridiagonalZ, "_BathymetryTex", bathymetryRT);
                    shader.SetTexture(kernelTridiagonalZ, "_AccelPong", accelPongRT);
                    shader.SetTexture(kernelTridiagonalZ, "_AccelPing", accelPingRT);
                    shader.Dispatch(kernelTridiagonalZ, N, 1, 1);
                }

                var req = AsyncGPUReadback.Request(accelPingRT, 0, TextureFormat.RGFloat);
                req.WaitForCompletion();
                var data = req.GetData<float2>();
                int cx = N / 2;
                int cy = N / 2;
                float2 centerVal = data[cy * N + cx];
                float centerPhase = kx * (cx * dx) + kz * (cy * dz);
                float expectedF = math.cos(centerPhase);
                float gpuGain = (math.abs(expectedF) > 1e-4f) ? (centerVal.x / expectedF) : centerVal.x;
                double cpuGain = cpuGains[s - 1];
                double err = math.abs(gpuGain - cpuGain);

                resultsJson.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    {{ \"sweep\": {0}, \"cpu_gain\": {1:F8}, \"gpu_gain\": {2:F8}, \"abs_error\": {3:E2}, \"passed\": {4} }}{5}",
                    s, cpuGain, gpuGain, err, (err < 0.05).ToString().ToLowerInvariant(), (s < 4 ? "," : "")));
            }

            resultsJson.AppendLine("  ]");
            resultsJson.AppendLine("}");

            File.WriteAllText("Docs/GpuDispersionVerificationResults.json", resultsJson.ToString(), Encoding.UTF8);
            Debug.Log($"[WaterSimulationBenchmark] Verified GPU dispersion vs CPU reference. Saved to Docs/GpuDispersionVerificationResults.json");

            forceOrigRT.Release();
            forceRT.Release();
            accelPingRT.Release();
            accelPongRT.Release();
            bathymetryRT.Release();
            Object.DestroyImmediate(forceOrigRT);
            Object.DestroyImmediate(forceRT);
            Object.DestroyImmediate(accelPingRT);
            Object.DestroyImmediate(accelPongRT);
            Object.DestroyImmediate(bathymetryRT);
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

        private static void Measure(Action stepAction, Action syncAction, int warmup, int measureSteps, int trials, out double meanMs, out double stdDevMs) {
            for (int i = 0; i < warmup; i++) stepAction();
            syncAction();

            double[] times = new double[trials];
            for (int t = 0; t < trials; t++) {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < measureSteps; i++) stepAction();
                syncAction();
                sw.Stop();
                times[t] = sw.Elapsed.TotalMilliseconds;
            }

            double sum = 0;
            for (int t = 0; t < trials; t++) sum += times[t];
            meanMs = sum / trials;

            double varSum = 0;
            for (int t = 0; t < trials; t++) varSum += (times[t] - meanMs) * (times[t] - meanMs);
            stdDevMs = math.sqrt((float)(varSum / trials));
        }

        private static void RunSolverBenchmarks(StringBuilder sb) {
            int[] resolutions = { 256, 512 };
            const int warmupSteps = 10;
            const int measureSteps = 100;
            const int trials = 3;
            const float dt = 0.016f;

            string gpu = SystemInfo.graphicsDeviceName.Replace(',', ';');
            string api = SystemInfo.graphicsDeviceType.ToString();
            string unityVer = Application.unityVersion;

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

                    Measure(() => sim.Step(dt), () => ForceGpuSync(sim.WaterDataTexture), warmupSteps, measureSteps, trials, out double meanMs, out double stdDevMs);

                    double avgMs = meanMs / measureSteps;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "SolverOnly,WaveEquation,{0}x{0},RGFloat_Symplectic,{1},{2},0,{3},{4:F3},{5:F3},{6:F4},{7},{8},{9},{10},c=8m/s_L=50m",
                        res, measureSteps, sim.LastSubStepCount, sim.LastDispatchCount, meanMs, stdDevMs, avgMs, gpu, api, unityVer, CommitHash));

                    Object.DestroyImmediate(go);
                }

                // 2. Boussinesq Solver-Only (alpha = 1.0, sweeps 1, 2, 3, 4)
                for (int sweeps = 1; sweeps <= 4; sweeps++) {
                    var go = new GameObject($"Bench_Boussinesq_Sweeps{sweeps}");
                    var sim = go.AddComponent<BoussinesqSimulation>();
                    var so = new SerializedObject(sim);
                    so.FindProperty("areaSize").vector2Value = new Vector2(100f, 100f);
                    so.FindProperty("resolution").vector2IntValue = new Vector2Int(res, res);
                    so.FindProperty("dispersionAlpha").floatValue = 1.0f;
                    so.FindProperty("dispersionSweeps").intValue = sweeps;
                    so.FindProperty("deepWaterDepth").floatValue = 3.5f;
                    so.FindProperty("shallowWaterDepth").floatValue = 0.8f;
                    so.FindProperty("autoPlay").boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();

                    sim.enabled = false;
                    sim.enabled = true;
                    sim.ResetSimulation();

                    Measure(() => sim.Step(dt), () => ForceGpuSync(sim.WaterDataTexture), warmupSteps, measureSteps, trials, out double meanMs, out double stdDevMs);

                    double avgMs = meanMs / measureSteps;
                    string modeStr = (sweeps == 1) ? "Peregrine_ADI_1Sweep" : $"Peregrine_DefectCorrected_{sweeps}Sweeps";
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "SolverOnly,Boussinesq,{0}x{0},{1},{2},{3},{4},{5},{6:F3},{7:F3},{8:F4},{9},{10},{11},{12},alpha=1_sweeps={4}_h=3.5m",
                        res, modeStr, measureSteps, sim.LastSubStepCount, sweeps, sim.LastDispatchCount, meanMs, stdDevMs, avgMs, gpu, api, unityVer, CommitHash));

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
                    Measure(() => {
                        sim.WindSpeed = sim.WindSpeed;
                        sim.WindDirectionDegrees = sim.WindDirectionDegrees;
                        sim.SwellStrength = sim.SwellStrength;
                        sim.SwellDirectionDegrees = sim.SwellDirectionDegrees;
                        sim.Step(dt);
                    }, () => ForceGpuSync(sim.WaterDataTexture), warmupSteps, measureSteps, trials, out double meanMs, out double stdDevMs);

                    int regenDelta = sim.SpectrumRegenerationCount - regenBefore;
                    double avgMs = meanMs / measureSteps;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "SolverOnly,FFTOcean,{0}x{0},SingleIFFT_Packed,{1},{2},0,{3},{4:F3},{5:F3},{6:F4},{7},{8},{9},{10},RedundantSpectrumRegen={11}",
                        res, measureSteps, sim.LastSubStepCount, sim.LastDispatchCount, meanMs, stdDevMs, avgMs, gpu, api, unityVer, CommitHash, regenDelta));

                    Object.DestroyImmediate(go);
                }
            }
        }

        private static void RunSceneModeBenchmarks(StringBuilder sb) {
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
            const int trials = 3;
            const float dt = 0.016f;

            string gpu = SystemInfo.graphicsDeviceName.Replace(',', ';');
            string api = SystemInfo.graphicsDeviceType.ToString();
            string unityVer = Application.unityVersion;

            // Mode A: Solver Only
            {
                foreach (var s in sims) s.ResetSimulation();
                Measure(() => {
                    foreach (var s in sims) s.Step(dt);
                }, () => {
                    for (int j = 0; j < sims.Length; j++) ForceGpuSync(sims[j].WaterDataTexture);
                }, 5, steps, trials, out double meanMs, out double stdDevMs);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Scene04_Separation,WaveEq+Boussinesq,512x512,1_SolverOnly,{0},1,0,0,{1:F3},{2:F3},{3:F4},{4},{5},{6},{7},2_Simulations_SolverOnly",
                    steps, meanMs, stdDevMs, meanMs / steps, gpu, api, unityVer, CommitHash));
            }

            // Mode B: Solver + Camera Render (Profile Viewers Disabled)
            {
                foreach (var p in profiles) p.enabled = false;
                foreach (var s in sims) s.ResetSimulation();
                Measure(() => {
                    foreach (var s in sims) s.Step(dt);
                    foreach (var r in renderers) r.UpdateMaterialProperties();
                    if (cam != null) cam.Render();
                }, () => ForceGpuSync(camRT), 5, steps, trials, out double meanMs, out double stdDevMs);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Scene04_Separation,WaveEq+Boussinesq,512x512,2_SolverPlusRender,{0},1,0,0,{1:F3},{2:F3},{3:F4},{4},{5},{6},{7},Camera_1280x720",
                    steps, meanMs, stdDevMs, meanMs / steps, gpu, api, unityVer, CommitHash));
            }

            // Mode C: Solver + Render + ProfileViewer (AsyncGPUReadback with AllowAsyncInEditMode)
            {
                foreach (var p in profiles) {
                    p.enabled = true;
                    p.ForceSynchronousReadback = false;
                    p.AllowAsyncInEditMode = true;
                }
                foreach (var s in sims) s.ResetSimulation();
                Measure(() => {
                    foreach (var s in sims) s.Step(dt);
                    foreach (var r in renderers) r.UpdateMaterialProperties();
                    foreach (var p in profiles) p.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);
                    if (cam != null) cam.Render();
                }, () => {
                    AsyncGPUReadback.WaitAllRequests();
                    ForceGpuSync(camRT);
                }, 5, steps, trials, out double meanMs, out double stdDevMs);

                bool wasSync = profiles.Length > 0 && profiles[0].LastReadbackWasSync;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Scene04_Separation,WaveEq+Boussinesq,512x512,3_SolverRenderProfileAsync,{0},1,0,0,{1:F3},{2:F3},{3:F4},{4},{5},{6},{7},ReadbackPath={8}",
                    steps, meanMs, stdDevMs, meanMs / steps, gpu, api, unityVer, CommitHash,
                    wasSync ? "SynchronousFallback" : "AsyncGPUReadback"));
            }

            // Mode D: Solver + Render + ProfileViewer (Synchronous CPU Stalling Readback)
            {
                foreach (var p in profiles) {
                    p.enabled = true;
                    p.ForceSynchronousReadback = true;
                    p.AllowAsyncInEditMode = false;
                }
                foreach (var s in sims) s.ResetSimulation();
                Measure(() => {
                    foreach (var s in sims) s.Step(dt);
                    foreach (var r in renderers) r.UpdateMaterialProperties();
                    foreach (var p in profiles) p.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);
                    if (cam != null) cam.Render();
                }, () => ForceGpuSync(camRT), 5, steps, trials, out double meanMs, out double stdDevMs);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Scene04_Separation,WaveEq+Boussinesq,512x512,4_SolverRenderProfileSync,{0},1,0,0,{1:F3},{2:F3},{3:F4},{4},{5},{6},{7},ReadbackPath=Synchronous_ForcedStall",
                    steps, meanMs, stdDevMs, meanMs / steps, gpu, api, unityVer, CommitHash));
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

        private static string GetGitCommitHash() {
            try {
                var startInfo = new ProcessStartInfo("git", "rev-parse --short HEAD") {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(startInfo)) {
                    if (process != null) {
                        string output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit(1000);
                        if (!string.IsNullOrEmpty(output)) return output;
                    }
                }
            } catch { }
            return "unknown";
        }
    }
}
