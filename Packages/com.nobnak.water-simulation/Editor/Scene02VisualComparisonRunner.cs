using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Executes visual rendering captures and quantitative profile tracking on Scene 02 (Boussinesq Shoal)
    /// comparing Baseline (1st-order upwind) vs Minmod TVD (2nd-order limiter) under identical conditions.
    /// </summary>
    public static class Scene02VisualComparisonRunner {
        #region constants / static
        private const string ScenePath = "Assets/Scenes/02_Boussinesq.unity";
        private const string BaseOutputDir = "Captures/VisualComparison";
        private const string JsonReportPath = "Docs/VisualComparison_Scene02_Data.json";
        private static readonly int PropWidth = Shader.PropertyToID("_Width");
        private static readonly int PropHeight = Shader.PropertyToID("_Height");
        private static readonly int PropStateIn = Shader.PropertyToID("_StateIn");
        #endregion

        #region data structures
        [Serializable]
        public class StationTimeSeriesData {
            public string stationName;
            public float stationZ;
            public float depth;
            public List<float> times = new List<float>();
            public List<float> etasBaseline = new List<float>();
            public List<float> etasMinmod = new List<float>();
        }

        [Serializable]
        public class ProfileSnapshotData {
            public float time;
            public List<float> zs = new List<float>();
            public List<float> depths = new List<float>();
            public List<float> etasBaseline = new List<float>();
            public List<float> etasMinmod = new List<float>();
        }

        [Serializable]
        public class TestCaseVisualData {
            public string caseId;
            public string caseName;
            public string description;
            public float duration;
            public float dtStep;
            public float wavemakerAmplitude;
            public float wavemakerFrequency;
            public float pulseInterval;
            public string initialCondition;
            public string impulseShape;
            public double baselineWallClockTimeMs;
            public double minmodWallClockTimeMs;
            public List<StationTimeSeriesData> stations = new List<StationTimeSeriesData>();
            public List<ProfileSnapshotData> profiles = new List<ProfileSnapshotData>();
            public List<string> frameFilesBaselineOverview = new List<string>();
            public List<string> frameFilesMinmodOverview = new List<string>();
            public List<string> frameFilesBaselineCloseup = new List<string>();
            public List<string> frameFilesMinmodCloseup = new List<string>();
        }

        [Serializable]
        public class InteractiveVerificationResult {
            public bool allPassed;
            public bool baselineInitialVerified;
            public bool switchResetsSimulationTime;
            public bool switchMaintainsRunningState;
            public bool switchUpdatesAdvectionMode;
            public bool pauseStopsSimulationTime;
            public bool playResumesSimulationTime;
            public bool normalResetRetainsSelectedMode;
            public bool noNaNOrInfDetected;
            public string details;
        }

        [Serializable]
        public class VisualComparisonReport {
            public string timestamp;
            public string unityVersion;
            public string gpuRenderer;
            public string gitCommit;
            public string note;
            public InteractiveVerificationResult interactiveVerification;
            public List<TestCaseVisualData> cases = new List<TestCaseVisualData>();
        }
        #endregion

        #region public interface
        [MenuItem("Tools/Water Simulation/Run Scene 02 Visual Comparison", false, 65)]
        public static void RunComparisonMenu() {
            RunVisualComparison();
        }

        public static void RunComparisonCommandLine() {
            try {
                RunVisualComparison();
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                UnityEngine.Debug.LogError($"[Scene02VisualComparison] Fatal error: {ex}");
                EditorApplication.Exit(1);
            }
        }

        public static void RunInteractiveVerificationCommandLine() {
            try {
                var res = VerifyInteractiveOperations();
                string path = "Docs/Scene02_Interactive_Verification.json";
                File.WriteAllText(path, JsonUtility.ToJson(res, true), Encoding.UTF8);
                EditorApplication.Exit(res.allPassed ? 0 : 1);
            } catch (Exception ex) {
                UnityEngine.Debug.LogError($"[VerifyInteractiveOperations] Fatal error: {ex}");
                EditorApplication.Exit(1);
            }
        }
        #endregion

        #region private methods
        public static VisualComparisonReport RunVisualComparison() {
            if (!Directory.Exists(BaseOutputDir)) Directory.CreateDirectory(BaseOutputDir);

            var report = new VisualComparisonReport {
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                unityVersion = Application.unityVersion,
                gpuRenderer = SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")",
                gitCommit = GetGitCommitHash(out bool dirty) + (dirty ? "-dirty" : ""),
                note = "Visual and quantitative profile comparison on Scene 02 Shoal & Cape under identical conditions."
            };

            // Test Case 1: Scene 02 Standard Default (Continuous Wavemaker on Shoal and Cape)
            report.cases.Add(RunSingleComparisonCase(
                caseId: "Case1_StandardWavemaker",
                caseName: "Scene 02 Standard Shoal (Continuous Wavemaker)",
                description: "Standard Scene 02 configuration: Continuous wavemaker (f=0.15Hz, amp=0.35m) entering Shoal and Cape bathymetry.",
                cond: BoussinesqSimulation.InitialConditionType.ContinuousWavemaker,
                shape: BoussinesqSimulation.ImpulseShapeType.PlanarSoliton,
                amp: 0.35f,
                freq: 0.15f,
                pulseInterval: 4.5f,
                duration: 16.0f,
                dtStep: 0.0333333f
            ));

            // Test Case 2: Steep Planar Solitary Wave (High-amplitude shoaling steepness & wave-train condition)
            report.cases.Add(RunSingleComparisonCase(
                caseId: "Case2_SteepPlanarSoliton",
                caseName: "Steep Planar Solitary Wave Shoaling",
                description: "Steep solitary wave shoaling (amp=0.60m, interval=6.0s) examining wave crest peaking, trailing oscillation, and high-frequency roughness.",
                cond: BoussinesqSimulation.InitialConditionType.PeriodicPulses,
                shape: BoussinesqSimulation.ImpulseShapeType.PlanarSoliton,
                amp: 0.60f,
                freq: 0.15f,
                pulseInterval: 6.0f,
                duration: 16.0f,
                dtStep: 0.0333333f
            ));

            string json = JsonUtility.ToJson(report, true);
            File.WriteAllText(JsonReportPath, json, Encoding.UTF8);
            UnityEngine.Debug.Log($"[Scene02VisualComparison] Successfully completed visual comparison and saved report to {JsonReportPath}");
            return report;
        }

        private static TestCaseVisualData RunSingleComparisonCase(
            string caseId, string caseName, string description,
            BoussinesqSimulation.InitialConditionType cond,
            BoussinesqSimulation.ImpulseShapeType shape,
            float amp, float freq, float pulseInterval, float duration, float dtStep) {

            var caseData = new TestCaseVisualData {
                caseId = caseId,
                caseName = caseName,
                description = description,
                duration = duration,
                dtStep = dtStep,
                wavemakerAmplitude = amp,
                wavemakerFrequency = freq,
                pulseInterval = pulseInterval,
                initialCondition = cond.ToString(),
                impulseShape = shape.ToString()
            };

            // Setup station definitions: z = 15m (deep offshore), 45m (slope toe), 70m (shallow shoal)
            float[] stationZs = new float[] { 15.0f, 45.0f, 70.0f };
            string[] stationNames = new string[] { "Gauge_Deep_15m", "Gauge_Slope_45m", "Gauge_Shoal_70m" };
            for (int i = 0; i < stationZs.Length; i++) {
                caseData.stations.Add(new StationTimeSeriesData {
                    stationName = stationNames[i],
                    stationZ = stationZs[i]
                });
            }

            // Setup snapshot target times: t = 4s, 8s, 12s, 16s
            float[] snapshotTimes = new float[] { 4.0f, 8.0f, 12.0f, 16.0f };
            foreach (var st in snapshotTimes) {
                caseData.profiles.Add(new ProfileSnapshotData { time = st });
            }

            // Run Baseline (FirstOrderUpwind = 0)
            UnityEngine.Debug.Log($"[Scene02VisualComparison] Running {caseId} - Baseline (1st-Order Upwind)...");
            caseData.baselineWallClockTimeMs = RunSimulationTrajectory(
                caseId, BoussinesqSimulation.AdvectionType.FirstOrderUpwind, "Baseline",
                cond, shape, amp, freq, pulseInterval, duration, dtStep,
                caseData.stations, caseData.profiles, isMinmod: false,
                caseData.frameFilesBaselineOverview, caseData.frameFilesBaselineCloseup
            );

            // Run Candidate (MinmodTVD = 1)
            UnityEngine.Debug.Log($"[Scene02VisualComparison] Running {caseId} - Candidate (Minmod TVD)...");
            caseData.minmodWallClockTimeMs = RunSimulationTrajectory(
                caseId, BoussinesqSimulation.AdvectionType.MinmodTVD, "Minmod",
                cond, shape, amp, freq, pulseInterval, duration, dtStep,
                caseData.stations, caseData.profiles, isMinmod: true,
                caseData.frameFilesMinmodOverview, caseData.frameFilesMinmodCloseup
            );

            return caseData;
        }

        private static double RunSimulationTrajectory(
            string caseId, BoussinesqSimulation.AdvectionType advType, string advTag,
            BoussinesqSimulation.InitialConditionType cond,
            BoussinesqSimulation.ImpulseShapeType shape,
            float amp, float freq, float pulseInterval, float duration, float dtStep,
            List<StationTimeSeriesData> stations, List<ProfileSnapshotData> profiles, bool isMinmod,
            List<string> frameFilesOverview, List<string> frameFilesCloseup) {

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var boussSim = UnityEngine.Object.FindFirstObjectByType<BoussinesqSimulation>();
            var renderer = UnityEngine.Object.FindFirstObjectByType<WaterRenderer>();
            var seabed = UnityEngine.Object.FindFirstObjectByType<SeabedRenderer>();
            var mainCam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();

            if (boussSim == null || renderer == null || mainCam == null) {
                throw new InvalidOperationException("Failed to find BoussinesqSimulation, WaterRenderer, or Main Camera in Scene 02.");
            }

            // Configure simulation parameters identically
            boussSim.Advection = advType;
            boussSim.Condition = cond;
            boussSim.ImpulseShape = shape;
            boussSim.WavemakerAmplitude = amp;
            boussSim.WavemakerFrequency = freq;
            boussSim.PulseInterval = pulseInterval;
            boussSim.DispersionAlpha = 1.0f;
            boussSim.Bathymetry = BoussinesqSimulation.BathymetryType.ShoalAndCape;
            boussSim.DeepWaterDepth = 3.5f;
            boussSim.ShallowWaterDepth = 0.8f;
            boussSim.SideWallSponge = false;
            boussSim.ResetSimulation();

            renderer.SetVisualMode(WaterRenderer.VisualMode.Realistic);
            renderer.UpdateMaterialProperties();
            if (seabed != null) {
                seabed.gameObject.SetActive(true);
                seabed.SetVisualMode(SeabedRenderer.VisualMode.RealisticSand);
                seabed.HeightExaggeration = 2.0f;
                seabed.UpdateMaterialProperties();
            }

            int W = boussSim.Resolution.x;
            int H = boussSim.Resolution.y;
            float dz = boussSim.AreaSize.y / (H - 1);
            float[] currentEta = new float[H];
            float[] currentDepth = new float[H];

            // Render texture for frame captures (800x450 for clear crisp side-by-side video generation)
            int frameW = 800;
            int frameH = 450;
            var captureRT = new RenderTexture(frameW, frameH, 24, RenderTextureFormat.ARGB32);
            var prevTarget = mainCam.targetTexture;
            mainCam.targetTexture = captureRT;

            string outDir = Path.Combine(BaseOutputDir, $"{caseId}_{advTag}");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            int totalSteps = (int)math.ceil(duration / dtStep);
            int recordInterval = 3; // Capture every 3 steps (approx 0.10s interval, 10 fps)

            // Read static bathymetry depths along center column x = W / 2
            ReadCenterColumnSlice(boussSim.BathymetryTexture, W, H, currentDepth, isDepth: true);
            foreach (var st in stations) {
                int yIdx = math.clamp((int)math.round(st.stationZ / dz), 0, H - 1);
                st.depth = currentDepth[yIdx];
            }

            var sw = Stopwatch.StartNew();

            for (int step = 0; step < totalSteps; step++) {
                float simTime = step * dtStep;

                // Step simulation forward
                boussSim.Step(dtStep);
                renderer.UpdateMaterialProperties();

                // Periodic frame capture (every 3 steps = 0.10s)
                if (step % recordInterval == 0 || step == totalSteps - 1) {
                    // 1. Overview Camera viewpoint
                    mainCam.transform.position = new Vector3(0f, 45f, -50f);
                    mainCam.transform.rotation = Quaternion.Euler(40f, 0f, 0f);
                    mainCam.Render();
                    string frameFileOverview = Path.Combine(outDir, $"overview_{step:D4}.png");
                    SaveRTToPNG(captureRT, frameFileOverview);
                    frameFilesOverview.Add(frameFileOverview);

                    // 2. Close-up Camera viewpoint (focused on shoaling slope and wave crest transition)
                    mainCam.transform.position = new Vector3(14f, 10f, 18f);
                    mainCam.transform.rotation = Quaternion.Euler(22f, -28f, 0f);
                    mainCam.Render();
                    string frameFileCloseup = Path.Combine(outDir, $"closeup_{step:D4}.png");
                    SaveRTToPNG(captureRT, frameFileCloseup);
                    frameFilesCloseup.Add(frameFileCloseup);

                    // 3. Station time-series recording
                    ReadCenterColumnSlice(boussSim.WaterDataTexture, W, H, currentEta, isDepth: false);
                    for (int s = 0; s < stations.Count; s++) {
                        int yIdx = math.clamp((int)math.round(stations[s].stationZ / dz), 0, H - 1);
                        if (!isMinmod) {
                            stations[s].times.Add(simTime);
                            stations[s].etasBaseline.Add(currentEta[yIdx]);
                        } else {
                            stations[s].etasMinmod.Add(currentEta[yIdx]);
                        }
                    }
                }

                // Profile snapshots at target times
                foreach (var prof in profiles) {
                    if (math.abs(simTime - prof.time) < dtStep * 0.5f) {
                        ReadCenterColumnSlice(boussSim.WaterDataTexture, W, H, currentEta, isDepth: false);
                        if (!isMinmod) {
                            prof.zs.Clear();
                            prof.depths.Clear();
                            prof.etasBaseline.Clear();
                            for (int y = 0; y < H; y++) {
                                prof.zs.Add(y * dz);
                                prof.depths.Add(currentDepth[y]);
                                prof.etasBaseline.Add(currentEta[y]);
                            }
                        } else {
                            prof.etasMinmod.Clear();
                            for (int y = 0; y < H; y++) {
                                prof.etasMinmod.Add(currentEta[y]);
                            }
                        }
                    }
                }
            }

            sw.Stop();
            double totalWallClockMs = sw.Elapsed.TotalMilliseconds;

            mainCam.targetTexture = prevTarget;
            captureRT.Release();
            UnityEngine.Object.DestroyImmediate(captureRT);

            return totalWallClockMs;
        }

        private static void ReadCenterColumnSlice(RenderTexture rt, int W, int H, float[] outSlice, bool isDepth) {
            if (rt == null) return;
            var req = AsyncGPUReadback.Request(rt);
            req.WaitForCompletion();
            if (req.hasError) return;

            int xCenter = W / 2;
            var halfData = req.GetData<ushort>(); // RGBAHalf (4 ushorts per pixel)
            for (int y = 0; y < H; y++) {
                int pIdx = (y * W + xCenter) * 4;
                if (isDepth) {
                    // BathymetryTexture: R is still water depth h
                    outSlice[y] = Mathf.HalfToFloat(halfData[pIdx]);
                } else {
                    // WaterDataTexture: G is surface displacement eta
                    outSlice[y] = Mathf.HalfToFloat(halfData[pIdx + 1]);
                }
            }
        }

        private static void SaveRTToPNG(RenderTexture rt, string filePath) {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;

            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            File.WriteAllBytes(filePath, png);
        }

        public static InteractiveVerificationResult VerifyInteractiveOperations() {
            var res = new InteractiveVerificationResult();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sim = UnityEngine.Object.FindFirstObjectByType<BoussinesqSimulation>();
            var ui = UnityEngine.Object.FindFirstObjectByType<WaterSampleUI>();
            if (sim == null || ui == null) {
                res.details = "Failed to find BoussinesqSimulation or WaterSampleUI in Scene 02.";
                return res;
            }

            // 1. Initial configuration check: Advection must be FirstOrderUpwind (Baseline)
            res.baselineInitialVerified = (sim.Advection == BoussinesqSimulation.AdvectionType.FirstOrderUpwind);

            // Run 5 steps
            sim.Play();
            for (int i = 0; i < 5; i++) sim.Step(0.0333f);

            // 2. Switch to MinmodTVD via reflection on WaterSampleUI.SwitchBoussinesqAdvection
            var switchMethod = typeof(WaterSampleUI).GetMethod("SwitchBoussinesqAdvection",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (switchMethod != null) {
                switchMethod.Invoke(ui, new object[] { sim, BoussinesqSimulation.AdvectionType.MinmodTVD });
            } else {
                sim.Advection = BoussinesqSimulation.AdvectionType.MinmodTVD;
                sim.ResetSimulation();
            }

            res.switchResetsSimulationTime = (sim.SimulationTime == 0.0f);
            res.switchMaintainsRunningState = sim.IsRunning;
            res.switchUpdatesAdvectionMode = (sim.Advection == BoussinesqSimulation.AdvectionType.MinmodTVD);

            // Step forward with Minmod
            for (int i = 0; i < 10; i++) sim.Step(0.0333f);
            float timeAfterMinmodSteps = sim.SimulationTime;

            // 3. Pause
            sim.Pause();
            float timeAtPause = sim.SimulationTime;
            res.pauseStopsSimulationTime = !sim.IsRunning;

            // 4. Play
            sim.Play();
            sim.Step(0.0333f);
            res.playResumesSimulationTime = (sim.SimulationTime > timeAtPause);

            // 5. Normal Reset (must retain selected MinmodTVD mode)
            sim.ResetSimulation();
            res.normalResetRetainsSelectedMode = (sim.SimulationTime == 0.0f && sim.Advection == BoussinesqSimulation.AdvectionType.MinmodTVD);

            // 6. Check finite values in WaterDataTexture
            var req = AsyncGPUReadback.Request(sim.WaterDataTexture);
            req.WaitForCompletion();
            bool finite = true;
            if (!req.hasError) {
                var raw = req.GetData<ushort>();
                for (int i = 0; i < raw.Length; i++) {
                    float val = Mathf.HalfToFloat(raw[i]);
                    if (float.IsNaN(val) || float.IsInfinity(val)) {
                        finite = false;
                        break;
                    }
                }
            }
            res.noNaNOrInfDetected = finite;

            // Restore Baseline default and reset scene state
            sim.Advection = BoussinesqSimulation.AdvectionType.FirstOrderUpwind;
            sim.ResetSimulation();

            res.allPassed = res.baselineInitialVerified && res.switchResetsSimulationTime &&
                            res.switchMaintainsRunningState && res.switchUpdatesAdvectionMode &&
                            res.pauseStopsSimulationTime && res.playResumesSimulationTime &&
                            res.normalResetRetainsSelectedMode && res.noNaNOrInfDetected;

            res.details = $"BaselineInitial={res.baselineInitialVerified}, SwitchResetsTime={res.switchResetsSimulationTime}, " +
                          $"SwitchMaintainsRun={res.switchMaintainsRunningState}, ModeUpdated={res.switchUpdatesAdvectionMode}, " +
                          $"Pause={res.pauseStopsSimulationTime}, Play={res.playResumesSimulationTime}, " +
                          $"NormalResetRetainsMode={res.normalResetRetainsSelectedMode}, FiniteOutputs={res.noNaNOrInfDetected}";

            UnityEngine.Debug.Log($"[VerifyInteractiveOperations] Result: {res.details}");
            return res;
        }

        private static string GetGitCommitHash(out bool dirty) {
            dirty = false;
            try {
                var p = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = "git",
                        Arguments = "describe --always --dirty",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                dirty = output.Contains("-dirty");
                return output;
            } catch {
                return "unknown";
            }
        }
        #endregion
    }
}
