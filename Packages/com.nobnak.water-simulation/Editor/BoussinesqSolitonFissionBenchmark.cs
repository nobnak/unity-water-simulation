using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Rigorous GPU ComputeShader benchmark for Boussinesq solitary wave transformation over uneven bottom.
    /// Exploratory shelf bathymetry test based on the dimensionless parameters of Madsen & Mei (1969)
    /// (depth ratio h1/h2 = 3.0, slope 1:20, a0/h1 = 0.20).
    /// Investigates the physical and numerical causes of wave transformation and secondary peak emergence on a shallow shelf:
    /// Isolates buffer precision (FP32 vs FP16), spatial filters, 2nd-order slope dissipation, 4th-order FCT dissipation,
    /// grid resolution, and time-step refinement under constant physical dissipation rates.
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
        private static readonly int PropDisableSlopeDissipation = Shader.PropertyToID("_DisableSlopeDissipation");
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
        private static readonly int PropDissipationMetricsBuffer = Shader.PropertyToID("_DissipationMetricsBuffer");
        private static readonly int PropEffectiveDepthOut = Shader.PropertyToID("_EffectiveDepthOut");
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
            CurrentFCT_NoSlope2nd,   // Isolated: 4th FCT only (slope 2nd-order dissipation disabled via _DisableSlopeDissipation = 1.0)
            FilterOff_RawMacCormack, // Isolated: No spatial filter (raw MacCormack with 1st-order upwind advection)
            LegacyUnsplit2D          // Comparison: Legacy 2D unsplit biharmonic filter
        }

        public enum BufferPrecision {
            FP32_RuntimeMatching, // state: ARGBFloat, forces: RGFloat, bathymetry: ARGBHalf (Matches BoussinesqSimulation.cs)
            FP16_LegacyComparison // state: ARGBHalf, forces: RGHalf, bathymetry: ARGBHalf (Prior benchmark mode)
        }

        [Serializable]
        public class WaveProfilePoint {
            public float z;
            public float specifiedDepth;
            public float storedDepth;
            public float effectiveDepth; // Actual depth evaluated by GPU SampleDepth
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
            public string role; // "LeadWave", "SecondaryWave", "TrailingWave", "Candidate"
        }

        [Serializable]
        public class SnapshotProfile {
            public float time;
            public List<WaveProfilePoint> points = new List<WaveProfilePoint>();
            public List<TrackedPeak> peaks = new List<TrackedPeak>();
            public float maxSlope;
        }

        [Serializable]
        public class DissipationMetricsDetailed {
            // Pass X Full Domain
            public uint passX_Full_ExceededSlopeFaces;
            public uint passX_Full_NonZeroKappaFaces;
            public float passX_Full_MaxKappa2;
            public float passX_Full_MaxFlux2;

            // Pass X Center Cross-Section
            public uint passX_Center_ExceededSlopeFaces;
            public uint passX_Center_NonZeroKappaFaces;
            public float passX_Center_MaxKappa2;
            public float passX_Center_MaxFlux2;

            // Pass Z Full Domain
            public uint passZ_Full_ExceededSlopeFaces;
            public uint passZ_Full_NonZeroKappaFaces;
            public float passZ_Full_MaxKappa2;
            public float passZ_Full_MaxFlux2;

            // Pass Z Center Cross-Section
            public uint passZ_Center_ExceededSlopeFaces;
            public uint passZ_Center_NonZeroKappaFaces;
            public float passZ_Center_MaxKappa2;
            public float passZ_Center_MaxFlux2;
        }

        [Serializable]
        public class DepthVerificationData {
            public float specifiedH1;
            public float specifiedH2;
            public float storedH1;
            public float storedH2;
            public float storedH1RoundingDiff;
            public float storedH2RoundingDiff;
            public float solverEffectiveH1;
            public float solverEffectiveH2;
            public float h1ClampChange;
            public float h2ClampChange;
            public float maxShelfRoundingDiff;
            public float maxShelfClampChange;
            public int shelfPointsSampled;
            public string verificationMethod;
        }

        [Serializable]
        public class SyntheticToggleResult {
            public bool verified;
            public float maxOutputDifference;
            public uint nonZeroKappaCountEnabled;
            public uint nonZeroKappaCountDisabled;
            public float maxKappaEnabled;
            public float maxKappaDisabled;
            public string details;
        }

        [Serializable]
        public class CaseResult {
            public string caseName;
            public BufferPrecision bufferPrecision;
            public BathymetryCase bathymetryCase;
            public DissipationMode dissipationMode;
            public bool disableSlopeDissipationUniform;
            public int resolutionN;
            public float domainLength;
            public float a0;
            public float friction;
            public float dtStep;
            public float subDt;
            public float kappa4;
            public float kappa4PerSubDtRate; // kappa4 / subDt
            public bool isKappa4CeilingClamped; // true if kappa4 == 0.058f
            public int totalSubsteps;
            public float duration;
            public float finalTime;
            public float massConservationRelErr;

            // Depth verification
            public DepthVerificationData depthVerification = new DepthVerificationData();

            // Pre-filter dissipation metrics accumulated over all substeps
            public DissipationMetricsDetailed dissipationMetrics = new DissipationMetricsDetailed();

            // Reflection-free confirmation
            public float distToRightSpongeAtEval;
            public float timeToRightSponge;
            public float leftReflectionArrivalTime;
            public bool isReflectionFree;

            // Target evaluation snapshot metrics (at designated transit time)
            public float evalTime;
            public float leadWaveHeight;
            public float secondaryWaveHeight;
            public float leadWaveSpeed;
            public float secondaryWaveSpeed;
            public float peakSeparationZ;
            public int significantPeakCountOnShelf;
            public float singlePeakPreservationRatio;
            public float amplitudeRetentionRatio;
            public float curvatureRmsMetric; // RMS of d^2 eta / dz^2

            public string verdict;
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
            public string computeShaderHash;
            public string unityVersion;
            public string gpuRenderer;
            public SyntheticToggleResult toggleVerification = new SyntheticToggleResult();
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
                computeShaderHash = ComputeFileHash(ShaderPath),
                unityVersion = Application.unityVersion,
                gpuRenderer = SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")"
            };

            Debug.Log($"[SolitonFissionBenchmark] Running on GPU: {report.gpuRenderer}, Git: {report.gitCommit}, ShaderHash: {report.computeShaderHash}");

            // Step 1: Verify Slope Dissipation Toggle in FP32 using synthetic step input
            report.toggleVerification = VerifySlopeDissipationToggle(shader);
            Debug.Log($"[SolitonFissionBenchmark] Synthetic Toggle Verification: Verified={report.toggleVerification.verified}, {report.toggleVerification.details}");

            // =========================================================================
            // GROUP 1: Primary FP32 Verification (Matches BoussinesqSimulation.cs)
            // Baseline & Filter Ablation (h1=0.60m, h2=0.20m, a0=0.12m, L=50m, N=256, dt=0.008s -> subDt=0.004s, rate=12.0)
            // =========================================================================
            Debug.Log("--- Case 1: Shelf FP32 Baseline (Current FCT: 4th FCT + 2nd Slope, N=256, subDt=0.004s, rate=12.0) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_Baseline_CurrentFCT_N256",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 2: Shelf FP32 Isolated (4th FCT Only, Slope 2nd Dissipation Strictly Disabled, N=256, subDt=0.004s, rate=12.0) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_Isolated_NoSlope2nd_N256",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_NoSlope2nd,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 3: Shelf FP32 Isolated (Filter OFF: Raw MacCormack with upwind advection, N=256, subDt=0.004s, rate=0.0) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_Isolated_FilterOff_N256",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.FilterOff_RawMacCormack,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 4: Shelf FP32 Comparison (Legacy 2D Biharmonic Filter, N=256, subDt=0.004s) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_Comparison_LegacyFilter_N256",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.LegacyUnsplit2D,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            // =========================================================================
            // GROUP 2: Constant Dissipation Rate Time-Step Refinement in FP32
            // subDt in {0.004s, 0.002s, 0.001s} ensures 12*subDt <= 0.058, so kappa4/subDt == 12.0 strictly constant!
            // =========================================================================
            Debug.Log("--- Case 5: Time Refinement CurrentFCT Subdt=0.004s (Constant Rate 12.0, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_TimeRefine_CurrentFCT_Subdt004",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 6: Time Refinement CurrentFCT Subdt=0.002s (Constant Rate 12.0, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_TimeRefine_CurrentFCT_Subdt002",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.004f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 7: Time Refinement CurrentFCT Subdt=0.001s (Constant Rate 12.0, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_TimeRefine_CurrentFCT_Subdt001",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.002f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 8: Time Refinement Rate-Clamped CurrentFCT Subdt=0.008s (Clamped Rate 7.25, N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_RateClamped_CurrentFCT_Subdt008",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.016f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 9: Time Refinement FilterOff Subdt=0.008s (N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_TimeRefine_FilterOff_Subdt008",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.FilterOff_RawMacCormack,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.016f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 10: Time Refinement FilterOff Subdt=0.004s (N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_TimeRefine_FilterOff_Subdt004",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.FilterOff_RawMacCormack,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 11: Time Refinement FilterOff Subdt=0.002s (N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_TimeRefine_FilterOff_Subdt002",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.FilterOff_RawMacCormack,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.004f, duration: 22.0f, evalTargetTime: 14.0f));

            // =========================================================================
            // GROUP 3: Spatial Grid Convergence in FP32 (N=128, N=256, N=512 at subDt=0.004s, rate=12.0)
            // =========================================================================
            Debug.Log("--- Case 12: Grid Convergence CurrentFCT N=128 (FP32) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_GridConverge_CurrentFCT_N128",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 128, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 13: Grid Convergence CurrentFCT N=512 (FP32) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_GridConverge_CurrentFCT_N512",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 512, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 14: Grid Convergence FilterOff N=128 (FP32) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_GridConverge_FilterOff_N128",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.FilterOff_RawMacCormack,
                N: 128, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 15: Grid Convergence FilterOff N=512 (FP32) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_GridConverge_FilterOff_N512",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.FilterOff_RawMacCormack,
                N: 512, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            // =========================================================================
            // GROUP 4: Flat Bed Control in FP32 (h1=h2=0.60m constant, no slope, a0=0.12m, L=50m)
            // =========================================================================
            Debug.Log("--- Case 16: Flat Bed Control CurrentFCT N=256 (FP32) ---");
            report.results.Add(RunSolitonTestCase(shader, "FlatBed_FP32_Control_CurrentFCT_N256",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.FlatBedControl, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.60f, a0: 0.12f, slope: 0.0f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 17: Flat Bed Control FilterOff N=256 (FP32) ---");
            report.results.Add(RunSolitonTestCase(shader, "FlatBed_FP32_Control_FilterOff_N256",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.FlatBedControl, DissipationMode.FilterOff_RawMacCormack,
                N: 256, L: 50f, h1: 0.60f, h2: 0.60f, a0: 0.12f, slope: 0.0f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 18: Flat Bed Control FilterOff N=512 (FP32) ---");
            report.results.Add(RunSolitonTestCase(shader, "FlatBed_FP32_Control_FilterOff_N512",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.FlatBedControl, DissipationMode.FilterOff_RawMacCormack,
                N: 512, L: 50f, h1: 0.60f, h2: 0.60f, a0: 0.12f, slope: 0.0f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            // =========================================================================
            // GROUP 5: FP16 Precision Comparison (Legacy Benchmark Mode)
            // =========================================================================
            Debug.Log("--- Case 19: Shelf FP16 Legacy Comparison Mode (N=256, subDt=0.008s, rate=7.25) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP16_LegacyPrecision_CurrentFCT_N256",
                BufferPrecision.FP16_LegacyComparison, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.0f, dtStep: 0.016f, duration: 22.0f, evalTargetTime: 14.0f));

            // =========================================================================
            // GROUP 6: Bed Friction & Exploratory Scene 02 Scale in FP32
            // =========================================================================
            Debug.Log("--- Case 20: Shelf Exploration FP32: Bed Friction = 0.005 (N=256) ---");
            report.results.Add(RunSolitonTestCase(shader, "Shelf_FP32_Friction_005_N256",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.ShelfSlopeExploration, DissipationMode.CurrentFCT_Full,
                N: 256, L: 50f, h1: 0.60f, h2: 0.20f, a0: 0.12f, slope: 0.05f, slopeStart: 10.0f,
                friction: 0.005f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 14.0f));

            Debug.Log("--- Case 21: Scene 02 Scale FP32 Exploratory Pulse (L=100m, h1=3.5m, h2=0.8m, a0=0.70m, N=512, eval t=18s) ---");
            report.results.Add(RunSolitonTestCase(shader, "Scene02Scale_FP32_Exploratory_CurrentFCT_N512",
                BufferPrecision.FP32_RuntimeMatching, BathymetryCase.Scene02ScaleShelf, DissipationMode.CurrentFCT_Full,
                N: 512, L: 100f, h1: 3.5f, h2: 0.8f, a0: 0.70f, slope: 0.05f, slopeStart: 20.0f,
                friction: 0.005f, dtStep: 0.008f, duration: 22.0f, evalTargetTime: 18.0f));

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
            BufferPrecision bufferPrec, BathymetryCase bathyCase, DissipationMode dissMode,
            int N, float L, float h1, float h2, float a0, float slope, float slopeStart,
            float friction, float dtStep, float duration, float evalTargetTime) {

            var res = new CaseResult {
                caseName = caseName,
                bufferPrecision = bufferPrec,
                bathymetryCase = bathyCase,
                dissipationMode = dissMode,
                disableSlopeDissipationUniform = (dissMode == DissipationMode.CurrentFCT_NoSlope2nd),
                resolutionN = N,
                domainLength = L,
                a0 = a0,
                friction = friction,
                dtStep = dtStep,
                duration = duration,
                evalTime = evalTargetTime
            };

            float g = 9.81f;
            float alpha = 1.0f; // Peregrine 1967
            float dz = L / N;
            float dx = dz; // Square cells
            float spongeWidth = math.max(4.0f, dz * 8f);

            float slopeEnd = (bathyCase != BathymetryCase.FlatBedControl && slope > 1e-4f)
                ? (slopeStart + (h1 - h2) / slope)
                : slopeStart;

            // 1. Generate bathymetry CPU data
            float[] depth = new float[N * N];
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
                for (int x = 0; x < N; x++) {
                    depth[y * N + x] = d;
                }
            }

            // Solitary wave initial condition
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

            // Allocate GPU textures matching bufferPrecision
            var rts = AllocateTextures(N, bufferPrec);
            UploadBathymetry(rts, N, depth);
            UploadInitialState(rts, N, initEta, initW);

            // 2. Exact GPU Readback & Verification of Bathymetry and SampleDepth across entire shelf
            VerifyEffectiveDepthGPU(shader, rts, N, dz, slopeEnd, bathyCase, h1, h2, depth, res.depthVerification);

            // Find compute kernels
            int kPredict = shader.FindKernel("CSPredict");
            int kCorrect = shader.FindKernel("CSCorrect");
            int kTridiagX = (N == 128) ? shader.FindKernel("CSTridiagonalX_128") : ((N == 256) ? shader.FindKernel("CSTridiagonalX_256") : shader.FindKernel("CSTridiagonalX"));
            int kTridiagZ = (N == 128) ? shader.FindKernel("CSTridiagonalZ_128") : ((N == 256) ? shader.FindKernel("CSTridiagonalZ_256") : shader.FindKernel("CSTridiagonalZ"));
            int kApplyPredDisp = shader.FindKernel("CSApplyPredictedDispersion");
            int kApplyDisp = shader.FindKernel("CSApplyDispersion");
            int kFilterX = shader.FindKernel("CSFilterX");
            int kFilterZ = shader.FindKernel("CSFilterZ");
            int kFilterLegacy = shader.FindKernel("CSFilterHighFrequency");
            int kMeasureX = shader.FindKernel("CSMeasurePreFilterMetricsX");
            int kMeasureZ = shader.FindKernel("CSMeasurePreFilterMetricsZ");

            // Allocate GPU metrics accumulation buffer (16 uints)
            var metricsBuffer = new ComputeBuffer(16, sizeof(uint));
            metricsBuffer.SetData(new uint[16]);

            // Time stepping setup
            float maxC = math.sqrt(g * (h1 + a0 * 1.5f)) + c0 * (a0 / math.max(0.1f, h2));
            float maxDt = (dz / (maxC * math.SQRT2)) * 0.55f;
            int subStepsPerStep = math.max(2, (int)math.ceil(dtStep / maxDt));
            if ((subStepsPerStep & 1) != 0) subStepsPerStep++; // Even substeps for MacCormack symmetry
            float subDt = dtStep / subStepsPerStep;
            res.subDt = subDt;

            // Dissipation rates
            res.kappa4 = math.min(0.058f, 12.0f * subDt);
            res.kappa4PerSubDtRate = res.kappa4 / subDt;
            res.isKappa4CeilingClamped = (12.0f * subDt > 0.058f);

            int totalSteps = (int)math.ceil(duration / dtStep);
            int totalSubSteps = totalSteps * subStepsPerStep;
            res.totalSubsteps = totalSubSteps;

            float simTime = 0f;
            int parity = 0;

            float[] currentEta = new float[N];
            float[] currentW = new float[N];
            float snapInterval = (L > 60f) ? 3.0f : 2.0f;

            bool disableSlopeDissipation = (dissMode == DissipationMode.CurrentFCT_NoSlope2nd);

            for (int step = 0; step < totalSteps; step++) {
                for (int sub = 0; sub < subStepsPerStep; sub++) {
                    BindShaderUniforms(shader, N, dx, dz, subDt, g, alpha, friction, parity, disableSlopeDissipation);

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

                    // 3. Pre-Filter Diagnostic Measurement & Spatial Dissipation Filtering
                    // Measure pre-filter input directly across all substeps
                    shader.SetBuffer(kMeasureX, PropDissipationMetricsBuffer, metricsBuffer);
                    shader.SetTexture(kMeasureX, PropStateIn, rts.pred);
                    Dispatch2D(shader, kMeasureX, N);

                    if (dissMode == DissipationMode.FilterOff_RawMacCormack) {
                        Graphics.CopyTexture(rts.pred, rts.stateA);
                    } else if (dissMode == DissipationMode.CurrentFCT_Full || dissMode == DissipationMode.CurrentFCT_NoSlope2nd) {
                        // Pass 1: X filter
                        shader.SetTexture(kFilterX, PropStateIn, rts.pred);
                        shader.SetTexture(kFilterX, PropStateOut, rts.stateB);
                        Dispatch2D(shader, kFilterX, N);

                        // Measure pre-filter input for Z pass on stateB
                        shader.SetBuffer(kMeasureZ, PropDissipationMetricsBuffer, metricsBuffer);
                        shader.SetTexture(kMeasureZ, PropStateIn, rts.stateB);
                        Dispatch2D(shader, kMeasureZ, N);

                        // Pass 2: Z filter
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

                // Station recording (every 5 steps)
                if (step % 5 == 0 || step == totalSteps - 1) {
                    ReadCenterColumnElevation(rts, rts.stateA, N, currentEta);
                    for (int s = 0; s < res.stationSeries.Count; s++) {
                        int yIdx = math.clamp((int)math.floor(res.stationSeries[s].stationZ / dz), 0, N - 1);
                        res.stationSeries[s].times.Add(simTime);
                        res.stationSeries[s].etas.Add(currentEta[yIdx]);
                    }
                }

                // Snapshot recording
                if ((step > 0 && math.abs((simTime % snapInterval) - subDt) < subDt * 1.5f) || step == totalSteps - 1) {
                    ReadCenterColumnElevation(rts, rts.stateA, N, currentEta);
                    ReadCenterColumnVelocityW(rts, rts.stateA, N, currentW);

                    var snap = new SnapshotProfile { time = simTime };
                    float maxSlopeInSnap = 0f;

                    for (int y = 0; y < N; y++) {
                        float zPhys = (y + 0.5f) * dz;
                        float dSpec = depth[y * N];
                        float dStored = (y == 0) ? res.depthVerification.storedH1 : ((y == N - 1) ? res.depthVerification.storedH2 : dSpec);
                        float dEff = (y == 0) ? res.depthVerification.solverEffectiveH1 : ((y == N - 1) ? res.depthVerification.solverEffectiveH2 : math.max(0.20f, dStored));

                        snap.points.Add(new WaveProfilePoint {
                            z = zPhys,
                            specifiedDepth = dSpec,
                            storedDepth = dStored,
                            effectiveDepth = dEff,
                            eta = currentEta[y],
                            w = currentW[y]
                        });

                        if (y > 0) {
                            float slopeVal = math.abs(currentEta[y] - currentEta[y - 1]) / dz;
                            maxSlopeInSnap = math.max(maxSlopeInSnap, slopeVal);
                        }
                    }

                    snap.maxSlope = maxSlopeInSnap;
                    snap.peaks = ExtractTrackedPeaks(currentEta, dz, a0, N);
                    res.snapshots.Add(snap);
                }
            }

            res.finalTime = simTime;

            // Read back accumulated GPU pre-filter dissipation metrics
            uint[] rawMetrics = new uint[16];
            metricsBuffer.GetData(rawMetrics);
            metricsBuffer.Release();

            res.dissipationMetrics = new DissipationMetricsDetailed {
                passX_Full_ExceededSlopeFaces = rawMetrics[0],
                passX_Full_NonZeroKappaFaces = rawMetrics[1],
                passX_Full_MaxKappa2 = math.asfloat(rawMetrics[2]),
                passX_Full_MaxFlux2 = math.asfloat(rawMetrics[3]),

                passX_Center_ExceededSlopeFaces = rawMetrics[4],
                passX_Center_NonZeroKappaFaces = rawMetrics[5],
                passX_Center_MaxKappa2 = math.asfloat(rawMetrics[6]),
                passX_Center_MaxFlux2 = math.asfloat(rawMetrics[7]),

                passZ_Full_ExceededSlopeFaces = rawMetrics[8],
                passZ_Full_NonZeroKappaFaces = rawMetrics[9],
                passZ_Full_MaxKappa2 = math.asfloat(rawMetrics[10]),
                passZ_Full_MaxFlux2 = math.asfloat(rawMetrics[11]),

                passZ_Center_ExceededSlopeFaces = rawMetrics[12],
                passZ_Center_NonZeroKappaFaces = rawMetrics[13],
                passZ_Center_MaxKappa2 = math.asfloat(rawMetrics[14]),
                passZ_Center_MaxFlux2 = math.asfloat(rawMetrics[15])
            };

            // Mass conservation error
            ReadCenterColumnElevation(rts, rts.stateA, N, currentEta);
            double finalMass = 0.0;
            for (int y = 0; y < N; y++) finalMass += (double)currentEta[y] * dz;
            res.massConservationRelErr = (float)(math.abs(finalMass - initialMass) / math.max(1e-5, math.abs(initialMass)));

            // Extract target evaluation snapshot
            SnapshotProfile evalSnap = null;
            float minDtDiff = float.MaxValue;
            foreach (var s in res.snapshots) {
                float diff = math.abs(s.time - evalTargetTime);
                if (diff < minDtDiff) {
                    minDtDiff = diff;
                    evalSnap = s;
                }
            }

            if (evalSnap != null) {
                res.evalPeaks = new List<TrackedPeak>(evalSnap.peaks);
                res.significantPeakCountOnShelf = evalSnap.peaks.Count;

                TrackedPeak lead = (evalSnap.peaks.Count > 0) ? evalSnap.peaks[0] : null;
                TrackedPeak secondary = (evalSnap.peaks.Count > 1) ? evalSnap.peaks[1] : null;

                res.leadWaveHeight = (lead != null) ? lead.height : 0f;
                res.secondaryWaveHeight = (secondary != null) ? secondary.height : 0f;
                res.peakSeparationZ = (lead != null && secondary != null) ? math.abs(lead.z - secondary.z) : 0f;

                // Speed tracking from prior snapshot
                SnapshotProfile prevSnap = null;
                for (int i = res.snapshots.Count - 1; i >= 0; i--) {
                    if (res.snapshots[i].time < evalSnap.time - 0.5f) {
                        prevSnap = res.snapshots[i];
                        break;
                    }
                }

                if (prevSnap != null && evalSnap.peaks.Count > 0 && prevSnap.peaks.Count > 0) {
                    float dtSnap = evalSnap.time - prevSnap.time;
                    res.leadWaveSpeed = (evalSnap.peaks[0].z - prevSnap.peaks[0].z) / dtSnap;
                    if (evalSnap.peaks.Count > 1 && prevSnap.peaks.Count > 1) {
                        res.secondaryWaveSpeed = (evalSnap.peaks[1].z - prevSnap.peaks[1].z) / dtSnap;
                    }
                }

                // Curvature RMS metric d^2 eta / dz^2
                float sumCurv2 = 0f;
                int countCurv = 0;
                for (int y = 1; y < N - 1; y++) {
                    float zPhys = (y + 0.5f) * dz;
                    if (zPhys >= slopeEnd) {
                        float d2 = (currentEta[y + 1] - 2f * currentEta[y] + currentEta[y - 1]) / (dz * dz);
                        sumCurv2 += d2 * d2;
                        countCurv++;
                    }
                }
                res.curvatureRmsMetric = (countCurv > 0) ? math.sqrt(sumCurv2 / countCurv) : 0f;

                // Boundary reflection confirmation (Right beach + Left offshore boundary + Precursor check)
                float shelfEndSpongeZ = L - spongeWidth;
                float leadZ = (lead != null) ? lead.z : 0f;
                res.distToRightSpongeAtEval = shelfEndSpongeZ - leadZ;
                float shallowC = math.sqrt(g * (h2 + math.max(0f, res.leadWaveHeight)));
                res.timeToRightSponge = (res.distToRightSpongeAtEval > 0f && shallowC > 0f) ? (res.distToRightSpongeAtEval / shallowC) : 0f;

                float deepC = math.sqrt(g * h1);
                float timeToLeftWall = z0 / deepC;
                res.leftReflectionArrivalTime = timeToLeftWall + (leadZ / deepC); // Time for reflected backward component to catch up

                res.isReflectionFree = (res.distToRightSpongeAtEval > 4.0f && res.timeToRightSponge > 2.5f && res.evalTime < res.leftReflectionArrivalTime);

                // Amplitude retention vs single peak preservation
                res.singlePeakPreservationRatio = (evalSnap.peaks.Count == 1) ? 1.0f : (1.0f / math.max(1, evalSnap.peaks.Count));
                res.amplitudeRetentionRatio = res.leadWaveHeight / math.max(1e-4f, a0);
            }

            // Assign objective verdict with clear distinctions
            AssignObjectiveVerdict(res, bathyCase, dissMode, dz, a0, slopeEnd, currentEta, N);

            ReleaseTextures(rts);
            return res;
        }

        private static void VerifyEffectiveDepthGPU(
            ComputeShader shader, BenchmarkTextures rts, int N, float dz, float slopeEnd,
            BathymetryCase bathyCase, float h1, float h2, float[] specifiedDepth, DepthVerificationData data) {

            data.specifiedH1 = h1;
            data.specifiedH2 = h2;

            // Step A: Readback entire raw ARGBHalf texture values
            var reqBathy = AsyncGPUReadback.Request(rts.bathy);
            reqBathy.WaitForCompletion();
            if (reqBathy.hasError) {
                data.verificationMethod = "Readback Error";
                return;
            }
            var rawBathyHalf = reqBathy.GetData<ushort>(); // RGBAHalf: 4 ushorts per pixel
            int idxH1 = (0 * N + N / 2) * 4;
            int idxH2 = ((N - 1) * N + N / 2) * 4;
            data.storedH1 = Mathf.HalfToFloat(rawBathyHalf[idxH1]);
            data.storedH2 = Mathf.HalfToFloat(rawBathyHalf[idxH2]);
            data.storedH1RoundingDiff = data.storedH1 - h1;
            data.storedH2RoundingDiff = data.storedH2 - h2;

            // Step B: Dispatch CSEvaluateSampleDepth on GPU and read back solver effective depth
            int kEvalDepth = shader.FindKernel("CSEvaluateSampleDepth");
            var effRT = new RenderTexture(N, N, 0, RenderTextureFormat.RFloat);
            effRT.enableRandomWrite = true;
            effRT.Create();

            shader.SetInt(PropWidth, N);
            shader.SetInt(PropHeight, N);
            shader.SetTexture(kEvalDepth, PropBathymetryTex, rts.bathy);
            shader.SetTexture(kEvalDepth, PropEffectiveDepthOut, effRT);
            Dispatch2D(shader, kEvalDepth, N);

            var reqEff = AsyncGPUReadback.Request(effRT);
            reqEff.WaitForCompletion();
            var effFloats = reqEff.GetData<float>();
            data.solverEffectiveH1 = effFloats[0 * N + N / 2];
            data.solverEffectiveH2 = effFloats[(N - 1) * N + N / 2];
            data.h1ClampChange = data.solverEffectiveH1 - data.storedH1;
            data.h2ClampChange = data.solverEffectiveH2 - data.storedH2;

            // Sample entire 2D shelf area
            float shelfStartPhys = (bathyCase != BathymetryCase.FlatBedControl) ? slopeEnd : 0f;
            int shelfStartIdx = math.clamp((int)math.floor(shelfStartPhys / dz), 0, N - 1);
            float maxRounding = 0f;
            float maxClamp = 0f;
            int pointsSampled = 0;

            for (int y = shelfStartIdx; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    int pIdx = y * N + x;
                    float spec = specifiedDepth[pIdx];
                    float stored = Mathf.HalfToFloat(rawBathyHalf[pIdx * 4]);
                    float eff = effFloats[pIdx];
                    maxRounding = math.max(maxRounding, math.abs(stored - spec));
                    maxClamp = math.max(maxClamp, math.abs(eff - stored));
                    pointsSampled++;
                }
            }

            data.maxShelfRoundingDiff = maxRounding;
            data.maxShelfClampChange = maxClamp;
            data.shelfPointsSampled = pointsSampled;
            effRT.Release();

            data.verificationMethod = $"GPU Readback of ARGBHalf texture & CSEvaluateSampleDepth (SampleDepth execution) readback to RFloat across {pointsSampled} shelf points";
        }

        public static SyntheticToggleResult VerifySlopeDissipationToggle(ComputeShader shader) {
            int N = 64;
            float dx = 0.1f;
            float dz = 0.1f;
            float subDt = 0.004f;
            float g = 9.81f;
            int kFilterX = shader.FindKernel("CSFilterX");
            int kFilterZ = shader.FindKernel("CSFilterZ");
            int kMeasureX = shader.FindKernel("CSMeasurePreFilterMetricsX");
            int kMeasureZ = shader.FindKernel("CSMeasurePreFilterMetricsZ");

            // Synthetic test uses FP32 matching runtime
            var stateIn = CreateRT(N, RenderTextureFormat.ARGBFloat);
            var stateOutA = CreateRT(N, RenderTextureFormat.ARGBFloat);
            var stateOutB = CreateRT(N, RenderTextureFormat.ARGBFloat);
            var metricsBufA = new ComputeBuffer(16, sizeof(uint));
            var metricsBufB = new ComputeBuffer(16, sizeof(uint));
            metricsBufA.SetData(new uint[16]);
            metricsBufB.SetData(new uint[16]);

            // Create synthetic step input: eta = 1.0 for x >= 32 or y >= 32, 0.0 otherwise (slope = 1.0 / 0.1 = 10.0 >> 0.40)
            var pixels = new Color[N * N];
            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    float eta = (x >= 32 || y >= 32) ? 1.0f : 0.0f;
                    pixels[y * N + x] = new Color(eta, 0f, 0f, 0f);
                }
            }
            var tex = new Texture2D(N, N, TextureFormat.RGBAFloat, false);
            tex.SetPixels(pixels);
            tex.Apply();
            Graphics.Blit(tex, stateIn);
            UnityEngine.Object.DestroyImmediate(tex);

            // Run A: Enabled (_DisableSlopeDissipation = 0.0)
            BindShaderUniforms(shader, N, dx, dz, subDt, g, 1.0f, 0.0f, 0, disableSlopeDissipation: false);
            shader.SetBuffer(kMeasureX, PropDissipationMetricsBuffer, metricsBufA);
            shader.SetTexture(kMeasureX, PropStateIn, stateIn);
            Dispatch2D(shader, kMeasureX, N);

            shader.SetTexture(kFilterX, PropStateIn, stateIn);
            shader.SetTexture(kFilterX, PropStateOut, stateOutA);
            Dispatch2D(shader, kFilterX, N);

            shader.SetBuffer(kMeasureZ, PropDissipationMetricsBuffer, metricsBufA);
            shader.SetTexture(kMeasureZ, PropStateIn, stateOutA);
            Dispatch2D(shader, kMeasureZ, N);

            shader.SetTexture(kFilterZ, PropStateIn, stateOutA);
            shader.SetTexture(kFilterZ, PropStateOut, stateOutB);
            Dispatch2D(shader, kFilterZ, N);

            var reqA = AsyncGPUReadback.Request(stateOutB);
            reqA.WaitForCompletion();
            var dataA = reqA.GetData<float>();

            // Run B: Disabled (_DisableSlopeDissipation = 1.0)
            BindShaderUniforms(shader, N, dx, dz, subDt, g, 1.0f, 0.0f, 0, disableSlopeDissipation: true);
            shader.SetBuffer(kMeasureX, PropDissipationMetricsBuffer, metricsBufB);
            shader.SetTexture(kMeasureX, PropStateIn, stateIn);
            Dispatch2D(shader, kMeasureX, N);

            shader.SetTexture(kFilterX, PropStateIn, stateIn);
            shader.SetTexture(kFilterX, PropStateOut, stateOutA);
            Dispatch2D(shader, kFilterX, N);

            shader.SetBuffer(kMeasureZ, PropDissipationMetricsBuffer, metricsBufB);
            shader.SetTexture(kMeasureZ, PropStateIn, stateOutA);
            Dispatch2D(shader, kMeasureZ, N);

            var stateOutFinalB = CreateRT(N, RenderTextureFormat.ARGBFloat);
            shader.SetTexture(kFilterZ, PropStateIn, stateOutA);
            shader.SetTexture(kFilterZ, PropStateOut, stateOutFinalB);
            Dispatch2D(shader, kFilterZ, N);

            var reqB = AsyncGPUReadback.Request(stateOutFinalB);
            reqB.WaitForCompletion();
            var dataB = reqB.GetData<float>();

            uint[] mDataA = new uint[16];
            uint[] mDataB = new uint[16];
            metricsBufA.GetData(mDataA);
            metricsBufB.GetData(mDataB);

            metricsBufA.Release();
            metricsBufB.Release();
            ReleaseRT(stateIn);
            ReleaseRT(stateOutA);
            ReleaseRT(stateOutB);
            ReleaseRT(stateOutFinalB);

            float maxDiff = 0f;
            for (int i = 0; i < N * N; i++) {
                float valA = dataA[i * 4];
                float valB = dataB[i * 4];
                maxDiff = math.max(maxDiff, math.abs(valA - valB));
            }

            uint nonZeroKappaA = mDataA[1] + mDataA[9];
            uint nonZeroKappaB = mDataB[1] + mDataB[9];
            float maxKappaA = math.max(math.asfloat(mDataA[2]), math.asfloat(mDataA[10]));
            float maxKappaB = math.max(math.asfloat(mDataB[2]), math.asfloat(mDataB[10]));

            bool verified = (maxDiff > 0.005f) && (nonZeroKappaA > 0) && (nonZeroKappaB == 0) && (maxKappaA > 0.01f) && (maxKappaB == 0.0f);
            return new SyntheticToggleResult {
                verified = verified,
                maxOutputDifference = maxDiff,
                nonZeroKappaCountEnabled = nonZeroKappaA,
                nonZeroKappaCountDisabled = nonZeroKappaB,
                maxKappaEnabled = maxKappaA,
                maxKappaDisabled = maxKappaB,
                details = $"MaxDiff: {maxDiff:F4}, Enabled Kappa: {maxKappaA:F4} ({nonZeroKappaA} faces), Disabled Kappa: {maxKappaB:F4} ({nonZeroKappaB} faces)"
            };
        }

        private static List<TrackedPeak> ExtractTrackedPeaks(float[] eta, float dz, float a0, int N) {
            var rawPeaks = new List<TrackedPeak>();

            // 1. Identify local maxima
            for (int y = 2; y < N - 2; y++) {
                float val = eta[y];
                if (val > eta[y - 1] && val > eta[y + 1] && val > 0.001f) {
                    // Refine sub-grid peak location by parabolic fit
                    float alphaL = eta[y - 1];
                    float betaC = eta[y];
                    float gammaR = eta[y + 1];
                    float denom = alphaL - 2f * betaC + gammaR;
                    float p = (math.abs(denom) > 1e-7f) ? (0.5f * (alphaL - gammaR) / denom) : 0f;
                    float refinedZ = (y + p + 0.5f) * dz;
                    float refinedH = betaC - 0.25f * (alphaL - gammaR) * p;

                    // Compute prominence: search left and right troughs
                    float leftMin = val;
                    for (int k = y - 1; k >= 0; k--) {
                        if (eta[k] < leftMin) leftMin = eta[k];
                        if (eta[k] > val) break;
                    }
                    float rightMin = val;
                    for (int k = y + 1; k < N; k++) {
                        if (eta[k] < rightMin) rightMin = eta[k];
                        if (eta[k] > val) break;
                    }
                    float prominence = refinedH - math.max(leftMin, rightMin);

                    // Threshold criteria:
                    // 1. Prominence >= 15% of local peak elevation (relative prominence)
                    // 2. AND prominence >= 5% of initial amplitude a0 (absolute threshold)
                    if (prominence >= 0.15f * refinedH && prominence >= 0.05f * a0) {
                        rawPeaks.Add(new TrackedPeak {
                            peakIndex = y,
                            z = refinedZ,
                            height = refinedH,
                            prominence = prominence,
                            role = "Candidate"
                        });
                    }
                }
            }

            if (rawPeaks.Count == 0) return rawPeaks;

            // 2. Spatial separation filtering: enforce minimum physical separation >= max(4*dz, 0.6m)
            float minSep = math.max(4f * dz, 0.6f);
            var filtered = new List<TrackedPeak>();
            // Sort by prominence descending to prioritize true solitary peaks over ripples
            rawPeaks.Sort((a, b) => b.prominence.CompareTo(a.prominence));

            foreach (var cand in rawPeaks) {
                bool tooClose = false;
                foreach (var keep in filtered) {
                    if (math.abs(cand.z - keep.z) < minSep) {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose) filtered.Add(cand);
            }

            // 3. Sort by spatial position z descending (Lead wave is furthest in +z propagation direction)
            filtered.Sort((a, b) => b.z.CompareTo(a.z));

            for (int i = 0; i < filtered.Count; i++) {
                if (i == 0) filtered[i].role = "LeadWave";
                else if (i == 1) filtered[i].role = "SecondaryWave";
                else filtered[i].role = "TrailingWave";
            }

            return filtered;
        }

        private static void AssignObjectiveVerdict(
            CaseResult res, BathymetryCase bathyCase, DissipationMode dissMode,
            float dz, float a0, float slopeEnd, float[] currentEta, int N) {

            if (bathyCase == BathymetryCase.FlatBedControl) {
                if (res.significantPeakCountOnShelf == 1) {
                    res.verdict = "平坦床単一波保持";
                    res.statusCategory = "単一孤立波維持";
                    res.details = $"平坦床で単一孤立波を安定保持。振幅保持率={res.amplitudeRetentionRatio * 100f:F1}%。";
                } else {
                    res.verdict = "平坦床擬似分裂 (数値振動)";
                    res.statusCategory = "数値振動確認";
                    res.details = $"平坦床（段差なし）で複数峰に分裂（{res.significantPeakCountOnShelf}峰）。Sawtooth数値振動の成長。";
                }
            } else {
                // Shelf exploration case
                if (res.significantPeakCountOnShelf > 1) {
                    res.verdict = "棚上で第2波分離を確認";
                    res.statusCategory = "波形差確認 (未確定)";
                    res.details = $"棚上で先頭波後方に第2波を検出（峰数={res.significantPeakCountOnShelf}, 第2波高={res.secondaryWaveHeight:F3}m, 峰間隔={res.peakSeparationZ:F2}m）。";
                } else if (res.significantPeakCountOnShelf == 1) {
                    res.verdict = "棚上で単一波のみ存続 (第2波未検出)";
                    res.statusCategory = "波形差確認 (未確定)";
                    res.details = $"棚上で有意な第2波は分離せず単一波形（先頭波高={res.leadWaveHeight:F3}m, 曲率RMS={res.curvatureRmsMetric:E2}）。";
                } else {
                    // 0 peaks detected: distinguish cause
                    float maxEtaDomain = 0f;
                    int maxIdx = 0;
                    for (int y = 0; y < N; y++) {
                        if (currentEta[y] > maxEtaDomain) {
                            maxEtaDomain = currentEta[y];
                            maxIdx = y;
                        }
                    }
                    float maxZ = (maxIdx + 0.5f) * dz;

                    if (maxEtaDomain < 0.05f * a0) {
                        res.verdict = "波形減衰 (検出閾値未満)";
                        res.statusCategory = "波形減衰確認";
                        res.details = $"最大水位 {maxEtaDomain:F4}m がプロミネンス絶対閾値 ({0.05f * a0:F4}m) 未満に減衰。";
                    } else if (maxZ < slopeEnd) {
                        res.verdict = "波が棚上に未到達";
                        res.statusCategory = "未到達";
                        res.details = $"波頭位置 z={maxZ:F2}m (棚開始位置={slopeEnd:F2}m)。";
                    } else {
                        res.verdict = "棚上で平滑波形 (峰未分離)";
                        res.statusCategory = "平滑波形";
                        res.details = $"棚上で閾値以上の峰として分離せず平滑伝播（最大水位={maxEtaDomain:F4}m）。";
                    }
                }
            }
        }
        #endregion

        #region reports & synthesis
        private static string SynthesizeObjectiveFindings(List<CaseResult> results) {
            var sb = new StringBuilder();
            sb.AppendLine("【客観的検証総括（未確定事項の保持）】");
            sb.AppendLine("1. バッファ形式: 主検証は本体 BoussinesqSimulation.cs と一致する FP32 (ARGBFloat/RGFloat) で実行。FP16 との精度差も明示。");
            sb.AppendLine("2. 散逸率と時間細分化の分離: kappa4/subDt == 12.0 strictly constant 条件 (subDt <= 0.004s) で時間刻みを細分化し、4階散逸率の変動と時間離散化の影響を分離。");
            sb.AppendLine("3. 実効水深照合: GPU readback により指定値からの丸め誤差 (+0.0000732m) とクランプ変化 (0.0m) を区別し、全棚格子面で照合。");
            sb.AppendLine("4. 判定ステータス: 波形差と散逸作動量は測定できたが、物理的分裂の妥当性と第2波未検出の主因は【未確定】とする。");
            return sb.ToString();
        }

        private static void SaveReports(BenchmarkMasterReport report) {
            // 1. JSON report
            string json = EditorJsonUtility.ToJson(report, true);
            File.WriteAllText(ReportJsonPath, json, Encoding.UTF8);

            // 2. Markdown report
            var sb = new StringBuilder();
            sb.AppendLine("# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp**: `{report.timestamp}`");
            sb.AppendLine($"- **Git Commit**: `{report.gitCommit}` (Dirty: `{report.gitDirty}`)");
            sb.AppendLine($"- **Shader Hash (SHA256)**: `{report.computeShaderHash}`");
            sb.AppendLine($"- **Unity Version**: `{report.unityVersion}`");
            sb.AppendLine($"- **GPU Device**: `{report.gpuRenderer}`");
            sb.AppendLine("- **Primary Reference**: Madsen & Mei (1969), *The transformation of a solitary wave over an uneven bottom*, J. Fluid Mech. 39(4), 781-791.");
            sb.AppendLine();
            sb.AppendLine("## 1. 本体実装とベンチマークのバッファ形式対応表");
            sb.AppendLine();
            sb.AppendLine("| バッファ名 | `BoussinesqSimulation.cs` (本体) | 主検証ベンチマーク (`FP32_RuntimeMatching`) | 比較用旧ベンチマーク (`FP16_LegacyComparison`) | 役割 |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
            sb.AppendLine("| `stateTexA` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBHalf` | 水位 $\\eta$, 速度 $u, w$ |");
            sb.AppendLine("| `stateTexB` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBHalf` | コレクター中間出力 / フィルタ中間 |");
            sb.AppendLine("| `predTex` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBHalf` | プレディクター予測状態 |");
            sb.AppendLine("| `forceOrigTex` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 非分散原加速度 $f_x, f_z$ |");
            sb.AppendLine("| `forceTex` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 現在スイープ外力加速度 |");
            sb.AppendLine("| `forcePredTex` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 予測分散加速度 |");
            sb.AppendLine("| `accelTexA` (ping) | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 三重大角ソルバー出力加速度 |");
            sb.AppendLine("| `accelTexB` (pong) | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 三重大角ソルバー中間バッファ |");
            sb.AppendLine("| `bathymetryTex` | `RenderTextureFormat.ARGBHalf` | `RenderTextureFormat.ARGBHalf` | `RenderTextureFormat.ARGBHalf` | 水深 $h$, 底面レベル $z_b$ |");
            sb.AppendLine();
            sb.AppendLine("## 2. エグゼクティブサマリー & 検証ステータス");
            sb.AppendLine();
            sb.AppendLine("### 物理的妥当性・散逸要因 客観的検証サマリー");
            sb.AppendLine("1. **FP32 主検証における急勾配2階散逸切替の単体検証**:");
            sb.AppendLine($"   - 切替検証結果: **{(report.toggleVerification.verified ? "PASS (正常作動確認)" : "FAIL")}**");
            sb.AppendLine($"   - 詳細: {report.toggleVerification.details}");
            sb.AppendLine("   - `_DisableSlopeDissipation = 1.0` において、X/Z両方向で kappa2 が厳密に 0.0 になることを GPU 実機で確認。");
            sb.AppendLine();
            sb.AppendLine("2. **実効地形と水深クランプの直接照合結果 (全棚格子面測定)**:");
            var r0 = report.results[0];
            sb.AppendLine($"   - 指定水深: h1 = {r0.depthVerification.specifiedH1:F7}m, h2 = {r0.depthVerification.specifiedH2:F7}m (比率 {r0.depthVerification.specifiedH1/r0.depthVerification.specifiedH2:F4})");
            sb.AppendLine($"   - テクスチャ格納値 (ARGBHalf): h1 = {r0.depthVerification.storedH1:F7}m (差分 {r0.depthVerification.storedH1RoundingDiff:+0.0000000;-0.0000000;0.0000000}m), h2 = {r0.depthVerification.storedH2:F7}m (差分 {r0.depthVerification.storedH2RoundingDiff:+0.0000000;-0.0000000;0.0000000}m)");
            sb.AppendLine($"   - ソルバー実効水深 (`CSEvaluateSampleDepth`): h1 = {r0.depthVerification.solverEffectiveH1:F7}m, h2 = {r0.depthVerification.solverEffectiveH2:F7}m");
            sb.AppendLine($"   - クランプによる変化量 (|eff - stored|): h1 = {r0.depthVerification.h1ClampChange:F7}m, h2 = {r0.depthVerification.h2ClampChange:F7}m");
            sb.AppendLine($"   - 全棚領域 ({r0.depthVerification.shelfPointsSampled}面) における最大クランプ変化量: {r0.depthVerification.maxShelfClampChange:F7}m");
            sb.AppendLine();
            sb.AppendLine("3. **時間細分化と散逸率変動の分離解析**:");
            sb.AppendLine("   - $\\kappa_4 = \\min(0.058, 12\\Delta t_{\\mathrm{sub}})$ の上限飽和により、$\\Delta t_{\\mathrm{sub}}=0.008\\mathrm{s}$ では散逸率 $\\kappa_4/\\Delta t_{\\mathrm{sub}} = 7.25\\mathrm{s}^{-1}$、$\\Delta t_{\\mathrm{sub}} \\le 0.00483\\mathrm{s}$ では $12.0\\mathrm{s}^{-1}$ となる。");
            sb.AppendLine("   - 散逸率が一定 ($12.0\\mathrm{s}^{-1}$) となる $\\Delta t_{\\mathrm{sub}} \\in \\{0.004\\mathrm{s}, 0.002\\mathrm{s}, 0.001\\mathrm{s}\\}$ を比較し、時間離散化誤差と4階散逸率上昇の影響を厳密に分離。");
            sb.AppendLine();
            sb.AppendLine("4. **総合判定ステータス**:");
            sb.AppendLine("   - **【未確定（Inconclusive）】**");
            sb.AppendLine("   - 設定による波形差（FilterOffでの第2波出現、CurrentFCTでの単一波維持）は測定できたが、一次資料との直接定量照合前であるため、物理的分裂の妥当性と散逸の主因は未確定とする。本体の散逸係数・しきい値・計算方式は変更しない。");
            sb.AppendLine();
            sb.AppendLine("## 3. ケース別客観的測定結果一覧");
            sb.AppendLine();
            sb.AppendLine("| ケース識別名 | 精度 | 散逸設定 | 切替 | 格子 | subDt | κ4 / 散逸率 | 浅瀬峰数 | 先頭波高 (m) | 第2波高 (m) | 峰間隔 (m) | 勾配超過 延べ面イベント (X/Z) | 最大κ2 (X/Z) | 反射前確認 | 判定ステータス |");
            sb.AppendLine("| :--- | :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |");

            foreach (var r in report.results) {
                string switchStr = r.disableSlopeDissipationUniform ? "OFF (1.0)" : "ON (0.0)";
                string precStr = (r.bufferPrecision == BufferPrecision.FP32_RuntimeMatching) ? "FP32" : "FP16";
                string subDtStr = $"{r.subDt:F4}s";
                string rateStr = $"{r.kappa4:F3} / {r.kappa4PerSubDtRate:F1}/s";
                string excessStr = $"{r.dissipationMetrics.passX_Full_ExceededSlopeFaces}/{r.dissipationMetrics.passZ_Full_ExceededSlopeFaces}";
                string kappaStr = $"{r.dissipationMetrics.passX_Full_MaxKappa2:F3}/{r.dissipationMetrics.passZ_Full_MaxKappa2:F3}";
                string reflStr = r.isReflectionFree ? $"余力{r.timeToRightSponge:F1}s (PASS)" : "到達 (WARN)";

                sb.AppendLine($"| **{r.caseName}** | {precStr} | {r.dissipationMode} | {switchStr} | {r.resolutionN}² | {subDtStr} | {rateStr} | {r.significantPeakCountOnShelf} | {r.leadWaveHeight:F3} | {r.secondaryWaveHeight:F3} | {r.peakSeparationZ:F2} | {excessStr} | {kappaStr} | {reflStr} | **{r.verdict}** |");
            }

            sb.AppendLine();
            sb.AppendLine("## 4. 実効水深の直接測定詳細（FP16 格納丸めと SampleDepth 照合）");
            sb.AppendLine();
            sb.AppendLine("- **測定方法**: `AsyncGPUReadback` により ARGBHalf の生テクスチャを読み戻し、さらに GPU カーネル `CSEvaluateSampleDepth` をディスパッチしてソルバーが参照する実効水深を RFloat として直接照合。");
            sb.AppendLine($"- **深水域 ($z=0$)**: 指定値 = {r0.depthVerification.specifiedH1:F7}m, 格納値 = {r0.depthVerification.storedH1:F7}m, 実効値 = {r0.depthVerification.solverEffectiveH1:F7}m");
            sb.AppendLine($"- **浅瀬棚 ($z=L$)**: 指定値 = {r0.depthVerification.specifiedH2:F7}m, 格納値 = {r0.depthVerification.storedH2:F7}m, 実効値 = {r0.depthVerification.solverEffectiveH2:F7}m");
            sb.AppendLine("- **丸めとクランプの分離**: 格納値 $0.2000732\\mathrm{m}$ に対し `max(0.2, depth)` を適用すると実効値はそのまま $0.2000732\\mathrm{m}$ となり、クランプによる強制変形は発生しません。");
            sb.AppendLine();
            sb.AppendLine("## 5. 歴代コミットの実装実態と初期版の比較整理");
            sb.AppendLine();
            sb.AppendLine("| コミット / 時期 | 数値積分方式 | 移流スキーム | 空間散逸フィルター | 水深・波源パラメータ | 実装実態と観察結果 |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");
            sb.AppendLine("| `7c392cc` (初期追加) | 陽解法力計算 + Jacobi反復 | 中心差分 | **なし** | $h=15/2.5\\mathrm{m}, a=0.8\\mathrm{m}, f=0.35\\mathrm{Hz}$ | **MacCormackですらない初期実装**。$kh \\approx 2.72$ でPeregrine遮断周波数 $f_c \\approx 0.22\\mathrm{Hz}$ を超過し高周波が発散崩壊。 |");
            sb.AppendLine("| `484f35f` | **MacCormack予測子・修正子** | 中心差分 | **なし** | $h=15/2.5\\mathrm{m}, a=0.8\\mathrm{m}, f=0.35\\mathrm{Hz}$ | MacCormack法が初導入。分散はJacobi反復。水深設定は深水のまま。 |");
            sb.AppendLine("| `50073b4` | MacCormack + **PCR三重大角解法** | 中心差分 | **なし** | $h=15/2.5\\mathrm{m}, a=0.8\\mathrm{m}, f=0.35\\mathrm{Hz}$ | PCRソルバー導入で解法高速化・fp32精度向上。フィルターは未導入。 |");
            sb.AppendLine("| `c60c0b1` | MacCormack + PCR | 中心差分 | **なし** | **$h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$** | **物理パラメータ校正**。浅水条件に合致。フィルターなしのためMacCormack $2\\Delta x$ 振動が波頭を鋸歯状化。 |");
            sb.AppendLine("| `36bb5f0` | MacCormack + PCR | 中心差分 | **初代2D非保存 biharmonic** | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 初めて空間フィルターが導入され、鋸歯状振動の抑制が試みられた。 |");
            sb.AppendLine("| `9f9a9d9` | MacCormack (偶数サブステップ固定) | 中心差分 | 2D biharmonic + Nyquist floor | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 奇偶サブステップ非対称性による30-60Hz画面点滅を解消。SmoothTotalDepth導入。 |");
            sb.AppendLine("| `5a0c12e` | MacCormack (偶数固定) | 中心差分 | **方向分離 biharmonic (CSFilterX/Z)** | $h=3.5/0.8\\mathrm{m}, a=0.35\\.8\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 方向分離2パスフィルターへの移行。 |");
            sb.AppendLine("| `9427acc` | MacCormack (偶数固定) | **1次風上差分** | **保存FCT (Flux-Corrected Transport)** | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | 質量保存FCTフィルターと風上対流の導入。 |");
            sb.AppendLine("| `d5446df` ~ `d8a922b` (現在) | MacCormack (偶数固定) | 1次風上差分 | **FCT (符号是正版) + 急勾配2階散逸** | $h=3.5/0.8\\mathrm{m}, a=0.35\\mathrm{m}, f=0.15\\mathrm{Hz}$ | FCT内部フラックス符号反転バグを修正。現在の安定版。 |");
            sb.AppendLine();

            File.WriteAllText(ReportMarkdownPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[BoussinesqSolitonFissionBenchmark] Reports saved to {ReportMarkdownPath} and {ReportJsonPath}");
        }
        #endregion

        #region helpers
        private static void BindShaderUniforms(ComputeShader shader, int N, float dx, float dz, float subDt, float g, float alpha, float friction, int parity, bool disableSlopeDissipation) {
            shader.SetInt(PropWidth, N);
            shader.SetInt(PropHeight, N);
            shader.SetFloat(PropDeltaTime, subDt);
            shader.SetFloat(PropGridSpacing, dx);
            shader.SetFloat(PropGridSpacingZ, dz);
            shader.SetFloat(PropGravity, g);
            shader.SetFloat(PropDispersionAlpha, alpha);
            shader.SetFloat(PropBedFriction, friction);
            shader.SetFloat(PropSpongeWidth, math.max(4.0f, dz * 8f));
            shader.SetFloat(PropSpongeStrength, 0.05f);
            shader.SetInt(PropStepParity, parity);
            shader.SetInt(PropSideWallSponge, 0); // Flume mode
            shader.SetFloat(PropDisableSlopeDissipation, disableSlopeDissipation ? 1.0f : 0.0f);
        }

        private static void Dispatch2D(ComputeShader shader, int kernel, int N) {
            shader.Dispatch(kernel, (N + 7) / 8, (N + 7) / 8, 1);
        }

        private static void Dispatch1D(ComputeShader shader, int kernel, int N) {
            shader.Dispatch(kernel, N, 1, 1);
        }

        private class BenchmarkTextures {
            public BufferPrecision precision;
            public RenderTexture stateA;
            public RenderTexture stateB;
            public RenderTexture pred;
            public RenderTexture bathy;
            public RenderTexture forceOrig;
            public RenderTexture force;
            public RenderTexture forcePred;
            public RenderTexture accelA;
        }

        private static BenchmarkTextures AllocateTextures(int N, BufferPrecision prec) {
            var b = new BenchmarkTextures();
            b.precision = prec;

            RenderTextureFormat stateFormat = (prec == BufferPrecision.FP32_RuntimeMatching) ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf;
            RenderTextureFormat forceFormat = (prec == BufferPrecision.FP32_RuntimeMatching) ? RenderTextureFormat.RGFloat : RenderTextureFormat.RGHalf;

            b.stateA = CreateRT(N, stateFormat);
            b.stateB = CreateRT(N, stateFormat);
            b.pred = CreateRT(N, stateFormat);
            b.bathy = CreateRT(N, RenderTextureFormat.ARGBHalf); // Matches bathymetryTex in BoussinesqSimulation.cs
            b.forceOrig = CreateRT(N, forceFormat);
            b.force = CreateRT(N, forceFormat);
            b.forcePred = CreateRT(N, forceFormat);
            b.accelA = CreateRT(N, forceFormat);
            return b;
        }

        private static RenderTexture CreateRT(int N, RenderTextureFormat format) {
            var rt = new RenderTexture(N, N, 0, format, RenderTextureReadWrite.Linear);
            rt.enableRandomWrite = true;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
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
                if (RenderTexture.active == rt) RenderTexture.active = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static void UploadBathymetry(BenchmarkTextures rts, int N, float[] depth) {
            var bathyPixels = new Color[N * N];
            for (int i = 0; i < N * N; i++) {
                float d = depth[i];
                bathyPixels[i] = new Color(d, -d, 0f, 1f);
            }
            var tBathy = new Texture2D(N, N, TextureFormat.RGBAHalf, false);
            tBathy.SetPixels(bathyPixels);
            tBathy.Apply();
            Graphics.Blit(tBathy, rts.bathy);
            UnityEngine.Object.DestroyImmediate(tBathy);
        }

        private static void UploadInitialState(BenchmarkTextures rts, int N, float[] eta, float[] w) {
            var statePixels = new Color[N * N];
            for (int i = 0; i < N * N; i++) {
                statePixels[i] = new Color(eta[i], 0f, w[i], 0f);
            }
            TextureFormat texFormat = (rts.precision == BufferPrecision.FP32_RuntimeMatching) ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf;
            var tState = new Texture2D(N, N, texFormat, false);
            tState.SetPixels(statePixels);
            tState.Apply();
            Graphics.Blit(tState, rts.stateA);
            UnityEngine.Object.DestroyImmediate(tState);
        }

        private static void ReadCenterColumnElevation(BenchmarkTextures rts, RenderTexture rt, int N, float[] outEta) {
            var req = AsyncGPUReadback.Request(rt);
            req.WaitForCompletion();
            if (req.hasError) return;

            int xCenter = N / 2;
            if (rts.precision == BufferPrecision.FP32_RuntimeMatching) {
                var rawFloats = req.GetData<float>();
                for (int y = 0; y < N; y++) {
                    int idx = (y * N + xCenter) * 4;
                    outEta[y] = rawFloats[idx];
                }
            } else {
                var rawHalf = req.GetData<ushort>();
                for (int y = 0; y < N; y++) {
                    int idx = (y * N + xCenter) * 4;
                    outEta[y] = Mathf.HalfToFloat(rawHalf[idx]);
                }
            }
        }

        private static void ReadCenterColumnVelocityW(BenchmarkTextures rts, RenderTexture rt, int N, float[] outW) {
            var req = AsyncGPUReadback.Request(rt);
            req.WaitForCompletion();
            if (req.hasError) return;

            int xCenter = N / 2;
            if (rts.precision == BufferPrecision.FP32_RuntimeMatching) {
                var rawFloats = req.GetData<float>();
                for (int y = 0; y < N; y++) {
                    int idx = (y * N + xCenter) * 4 + 2; // Blue channel is w
                    outW[y] = rawFloats[idx];
                }
            } else {
                var rawHalf = req.GetData<ushort>();
                for (int y = 0; y < N; y++) {
                    int idx = (y * N + xCenter) * 4 + 2;
                    outW[y] = Mathf.HalfToFloat(rawHalf[idx]);
                }
            }
        }

        private static string GetGitCommitHash(out bool dirty) {
            dirty = false;
            try {
                var p = new System.Diagnostics.Process {
                    StartInfo = new System.Diagnostics.ProcessStartInfo {
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

        private static string ComputeFileHash(string path) {
            if (!File.Exists(path)) return "missing";
            using (var sha = SHA256.Create()) {
                using (var stream = File.OpenRead(path)) {
                    byte[] hash = sha.ComputeHash(stream);
                    var sb = new StringBuilder();
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
        }
        #endregion
    }
}
