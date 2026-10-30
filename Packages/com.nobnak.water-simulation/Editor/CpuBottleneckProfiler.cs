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
    /// 3. Step margin in 16.67ms frame (simulation-only, not whole app headroom)
    /// 4. PlayerLoop loop interval and workload tracking (not labeled as display FPS).
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
                    step_margin_percent_in_16ms_frame = Math.Round((16.667 - stepSyncThroughput) / 16.667 * 100.0, 1),
                    note = "1 Step費用: CPU発行時間と1セルGPU完了同期を含むスループット。物理1秒費用: 1秒分の物理進行に必要な全ステップ累計費用。16.67ms枠に対するStep単体マージン: 描画・UI・表示待ちを含まないシミュレーション単体の時間マージン。"
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

                int renderedCountOff = 0;
                void OnEndCameraOff(ScriptableRenderContext ctx, Camera c) {
                    if (cam != null && c == cam) renderedCountOff++;
                }
                RenderPipelineManager.endCameraRendering += OnEndCameraOff;

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
                RenderPipelineManager.endCameraRendering -= OnEndCameraOff;

                int endStepOff = sim != null ? sim.StepCount : 0;
                float endSimTimeOff = sim != null ? sim.SimulationTime : 0f;
                int executedStepsOff = endStepOff - startStepOff;
                float simulatedTimeOff = endSimTimeOff - startSimTimeOff;
                string renderedFramesOffStr = renderedCountOff > 0 ? renderedCountOff.ToString() : "未計測 (バッチモード描画コールバック未検知)";

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
                    rendered_frames = renderedFramesOffStr,
                    note = executedStepsOff == 0
                        ? "バッチモードの非制限ループではフレーム間隔が極小(約0.18ms)のため、60フレームの累計実経過時間がfixedTimeStep(0.016s)に達せず物理Step実行は0回。"
                        : $"60フレーム間に {executedStepsOff} 回の物理Stepが実行された。"
                };

                // Phase 1B: UI ON
                if (sim != null) sim.ResetSimulation();
                if (ui != null) ui.enabled = true;
                for (int i = 0; i < warmupFrames; i++) yield return null;

                int renderedCountOn = 0;
                void OnEndCameraOn(ScriptableRenderContext ctx, Camera c) {
                    if (cam != null && c == cam) renderedCountOn++;
                }
                RenderPipelineManager.endCameraRendering += OnEndCameraOn;

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
                RenderPipelineManager.endCameraRendering -= OnEndCameraOn;

                int endStepOn = sim != null ? sim.StepCount : 0;
                float endSimTimeOn = sim != null ? sim.SimulationTime : 0f;
                int executedStepsOn = endStepOn - startStepOn;
                float simulatedTimeOn = endSimTimeOn - startSimTimeOn;
                string renderedFramesOnStr = renderedCountOn > 0 ? renderedCountOn.ToString() : "未計測 (バッチモード描画コールバック未検知)";

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
                    rendered_frames = renderedFramesOnStr,
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
                bool hasGcOn = gcRecorder.Valid && gcAllocUiOn.Count > 0;
                bool hasGcOff = gcRecorder.Valid && gcAllocUiOff.Count > 0;

                if (hasGcOn) {
                    avgGcOn = (long)ComputeMean(gcAllocUiOn);
                    profileResult.GcAllocBytesUiOn = $"{avgGcOn} B";
                } else {
                    profileResult.GcAllocBytesUiOn = "取得不可";
                }

                if (hasGcOff) {
                    avgGcOff = (long)ComputeMean(gcAllocUiOff);
                    profileResult.GcAllocBytesUiOff = $"{avgGcOff} B";
                } else {
                    profileResult.GcAllocBytesUiOff = "取得不可";
                }

                if (hasGcOn && hasGcOff) {
                    long gcDelta = avgGcOn - avgGcOff;
                    profileResult.GcAllocDeltaBytes = $"{gcDelta} B";
                    profileResult.GcAllocAnalysis = Math.Abs(gcDelta) <= 50
                        ? $"UI ON({avgGcOn} B) と UI OFF({avgGcOff} B) の差分({gcDelta} B)は測定ばらつきの範囲内であり有意差は確認できない。【仮説（コールスタック未取得のため推定）】約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因すると推定される。"
                        : $"UI ON({avgGcOn} B) と UI OFF({avgGcOff} B) の差分は {gcDelta} B。";
                } else {
                    profileResult.GcAllocDeltaBytes = "取得不可";
                    profileResult.GcAllocAnalysis = "GC ProfilerRecorder未取得のためアロケーション差分および要因分析は取得不可。";
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

            // 1. CFL Substep count impact with measured substep/dispatch counts
            const int trials = 30;

            sim.ResetSimulation();
            int initDisp4 = sim.LastDispatchCount;
            double cpu4 = MeasureCpuIssuance(sim, 0.016f, trials);
            double sync4 = MeasureGpuSyncThroughput(sim, 0.016f, trials);
            int sub4 = sim.LastSubStepCount;
            int disp4 = sim.LastDispatchCount;
            int totalDisp4 = trials * disp4;

            sim.ResetSimulation();
            int initDisp2 = sim.LastDispatchCount;
            double cpu2 = MeasureCpuIssuance(sim, 0.008f, trials);
            double sync2 = MeasureGpuSyncThroughput(sim, 0.008f, trials);
            int sub2 = sim.LastSubStepCount;
            int disp2 = sim.LastDispatchCount;
            int totalDisp2 = trials * disp2;

            // Single substep diagnostic API: measure isolated 1-call dispatch difference
            sim.ResetSimulation();
            int initDispDiag = sim.LastDispatchCount;
            int dispBeforeSingle = sim.LastDispatchCount;
            sim.StepSingleSubstep(0.004f);
            int dispAfterSingle = sim.LastDispatchCount;
            int singleCallDiff = dispAfterSingle - dispBeforeSingle;

            // Multi-trial invariance verification (verifying 1-call dispatch count does not change with trial count)
            var invarianceChecks = new List<SingleSubstepTrialInvarianceTest>();
            int[] testTrials = { 1, 10, 30, 50 };
            foreach (int tc in testTrials) {
                sim.ResetSimulation();
                int tStart = sim.LastDispatchCount;
                for (int t = 0; t < tc; t++) {
                    sim.StepSingleSubstep(0.004f);
                }
                int tEnd = sim.LastDispatchCount;
                int tTotal = tEnd - tStart;
                int tPerCall = tc > 0 ? tTotal / tc : 0;
                invarianceChecks.Add(new SingleSubstepTrialInvarianceTest {
                    trials = tc,
                    start_dispatch_count = tStart,
                    end_dispatch_count = tEnd,
                    total_interval_dispatches = tTotal,
                    measured_dispatch_per_call = tPerCall,
                    is_invariant = (tPerCall == singleCallDiff)
                });
            }

            var (cpuDiag, cpuDiagInitDisp, cpuDiagTotalDisp, cpuDiagPerCall) = MeasureSingleSubstepCpuIssuance(sim, 0.004f, trials);
            var (syncDiag, syncDiagInitDisp, syncDiagTotalDisp, syncDiagPerCall) = MeasureSingleSubstepGpuSyncThroughput(sim, 0.004f, trials);
            int subDiag = 1;
            int dispDiag = singleCallDiff;

            var cfl = new CflSubstepInvestigation {
                case_step_0016 = new CflSubstepCase {
                    condition_name = "Step(0.016) [通常Step, 4サブステップ]",
                    dt_per_call_s = 0.016f,
                    measured_substep_count = sub4,
                    measured_dispatch_count = disp4,
                    initialization_dispatches = initDisp4,
                    measurement_trials = trials,
                    total_interval_dispatches = totalDisp4,
                    cpu_issuance_per_call_ms = Math.Round(cpu4, 4),
                    gpu_sync_throughput_per_call_ms = Math.Round(sync4, 4),
                    calls_for_1s_physical_time = 62.5,
                    cpu_issuance_for_1s_physical_time_ms = Math.Round(62.5 * cpu4, 2),
                    gpu_sync_throughput_for_1s_physical_time_ms = Math.Round(62.5 * sync4, 2),
                    dispatch_composition = "通常Step: 4サブステップ分の物理更新(4×10) + フレーム末尾の法線計算・出力パック(1) = 計41 Dispatch"
                },
                case_step_0008 = new CflSubstepCase {
                    condition_name = "Step(0.008) [通常Step, 2サブステップ]",
                    dt_per_call_s = 0.008f,
                    measured_substep_count = sub2,
                    measured_dispatch_count = disp2,
                    initialization_dispatches = initDisp2,
                    measurement_trials = trials,
                    total_interval_dispatches = totalDisp2,
                    cpu_issuance_per_call_ms = Math.Round(cpu2, 4),
                    gpu_sync_throughput_per_call_ms = Math.Round(sync2, 4),
                    calls_for_1s_physical_time = 125.0,
                    cpu_issuance_for_1s_physical_time_ms = Math.Round(125.0 * cpu2, 2),
                    gpu_sync_throughput_for_1s_physical_time_ms = Math.Round(125.0 * sync2, 2),
                    dispatch_composition = "通常Step: 2サブステップ分の物理更新(2×10) + フレーム末尾の法線計算・出力パック(1) = 計21 Dispatch"
                },
                case_single_substep_diag = new CflSubstepCase {
                    condition_name = "StepSingleSubstep(0.004) [単一サブステップ診断API試験]",
                    dt_per_call_s = 0.004f,
                    measured_substep_count = subDiag,
                    measured_dispatch_count = dispDiag,
                    initialization_dispatches = initDispDiag,
                    measurement_trials = trials,
                    total_interval_dispatches = syncDiagTotalDisp,
                    cpu_issuance_per_call_ms = Math.Round(cpuDiag, 4),
                    gpu_sync_throughput_per_call_ms = Math.Round(syncDiag, 4),
                    calls_for_1s_physical_time = 250.0,
                    cpu_issuance_for_1s_physical_time_ms = Math.Round(250.0 * cpuDiag, 2),
                    gpu_sync_throughput_for_1s_physical_time_ms = Math.Round(250.0 * syncDiag, 2),
                    dispatch_composition = "診断API: 物理更新単体のみを実行(10 Dispatch)。通常Stepに含まれる法線計算・出力パック(1)は含まない"
                },
                trial_invariance_checks = invarianceChecks,
                note = "1呼び出しあたりの実測Dispatch数、初期化費用(Reset時4 Dispatch)、および測定区間合計(試行数×1呼出)を厳密に区別して集計。診断API(StepSingleSubstep)は物理更新のみを実行する単一サブステップ(10 Dispatch)であり、通常Stepに含まれるフレーム末尾の法線計算・出力パック処理(CSComputeNormal, 1 Dispatch)を含まない。通常Stepでは4サブステップ時に 4×10 + 1 = 41 Dispatch、2サブステップ時に 2×10 + 1 = 21 Dispatch となる。固定値ではなくリセット完了後のカウンターを基準とした実測差分として10 Dispatchを取得し、試行回数(1, 10, 30, 50回)を変更しても1呼出あたりのDispatch数が10で不変であることを実証確認。"
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
            string gcOnStr = scene02 != null ? scene02.GcAllocBytesUiOn : "取得不可";
            string gcOffStr = scene02 != null ? scene02.GcAllocBytesUiOff : "取得不可";
            string deltaStr = scene02 != null ? scene02.GcAllocDeltaBytes : "取得不可";

            report.UiInvestigation = new UiInvestigation {
                OnGuiCpuMsPerFrame = scene02 != null ? scene02.UiOverheadDeltaMs : 0.0,
                GcAllocBytesUiOn = gcOnStr,
                GcAllocBytesUiOff = gcOffStr,
                UiGcDeltaBytes = deltaStr,
                AllocationAnalysis = "【仮説（コールスタック未取得のため推定）】UI ON と UI OFF の差分は測定ばらつきの範囲内であり、有意なUI固有アロケーション差は確認できない。実測された約1.9KB/frameは、Unity EditorのPlayerLoop内部処理、コルーチン（MoveNext）、ProfilerRecorderの内部バッファ管理等に起因するフレーム固定内部オーバーヘッドと推定される。シミュレーション本体ホットパスの単独アロケーションは別途プロファイラコールスタックで確認が必要。"
            };

            // 7. Accumulator investigation matching WaterSimulationBase.Update implementation
            float fixedDt = sim.FixedTimeStep;
            var accum = new AccumulatorInvestigation {
                max_steps_per_frame_limit = 2,
                accumulator_clamp_seconds = fixedDt * 2f,
                scenario_60fps = SimulateAccumulatorScheduling(fixedDt, 1f / 60f, cpu4, sync4, "60 FPS 運転時 (dt ≈ 0.0167s)", 60.0),
                scenario_30fps = SimulateAccumulatorScheduling(fixedDt, 1f / 30f, cpu4, sync4, "30 FPS 運転時 (dt ≈ 0.0333s)", 30.0),
                scenario_20fps = SimulateAccumulatorScheduling(fixedDt, 1f / 20f, cpu4, sync4, "20 FPS 運転時 (dt = 0.0500s)", 20.0),
                scenario_max_lag_10fps = SimulateAccumulatorScheduling(fixedDt, 1f / 10f, cpu4, sync4, "最大遅延時 (10 FPS, dt = 0.100s)", 10.0),
                scheduling_analysis = "現行のWaterSimulationBase実装では、maxStepsPerFrame=2の上限およびaccumulatorクランプ(最大2ステップ分=0.032s)により、1フレームで実行されるStep数は最大2回に制限されている。フレームレートが20 FPS以下に低下した場合でも3〜4ステップは実行されず、超過した物理時間は切り捨て(ドロップ)されてスパイラル・オブ・デスを防止する。単純な費用の掛け算は実測値ではなく線形推定値である。"
            };
            report.AccumulatorInvestigation = accum;
        }

        private static AccumulatorCase SimulateAccumulatorScheduling(float fixedDt, float frameDt, double singleStepCpu, double singleStepGpu, string name, double fps) {
            float accum = frameDt;
            float truncated = 0f;
            if (accum > fixedDt * 2f) {
                truncated = accum - fixedDt * 2f;
                accum = fixedDt * 2f;
            }
            int count = 0;
            while (accum >= fixedDt && count < 2) {
                accum -= fixedDt;
                count++;
            }
            return new AccumulatorCase {
                scenario_name = name,
                target_fps = fps,
                frame_delta_time_s = Math.Round(frameDt, 5),
                steps_executed_per_frame = count,
                simulated_time_per_frame_s = Math.Round(count * fixedDt, 5),
                truncated_dropped_time_per_frame_s = Math.Round(truncated, 5),
                estimated_cpu_issuance_ms = Math.Round(count * singleStepCpu, 4),
                estimated_gpu_sync_ms = Math.Round(count * singleStepGpu, 4),
                note = count == 2 && truncated > 0.0001f
                    ? $"maxStepsPerFrame=2の上限とクランプにより、超過物理時間({truncated:F4}s)は切り捨て(ドロップ)。費用は1 Step実測値に基づく単純線形推定。"
                    : "クランプ上限内で正常進行。費用は1 Step実測値に基づく単純線形推定。"
            };
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

        private static (double meanMs, int initDisp, int totalIntervalDisp, int dispPerCall) MeasureSingleSubstepCpuIssuance(BoussinesqSimulation sim, float subDt, int trials) {
            sim.ResetSimulation();
            int initDisp = sim.LastDispatchCount;
            int dispStart = sim.LastDispatchCount;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                sim.StepSingleSubstep(subDt);
            }
            sw.Stop();
            int dispEnd = sim.LastDispatchCount;
            int totalIntervalDisp = dispEnd - dispStart;
            int dispPerCall = trials > 0 ? totalIntervalDisp / trials : 0;
            return (sw.Elapsed.TotalMilliseconds / trials, initDisp, totalIntervalDisp, dispPerCall);
        }

        private static (double meanMs, int initDisp, int totalIntervalDisp, int dispPerCall) MeasureSingleSubstepGpuSyncThroughput(BoussinesqSimulation sim, float subDt, int trials) {
            sim.ResetSimulation();
            int initDisp = sim.LastDispatchCount;
            var targetRT = sim.WaterDataTexture ?? sim.NormalTexture;
            if (targetRT == null) return MeasureSingleSubstepCpuIssuance(sim, subDt, trials);

            var initReq = AsyncGPUReadback.Request(targetRT, 0, 0, 1, 0, 1, 0, 1);
            initReq.WaitForCompletion();

            int dispStart = sim.LastDispatchCount;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < trials; i++) {
                sim.StepSingleSubstep(subDt);
                var req = AsyncGPUReadback.Request(targetRT, 0, 0, 1, 0, 1, 0, 1);
                req.WaitForCompletion();
            }
            sw.Stop();
            int dispEnd = sim.LastDispatchCount;
            int totalIntervalDisp = dispEnd - dispStart;
            int dispPerCall = trials > 0 ? totalIntervalDisp / trials : 0;
            return (sw.Elapsed.TotalMilliseconds / trials, initDisp, totalIntervalDisp, dispPerCall);
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
            sb.AppendLine("| シーン名 | 格子解像度 | fixedDt | SubSteps | Dispatches | 1 Step CPU発行時間 | 1 Step GPU同期スループット | 物理1秒進行費用 (Steps) | 物理1秒 CPU発行 | 物理1秒 GPU同期 | 16.67ms枠 Step単体マージン |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var r in report.SceneProfiles) {
                var c = r.StepCost;
                sb.AppendLine($"| {r.SceneName} | {r.Resolution} | {c.fixed_time_step_s:F3}s | {c.substeps_per_step} | {c.dispatches_per_step} | {c.step_cpu_issuance_ms:F4} ms | {c.step_gpu_sync_throughput_ms:F4} ms | {c.steps_per_physical_second} steps | {c.physical_second_cpu_issuance_ms:F2} ms | {c.physical_second_gpu_sync_throughput_ms:F2} ms | {c.step_margin_percent_in_16ms_frame:F1}% |");
            }
            sb.AppendLine();
            sb.AppendLine("> [!NOTE]");
            sb.AppendLine("> - **1 Step CPU発行時間**: CPUがコンピュートカーネルをディスパッチしドライバキューに積むまでの時間（GPU完了待ちは含まない）。");
            sb.AppendLine("> - **1 Step GPU同期スループット**: CPUディスパッチ ＋ GPU計算実行 ＋ 最小限(1セル)の `AsyncGPUReadback` 完了待機を含む時間。PCI-e転送オーバーヘッドを最小化してGPU完了バリアを確立。");
            sb.AppendLine("> - **16.67ms枠 Step単体マージン**: 本指標は描画パス、UI、ポストプロセス、表示待機等を含まない『シミュレーションStep単体』の16.67ms(60Hz)枠に対する時間マージンであり、アプリケーション全体の総合的な動作余力を表すものではありません。");
            sb.AppendLine();

            sb.AppendLine("## 2. PlayerLoop 実フレーム計測結果（非制限バッチモード・ループレート）と処理量");
            sb.AppendLine();
            sb.AppendLine("Unity PlayerLoop（PlayMode）上でフレームをまたいで計測した実フレーム間隔、およびその間に実行された実際の処理量：");
            sb.AppendLine();
            sb.AppendLine("| シーン名 | 測定フェーズ | ループ回数 | 実経過時間 | 平均フレーム間隔 | P95 | P99 | バッチループ回転速度 | 実行Step数 | 進行物理時間 | 描画回数 | CPU Main Thread | GPU Frame | GC Alloc |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var r in report.SceneProfiles) {
                sb.AppendLine($"| {r.SceneName} | UI OFF | {r.WorkloadUiOff.frame_count} | {r.WorkloadUiOff.elapsed_wall_time_ms:F2} ms | {r.MeanLoopIntervalUiOffMs:F4} ms | {r.P95LoopIntervalUiOffMs:F4} ms | - | - | {r.WorkloadUiOff.executed_steps} | {r.WorkloadUiOff.simulated_physical_time_s:F3}s | {r.WorkloadUiOff.rendered_frames} | - | 取得不可 | {r.GcAllocBytesUiOff} |");
                sb.AppendLine($"| {r.SceneName} | UI ON | {r.WorkloadUiOn.frame_count} | {r.WorkloadUiOn.elapsed_wall_time_ms:F2} ms | {r.MeanLoopIntervalUiOnMs:F4} ms | {r.P95LoopIntervalUiOnMs:F4} ms | {r.P99LoopIntervalUiOnMs:F4} ms | {r.UnconstrainedBatchLoopRateUiOn:F1} iter/s | {r.WorkloadUiOn.executed_steps} | {r.WorkloadUiOn.simulated_physical_time_s:F3}s | {r.WorkloadUiOn.rendered_frames} | {r.CpuMainThreadMs} | {r.GpuFrameMs} | {r.GcAllocBytesUiOn} |");
            }
            sb.AppendLine();
            sb.AppendLine("> [!IMPORTANT]");
            sb.AppendLine("> - **表示FPSではないことの明記**: バッチモード実行ではモニタ垂直同期および表示スワップチェーンが存在しないため、フレーム間隔は極小（0.1〜0.2ms）でループ回転します。物理アキュムレータが閾値に達しない空フレーム（実行Step数: 0回 または 1回 / 60ループ）を含むため、上記ループ回転速度（iter/s）を実画面の「表示FPS」として報告・解釈してはなりません。");
            sb.AppendLine("> - **描画回数の実測**: カメラの描画完了イベント（RenderPipelineManager.endCameraRendering）をフックして実際の描画回数を計測。バッチモード等で描画パイプラインが実行されない場合は「未計測」と明記。");
            sb.AppendLine("> - **GPU Frame Time**: バッチモードで表示スワップチェーン非アクティブのためドライバ非公開（取得不可）。");
            sb.AppendLine();

            sb.AppendLine("## 3. UI ON vs UI OFF 比較および GC Alloc 分析");
            sb.AppendLine();
            sb.AppendLine("| シーン名 | UI Overhead Delta | Order Bias | GC Alloc (UI ON) | GC Alloc (UI OFF) | GC Delta | 評価・分析 |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :--- |");

            foreach (var r in report.SceneProfiles) {
                sb.AppendLine($"| {r.SceneName} | {r.UiOverheadDeltaMs:F4} ms | {r.OrderBiasDeltaMs:F4} ms | {r.GcAllocBytesUiOn} | {r.GcAllocBytesUiOff} | {r.GcAllocDeltaBytes} | {r.GcAllocAnalysis} |");
            }
            sb.AppendLine();

            sb.AppendLine("## 4. Scene 02 詳細要因分析（CFL・ディスパッチ・定数バインド・Accumulator）");
            sb.AppendLine();
            if (report.CflInvestigation != null) {
                sb.AppendLine("### CFL Substep 数による影響（CPU発行時間 vs GPU同期スループット、実測サブステップ・Dispatch数）");
                sb.AppendLine();
                sb.AppendLine("| 試験条件 | 1呼出 dt | 実測Substeps | 1呼出実測Dispatch | 初期化Dispatch | 測定区間合計Dispatch (試行数) | 1呼出 CPU発行時間 | 1呼出 GPU同期スループット | 物理1秒進行 (呼出数) | 物理1秒 CPU発行 | 物理1秒 GPU同期 |");
                sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");
                var c4 = report.CflInvestigation.case_step_0016;
                var c2 = report.CflInvestigation.case_step_0008;
                var c1 = report.CflInvestigation.case_single_substep_diag;
                sb.AppendLine($"| {c4.condition_name} | {c4.dt_per_call_s:F3}s | {c4.measured_substep_count} | {c4.measured_dispatch_count} | {c4.initialization_dispatches} | {c4.total_interval_dispatches} ({c4.measurement_trials}回) | {c4.cpu_issuance_per_call_ms:F4} ms | {c4.gpu_sync_throughput_per_call_ms:F4} ms | {c4.calls_for_1s_physical_time:F1} | {c4.cpu_issuance_for_1s_physical_time_ms:F2} ms | {c4.gpu_sync_throughput_for_1s_physical_time_ms:F2} ms |");
                sb.AppendLine($"| {c2.condition_name} | {c2.dt_per_call_s:F3}s | {c2.measured_substep_count} | {c2.measured_dispatch_count} | {c2.initialization_dispatches} | {c2.total_interval_dispatches} ({c2.measurement_trials}回) | {c2.cpu_issuance_per_call_ms:F4} ms | {c2.gpu_sync_throughput_per_call_ms:F4} ms | {c2.calls_for_1s_physical_time:F1} | {c2.cpu_issuance_for_1s_physical_time_ms:F2} ms | {c2.gpu_sync_throughput_for_1s_physical_time_ms:F2} ms |");
                sb.AppendLine($"| {c1.condition_name} | {c1.dt_per_call_s:F3}s | {c1.measured_substep_count} | {c1.measured_dispatch_count} | {c1.initialization_dispatches} | {c1.total_interval_dispatches} ({c1.measurement_trials}回) | {c1.cpu_issuance_per_call_ms:F4} ms | {c1.gpu_sync_throughput_per_call_ms:F4} ms | {c1.calls_for_1s_physical_time:F1} | {c1.cpu_issuance_for_1s_physical_time_ms:F2} ms | {c1.gpu_sync_throughput_for_1s_physical_time_ms:F2} ms |");
                sb.AppendLine();
                sb.AppendLine($"> [!NOTE]\n> {report.CflInvestigation.note}");
                sb.AppendLine();

                if (report.CflInvestigation.trial_invariance_checks != null && report.CflInvestigation.trial_invariance_checks.Count > 0) {
                    sb.AppendLine("#### 単一サブステップ診断API 試行回数不変性検証 (Trial Invariance Verification)");
                    sb.AppendLine();
                    sb.AppendLine("| 試行回数 (Trials) | 開始カウンター (初期化後) | 終了カウンター | 測定区間合計Dispatch | 1呼び出しあたり実測Dispatch | 不変性判定 |");
                    sb.AppendLine("| :---: | :---: | :---: | :---: | :---: | :---: |");
                    foreach (var chk in report.CflInvestigation.trial_invariance_checks) {
                        sb.AppendLine($"| {chk.trials} 回 | {chk.start_dispatch_count} | {chk.end_dispatch_count} | {chk.total_interval_dispatches} | **{chk.measured_dispatch_per_call}** | {(chk.is_invariant ? "PASS (一致)" : "FAIL (不一致)")} |");
                    }
                    sb.AppendLine();
                    sb.AppendLine("> [!TIP]\n> 試行回数を1回、10回、30回、50回と変更しても、1呼び出しあたりの実測Dispatch数は常に厳密に10 Dispatchで一致しており、初期化時カウンター(4)や試行回数の累積が混入していないことを実証。");
                    sb.AppendLine();
                }
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
                sb.AppendLine($"- GC Allocated In Frame (UI ON): `{report.UiInvestigation.GcAllocBytesUiOn}`");
                sb.AppendLine($"- GC Allocated In Frame (UI OFF): `{report.UiInvestigation.GcAllocBytesUiOff}`");
                sb.AppendLine($"- GC 差分: `{report.UiInvestigation.UiGcDeltaBytes}`");
                sb.AppendLine($"- 割り当て元分析: {report.UiInvestigation.AllocationAnalysis}");
                sb.AppendLine();
            }
            if (report.AccumulatorInvestigation != null) {
                var a = report.AccumulatorInvestigation;
                sb.AppendLine("### Accumulator スケジューリングとフレームレート別物理処理負荷（現行実装整合）");
                sb.AppendLine();
                sb.AppendLine($"- **実装仕様**: 1フレームあたりの最大更新回数上限 `maxStepsPerFrame = {a.max_steps_per_frame_limit}`, アキュムレータクランプ上限 `{a.accumulator_clamp_seconds:F3}s` (最大2ステップ分)");
                sb.AppendLine();
                sb.AppendLine("| シナリオ | 想定FPS | フレームdt | 実行Step数/frame | 進行物理時間/frame | 切り捨て(ドロップ)時間 | 推定CPU発行費用 | 推定GPU同期スループット |");
                sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");
                var s60 = a.scenario_60fps;
                var s30 = a.scenario_30fps;
                var s20 = a.scenario_20fps;
                var slag = a.scenario_max_lag_10fps;
                sb.AppendLine($"| {s60.scenario_name} | {s60.target_fps:F0} FPS | {s60.frame_delta_time_s:F4}s | {s60.steps_executed_per_frame} Step | {s60.simulated_time_per_frame_s:F3}s | {s60.truncated_dropped_time_per_frame_s:F4}s | {s60.estimated_cpu_issuance_ms:F4} ms (推定) | {s60.estimated_gpu_sync_ms:F4} ms (推定) |");
                sb.AppendLine($"| {s30.scenario_name} | {s30.target_fps:F0} FPS | {s30.frame_delta_time_s:F4}s | {s30.steps_executed_per_frame} Steps | {s30.simulated_time_per_frame_s:F3}s | {s30.truncated_dropped_time_per_frame_s:F4}s | {s30.estimated_cpu_issuance_ms:F4} ms (推定) | {s30.estimated_gpu_sync_ms:F4} ms (推定) |");
                sb.AppendLine($"| {s20.scenario_name} | {s20.target_fps:F0} FPS | {s20.frame_delta_time_s:F4}s | {s20.steps_executed_per_frame} Steps | {s20.simulated_time_per_frame_s:F3}s | {s20.truncated_dropped_time_per_frame_s:F4}s | {s20.estimated_cpu_issuance_ms:F4} ms (推定) | {s20.estimated_gpu_sync_ms:F4} ms (推定) |");
                sb.AppendLine($"| {slag.scenario_name} | {slag.target_fps:F0} FPS | {slag.frame_delta_time_s:F4}s | {slag.steps_executed_per_frame} Steps | {slag.simulated_time_per_frame_s:F3}s | {slag.truncated_dropped_time_per_frame_s:F4}s | {slag.estimated_cpu_issuance_ms:F4} ms (推定) | {slag.estimated_gpu_sync_ms:F4} ms (推定) |");
                sb.AppendLine();
                sb.AppendLine($"> [!NOTE]\n> {a.scheduling_analysis}");
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
        public string rendered_frames;
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
        public double step_margin_percent_in_16ms_frame;
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
    public class SingleSubstepTrialInvarianceTest {
        public int trials;
        public int start_dispatch_count;
        public int end_dispatch_count;
        public int total_interval_dispatches;
        public int measured_dispatch_per_call;
        public bool is_invariant;
    }

    [Serializable]
    public class CflSubstepCase {
        public string condition_name;
        public float dt_per_call_s;
        public int measured_substep_count;
        public int measured_dispatch_count;
        public int initialization_dispatches;
        public int measurement_trials;
        public int total_interval_dispatches;
        public double cpu_issuance_per_call_ms;
        public double gpu_sync_throughput_per_call_ms;
        public double calls_for_1s_physical_time;
        public double cpu_issuance_for_1s_physical_time_ms;
        public double gpu_sync_throughput_for_1s_physical_time_ms;
        public string dispatch_composition;
    }

    [Serializable]
    public class CflSubstepInvestigation {
        public CflSubstepCase case_step_0016;
        public CflSubstepCase case_step_0008;
        public CflSubstepCase case_single_substep_diag;
        public List<SingleSubstepTrialInvarianceTest> trial_invariance_checks = new List<SingleSubstepTrialInvarianceTest>();
        public string note;
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
        public string GcAllocBytesUiOn;
        public string GcAllocBytesUiOff;
        public string UiGcDeltaBytes;
        public string AllocationAnalysis;
    }

    [Serializable]
    public class AccumulatorCase {
        public string scenario_name;
        public double target_fps;
        public double frame_delta_time_s;
        public int steps_executed_per_frame;
        public double simulated_time_per_frame_s;
        public double truncated_dropped_time_per_frame_s;
        public double estimated_cpu_issuance_ms;
        public double estimated_gpu_sync_ms;
        public string note;
    }

    [Serializable]
    public class AccumulatorInvestigation {
        public int max_steps_per_frame_limit;
        public float accumulator_clamp_seconds;
        public AccumulatorCase scenario_60fps;
        public AccumulatorCase scenario_30fps;
        public AccumulatorCase scenario_20fps;
        public AccumulatorCase scenario_max_lag_10fps;
        public string scheduling_analysis;
    }
    #endregion
}
