using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Executes rigorous 2D GPU simulation diagnostics on Scene 02 (Shoal and Cape bathymetry).
    /// Tracks the continuous 2D wave crest as it refracts, shoals, and steepens over the submerged ridge and cape.
    /// Records at frame and substep level:
    /// - Continuous 2D crest coordinates (x, y_crest(x)) across the whole domain width
    /// - Wave crest height profile along the crest curve
    /// - Separation of smooth macroscopic profile from high-frequency alternating corrugations (2dx sawtooth mode)
    /// - Cross-crest 2dy profile along the propagation direction
    /// - Peak height time series, substep jump, temporal oscillation, and correlation with MacCormack parity
    /// - Local depth, total depth, and local CFL
    /// </summary>
    public static class ShoalDiagnosticProbe {
        private const string ScenePath = "Assets/Scenes/02_Boussinesq.unity";
        private const string OutputJsonPath = "Docs/ShoalInvestigation/UnityGpuShoalMetrics_Fixed.json";
        private const string CurrentCommitId = "191a8d1";
        private const string ShaderIdentifier = "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute";
        private const string TriggerFile = "Temp/run_shoal_diagnostic.trigger";
        private const string DoneMarker = "Temp/shoal_diagnostic_done.marker";

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
                    Debug.Log("[ShoalDiagnosticProbe] Trigger detected, executing GPU diagnostics...");
                    RunDiagnostic();
                    File.WriteAllText(DoneMarker, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                } catch (Exception ex) {
                    Debug.LogError($"[ShoalDiagnosticProbe] Trigger execution error: {ex}");
                    File.WriteAllText(DoneMarker, "ERROR: " + ex.ToString(), Encoding.UTF8);
                }
            }
        }

        [MenuItem("Tools/Water Simulation/Run Shoal GPU Diagnostics", false, 52)]
        public static void RunDiagnostic() {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var boussSim = UnityEngine.Object.FindFirstObjectByType<BoussinesqSimulation>();
            if (boussSim == null) {
                Debug.LogError("[ShoalDiagnosticProbe] BoussinesqSimulation component not found in Scene 02.");
                return;
            }

            boussSim.ResetSimulation();

            int W = boussSim.Resolution.x;
            int H = boussSim.Resolution.y;
            float dx = boussSim.AreaSize.x / W;
            float dz = boussSim.AreaSize.y / H;
            float g = 9.81f;

            Debug.Log($"[ShoalDiagnosticProbe] Initialized Scene 02: {W}x{H}, dx={dx:F4}m, dz={dz:F4}m, Amp={boussSim.WavemakerAmplitude}m");

            // Read bathymetry once
            var bathyRT = boussSim.BathymetryTexture;
            var bathyReq = AsyncGPUReadback.Request(bathyRT, 0, TextureFormat.RGBAHalf);
            bathyReq.WaitForCompletion();
            var bathyData = bathyReq.GetData<half4>();
            float[] depthGrid = new float[W * H];
            for (int i = 0; i < W * H; i++) depthGrid[i] = (float)bathyData[i].x;

            float dt = 0.016f;
            float totalTime = 20.0f; // Track wave through shoal and shallow cape
            int totalFrames = Mathf.RoundToInt(totalTime / dt);

            var frameRecords = new List<FrameRecord>();
            var substepRecords = new List<SubstepRecord>();

            // Initialize 2D crest tracker across all columns x in [0, W-1]
            int[] crestY = new int[W];
            float[] crestEta = new float[W];

            // Sample initial solitary wave crest around y = 51 (y_norm = 0.1)
            var initStateRT = boussSim.CurrentStateTexture;
            var initReq = AsyncGPUReadback.Request(initStateRT, 0, TextureFormat.RGBAFloat);
            initReq.WaitForCompletion();
            var initData = initReq.GetData<float4>();

            for (int x = 0; x < W; x++) {
                int bestY = (int)(0.10f * H);
                float bestVal = -999f;
                for (int y = math.max(0, bestY - 15); y <= math.min(H - 1, bestY + 25); y++) {
                    float v = initData[y * W + x].x;
                    if (v > bestVal) {
                        bestVal = v;
                        bestY = y;
                    }
                }
                crestY[x] = bestY;
                crestEta[x] = bestVal;
            }

            float maxFramePeakJump = 0f;
            float maxSubstepPeakJump = 0f;
            float maxCrestCorrugation = 0f;
            float maxCrestSawtoothD2 = 0f;
            float maxCrossCrestSawtoothD2 = 0f;
            float maxCapeSawtoothD2 = 0f;
            float maxCapePeakJump = 0f;
            float prevCapePeak = 0f;
            float maxCfl = 0f;
            int depthClampActivationCount = 0;
            int hardClampActivationCount = 0;

            float prevFramePeak = 0f;
            float prevSubstepPeak = 0f;
            float prevPrevSubstepPeak = 0f;
            int paritySignFlips = 0;
            int totalSubstepCompares = 0;

            // Cape region bounds in grid index:
            // Cape center is at (0.72, 0.62) -> x around 368, y around 317
            int capeMinX = (int)(0.60f * W);
            int capeMaxX = (int)(0.85f * W);
            int capeMinY = (int)(0.50f * H);
            int capeMaxY = (int)(0.75f * H);

            for (int frame = 0; frame < totalFrames; frame++) {
                float timeSec = frame * dt;

                float ampBound = math.max(0.1f, boussSim.WavemakerAmplitude * 1.5f);
                float hMax = math.max(0.5f, boussSim.DeepWaterDepth + ampBound);
                float cMax = math.sqrt(g * hMax);
                float uBound = cMax * (ampBound / math.max(0.5f, boussSim.ShallowWaterDepth));
                float maxWaveSpeed = (cMax + uBound) * math.SQRT2;
                float maxDt = (math.min(dx, dz) / maxWaveSpeed) * 0.55f;
                int subSteps = math.max(1, (int)math.ceil(dt / maxDt));
                if ((subSteps & 1) != 0) subSteps++;
                float subDt = dt / subSteps;

                // Track at substep resolution during shoal / cape passage (t = 4.0s .. 8.5s)
                bool recordSubsteps = (timeSec >= 4.0f && timeSec <= 8.5f);

                for (int s = 0; s < subSteps; s++) {
                    int parityBefore = boussSim.CurrentPingPong ? 1 : 0;
                    boussSim.StepSingleSubstep(subDt);

                    if (recordSubsteps) {
                        var stateRT = boussSim.CurrentStateTexture;
                        var req = AsyncGPUReadback.Request(stateRT, 0, TextureFormat.RGBAFloat);
                        req.WaitForCompletion();
                        var data = req.GetData<float4>();

                        // Track 2D wave crest with forward spatial continuity
                        float substepMaxPeak = -999f;
                        int substepPeakX = -1;
                        int substepPeakY = -1;
                        float substepPeakU = 0f, substepPeakW = 0f;
                        float substepPeakDepth = 0f;

                        for (int x = 0; x < W; x++) {
                            int prevY = crestY[x];
                            // Search in window [prevY - 1, prevY + 6]
                            int minY = math.max(0, prevY - 1);
                            int maxY = math.min(H - 1, prevY + 6);
                            int bestY = prevY;
                            float bestVal = -999f;
                            for (int y = minY; y <= maxY; y++) {
                                float val = data[y * W + x].x;
                                if (val > bestVal) {
                                    bestVal = val;
                                    bestY = y;
                                }
                            }
                            crestY[x] = bestY;
                            crestEta[x] = bestVal;

                            if (bestVal > substepMaxPeak) {
                                substepMaxPeak = bestVal;
                                substepPeakX = x;
                                substepPeakY = bestY;
                                substepPeakU = data[bestY * W + x].y;
                                substepPeakW = data[bestY * W + x].z;
                                substepPeakDepth = depthGrid[bestY * W + x];
                            }
                        }

                        // Along-crest sawtooth metric (2dx mode)
                        float crestSawtoothMax = 0f;
                        for (int x = 2; x < W - 2; x++) {
                            float d2 = math.abs(crestEta[x + 1] - 2f * crestEta[x] + crestEta[x - 1]);
                            if (d2 > crestSawtoothMax) crestSawtoothMax = d2;
                        }

                        // Substep peak height jump and oscillation
                        float substepJump = (prevSubstepPeak > 0f) ? math.abs(substepMaxPeak - prevSubstepPeak) : 0f;
                        if (substepJump > maxSubstepPeakJump && frame > 5) maxSubstepPeakJump = substepJump;

                        // Check if peak delta alternates sign with parity
                        if (prevSubstepPeak > 0f && prevPrevSubstepPeak > 0f) {
                            float delta1 = substepMaxPeak - prevSubstepPeak;
                            float delta0 = prevSubstepPeak - prevPrevSubstepPeak;
                            if (delta1 * delta0 < 0f) paritySignFlips++;
                            totalSubstepCompares++;
                        }
                        prevPrevSubstepPeak = prevSubstepPeak;
                        prevSubstepPeak = substepMaxPeak;

                        float c = math.sqrt(g * math.max(0.01f, substepPeakDepth + substepMaxPeak));
                        float vel = math.sqrt(substepPeakU * substepPeakU + substepPeakW * substepPeakW);
                        float cfl = (c + vel) * subDt / math.min(dx, dz);

                        substepRecords.Add(new SubstepRecord {
                            frame = frame,
                            substep = s,
                            time = timeSec + (s + 1) * subDt,
                            parity = parityBefore,
                            direction = (parityBefore == 0) ? 1 : -1,
                            peak_x = substepPeakX,
                            peak_y = substepPeakY,
                            peak_eta = substepMaxPeak,
                            peak_u = substepPeakU,
                            peak_w = substepPeakW,
                            peak_depth = substepPeakDepth,
                            total_depth = substepPeakDepth + substepMaxPeak,
                            cfl = cfl,
                            crest_sawtooth_d2 = crestSawtoothMax,
                            substep_jump = substepJump
                        });
                    }
                }

                boussSim.FinalizeFrameOutput();

                // Frame-level full 2D scan and crest decomposition
                var frameStateRT = boussSim.CurrentStateTexture;
                var frameReq = AsyncGPUReadback.Request(frameStateRT, 0, TextureFormat.RGBAFloat);
                frameReq.WaitForCompletion();
                var frameData = frameReq.GetData<float4>();

                // Track continuous wave crest across entire width
                float fPeakEta = -999f;
                int fPeakX = -1;
                int fPeakY = -1;
                float fPeakU = 0f, fPeakW = 0f;
                float fPeakDepth = 0f;

                for (int x = 0; x < W; x++) {
                    int prevY = crestY[x];
                    int minY = math.max(0, prevY - 2);
                    int maxY = math.min(H - 1, prevY + 8);
                    int bestY = prevY;
                    float bestVal = -999f;
                    for (int y = minY; y <= maxY; y++) {
                        float val = frameData[y * W + x].x;
                        if (val > bestVal) {
                            bestVal = val;
                            bestY = y;
                        }
                    }
                    crestY[x] = bestY;
                    crestEta[x] = bestVal;

                    if (bestVal > fPeakEta) {
                        fPeakEta = bestVal;
                        fPeakX = x;
                        fPeakY = bestY;
                        fPeakU = frameData[bestY * W + x].y;
                        fPeakW = frameData[bestY * W + x].z;
                        fPeakDepth = depthGrid[bestY * W + x];
                    }
                }

                // Decompose crest into smooth profile (9-point moving avg) and high-frequency corrugations
                float frameMaxCorrugation = 0f;
                float frameMaxCrestSawtooth = 0f;
                int zigzagCount = 0;
                for (int x = 4; x < W - 4; x++) {
                    float sum = 0f;
                    for (int k = -4; k <= 4; k++) sum += crestEta[x + k];
                    float smoothEta = sum / 9f;
                    float corrugation = math.abs(crestEta[x] - smoothEta);
                    if (corrugation > frameMaxCorrugation) frameMaxCorrugation = corrugation;

                    float d2 = math.abs(crestEta[x + 1] - 2f * crestEta[x] + crestEta[x - 1]);
                    if (d2 > frameMaxCrestSawtooth) frameMaxCrestSawtooth = d2;

                    float slopeL = crestEta[x] - crestEta[x - 1];
                    float slopeR = crestEta[x + 1] - crestEta[x];
                    if (slopeL * slopeR < -1e-6f) zigzagCount++;
                }

                // Cross-crest profile along propagation direction Y at peakX
                float crossCrestSawtooth = 0f;
                if (fPeakX >= 0 && fPeakY >= 1 && fPeakY < H - 1) {
                    crossCrestSawtooth = math.abs(frameData[(fPeakY + 1) * W + fPeakX].x - 2f * fPeakEta + frameData[(fPeakY - 1) * W + fPeakX].x);
                }

                float fJump = (prevFramePeak > 0f) ? math.abs(fPeakEta - prevFramePeak) : 0f;
                if (fJump > maxFramePeakJump && frame > 5) maxFramePeakJump = fJump;
                prevFramePeak = fPeakEta;

                if (frameMaxCorrugation > maxCrestCorrugation) maxCrestCorrugation = frameMaxCorrugation;
                if (frameMaxCrestSawtooth > maxCrestSawtoothD2) maxCrestSawtoothD2 = frameMaxCrestSawtooth;
                if (crossCrestSawtooth > maxCrossCrestSawtoothD2) maxCrossCrestSawtoothD2 = crossCrestSawtooth;

                // Check domain-wide depth clamp and CFL
                float frameMaxCfl = 0f;
                for (int i = 0; i < W * H; i++) {
                    float totH = depthGrid[i] + frameData[i].x;
                    if (totH <= 0.051f) depthClampActivationCount++;
                    if (math.abs(frameData[i].x) >= 14.9f || math.abs(frameData[i].y) >= 29.9f || math.abs(frameData[i].z) >= 29.9f) {
                        hardClampActivationCount++;
                    }
                    float c = math.sqrt(g * math.max(0.01f, totH));
                    float vel = math.sqrt(frameData[i].y * frameData[i].y + frameData[i].z * frameData[i].z);
                    float localCfl = (c + vel) * subDt / math.min(dx, dz);
                    if (localCfl > frameMaxCfl) frameMaxCfl = localCfl;
                }
                if (frameMaxCfl > maxCfl) maxCfl = frameMaxCfl;

                // Sample crest profile and 2D coordinates at key shoaling time (t = 6.0s)
                List<float> crestProfile = null;
                List<int> crestYCoords = null;
                if (frame == Mathf.RoundToInt(6.0f / dt)) {
                    crestProfile = new List<float>(crestEta);
                    crestYCoords = new List<int>(crestY);
                }

                if (frame % 5 == 0 || frame == totalFrames - 1) {
                    frameRecords.Add(new FrameRecord {
                        frame = frame,
                        time = timeSec,
                        substeps = subSteps,
                        peak_x = fPeakX,
                        peak_y = fPeakY,
                        peak_eta = fPeakEta,
                        peak_u = fPeakU,
                        peak_w = fPeakW,
                        peak_depth = fPeakDepth,
                        total_depth = fPeakDepth + fPeakEta,
                        max_cfl = frameMaxCfl,
                        crest_corrugation = frameMaxCorrugation,
                        crest_sawtooth_d2 = frameMaxCrestSawtooth,
                        cross_crest_sawtooth_d2 = crossCrestSawtooth,
                        zigzag_points = zigzagCount,
                        frame_jump = fJump,
                        crest_profile = crestProfile,
                        crest_y_coords = crestYCoords
                    });
                }
            }

            float parityFlipRatio = totalSubstepCompares > 0 ? (float)paritySignFlips / totalSubstepCompares : 0f;

            var outObj = new DiagnosticOutput {
                commit = CurrentCommitId,
                shader_id = ShaderIdentifier,
                resolution = W,
                dt = dt,
                area_size = boussSim.AreaSize.x,
                wavemaker_amp = boussSim.WavemakerAmplitude,
                wavemaker_freq = boussSim.WavemakerFrequency,
                deep_depth = boussSim.DeepWaterDepth,
                shallow_depth = boussSim.ShallowWaterDepth,
                summary = new DiagnosticSummary {
                    max_frame_peak_jump = maxFramePeakJump,
                    max_substep_peak_jump = maxSubstepPeakJump,
                    max_crest_corrugation = maxCrestCorrugation,
                    max_crest_sawtooth_d2 = maxCrestSawtoothD2,
                    max_cross_crest_sawtooth_d2 = maxCrossCrestSawtoothD2,
                    parity_sign_flip_ratio = parityFlipRatio,
                    max_cfl = maxCfl,
                    depth_clamp_activations = depthClampActivationCount,
                    hard_clamp_activations = hardClampActivationCount
                },
                frames = frameRecords,
                substeps = substepRecords
            };

            string jsonStr = JsonUtility.ToJson(outObj, true);
            string outDir = Path.GetDirectoryName(OutputJsonPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(OutputJsonPath, jsonStr, Encoding.UTF8);

            Debug.Log($"[ShoalDiagnosticProbe] 2D Wave Crest Tracking GPU Diagnostic Complete!\n" +
                      $"  Commit: {CurrentCommitId}\n" +
                      $"  Shader: {ShaderIdentifier}\n" +
                      $"  Max Frame Peak Jump: {maxFramePeakJump:F5} m\n" +
                      $"  Max Substep Peak Jump: {maxSubstepPeakJump:F5} m\n" +
                      $"  Max Along-Crest Corrugation: {maxCrestCorrugation:F6} m\n" +
                      $"  Max Along-Crest Sawtooth d2: {maxCrestSawtoothD2:F6}\n" +
                      $"  Max Cross-Crest Sawtooth d2: {maxCrossCrestSawtoothD2:F6}\n" +
                      $"  Parity Sign Flip Ratio: {parityFlipRatio * 100f:F1}%\n" +
                      $"  Max CFL: {maxCfl:F4}\n" +
                      $"  Depth Clamp Activations: {depthClampActivationCount}\n" +
                      $"Saved report to: {OutputJsonPath}");
        }

        public static void RunCommandLine() {
            try {
                RunDiagnostic();
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                Debug.LogError($"[ShoalDiagnosticProbe] Fatal error: {ex}");
                EditorApplication.Exit(1);
            }
        }

        #region nested types
        [Serializable]
        public class DiagnosticSummary {
            public float max_frame_peak_jump;
            public float max_substep_peak_jump;
            public float max_crest_corrugation;
            public float max_crest_sawtooth_d2;
            public float max_cross_crest_sawtooth_d2;
            public float parity_sign_flip_ratio;
            public float max_cfl;
            public int depth_clamp_activations;
            public int hard_clamp_activations;
        }

        [Serializable]
        public class FrameRecord {
            public int frame;
            public float time;
            public int substeps;
            public int peak_x;
            public int peak_y;
            public float peak_eta;
            public float peak_u;
            public float peak_w;
            public float peak_depth;
            public float total_depth;
            public float max_cfl;
            public float crest_corrugation;
            public float crest_sawtooth_d2;
            public float cross_crest_sawtooth_d2;
            public int zigzag_points;
            public float frame_jump;
            public List<float> crest_profile;
            public List<int> crest_y_coords;
        }

        [Serializable]
        public class SubstepRecord {
            public int frame;
            public int substep;
            public float time;
            public int parity;
            public int direction;
            public int peak_x;
            public int peak_y;
            public float peak_eta;
            public float peak_u;
            public float peak_w;
            public float peak_depth;
            public float total_depth;
            public float cfl;
            public float crest_sawtooth_d2;
            public float substep_jump;
        }

        [Serializable]
        public class DiagnosticOutput {
            public string commit;
            public string shader_id;
            public int resolution;
            public float dt;
            public float area_size;
            public float wavemaker_amp;
            public float wavemaker_freq;
            public float deep_depth;
            public float shallow_depth;
            public DiagnosticSummary summary;
            public List<FrameRecord> frames;
            public List<SubstepRecord> substeps;
        }
        #endregion
    }
}
