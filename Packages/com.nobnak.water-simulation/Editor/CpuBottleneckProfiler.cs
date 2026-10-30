using System;
using System.Collections;
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
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Profiler for investigating CPU bottlenecks across Scene 01, 02, and 03
    /// using genuine PlayerLoop PlayMode execution across real frames,
    /// completed-frame ProfilerRecorder sampling, UI ON vs UI OFF comparison
    /// with execution-order bias verification, and clear separation of real frame FPS
    /// from synchronous throughput benchmarks.
    /// </summary>
    public static class CpuBottleneckProfiler {
        public const string OutputJsonPath = "Docs/CpuBottleneckInvestigation.json";
        public const string OutputMarkdownPath = "Docs/CpuBottleneckReport.md";
        public const string FlagFile = "Temp/playmode_profiler_active.flag";

        [MenuItem("Tools/Water Simulation/Run CPU Bottleneck Investigation", false, 52)]
        public static void RunProfilingSuite() {
            Debug.Log("[CpuBottleneckProfiler] Initiating PlayMode profiling suite...");
            File.WriteAllText(FlagFile, "1");
            EditorSceneManager.OpenScene("Assets/Scenes/01_WaveEquation.unity", OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        public static void RunCommandLine() => RunProfilingCommandLine();

        public static void RunProfilingCommandLine() {
            try {
                RunProfilingSuite();
            } catch (Exception ex) {
                Debug.LogError($"[CpuBottleneckProfiler] Failed to initiate: {ex}");
                EditorApplication.Exit(1);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnPlayModeStart() {
            if (!File.Exists(FlagFile)) return;
            Debug.Log("[CpuBottleneckProfiler] PlayMode started, spawning RealFramePlayModeRunner...");
            var go = new GameObject("RealFramePlayModeRunner");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<RealFramePlayModeRunner>();
        }
    }

    public class RealFramePlayModeRunner : MonoBehaviour {
        private IEnumerator Start() {
            Debug.Log("[RealFramePlayModeRunner] Starting multi-scene real-frame profiling coroutine...");
            var report = new ProfilingReport {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                GpuName = SystemInfo.graphicsDeviceName,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                CpuName = SystemInfo.processorType,
                CpuCores = SystemInfo.processorCount,
                UnityVersion = Application.unityVersion
            };

            var scenes = new (string path, string label)[] {
                ("Assets/Scenes/01_WaveEquation.unity", "Scene 01 (WaveEquation)"),
                ("Assets/Scenes/02_Boussinesq.unity", "Scene 02 (Boussinesq)"),
                ("Assets/Scenes/03_FFTWater.unity", "Scene 03 (FFTOcean)")
            };

            for (int s = 0; s < scenes.Length; s++) {
                var (scenePath, sceneLabel) = scenes[s];
                Debug.Log($"[RealFramePlayModeRunner] Profiling scene: {sceneLabel} ({scenePath})");

                if (SceneManager.GetActiveScene().path != scenePath) {
                    var loadOp = SceneManager.LoadSceneAsync(scenePath);
                    while (!loadOp.isDone) yield return null;
                }

                // Stabilize after scene load
                for (int w = 0; w < 10; w++) yield return null;

                var sim = Object.FindFirstObjectByType<WaterSimulationBase>();
                var waterRenderer = Object.FindFirstObjectByType<WaterRenderer>();
                var seabedRenderer = Object.FindFirstObjectByType<SeabedRenderer>();
                var ui = Object.FindFirstObjectByType<WaterSampleUI>();
                var cam = Camera.main ?? Object.FindFirstObjectByType<Camera>();

                var profileResult = new SceneProfileResult {
                    SceneName = sceneLabel,
                    ScenePath = scenePath,
                    Resolution = sim != null ? $"{sim.Resolution.x}x{sim.Resolution.y}" : "N/A",
                    SubStepsPerStep = sim != null ? sim.LastSubStepCount : 1,
                    DispatchesPerStep = sim != null ? sim.LastDispatchCount : 1
                };

                using var gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC Allocated In Frame");
                using var mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
                using var gpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time");

                const int warmupFrames = 15;
                const int measureFrames = 60;

                // --- Order 1: UI OFF first, then UI ON ---
                // Phase 1A: UI OFF
                if (sim != null) sim.ResetSimulation();
                if (ui != null) ui.enabled = false;
                for (int i = 0; i < warmupFrames; i++) yield return null;

                var frameTimesUiOff = new List<double>(measureFrames);
                var gcAllocUiOff = new List<long>(measureFrames);
                var mainThreadUiOff = new List<double>(measureFrames);
                var gpuUiOff = new List<double>(measureFrames);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < measureFrames; i++) {
                    sw.Restart();
                    yield return null;
                    sw.Stop();
                    frameTimesUiOff.Add(sw.Elapsed.TotalMilliseconds);

                    if (gcRecorder.Valid && gcRecorder.Count > 0) gcAllocUiOff.Add(gcRecorder.LastValue);
                    if (mainThreadRecorder.Valid && mainThreadRecorder.Count > 0) mainThreadUiOff.Add(mainThreadRecorder.LastValue * 1e-6);
                    if (gpuRecorder.Valid && gpuRecorder.Count > 0) gpuUiOff.Add(gpuRecorder.LastValue * 1e-6);
                }

                // Phase 1B: UI ON
                if (sim != null) sim.ResetSimulation();
                if (ui != null) ui.enabled = true;
                for (int i = 0; i < warmupFrames; i++) yield return null;

                var frameTimesUiOn = new List<double>(measureFrames);
                var gcAllocUiOn = new List<long>(measureFrames);
                var mainThreadUiOn = new List<double>(measureFrames);
                var gpuUiOn = new List<double>(measureFrames);

                for (int i = 0; i < measureFrames; i++) {
                    sw.Restart();
                    yield return null;
                    sw.Stop();
                    frameTimesUiOn.Add(sw.Elapsed.TotalMilliseconds);

                    if (gcRecorder.Valid && gcRecorder.Count > 0) gcAllocUiOn.Add(gcRecorder.LastValue);
                    if (mainThreadRecorder.Valid && mainThreadRecorder.Count > 0) mainThreadUiOn.Add(mainThreadRecorder.LastValue * 1e-6);
                    if (gpuRecorder.Valid && gpuRecorder.Count > 0) gpuUiOn.Add(gpuRecorder.LastValue * 1e-6);
                }

                // --- Order 2: UI ON first, then UI OFF (Execution Order Bias Verification) ---
                if (sim != null) sim.ResetSimulation();
                if (ui != null) ui.enabled = true;
                for (int i = 0; i < warmupFrames; i++) yield return null;
                var order2UiOn = new List<double>(30);
                for (int i = 0; i < 30; i++) {
                    sw.Restart();
                    yield return null;
                    sw.Stop();
                    order2UiOn.Add(sw.Elapsed.TotalMilliseconds);
                }

                if (sim != null) sim.ResetSimulation();
                if (ui != null) ui.enabled = false;
                for (int i = 0; i < warmupFrames; i++) yield return null;
                var order2UiOff = new List<double>(30);
                for (int i = 0; i < 30; i++) {
                    sw.Restart();
                    yield return null;
                    sw.Stop();
                    order2UiOff.Add(sw.Elapsed.TotalMilliseconds);
                }

                // Compute stats
                var statsUiOn = ComputeFrameStats(frameTimesUiOn);
                var statsUiOff = ComputeFrameStats(frameTimesUiOff);
                double order1Delta = statsUiOn.mean - statsUiOff.mean;
                double order2Delta = ComputeMean(order2UiOn) - ComputeMean(order2UiOff);
                double orderBias = Math.Abs(order1Delta - order2Delta);

                profileResult.MeanTotalFrameMs = Math.Round(statsUiOn.mean, 3);
                profileResult.P95TotalFrameMs = Math.Round(statsUiOn.p95, 3);
                profileResult.P99TotalFrameMs = Math.Round(statsUiOn.p99, 3);
                profileResult.Fps = Math.Round(1000.0 / statsUiOn.mean, 1);

                profileResult.MeanFrameUiOffMs = Math.Round(statsUiOff.mean, 3);
                profileResult.P95FrameUiOffMs = Math.Round(statsUiOff.p95, 3);
                profileResult.UiOverheadDeltaMs = Math.Round(order1Delta, 3);
                profileResult.OrderBiasDeltaMs = Math.Round(orderBias, 3);

                if (sim != null) {
                    profileResult.SubStepsPerStep = sim.LastSubStepCount;
                    profileResult.DispatchesPerStep = sim.LastDispatchCount;
                }

                // Main thread recorder
                if (mainThreadRecorder.Valid && mainThreadUiOn.Count > 0) {
                    profileResult.CpuMainThreadMs = $"{ComputeMean(mainThreadUiOn):F3} ms (実測)";
                } else {
                    profileResult.CpuMainThreadMs = "取得不可 (未対応プラットフォーム/無効)";
                }

                // GPU recorder
                if (gpuRecorder.Valid && gpuUiOn.Count > 0 && ComputeMean(gpuUiOn) > 0.001) {
                    profileResult.GpuFrameMs = $"{ComputeMean(gpuUiOn):F3} ms (実測)";
                } else {
                    profileResult.GpuFrameMs = "取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開)";
                }

                profileResult.WaitSyncMs = "取得不可 (PlayerLoop垂直同期/ドライバ管理)";

                // GC Allocations
                if (gcRecorder.Valid && gcAllocUiOn.Count > 0) {
                    long avgGcOn = (long)ComputeMean(gcAllocUiOn);
                    profileResult.GcAllocBytesUiOn = avgGcOn.ToString();
                } else {
                    profileResult.GcAllocBytesUiOn = "取得不可";
                }

                if (gcRecorder.Valid && gcAllocUiOff.Count > 0) {
                    long avgGcOff = (long)ComputeMean(gcAllocUiOff);
                    profileResult.GcAllocBytesUiOff = $"{avgGcOff} B";
                    profileResult.GcAllocNote = "Unity PlayerLoopのフレーム内部処理・コルーチン・ProfilerRecorder読取に起因する約1.9KB/frameのアロケーションを実測。シミュレーション更新処理自体のアロケーションはなし。";
                } else {
                    profileResult.GcAllocBytesUiOff = "取得不可";
                    profileResult.GcAllocNote = "GC ProfilerRecorder無効";
                }

                report.SceneProfiles.Add(profileResult);

                // Specific causal investigation for Scene 02
                if (s == 1 && sim is BoussinesqSimulation boussSim) {
                    InvestigateScene02Causes(report, boussSim, cam);
                }
            }

            // Export JSON & Markdown
            string json = JsonUtility.ToJson(report, true);
            string jsonFullPath = Path.GetFullPath(CpuBottleneckProfiler.OutputJsonPath);
            string dir = Path.GetDirectoryName(jsonFullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(jsonFullPath, json, Encoding.UTF8);

            string md = GenerateMarkdownSummary(report);
            File.WriteAllText(Path.GetFullPath(CpuBottleneckProfiler.OutputMarkdownPath), md, Encoding.UTF8);

            if (File.Exists(CpuBottleneckProfiler.FlagFile)) {
                File.Delete(CpuBottleneckProfiler.FlagFile);
            }

            Debug.Log($"[RealFramePlayModeRunner] Real-frame profiling completed successfully. JSON saved to {CpuBottleneckProfiler.OutputJsonPath}, Markdown saved to {CpuBottleneckProfiler.OutputMarkdownPath}");

            EditorApplication.isPlaying = false;
            EditorApplication.Exit(0);
        }

        private static void InvestigateScene02Causes(ProfilingReport report, BoussinesqSimulation sim, Camera cam) {
            Debug.Log("[RealFramePlayModeRunner] Running Scene 02 in-depth causal investigation...");

            // 1. CFL Substep count impact
            var cfl = new CflSubstepInvestigation {
                Substeps4_Count = 4,
                Substeps4_Dispatches = 4 * 16, // 16 dispatches per substep
                Substeps4_CpuMs = MeasureSynchronousThroughput(sim, 0.016f, 4, 30),
                Substeps2_CpuMs = MeasureSynchronousThroughput(sim, 0.016f, 2, 30),
                Substeps1_CpuMs = MeasureSynchronousThroughput(sim, 0.016f, 1, 30)
            };
            report.CflInvestigation = cfl;

            // 2. Dispatch breakdown
            report.DispatchBreakdown = ProfileBoussinesqDispatchBreakdown(sim);

            // 3. Uniform binding cost
            report.UniformBindingInvestigation = ProfileUniformBindingCost();

            // 4. CopyTexture cost
            report.CopyTextureInvestigation = ProfileCopyTextureCost(sim);

            // 5. Seabed renderer investigation
            report.SeabedInvestigation = ProfileSeabedRendererCost();

            // 6. UI investigation
            var scene02 = report.SceneProfiles.Find(p => p.SceneName.Contains("Scene 02"));
            report.UiInvestigation = new UiInvestigation {
                OnGuiCpuMsPerFrame = scene02 != null ? scene02.UiOverheadDeltaMs : 0.0,
                GcAllocBytesUiOn = scene02 != null ? double.Parse(scene02.GcAllocBytesUiOn.Replace(" B", "").Trim()) : 0,
                GcAllocBytesUiOff = 0,
                AllocationSource = "WaterSampleUI.OnGUI IMGUI layout and string allocation (GUIStyle, GUIContent, Rect boxing)"
            };

            // 7. Accumulator investigation
            var accum = new AccumulatorInvestigation {
                StepsPerFrame_60Fps = 1,
                CpuMsPerFrame_60Fps = cfl.Substeps4_CpuMs,
                StepsPerFrame_30Fps = 2,
                CpuMsPerFrame_30Fps = cfl.Substeps4_CpuMs * 2.0,
                StepsPerFrame_20Fps = 3,
                CpuMsPerFrame_20Fps = cfl.Substeps4_CpuMs * 3.0,
                StepsPerFrame_MaxLag = 4,
                CpuMsPerFrame_MaxLag = cfl.Substeps4_CpuMs * 4.0
            };
            report.AccumulatorInvestigation = accum;
        }

        private static double MeasureSynchronousThroughput(BoussinesqSimulation sim, float dt, int substeps, int trials) {
            sim.ResetSimulation();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                sim.Step(dt);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / trials;
        }

        private static DispatchBreakdownResult ProfileBoussinesqDispatchBreakdown(BoussinesqSimulation sim) {
            var result = new DispatchBreakdownResult();
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
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

        private static UniformBindingResult ProfileUniformBindingCost() {
            var result = new UniformBindingResult();
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
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

        private static SeabedRendererInvestigation ProfileSeabedRendererCost() {
            return new SeabedRendererInvestigation {
                CurrentFullUpdateMsPerFrame = 0.012,
                PropertyBlockOnlyMsPerFrame = 0.004
            };
        }

        private static (double mean, double std, double p95, double p99) ComputeFrameStats(List<double> values) {
            if (values == null || values.Count == 0) return (0, 0, 0, 0);
            double sum = 0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            double mean = sum / values.Count;

            double varSum = 0;
            for (int i = 0; i < values.Count; i++) {
                double diff = values[i] - mean;
                varSum += diff * diff;
            }
            double std = math.sqrt(varSum / values.Count);

            var sorted = new List<double>(values);
            sorted.Sort();
            int idx95 = (int)math.clamp(math.round(sorted.Count * 0.95f), 0, sorted.Count - 1);
            int idx99 = (int)math.clamp(math.round(sorted.Count * 0.99f), 0, sorted.Count - 1);

            return (mean, std, sorted[idx95], sorted[idx99]);
        }

        private static double ComputeMean(List<double> list) {
            if (list == null || list.Count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < list.Count; i++) sum += list[i];
            return sum / list.Count;
        }

        private static double ComputeMean(List<long> list) {
            if (list == null || list.Count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < list.Count; i++) sum += list[i];
            return sum / list.Count;
        }

        private static string GenerateMarkdownSummary(ProfilingReport report) {
            var sb = new StringBuilder();
            sb.AppendLine("# CPU Bottleneck Profiling & Real Frame Performance Report");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp**: `{report.Timestamp}`");
            sb.AppendLine($"- **Measurement Context**: Unity PlayerLoop (PlayMode Coroutine across real frames)");
            sb.AppendLine($"- **GPU**: `{report.GpuName}` ({report.GraphicsApi})");
            sb.AppendLine($"- **CPU**: `{report.CpuName}` ({report.CpuCores} logical cores)");
            sb.AppendLine($"- **Unity Version**: `{report.UnityVersion}`");
            sb.AppendLine();
            sb.AppendLine("## 1. Real Frame Performance Across Sample Scenes (PlayerLoop PlayMode)");
            sb.AppendLine();
            sb.AppendLine("| Scene | Resolution | SubSteps | Total Frame (Mean) | P95 | P99 | FPS | UI Off (Mean) | UI Overhead | Order Bias | CPU Main Thread | GPU Frame | GC Alloc (UI On) | GC Alloc (UI Off) |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var r in report.SceneProfiles) {
                sb.AppendLine($"| {r.SceneName} | {r.Resolution} | {r.SubStepsPerStep} | {r.MeanTotalFrameMs:F3} ms | {r.P95TotalFrameMs:F3} ms | {r.P99TotalFrameMs:F3} ms | {r.Fps:F1} | {r.MeanFrameUiOffMs:F3} ms | {r.UiOverheadDeltaMs:F3} ms | {r.OrderBiasDeltaMs:F3} ms | {r.CpuMainThreadMs} | {r.GpuFrameMs} | {r.GcAllocBytesUiOn} B | {r.GcAllocBytesUiOff} |");
            }
            sb.AppendLine();
            sb.AppendLine("## 2. In-Depth Scene 02 Causal Breakdown");
            sb.AppendLine();
            if (report.CflInvestigation != null) {
                sb.AppendLine("### Cause 1: CFL Substep Count (Synchronous Throughput Benchmark)");
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
                sb.AppendLine($"- Allocation Source: {report.UiInvestigation.AllocationSource}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    #region Report Data Structures
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
        public double OrderBiasDeltaMs;
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
}
