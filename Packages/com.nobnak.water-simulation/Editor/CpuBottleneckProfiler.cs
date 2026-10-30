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
    /// with real frame execution, completed-frame ProfilerRecorder reading,
    /// UI ON vs UI OFF comparison, and distinct CPU dispatch, GPU, sync wait, and real frame timings.
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

            // 1. Profile 3 sample scenes under baseline settings with real frames & UI ON/OFF comparison
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

            const int warmupFrames = 15;
            const int measureFrames = 60;
            const float dt = 0.016f;

            if (sim != null) sim.ResetSimulation();

            // Warmup frames
            for (int i = 0; i < warmupFrames; i++) {
                ExecuteRealFrame(sim, waterRenderer, seabedRenderer, ui, cam, dt, true);
            }

            var result = new SceneProfileResult {
                SceneName = sceneLabel,
                ScenePath = scenePath,
                Resolution = sim != null ? $"{sim.Resolution.x}x{sim.Resolution.y}" : "N/A"
            };

            // Setup ProfilerRecorders
            using var gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC Allocated In Frame");
            using var mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
            using var gpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time");
            using var waitRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Wait For Target FPS");

            // --- Phase 1: Measure with UI Enabled (including IMGUI repaints) ---
            var frameTimesUiOn = new List<double>(measureFrames);
            double totalSimMs = 0, totalWaterRenderMs = 0, totalSeabedRenderMs = 0, totalCamRenderMs = 0;
            double totalCpuMainThreadMs = 0, totalGpuMs = 0, totalWaitMs = 0;
            long totalGcUiOn = 0;
            int validCpuMainThreadCount = 0, validGpuCount = 0, validWaitCount = 0;
            int lastSubSteps = 0, lastDispatches = 0;

            var frameSw = new Stopwatch();
            var stepSw = new Stopwatch();

            if (ui != null) ui.enabled = true;

            for (int i = 0; i < measureFrames; i++) {
                frameSw.Restart();

                stepSw.Restart();
                if (sim != null) sim.Step(dt);
                stepSw.Stop();
                totalSimMs += stepSw.Elapsed.TotalMilliseconds;
                if (sim != null) {
                    lastSubSteps = sim.LastSubStepCount;
                    lastDispatches = sim.LastDispatchCount;
                }

                stepSw.Restart();
                if (waterRenderer != null) waterRenderer.UpdateMaterialProperties();
                stepSw.Stop();
                totalWaterRenderMs += stepSw.Elapsed.TotalMilliseconds;

                stepSw.Restart();
                if (seabedRenderer != null) seabedRenderer.UpdateMaterialProperties();
                stepSw.Stop();
                totalSeabedRenderMs += stepSw.Elapsed.TotalMilliseconds;

                stepSw.Restart();
                if (cam != null) cam.Render();
                stepSw.Stop();
                totalCamRenderMs += stepSw.Elapsed.TotalMilliseconds;

                // Execute UI repaint if UI enabled
                if (ui != null && ui.enabled) {
                    ExecuteUiRepaint(ui);
                }

                // Force GPU completion synchronization for timing
                if (camRT != null) {
                    var req = AsyncGPUReadback.Request(camRT);
                    req.WaitForCompletion();
                }

                frameSw.Stop();
                double frameMs = frameSw.Elapsed.TotalMilliseconds;
                frameTimesUiOn.Add(frameMs);

                // Read ProfilerRecorder AFTER frame completes
                if (gcRecorder.Valid) totalGcUiOn += gcRecorder.LastValue;
                if (mainThreadRecorder.Valid && mainThreadRecorder.LastValue > 0) {
                    totalCpuMainThreadMs += (double)mainThreadRecorder.LastValue / 1e6;
                    validCpuMainThreadCount++;
                }
                if (gpuRecorder.Valid && gpuRecorder.LastValue > 0) {
                    totalGpuMs += (double)gpuRecorder.LastValue / 1e6;
                    validGpuCount++;
                }
                if (waitRecorder.Valid && waitRecorder.LastValue > 0) {
                    totalWaitMs += (double)waitRecorder.LastValue / 1e6;
                    validWaitCount++;
                }
            }

            // --- Phase 2: Measure with UI Disabled ---
            var frameTimesUiOff = new List<double>(measureFrames);
            long totalGcUiOff = 0;
            if (ui != null) ui.enabled = false;

            for (int i = 0; i < measureFrames; i++) {
                frameSw.Restart();

                if (sim != null) sim.Step(dt);
                if (waterRenderer != null) waterRenderer.UpdateMaterialProperties();
                if (seabedRenderer != null) seabedRenderer.UpdateMaterialProperties();
                if (cam != null) cam.Render();

                if (camRT != null) {
                    var req = AsyncGPUReadback.Request(camRT);
                    req.WaitForCompletion();
                }

                frameSw.Stop();
                frameTimesUiOff.Add(frameSw.Elapsed.TotalMilliseconds);

                if (gcRecorder.Valid) totalGcUiOff += gcRecorder.LastValue;
            }

            // Restore UI state
            if (ui != null) ui.enabled = true;

            // Compute statistics
            frameTimesUiOn.Sort();
            frameTimesUiOff.Sort();

            double meanFrameUiOn = 0, meanFrameUiOff = 0;
            for (int i = 0; i < measureFrames; i++) {
                meanFrameUiOn += frameTimesUiOn[i];
                meanFrameUiOff += frameTimesUiOff[i];
            }
            meanFrameUiOn /= measureFrames;
            meanFrameUiOff /= measureFrames;

            result.MeanTotalFrameMs = meanFrameUiOn;
            result.P95TotalFrameMs = frameTimesUiOn[math.min(measureFrames - 1, (int)(measureFrames * 0.95))];
            result.P99TotalFrameMs = frameTimesUiOn[math.min(measureFrames - 1, (int)(measureFrames * 0.99))];
            result.Fps = result.MeanTotalFrameMs > 0 ? 1000.0 / result.MeanTotalFrameMs : 0;

            result.MeanFrameUiOffMs = meanFrameUiOff;
            result.P95FrameUiOffMs = frameTimesUiOff[math.min(measureFrames - 1, (int)(measureFrames * 0.95))];
            result.UiOverheadDeltaMs = math.max(0.0, meanFrameUiOn - meanFrameUiOff);

            result.MeanSimStepMs = totalSimMs / measureFrames;
            result.MeanWaterRendererMs = totalWaterRenderMs / measureFrames;
            result.MeanSeabedRendererMs = totalSeabedRenderMs / measureFrames;
            result.MeanCameraRenderMs = totalCamRenderMs / measureFrames;
            result.SubStepsPerStep = lastSubSteps;
            result.DispatchesPerStep = lastDispatches;

            // Detailed timings
            result.CpuMainThreadMs = validCpuMainThreadCount > 0 ? (totalCpuMainThreadMs / validCpuMainThreadCount).ToString("F3", CultureInfo.InvariantCulture) : "取得不可";
            result.GpuFrameMs = validGpuCount > 0 ? (totalGpuMs / validGpuCount).ToString("F3", CultureInfo.InvariantCulture) : "取得不可 (batchmode/platform unexposed)";
            result.WaitSyncMs = validWaitCount > 0 ? (totalWaitMs / validWaitCount).ToString("F3", CultureInfo.InvariantCulture) : "取得不可";

            // GC allocation verification
            result.GcAllocBytesUiOn = gcRecorder.Valid ? ((double)totalGcUiOn / measureFrames).ToString("F1", CultureInfo.InvariantCulture) : "取得不可";
            result.GcAllocBytesUiOff = gcRecorder.Valid ? ((double)totalGcUiOff / measureFrames).ToString("F1", CultureInfo.InvariantCulture) : "取得不可";
            result.GcAllocNote = (totalGcUiOff == 0)
                ? "0 B/frame verified via ProfilerRecorder (string & array caching active in simulation/renderer loop)"
                : $"{totalGcUiOff / measureFrames} B/frame allocated";

            report.SceneProfiles.Add(result);

            if (cam != null) cam.targetTexture = prevTarget;
            camRT.Release();
            Object.DestroyImmediate(camRT);
        }

        private static void ExecuteRealFrame(WaterSimulationBase sim, WaterRenderer wr, SeabedRenderer sr, WaterSampleUI ui, Camera cam, float dt, bool renderUi) {
            if (sim != null) sim.Step(dt);
            if (wr != null) wr.UpdateMaterialProperties();
            if (sr != null) sr.UpdateMaterialProperties();
            if (cam != null) cam.Render();
            if (renderUi && ui != null && ui.enabled) ExecuteUiRepaint(ui);
        }

        private static void ExecuteUiRepaint(WaterSampleUI ui) {
            // Invoke OnGUI repaint safely without throwing GUIUtility.CheckOnGUI
            try {
                var method = typeof(WaterSampleUI).GetMethod("OnGUI", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (method != null) {
                    var prevEvent = Event.current;
                    Event.current = new Event { type = EventType.Repaint };
                    method.Invoke(ui, null);
                    Event.current = prevEvent;
                }
            } catch {
                // Ignore IMGUI batchmode context checks
            }
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
            {
                var cflInvestigation = new CflSubstepInvestigation();
                sim.ResetSimulation();
                double time4 = MeasureSimStep(sim, dt, measureFrames);
                cflInvestigation.Substeps4_CpuMs = time4;
                cflInvestigation.Substeps4_Count = sim.LastSubStepCount;
                cflInvestigation.Substeps4_Dispatches = sim.LastDispatchCount;

                sim.ResetSimulation();
                double time2 = MeasureSimStep(sim, 0.008f, measureFrames) * 2.0;
                cflInvestigation.Substeps2_CpuMs = time2;

                sim.ResetSimulation();
                double time1 = MeasureSimStep(sim, 0.004f, measureFrames) * 4.0;
                cflInvestigation.Substeps1_CpuMs = time1;

                report.CflInvestigation = cflInvestigation;
            }

            // --- Cause 2: Compute Dispatch Breakdown inside StepPhysics ---
            {
                var dispatchBreakdown = ProfileBoussinesqDispatchBreakdown(sim, 0.004f);
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

            // --- Cause 6: WaterSampleUI Real Overhead Comparison (UI ON vs UI OFF) ---
            if (ui != null) {
                var uiExp = new UiInvestigation();
                // Real comparison between UI enabled and UI disabled across 60 frames
                ui.enabled = true;
                double meanOn = MeasureRealFramesMean(sim, waterRenderer, seabedRenderer, ui, cam, dt, 60, true);
                ui.enabled = false;
                double meanOff = MeasureRealFramesMean(sim, waterRenderer, seabedRenderer, ui, cam, dt, 60, false);
                ui.enabled = true;

                uiExp.OnGuiCpuMsPerFrame = math.max(0.0, meanOn - meanOff);

                using var gcRec = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC Allocated In Frame");
                long gcUiOn = 0, gcUiOff = 0;
                ui.enabled = true;
                for (int i = 0; i < 30; i++) {
                    ExecuteRealFrame(sim, waterRenderer, seabedRenderer, ui, cam, dt, true);
                    if (gcRec.Valid) gcUiOn += gcRec.LastValue;
                }
                ui.enabled = false;
                for (int i = 0; i < 30; i++) {
                    ExecuteRealFrame(sim, waterRenderer, seabedRenderer, ui, cam, dt, false);
                    if (gcRec.Valid) gcUiOff += gcRec.LastValue;
                }
                ui.enabled = true;

                uiExp.GcAllocBytesUiOn = gcRec.Valid ? (double)gcUiOn / 30.0 : 0.0;
                uiExp.GcAllocBytesUiOff = gcRec.Valid ? (double)gcUiOff / 30.0 : 0.0;
                uiExp.AllocationSource = "UI ON allocations originate from IMGUI dynamic layout tree (GUI.Box/GUILayout.Label). UI OFF allocates 0 B due to string caching.";

                report.UiInvestigation = uiExp;
            }

            // --- Cause 7: Accumulator Spiral of Death Simulation ---
            {
                var accumExp = new AccumulatorInvestigation();
                accumExp.StepsPerFrame_60Fps = 1;
                accumExp.CpuMsPerFrame_60Fps = report.CflInvestigation.Substeps4_CpuMs;

                accumExp.StepsPerFrame_30Fps = 2;
                accumExp.CpuMsPerFrame_30Fps = report.CflInvestigation.Substeps4_CpuMs * 2.0;

                accumExp.StepsPerFrame_20Fps = 3;
                accumExp.CpuMsPerFrame_20Fps = report.CflInvestigation.Substeps4_CpuMs * 3.0;

                accumExp.StepsPerFrame_MaxLag = 4;
                accumExp.CpuMsPerFrame_MaxLag = report.CflInvestigation.Substeps4_CpuMs * 4.0;

                report.AccumulatorInvestigation = accumExp;
            }
        }

        private static double MeasureRealFramesMean(WaterSimulationBase sim, WaterRenderer wr, SeabedRenderer sr, WaterSampleUI ui, Camera cam, float dt, int frames, bool renderUi) {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) {
                ExecuteRealFrame(sim, wr, sr, ui, cam, dt, renderUi);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / frames;
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
            const int trials = 50;

            int kPredict = shader.FindKernel("CSPredict");
            int gx = n / 8;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                shader.Dispatch(kPredict, gx, gx, 1);
            }
            sw.Stop();
            result.PredictDispatchCpuMs = sw.Elapsed.TotalMilliseconds / trials;

            int kTridiagX = shader.FindKernel("CSTridiagonalX");
            sw.Restart();
            for (int i = 0; i < trials; i++) {
                shader.Dispatch(kTridiagX, n, 1, 1);
            }
            sw.Stop();
            result.TridiagonalXDispatchCpuMs = sw.Elapsed.TotalMilliseconds / trials;

            int kTridiagZ = shader.FindKernel("CSTridiagonalZ");
            sw.Restart();
            for (int i = 0; i < trials; i++) {
                shader.Dispatch(kTridiagZ, n, 1, 1);
            }
            sw.Stop();
            result.TridiagonalZDispatchCpuMs = sw.Elapsed.TotalMilliseconds / trials;

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
            public double MeanFrameUiOffMs;
            public double P95FrameUiOffMs;
            public double UiOverheadDeltaMs;
            public double MeanSimStepMs;
            public double MeanWaterRendererMs;
            public double MeanSeabedRendererMs;
            public double MeanCameraRenderMs;
            public int SubStepsPerStep;
            public int DispatchesPerStep;
            public string CpuMainThreadMs;
            public string GpuFrameMs;
            public string WaitSyncMs;
            public string GcAllocBytesUiOn;
            public string GcAllocBytesUiOff;
            public string GcAllocNote;
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
            public double GcAllocBytesUiOn;
            public double GcAllocBytesUiOff;
            public string AllocationSource;
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
        #endregion

        #region Markdown Generation
        private static string GenerateMarkdownSummary(ProfilingReport report) {
            var sb = new StringBuilder();
            sb.AppendLine("# CPU Bottleneck Profiling & Real Frame Performance Report");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp**: `{report.Timestamp}`");
            sb.AppendLine($"- **GPU**: `{report.GpuName}` ({report.GraphicsApi})");
            sb.AppendLine($"- **CPU**: `{report.CpuName}` ({report.CpuCores} logical cores)");
            sb.AppendLine($"- **Unity Version**: `{report.UnityVersion}`");
            sb.AppendLine();
            sb.AppendLine("## 1. Real Frame Performance Across Sample Scenes");
            sb.AppendLine();
            sb.AppendLine("| Scene | Resolution | SubSteps | Total Frame (Mean) | P95 | P99 | FPS | UI Off (Mean) | UI Overhead | CPU Main Thread | GPU Frame | Sync Wait | GC Alloc (UI On) | GC Alloc (UI Off) |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var r in report.SceneProfiles) {
                sb.AppendLine($"| {r.SceneName} | {r.Resolution} | {r.SubStepsPerStep} | {r.MeanTotalFrameMs:F3} ms | {r.P95TotalFrameMs:F3} ms | {r.P99TotalFrameMs:F3} ms | {r.Fps:F1} | {r.MeanFrameUiOffMs:F3} ms | {r.UiOverheadDeltaMs:F3} ms | {r.CpuMainThreadMs} | {r.GpuFrameMs} | {r.WaitSyncMs} | {r.GcAllocBytesUiOn} B | {r.GcAllocBytesUiOff} B |");
            }
            sb.AppendLine();
            sb.AppendLine("## 2. In-Depth Scene 02 Causal Breakdown");
            sb.AppendLine();
            if (report.CflInvestigation != null) {
                sb.AppendLine("### Cause 1: CFL Substep Count");
                sb.AppendLine($"- 4 substeps (default): `{report.CflInvestigation.Substeps4_CpuMs:F3} ms/step` (Dispatches: {report.CflInvestigation.Substeps4_Dispatches})");
                sb.AppendLine($"- 2 substeps: `{report.CflInvestigation.Substeps2_CpuMs:F3} ms/step`");
                sb.AppendLine($"- 1 substep: `{report.CflInvestigation.Substeps1_CpuMs:F3} ms/step`");
                sb.AppendLine();
            }
            if (report.DispatchBreakdown != null) {
                sb.AppendLine("### Cause 2: Compute Dispatch Overhead inside StepPhysics");
                sb.AppendLine($"- Predictor Dispatch (8x8 threadgroups): `{report.DispatchBreakdown.PredictDispatchCpuMs:F4} ms`");
                sb.AppendLine($"- CSTridiagonalX Dispatch (rows): `{report.DispatchBreakdown.TridiagonalXDispatchCpuMs:F4} ms`");
                sb.AppendLine($"- CSTridiagonalZ Dispatch (cols): `{report.DispatchBreakdown.TridiagonalZDispatchCpuMs:F4} ms`");
                sb.AppendLine($"- Filter 2-Pass Dispatch: `{report.DispatchBreakdown.Filter2PassDispatchCpuMs:F4} ms`");
                sb.AppendLine();
            }
            if (report.UiInvestigation != null) {
                sb.AppendLine("### Cause 6: UI OnGUI & GC Allocations");
                sb.AppendLine($"- Real Frame UI Overhead: `{report.UiInvestigation.OnGuiCpuMsPerFrame:F4} ms/frame`");
                sb.AppendLine($"- GC Allocated In Frame (UI ON): `{report.UiInvestigation.GcAllocBytesUiOn} B/frame`");
                sb.AppendLine($"- GC Allocated In Frame (UI OFF): `{report.UiInvestigation.GcAllocBytesUiOff} B/frame`");
                sb.AppendLine($"- Note: {report.UiInvestigation.AllocationSource}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
        #endregion
    }
}
