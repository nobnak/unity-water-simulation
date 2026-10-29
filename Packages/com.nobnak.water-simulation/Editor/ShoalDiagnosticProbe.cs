using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Executes GPU simulation diagnostics directly inside Unity for Scene 02 (Shoal bathymetry).
    /// Records substep-by-substep and frame-by-frame:
    /// - Wave crest height profile along the shoal
    /// - Peak height time series and rapid oscillation jump
    /// - Substep elevation, velocity, parity, and actual difference direction
    /// - Local depth, total depth, and minimum depth checks
    /// - Local CFL index across the domain
    /// - Clamp activation counts
    /// </summary>
    public static class ShoalDiagnosticProbe {
        private const string ScenePath = "Assets/Scenes/02_Boussinesq.unity";
        private const string OutputJsonPath = "Docs/ShoalInvestigation/UnityGpuShoalMetrics_Fixed.json";
        private const string TriggerFile = "Temp/run_shoal_diagnostic.trigger";
        private const string DoneMarker = "Temp/shoal_diagnostic_done.marker";

        [InitializeOnLoadMethod]
        private static void OnEditorInit() {
            EditorApplication.delayCall += () => {
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
            };
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

            Debug.Log($"[ShoalDiagnosticProbe] Initialized Scene 02: {W}x{H}, dx={dx:F4}m, dz={dz:F4}m, Condition={boussSim.Condition}, Amp={boussSim.WavemakerAmplitude}m");

            // Read bathymetry once
            var bathyRT = boussSim.BathymetryTexture;
            var bathyReq = AsyncGPUReadback.Request(bathyRT, 0, TextureFormat.RGBAHalf);
            bathyReq.WaitForCompletion();
            var bathyData = bathyReq.GetData<half4>();
            float[] depthGrid = new float[W * H];
            for (int i = 0; i < W * H; i++) {
                depthGrid[i] = (float)bathyData[i].x;
            }

            float dt = 0.016f;
            float totalTime = 12.0f; // 12 seconds
            int totalFrames = Mathf.RoundToInt(totalTime / dt);

            var frameRecords = new List<FrameRecord>();
            var substepRecords = new List<SubstepRecord>();

            float maxFramePeakJump = 0f;
            float maxSubstepPeakJump = 0f;
            float maxWavefrontSawtooth = 0f;
            float maxShoalSawtooth = 0f;
            float maxCfl = 0f;
            int depthClampActivationCount = 0;
            int hardClampActivationCount = 0;

            float prevFramePeak = 0f;
            float prevSubstepPeak = 0f;

            int cx = W / 2;

            for (int frame = 0; frame < totalFrames; frame++) {
                float timeSec = frame * dt;

                // Calculate substeps exactly as in BoussinesqSimulation.cs
                float ampBound = math.max(0.1f, boussSim.WavemakerAmplitude * 1.5f);
                float hMax = math.max(0.5f, boussSim.DeepWaterDepth + ampBound);
                float cMax = math.sqrt(g * hMax);
                float uBound = cMax * (ampBound / math.max(0.5f, boussSim.ShallowWaterDepth));
                float maxWaveSpeed = (cMax + uBound) * math.SQRT2;
                float maxDt = (math.min(dx, dz) / maxWaveSpeed) * 0.55f;
                int subSteps = math.max(1, (int)math.ceil(dt / maxDt));
                if ((subSteps & 1) != 0) subSteps++;
                float subDt = dt / subSteps;

                // Detailed substep profiling during wave passage over shoal (t = 8s..11s)
                bool recordSubsteps = (timeSec >= 8.0f && timeSec <= 10.5f);

                for (int s = 0; s < subSteps; s++) {
                    int parityBefore = boussSim.CurrentPingPong ? 1 : 0;
                    boussSim.StepSingleSubstep(subDt);

                    if (recordSubsteps) {
                        var stateRT = boussSim.CurrentStateTexture;
                        var req = AsyncGPUReadback.Request(stateRT, 0, TextureFormat.RGBAFloat);
                        req.WaitForCompletion();
                        var data = req.GetData<float4>();

                        // Find wave crest in central shoal region (y in [0.35..0.65])
                        int peakY = -1;
                        float peakEta = -999f;
                        float peakU = 0f, peakW = 0f;
                        float peakH = 0f;

                        for (int y = (int)(0.30f * H); y < (int)(0.70f * H); y++) {
                            float val = data[y * W + cx].x;
                            if (val > peakEta) {
                                peakEta = val;
                                peakY = y;
                                peakU = data[y * W + cx].y;
                                peakW = data[y * W + cx].z;
                                peakH = depthGrid[y * W + cx];
                            }
                        }

                        float substepJump = (prevSubstepPeak > 0f) ? math.abs(peakEta - prevSubstepPeak) : 0f;
                        if (substepJump > maxSubstepPeakJump && frame > 10) maxSubstepPeakJump = substepJump;
                        prevSubstepPeak = peakEta;

                        // Crest sawtooth along X at peakY
                        float localSawtooth = 0f;
                        if (peakY >= 0) {
                            int row = peakY * W;
                            for (int x = 1; x < W - 1; x++) {
                                float d2 = math.abs(data[row + x + 1].x - 2f * data[row + x].x + data[row + x - 1].x);
                                if (d2 > localSawtooth) localSawtooth = d2;
                            }
                        }

                        // Local CFL
                        float c = math.sqrt(g * math.max(0.01f, peakH + peakEta));
                        float vel = math.sqrt(peakU * peakU + peakW * peakW);
                        float cfl = (c + vel) * subDt / math.min(dx, dz);

                        substepRecords.Add(new SubstepRecord {
                            frame = frame,
                            substep = s,
                            time = timeSec + (s + 1) * subDt,
                            parity = parityBefore,
                            direction = (parityBefore == 0) ? 1 : -1,
                            peak_y = peakY,
                            peak_eta = peakEta,
                            peak_u = peakU,
                            peak_w = peakW,
                            peak_depth = peakH,
                            total_depth = peakH + peakEta,
                            cfl = cfl,
                            sawtooth_d2 = localSawtooth,
                            substep_jump = substepJump
                        });
                    }
                }

                boussSim.FinalizeFrameOutput();

                // Readback state at end of frame
                var frameStateRT = boussSim.CurrentStateTexture;
                var frameReq = AsyncGPUReadback.Request(frameStateRT, 0, TextureFormat.RGBAFloat);
                frameReq.WaitForCompletion();
                var frameData = frameReq.GetData<float4>();

                // Scan entire domain for peak, CFL, sawtooth, and clamp checks
                int fPeakY = -1;
                float fPeakEta = -999f;
                float fPeakU = 0f, fPeakW = 0f;
                float fPeakDepth = 0f;
                float frameMaxSawtooth = 0f;
                int frameMaxSawtoothX = -1;
                int frameMaxSawtoothY = -1;
                float frameMaxCfl = 0f;
                float shoalMaxSawtooth = 0f;
                int shoalMaxSawtoothX = -1;
                int shoalMaxSawtoothY = -1;

                for (int y = 0; y < H; y++) {
                    int row = y * W;
                    for (int x = 0; x < W; x++) {
                        int idx = row + x;
                        float eta = frameData[idx].x;
                        float u = frameData[idx].y;
                        float w = frameData[idx].z;
                        float h = depthGrid[idx];
                        float totalH = h + eta;

                        if (totalH <= 0.051f) depthClampActivationCount++;
                        if (math.abs(eta) >= 14.9f || math.abs(u) >= 29.9f || math.abs(w) >= 29.9f) {
                            hardClampActivationCount++;
                        }

                        float c = math.sqrt(g * math.max(0.01f, totalH));
                        float vel = math.sqrt(u * u + w * w);
                        float localCfl = (c + vel) * subDt / math.min(dx, dz);
                        if (localCfl > frameMaxCfl) frameMaxCfl = localCfl;

                        if (x == cx && y >= (int)(0.25f * H) && y <= (int)(0.75f * H)) {
                            if (eta > fPeakEta) {
                                fPeakEta = eta;
                                fPeakY = y;
                                fPeakU = u;
                                fPeakW = w;
                                fPeakDepth = h;
                            }
                        }

                        if (x > 0 && x < W - 1) {
                            float d2 = math.abs(frameData[row + x + 1].x - 2f * eta + frameData[row + x - 1].x);
                            if (d2 > frameMaxSawtooth) {
                                frameMaxSawtooth = d2;
                                frameMaxSawtoothX = x;
                                frameMaxSawtoothY = y;
                            }
                            if (y >= (int)(0.35f * H) && y <= (int)(0.65f * H)) {
                                if (d2 > shoalMaxSawtooth) {
                                    shoalMaxSawtooth = d2;
                                    shoalMaxSawtoothX = x;
                                    shoalMaxSawtoothY = y;
                                }
                            }
                        }
                    }
                }

                if (frameMaxCfl > maxCfl) maxCfl = frameMaxCfl;
                if (frameMaxSawtooth > maxWavefrontSawtooth) maxWavefrontSawtooth = frameMaxSawtooth;
                if (shoalMaxSawtooth > maxShoalSawtooth) maxShoalSawtooth = shoalMaxSawtooth;

                float fJump = (prevFramePeak > 0f && fPeakY >= (int)(0.35f * H) && fPeakY <= (int)(0.65f * H))
                    ? math.abs(fPeakEta - prevFramePeak) : 0f;
                if (fJump > maxFramePeakJump && frame > 10) maxFramePeakJump = fJump;
                prevFramePeak = fPeakEta;

                // Sample crest line profile at t = 10s
                List<float> crestProfile = null;
                if (frame == Mathf.RoundToInt(10.0f / dt) && fPeakY >= 0) {
                    crestProfile = new List<float>(W);
                    int r = fPeakY * W;
                    for (int x = 0; x < W; x++) crestProfile.Add(frameData[r + x].x);
                }

                if (frame % 5 == 0 || frame == totalFrames - 1) {
                    frameRecords.Add(new FrameRecord {
                        frame = frame,
                        time = timeSec,
                        substeps = subSteps,
                        peak_y = fPeakY,
                        peak_eta = fPeakEta,
                        peak_u = fPeakU,
                        peak_w = fPeakW,
                        peak_depth = fPeakDepth,
                        total_depth = fPeakDepth + fPeakEta,
                        max_cfl = frameMaxCfl,
                        sawtooth_d2 = frameMaxSawtooth,
                        sawtooth_x = frameMaxSawtoothX,
                        sawtooth_y = frameMaxSawtoothY,
                        shoal_sawtooth_d2 = shoalMaxSawtooth,
                        shoal_sawtooth_x = shoalMaxSawtoothX,
                        shoal_sawtooth_y = shoalMaxSawtoothY,
                        frame_jump = fJump,
                        crest_profile = crestProfile
                    });
                }
            }

            // Output JSON
            var outObj = new DiagnosticOutput {
                commit = "tvd_dt_fixed",
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
                    max_wavefront_sawtooth = maxWavefrontSawtooth,
                    max_shoal_sawtooth = maxShoalSawtooth,
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

            Debug.Log($"[ShoalDiagnosticProbe] GPU Diagnostic Complete!\n" +
                      $"  Max Frame Peak Jump: {maxFramePeakJump:F5} m\n" +
                      $"  Max Substep Peak Jump: {maxSubstepPeakJump:F5} m\n" +
                      $"  Max Wavefront Sawtooth d2: {maxWavefrontSawtooth:F6}\n" +
                      $"  Max CFL: {maxCfl:F4}\n" +
                      $"  Depth Clamp (H <= 0.05m) Count: {depthClampActivationCount}\n" +
                      $"  Hard Clamp Count: {hardClampActivationCount}\n" +
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

        [Serializable]
        public class DiagnosticSummary {
            public float max_frame_peak_jump;
            public float max_substep_peak_jump;
            public float max_wavefront_sawtooth;
            public float max_shoal_sawtooth;
            public float max_cfl;
            public int depth_clamp_activations;
            public int hard_clamp_activations;
        }

        [Serializable]
        public class FrameRecord {
            public int frame;
            public float time;
            public int substeps;
            public int peak_y;
            public float peak_eta;
            public float peak_u;
            public float peak_w;
            public float peak_depth;
            public float total_depth;
            public float max_cfl;
            public float sawtooth_d2;
            public int sawtooth_x;
            public int sawtooth_y;
            public float shoal_sawtooth_d2;
            public int shoal_sawtooth_x;
            public int shoal_sawtooth_y;
            public float frame_jump;
            public List<float> crest_profile;
        }

        [Serializable]
        public class SubstepRecord {
            public int frame;
            public int substep;
            public float time;
            public int parity;
            public int direction;
            public int peak_y;
            public float peak_eta;
            public float peak_u;
            public float peak_w;
            public float peak_depth;
            public float total_depth;
            public float cfl;
            public float sawtooth_d2;
            public float substep_jump;
        }

        [Serializable]
        public class DiagnosticOutput {
            public string commit;
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
    }
}
