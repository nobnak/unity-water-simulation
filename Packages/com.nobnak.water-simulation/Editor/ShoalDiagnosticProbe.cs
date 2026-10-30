using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Executes rigorous 2D GPU simulation diagnostics on Scene 02 (Shoal and Cape bathymetry).
    /// Tracks the continuous 2D wave crest through shoal and cape regions from entry to exit.
    /// Dynamically records at frame and substep level:
    /// - Continuous primary crest coordinates y_crest(x) and height distribution across domain width
    /// - Separation of global peak, tracked crest peak, and cape local peak
    /// - Detection of crest split, tracking loss, and target switching
    /// - Along-crest corrugation (2dx sawtooth mode d2) and cross-crest profiles
    /// - Fixed probe stations at Cape Summit, Cape Approach, Shoal Ridge, and Deep Water
    /// - Substep peak height jump and MacCormack parity oscillation
    /// - Live Git commit hash, dirty flag, and shader SHA256 content hash
    /// </summary>
    public static class ShoalDiagnosticProbe {
        #region constants / static
        private const string ScenePath = "Assets/Scenes/02_Boussinesq.unity";
        private const string ShaderPath = "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute";
        public static string OutputJsonPath = "Docs/ShoalInvestigation/UnityGpuShoalMetrics_Fixed.json";
        public static string AnimationOutputDir = "Captures/ShoalAnimation";
        private const string TriggerFile = "Temp/run_shoal_diagnostic.trigger";
        private const string DoneMarker = "Temp/shoal_diagnostic_done.marker";
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
                    string customOut = "";
                    if (File.Exists(TriggerFile)) {
                        string text = File.ReadAllText(TriggerFile).Trim();
                        if (!string.IsNullOrEmpty(text)) customOut = text;
                        File.Delete(TriggerFile);
                    }
                    if (File.Exists(DoneMarker)) File.Delete(DoneMarker);
                    Debug.Log($"[ShoalDiagnosticProbe] Trigger detected, executing GPU diagnostics (Out: {customOut})...");
                    RunDiagnostic(customOut);
                    File.WriteAllText(DoneMarker, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                } catch (Exception ex) {
                    Debug.LogError($"[ShoalDiagnosticProbe] Trigger execution error: {ex}");
                    File.WriteAllText(DoneMarker, "ERROR: " + ex.ToString(), Encoding.UTF8);
                }
            }
        }
        #endregion

        #region public interface
        public static void RunBatch() {
            RunDiagnostic();
        }

        public static void RunCommandLine() {
            try {
                string[] args = Environment.GetCommandLineArgs();
                string customOut = "";
                BoussinesqSimulation.FilterType filterType = BoussinesqSimulation.FilterType.DirectionalSplit;
                for (int i = 0; i < args.Length - 1; i++) {
                    if (args[i] == "-diagnosticOutput") customOut = args[i + 1];
                    if (args[i] == "-filterMode" && args[i + 1] == "Legacy") filterType = BoussinesqSimulation.FilterType.LegacyUnsplit2D;
                }
                RunDiagnostic(customOut, filterType);
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                Debug.LogError($"[ShoalDiagnosticProbe] Fatal error: {ex}");
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("Tools/Water Simulation/Run Shoal GPU Diagnostics", false, 52)]
        public static void RunDiagnosticMenu() {
            RunDiagnostic();
        }

        public static void RunDiagnostic(string customOutputPath = "", BoussinesqSimulation.FilterType filterMode = BoussinesqSimulation.FilterType.DirectionalSplit) {
            string actualJsonPath = !string.IsNullOrEmpty(customOutputPath) ? customOutputPath : OutputJsonPath;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var boussSim = UnityEngine.Object.FindFirstObjectByType<BoussinesqSimulation>();
            if (boussSim == null) {
                Debug.LogError("[ShoalDiagnosticProbe] BoussinesqSimulation component not found in Scene 02.");
                return;
            }

            boussSim.Filter = filterMode;
            boussSim.Pause();
            boussSim.ResetSimulation();

            int W = boussSim.Resolution.x;
            int H = boussSim.Resolution.y;
            float dx = boussSim.AreaSize.x / W;
            float dz = boussSim.AreaSize.y / H;
            float g = 9.81f;

            string gitCommit = QueryGitCommit();
            bool isDirty = QueryGitDirty();
            string shaderHash = ComputeShaderSha256(ShaderPath);
            string csSimHash = ComputeShaderSha256("Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs");
            string csProbeHash = ComputeShaderSha256("Packages/com.nobnak.water-simulation/Editor/ShoalDiagnosticProbe.cs");
            string csVerifierHash = ComputeShaderSha256("Packages/com.nobnak.water-simulation/Editor/ShoalGpuCpuVerifier.cs");

            Debug.Log($"[ShoalDiagnosticProbe] Initialized Scene 02: {W}x{H}, dx={dx:F4}m, dz={dz:F4}m\n" +
                      $"  Git: {gitCommit} (Dirty: {isDirty}), Shader SHA: {shaderHash}\n" +
                      $"  Output target: {actualJsonPath}");

            // Read bathymetry once
            var bathyRT = boussSim.BathymetryTexture;
            var bathyReq = AsyncGPUReadback.Request(bathyRT, 0, TextureFormat.RGBAHalf);
            bathyReq.WaitForCompletion();
            var bathyData = bathyReq.GetData<half4>();
            float[] depthGrid = new float[W * H];
            for (int i = 0; i < W * H; i++) depthGrid[i] = (float)bathyData[i].x;

            float dt = 0.016f;
            float totalTime = 20.0f; // Track wave through entry, shoal, cape, and offshore
            int totalFrames = Mathf.RoundToInt(totalTime / dt);

            var frameRecords = new List<FrameRecord>();
            var substepRecords = new List<SubstepRecord>();

            // Region boundaries
            // Cape center is at (nx=0.72, ny=0.62) -> x around 368, y around 317
            int capeMinX = (int)(0.60f * W);
            int capeMaxX = (int)(0.85f * W);
            int capeMinY = (int)(0.50f * H);
            int capeMaxY = (int)(0.75f * H);

            // Shoal ridge region: | (nx - 0.35) - (ny - 0.45)*0.7 | < 0.15
            int shoalMinX = (int)(0.20f * W);
            int shoalMaxX = (int)(0.60f * W);
            int shoalMinY = (int)(0.35f * H);
            int shoalMaxY = (int)(0.70f * H);

            // Fixed probe locations:
            int probeCapeX = (int)(0.72f * W);
            int probeCapeY = (int)(0.62f * H);
            int probeApprX = (int)(0.72f * W);
            int probeApprY = (int)(0.55f * H);
            int probeRidgeX = (int)(0.35f * W);
            int probeRidgeY = (int)(0.49f * H);
            int probeDeepX = W / 2;
            int probeDeepY = (int)(0.20f * H);

            // Primary 2D wave crest tracker
            int[] crestY = new int[W];
            float[] crestEta = new float[W];

            // Initialize crest tracker at initial planar solitary wave (around y = 0.10 * H)
            var initStateRT = boussSim.CurrentStateTexture;
            var initReq = AsyncGPUReadback.Request(initStateRT, 0, TextureFormat.RGBAFloat);
            initReq.WaitForCompletion();
            var initData = initReq.GetData<float4>();

            for (int x = 0; x < W; x++) {
                int initCenterY = (int)(0.10f * H);
                int bestY = initCenterY;
                float bestVal = -999f;
                for (int y = math.max(0, initCenterY - 15); y <= math.min(H - 1, initCenterY + 25); y++) {
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
            float maxCapeSawtoothD2 = 0f;
            float maxCrossCrestSawtoothD2 = 0f;
            float maxCapePeakJump = 0f;
            float maxCfl = 0f;

            float prevFramePeak = 0f;
            float prevSubstepGlobalPeak = 0f;
            float prevPrevSubstepGlobalPeak = 0f;
            float prevSubstepTrackedPeak = 0f;
            float prevPrevSubstepTrackedPeak = 0f;
            float prevSubstepCapePeak = 0f;
            int prevTrackedPeakX = -1;
            int prevTrackedPeakY = -1;

            int globalParityFlips = 0;
            int trackedParityFlips = 0;
            int totalSubstepCompares = 0;

            int totalCrestSplitCount = 0;
            int totalTrackingLostCount = 0;
            int totalTargetSwitchCount = 0;

            string prevTrackState = "ValidTracking";
            int validTrackingSubstepCount = 0;
            int totalRecordedSubstepCount = 0;
            double sumValidColRatio = 0.0;

            // Ensure capture directory exists for video/visual inspection
            if (!Directory.Exists(AnimationOutputDir)) Directory.CreateDirectory(AnimationOutputDir);

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

                // Determine representative crest position
                int midX = W / 2;
                int repCrestY = crestY[midX];

                // Dynamically record substeps from prior to entering shoal (repCrestY >= 120 or t >= 3.5s)
                // until fully traversing cape and shoal (repCrestY >= 430 or t >= 17.5s)
                bool recordSubsteps = (timeSec >= 3.5f && timeSec <= 17.5f) || (repCrestY >= 120 && repCrestY <= 430);

                if (frame % 100 == 0) {
                    Debug.Log($"[ShoalDiagnosticProbe] Frame {frame}/{totalFrames} (t={timeSec:F2}s, repCrestY={repCrestY}, record={recordSubsteps})");
                }

                for (int s = 0; s < subSteps; s++) {
                    int parityBefore = boussSim.CurrentPingPong ? 1 : 0;
                    boussSim.StepSingleSubstep(subDt);

                    if (recordSubsteps) {
                        var stateRT = boussSim.CurrentStateTexture;
                        var req = AsyncGPUReadback.Request(stateRT, 0, TextureFormat.RGBAFloat);
                        req.WaitForCompletion();
                        if (req.hasError) {
                            Debug.LogError($"[ShoalDiagnosticProbe] Readback failed: frame={frame}, substep={s}, time={timeSec:F3}s, repCrestY={repCrestY}");
                            break;
                        }
                        var data = req.GetData<float4>();

                        // 1. Primary crest tracking with strict lateral continuity
                        int prevMidY = crestY[midX];
                        int bestMidY = prevMidY;
                        float bestMidVal = -999f;
                        for (int y = math.max(0, prevMidY - 2); y <= math.min(H - 1, prevMidY + 5); y++) {
                            float val = data[y * W + midX].x;
                            if (val > bestMidVal) {
                                bestMidVal = val;
                                bestMidY = y;
                            }
                        }
                        crestY[midX] = bestMidY;
                        crestEta[midX] = bestMidVal;

                        // Propagate laterally rightward from midX
                        for (int x = midX + 1; x < W; x++) {
                            int refY = crestY[x - 1];
                            int minY = math.max(0, refY - 2);
                            int maxY = math.min(H - 1, refY + 2);
                            int bestY = refY;
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
                        }

                        // Propagate laterally leftward from midX
                        for (int x = midX - 1; x >= 0; x--) {
                            int refY = crestY[x + 1];
                            int minY = math.max(0, refY - 2);
                            int maxY = math.min(H - 1, refY + 2);
                            int bestY = refY;
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
                        }

                        bool[] isValidCrest = new bool[W];
                        float trackedCrestMaxEta = -999f;
                        int trackedPeakX = -1;
                        int trackedPeakY = -1;
                        int frameSplitCount = 0;
                        int frameLostCount = 0;

                        int validCols = 0;
                        int checkCols = 0;
                        for (int x = 0; x < W; x++) {
                            float val = crestEta[x];
                            int cy = crestY[x];
                            bool valid = (val >= 0.05f) && (x >= 40 && x < W - 40);
                            isValidCrest[x] = valid;
                            if (x >= 40 && x < W - 40) {
                                checkCols++;
                                if (valid) validCols++;
                                else frameLostCount++;
                            }

                            if (val > trackedCrestMaxEta) {
                                trackedCrestMaxEta = val;
                                trackedPeakX = x;
                                trackedPeakY = cy;
                            }

                            // True wave split / bifurcation: a distinct secondary crest ahead/behind separated by a trough
                            if (cy > 10 && cy < H - 25) {
                                for (int y2 = cy + 10; y2 <= math.min(H - 2, cy + 30); y2++) {
                                    float val2 = data[y2 * W + x].x;
                                    if (val2 > 0.08f && val2 > data[(y2 - 1) * W + x].x && val2 > data[(y2 + 1) * W + x].x) {
                                        float minTrough = 999f;
                                        for (int yt = cy + 1; yt < y2; yt++) {
                                            float vt = data[yt * W + x].x;
                                            if (vt < minTrough) minTrough = vt;
                                        }
                                        if (minTrough < 0.6f * math.min(val, val2)) {
                                            frameSplitCount++;
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        totalCrestSplitCount += frameSplitCount;
                        totalTrackingLostCount += frameLostCount;

                        float validColRatio = (checkCols > 0) ? (float)validCols / checkCols : 0f;
                        sumValidColRatio += validColRatio;
                        totalRecordedSubstepCount++;

                        // 2. Global Max Peak (across entire domain)
                        float globalMaxPeak = -999f;
                        int globalPeakX = -1, globalPeakY = -1;
                        float globalPeakU = 0f, globalPeakW = 0f;

                        // 3. Cape Local Peak (within cape region [capeMinX..capeMaxX, capeMinY..capeMaxY])
                        float capeMaxPeak = -999f;
                        int capePeakX = -1, capePeakY = -1;
                        float capePeakU = 0f, capePeakW = 0f;

                        // 4. Shoal Ridge Local Peak
                        float shoalMaxPeak = -999f;
                        int shoalPeakX = -1, shoalPeakY = -1;

                        for (int y = 0; y < H; y++) {
                            int row = y * W;
                            bool isCapeY = (y >= capeMinY && y <= capeMaxY);
                            bool isShoalY = (y >= shoalMinY && y <= shoalMaxY);

                            for (int x = 0; x < W; x++) {
                                float etaVal = data[row + x].x;
                                if (etaVal > globalMaxPeak) {
                                    globalMaxPeak = etaVal;
                                    globalPeakX = x;
                                    globalPeakY = y;
                                    globalPeakU = data[row + x].y;
                                    globalPeakW = data[row + x].z;
                                }

                                if (isCapeY && x >= capeMinX && x <= capeMaxX) {
                                    if (etaVal > capeMaxPeak) {
                                        capeMaxPeak = etaVal;
                                        capePeakX = x;
                                        capePeakY = y;
                                        capePeakU = data[row + x].y;
                                        capePeakW = data[row + x].z;
                                    }
                                }

                                if (isShoalY && x >= shoalMinX && x <= shoalMaxX) {
                                    if (etaVal > shoalMaxPeak) {
                                        shoalMaxPeak = etaVal;
                                        shoalPeakX = x;
                                        shoalPeakY = y;
                                    }
                                }
                            }
                        }

                        // Check target switching: true discontinuous jump in tracked crest trajectory
                        bool targetSwitched = (prevTrackedPeakY >= 0 && math.abs(trackedPeakY - prevTrackedPeakY) > 8);
                        if (targetSwitched) totalTargetSwitchCount++;
                        bool isGlobalPeak = (math.abs(globalPeakY - trackedPeakY) <= 10 && math.abs(globalPeakX - trackedPeakX) <= 30);

                        // Along-crest sawtooth d2 (2dx mode)
                        float crestSawtoothMax = 0f;
                        for (int x = 2; x < W - 2; x++) {
                            if (isValidCrest[x - 1] && isValidCrest[x] && isValidCrest[x + 1]) {
                                float d2 = math.abs(crestEta[x + 1] - 2f * crestEta[x] + crestEta[x - 1]);
                                if (d2 > crestSawtoothMax) crestSawtoothMax = d2;
                            }
                        }

                        // Cape along-crest sawtooth d2: only evaluate if tracked crest is inside Cape latitude [capeMinY, capeMaxY]
                        float capeSawtoothMax = 0f;
                        for (int x = capeMinX + 1; x < capeMaxX - 1; x++) {
                            if (isValidCrest[x - 1] && isValidCrest[x] && isValidCrest[x + 1]) {
                                int cy = crestY[x];
                                if (cy >= capeMinY && cy <= capeMaxY) {
                                    float d2 = math.abs(crestEta[x + 1] - 2f * crestEta[x] + crestEta[x - 1]);
                                    if (d2 > capeSawtoothMax) capeSawtoothMax = d2;
                                }
                            }
                        }
                        if (capeSawtoothMax > maxCapeSawtoothD2) maxCapeSawtoothD2 = capeSawtoothMax;

                        // Substep peak height jump and parity flip
                        float substepJump = (prevSubstepGlobalPeak > 0f) ? math.abs(globalMaxPeak - prevSubstepGlobalPeak) : 0f;
                        if (substepJump > maxSubstepPeakJump && frame > 5) maxSubstepPeakJump = substepJump;

                        float trackedJump = (prevSubstepTrackedPeak > 0f) ? math.abs(trackedCrestMaxEta - prevSubstepTrackedPeak) : 0f;
                        float capeJump = (prevSubstepCapePeak > 0f) ? math.abs(capeMaxPeak - prevSubstepCapePeak) : 0f;
                        if (capeJump > maxCapePeakJump && frame > 5) maxCapePeakJump = capeJump;

                        if (prevSubstepGlobalPeak > 0f && prevPrevSubstepGlobalPeak > 0f) {
                            float d1 = globalMaxPeak - prevSubstepGlobalPeak;
                            float d0 = prevSubstepGlobalPeak - prevPrevSubstepGlobalPeak;
                            if (d1 * d0 < 0f) globalParityFlips++;

                            float dt1 = trackedCrestMaxEta - prevSubstepTrackedPeak;
                            float dt0 = prevSubstepTrackedPeak - prevPrevSubstepTrackedPeak;
                            if (dt1 * dt0 < 0f) trackedParityFlips++;

                            totalSubstepCompares++;
                        }
                        prevPrevSubstepGlobalPeak = prevSubstepGlobalPeak;
                        prevSubstepGlobalPeak = globalMaxPeak;
                        prevPrevSubstepTrackedPeak = prevSubstepTrackedPeak;
                        prevSubstepTrackedPeak = trackedCrestMaxEta;
                        prevSubstepCapePeak = capeMaxPeak;
                        prevTrackedPeakX = trackedPeakX;
                        prevTrackedPeakY = trackedPeakY;

                        float peakH = depthGrid[globalPeakY * W + globalPeakX];
                        float c = math.sqrt(g * math.max(0.01f, peakH + globalMaxPeak));
                        float vel = math.sqrt(globalPeakU * globalPeakU + globalPeakW * globalPeakW);
                        float cfl = (c + vel) * subDt / math.min(dx, dz);
                        if (cfl > maxCfl) maxCfl = cfl;

                        // Sample fixed probes
                        float probeCapeEta = data[probeCapeY * W + probeCapeX].x;
                        float probeApprEta = data[probeApprY * W + probeApprX].x;
                        float probeRidgeEta = data[probeRidgeY * W + probeRidgeX].x;
                        float probeDeepEta = data[probeDeepY * W + probeDeepX].x;

                        string trackState = "ValidTracking";
                        if (trackedCrestMaxEta < 0.05f || validColRatio < 0.35f) {
                            trackState = "Lost";
                        } else if (targetSwitched) {
                            trackState = "TargetSwitch";
                        } else if (frameSplitCount > 20) {
                            trackState = "Split";
                        } else if (prevTrackState == "Split" && frameSplitCount <= 5) {
                            trackState = "Merge";
                        } else {
                            trackState = "ValidTracking";
                            validTrackingSubstepCount++;
                        }
                        prevTrackState = trackState;

                        // Localized Cape & Shoal box statistics at full grid resolution
                        float locCapeMaxEta = -999f;
                        float locCapeMinEta = 999f;
                        double locCapeSumEta = 0.0;
                        int locCapeCount = 0;
                        float locCapeMaxSlope = 0f;
                        float locCapeMax2dx = 0f;

                        for (int ly = capeMinY; ly <= capeMaxY; ly++) {
                            int lrow = ly * W;
                            for (int lx = capeMinX; lx <= capeMaxX; lx++) {
                                float v = data[lrow + lx].x;
                                if (v > locCapeMaxEta) locCapeMaxEta = v;
                                if (v < locCapeMinEta) locCapeMinEta = v;
                                locCapeSumEta += v;
                                locCapeCount++;

                                if (lx > 0 && lx < W - 1) {
                                    float vL = data[lrow + lx - 1].x;
                                    float vR = data[lrow + lx + 1].x;
                                    float d2x = math.abs(vR - 2f * v + vL);
                                    if (d2x > locCapeMax2dx) locCapeMax2dx = d2x;
                                }
                                if (ly > 0 && ly < H - 1) {
                                    float vD = data[(ly - 1) * W + lx].x;
                                    float vU = data[(ly + 1) * W + lx].x;
                                    float d2z = math.abs(vU - 2f * v + vD);
                                    if (d2z > locCapeMax2dx) locCapeMax2dx = d2z;

                                    float dhdx = (data[lrow + math.min(W - 1, lx + 1)].x - data[lrow + math.max(0, lx - 1)].x) / (2f * dx);
                                    float dhdz = (vU - vD) / (2f * dz);
                                    float slope = math.sqrt(dhdx * dhdx + dhdz * dhdz);
                                    if (slope > locCapeMaxSlope) locCapeMaxSlope = slope;
                                }
                            }
                        }
                        float locCapeMeanEta = (locCapeCount > 0) ? (float)(locCapeSumEta / locCapeCount) : 0f;

                        substepRecords.Add(new SubstepRecord {
                            frame = frame,
                            substep = s,
                            time = timeSec + (s + 1) * subDt,
                            parity = parityBefore,
                            direction = (parityBefore == 0) ? 1 : -1,
                            tracking_state = trackState,
                            valid_tracking_ratio = validColRatio,
                            global_peak_x = globalPeakX,
                            global_peak_y = globalPeakY,
                            global_peak_eta = globalMaxPeak,
                            global_peak_depth = peakH,
                            tracked_peak_x = trackedPeakX,
                            tracked_peak_y = trackedPeakY,
                            tracked_peak_eta = trackedCrestMaxEta,
                            cape_peak_x = capePeakX,
                            cape_peak_y = capePeakY,
                            cape_peak_eta = capeMaxPeak,
                            shoal_peak_x = shoalPeakX,
                            shoal_peak_y = shoalPeakY,
                            shoal_peak_eta = shoalMaxPeak,
                            local_cape_max_eta = locCapeMaxEta,
                            local_cape_min_eta = locCapeMinEta,
                            local_cape_mean_eta = locCapeMeanEta,
                            local_cape_max_slope = locCapeMaxSlope,
                            local_cape_max_2dx = locCapeMax2dx,
                            probe_cape_eta = probeCapeEta,
                            probe_approach_eta = probeApprEta,
                            probe_ridge_eta = probeRidgeEta,
                            probe_deep_eta = probeDeepEta,
                            crest_sawtooth_d2 = crestSawtoothMax,
                            cape_sawtooth_d2 = capeSawtoothMax,
                            substep_jump = substepJump,
                            tracked_jump = trackedJump,
                            cape_jump = capeJump,
                            cfl = cfl,
                            target_switched = targetSwitched,
                            is_global_peak = isGlobalPeak,
                            split_count = frameSplitCount,
                            lost_count = frameLostCount
                        });
                    }
                }

                boussSim.FinalizeFrameOutput();

                // Frame-level scan and crest profile extraction
                var frameStateRT = boussSim.CurrentStateTexture;
                var frameReq = AsyncGPUReadback.Request(frameStateRT, 0, TextureFormat.RGBAFloat);
                frameReq.WaitForCompletion();
                if (frameReq.hasError) {
                    Debug.LogError($"[ShoalDiagnosticProbe] Frame readback failed at frame={frame}, time={timeSec:F3}s");
                    break;
                }
                var frameData = frameReq.GetData<float4>();

                if (!recordSubsteps) {
                    // Update crest position at frame level so tracker smoothly follows primary solitary wave from t=0
                    int prevMidY = crestY[midX];
                    int bestMidY = prevMidY;
                    float bestMidVal = -999f;
                    for (int y = math.max(0, prevMidY - 2); y <= math.min(H - 1, prevMidY + 10); y++) {
                        float val = frameData[y * W + midX].x;
                        if (val > bestMidVal) {
                            bestMidVal = val;
                            bestMidY = y;
                        }
                    }
                    crestY[midX] = bestMidY;
                    crestEta[midX] = bestMidVal;

                    for (int x = midX + 1; x < W; x++) {
                        int refY = crestY[x - 1];
                        int minY = math.max(0, refY - 4);
                        int maxY = math.min(H - 1, refY + 4);
                        int bestY = refY;
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
                    }

                    for (int x = midX - 1; x >= 0; x--) {
                        int refY = crestY[x + 1];
                        int minY = math.max(0, refY - 4);
                        int maxY = math.min(H - 1, refY + 4);
                        int bestY = refY;
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
                    }
                }

                // Capture visual frames every 12 frames (approx 0.192s) for video animation
                if (frame % 12 == 0) {
                    SaveFrameHeatmap(frameData, depthGrid, W, H, frame, timeSec);
                }

                float fGlobalPeakEta = -999f;
                int fGlobalPeakX = -1, fGlobalPeakY = -1;
                float fCapePeakEta = -999f;
                int fCapePeakX = -1, fCapePeakY = -1;

                for (int y = 0; y < H; y++) {
                    int row = y * W;
                    bool isCapeY = (y >= capeMinY && y <= capeMaxY);
                    for (int x = 0; x < W; x++) {
                        float v = frameData[row + x].x;
                        if (v > fGlobalPeakEta) {
                            fGlobalPeakEta = v;
                            fGlobalPeakX = x;
                            fGlobalPeakY = y;
                        }
                        if (isCapeY && x >= capeMinX && x <= capeMaxX) {
                            if (v > fCapePeakEta) {
                                fCapePeakEta = v;
                                fCapePeakX = x;
                                fCapePeakY = y;
                            }
                        }
                    }
                }

                // Wave crest corrugation along crest line:
                // Smooth macroscopic curve using moving window (size 11), subtract to isolate corrugation
                float[] crestProfile = new float[W];
                int[] crestYCoords = new int[W];
                float[] smoothProfile = new float[W];

                for (int x = 0; x < W; x++) {
                    crestProfile[x] = crestEta[x];
                    crestYCoords[x] = crestY[x];
                }

                int win = 5;
                for (int x = 0; x < W; x++) {
                    float sum = 0f;
                    int count = 0;
                    for (int k = -win; k <= win; k++) {
                        int idx = math.clamp(x + k, 0, W - 1);
                        sum += crestProfile[idx];
                        count++;
                    }
                    smoothProfile[x] = sum / count;
                }

                float frameMaxCorrugation = 0f;
                float frameMaxCrestSawtooth = 0f;
                int zigzagCount = 0;

                for (int x = 2; x < W - 2; x++) {
                    if (crestProfile[x] < 0.05f || x < 40 || x > W - 40) continue;

                    float corrug = math.abs(crestProfile[x] - smoothProfile[x]);
                    if (corrug > frameMaxCorrugation) frameMaxCorrugation = corrug;

                    if (crestProfile[x - 1] >= 0.05f && crestProfile[x + 1] >= 0.05f) {
                        float d2 = math.abs(crestProfile[x + 1] - 2f * crestProfile[x] + crestProfile[x - 1]);
                        if (d2 > frameMaxCrestSawtooth) frameMaxCrestSawtooth = d2;

                        float dL = crestProfile[x] - crestProfile[x - 1];
                        float dR = crestProfile[x + 1] - crestProfile[x];
                        if (dL * dR < -1e-6f) zigzagCount++;
                    }
                }

                if (frameMaxCorrugation > maxCrestCorrugation) maxCrestCorrugation = frameMaxCorrugation;
                if (frameMaxCrestSawtooth > maxCrestSawtoothD2) maxCrestSawtoothD2 = frameMaxCrestSawtooth;

                // Cross-crest (propagation direction) sawtooth metric
                int probeX = (fCapePeakX >= 0) ? fCapePeakX : fGlobalPeakX;
                int crestAtX = crestY[probeX];
                float crossCrestSawtooth = 0f;
                for (int y = math.max(1, crestAtX - 4); y <= math.min(H - 2, crestAtX + 4); y++) {
                    float vC = frameData[y * W + probeX].x;
                    float vD = frameData[(y - 1) * W + probeX].x;
                    float vU = frameData[(y + 1) * W + probeX].x;
                    float d2y = math.abs(vU - 2f * vC + vD);
                    if (d2y > crossCrestSawtooth) crossCrestSawtooth = d2y;
                }
                if (crossCrestSawtooth > maxCrossCrestSawtoothD2) maxCrossCrestSawtoothD2 = crossCrestSawtooth;

                float fJump = (prevFramePeak > 0f) ? math.abs(fGlobalPeakEta - prevFramePeak) : 0f;
                if (fJump > maxFramePeakJump && frame > 5) maxFramePeakJump = fJump;
                prevFramePeak = fGlobalPeakEta;

                frameRecords.Add(new FrameRecord {
                    frame = frame,
                    time = timeSec,
                    substeps = subSteps,
                    global_peak_x = fGlobalPeakX,
                    global_peak_y = fGlobalPeakY,
                    global_peak_eta = fGlobalPeakEta,
                    cape_peak_x = fCapePeakX,
                    cape_peak_y = fCapePeakY,
                    cape_peak_eta = fCapePeakEta,
                    crest_corrugation = frameMaxCorrugation,
                    crest_sawtooth_d2 = frameMaxCrestSawtooth,
                    cross_crest_sawtooth_d2 = crossCrestSawtooth,
                    zigzag_points = zigzagCount,
                    frame_jump = fJump,
                    crest_profile = crestProfile,
                    crest_y_coords = crestYCoords
                });
            }

            float globalParityFlipRatio = totalSubstepCompares > 0 ? (float)globalParityFlips / totalSubstepCompares : 0f;
            float trackedParityFlipRatio = totalSubstepCompares > 0 ? (float)trackedParityFlips / totalSubstepCompares : 0f;

            var outObj = new DiagnosticOutput {
                git_commit = gitCommit,
                is_dirty = isDirty,
                shader_hash = shaderHash,
                csharp_boussinesq_sim_hash = csSimHash,
                csharp_probe_hash = csProbeHash,
                csharp_verifier_hash = csVerifierHash,
                filter_mode = filterMode.ToString(),
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
                    max_cape_peak_jump = maxCapePeakJump,
                    max_crest_corrugation = maxCrestCorrugation,
                    max_crest_sawtooth_d2 = maxCrestSawtoothD2,
                    max_cape_sawtooth_d2 = maxCapeSawtoothD2,
                    max_cross_crest_sawtooth_d2 = maxCrossCrestSawtoothD2,
                    global_parity_sign_flip_ratio = globalParityFlipRatio,
                    tracked_parity_sign_flip_ratio = trackedParityFlipRatio,
                    max_cfl = maxCfl,
                    valid_tracking_substep_ratio = (totalRecordedSubstepCount > 0) ? (float)validTrackingSubstepCount / totalRecordedSubstepCount : 0f,
                    valid_tracking_mean_col_ratio = (totalRecordedSubstepCount > 0) ? (float)(sumValidColRatio / totalRecordedSubstepCount) : 0f,
                    total_crest_split_events = totalCrestSplitCount,
                    total_tracking_lost_events = totalTrackingLostCount,
                    total_target_switch_events = totalTargetSwitchCount
                },
                frames = frameRecords,
                substeps = substepRecords
            };

            string jsonStr = JsonUtility.ToJson(outObj, true);
            string outDir = Path.GetDirectoryName(actualJsonPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(actualJsonPath, jsonStr, Encoding.UTF8);

            Debug.Log($"[ShoalDiagnosticProbe] 2D Wave Crest Tracking GPU Diagnostic Complete!\n" +
                      $"  Git Commit: {gitCommit} (Dirty: {isDirty})\n" +
                      $"  Shader Hash: {shaderHash}\n" +
                      $"  Saved to: {actualJsonPath}\n" +
                      $"  Max Frame Peak Jump: {maxFramePeakJump:F5} m\n" +
                      $"  Max Substep Peak Jump: {maxSubstepPeakJump:F5} m\n" +
                      $"  Max Cape Peak Jump: {maxCapePeakJump:F5} m\n" +
                      $"  Max Crest Corrugation: {maxCrestCorrugation:F6} m\n" +
                      $"  Max Crest Sawtooth d2: {maxCrestSawtoothD2:F6}\n" +
                      $"  Max Cape Sawtooth d2: {maxCapeSawtoothD2:F6}\n" +
                      $"  Tracked Parity Sign Flip Ratio: {trackedParityFlipRatio * 100f:F1}%\n" +
                      $"  Total Crest Splits: {totalCrestSplitCount}, Lost: {totalTrackingLostCount}, Switches: {totalTargetSwitchCount}");
        }
        #endregion

        #region private methods
        private static void SaveFrameHeatmap(NativeArray<float4> stateData, float[] depthGrid, int W, int H, int frame, float timeSec) {
            try {
                // Save a downsampled 128x128 byte map for fast visual animation rendering
                int downW = 128;
                int downH = 128;
                byte[] rawBytes = new byte[downW * downH * 3];
                int stepX = W / downW;
                int stepY = H / downH;

                for (int dy = 0; dy < downH; dy++) {
                    int sy = dy * stepY;
                    for (int dx = 0; dx < downW; dx++) {
                        int sx = dx * stepX;
                        int srcIdx = sy * W + sx;
                        float eta = stateData[srcIdx].x;
                        float depth = depthGrid[srcIdx];

                        // Normalized elevation color mapping:
                        // Negative (trough): Blue-Cyan
                        // Zero (still water): Dark Slate
                        // Positive peak (crest): Yellow-White
                        float normEta = math.clamp((eta + 0.2f) / 0.9f, 0f, 1f);
                        byte r = (byte)(normEta * 255f);
                        byte g = (byte)(math.sin(normEta * math.PI * 0.5f) * 255f);
                        byte b = (byte)((1f - normEta * 0.7f) * 200f);

                        int dstIdx = (dy * downW + dx) * 3;
                        rawBytes[dstIdx + 0] = r;
                        rawBytes[dstIdx + 1] = g;
                        rawBytes[dstIdx + 2] = b;
                    }
                }

                string pngPath = Path.Combine(AnimationOutputDir, $"frame_{frame:D4}.raw");
                File.WriteAllBytes(pngPath, rawBytes);
            } catch {
                // Ignore capture errors
            }
        }

        private static string QueryGitCommit() {
            try {
                var psi = new ProcessStartInfo {
                    FileName = "git",
                    Arguments = "rev-parse HEAD",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                string commit = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                return commit;
            } catch {
                return "unknown";
            }
        }

        private static bool QueryGitDirty() {
            try {
                var psi = new ProcessStartInfo {
                    FileName = "git",
                    Arguments = "status --porcelain",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                string dirty = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                return !string.IsNullOrEmpty(dirty);
            } catch {
                return true;
            }
        }

        private static string ComputeShaderSha256(string path) {
            if (!File.Exists(path)) return "missing";
            try {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            } catch {
                return "error";
            }
        }
        #endregion

        #region nested types
        [Serializable]
        public class DiagnosticOutput {
            public string git_commit;
            public bool is_dirty;
            public string shader_hash;
            public string csharp_boussinesq_sim_hash;
            public string csharp_probe_hash;
            public string csharp_verifier_hash;
            public string filter_mode;
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

        [Serializable]
        public class DiagnosticSummary {
            public float max_frame_peak_jump;
            public float max_substep_peak_jump;
            public float max_cape_peak_jump;
            public float max_crest_corrugation;
            public float max_crest_sawtooth_d2;
            public float max_cape_sawtooth_d2;
            public float max_cross_crest_sawtooth_d2;
            public float global_parity_sign_flip_ratio;
            public float tracked_parity_sign_flip_ratio;
            public float max_cfl;
            public float valid_tracking_substep_ratio;
            public float valid_tracking_mean_col_ratio;
            public int total_crest_split_events;
            public int total_tracking_lost_events;
            public int total_target_switch_events;
        }

        [Serializable]
        public class FrameRecord {
            public int frame;
            public float time;
            public int substeps;
            public int global_peak_x;
            public int global_peak_y;
            public float global_peak_eta;
            public int cape_peak_x;
            public int cape_peak_y;
            public float cape_peak_eta;
            public float crest_corrugation;
            public float crest_sawtooth_d2;
            public float cross_crest_sawtooth_d2;
            public int zigzag_points;
            public float frame_jump;
            public float[] crest_profile;
            public int[] crest_y_coords;
        }

        [Serializable]
        public class SubstepRecord {
            public int frame;
            public int substep;
            public float time;
            public int parity;
            public int direction;
            public string tracking_state;
            public float valid_tracking_ratio;
            public int global_peak_x;
            public int global_peak_y;
            public float global_peak_eta;
            public float global_peak_depth;
            public int tracked_peak_x;
            public int tracked_peak_y;
            public float tracked_peak_eta;
            public int cape_peak_x;
            public int cape_peak_y;
            public float cape_peak_eta;
            public int shoal_peak_x;
            public int shoal_peak_y;
            public float shoal_peak_eta;
            public float local_cape_max_eta;
            public float local_cape_min_eta;
            public float local_cape_mean_eta;
            public float local_cape_max_slope;
            public float local_cape_max_2dx;
            public float probe_cape_eta;
            public float probe_approach_eta;
            public float probe_ridge_eta;
            public float probe_deep_eta;
            public float crest_sawtooth_d2;
            public float cape_sawtooth_d2;
            public float substep_jump;
            public float tracked_jump;
            public float cape_jump;
            public float cfl;
            public bool target_switched;
            public bool is_global_peak;
            public int split_count;
            public int lost_count;
        }
        #endregion
    }
}
