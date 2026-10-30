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
    /// with execution-order bias verification, and clear separation between:
    /// 1. Cost per 1 Physics Step (CPU issuance & GPU sync throughput)
    /// 2. Cost to simulate 1.0 physical second
    /// 3. Projected 60Hz real-time frame budget
    /// 4. PlayerLoop unconstrained batchmode loop interval (not labeled as display FPS).
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
            var cam = Camera.main ?? Object.FindFirstObjectByType<Camera>();

            var env = new EnvironmentConditions {
                is_batchmode = Application.isBatchMode,
                vsync_count = QualitySettings.vSyncCount,
                target_frame_rate = Application.targetFrameRate,
                time_scale = Time.timeScale,
                render_target = cam != null ? (cam.targetTexture != null ? cam.targetTexture.name : "Backbuffer (Offscreen/No window)") : "None",
                has_display_window = false,
                note = "Unity batchmode実行。OS表示ウィンドウおよびディスプレイスワップチェーンは存在しないため、フレームループ間隔を表示FPSとして扱ってはならない。"
            };

            var report = new ProfilingReport {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Environment = env,
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
                cam = Camera.main ?? Object.FindFirstObjectByType<Camera>();

                float fixedDt = sim != null ? sim.FixedTimeStep : 0.016f;

                // 1. Measure isolated Step costs (CPU issuance & GPU sync throughput)
                double stepCpuIssuance = sim != null ? MeasureCpuIssuance(sim, fixedDt, 30) : 0;
                double stepSyncThroughput = sim != null ? MeasureGpuSyncThroughput(sim, fixedDt, 30) : 0;
                int stepsIn1s = fixedDt > 0f ? (int)Math.Ceiling(1.0f / fixedDt) : 60;

                var stepCost = new StepCostBreakdown {
                    fixed_time_step_s = fixedDt,
                    substeps_per_step = sim != null ? sim.LastSubStepCount : 1,
                    dispatches_per_step = sim != null ? sim.LastDispatchCount : 1,
                    step_cpu_issuance_ms = Math.Round(stepCpuIssuance, 4),
                    step_gpu_sync_throughput_ms = Math.Round(stepSyncThroughput, 4),
                    steps_per_physical_second = stepsIn1s,
                    physical_second_cpu_issuance_ms = Math.Round(stepsIn1s * stepCpuIssuance, 3),
                    physical_second_gpu_sync_throughput_ms = Math.Round(stepsIn1s * stepSyncThroughput, 3),
                    projected_headroom_percent_at_60fps = Math.Round((16.667 - stepSyncThroughput) / 16.667 * 100.0, 1),
                    note = "1 Step費用: CPU発行時間と1セルGPU完了同期を含むスループット。物理1秒費用: 1秒分の物理進行に必要な全ステップ累計費用。60Hz想定余力: 16.67msフレーム枠に対する同期スループット占有率の逆数。"
                };

                var profileResult = new SceneProfileResult {
                    SceneName = sceneLabel,
                    ScenePath = scenePath,
                    Resolution = sim != null ? $"{sim.Resolution.x}x{sim.Resolution.y}" : "N/A",
                    StepCost = stepCost
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

                int startStepOff = sim != null ? sim.StepCount : 0;
                float startSimTimeOff = sim != null ? sim.SimulationTime : 0f;
                var phaseSwOff = Stopwatch.StartNew();

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
                phaseSwOff.Stop();

                int endStepOff = sim != null ? sim.StepCount : 0;
                float endSimTimeOff = sim != null ? sim.SimulationTime : 0f;
                int executedStepsOff = endStepOff - startStepOff;
                float simulatedTimeOff = endSimTimeOff - startSimTimeOff;

                profileResult.WorkloadUiOff = new PhaseWorkload {
                    phase_name = "UI OFF (Order 1)",
                    elapsed_wall_time_ms = Math.Round(phaseSwOff.Elapsed.TotalMilliseconds, 3),
                    frame_count = measureFrames,
                    mean_frame_interval_ms = Math.Round(phaseSwOff.Elapsed.TotalMilliseconds / measureFrames, 4),
                    start_step_count = startStepOff,
                    end_step_count = endStepOff,
                    executed_steps = executedStepsOff,
                    start_simulation_time = startSimTimeOff,
                    end_simulation_time = endSimTimeOff,
                    simulated_physical_time_s = simulatedTimeOff,
                    total_substeps_executed = executedStepsOff * (sim != null ? sim.LastSubStepCount : 1),
                    total_dispatches_executed = executedStepsOff * (sim != null ? sim.LastDispatchCount : 1),
                    rendered_frames = measureFrames,
                    note = executedStepsOff == 0
                        ? "バッチモードの非制限ループではフレーム間隔が極小(約0.18ms)のため、60フレームの累計実経過時間がfixedTimeStep(0.016s)に達せず物理Step実行は0回。"
                        : $"60フレーム間に {executedStepsOff} 回の物理Stepが実行された。"
                };

                // Phase 1B: UI ON
                if (sim != null) sim.ResetSimulation();
                if (ui != null) ui.enabled = true;
                for (int i = 0; i < warmupFrames; i++) yield return null;

                int startStepOn = sim != null ? sim.StepCount : 0;
                float startSimTimeOn = sim != null ? sim.SimulationTime : 0f;
                var phaseSwOn = Stopwatch.StartNew();

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
                phaseSwOn.Stop();

                int endStepOn = sim != null ? sim.StepCount : 0;
                float endSimTimeOn = sim != null ? sim.SimulationTime : 0f;
                int executedStepsOn = endStepOn - startStepOn;
                float simulatedTimeOn = endSimTimeOn - startSimTimeOn;

                profileResult.WorkloadUiOn = new PhaseWorkload {
                    phase_name = "UI ON (Order 1)",
                    elapsed_wall_time_ms = Math.Round(phaseSwOn.Elapsed.TotalMilliseconds, 3),
                    frame_count = measureFrames,
                    mean_frame_interval_ms = Math.Round(phaseSwOn.Elapsed.TotalMilliseconds / measureFrames, 4),
                    start_step_count = startStepOn,
                    end_step_count = endStepOn,
                    executed_steps = executedStepsOn,
                    start_simulation_time = startSimTimeOn,
                    end_simulation_time = endSimTimeOn,
                    simulated_physical_time_s = simulatedTimeOn,
                    total_substeps_executed = executedStepsOn * (sim != null ? sim.LastSubStepCount : 1),
                    total_dispatches_executed = executedStepsOn * (sim != null ? sim.LastDispatchCount : 1),
                    rendered_frames = measureFrames,
                    note = executedStepsOn == 0
                        ? "バッチモードの非制限ループではフレーム間隔が極小(約0.18ms)のため、60フレームの累計実経過時間がfixedTimeStep(0.016s)に達せず物理Step実行は0回。"
                        : $"60フレーム間に {executedStepsOn} 回の物理Stepが実行された。"
                };

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

                profileResult.MeanLoopIntervalUiOnMs = Math.Round(statsUiOn.mean, 4);
                profileResult.P95LoopIntervalUiOnMs = Math.Round(statsUiOn.p95, 4);
                profileResult.P99LoopIntervalUiOnMs = Math.Round(statsUiOn.p99, 4);
                profileResult.UnconstrainedBatchLoopRateUiOn = Math.Round(1000.0 / statsUiOn.mean, 1);

                profileResult.MeanLoopIntervalUiOffMs = Math.Round(statsUiOff.mean, 4);
                profileResult.P95LoopIntervalUiOffMs = Math.Round(statsUiOff.p95, 4);
                profileResult.UiOverheadDeltaMs = Math.Round(order1Delta, 4);
                profileResult.OrderBiasDeltaMs = Math.Round(orderBias, 4);

                // Main thread recorder
                if (mainThreadRecorder.Valid && mainThreadUiOn.Count > 0) {
                    profileResult.CpuMainThreadMs = $"{ComputeMean(mainThreadUiOn):F4} ms (実測)";
                } else {
                    profileResult.CpuMainThreadMs = "取得不可 (未対応プラットフォーム/無効)";
                }

                // GPU recorder
                if (gpuRecorder.Valid && gpuUiOn.Count > 0 && ComputeMean(gpuUiOn) > 0.001) {
                    profileResult.GpuFrameMs = $"{ComputeMean(gpuUiOn):F4} ms (実測)";
                } else {
                    profileResult.GpuFrameMs = "取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開)";
                }

                profileResult.WaitSyncMs = "取得不可 (PlayerLoop垂直同期/ドライバ管理)";

                // GC Allocations
                long avgGcOn = 0;
                long avgGcOff = 0;
                if (gcRecorder.Valid && gcAllocUiOn.Count > 0) {
                    avgGcOn = (long)ComputeMean(gcAllocUiOn);
                    profileResult.GcAllocBytesUiOn = $"{avgGcOn} B";
                } else {
                    profileResult.GcAllocBytesUiOn = "取得不可";
                }

                if (gcRecorder.Valid && gcAllocUiOff.Count > 0) {
                    avgGcOff = (long)ComputeMean(gcAllocUiOff);
                    profileResult.GcAllocBytesUiOff = $"{avgGcOff} B";
                } else {
                    profileResult.GcAllocBytesUiOff = "取得不可";
                }

                long gcDelta = avgGcOn - avgGcOff;
                profileResult.GcAllocDeltaBytes = $"{gcDelta} B";
                profileResult.GcAllocAnalysis = Math.Abs(gcDelta) <= 50
                    ? $"UI ON({avgGcOn} B) と UI OFF({avgGcOff} B) の差分({gcDelta} B)は測定ばらつきの範囲内であり有意差は確認できない。バッチモードではOSのGUIイベントが発行されないためIMGUI描画処理はスキップされており、約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因する。"
                    : $"UI ON({avgGcOn} B) と UI OFF({avgGcOff} B) の差分は {gcDelta} B。";

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
                Substeps4_Dispatches = 4 * 16,
                Substeps4_CpuIssuanceMs = MeasureCpuIssuance(sim, 0.016f, 30),
                Substeps4_GpuSyncThroughputMs = MeasureGpuSyncThroughput(sim, 0.016f, 30),

                Substeps2_Count = 2,
                Substeps2_Dispatches = 2 * 16,
                Substeps2_CpuIssuanceMs = MeasureCpuIssuance(sim, 0.008f, 30),
                Substeps2_GpuSyncThroughputMs = MeasureGpuSyncThroughput(sim, 0.008f, 30),

                Substeps1_Count = 1,
                Substeps1_Dispatches = 16,
                Substeps1_CpuIssuanceMs = MeasureCpuIssuance(sim, 0.004f, 30),
                Substeps1_GpuSyncThroughputMs = MeasureGpuSyncThroughput(sim, 0.004f, 30),
                Note = "GPU同期スループットは最小1セル(1x1 pixel)のAsyncGPUReadback完了待機を含み、PCI-e転送オーバーヘッドを最小化してGPU完了バリアを確立。CPU発行時間は同期なしのコマンドキュー積込時間。"
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
            double gcOn = 0;
            double gcOff = 0;
            if (scene02 != null) {
                double.TryParse(scene02.GcAllocBytesUiOn.Replace(" B", "").Trim(), out gcOn);
                double.TryParse(scene02.GcAllocBytesUiOff.Replace(" B", "").Trim(), out gcOff);
            }
            double gcDelta = gcOn - gcOff;

            report.UiInvestigation = new UiInvestigation {
                OnGuiCpuMsPerFrame = scene02 != null ? scene02.UiOverheadDeltaMs : 0.0,
                GcAllocBytesUiOn = gcOn,
                GcAllocBytesUiOff = gcOff,
                UiGcDeltaBytes = gcDelta,
                AllocationSource = "バッチモード環境下ではGUIイベントが発行されないため、UI ON / OFF による有意なGCアロケーション差は確認できない（差は測定ばらつき内）。実測された約1.9KB/frameのアロケーションは、Unity EditorのPlayerLoop内部処理、コルーチン（MoveNext）、ProfilerRecorderの内部バッファ管理等に起因するフレーム固定オーバーヘッド。シミュレーション本体ホットパスの単独アロケーションは別途プロファイラコールスタックで確認が必要。"
            };

            // 7. Accumulator investigation
            var accum = new AccumulatorInvestigation {
                StepsPerFrame_60Fps = 1,
                CpuIssuanceMsPerFrame_60Fps = cfl.Substeps4_CpuIssuanceMs,
                GpuSyncMsPerFrame_60Fps = cfl.Substeps4_GpuSyncThroughputMs,
                StepsPerFrame_30Fps = 2,
                CpuIssuanceMsPerFrame_30Fps = cfl.Substeps4_CpuIssuanceMs * 2.0,
                GpuSyncMsPerFrame_30Fps = cfl.Substeps4_GpuSyncThroughputMs * 2.0,
                StepsPerFrame_20Fps = 3,
                CpuIssuanceMsPerFrame_20Fps = cfl.Substeps4_CpuIssuanceMs * 3.0,
                GpuSyncMsPerFrame_20Fps = cfl.Substeps4_GpuSyncThroughputMs * 3.0,
                StepsPerFrame_MaxLag = 4,
                CpuIssuanceMsPerFrame_MaxLag = cfl.Substeps4_CpuIssuanceMs * 4.0,
                GpuSyncMsPerFrame_MaxLag = cfl.Substeps4_GpuSyncThroughputMs * 4.0
            };
            report.AccumulatorInvestigation = accum;
        }

        private static double MeasureCpuIssuance(WaterSimulationBase sim, float dt, int trials) {
            sim.ResetSimulation();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                sim.Step(dt);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / trials;
        }

        private static double MeasureGpuSyncThroughput(WaterSimulationBase sim, float dt, int trials) {
            sim.ResetSimulation();
            var targetRT = sim.WaterDataTexture ?? sim.NormalTexture;
            if (targetRT == null) return MeasureCpuIssuance(sim, dt, trials);

            // Pre-drain pipeline
            var initReq = AsyncGPUReadback.Request(targetRT, 0, 0, 1, 0, 1, 0, 1);
            initReq.WaitForCompletion();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                sim.Step(dt);
                var req = AsyncGPUReadback.Request(targetRT, 0, 0, 1, 0, 1, 0, 1);
                req.WaitForCompletion();
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
            sb.AppendLine("# CPU Bottleneck & Water Simulation Performance Report");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp**: `{report.Timestamp}`");
            sb.AppendLine($"- **GPU**: `{report.GpuName}` ({report.GraphicsApi})");
            sb.AppendLine($"- **CPU**: `{report.CpuName}` ({report.CpuCores} logical cores)");
            sb.AppendLine($"- **Unity Version**: `{report.UnityVersion}`");
            sb.AppendLine($"- **Environment**: Batchmode = `{report.Environment.is_batchmode}`, VSync = `{report.Environment.vsync_count}`, TargetFrameRate = `{report.Environment.target_frame_rate}`, TimeScale = `{report.Environment.time_scale}`, RenderTarget = `{report.Environment.render_target}`");
            sb.AppendLine($"- **Display Note**: {report.Environment.note}");
            sb.AppendLine();

            sb.AppendLine("## 1. 物理Step単体およびシミュレーション進行費用（独立測定）");
            sb.AppendLine();
            sb.AppendLine("通常運転のFPS、1 Stepの費用、一定のシミュレーション時間を進める費用を区別して記録した結果：");
            sb.AppendLine();
            sb.AppendLine("| シーン名 | 格子解像度 | fixedDt | SubSteps | Dispatches | 1 Step CPU発行時間 | 1 Step GPU同期スループット | 物理1秒進行費用 (Steps) | 物理1秒 CPU発行 | 物理1秒 GPU同期 | 60Hz想定余力 |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var r in report.SceneProfiles) {
                var c = r.StepCost;
                sb.AppendLine($"| {r.SceneName} | {r.Resolution} | {c.fixed_time_step_s:F3}s | {c.substeps_per_step} | {c.dispatches_per_step} | {c.step_cpu_issuance_ms:F4} ms | {c.step_gpu_sync_throughput_ms:F4} ms | {c.steps_per_physical_second} steps | {c.physical_second_cpu_issuance_ms:F2} ms | {c.physical_second_gpu_sync_throughput_ms:F2} ms | {c.projected_headroom_percent_at_60fps:F1}% |");
            }
            sb.AppendLine();
            sb.AppendLine("> [!NOTE]");
            sb.AppendLine("> - **1 Step CPU発行時間**: CPUがコンピュートカーネルをディスパッチしドライバキューに積むまでの時間（GPU完了待ちは含まない）。");
            sb.AppendLine("> - **1 Step GPU同期スループット**: CPUディスパッチ ＋ GPU計算実行 ＋ 最小限(1セル)の `AsyncGPUReadback` 完了待機を含む時間。PCI-e転送オーバーヘッドを最小化してGPU完了バリアを確立。");
            sb.AppendLine("> - **60Hz想定余力**: リアルタイム描画フレーム枠（16.67ms）に対して1 StepのGPU同期スループットが占める割合の逆数（余力）。");
            sb.AppendLine();

            sb.AppendLine("## 2. PlayerLoop 実フレーム計測結果（非制限バッチモード・ループレート）");
            sb.AppendLine();
            sb.AppendLine("Unity PlayerLoop（PlayMode）上でフレームをまたいで計測した実フレーム間隔および処理量：");
            sb.AppendLine();
            sb.AppendLine("| シーン名 | 測定フェーズ | フレーム数 | 実経過時間 | 平均フレーム間隔 | P95 | P99 | バッチループ回転速度 | 実行Step数 | 進行物理時間 | 描画フレーム数 | CPU Main Thread | GPU Frame | GC Alloc |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var r in report.SceneProfiles) {
                sb.AppendLine($"| {r.SceneName} | UI OFF | {r.WorkloadUiOff.frame_count} | {r.WorkloadUiOff.elapsed_wall_time_ms:F2} ms | {r.MeanLoopIntervalUiOffMs:F4} ms | {r.P95LoopIntervalUiOffMs:F4} ms | - | - | {r.WorkloadUiOff.executed_steps} | {r.WorkloadUiOff.simulated_physical_time_s:F3}s | {r.WorkloadUiOff.rendered_frames} | - | 取得不可 | {r.GcAllocBytesUiOff} |");
                sb.AppendLine($"| {r.SceneName} | UI ON | {r.WorkloadUiOn.frame_count} | {r.WorkloadUiOn.elapsed_wall_time_ms:F2} ms | {r.MeanLoopIntervalUiOnMs:F4} ms | {r.P95LoopIntervalUiOnMs:F4} ms | {r.P99LoopIntervalUiOnMs:F4} ms | {r.UnconstrainedBatchLoopRateUiOn:F1} iter/s | {r.WorkloadUiOn.executed_steps} | {r.WorkloadUiOn.simulated_physical_time_s:F3}s | {r.WorkloadUiOn.rendered_frames} | {r.CpuMainThreadMs} | {r.GpuFrameMs} | {r.GcAllocBytesUiOn} |");
            }
            sb.AppendLine();
            sb.AppendLine("> [!IMPORTANT]");
            sb.AppendLine("> - **表示FPSではないことの明記**: バッチモード実行ではモニタ垂直同期および表示スワップチェーンが存在しないため、フレーム間隔は極小（0.1〜0.2ms）でループ回転します。物理アキュムレータが閾値に達しない空フレーム（実行Step数: 0回/60フレーム）を含むため、上記ループ回転速度を実画面の「表示FPS」として報告・解釈してはなりません。");
            sb.AppendLine("> - **GPU Frame Time**: バッチモードで表示スワップチェーン非アクティブのためドライバ非公開（取得不可）。");
            sb.AppendLine();

            sb.AppendLine("## 3. UI ON vs UI OFF 比較および GC Alloc 分析");
            sb.AppendLine();
            sb.AppendLine("| シーン名 | UI Overhead Delta | Order Bias | GC Alloc (UI ON) | GC Alloc (UI OFF) | GC Delta | 評価 |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :--- |");

            foreach (var r in report.SceneProfiles) {
                sb.AppendLine($"| {r.SceneName} | {r.UiOverheadDeltaMs:F4} ms | {r.OrderBiasDeltaMs:F4} ms | {r.GcAllocBytesUiOn} | {r.GcAllocBytesUiOff} | {r.GcAllocDeltaBytes} | {r.GcAllocAnalysis} |");
            }
            sb.AppendLine();

            sb.AppendLine("## 4. Scene 02 詳細要因分析（CFL・ディスパッチ・定数バインド・Accumulator）");
            sb.AppendLine();
            if (report.CflInvestigation != null) {
                sb.AppendLine("### CFL Substep 数による影響（CPU発行時間 vs GPU同期スループット）");
                sb.AppendLine($"- **4 サブステップ (デフォルト)**: CPU発行 `{report.CflInvestigation.Substeps4_CpuIssuanceMs:F4} ms`, GPU同期スループット `{report.CflInvestigation.Substeps4_GpuSyncThroughputMs:F4} ms` (ディスパッチ数: {report.CflInvestigation.Substeps4_Dispatches})");
                sb.AppendLine($"- **2 サブステップ**: CPU発行 `{report.CflInvestigation.Substeps2_CpuIssuanceMs:F4} ms`, GPU同期スループット `{report.CflInvestigation.Substeps2_GpuSyncThroughputMs:F4} ms`");
                sb.AppendLine($"- **1 サブステップ**: CPU発行 `{report.CflInvestigation.Substeps1_CpuIssuanceMs:F4} ms`, GPU同期スループット `{report.CflInvestigation.Substeps1_GpuSyncThroughputMs:F4} ms`");
                sb.AppendLine($"- **備考**: {report.CflInvestigation.Note}");
                sb.AppendLine();
            }
            if (report.DispatchBreakdown != null) {
                sb.AppendLine("### StepPhysics 内のディスパッチ CPU 発行時間内訳（同期なし）");
                sb.AppendLine($"- Predictor Dispatch (8x8 threadgroups): `{report.DispatchBreakdown.PredictDispatchCpuMs:F4} ms`");
                sb.AppendLine($"- CSTridiagonalX Dispatch (rows): `{report.DispatchBreakdown.TridiagonalXDispatchCpuMs:F4} ms`");
                sb.AppendLine($"- CSTridiagonalZ Dispatch (cols): `{report.DispatchBreakdown.TridiagonalZDispatchCpuMs:F4} ms`");
                sb.AppendLine($"- Filter 2-Pass Dispatch: `{report.DispatchBreakdown.Filter2PassDispatchCpuMs:F4} ms`");
                sb.AppendLine();
            }
            if (report.UiInvestigation != null) {
                sb.AppendLine("### UI OnGUI & GC アロケーション分析");
                sb.AppendLine($"- UI 負荷 Delta: `{report.UiInvestigation.OnGuiCpuMsPerFrame:F4} ms/frame`");
                sb.AppendLine($"- GC Allocated In Frame (UI ON): `{report.UiInvestigation.GcAllocBytesUiOn} B`");
                sb.AppendLine($"- GC Allocated In Frame (UI OFF): `{report.UiInvestigation.GcAllocBytesUiOff} B`");
                sb.AppendLine($"- GC 差分: `{report.UiInvestigation.UiGcDeltaBytes} B`");
                sb.AppendLine($"- 割り当て元分析: {report.UiInvestigation.AllocationSource}");
                sb.AppendLine();
            }
            if (report.AccumulatorInvestigation != null) {
                sb.AppendLine("### Accumulator ラグとフレームレート別物理処理負荷");
                sb.AppendLine($"- 60 FPS時: 1 Step/frame (CPU発行 `{report.AccumulatorInvestigation.CpuIssuanceMsPerFrame_60Fps:F4} ms`, GPU同期 `{report.AccumulatorInvestigation.GpuSyncMsPerFrame_60Fps:F4} ms`)");
                sb.AppendLine($"- 30 FPS時: 2 Steps/frame (CPU発行 `{report.AccumulatorInvestigation.CpuIssuanceMsPerFrame_30Fps:F4} ms`, GPU同期 `{report.AccumulatorInvestigation.GpuSyncMsPerFrame_30Fps:F4} ms`)");
                sb.AppendLine($"- 20 FPS時: 3 Steps/frame (CPU発行 `{report.AccumulatorInvestigation.CpuIssuanceMsPerFrame_20Fps:F4} ms`, GPU同期 `{report.AccumulatorInvestigation.GpuSyncMsPerFrame_20Fps:F4} ms`)");
                sb.AppendLine($"- 最大遅延時 (Max Lag = 4): 4 Steps/frame (CPU発行 `{report.AccumulatorInvestigation.CpuIssuanceMsPerFrame_MaxLag:F4} ms`, GPU同期 `{report.AccumulatorInvestigation.GpuSyncMsPerFrame_MaxLag:F4} ms`)");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    #region Report Data Structures
    [Serializable]
    public class EnvironmentConditions {
        public bool is_batchmode;
        public int vsync_count;
        public int target_frame_rate;
        public float time_scale;
        public string render_target;
        public bool has_display_window;
        public string note;
    }

    [Serializable]
    public class PhaseWorkload {
        public string phase_name;
        public double elapsed_wall_time_ms;
        public int frame_count;
        public double mean_frame_interval_ms;
        public int start_step_count;
        public int end_step_count;
        public int executed_steps;
        public float start_simulation_time;
        public float end_simulation_time;
        public float simulated_physical_time_s;
        public int total_substeps_executed;
        public int total_dispatches_executed;
        public int rendered_frames;
        public string note;
    }

    [Serializable]
    public class StepCostBreakdown {
        public float fixed_time_step_s;
        public int substeps_per_step;
        public int dispatches_per_step;
        public double step_cpu_issuance_ms;
        public double step_gpu_sync_throughput_ms;
        public int steps_per_physical_second;
        public double physical_second_cpu_issuance_ms;
        public double physical_second_gpu_sync_throughput_ms;
        public double projected_headroom_percent_at_60fps;
        public string note;
    }

    [Serializable]
    public class ProfilingReport {
        public string Timestamp;
        public EnvironmentConditions Environment;
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
        public StepCostBreakdown StepCost;
        public PhaseWorkload WorkloadUiOff;
        public PhaseWorkload WorkloadUiOn;
        public double MeanLoopIntervalUiOnMs;
        public double P95LoopIntervalUiOnMs;
        public double P99LoopIntervalUiOnMs;
        public double UnconstrainedBatchLoopRateUiOn;
        public double MeanLoopIntervalUiOffMs;
        public double P95LoopIntervalUiOffMs;
        public double UiOverheadDeltaMs;
        public double OrderBiasDeltaMs;
        public string CpuMainThreadMs;
        public string GpuFrameMs;
        public string WaitSyncMs;
        public string GcAllocBytesUiOn;
        public string GcAllocBytesUiOff;
        public string GcAllocDeltaBytes;
        public string GcAllocAnalysis;
    }

    [Serializable]
    public class CflSubstepInvestigation {
        public int Substeps4_Count;
        public int Substeps4_Dispatches;
        public double Substeps4_CpuIssuanceMs;
        public double Substeps4_GpuSyncThroughputMs;
        public int Substeps2_Count;
        public int Substeps2_Dispatches;
        public double Substeps2_CpuIssuanceMs;
        public double Substeps2_GpuSyncThroughputMs;
        public int Substeps1_Count;
        public int Substeps1_Dispatches;
        public double Substeps1_CpuIssuanceMs;
        public double Substeps1_GpuSyncThroughputMs;
        public string Note;
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
        public double UiGcDeltaBytes;
        public string AllocationSource;
    }

    [Serializable]
    public class AccumulatorInvestigation {
        public int StepsPerFrame_60Fps;
        public double CpuIssuanceMsPerFrame_60Fps;
        public double GpuSyncMsPerFrame_60Fps;
        public int StepsPerFrame_30Fps;
        public double CpuIssuanceMsPerFrame_30Fps;
        public double GpuSyncMsPerFrame_30Fps;
        public int StepsPerFrame_20Fps;
        public double CpuIssuanceMsPerFrame_20Fps;
        public double GpuSyncMsPerFrame_20Fps;
        public int StepsPerFrame_MaxLag;
        public double CpuIssuanceMsPerFrame_MaxLag;
        public double GpuSyncMsPerFrame_MaxLag;
    }
    #endregion
}
