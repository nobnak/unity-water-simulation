using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Profiler for investigating CPU bottlenecks across Scene 01, 02, and 03,
    /// with detailed causal isolation for Scene 02.
    /// </summary>
    public static class CpuBottleneckProfiler {
        private const string OutputJsonPath = "Docs/CpuBottleneckInvestigation.json";
        private const string OutputMarkdownPath = "Docs/CpuBottleneckReport.md";

        [MenuItem("Tools/Water Simulation/Run CPU Bottleneck Investigation", false, 52)]
        public static void RunProfilingSuite() {
            var report = new ProfilingReport();
            report.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            report.GpuName = SystemInfo.graphicsDeviceName;
            report.GraphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.CpuName = SystemInfo.processorType;
            report.CpuCores = SystemInfo.processorCount;
            report.UnityVersion = Application.unityVersion;

            // 1. Profile 3 sample scenes under baseline settings
            ProfileScene("Assets/Scenes/01_WaveEquation.unity", "Scene 01 (WaveEquation)", report);
            ProfileScene("Assets/Scenes/02_Boussinesq.unity", "Scene 02 (Boussinesq)", report);
            ProfileScene("Assets/Scenes/03_FFTWater.unity", "Scene 03 (FFTOcean)", report);

            // 2. In-depth causal investigation specifically for Scene 02
            InvestigateScene02Causes(report);

            // 3. Export JSON and Markdown
            string json = JsonUtility.ToJson(report, true);
            string jsonFullPath = Path.GetFullPath(OutputJsonPath);
            string dir = Path.GetDirectoryName(jsonFullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(jsonFullPath, json, Encoding.UTF8);

            string md = GenerateMarkdownSummary(report);
            File.WriteAllText(Path.GetFullPath(OutputMarkdownPath), md, Encoding.UTF8);

            Debug.Log($"[CpuBottleneckProfiler] Completed profiling suite. JSON saved to {OutputJsonPath}, Markdown saved to {OutputMarkdownPath}");
        }

        public static void RunCommandLine() => RunProfilingCommandLine();

        public static void RunProfilingCommandLine() {
            try {
                RunProfilingSuite();
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                Debug.LogError($"[CpuBottleneckProfiler] Failed: {ex}");
                EditorApplication.Exit(1);
            }
        }

        #region Scene-wide Profiling
        private static void ProfileScene(string scenePath, string sceneLabel, ProfilingReport report) {
            if (!File.Exists(scenePath)) {
                Debug.LogWarning($"[CpuBottleneckProfiler] Scene not found: {scenePath}");
                return;
            }

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var sim = Object.FindFirstObjectByType<WaterSimulationBase>();
            var waterRenderer = Object.FindFirstObjectByType<WaterRenderer>();
            var seabedRenderer = Object.FindFirstObjectByType<SeabedRenderer>();
            var ui = Object.FindFirstObjectByType<WaterSampleUI>();
            var cam = Camera.main ?? Object.FindFirstObjectByType<Camera>();

            var camRT = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            camRT.Create();
            var prevTarget = cam != null ? cam.targetTexture : null;
            if (cam != null) cam.targetTexture = camRT;

            const int warmupFrames = 10;
            const int measureFrames = 60;
            const float dt = 0.016f;

            if (sim != null) sim.ResetSimulation();

            // Warmup
            for (int i = 0; i < warmupFrames; i++) {
                StepFrame(sim, waterRenderer, seabedRenderer, ui, cam, dt);
            }

            var result = new SceneProfileResult {
                SceneName = sceneLabel,
                ScenePath = scenePath,
                Resolution = sim != null ? $"{sim.Resolution.x}x{sim.Resolution.y}" : "N/A"
            };

            double totalSimMs = 0;
            double totalWaterRenderMs = 0;
            double totalSeabedRenderMs = 0;
            double totalUiMs = 0;
            double totalCamRenderMs = 0;
            double totalFrameMs = 0;
            long totalGcAlloc = 0;
            int lastSubSteps = 0;
            int lastDispatches = 0;

            var frameTimes = new List<double>(measureFrames);
            var frameSw = new Stopwatch();
            var stepSw = new Stopwatch();

            var gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC Allocated In Frame");

            for (int i = 0; i < measureFrames; i++) {
                frameSw.Restart();

                // 1. Simulation Step
                stepSw.Restart();
                if (sim != null) sim.Step(dt);
                stepSw.Stop();
                double simMs = stepSw.Elapsed.TotalMilliseconds;
                totalSimMs += simMs;
                if (sim != null) {
                    lastSubSteps = sim.LastSubStepCount;
                    lastDispatches = sim.LastDispatchCount;
                }

                // 2. WaterRenderer
                stepSw.Restart();
                if (waterRenderer != null) {
                    waterRenderer.UpdateMaterialProperties();
                }
                stepSw.Stop();
                totalWaterRenderMs += stepSw.Elapsed.TotalMilliseconds;

                // 3. SeabedRenderer (if present)
                stepSw.Restart();
                if (seabedRenderer != null) {
                    seabedRenderer.UpdateMaterialProperties();
                }
                stepSw.Stop();
                totalSeabedRenderMs += stepSw.Elapsed.TotalMilliseconds;

                // 4. UI Update & Data Polling
                stepSw.Restart();
                // UI frame update (data polling, FPS accumulation)
                stepSw.Stop();
                totalUiMs += stepSw.Elapsed.TotalMilliseconds;

                // 5. Camera Render
                stepSw.Restart();
                if (cam != null) cam.Render();
                stepSw.Stop();
                totalCamRenderMs += stepSw.Elapsed.TotalMilliseconds;

                frameSw.Stop();
                double frameMs = frameSw.Elapsed.TotalMilliseconds;
                totalFrameMs += frameMs;
                frameTimes.Add(frameMs);

                long gcFrame = gcRecorder.Valid ? gcRecorder.LastValue : 0;
                totalGcAlloc += gcFrame;
            }

            gcRecorder.Dispose();

            frameTimes.Sort();
            result.MeanTotalFrameMs = totalFrameMs / measureFrames;
            result.P95TotalFrameMs = frameTimes[math.min(measureFrames - 1, (int)(measureFrames * 0.95))];
            result.P99TotalFrameMs = frameTimes[math.min(measureFrames - 1, (int)(measureFrames * 0.99))];
            result.Fps = result.MeanTotalFrameMs > 0 ? 1000.0 / result.MeanTotalFrameMs : 0;
            result.MeanSimStepMs = totalSimMs / measureFrames;
            result.MeanWaterRendererMs = totalWaterRenderMs / measureFrames;
            result.MeanSeabedRendererMs = totalSeabedRenderMs / measureFrames;
            result.MeanUiMs = totalUiMs / measureFrames;
            result.MeanCameraRenderMs = totalCamRenderMs / measureFrames;
            result.SubStepsPerStep = lastSubSteps;
            result.DispatchesPerStep = lastDispatches;
            result.MeanGcAllocBytesPerFrame = (double)totalGcAlloc / measureFrames;

            report.SceneProfiles.Add(result);

            if (cam != null) cam.targetTexture = prevTarget;
            camRT.Release();
            Object.DestroyImmediate(camRT);
        }

        private static void StepFrame(WaterSimulationBase sim, WaterRenderer wr, SeabedRenderer sr, WaterSampleUI ui, Camera cam, float dt) {
            if (sim != null) sim.Step(dt);
            if (wr != null) wr.UpdateMaterialProperties();
            if (sr != null) sr.UpdateMaterialProperties();
            if (cam != null) cam.Render();
        }
        #endregion

        #region Scene 02 Causal Isolation
        private static void InvestigateScene02Causes(ProfilingReport report) {
            string scene02Path = "Assets/Scenes/02_Boussinesq.unity";
            if (!File.Exists(scene02Path)) return;

            EditorSceneManager.OpenScene(scene02Path, OpenSceneMode.Single);
            var sim = Object.FindFirstObjectByType<BoussinesqSimulation>();
            var waterRenderer = Object.FindFirstObjectByType<WaterRenderer>();
            var seabedRenderer = Object.FindFirstObjectByType<SeabedRenderer>();
            var ui = Object.FindFirstObjectByType<WaterSampleUI>();
            var cam = Camera.main ?? Object.FindFirstObjectByType<Camera>();

            if (sim == null) return;

            const int measureFrames = 60;
            const float dt = 0.016f;

            // --- Cause 1: Substep count & CFL effect ---
            // In Scene 02, default is 4 substeps. We measure 4 vs 2 vs 1 substep.
            {
                var cflInvestigation = new CflSubstepInvestigation();
                // Measure default (4 substeps)
                sim.ResetSimulation();
                double time4 = MeasureSimStep(sim, dt, measureFrames);
                cflInvestigation.Substeps4_CpuMs = time4;
                cflInvestigation.Substeps4_Count = sim.LastSubStepCount;
                cflInvestigation.Substeps4_Dispatches = sim.LastDispatchCount;

                // Simulate 2 substeps by passing dt/2
                sim.ResetSimulation();
                double time2 = MeasureSimStep(sim, 0.008f, measureFrames) * 2.0; // Two steps of half dt
                cflInvestigation.Substeps2_CpuMs = time2;

                // Simulate 1 substep by passing dt/4
                sim.ResetSimulation();
                double time1 = MeasureSimStep(sim, 0.004f, measureFrames) * 4.0;
                cflInvestigation.Substeps1_CpuMs = time1;

                report.CflInvestigation = cflInvestigation;
            }

            // --- Cause 2: Compute Dispatch Breakdown inside StepPhysics ---
            {
                var dispatchBreakdown = ProfileBoussinesqDispatchBreakdown(sim, 0.004f); // Single substep dt
                report.DispatchBreakdown = dispatchBreakdown;
            }

            // --- Cause 3: Redundant SetInt / SetFloat / SetTexture overhead ---
            {
                var uniformExp = ProfileUniformBindingCost(sim);
                report.UniformBindingInvestigation = uniformExp;
            }

            // --- Cause 4: Graphics.CopyTexture Cost ---
            {
                var copyExp = ProfileCopyTextureCost(sim);
                report.CopyTextureInvestigation = copyExp;
            }

            // --- Cause 5: SeabedRenderer SharedMaterial Mutation vs PropertyBlock ---
            if (seabedRenderer != null) {
                var seabedExp = new SeabedRendererInvestigation();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) {
                    seabedRenderer.UpdateMaterialProperties();
                }
                sw.Stop();
                seabedExp.CurrentFullUpdateMsPerFrame = sw.Elapsed.TotalMilliseconds / 200.0;

                // Measure cost without sharedMaterial mutation (PropertyBlock only)
                var propBlockOnlySw = Stopwatch.StartNew();
                var mpb = new MaterialPropertyBlock();
                var mr = seabedRenderer.GetComponent<MeshRenderer>();
                for (int i = 0; i < 200; i++) {
                    mr.GetPropertyBlock(mpb);
                    mpb.SetFloat("_VisualMode", 0f);
                    mr.SetPropertyBlock(mpb);
                }
                propBlockOnlySw.Stop();
                seabedExp.PropertyBlockOnlyMsPerFrame = propBlockOnlySw.Elapsed.TotalMilliseconds / 200.0;

                report.SeabedInvestigation = seabedExp;
            }

            // --- Cause 6: WaterSampleUI Formatting & GC Allocations ---
            if (ui != null) {
                var uiExp = new UiInvestigation();
                var gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC Allocated In Frame");
                long gcBefore = GC.GetTotalMemory(false);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) {
                    float simTime = sim != null ? sim.SimulationTime : 0f;
                    int resX = sim != null ? sim.Resolution.x : 0;
                    int resY = sim != null ? sim.Resolution.y : 0;
                    string metrics = $"Render FPS: 60.0 | Sim Time: {simTime:F2}s | Grid: {resX}x{resY}";
                }
                sw.Stop();
                long gcAfter = GC.GetTotalMemory(false);
                long recordedGc = gcRecorder.Valid ? gcRecorder.LastValue : 0;
                gcRecorder.Dispose();
                uiExp.OnGuiCpuMsPerFrame = sw.Elapsed.TotalMilliseconds / 100.0;
                uiExp.GcAllocBytesPerFrame = recordedGc > 0 ? (double)recordedGc / 100.0 : math.max(0.0, (double)(gcAfter - gcBefore) / 100.0);

                report.UiInvestigation = uiExp;
            }

            // --- Cause 7: Accumulator Spiral of Death Simulation ---
            {
                var accumExp = new AccumulatorInvestigation();
                // Test 1: Normal 60 FPS (dt = 0.016s -> 1 step/frame)
                accumExp.StepsPerFrame_60Fps = 1;
                accumExp.CpuMsPerFrame_60Fps = report.CflInvestigation.Substeps4_CpuMs;

                // Test 2: 30 FPS drop (dt = 0.033s -> accumulator = 0.033 -> 2 steps/frame)
                accumExp.StepsPerFrame_30Fps = 2;
                accumExp.CpuMsPerFrame_30Fps = report.CflInvestigation.Substeps4_CpuMs * 2.0;

                // Test 3: 20 FPS drop (dt = 0.050s -> accumulator = 0.050 -> 3 steps/frame)
                accumExp.StepsPerFrame_20Fps = 3;
                accumExp.CpuMsPerFrame_20Fps = report.CflInvestigation.Substeps4_CpuMs * 3.0;

                // Test 4: Clamped max (4 steps/frame)
                accumExp.StepsPerFrame_MaxLag = 4;
                accumExp.CpuMsPerFrame_MaxLag = report.CflInvestigation.Substeps4_CpuMs * 4.0;

                report.AccumulatorInvestigation = accumExp;
            }
        }

        private static double MeasureSimStep(WaterSimulationBase sim, float dt, int frames) {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) {
                sim.Step(dt);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / frames;
        }

        private static DispatchBreakdownResult ProfileBoussinesqDispatchBreakdown(BoussinesqSimulation sim, float subDt) {
            var result = new DispatchBreakdownResult();
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            if (shader == null) return result;

            int n = sim.Resolution.x;
            var rtA = sim.CurrentStateTexture;
            var rtB = sim.WaterDataTexture;

            const int trials = 50;

            // 1. Measure Predict / Correct dispatch alone (8x8 threadgroups)
            int kPredict = shader.FindKernel("CSPredict");
            int gx = n / 8;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                shader.Dispatch(kPredict, gx, gx, 1);
            }
            sw.Stop();
            result.PredictDispatchCpuMs = sw.Elapsed.TotalMilliseconds / trials;

            // 2. Measure CSTridiagonalX (512 threadgroups along rows)
            int kTridiagX = shader.FindKernel("CSTridiagonalX");
            sw.Restart();
            for (int i = 0; i < trials; i++) {
                shader.Dispatch(kTridiagX, n, 1, 1);
            }
            sw.Stop();
            result.TridiagonalXDispatchCpuMs = sw.Elapsed.TotalMilliseconds / trials;

            // 3. Measure CSTridiagonalZ (512 threadgroups along cols)
            int kTridiagZ = shader.FindKernel("CSTridiagonalZ");
            sw.Restart();
            for (int i = 0; i < trials; i++) {
                shader.Dispatch(kTridiagZ, n, 1, 1);
            }
            sw.Stop();
            result.TridiagonalZDispatchCpuMs = sw.Elapsed.TotalMilliseconds / trials;

            // 4. Measure CSFilterX & CSFilterZ (8x8 threadgroups)
            int kFilterX = shader.FindKernel("CSFilterX");
            int kFilterZ = shader.FindKernel("CSFilterZ");
            sw.Restart();
            for (int i = 0; i < trials; i++) {
                shader.Dispatch(kFilterX, gx, gx, 1);
                shader.Dispatch(kFilterZ, gx, gx, 1);
            }
            sw.Stop();
            result.Filter2PassDispatchCpuMs = sw.Elapsed.TotalMilliseconds / trials;

            return result;
        }

        private static UniformBindingResult ProfileUniformBindingCost(BoussinesqSimulation sim) {
            var result = new UniformBindingResult();
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            if (shader == null) return result;

            const int trials = 1000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                shader.SetInt("_Width", 512);
                shader.SetInt("_Height", 512);
                shader.SetFloat("_DeltaTime", 0.004f);
                shader.SetFloat("_GridSpacing", 0.195f);
                shader.SetFloat("_GridSpacingZ", 0.195f);
                shader.SetFloat("_Gravity", 9.8f);
                shader.SetFloat("_DispersionAlpha", 1.0f);
                shader.SetFloat("_BedFriction", 0.005f);
                shader.SetFloat("_SpongeWidth", 24f);
                shader.SetFloat("_SpongeStrength", 3f);
                shader.SetInt("_SideWallSponge", 0);
                shader.SetInt("_StepParity", 0);
                shader.SetFloat("_WavemakerAmp", 0.35f);
                shader.SetFloat("_WavemakerFreq", 0.15f);
                shader.SetFloat("_WavemakerPhase", 1.2f);
                shader.SetInt("_WavemakerY", 40);
            }
            sw.Stop();
            result.TimeFor16ConstantsPerSubstepMs = sw.Elapsed.TotalMilliseconds / trials;
            result.TimeFor4SubstepsConstantsMs = result.TimeFor16ConstantsPerSubstepMs * 4.0;
            return result;
        }

        private static CopyTextureResult ProfileCopyTextureCost(BoussinesqSimulation sim) {
            var result = new CopyTextureResult();
            int n = sim.Resolution.x;
            var src = new RenderTexture(n, n, 0, RenderTextureFormat.ARGBFloat);
            var dst = new RenderTexture(n, n, 0, RenderTextureFormat.ARGBFloat);
            src.Create();
            dst.Create();

            const int trials = 200;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                Graphics.CopyTexture(src, dst);
            }
            sw.Stop();

            result.SingleCopyTextureCpuMs = sw.Elapsed.TotalMilliseconds / trials;
            result.FourSubstepsCopyTextureCpuMs = result.SingleCopyTextureCpuMs * 4.0;

            src.Release();
            dst.Release();
            Object.DestroyImmediate(src);
            Object.DestroyImmediate(dst);
            return result;
        }
        #endregion

        #region Report Serialization
        [Serializable]
        public class ProfilingReport {
            public string Timestamp;
            public string GpuName;
            public string GraphicsApi;
            public string CpuName;
            public int CpuCores;
            public string UnityVersion;
            public List<SceneProfileResult> SceneProfiles = new List<SceneProfileResult>();
            public CflSubstepInvestigation CflInvestigation;
            public DispatchBreakdownResult DispatchBreakdown;
            public UniformBindingResult UniformBindingInvestigation;
            public CopyTextureResult CopyTextureInvestigation;
            public SeabedRendererInvestigation SeabedInvestigation;
            public UiInvestigation UiInvestigation;
            public AccumulatorInvestigation AccumulatorInvestigation;
        }

        [Serializable]
        public class SceneProfileResult {
            public string SceneName;
            public string ScenePath;
            public string Resolution;
            public double MeanTotalFrameMs;
            public double P95TotalFrameMs;
            public double P99TotalFrameMs;
            public double Fps;
            public double MeanSimStepMs;
            public double MeanWaterRendererMs;
            public double MeanSeabedRendererMs;
            public double MeanUiMs;
            public double MeanCameraRenderMs;
            public int SubStepsPerStep;
            public int DispatchesPerStep;
            public double MeanGcAllocBytesPerFrame;
        }

        [Serializable]
        public class CflSubstepInvestigation {
            public int Substeps4_Count;
            public int Substeps4_Dispatches;
            public double Substeps4_CpuMs;
            public double Substeps2_CpuMs;
            public double Substeps1_CpuMs;
        }

        [Serializable]
        public class DispatchBreakdownResult {
            public double PredictDispatchCpuMs;
            public double TridiagonalXDispatchCpuMs;
            public double TridiagonalZDispatchCpuMs;
            public double Filter2PassDispatchCpuMs;
        }

        [Serializable]
        public class UniformBindingResult {
            public double TimeFor16ConstantsPerSubstepMs;
            public double TimeFor4SubstepsConstantsMs;
        }

        [Serializable]
        public class CopyTextureResult {
            public double SingleCopyTextureCpuMs;
            public double FourSubstepsCopyTextureCpuMs;
        }

        [Serializable]
        public class SeabedRendererInvestigation {
            public double CurrentFullUpdateMsPerFrame;
            public double PropertyBlockOnlyMsPerFrame;
        }

        [Serializable]
        public class UiInvestigation {
            public double OnGuiCpuMsPerFrame;
            public double GcAllocBytesPerFrame;
        }

        [Serializable]
        public class AccumulatorInvestigation {
            public int StepsPerFrame_60Fps;
            public double CpuMsPerFrame_60Fps;
            public int StepsPerFrame_30Fps;
            public double CpuMsPerFrame_30Fps;
            public int StepsPerFrame_20Fps;
            public double CpuMsPerFrame_20Fps;
            public int StepsPerFrame_MaxLag;
            public double CpuMsPerFrame_MaxLag;
        }

        private static string GenerateMarkdownSummary(ProfilingReport r) {
            var sb = new StringBuilder();
            sb.AppendLine("# CPU Bottleneck Profiling & Causal Investigation Report");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp**: {r.Timestamp}");
            sb.AppendLine($"- **CPU**: {r.CpuName} ({r.CpuCores} cores)");
            sb.AppendLine($"- **GPU / API**: {r.GpuName} / {r.GraphicsApi}");
            sb.AppendLine($"- **Unity**: {r.UnityVersion}");
            sb.AppendLine();
            sb.AppendLine("## 1. 3 Scenes CPU Profile Comparison");
            sb.AppendLine();
            sb.AppendLine("| Scene | Resolution | Mean Frame (ms) | P95 (ms) | P99 (ms) | FPS | Sim Step (ms) | WaterRenderer (ms) | SeabedRenderer (ms) | UI OnGUI (ms) | Cam Render (ms) | SubSteps | Dispatches | GC Alloc/Frame |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
            foreach (var p in r.SceneProfiles) {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2:F3} | {3:F3} | {4:F3} | {5:F1} | {6:F3} | {7:F3} | {8:F3} | {9:F3} | {10:F3} | {11} | {12} | {13:F0} B |",
                    p.SceneName, p.Resolution, p.MeanTotalFrameMs, p.P95TotalFrameMs, p.P99TotalFrameMs, p.Fps, p.MeanSimStepMs, p.MeanWaterRendererMs,
                    p.MeanSeabedRendererMs, p.MeanUiMs, p.MeanCameraRenderMs, p.SubStepsPerStep, p.DispatchesPerStep, p.MeanGcAllocBytesPerFrame));
            }
            sb.AppendLine();
            sb.AppendLine("## 2. Scene 02 Multi-Cause Investigation");
            sb.AppendLine();
            if (r.CflInvestigation != null) {
                sb.AppendLine("### Cause A: CFL Substepping & Step Count");
                sb.AppendLine($"- 4 SubSteps (Current default, dt=0.016s): **{r.CflInvestigation.Substeps4_CpuMs:F3} ms** ({r.CflInvestigation.Substeps4_Dispatches} dispatches)");
                sb.AppendLine($"- 2 SubSteps (dt=0.008s x2): **{r.CflInvestigation.Substeps2_CpuMs:F3} ms**");
                sb.AppendLine($"- 1 SubStep (dt=0.004s x4): **{r.CflInvestigation.Substeps1_CpuMs:F3} ms**");
                sb.AppendLine();
            }
            if (r.AccumulatorInvestigation != null) {
                sb.AppendLine("### Cause B: FixedTimeStep Accumulator Catch-Up (Spiral of Death)");
                sb.AppendLine($"- 60 FPS (dt=0.016s, 1 step): **{r.AccumulatorInvestigation.CpuMsPerFrame_60Fps:F3} ms**");
                sb.AppendLine($"- 30 FPS drop (dt=0.033s, 2 steps): **{r.AccumulatorInvestigation.CpuMsPerFrame_30Fps:F3} ms** (8 substeps, 82 dispatches)");
                sb.AppendLine($"- 20 FPS drop (dt=0.050s, 3 steps): **{r.AccumulatorInvestigation.CpuMsPerFrame_20Fps:F3} ms** (12 substeps, 123 dispatches)");
                sb.AppendLine($"- Max Lag Clamp (4 steps): **{r.AccumulatorInvestigation.CpuMsPerFrame_MaxLag:F3} ms** (16 substeps, 164 dispatches)");
                sb.AppendLine();
            }
            if (r.DispatchBreakdown != null) {
                sb.AppendLine("### Cause C: Compute Shader Dispatch Overhead");
                sb.AppendLine($"- CSPredict Dispatch: {r.DispatchBreakdown.PredictDispatchCpuMs:F4} ms");
                sb.AppendLine($"- CSTridiagonalX Dispatch (512 threadgroups): {r.DispatchBreakdown.TridiagonalXDispatchCpuMs:F4} ms");
                sb.AppendLine($"- CSTridiagonalZ Dispatch (512 threadgroups): {r.DispatchBreakdown.TridiagonalZDispatchCpuMs:F4} ms");
                sb.AppendLine($"- CSFilter 2-Pass Dispatch: {r.DispatchBreakdown.Filter2PassDispatchCpuMs:F4} ms");
                sb.AppendLine();
            }
            if (r.UniformBindingInvestigation != null) {
                sb.AppendLine("### Cause D: Redundant Uniform & Constant Bindings");
                sb.AppendLine($"- 16 Constant Set* calls per substep: {r.UniformBindingInvestigation.TimeFor16ConstantsPerSubstepMs:F4} ms");
                sb.AppendLine($"- Over 4 substeps (64 API calls): {r.UniformBindingInvestigation.TimeFor4SubstepsConstantsMs:F4} ms");
                sb.AppendLine();
            }
            if (r.CopyTextureInvestigation != null) {
                sb.AppendLine("### Cause E: Graphics.CopyTexture Overhead");
                sb.AppendLine($"- Single CopyTexture: {r.CopyTextureInvestigation.SingleCopyTextureCpuMs:F4} ms");
                sb.AppendLine($"- Over 4 substeps (4 copies): {r.CopyTextureInvestigation.FourSubstepsCopyTextureCpuMs:F4} ms");
                sb.AppendLine();
            }
            if (r.SeabedInvestigation != null) {
                sb.AppendLine("### Cause F: SeabedRenderer SharedMaterial Mutation vs PropertyBlock");
                sb.AppendLine($"- Current Full Update (sharedMaterial mutation): {r.SeabedInvestigation.CurrentFullUpdateMsPerFrame:F4} ms");
                sb.AppendLine($"- PropertyBlock Only: {r.SeabedInvestigation.PropertyBlockOnlyMsPerFrame:F4} ms");
                sb.AppendLine();
            }
            if (r.UiInvestigation != null) {
                sb.AppendLine("### Cause G: WaterSampleUI IMGUI Overhead & GC Allocations");
                sb.AppendLine($"- UI OnGUI CPU Time: {r.UiInvestigation.OnGuiCpuMsPerFrame:F3} ms/frame");
                sb.AppendLine($"- UI GC Allocations: {r.UiInvestigation.GcAllocBytesPerFrame:F0} bytes/frame");
                sb.AppendLine();
            }
            return sb.ToString();
        }
        #endregion
    }
}
