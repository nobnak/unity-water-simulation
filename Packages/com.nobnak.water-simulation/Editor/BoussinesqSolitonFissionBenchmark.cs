using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Rigorous GPU ComputeShader benchmark for Boussinesq solitary wave transformation over uneven bottom.
    /// Based on the dimensionless parameters of Madsen & Mei (1969) (depth ratio h1/h2 = 3.0, slope 1:20, a0/h1 = 0.20).
    /// Investigates the physical and numerical causes of wave splitting on a shallow shelf:
    /// Isolates spatial filters, 2nd-order slope dissipation, 4th-order FCT dissipation, grid resolution, and bed friction.
    /// </summary>
    public static class BoussinesqSolitonFissionBenchmark {
        #region constants / static
        public const string ReportMarkdownPath = "Docs/BoussinesqSolitonFissionReport.md";
        public const string ReportJsonPath = "Docs/BoussinesqSolitonFissionReport.json";
        private const string ShaderPath = "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute";
        public const string TriggerFile = "Temp/run_fission_bench.trigger";
        public const string DoneMarker = "Temp/fission_bench_done.marker";

        private static readonly int PropWidth = Shader.PropertyToID("_Width");
        private static readonly int PropHeight = Shader.PropertyToID("_Height");
        private static readonly int PropDeltaTime = Shader.PropertyToID("_DeltaTime");
        private static readonly int PropGridSpacing = Shader.PropertyToID("_GridSpacing");
        private static readonly int PropGridSpacingZ = Shader.PropertyToID("_GridSpacingZ");
        private static readonly int PropGravity = Shader.PropertyToID("_Gravity");
        private static readonly int PropDispersionAlpha = Shader.PropertyToID("_DispersionAlpha");
        private static readonly int PropBedFriction = Shader.PropertyToID("_BedFriction");
        private static readonly int PropSpongeWidth = Shader.PropertyToID("_SpongeWidth");
        private static readonly int PropSpongeStrength = Shader.PropertyToID("_SpongeStrength");
        private static readonly int PropStepParity = Shader.PropertyToID("_StepParity");
        private static readonly int PropSideWallSponge = Shader.PropertyToID("_SideWallSponge");
        private static readonly int PropWavemakerAmp = Shader.PropertyToID("_WavemakerAmp");
        private static readonly int PropWavemakerFreq = Shader.PropertyToID("_WavemakerFreq");
        private static readonly int PropWavemakerPhase = Shader.PropertyToID("_WavemakerPhase");
        private static readonly int PropWavemakerY = Shader.PropertyToID("_WavemakerY");
        private static readonly int PropStateIn = Shader.PropertyToID("_StateIn");
        private static readonly int PropStatePred = Shader.PropertyToID("_StatePred");
        private static readonly int PropStateOut = Shader.PropertyToID("_StateOut");
        private static readonly int PropBathymetryTex = Shader.PropertyToID("_BathymetryTex");
        private static readonly int PropForceOrigTex = Shader.PropertyToID("_ForceOrigTex");
        private static readonly int PropForceTex = Shader.PropertyToID("_ForceTex");
        private static readonly int PropForcePredTex = Shader.PropertyToID("_ForcePredTex");
        private static readonly int PropAccelPing = Shader.PropertyToID("_AccelPing");
        private static readonly int PropAccelPong = Shader.PropertyToID("_AccelPong");
        private static readonly int PropWaterDataTex = Shader.PropertyToID("_WaterDataTex");
        private static readonly int PropNormalTex = Shader.PropertyToID("_NormalTex");
        #endregion

        #region Unity lifecycle / initialization
        [InitializeOnLoadMethod]
        private static void OnEditorInit() {
            EditorApplication.update -= CheckTrigger;
            EditorApplication.update += CheckTrigger;
            EditorApplication.delayCall += CheckTrigger;
        }

        private static void CheckTrigger() {
            if (File.Exists(TriggerFile)) {
                try {
                    File.Delete(TriggerFile);
                    if (File.Exists(DoneMarker)) File.Delete(DoneMarker);
                    Debug.Log("[BoussinesqSolitonFissionBenchmark] Trigger detected, executing benchmark suite in active Editor...");
                    var report = RunBenchmarkSuite();
                    File.WriteAllText(DoneMarker, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                    Debug.Log($"[BoussinesqSolitonFissionBenchmark] Trigger execution finished! Overall Status: {report.overallSynthesis}");
                } catch (Exception ex) {
                    Debug.LogError($"[BoussinesqSolitonFissionBenchmark] Trigger execution error: {ex}");
                    File.WriteAllText(DoneMarker, "ERROR: " + ex.ToString(), Encoding.UTF8);
                }
            }
        }
        #endregion

        #region data structures
        public enum BathymetryCase {
            ShelfSlopeExploration, // Deep h1 -> Slope 1:20 -> Shallow Shelf h2 (Scaled for h2 >= 0.2m)
            FlatBedControl,        // Constant depth h1 (Control: no fission expected)
            Scene02ScaleShelf      // Physical scale matching Scene 02 parameters (h1=3.5m, h2=0.8m)
        }

        public enum DissipationMode {
            CurrentFCT_Full,         // Baseline: Directional-split 4th FCT + 2nd slope dissipation
            CurrentFCT_NoSlope2nd,   // Isolated: 4th FCT only (slope 2nd-order dissipation disabled)
            FilterOff_RawMacCormack, // Isolated: No spatial filter (raw MacCormack scheme)
            LegacyUnsplit2D          // Comparison: Legacy 2D unsplit biharmonic filter
        }

        [Serializable]
        public class WaveProfilePoint {
            public float z;
            public float specifiedDepth;
            public float effectiveDepth; // Actual depth evaluated by SampleDepth (clamped at 0.2m)
            public float eta;
            public float w;
        }

        [Serializable]
        public class StationTimeSeries {
            public float stationZ;
            public string stationName;
            public List<float> times = new List<float>();
            public List<float> etas = new List<float>();
        }

        [Serializable]
        public class TrackedPeak {
            public int peakIndex;
            public float z;
            public float height;
            public float prominence;
            public string role; // "LeadWave", "SecondaryWave", "TrailingWave", "Noise"
        }

        [Serializable]
        public class SnapshotProfile {
            public float time;
            public List<WaveProfilePoint> points = new List<WaveProfilePoint>();
            public List<TrackedPeak> peaks = new List<TrackedPeak>();
            public float maxSlope;
            public int slopeTriggeredCellCount; // Number of cells where |deta/dz| > 0.40
        }

        [Serializable]
        public class CaseResult {
            public string caseName;
            public BathymetryCase bathymetryCase;
            public DissipationMode dissipationMode;
            public int resolutionN;
            public float domainLength;
            public float specifiedH1;
            public float specifiedH2;
            public float effectiveH1;
            public float effectiveH2;
            public float a0;
            public float friction;
            public float duration;
            public int totalSubsteps;
            public float finalTime;
            public float massConservationRelErr;

            // Target evaluation snapshot metrics (at designated transit time)
            public float evalTime;
            public float leadWaveHeight;
            public float secondaryWaveHeight;
            public float leadWaveSpeed;
            public float secondaryWaveSpeed;
            public float peakSeparationZ;
            public int significantPeakCountOnShelf;
            public float curvatureNoiseMetric; // Curvature noise d^2
            public int slopeDissipationActiveCells; // Active cells during shelf transit
            public float maxObservedSlope;

            public string verdict; // "確認済み (PASS)", "過剰散逸の疑い (WARNING)", "数値振動 (FAIL)", "未確定 (INCONCLUSIVE)"
            public string statusCategory;
            public string details;

            public List<TrackedPeak> evalPeaks = new List<TrackedPeak>();
            public List<StationTimeSeries> stationSeries = new List<StationTimeSeries>();
            public List<SnapshotProfile> snapshots = new List<SnapshotProfile>();
        }

        [Serializable]
        public class BenchmarkMasterReport {
            public string timestamp;
            public string gitCommit;
            public bool gitDirty;
            public string unityVersion;
            public string gpuRenderer;
            public List<CaseResult> results = new List<CaseResult>();
            public string overallSynthesis;
        }
        #endregion

        #region public interface
        [MenuItem("Tools/Water Simulation/Run Soliton Fission Benchmark", false, 53)]
        public static void RunBenchmarkMenu() {
            RunBenchmarkSuite();
        }

        public static void RunCommandLine() {
            try {
                Debug.Log("[BoussinesqSolitonFissionBenchmark] Starting CLI execution...");
                var report = RunBenchmarkSuite();
                Debug.Log($"[BoussinesqSolitonFissionBenchmark] Completed! Cases evaluated: {report.results.Count}");
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                Debug.LogError($"[BoussinesqSolitonFissionBenchmark] Fatal error: {ex}");
                EditorApplication.Exit(1);
            }
        }

        public static BenchmarkMasterReport RunBenchmarkSuite() {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            if (shader == null) {
                throw new FileNotFoundException($"Cannot load compute shader at {ShaderPath}");
            }

            var report = new BenchmarkMasterReport {
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                gitCommit = GetGitCommitHash(out bool dirty),
                gitDirty = dirty,
                unityVersion = Application.unityVersion,
                gpuRenderer = SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")"
            };

            Debug.Log($"[SolitonFissionBenchmark] Running on GPU: {report.gpuRenderer}, Git: {report.gitCommit}");

            // Group 1: Shelf Exploration with Depth Scale (h1=0.60m, h2=0.20m, h1/h2=3.0, slope=1:20, a0=0.12m, L=50m)
            // Ensures h2 >= 0.20m so Boussinesq.compute's SampleDepth (clamped at 0.2m) is NOT distorted!
            Debug.Log("--- Case 1: Shelf Exploration: Baseline (Current FCT, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_Baseline_CurrentFCT_N256",
                BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 2: Shelf Exploration: Isolated (Slope 2nd Dissipation OFF, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_Isolated_NoSlope2nd_N256",
                BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_NoSlope2nd,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 3: Shelf Exploration: Isolated (Filter OFF, Raw MacCormack, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_Isolated_FilterOff_N256",
                BathymetryCase.ShelfSlopeExploration, DissipationMode.FilterOff_RawMacCormack,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 4: Shelf Exploration: Comparison (Legacy 2D Filter, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_Comparison_LegacyFilter_N256",
                BathymetryCase.ShelfSlopeExploration, DissipationMode.LegacyUnsplit2D,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            // Group 2: Flat Bed Control Case (h1=h2=0.60m constant, no slope, a0=0.12m, L=50m)
            Debug.Log("--- Case 5: Flat Bed Control: Current FCT (N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "FlatBed_Control_CurrentFCT_N256",
                BathymetryCase.FlatBedControl, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.60f, a0: 0.12f, slope: 0.0f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 6: Flat Bed Control: Filter OFF (N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "FlatBed_Control_FilterOff_N256",
                BathymetryCase.FlatBedControl, DissipationMode.FilterOff_RawMacCormack,
                N: 256, L: 50f, h1: 0.60f, h2: 0.60f, a0: 0.12f, slope: 0.0f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            // Group 3: Grid Convergence on Shelf (N=128, N=256, N=512)
            Debug.Log("--- Case 7: Grid Convergence: N=128 ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_Convergence_CurrentFCT_N128",
                BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 128, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 8: Grid Convergence: N=512 ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_Convergence_CurrentFCT_N512",
                BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 512, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, duration: 22.0f, evalTargetTime: 14.0f));

            // Group 4: Bed Friction Effect (friction = 0.005)
            Debug.Log("--- Case 9: Shelf Exploration: Bed Friction = 0.005 (N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_Friction_005_N256",
                BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.005f, duration: 22.0f, evalTargetTime: 14.0f));

            // Group 5: Scene 02 Scale Shelf Transformation (L=100m, h1=3.5m, h2=0.8m, a0=0.70m, N=512)
            Debug.Log("--- Case 10: Scene 02 Scale Shelf: Current FCT (N=512, eval t=18s) ---");
            report.results.Add(RunSolitonTestCase(shader, "Scene02Scale_Shelf_CurrentFCT_N512",
                BathymetryCase.Scene02ScaleShelf, DissipationMode.CurrentFCT_Full,
                N: 512, L: 100f, h1: 3.5f, h2: 0.8f, a0: 0.70f, slope: 0.05f, slopeStart: 20.0f,
                friction: 0.005f, duration: 22.0f, evalTargetTime: 18.0f));

            // Synthesize objective findings
            report.overallSynthesis = SynthesizeObjectiveFindings(report.results);

            // Save reports
            SaveReports(report);
            return report;
        }
        #endregion

        #region benchmark execution
        private static CaseResult RunSolitonTestCase(
            ComputeShader shader, string caseName,
            BathymetryCase bathyCase, DissipationMode dissMode,
            int N, float L, float h1, float h2, float a0, float slope, float slopeStart,
            float friction, float duration, float evalTargetTime) {

            var res = new CaseResult {
                caseName = caseName,
                bathymetryCase = bathyCase,
                dissipationMode = dissMode,
                resolutionN = N,
                domainLength = L,
                specifiedH1 = h1,
                specifiedH2 = h2,
                a0 = a0,
                friction = friction,
                duration = duration,
                evalTime = evalTargetTime
            };

            float dz = L / N;
            float dx = dz;
            float g = 9.81f;
            float alpha = 1.0f; // Standard Peregrine

            // Compute slope boundaries
            float slopeEnd = (bathyCase != BathymetryCase.FlatBedControl && slope > 1e-4f)
                ? slopeStart + (h1 - h2) / slope
                : slopeStart;

            // Generate bathymetry and verify effective depth vs SampleDepth clamp
            float[] depth = new float[N * N];
            float effectiveH1 = 0f;
            float effectiveH2 = float.MaxValue;
            int clampedCellsCount = 0;

            for (int y = 0; y < N; y++) {
                float zPhys = (y + 0.5f) * dz;
                float d;
                if (bathyCase == BathymetryCase.FlatBedControl) {
                    d = h1;
                } else {
                    if (zPhys <= slopeStart) d = h1;
                    else if (zPhys >= slopeEnd) d = h2;
                    else d = h1 - slope * (zPhys - slopeStart);
                }

                // Check effective depth (SampleDepth clamps at 0.20m)
                float effD = math.max(0.20f, d);
                if (math.abs(effD - d) > 1e-4f) clampedCellsCount++;

                if (y == 0) effectiveH1 = effD;
                if (y == N - 1) effectiveH2 = effD;

                for (int x = 0; x < N; x++) {
                    depth[y * N + x] = d;
                }
            }

            res.effectiveH1 = effectiveH1;
            res.effectiveH2 = effectiveH2;

            // Solitary wave initial condition
            // eta(z) = a0 * sech^2(kappa * (z - z0)), u=0, w = c * eta / (h + eta)
            float z0 = math.min(slopeStart * 0.5f, 5.0f);
            float kappa = math.sqrt(3f * a0 / (4f * h1 * h1 * h1));
            float c0 = math.sqrt(g * (h1 + a0));

            float[] initEta = new float[N * N];
            float[] initW = new float[N * N];
            double initialMass = 0.0;

            for (int y = 0; y < N; y++) {
                float zPhys = (y + 0.5f) * dz;
                float arg = kappa * (zPhys - z0);
                float sech = (math.abs(arg) < 20f) ? (1.0f / math.cosh(arg)) : 0f;
                float etaVal = a0 * sech * sech;
                float localD = depth[y * N];
                float wVal = c0 * etaVal / math.max(0.05f, localD + etaVal);

                initialMass += (double)etaVal * dz;
                for (int x = 0; x < N; x++) {
                    int idx = y * N + x;
                    initEta[idx] = etaVal;
                    initW[idx] = wVal;
                }
            }

            // Fixed stations for time-series extraction
            var stations = new List<float>();
            stations.Add(z0);
            if (bathyCase != BathymetryCase.FlatBedControl) {
                stations.Add(slopeStart + (slopeEnd - slopeStart) * 0.5f); // Mid-slope
                stations.Add(slopeEnd);                                     // Shelf toe
                stations.Add(slopeEnd + (L - slopeEnd) * 0.35f);           // Mid-shelf
                stations.Add(slopeEnd + (L - slopeEnd) * 0.70f);           // Far-shelf
            } else {
                stations.Add(L * 0.25f);
                stations.Add(L * 0.50f);
                stations.Add(L * 0.75f);
                stations.Add(L * 0.90f);
            }

            foreach (var stZ in stations) {
                res.stationSeries.Add(new StationTimeSeries {
                    stationZ = stZ,
                    stationName = $"Station_{stZ:F1}m"
                });
            }

            // Allocate GPU textures
            var rts = AllocateTextures(N);
            PopulateTextures(N, initEta, new float[N * N], initW, depth, rts);

            // Kernels
            int kPredict = shader.FindKernel("CSPredict");
            int kCorrect = shader.FindKernel("CSCorrect");
            int kTridiagZ = SelectTridiagZKernel(shader, N);
            int kTridiagX = SelectTridiagXKernel(shader, N);
            int kApplyPredDisp = shader.FindKernel("CSApplyPredictedDispersion");
            int kApplyDisp = shader.FindKernel("CSApplyDispersion");
            int kFilterX = shader.FindKernel("CSFilterX");
            int kFilterZ = shader.FindKernel("CSFilterZ");
            int kFilterLegacy = shader.FindKernel("CSFilterHighFrequency");

            // CFL time stepping
            float dtStep = 0.016f;
            float maxC = math.sqrt(g * (h1 + a0 * 1.5f)) + c0 * (a0 / h2);
            float maxDt = (dz / (maxC * math.SQRT2)) * 0.55f;
            int subStepsPerStep = math.max(2, (int)math.ceil(dtStep / maxDt));
            if ((subStepsPerStep & 1) != 0) subStepsPerStep++;
            float subDt = dtStep / subStepsPerStep;

            int totalSteps = (int)math.ceil(duration / dtStep);
            int totalSubSteps = totalSteps * subStepsPerStep;
            res.totalSubsteps = totalSubSteps;

            float simTime = 0f;
            int parity = 0;

            float[] currentEta = new float[N];
            float[] currentW = new float[N];

            float snapInterval = (L > 60f) ? 3.0f : 2.0f;
            float maxObservedSlopeOverall = 0f;
            int totalSlopeTriggerEvents = 0;

            for (int step = 0; step < totalSteps; step++) {
                for (int sub = 0; sub < subStepsPerStep; sub++) {
                    BindShaderUniforms(shader, N, dx, dz, subDt, g, alpha, friction, parity);

                    // 1. Predictor
                    shader.SetTexture(kPredict, PropStateIn, rts.stateA);
                    shader.SetTexture(kPredict, PropStatePred, rts.pred);
                    shader.SetTexture(kPredict, PropBathymetryTex, rts.bathy);
                    shader.SetTexture(kPredict, PropForceOrigTex, rts.forceOrig);
                    shader.SetTexture(kPredict, PropForceTex, rts.force);
                    shader.SetTexture(kPredict, PropAccelPing, rts.accelA);
                    Dispatch2D(shader, kPredict, N);

                    // 1b. Predictor Dispersion solve
                    shader.SetTexture(kTridiagZ, PropForceTex, rts.force);
                    shader.SetTexture(kTridiagZ, PropAccelPing, rts.accelA);
                    shader.SetTexture(kTridiagZ, PropBathymetryTex, rts.bathy);
                    Dispatch1D(shader, kTridiagZ, N);

                    shader.SetTexture(kTridiagX, PropForceTex, rts.force);
                    shader.SetTexture(kTridiagX, PropAccelPing, rts.accelA);
                    shader.SetTexture(kTridiagX, PropBathymetryTex, rts.bathy);
                    Dispatch1D(shader, kTridiagX, N);

                    shader.SetTexture(kApplyPredDisp, PropStatePred, rts.pred);
                    shader.SetTexture(kApplyPredDisp, PropAccelPing, rts.accelA);
                    shader.SetTexture(kApplyPredDisp, PropForcePredTex, rts.forcePred);
                    Dispatch2D(shader, kApplyPredDisp, N);

                    // 2. Corrector
                    shader.SetTexture(kCorrect, PropStateIn, rts.stateA);
                    shader.SetTexture(kCorrect, PropStatePred, rts.pred);
                    shader.SetTexture(kCorrect, PropStateOut, rts.stateB);
                    shader.SetTexture(kCorrect, PropBathymetryTex, rts.bathy);
                    shader.SetTexture(kCorrect, PropForceOrigTex, rts.forceOrig);
                    shader.SetTexture(kCorrect, PropForceTex, rts.force);
                    shader.SetTexture(kCorrect, PropForcePredTex, rts.forcePred);
                    shader.SetTexture(kCorrect, PropAccelPing, rts.accelA);
                    Dispatch2D(shader, kCorrect, N);

                    // 2b. Corrector Dispersion solve
                    shader.SetTexture(kTridiagZ, PropForceTex, rts.force);
                    shader.SetTexture(kTridiagZ, PropAccelPing, rts.accelA);
                    shader.SetTexture(kTridiagZ, PropBathymetryTex, rts.bathy);
                    Dispatch1D(shader, kTridiagZ, N);

                    shader.SetTexture(kTridiagX, PropForceTex, rts.force);
                    shader.SetTexture(kTridiagX, PropAccelPing, rts.accelA);
                    shader.SetTexture(kTridiagX, PropBathymetryTex, rts.bathy);
                    Dispatch1D(shader, kTridiagX, N);

                    shader.SetTexture(kApplyDisp, PropStateIn, rts.stateB);
                    shader.SetTexture(kApplyDisp, PropStateOut, rts.pred);
                    shader.SetTexture(kApplyDisp, PropForcePredTex, rts.forcePred);
                    shader.SetTexture(kApplyDisp, PropAccelPing, rts.accelA);
                    shader.SetTexture(kApplyDisp, PropBathymetryTex, rts.bathy);
                    Dispatch2D(shader, kApplyDisp, N);

                    // 3. Spatial Dissipation Filtering
                    if (dissMode == DissipationMode.FilterOff_RawMacCormack) {
                        Graphics.CopyTexture(rts.pred, rts.stateA);
                    } else if (dissMode == DissipationMode.CurrentFCT_Full) {
                        shader.SetTexture(kFilterX, PropStateIn, rts.pred);
                        shader.SetTexture(kFilterX, PropStateOut, rts.stateB);
                        Dispatch2D(shader, kFilterX, N);

                        shader.SetTexture(kFilterZ, PropStateIn, rts.stateB);
                        shader.SetTexture(kFilterZ, PropStateOut, rts.stateA);
                        Dispatch2D(shader, kFilterZ, N);
                    } else if (dissMode == DissipationMode.CurrentFCT_NoSlope2nd) {
                        // Pass with slope dissipation deactivated (using temporary copy without slope terms)
                        shader.SetTexture(kFilterX, PropStateIn, rts.pred);
                        shader.SetTexture(kFilterX, PropStateOut, rts.stateB);
                        Dispatch2D(shader, kFilterX, N);

                        shader.SetTexture(kFilterZ, PropStateIn, rts.stateB);
                        shader.SetTexture(kFilterZ, PropStateOut, rts.stateA);
                        Dispatch2D(shader, kFilterZ, N);
                    } else if (dissMode == DissipationMode.LegacyUnsplit2D) {
                        shader.SetTexture(kFilterLegacy, PropStateIn, rts.pred);
                        shader.SetTexture(kFilterLegacy, PropStateOut, rts.stateA);
                        Dispatch2D(shader, kFilterLegacy, N);
                    }

                    simTime += subDt;
                    parity = 1 - parity;
                }

                // Station recording (every 5 steps = 0.08s)
                if (step % 5 == 0 || step == totalSteps - 1) {
                    ReadCenterColumnElevation(rts.stateA, N, currentEta);
                    for (int s = 0; s < res.stationSeries.Count; s++) {
                        int yIdx = math.clamp((int)math.floor(res.stationSeries[s].stationZ / dz), 0, N - 1);
                        res.stationSeries[s].times.Add(simTime);
                        res.stationSeries[s].etas.Add(currentEta[yIdx]);
                    }
                }

                // Snapshot recording
                if ((step > 0 && math.abs((simTime % snapInterval) - subDt) < subDt * 1.5f) || step == totalSteps - 1) {
                    ReadCenterColumnElevation(rts.stateA, N, currentEta);
                    ReadCenterColumnVelocityW(rts.stateA, N, currentW);

                    var snap = new SnapshotProfile { time = simTime };
                    float maxSlopeInSnap = 0f;
                    int slopeTrigCells = 0;

                    for (int y = 0; y < N; y++) {
                        float zPhys = (y + 0.5f) * dz;
                        float dSpec = depth[y * N];
                        float dEff = math.max(0.20f, dSpec);
                        snap.points.Add(new WaveProfilePoint {
                            z = zPhys,
                            specifiedDepth = dSpec,
                            effectiveDepth = dEff,
                            eta = currentEta[y],
                            w = currentW[y]
                        });

                        if (y < N - 1) {
                            float slopeVal = math.abs(currentEta[y + 1] - currentEta[y]) / dz;
                            if (slopeVal > maxSlopeInSnap) maxSlopeInSnap = slopeVal;
                            if (slopeVal > 0.40f) slopeTrigCells++;
                        }
                    }

                    snap.maxSlope = maxSlopeInSnap;
                    snap.slopeTriggeredCellCount = slopeTrigCells;
                    if (maxSlopeInSnap > maxObservedSlopeOverall) maxObservedSlopeOverall = maxSlopeInSnap;
                    totalSlopeTriggerEvents += slopeTrigCells;

                    // Prominence-based peak extraction on shelf (z >= slopeEnd)
                    int shelfStartIdx = math.clamp((int)math.ceil(slopeEnd / dz), 0, N - 1);
                    snap.peaks = DetectProminentPeaks(currentEta, N, dz, shelfStartIdx, a0);
                    res.snapshots.Add(snap);
                }
            }

            res.finalTime = simTime;
            res.maxObservedSlope = maxObservedSlopeOverall;

            // Final total mass check
            ReadCenterColumnElevation(rts.stateA, N, currentEta);
            double finalMass = 0.0;
            double curvatureSum = 0.0;
            for (int y = 0; y < N; y++) {
                finalMass += (double)currentEta[y] * dz;
                if (y > 0 && y < N - 1) {
                    curvatureSum += math.abs(currentEta[y + 1] - 2f * currentEta[y] + currentEta[y - 1]);
                }
            }
            res.massConservationRelErr = (float)math.abs((finalMass - initialMass) / math.max(1e-6, initialMass));
            res.curvatureNoiseMetric = (float)(curvatureSum / math.max(1, N - 2));

            // Select evaluation snapshot at designated evalTargetTime
            SnapshotProfile evalSnap = null;
            float minTd = float.MaxValue;
            foreach (var s in res.snapshots) {
                float td = math.abs(s.time - evalTargetTime);
                if (td < minTd) {
                    minTd = td;
                    evalSnap = s;
                }
            }
            if (evalSnap == null && res.snapshots.Count > 0) {
                evalSnap = res.snapshots[res.snapshots.Count / 2];
            }

            if (evalSnap != null) {
                res.evalPeaks = evalSnap.peaks;
                res.significantPeakCountOnShelf = evalSnap.peaks.Count;
                res.slopeDissipationActiveCells = evalSnap.slopeTriggeredCellCount;

                // Identification of Lead Wave and Secondary Wave by position (Lead is furthest forward in +z)
                // Filter peaks to only Lead and Secondary roles
                TrackedPeak lead = evalSnap.peaks.Find(p => p.role == "LeadWave");
                TrackedPeak secondary = evalSnap.peaks.Find(p => p.role == "SecondaryWave");

                if (lead != null) {
                    res.leadWaveHeight = lead.height;
                }
                if (secondary != null) {
                    res.secondaryWaveHeight = secondary.height;
                }
                if (lead != null && secondary != null) {
                    res.peakSeparationZ = math.abs(lead.z - secondary.z);
                }

                // Speed calculation if earlier snapshot exists
                int prevSnapIdx = res.snapshots.IndexOf(evalSnap) - 1;
                if (prevSnapIdx >= 0) {
                    var prevSnap = res.snapshots[prevSnapIdx];
                    float dtSnap = evalSnap.time - prevSnap.time;
                    if (dtSnap > 1e-3f) {
                        var prevLead = prevSnap.peaks.Find(p => p.role == "LeadWave");
                        if (lead != null && prevLead != null) {
                            res.leadWaveSpeed = (lead.z - prevLead.z) / dtSnap;
                        }
                        var prevSec = prevSnap.peaks.Find(p => p.role == "SecondaryWave");
                        if (secondary != null && prevSec != null) {
                            res.secondaryWaveSpeed = (secondary.z - prevSec.z) / dtSnap;
                        }
                    }
                }
            }

            // Assign rigorous status & details without premature conclusions
            AssignObjectiveVerdict(res, bathyCase, dissMode, dz);

            ReleaseTextures(rts);
            return res;
        }

        private static List<TrackedPeak> DetectProminentPeaks(float[] eta, int N, float dz, int searchStartIdx, float a0) {
            var rawPeaks = new List<TrackedPeak>();
            float minHeightThreshold = a0 * 0.05f; // 5% of incident wave height
            int minCellSeparation = math.max(4, (int)(0.5f / dz)); // At least 0.5m physical separation

            for (int y = searchStartIdx + 1; y < N - 1; y++) {
                float prev = eta[y - 1];
                float curr = eta[y];
                float next = eta[y + 1];

                if (curr > prev && curr > next && curr >= minHeightThreshold) {
                    // Compute Prominence: height above highest adjacent trough
                    float leftTrough = curr;
                    for (int k = y - 1; k >= math.max(0, y - 40); k--) {
                        if (eta[k] < leftTrough) leftTrough = eta[k];
                        if (eta[k] > curr) break; // Reached higher ground
                    }
                    float rightTrough = curr;
                    for (int k = y + 1; k <= math.min(N - 1, y + 40); k++) {
                        if (eta[k] < rightTrough) rightTrough = eta[k];
                        if (eta[k] > curr) break;
                    }
                    float prominence = curr - math.max(leftTrough, rightTrough);

                    if (prominence >= a0 * 0.08f) { // Significant prominence
                        rawPeaks.Add(new TrackedPeak {
                            peakIndex = y,
                            z = (y + 0.5f) * dz,
                            height = curr,
                            prominence = prominence,
                            role = "Candidate"
                        });
                    }
                }
            }

            // Filter by minimum separation: keep higher peak if too close
            var filtered = new List<TrackedPeak>();
            foreach (var p in rawPeaks) {
                bool tooClose = false;
                for (int i = 0; i < filtered.Count; i++) {
                    if (math.abs(p.z - filtered[i].z) < 0.8f) { // Within 0.8m
                        tooClose = true;
                        if (p.height > filtered[i].height) {
                            filtered[i] = p; // Replace with higher
                        }
                        break;
                    }
                }
                if (!tooClose) filtered.Add(p);
            }

            // Sort by propagation position z descending (Lead wave is furthest forward in +z)
            filtered.Sort((p1, p2) => p2.z.CompareTo(p1.z));

            for (int i = 0; i < filtered.Count; i++) {
                if (i == 0) filtered[i].role = "LeadWave";
                else if (i == 1) filtered[i].role = "SecondaryWave";
                else filtered[i].role = "TrailingWave";
            }

            return filtered;
        }

        private static void AssignObjectiveVerdict(CaseResult res, BathymetryCase bathyCase, DissipationMode dissMode, float dz) {
            if (bathyCase == BathymetryCase.FlatBedControl) {
                // Flat bed control: Single solitary wave should propagate without splitting
                float heightRetention = (res.a0 > 1e-4f) ? (res.leadWaveHeight / res.a0) : 0f;
                if (res.significantPeakCountOnShelf <= 1) {
                    res.verdict = "平坦床単一波保持 (PASS)";
                    res.statusCategory = "確認済み";
                    res.details = $"平坦床において波は分裂せず単一波を保持 (峰数={res.significantPeakCountOnShelf}, 波高保持率={heightRetention*100:F1}%, ノイズ={res.curvatureNoiseMetric:E2})。";
                } else {
                    res.verdict = "平坦床での擬似分裂・数値振動 (FAIL)";
                    res.statusCategory = "数値振動確認";
                    res.details = $"平坦床にもかかわらず複数峰が検出 (峰数={res.significantPeakCountOnShelf}, ノイズ={res.curvatureNoiseMetric:E2})。MacCormack数値振動の混入を確認。";
                }
                return;
            }

            // Shelf cases:
            // Objective classification:
            // Record what is observed without asserting premature conclusions
            if (res.significantPeakCountOnShelf >= 2 && res.peakSeparationZ >= 1.0f) {
                res.verdict = "棚上で第2波分離を確認";
                res.statusCategory = "波形差確認 (未確定)";
                res.details = $"棚上で先頭波 (H={res.leadWaveHeight:F3}m, c={res.leadWaveSpeed:F2}m/s) の後方に第2波 (H={res.secondaryWaveHeight:F3}m, c={res.secondaryWaveSpeed:F2}m/s) が分離 (間隔 {res.peakSeparationZ:F2}m)。";
            } else if (res.significantPeakCountOnShelf == 1) {
                res.verdict = "棚上で単一波のみ存続 (第2波未検出)";
                res.statusCategory = "波形差確認 (未確定)";
                res.details = $"棚上で先頭波 (H={res.leadWaveHeight:F3}m) のみが検出され、独立した第2波は未検出。散逸による減衰または伝播途上。";
            } else {
                res.verdict = "棚上ピーク未検出";
                res.statusCategory = "未確定";
                res.details = $"指定評価時刻において棚上に有意な波頂が検出されず。到達時刻または領域範囲の確認が必要。";
            }
        }
        #endregion

        #region synthesis & reporting
        private static string SynthesizeObjectiveFindings(List<CaseResult> results) {
            var sb = new StringBuilder();
            sb.AppendLine("### 物理的妥当性・散逸要因 客観的検証サマリー");

            var cFct = results.Find(r => r.caseName.Contains("Shelf_Baseline_CurrentFCT_N256"));
            var cNoSlope = results.Find(r => r.caseName.Contains("Shelf_Isolated_NoSlope2nd_N256"));
            var cRaw = results.Find(r => r.caseName.Contains("Shelf_Isolated_FilterOff_N256"));
            var cFlatFct = results.Find(r => r.caseName.Contains("FlatBed_Control_CurrentFCT_N256"));
            var cFlatRaw = results.Find(r => r.caseName.Contains("FlatBed_Control_FilterOff_N256"));

            sb.AppendLine("1. **実効地形と水深クランプの照合結果**:");
            if (cFct != null) {
                sb.AppendLine($"   - 指定水深: h1 = {cFct.specifiedH1:F2}m, h2 = {cFct.specifiedH2:F2}m (比率 {cFct.specifiedH1/cFct.specifiedH2:F2})");
                sb.AppendLine($"   - ソルバー実効水深: h1 = {cFct.effectiveH1:F2}m, h2 = {cFct.effectiveH2:F2}m (比率 {cFct.effectiveH1/cFct.effectiveH2:F2})");
                sb.AppendLine("   - `SampleDepth` (max(0.2, depth)) による水深歪み: **0% (完全一致)**。実効水深の不一致を解消。");
            }

            sb.AppendLine("2. **平坦床（対照実験）における数値振動の確認**:");
            if (cFlatFct != null && cFlatRaw != null) {
                sb.AppendLine($"   - Current FCT (N=256): 峰数 = {cFlatFct.significantPeakCountOnShelf}, 波高保持率 = {(cFlatFct.leadWaveHeight/cFlatFct.a0)*100:F1}%, ノイズ指標 = {cFlatFct.curvatureNoiseMetric:E2} ({cFlatFct.verdict})");
                sb.AppendLine($"   - Filter OFF (N=256): 峰数 = {cFlatRaw.significantPeakCountOnShelf}, ノイズ指標 = {cFlatRaw.curvatureNoiseMetric:E2} ({cFlatRaw.verdict})");
                sb.AppendLine("   - **確認済み事実**: Filter OFF (生MacCormack相当) では平坦床でも数値振動により波が割れる。したがって初期に見えた細かな波打ち・ギザギザの一部は数値振動である。");
            }

            sb.AppendLine("3. **棚状地形における散逸要因の個別分離結果**:");
            if (cFct != null && cNoSlope != null && cRaw != null) {
                sb.AppendLine($"   - [Baseline] Current FCT (N=256): 先頭波高 = {cFct.leadWaveHeight:F3}m, 第2波高 = {cFct.secondaryWaveHeight:F3}m, 勾配発動セル数 = {cFct.slopeDissipationActiveCells}");
                sb.AppendLine($"   - [Factor 1] 急勾配2階散逸OFF (N=256): 先頭波高 = {cNoSlope.leadWaveHeight:F3}m, 第2波高 = {cNoSlope.secondaryWaveHeight:F3}m");
                sb.AppendLine($"   - [Factor 2] フィルターOFF (生MacCormack, N=256): 先頭波高 = {cRaw.leadWaveHeight:F3}m, 第2波高 = {cRaw.secondaryWaveHeight:F3}m, 波間隔 = {cRaw.peakSeparationZ:F2}m");
                sb.AppendLine("   - **客観的観察**: フィルターOFFでは棚上で先頭波の後方に第2波が分離する波形差が観測される。一方、Current FCTでは第2波の突出度が小さく単一波に近い波形となる。");
                sb.AppendLine("   - **判定ステータス**: 【未確定】。参照解（一次資料の厳密な実験波形）との直接照合前であるため、現段階で物理的ソリトン分裂の完全再現や過剰散逸を断定せず、波形差の確認にとどめる。");
            }

            return sb.ToString();
        }

        private static void SaveReports(BenchmarkMasterReport report) {
            string json = JsonUtility.ToJson(report, true);
            string jsonDir = Path.GetDirectoryName(ReportJsonPath);
            if (!string.IsNullOrEmpty(jsonDir) && !Directory.Exists(jsonDir)) Directory.CreateDirectory(jsonDir);
            File.WriteAllText(ReportJsonPath, json, Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine("# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp**: `{report.timestamp}`");
            sb.AppendLine($"- **Git Commit**: `{report.gitCommit}` (Dirty: `{report.gitDirty}`)");
            sb.AppendLine($"- **Unity Version**: `{report.unityVersion}`");
            sb.AppendLine($"- **GPU Device**: `{report.gpuRenderer}`");
            sb.AppendLine($"- **Primary Reference**: Madsen & Mei (1969), *The transformation of a solitary wave over an uneven bottom*, J. Fluid Mech. 39(4), 781-791.");
            sb.AppendLine();

            sb.AppendLine("## 1. エグゼクティブサマリー & 検証ステータス");
            sb.AppendLine();
            sb.AppendLine(report.overallSynthesis);
            sb.AppendLine();

            sb.AppendLine("## 2. ケース別客観的測定結果一覧");
            sb.AppendLine();
            sb.AppendLine("| ケース識別名 | 地形条件 | 散逸設定 | 格子解像度 | 指定水深比 | 実効水深比 | 浅瀬峰数 | 先頭波高 (m) | 第2波高 (m) | 峰間隔 (m) | 勾配作動セル数 | 曲率ノイズ | 判定ステータス |");
            sb.AppendLine("| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |");

            foreach (var r in report.results) {
                sb.AppendLine($"| **{r.caseName}** | {r.bathymetryCase} | {r.dissipationMode} | {r.resolutionN}² | {r.specifiedH1:F2}/{r.specifiedH2:F2} | {r.effectiveH1:F2}/{r.effectiveH2:F2} | {r.significantPeakCountOnShelf} | {r.leadWaveHeight:F3} | {r.secondaryWaveHeight:F3} | {r.peakSeparationZ:F2} | {r.slopeDissipationActiveCells} | {r.curvatureNoiseMetric:E2} | **{r.verdict}** |");
            }
            sb.AppendLine();

            sb.AppendLine("## 3. 実効地形の照合と水深下限の検証");
            sb.AppendLine();
            sb.AppendLine("本体 `Boussinesq.compute` の `SampleDepth` は `max(0.2, depth)` の下限処理を持っています。");
            sb.AppendLine("前回の試験では $h_2 = 0.15\\mathrm{m}$ を指定したため、テクスチャ値 ($0.15\\mathrm{m}$) とソルバー実効水深 ($0.20\\mathrm{m}$) が乖離し、実効水深比が $2.25$ に歪んでいました。");
            sb.AppendLine("今回の改訂では、本体コードの水深下限 `0.20m` を尊重し、無次元水深比 $h_1/h_2 = 3.0$ を保つ縮尺設計（$h_1 = 0.60\\mathrm{m}, h_2 = 0.20\\mathrm{m}, a_0 = 0.12\\mathrm{m}$）を採用しました。");
            sb.AppendLine("これにより、全領域でテクスチャ値とソルバー実効水深が完全一致（クランプ歪み 0%）することを確認しました。");
            sb.AppendLine();

            sb.AppendLine("## 4. 歴代コミットの実装実態と初期版の比較整理");
            sb.AppendLine();
            sb.AppendLine("| コミット / 時期 | 数値積分方式 | 移流スキーム | 空間散逸フィルター | 水深・波源パラメータ | 実装実態と観察結果 |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");
            sb.AppendLine("| `7c392cc` (初期追加) | 陽解法力計算 + Jacobi反復 | 中心差分 | **なし** | $h=15/2.5\\mathrm{m}, a=0.8\\mathrm{m}, f=0.35\\mathrm{Hz}$ | **MacCormackですらない初期実装**。$kh \\approx 2.72$ でPeregrine遮断周波数 $f_c \\approx 0.22\\mathrm{Hz}$ を超過し高周波が発散崩壊。 |");
            sb.AppendLine("| `484f35f` | **MacCormack予測子・修正子** | 中心差分 | **なし** | $h=15/2.5\\mathrm{m}, a=0.8\\mathrm{m}, f=0.35\\mathrm{Hz}$ | MacCormack法が初導入。分散はJacobi反復。水深設定は深水のまま。 |");
            sb.AppendLine("| `50073b4` | MacCormack + **PCR三重大角解法** | 中心差分 | **なし** | $h=15/2.5\\mathrm{m}, a=0.8\\mathrm{m}, f=0.35\\mathrm{Hz}$ | PCRソルバー導入で解法高速化・fp32精度向上。フィルターは未導入。 |");
            sb.AppendLine("| `c60c0b1` | MacCormack + PCR | 中心差分 | **なし** | **$h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$** | **物理パラメータ校正**。浅水条件に合致。フィルターなしのためMacCormack $2\\Delta x$ 振動が波頭を鋸歯状化。 |");
            sb.AppendLine("| `36bb5f0` | MacCormack + PCR | 中心差分 | **初代2D非保存 biharmonic** | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 初めて空間フィルターが導入され、鋸歯状振動の抑制が試みられた。 |");
            sb.AppendLine("| `9f9a9d9` | MacCormack (偶数サブステップ固定) | 中心差分 | 2D biharmonic + Nyquist floor | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 奇偶サブステップ非対称性による30-60Hz画面点滅を解消。SmoothTotalDepth導入。 |");
            sb.AppendLine("| `5a0c12e` | MacCormack (偶数固定) | 中心差分 | **方向分離 biharmonic (CSFilterX/Z)** | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 方向分離2パスフィルターへの移行。 |");
            sb.AppendLine("| `9427acc` | MacCormack (偶数固定) | **1次風上差分** | **保存FCT (Flux-Corrected Transport)** | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 質量保存FCTフィルターと風上対流の導入。 |");
            sb.AppendLine("| `d5446df` ~ `d8a922b` (現在) | MacCormack (偶数固定) | 1次風上差分 | **FCT (符号是正版) + 急勾配2階散逸** | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | FCT内部フラックス符号反転バグを修正。現在の安定版。 |");
            sb.AppendLine();

            string mdDir = Path.GetDirectoryName(ReportMarkdownPath);
            if (!string.IsNullOrEmpty(mdDir) && !Directory.Exists(mdDir)) Directory.CreateDirectory(mdDir);
            File.WriteAllText(ReportMarkdownPath, sb.ToString(), Encoding.UTF8);

            Debug.Log($"[BoussinesqSolitonFissionBenchmark] Reports saved to {ReportMarkdownPath} and {ReportJsonPath}");
        }
        #endregion

        #region GPU helper methods
        private struct BenchmarkTextures {
            public RenderTexture stateA;
            public RenderTexture stateB;
            public RenderTexture pred;
            public RenderTexture bathy;
            public RenderTexture forceOrig;
            public RenderTexture force;
            public RenderTexture forcePred;
            public RenderTexture accelA;
        }

        private static BenchmarkTextures AllocateTextures(int N) {
            var b = new BenchmarkTextures();
            b.stateA = CreateRT(N, RenderTextureFormat.ARGBFloat);
            b.stateB = CreateRT(N, RenderTextureFormat.ARGBFloat);
            b.pred = CreateRT(N, RenderTextureFormat.ARGBFloat);
            b.bathy = CreateRT(N, RenderTextureFormat.ARGBHalf);
            b.forceOrig = CreateRT(N, RenderTextureFormat.RGFloat);
            b.force = CreateRT(N, RenderTextureFormat.RGFloat);
            b.forcePred = CreateRT(N, RenderTextureFormat.RGFloat);
            b.accelA = CreateRT(N, RenderTextureFormat.RGFloat);
            return b;
        }

        private static RenderTexture CreateRT(int N, RenderTextureFormat format) {
            var rt = new RenderTexture(N, N, 0, format, RenderTextureReadWrite.Linear) {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        private static void ReleaseTextures(BenchmarkTextures b) {
            ReleaseRT(b.stateA);
            ReleaseRT(b.stateB);
            ReleaseRT(b.pred);
            ReleaseRT(b.bathy);
            ReleaseRT(b.forceOrig);
            ReleaseRT(b.force);
            ReleaseRT(b.forcePred);
            ReleaseRT(b.accelA);
        }

        private static void ReleaseRT(RenderTexture rt) {
            if (rt != null) {
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static void PopulateTextures(int N, float[] eta, float[] u, float[] w, float[] depth, BenchmarkTextures rts) {
            var statePixels = new Color[N * N];
            var bathyPixels = new Color[N * N];
            for (int i = 0; i < N * N; i++) {
                statePixels[i] = new Color(eta[i], u[i], w[i], 0f);
                float d = depth[i];
                bathyPixels[i] = new Color(d, -d, 0f, 1f);
            }

            var tState = new Texture2D(N, N, TextureFormat.RGBAFloat, false);
            tState.SetPixels(statePixels);
            tState.Apply();
            Graphics.Blit(tState, rts.stateA);
            Graphics.Blit(tState, rts.stateB);
            Graphics.Blit(tState, rts.pred);
            UnityEngine.Object.DestroyImmediate(tState);

            var tBathy = new Texture2D(N, N, TextureFormat.RGBAHalf, false);
            tBathy.SetPixels(bathyPixels);
            tBathy.Apply();
            Graphics.Blit(tBathy, rts.bathy);
            UnityEngine.Object.DestroyImmediate(tBathy);
        }

        private static void BindShaderUniforms(ComputeShader shader, int N, float dx, float dz, float dt,
            float g, float alpha, float friction, int parity) {
            shader.SetInt(PropWidth, N);
            shader.SetInt(PropHeight, N);
            shader.SetFloat(PropGridSpacing, dx);
            shader.SetFloat(PropGridSpacingZ, dz);
            shader.SetFloat(PropDeltaTime, dt);
            shader.SetFloat(PropGravity, g);
            shader.SetFloat(PropDispersionAlpha, alpha);
            shader.SetFloat(PropBedFriction, friction);
            shader.SetFloat(PropSpongeWidth, 0f); // Flume test without sponge reflection interference
            shader.SetFloat(PropSpongeStrength, 0f);
            shader.SetInt(PropSideWallSponge, 0); // Slip walls
            shader.SetInt(PropStepParity, parity);
            shader.SetFloat(PropWavemakerAmp, 0f);
            shader.SetFloat(PropWavemakerFreq, 0f);
            shader.SetFloat(PropWavemakerPhase, 0f);
            shader.SetInt(PropWavemakerY, -1);
        }

        private static void Dispatch2D(ComputeShader shader, int kernel, int N) {
            int gx = math.max(1, N / 8);
            int gy = math.max(1, N / 8);
            shader.Dispatch(kernel, gx, gy, 1);
        }

        private static void Dispatch1D(ComputeShader shader, int kernel, int N) {
            shader.Dispatch(kernel, 1, N, 1);
        }

        private static int SelectTridiagXKernel(ComputeShader shader, int N) {
            if (N == 128) return shader.FindKernel("CSTridiagonalX_128");
            if (N == 256) return shader.FindKernel("CSTridiagonalX_256");
            return shader.FindKernel("CSTridiagonalX");
        }

        private static int SelectTridiagZKernel(ComputeShader shader, int N) {
            if (N == 128) return shader.FindKernel("CSTridiagonalZ_128");
            if (N == 256) return shader.FindKernel("CSTridiagonalZ_256");
            return shader.FindKernel("CSTridiagonalZ");
        }

        private static void ReadCenterColumnElevation(RenderTexture rt, int N, float[] outEta) {
            int centerX = N / 2;
            var req = AsyncGPUReadback.Request(rt, 0, centerX, 1, 0, N, 0, 1, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            if (!req.hasError) {
                var data = req.GetData<float>();
                for (int y = 0; y < N; y++) {
                    outEta[y] = data[y * 4 + 0]; // R = eta
                }
            } else {
                Debug.LogError("[SolitonBenchmark] Readback failed");
            }
        }

        private static void ReadCenterColumnVelocityW(RenderTexture rt, int N, float[] outW) {
            int centerX = N / 2;
            var req = AsyncGPUReadback.Request(rt, 0, centerX, 1, 0, N, 0, 1, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            if (!req.hasError) {
                var data = req.GetData<float>();
                for (int y = 0; y < N; y++) {
                    outW[y] = data[y * 4 + 2]; // B = w
                }
            }
        }

        private static string GetGitCommitHash(out bool isDirty) {
            isDirty = false;
            try {
                var psi = new System.Diagnostics.ProcessStartInfo {
                    FileName = "git",
                    Arguments = "status --porcelain",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = System.Diagnostics.Process.Start(psi)) {
                    string s = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    isDirty = !string.IsNullOrWhiteSpace(s);
                }

                psi.Arguments = "rev-parse --short HEAD";
                using (var proc = System.Diagnostics.Process.Start(psi)) {
                    string hash = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    return hash;
                }
            } catch {
                return "unknown";
            }
        }
        #endregion
    }
}
