using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Executes a rigorous GPU Compute Shader vs CPU continuum solver comparison
    /// across all intermediate pipeline stages on a non-uniform bathymetry grid.
    /// Verifies current directional-split 2-pass filter (CSFilterX -> CSFilterZ),
    /// both parity directions (forward & backward), wavemaker source, multi-substeps,
    /// wall normal velocities, mass conservation, and NaN/Inf integrity.
    /// </summary>
    public static class ShoalGpuCpuVerifier {
        #region constants / static
        private const string OutputJsonPath = "Docs/ShoalInvestigation/GpuCpuSubstepMatch.json";
        private const string ShaderPath = "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute";
        private const string TriggerFile = "Temp/run_shoal_verifier.trigger";
        private const string DoneMarker = "Temp/shoal_verifier_done.marker";
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
                    Debug.Log("[ShoalGpuCpuVerifier] Trigger detected, executing verification suite...");
                    RunVerification();
                    File.WriteAllText(DoneMarker, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                } catch (Exception ex) {
                    Debug.LogError($"[ShoalGpuCpuVerifier] Trigger execution error: {ex}");
                    File.WriteAllText(DoneMarker, "ERROR: " + ex.ToString(), Encoding.UTF8);
                }
            }
        }
        #endregion

        #region public interface
        public static void RunBatch() {
            RunVerification();
        }

        public static void RunCommandLine() {
            try {
                RunVerification();
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                Debug.LogError($"[ShoalGpuCpuVerifier] Verification failed: {ex}");
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("Tools/Water Simulation/Verify GPU vs CPU New 2-Pass Filter", false, 53)]
        public static void RunVerification() {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            if (shader == null) {
                Debug.LogError($"[ShoalGpuCpuVerifier] Compute shader not found at {ShaderPath}.");
                return;
            }

            string gitCommit = QueryGitCommit();
            bool isDirty = QueryGitDirty();
            string shaderHash = ComputeShaderSha256(ShaderPath);

            int N = 32;
            float L = 50.0f;
            float dx = L / N;
            float dz = L / N;
            float dt = 0.008f;
            float g = 9.81f;
            float alpha = 1.0f;
            float frictionCoeff = 0.005f;
            float waveAmp = 0.20f;
            float waveFreq = 0.25f;

            float[] depth = new float[N * N];
            for (int y = 0; y < N; y++) {
                float ny = (float)y / (N - 1);
                for (int x = 0; x < N; x++) {
                    float nx = (float)x / (N - 1);
                    depth[y * N + x] = math.max(0.5f, math.lerp(3.5f, 0.8f, ny) + 0.3f * math.sin(nx * math.PI * 2f));
                }
            }

            var suiteReport = new VerifierReport {
                test = "GPU Compute Shader vs CPU Continuum Solver Verification Suite",
                git_commit = gitCommit,
                is_dirty = isDirty,
                shader_hash = shaderHash,
                grid_size = N,
                dx = dx,
                dz = dz,
                dt = dt,
                alpha = alpha,
                friction = frictionCoeff,
                wave_amplitude = waveAmp,
                wave_frequency = waveFreq
            };

            bool allPassed = true;

            // Suite 1: Single substep Forward Parity (parity = 0) with wavemaker
            Debug.Log("[ShoalGpuCpuVerifier] Running Suite 1: Forward Parity (0)...");
            var res1 = ExecuteSubstep(shader, N, dx, dz, dt, g, alpha, frictionCoeff, depth, 0, 0f, waveAmp, waveFreq);
            suiteReport.test_runs.Add(res1);
            if (!res1.all_passed) allPassed = false;

            // Suite 2: Single substep Backward Parity (parity = 1) with wavemaker
            Debug.Log("[ShoalGpuCpuVerifier] Running Suite 2: Backward Parity (1)...");
            var res2 = ExecuteSubstep(shader, N, dx, dz, dt, g, alpha, frictionCoeff, depth, 1, 0.5f, waveAmp, waveFreq);
            suiteReport.test_runs.Add(res2);
            if (!res2.all_passed) allPassed = false;

            // Suite 3: Multi-substep run (4 sequential substeps with alternating parity)
            Debug.Log("[ShoalGpuCpuVerifier] Running Suite 3: Multi-Substep (4 steps)...");
            var res3 = ExecuteMultiSubstep(shader, N, dx, dz, dt, g, alpha, frictionCoeff, depth, 4, waveAmp, waveFreq);
            suiteReport.multi_substep_runs.Add(res3);
            if (!res3.all_passed) allPassed = false;

            // Suite 4: Standalone Filter Mass Conservation, FCT Monotonicity, and Wall Normal Velocities
            Debug.Log("[ShoalGpuCpuVerifier] Running Suite 4: Standalone Conservative Filter...");
            var res4 = ExecuteFilterStandaloneTest(shader, N, dx, dz, dt);
            suiteReport.test_runs.Add(res4);
            if (!res4.all_passed) allPassed = false;

            // Suite 5: Step vs Substep Equivalence (verifying diagnostic uniform binding and parity state)
            Debug.Log("[ShoalGpuCpuVerifier] Running Suite 5: Step vs Substep Equivalence in Scene 02...");
            var res5 = ExecuteStepVsSubstepEquivalenceTest();
            suiteReport.test_runs.Add(res5);
            if (!res5.all_passed) allPassed = false;

            suiteReport.overall_passed = allPassed;

            string jsonStr = JsonUtility.ToJson(suiteReport, true);
            string outDir = Path.GetDirectoryName(OutputJsonPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(OutputJsonPath, jsonStr, Encoding.UTF8);

            Debug.Log($"[ShoalGpuCpuVerifier] Verification Complete! Overall Passed = {allPassed}\n" +
                      $"  Git Commit: {gitCommit} (Dirty: {isDirty})\n" +
                      $"  Shader Hash: {shaderHash}\n" +
                      $"  Saved to: {OutputJsonPath}");

            if (!allPassed) throw new Exception("One or more test suites failed GPU-CPU equivalence check.");
        }
        #endregion

        #region private methods
        private static TestRunResult ExecuteSubstep(ComputeShader shader, int N, float dx, float dz, float dt,
            float g, float alpha, float frictionCoeff, float[] depth, int parity, float wavePhase, float waveAmp, float waveFreq) {
            var runRes = new TestRunResult {
                suite_name = $"SingleSubstep_Parity_{parity}",
                parity = parity,
                stages = new List<StageResult>()
            };

            float[] initEta = new float[N * N];
            float[] initU = new float[N * N];
            float[] initW = new float[N * N];
            for (int y = 0; y < N; y++) {
                float ny = (float)y / (N - 1);
                for (int x = 0; x < N; x++) {
                    float nx = (float)x / (N - 1);
                    int idx = y * N + x;
                    float dxDist = (nx - 0.5f) * 4f;
                    float dyDist = (ny - 0.4f) * 4f;
                    initEta[idx] = 0.35f * math.exp(-(dxDist * dxDist + dyDist * dyDist));
                    initU[idx] = (x == 0 || x == N - 1) ? 0f : 0.05f * math.cos(nx * math.PI * 2f);
                    initW[idx] = (y == 0 || y == N - 1) ? 0f : 0.10f * math.sin(ny * math.PI * 2f);
                }
            }

            var rts = AllocateTextures(N);
            PopulateTextures(N, initEta, initU, initW, depth, rts);
            BindShaderUniforms(shader, N, dx, dz, dt, g, alpha, frictionCoeff, parity, waveAmp, waveFreq, wavePhase);

            int kPredict = shader.FindKernel("CSPredict");
            int kTridiagX = shader.FindKernel("CSTridiagonalX");
            int kTridiagZ = shader.FindKernel("CSTridiagonalZ");
            int kApplyPredDisp = shader.FindKernel("CSApplyPredictedDispersion");
            int kCorrect = shader.FindKernel("CSCorrect");
            int kApplyDisp = shader.FindKernel("CSApplyDispersion");
            int kFilterX = shader.FindKernel("CSFilterX");
            int kFilterZ = shader.FindKernel("CSFilterZ");

            // Stage 1: Predictor
            shader.SetTexture(kPredict, "_StateIn", rts.stateInRT);
            shader.SetTexture(kPredict, "_StatePred", rts.statePredRT);
            shader.SetTexture(kPredict, "_BathymetryTex", rts.bathyRT);
            shader.SetTexture(kPredict, "_ForceOrigTex", rts.forceOrigRT);
            shader.SetTexture(kPredict, "_ForceTex", rts.forceRT);
            shader.SetTexture(kPredict, "_AccelPing", rts.accelPingRT);
            shader.Dispatch(kPredict, N / 8, N / 8, 1);

            var gpuStatePred = ReadbackFloat4(rts.statePredRT);
            var gpuForcePred = ReadbackFloat2(rts.forceRT);

            float[] cpuEtaPred = new float[N * N];
            float[] cpuFxPred = new float[N * N];
            float[] cpuFzPred = new float[N * N];
            int dirPred = (parity == 0) ? 1 : -1;
            ComputeCpuPredictor(N, dx, dz, dt, g, frictionCoeff, depth, initEta, initU, initW,
                dirPred, wavePhase, waveAmp, waveFreq, cpuEtaPred, cpuFxPred, cpuFzPred);

            CompareStage(runRes.stages, "1_Predictor_Eta", gpuStatePred, cpuEtaPred, 0, 1e-4f);
            CompareStage(runRes.stages, "1_Predictor_ForceX", gpuForcePred, cpuFxPred, 0, 1e-4f);
            CompareStage(runRes.stages, "1_Predictor_ForceZ", gpuForcePred, cpuFzPred, 1, 1e-4f);

            // Stage 2: Predictor Dispersion (CSTridiagonalX -> CSTridiagonalZ)
            shader.SetTexture(kTridiagX, "_BathymetryTex", rts.bathyRT);
            shader.SetTexture(kTridiagX, "_ForceTex", rts.forceRT);
            shader.SetTexture(kTridiagX, "_AccelPong", rts.accelPongRT);
            shader.Dispatch(kTridiagX, N, 1, 1);

            shader.SetTexture(kTridiagZ, "_BathymetryTex", rts.bathyRT);
            shader.SetTexture(kTridiagZ, "_AccelPong", rts.accelPongRT);
            shader.SetTexture(kTridiagZ, "_AccelPing", rts.accelPingRT);
            shader.Dispatch(kTridiagZ, N, 1, 1);

            var gpuA1 = ReadbackFloat2(rts.accelPingRT);
            float2[] cpuAStar = SolveTridiagX(cpuFxPred, cpuFzPred, depth, N, dx, alpha, false);
            float2[] cpuA1 = SolveTridiagZ(cpuAStar, depth, N, dz, alpha, false);

            CompareStage(runRes.stages, "2_Dispersion_Ax1", gpuA1, cpuA1, 0, 5e-4f);
            CompareStage(runRes.stages, "2_Dispersion_Az1", gpuA1, cpuA1, 1, 5e-4f);

            // Stage 3: CSApplyPredictedDispersion
            shader.SetTexture(kApplyPredDisp, "_StatePred", rts.statePredRT);
            shader.SetTexture(kApplyPredDisp, "_AccelPing", rts.accelPingRT);
            shader.SetTexture(kApplyPredDisp, "_ForcePredTex", rts.forcePredRT);
            shader.Dispatch(kApplyPredDisp, N / 8, N / 8, 1);

            var gpuStatePredDisp = ReadbackFloat4(rts.statePredRT);
            float[] cpuUPred = new float[N * N];
            float[] cpuWPred = new float[N * N];
            ComputeCpuApplyPredDisp(N, dt, cpuEtaPred, initU, initW, cpuA1, cpuUPred, cpuWPred);

            CompareStage(runRes.stages, "3_ApplyPredDisp_EtaPred", gpuStatePredDisp, cpuEtaPred, 0, 1e-4f);
            CompareStage(runRes.stages, "3_ApplyPredDisp_UPred", gpuStatePredDisp, cpuUPred, 1, 5e-4f);
            CompareStage(runRes.stages, "3_ApplyPredDisp_WPred", gpuStatePredDisp, cpuWPred, 2, 5e-4f);

            // Stage 4: Corrector
            shader.SetTexture(kCorrect, "_StateIn", rts.stateInRT);
            shader.SetTexture(kCorrect, "_StatePred", rts.statePredRT);
            shader.SetTexture(kCorrect, "_StateOut", rts.stateOutRT);
            shader.SetTexture(kCorrect, "_BathymetryTex", rts.bathyRT);
            shader.SetTexture(kCorrect, "_ForceOrigTex", rts.forceOrigRT);
            shader.SetTexture(kCorrect, "_ForceTex", rts.forceRT);
            shader.SetTexture(kCorrect, "_ForcePredTex", rts.forcePredRT);
            shader.SetTexture(kCorrect, "_AccelPing", rts.accelPingRT);
            shader.Dispatch(kCorrect, N / 8, N / 8, 1);

            var gpuStateCorr = ReadbackFloat4(rts.stateOutRT);
            var gpuForceStar = ReadbackFloat2(rts.forceOrigRT);

            float[] cpuEtaNext = new float[N * N];
            float[] cpuFxStar = new float[N * N];
            float[] cpuFzStar = new float[N * N];
            int dirCorr = -dirPred;
            float nextWavePhase = wavePhase + 2f * math.PI * waveFreq * dt;
            ComputeCpuCorrector(N, dx, dz, dt, g, frictionCoeff, depth, initEta, cpuEtaPred, cpuUPred, cpuWPred,
                dirCorr, nextWavePhase, waveAmp, waveFreq, cpuEtaNext, cpuFxStar, cpuFzStar);

            CompareStage(runRes.stages, "4_Corrector_EtaNext", gpuStateCorr, cpuEtaNext, 0, 2e-4f);
            CompareStage(runRes.stages, "4_Corrector_ForceStarX", gpuForceStar, cpuFxStar, 0, 2e-4f);
            CompareStage(runRes.stages, "4_Corrector_ForceStarZ", gpuForceStar, cpuFzStar, 1, 2e-4f);

            // Stage 5: Corrector Dispersion (CSTridiagonalX -> CSTridiagonalZ)
            shader.SetTexture(kTridiagX, "_BathymetryTex", rts.bathyRT);
            shader.SetTexture(kTridiagX, "_ForceTex", rts.forceRT);
            shader.SetTexture(kTridiagX, "_AccelPong", rts.accelPongRT);
            shader.Dispatch(kTridiagX, N, 1, 1);

            shader.SetTexture(kTridiagZ, "_BathymetryTex", rts.bathyRT);
            shader.SetTexture(kTridiagZ, "_AccelPong", rts.accelPongRT);
            shader.SetTexture(kTridiagZ, "_AccelPing", rts.accelPingRT);
            shader.Dispatch(kTridiagZ, N, 1, 1);

            var gpuA2 = ReadbackFloat2(rts.accelPingRT);
            float2[] cpuAStar2 = SolveTridiagX(cpuFxStar, cpuFzStar, depth, N, dx, alpha, false);
            float2[] cpuA2 = SolveTridiagZ(cpuAStar2, depth, N, dz, alpha, false);

            CompareStage(runRes.stages, "5_Dispersion_Ax2", gpuA2, cpuA2, 0, 5e-4f);
            CompareStage(runRes.stages, "5_Dispersion_Az2", gpuA2, cpuA2, 1, 5e-4f);

            // Stage 6: CSApplyDispersion (next -> predTex)
            shader.SetTexture(kApplyDisp, "_StateIn", rts.stateOutRT);
            shader.SetTexture(kApplyDisp, "_StateOut", rts.statePredRT);
            shader.SetTexture(kApplyDisp, "_ForcePredTex", rts.forcePredRT);
            shader.SetTexture(kApplyDisp, "_AccelPing", rts.accelPingRT);
            shader.SetTexture(kApplyDisp, "_BathymetryTex", rts.bathyRT);
            shader.Dispatch(kApplyDisp, N / 8, N / 8, 1);

            var gpuStateDisp = ReadbackFloat4(rts.statePredRT);
            float[] cpuUNext = new float[N * N];
            float[] cpuWNext = new float[N * N];
            ComputeCpuApplyDisp(N, dt, initU, initW, cpuA1, cpuA2, cpuUNext, cpuWNext);

            CompareStage(runRes.stages, "6_ApplyDispersion_UNext", gpuStateDisp, cpuUNext, 1, 5e-4f);
            CompareStage(runRes.stages, "6_ApplyDispersion_WNext", gpuStateDisp, cpuWNext, 2, 5e-4f);

            // Stage 7: CSFilterX (Pass 1 along X: predTex -> stateInRT)
            shader.SetTexture(kFilterX, "_StateIn", rts.statePredRT);
            shader.SetTexture(kFilterX, "_StateOut", rts.stateInRT);
            shader.Dispatch(kFilterX, N / 8, N / 8, 1);

            var gpuFilterX = ReadbackFloat4(rts.stateInRT);
            ApplyFilterX(cpuEtaNext, cpuUNext, cpuWNext, N, dx, dt, out var cpuEtaFilterX, out var cpuUFilterX, out var cpuWFilterX);

            CompareStage(runRes.stages, "7_FilterX_Eta", gpuFilterX, cpuEtaFilterX, 0, 1e-4f);
            CompareStage(runRes.stages, "7_FilterX_U", gpuFilterX, cpuUFilterX, 1, 5e-4f);
            CompareStage(runRes.stages, "7_FilterX_W", gpuFilterX, cpuWFilterX, 2, 5e-4f);

            // Stage 8: CSFilterZ (Pass 2 along Z: stateInRT -> stateOutRT)
            shader.SetTexture(kFilterZ, "_StateIn", rts.stateInRT);
            shader.SetTexture(kFilterZ, "_StateOut", rts.stateOutRT);
            shader.Dispatch(kFilterZ, N / 8, N / 8, 1);

            var gpuFilterZ = ReadbackFloat4(rts.stateOutRT);
            ApplyFilterZ(cpuEtaFilterX, cpuUFilterX, cpuWFilterX, N, dz, dt, out var cpuEtaFinal, out var cpuUFinal, out var cpuWFinal);

            CompareStage(runRes.stages, "8_FilterZ_EtaFinal", gpuFilterZ, cpuEtaFinal, 0, 1e-4f);
            CompareStage(runRes.stages, "8_FilterZ_UFinal", gpuFilterZ, cpuUFinal, 1, 5e-4f);
            CompareStage(runRes.stages, "8_FilterZ_WFinal", gpuFilterZ, cpuWFinal, 2, 5e-4f);

            // Wall boundary checks
            float maxWallU = 0f;
            float maxWallW = 0f;
            for (int y = 0; y < N; y++) {
                maxWallU = math.max(maxWallU, math.abs(gpuFilterZ[y * N + 0].y));
                maxWallU = math.max(maxWallU, math.abs(gpuFilterZ[y * N + N - 1].y));
            }
            for (int x = 0; x < N; x++) {
                maxWallW = math.max(maxWallW, math.abs(gpuFilterZ[0 * N + x].z));
                maxWallW = math.max(maxWallW, math.abs(gpuFilterZ[(N - 1) * N + x].z));
            }
            runRes.max_lateral_wall_u = maxWallU;
            runRes.max_transverse_wall_w = maxWallW;
            runRes.wall_velocities_strictly_zero = (maxWallU == 0f && maxWallW == 0f);

            // Mass conservation check
            double initMass = 0;
            double finalMass = 0;
            bool hasNan = false;
            for (int i = 0; i < N * N; i++) {
                initMass += initEta[i];
                finalMass += gpuFilterZ[i].x;
                if (float.IsNaN(gpuFilterZ[i].x) || float.IsInfinity(gpuFilterZ[i].x) ||
                    float.IsNaN(gpuFilterZ[i].y) || float.IsInfinity(gpuFilterZ[i].y) ||
                    float.IsNaN(gpuFilterZ[i].z) || float.IsInfinity(gpuFilterZ[i].z)) {
                    hasNan = true;
                }
            }
            runRes.mass_before = (float)initMass;
            runRes.mass_after = (float)finalMass;
            runRes.mass_delta = (float)math.abs(finalMass - initMass);
            runRes.nan_free = !hasNan;

            ReleaseTextures(ref rts);

            bool pass = true;
            foreach (var st in runRes.stages) {
                if (!st.passed) pass = false;
            }
            if (!runRes.wall_velocities_strictly_zero) pass = false;
            if (!runRes.nan_free) pass = false;
            runRes.all_passed = pass;

            return runRes;
        }

        private static MultiSubstepResult ExecuteMultiSubstep(ComputeShader shader, int N, float dx, float dz, float dt,
            float g, float alpha, float frictionCoeff, float[] depth, int stepCount, float waveAmp, float waveFreq) {
            var multiRes = new MultiSubstepResult {
                total_steps = stepCount,
                step_reports = new List<TestRunResult>()
            };

            float[] cpuEta = new float[N * N];
            float[] cpuU = new float[N * N];
            float[] cpuW = new float[N * N];
            for (int y = 0; y < N; y++) {
                float ny = (float)y / (N - 1);
                for (int x = 0; x < N; x++) {
                    float nx = (float)x / (N - 1);
                    int idx = y * N + x;
                    float dxDist = (nx - 0.5f) * 4f;
                    float dyDist = (ny - 0.4f) * 4f;
                    cpuEta[idx] = 0.35f * math.exp(-(dxDist * dxDist + dyDist * dyDist));
                    cpuU[idx] = (x == 0 || x == N - 1) ? 0f : 0.05f * math.cos(nx * math.PI * 2f);
                    cpuW[idx] = (y == 0 || y == N - 1) ? 0f : 0.10f * math.sin(ny * math.PI * 2f);
                }
            }

            var rts = AllocateTextures(N);
            PopulateTextures(N, cpuEta, cpuU, cpuW, depth, rts);

            int kPredict = shader.FindKernel("CSPredict");
            int kTridiagX = shader.FindKernel("CSTridiagonalX");
            int kTridiagZ = shader.FindKernel("CSTridiagonalZ");
            int kApplyPredDisp = shader.FindKernel("CSApplyPredictedDispersion");
            int kCorrect = shader.FindKernel("CSCorrect");
            int kApplyDisp = shader.FindKernel("CSApplyDispersion");
            int kFilterX = shader.FindKernel("CSFilterX");
            int kFilterZ = shader.FindKernel("CSFilterZ");

            float phase = 0f;
            bool allPassed = true;

            for (int s = 0; s < stepCount; s++) {
                int parity = s % 2;
                BindShaderUniforms(shader, N, dx, dz, dt, g, alpha, frictionCoeff, parity, waveAmp, waveFreq, phase);

                // GPU Step:
                // 1. Predict
                shader.SetTexture(kPredict, "_StateIn", rts.stateInRT);
                shader.SetTexture(kPredict, "_StatePred", rts.statePredRT);
                shader.SetTexture(kPredict, "_BathymetryTex", rts.bathyRT);
                shader.SetTexture(kPredict, "_ForceOrigTex", rts.forceOrigRT);
                shader.SetTexture(kPredict, "_ForceTex", rts.forceRT);
                shader.SetTexture(kPredict, "_AccelPing", rts.accelPingRT);
                shader.Dispatch(kPredict, N / 8, N / 8, 1);

                // 2. Predictor Dispersion
                shader.SetTexture(kTridiagX, "_BathymetryTex", rts.bathyRT);
                shader.SetTexture(kTridiagX, "_ForceTex", rts.forceRT);
                shader.SetTexture(kTridiagX, "_AccelPong", rts.accelPongRT);
                shader.Dispatch(kTridiagX, N, 1, 1);

                shader.SetTexture(kTridiagZ, "_BathymetryTex", rts.bathyRT);
                shader.SetTexture(kTridiagZ, "_AccelPong", rts.accelPongRT);
                shader.SetTexture(kTridiagZ, "_AccelPing", rts.accelPingRT);
                shader.Dispatch(kTridiagZ, N, 1, 1);

                // 3. Apply Predicted Dispersion
                shader.SetTexture(kApplyPredDisp, "_StatePred", rts.statePredRT);
                shader.SetTexture(kApplyPredDisp, "_AccelPing", rts.accelPingRT);
                shader.SetTexture(kApplyPredDisp, "_ForcePredTex", rts.forcePredRT);
                shader.Dispatch(kApplyPredDisp, N / 8, N / 8, 1);

                // 4. Correct
                shader.SetTexture(kCorrect, "_StateIn", rts.stateInRT);
                shader.SetTexture(kCorrect, "_StatePred", rts.statePredRT);
                shader.SetTexture(kCorrect, "_StateOut", rts.stateOutRT);
                shader.SetTexture(kCorrect, "_BathymetryTex", rts.bathyRT);
                shader.SetTexture(kCorrect, "_ForceOrigTex", rts.forceOrigRT);
                shader.SetTexture(kCorrect, "_ForceTex", rts.forceRT);
                shader.SetTexture(kCorrect, "_ForcePredTex", rts.forcePredRT);
                shader.SetTexture(kCorrect, "_AccelPing", rts.accelPingRT);
                shader.Dispatch(kCorrect, N / 8, N / 8, 1);

                // 5. Corrector Dispersion
                shader.SetTexture(kTridiagX, "_BathymetryTex", rts.bathyRT);
                shader.SetTexture(kTridiagX, "_ForceTex", rts.forceRT);
                shader.SetTexture(kTridiagX, "_AccelPong", rts.accelPongRT);
                shader.Dispatch(kTridiagX, N, 1, 1);

                shader.SetTexture(kTridiagZ, "_BathymetryTex", rts.bathyRT);
                shader.SetTexture(kTridiagZ, "_AccelPong", rts.accelPongRT);
                shader.SetTexture(kTridiagZ, "_AccelPing", rts.accelPingRT);
                shader.Dispatch(kTridiagZ, N, 1, 1);

                // 6. Apply Dispersion
                shader.SetTexture(kApplyDisp, "_StateIn", rts.stateOutRT);
                shader.SetTexture(kApplyDisp, "_StateOut", rts.statePredRT);
                shader.SetTexture(kApplyDisp, "_ForcePredTex", rts.forcePredRT);
                shader.SetTexture(kApplyDisp, "_AccelPing", rts.accelPingRT);
                shader.SetTexture(kApplyDisp, "_BathymetryTex", rts.bathyRT);
                shader.Dispatch(kApplyDisp, N / 8, N / 8, 1);

                // 7. CSFilterX (predTex -> stateInRT)
                shader.SetTexture(kFilterX, "_StateIn", rts.statePredRT);
                shader.SetTexture(kFilterX, "_StateOut", rts.stateInRT);
                shader.Dispatch(kFilterX, N / 8, N / 8, 1);

                // 8. CSFilterZ (stateInRT -> stateOutRT)
                shader.SetTexture(kFilterZ, "_StateIn", rts.stateInRT);
                shader.SetTexture(kFilterZ, "_StateOut", rts.stateOutRT);
                shader.Dispatch(kFilterZ, N / 8, N / 8, 1);

                var gpuState = ReadbackFloat4(rts.stateOutRT);

                // Zero-copy ping-pong pointer swap for next step
                var tmpRT = rts.stateInRT;
                rts.stateInRT = rts.stateOutRT;
                rts.stateOutRT = tmpRT;

                // CPU Step:
                int dirPred = (parity == 0) ? 1 : -1;
                float[] cpuEtaPred = new float[N * N];
                float[] cpuFxPred = new float[N * N];
                float[] cpuFzPred = new float[N * N];
                ComputeCpuPredictor(N, dx, dz, dt, g, frictionCoeff, depth, cpuEta, cpuU, cpuW,
                    dirPred, phase, waveAmp, waveFreq, cpuEtaPred, cpuFxPred, cpuFzPred);

                float2[] cpuAStar1 = SolveTridiagX(cpuFxPred, cpuFzPred, depth, N, dx, alpha, false);
                float2[] cpuA1 = SolveTridiagZ(cpuAStar1, depth, N, dz, alpha, false);

                float[] cpuUPred = new float[N * N];
                float[] cpuWPred = new float[N * N];
                ComputeCpuApplyPredDisp(N, dt, cpuEtaPred, cpuU, cpuW, cpuA1, cpuUPred, cpuWPred);

                int dirCorr = -dirPred;
                float nextPhase = phase + 2f * math.PI * waveFreq * dt;
                float[] cpuEtaNext = new float[N * N];
                float[] cpuFxStar = new float[N * N];
                float[] cpuFzStar = new float[N * N];
                ComputeCpuCorrector(N, dx, dz, dt, g, frictionCoeff, depth, cpuEta, cpuEtaPred, cpuUPred, cpuWPred,
                    dirCorr, nextPhase, waveAmp, waveFreq, cpuEtaNext, cpuFxStar, cpuFzStar);

                float2[] cpuAStar2 = SolveTridiagX(cpuFxStar, cpuFzStar, depth, N, dx, alpha, false);
                float2[] cpuA2 = SolveTridiagZ(cpuAStar2, depth, N, dz, alpha, false);

                float[] cpuUNext = new float[N * N];
                float[] cpuWNext = new float[N * N];
                ComputeCpuApplyDisp(N, dt, cpuU, cpuW, cpuA1, cpuA2, cpuUNext, cpuWNext);

                ApplyFilterX(cpuEtaNext, cpuUNext, cpuWNext, N, dx, dt, out var cpuEtaFilterX, out var cpuUFilterX, out var cpuWFilterX);
                ApplyFilterZ(cpuEtaFilterX, cpuUFilterX, cpuWFilterX, N, dz, dt, out cpuEta, out cpuU, out cpuW);

                phase = nextPhase;

                var stepRes = new TestRunResult {
                    suite_name = $"MultiStep_{s}",
                    parity = parity,
                    stages = new List<StageResult>()
                };
                CompareStage(stepRes.stages, $"Substep_{s}_EtaFinal", gpuState, cpuEta, 0, 1e-3f);
                CompareStage(stepRes.stages, $"Substep_{s}_UFinal", gpuState, cpuU, 1, 1e-3f);
                CompareStage(stepRes.stages, $"Substep_{s}_WFinal", gpuState, cpuW, 2, 1e-3f);

                float maxWallU = 0f, maxWallW = 0f;
                for (int y = 0; y < N; y++) {
                    maxWallU = math.max(maxWallU, math.abs(gpuState[y * N + 0].y));
                    maxWallU = math.max(maxWallU, math.abs(gpuState[y * N + N - 1].y));
                }
                for (int x = 0; x < N; x++) {
                    maxWallW = math.max(maxWallW, math.abs(gpuState[0 * N + x].z));
                    maxWallW = math.max(maxWallW, math.abs(gpuState[(N - 1) * N + x].z));
                }
                stepRes.max_lateral_wall_u = maxWallU;
                stepRes.max_transverse_wall_w = maxWallW;
                stepRes.wall_velocities_strictly_zero = (maxWallU == 0f && maxWallW == 0f);

                bool pass = true;
                foreach (var st in stepRes.stages) {
                    if (!st.passed) pass = false;
                }
                if (!stepRes.wall_velocities_strictly_zero) pass = false;
                stepRes.all_passed = pass;
                if (!pass) allPassed = false;

                multiRes.step_reports.Add(stepRes);
            }

            ReleaseTextures(ref rts);
            multiRes.all_passed = allPassed;
            return multiRes;
        }

        private static TestRunResult ExecuteFilterStandaloneTest(ComputeShader shader, int N, float dx, float dz, float dt) {
            var runRes = new TestRunResult {
                suite_name = "Standalone_Conservative_Filter_Suite",
                parity = 0,
                stages = new List<StageResult>()
            };

            float[] initEta = new float[N * N];
            float[] initU = new float[N * N];
            float[] initW = new float[N * N];
            float[] dummyDepth = new float[N * N];

            var rnd = new Unity.Mathematics.Random(42);
            for (int y = 0; y < N; y++) {
                float ny = (float)y / (N - 1);
                for (int x = 0; x < N; x++) {
                    float nx = (float)x / (N - 1);
                    int idx = y * N + x;
                    dummyDepth[idx] = 1.0f;

                    // Steep solitary peak in center (slope > 0.40)
                    float rCenter = math.sqrt((nx - 0.5f) * (nx - 0.5f) + (ny - 0.5f) * (ny - 0.5f));
                    float peak = 0.35f * math.exp(-math.pow(rCenter / 0.08f, 2f));

                    // Narrow bump
                    float rNarrow = math.sqrt((nx - 0.25f) * (nx - 0.25f) + (ny - 0.25f) * (ny - 0.25f));
                    float bump = 0.035f * math.exp(-math.pow(rNarrow / 0.04f, 2f));

                    // Noise
                    float noise = rnd.NextFloat(-0.01f, 0.01f);

                    initEta[idx] = peak + bump + noise;
                    initU[idx] = (x == 0 || x == N - 1) ? 0f : rnd.NextFloat(-0.1f, 0.1f);
                    initW[idx] = (y == 0 || y == N - 1) ? 0f : rnd.NextFloat(-0.1f, 0.1f);
                }
            }

            // Boundary impulses
            initEta[0] += 0.20f;
            initEta[N - 1] += 0.20f;
            initEta[(N - 1) * N + 0] += 0.20f;
            initEta[(N - 1) * N + N - 1] += 0.20f;

            var rts = AllocateTextures(N);
            PopulateTextures(N, initEta, initU, initW, dummyDepth, rts);
            BindShaderUniforms(shader, N, dx, dz, dt, 9.81f, 1.0f, 0f, 0, 0f, 0f, 0f);

            int kFilterX = shader.FindKernel("CSFilterX");
            int kFilterZ = shader.FindKernel("CSFilterZ");

            // GPU Pass 1: X (stateInRT -> statePredRT)
            shader.SetTexture(kFilterX, "_StateIn", rts.stateInRT);
            shader.SetTexture(kFilterX, "_StateOut", rts.statePredRT);
            shader.Dispatch(kFilterX, N / 8, N / 8, 1);

            // GPU Pass 2: Z (statePredRT -> stateOutRT)
            shader.SetTexture(kFilterZ, "_StateIn", rts.statePredRT);
            shader.SetTexture(kFilterZ, "_StateOut", rts.stateOutRT);
            shader.Dispatch(kFilterZ, N / 8, N / 8, 1);

            var gpuFinal = ReadbackFloat4(rts.stateOutRT);

            // CPU 2-pass
            ApplyFilterX(initEta, initU, initW, N, dx, dt, out var cpuEtaX, out var cpuUX, out var cpuWX);
            ApplyFilterZ(cpuEtaX, cpuUX, cpuWX, N, dz, dt, out var cpuEtaFinal, out var cpuUFinal, out var cpuWFinal);

            CompareStage(runRes.stages, "Standalone_Filter_Eta", gpuFinal, cpuEtaFinal, 0, 1e-4f);
            CompareStage(runRes.stages, "Standalone_Filter_U", gpuFinal, cpuUFinal, 1, 1e-4f);
            CompareStage(runRes.stages, "Standalone_Filter_W", gpuFinal, cpuWFinal, 2, 1e-4f);

            double massBefore = 0.0;
            double massAfterGpu = 0.0;
            bool hasNan = false;
            for (int i = 0; i < N * N; i++) {
                massBefore += initEta[i];
                massAfterGpu += gpuFinal[i].x;
                if (float.IsNaN(gpuFinal[i].x) || float.IsInfinity(gpuFinal[i].x) ||
                    float.IsNaN(gpuFinal[i].y) || float.IsInfinity(gpuFinal[i].y) ||
                    float.IsNaN(gpuFinal[i].z) || float.IsInfinity(gpuFinal[i].z)) {
                    hasNan = true;
                }
            }

            runRes.mass_before = (float)massBefore;
            runRes.mass_after = (float)massAfterGpu;
            runRes.mass_delta = (float)math.abs(massAfterGpu - massBefore);
            runRes.nan_free = !hasNan;

            float maxWallU = 0f, maxWallW = 0f;
            for (int y = 0; y < N; y++) {
                maxWallU = math.max(maxWallU, math.abs(gpuFinal[y * N + 0].y));
                maxWallU = math.max(maxWallU, math.abs(gpuFinal[y * N + N - 1].y));
            }
            for (int x = 0; x < N; x++) {
                maxWallW = math.max(maxWallW, math.abs(gpuFinal[0 * N + x].z));
                maxWallW = math.max(maxWallW, math.abs(gpuFinal[(N - 1) * N + x].z));
            }
            runRes.max_lateral_wall_u = maxWallU;
            runRes.max_transverse_wall_w = maxWallW;
            runRes.wall_velocities_strictly_zero = (maxWallU == 0f && maxWallW == 0f);

            bool pass = true;
            foreach (var st in runRes.stages) {
                if (!st.passed) pass = false;
            }
            if (runRes.mass_delta > 1e-5f) {
                Debug.LogError($"[ExecuteFilterStandaloneTest] Mass conservation failed: delta={runRes.mass_delta:E}");
                pass = false;
            }
            if (!runRes.wall_velocities_strictly_zero) pass = false;
            if (!runRes.nan_free) pass = false;
            runRes.all_passed = pass;

            ReleaseTextures(ref rts);
            return runRes;
        }

        private static TestRunResult ExecuteStepVsSubstepEquivalenceTest() {
            var runRes = new TestRunResult {
                suite_name = "Step_vs_Substep_Equivalence",
                parity = 0,
                stages = new List<StageResult>()
            };

            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/02_Boussinesq.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            var sim = UnityEngine.Object.FindFirstObjectByType<BoussinesqSimulation>();
            if (sim == null) {
                Debug.LogError("[ShoalGpuCpuVerifier] Scene 02 BoussinesqSimulation not found for equivalence test.");
                runRes.all_passed = false;
                return runRes;
            }

            // 1. Run via normal Step()
            sim.Pause();
            sim.ResetSimulation();

            float frameDt = 0.016f;
            int testFrames = 5;
            for (int f = 0; f < testFrames; f++) {
                sim.Step(frameDt);
            }

            var reqA = AsyncGPUReadback.Request(sim.CurrentStateTexture, 0, TextureFormat.RGBAFloat);
            reqA.WaitForCompletion();
            var dataA = reqA.GetData<float4>().ToArray();
            float simTimeA = sim.SimulationTime;
            bool pingPongA = sim.CurrentPingPong;
            int subStepCountA = sim.LastSubStepCount;

            // 2. Pollute state with arbitrary settings to simulate running other scenes / conditions
            sim.DispersionAlpha = 0.3f;
            sim.BedFriction = 0.05f;
            sim.Step(0.02f);

            // 3. Reset and run via StepSingleSubstep()
            sim.DispersionAlpha = 1.0f;
            sim.BedFriction = 0.0f;
            sim.ResetSimulation();

            float subDt = frameDt / subStepCountA;
            for (int f = 0; f < testFrames; f++) {
                for (int s = 0; s < subStepCountA; s++) {
                    sim.StepSingleSubstep(subDt);
                }
            }

            var reqB = AsyncGPUReadback.Request(sim.CurrentStateTexture, 0, TextureFormat.RGBAFloat);
            reqB.WaitForCompletion();
            var dataB = reqB.GetData<float4>().ToArray();
            float simTimeB = sim.SimulationTime;
            bool pingPongB = sim.CurrentPingPong;

            float maxErrEta = 0f, maxErrU = 0f, maxErrW = 0f;
            for (int i = 0; i < dataA.Length; i++) {
                maxErrEta = math.max(maxErrEta, math.abs(dataA[i].x - dataB[i].x));
                maxErrU   = math.max(maxErrU,   math.abs(dataA[i].y - dataB[i].y));
                maxErrW   = math.max(maxErrW,   math.abs(dataA[i].z - dataB[i].z));
            }

            float timeDiff = math.abs(simTimeA - simTimeB);
            bool pingPongMatch = (pingPongA == pingPongB);

            runRes.stages.Add(new StageResult("Equivalence_Eta", maxErrEta, 1e-5f));
            runRes.stages.Add(new StageResult("Equivalence_U",   maxErrU,   1e-5f));
            runRes.stages.Add(new StageResult("Equivalence_W",   maxErrW,   1e-5f));
            runRes.stages.Add(new StageResult("Equivalence_Time", timeDiff, 1e-6f));

            bool pass = (maxErrEta < 1e-5f) && (maxErrU < 1e-5f) && (maxErrW < 1e-5f) && (timeDiff < 1e-6f) && pingPongMatch;
            runRes.all_passed = pass;
            runRes.wall_velocities_strictly_zero = true;
            runRes.nan_free = true;

            return runRes;
        }

        private static void ComputeCpuPredictor(int N, float dx, float dz, float dt, float g, float frictionCoeff,
            float[] depth, float[] initEta, float[] initU, float[] initW, int dirPred,
            float wavePhase, float waveAmp, float waveFreq,
            float[] cpuEtaPred, float[] cpuFxPred, float[] cpuFzPred) {
            float invDx = (float)dirPred / dx;
            float invDz = (float)dirPred / dz;
            int waveY = (int)(N * 0.08f);

            for (int y = 0; y < N; y++) {
                int py = math.min(N - 1, math.max(0, y + dirPred));
                for (int x = 0; x < N; x++) {
                    int px = math.min(N - 1, math.max(0, x + dirPred));
                    int idxC = y * N + x;
                    int idxX = y * N + px;
                    int idxZ = py * N + x;

                    float hC = math.max(0.2f, depth[idxC]);
                    float hX = math.max(0.2f, depth[idxX]);
                    float hZ = math.max(0.2f, depth[idxZ]);

                    float totHC = SmoothDepth(hC + initEta[idxC]);
                    float totHX = SmoothDepth(hX + initEta[idxX]);
                    float totHZ = SmoothDepth(hZ + initEta[idxZ]);

                    float uC = initU[idxC], uX = initU[idxX];
                    float wC = initW[idxC], wZ = initW[idxZ];

                    float dHudx = (totHX * uX - totHC * uC) * invDx;
                    float dHwdz = (totHZ * wZ - totHC * wC) * invDz;

                    float srcEta = 0f;
                    if (waveAmp > 0.001f) {
                        float dy = (float)(y - waveY);
                        if (math.abs(dy) <= 18f) {
                            float sigmaCells = 4.0f;
                            float gaussian = math.exp(-0.5f * (dy * dy) / (sigmaCells * sigmaCells));
                            float invSqrt2Pi = 1f / (2.506628f * sigmaCells * dz);
                            float c = math.sqrt(g * math.max(0.5f, hC));
                            srcEta = 2f * c * waveAmp * math.sin(wavePhase) * gaussian * invSqrt2Pi;
                        }
                    }

                    cpuEtaPred[idxC] = initEta[idxC] - dt * (dHudx + dHwdz) + dt * srcEta;

                    float detadx = (initEta[idxX] - initEta[idxC]) * invDx;
                    float detadz = (initEta[idxZ] - initEta[idxC]) * invDz;

                    int lx = math.max(0, x - 1);
                    int rx = math.min(N - 1, x + 1);
                    int dy_idx = math.max(0, y - 1);
                    int uy_idx = math.min(N - 1, y + 1);

                    float dudx = (uC >= 0f) ? (uC - initU[y * N + lx]) / dx : (initU[y * N + rx] - uC) / dx;
                    float dudz = (wC >= 0f) ? (uC - initU[dy_idx * N + x]) / dz : (initU[uy_idx * N + x] - uC) / dz;
                    float dwdx = (uC >= 0f) ? (wC - initW[y * N + lx]) / dx : (initW[y * N + rx] - wC) / dx;
                    float dwdz = (wC >= 0f) ? (wC - initW[dy_idx * N + x]) / dz : (initW[uy_idx * N + x] - wC) / dz;

                    float advX = uC * dudx + wC * dudz;
                    float advZ = uC * dwdx + wC * dwdz;
                    float fric = frictionCoeff / totHC;

                    cpuFxPred[idxC] = -g * detadx - advX - fric * uC;
                    cpuFzPred[idxC] = -g * detadz - advZ - fric * wC;
                }
            }
        }

        private static void ComputeCpuApplyPredDisp(int N, float dt, float[] cpuEtaPred, float[] initU, float[] initW,
            float2[] cpuA1, float[] cpuUPred, float[] cpuWPred) {
            for (int y = 0; y < N; y++) {
                float distY = math.min(y, N - 1 - y);
                float damp = (distY < 4f) ? (1f / (1f + math.pow(1f - distY / 4f, 2f) * 3f * dt * 15f)) : 1f;
                for (int x = 0; x < N; x++) {
                    int i = y * N + x;
                    cpuEtaPred[i] = math.clamp(cpuEtaPred[i] * damp, -15f, 15f);
                    float up = (initU[i] + dt * cpuA1[i].x) * damp;
                    float wp = (initW[i] + dt * cpuA1[i].y) * damp;
                    cpuUPred[i] = math.clamp(up, -30f, 30f);
                    cpuWPred[i] = math.clamp(wp, -30f, 30f);
                }
            }
        }

        private static void ComputeCpuCorrector(int N, float dx, float dz, float dt, float g, float frictionCoeff,
            float[] depth, float[] initEta, float[] etaStar, float[] uStar, float[] wStar, int dirCorr,
            float nextWavePhase, float waveAmp, float waveFreq,
            float[] cpuEtaNext, float[] cpuFxStar, float[] cpuFzStar) {
            float invDx = (float)dirCorr / dx;
            float invDz = (float)dirCorr / dz;
            int waveY = (int)(N * 0.08f);

            for (int y = 0; y < N; y++) {
                int py = math.min(N - 1, math.max(0, y + dirCorr));
                for (int x = 0; x < N; x++) {
                    int px = math.min(N - 1, math.max(0, x + dirCorr));
                    int idxC = y * N + x;
                    int idxX = y * N + px;
                    int idxZ = py * N + x;

                    float hC = math.max(0.2f, depth[idxC]);
                    float hX = math.max(0.2f, depth[idxX]);
                    float hZ = math.max(0.2f, depth[idxZ]);

                    float totHC = SmoothDepth(hC + etaStar[idxC]);
                    float totHX = SmoothDepth(hX + etaStar[idxX]);
                    float totHZ = SmoothDepth(hZ + etaStar[idxZ]);

                    float uC = uStar[idxC], uX = uStar[idxX];
                    float wC = wStar[idxC], wZ = wStar[idxZ];

                    float dHudxStar = (totHX * uX - totHC * uC) * invDx;
                    float dHwdzStar = (totHZ * wZ - totHC * wC) * invDz;

                    float srcEtaStar = 0f;
                    if (waveAmp > 0.001f) {
                        float dy = (float)(y - waveY);
                        if (math.abs(dy) <= 18f) {
                            float sigmaCells = 4.0f;
                            float gaussian = math.exp(-0.5f * (dy * dy) / (sigmaCells * sigmaCells));
                            float invSqrt2Pi = 1f / (2.506628f * sigmaCells * dz);
                            float c = math.sqrt(g * math.max(0.5f, hC));
                            srcEtaStar = 2f * c * waveAmp * math.sin(nextWavePhase) * gaussian * invSqrt2Pi;
                        }
                    }

                    float rawNext = 0.5f * (initEta[idxC] + etaStar[idxC] - dt * (dHudxStar + dHwdzStar) + dt * srcEtaStar);
                    float distY = math.min(y, N - 1 - y);
                    float damp = (distY < 4f) ? (1f / (1f + math.pow(1f - distY / 4f, 2f) * 3f * dt * 15f)) : 1f;
                    cpuEtaNext[idxC] = math.clamp(rawNext * damp, -15f, 15f);

                    float detadxStar = (etaStar[idxX] - etaStar[idxC]) * invDx;
                    float detadzStar = (etaStar[idxZ] - etaStar[idxC]) * invDz;

                    int lx = math.max(0, x - 1);
                    int rx = math.min(N - 1, x + 1);
                    int dy_idx = math.max(0, y - 1);
                    int uy_idx = math.min(N - 1, y + 1);

                    float dudxStar = (uC >= 0f) ? (uC - uStar[y * N + lx]) / dx : (uStar[y * N + rx] - uC) / dx;
                    float dudzStar = (wC >= 0f) ? (uC - uStar[dy_idx * N + x]) / dz : (uStar[uy_idx * N + x] - uC) / dz;
                    float dwdxStar = (uC >= 0f) ? (wC - wStar[y * N + lx]) / dx : (wStar[y * N + rx] - wC) / dx;
                    float dwdzStar = (wC >= 0f) ? (wC - wStar[dy_idx * N + x]) / dz : (wStar[uy_idx * N + x] - wC) / dz;

                    float advXStar = uC * dudxStar + wC * dudzStar;
                    float advZStar = uC * dwdxStar + wC * dwdzStar;
                    float fricStar = frictionCoeff / totHC;

                    cpuFxStar[idxC] = -g * detadxStar - advXStar - fricStar * uC;
                    cpuFzStar[idxC] = -g * detadzStar - advZStar - fricStar * wC;
                }
            }
        }

        private static void ComputeCpuApplyDisp(int N, float dt, float[] initU, float[] initW,
            float2[] cpuA1, float2[] cpuA2, float[] cpuUNext, float[] cpuWNext) {
            for (int y = 0; y < N; y++) {
                float distY = math.min(y, N - 1 - y);
                float damp = (distY < 4f) ? (1f / (1f + math.pow(1f - distY / 4f, 2f) * 3f * dt * 15f)) : 1f;
                for (int x = 0; x < N; x++) {
                    int i = y * N + x;
                    float2 aAvg = 0.5f * (cpuA1[i] + cpuA2[i]);
                    float un = (initU[i] + dt * aAvg.x) * damp;
                    float wn = (initW[i] + dt * aAvg.y) * damp;
                    if (x == 0 || x == N - 1) un = 0f;
                    if (y == 0 || y == N - 1) wn = 0f;
                    cpuUNext[i] = math.clamp(un, -30f, 30f);
                    cpuWNext[i] = math.clamp(wn, -30f, 30f);
                }
            }
        }

        private struct FaceFlux3 {
            public float3 f2;
            public float3 f4;
        }

        private struct CellRatio3 {
            public float3 rp;
            public float3 rm;
        }

        private static FaceFlux3 GetCpuFaceFluxX(int i, int y, int N, float[] eta, float[] u, float[] w, float dx, float dt) {
            var res = new FaceFlux3 { f2 = float3.zero, f4 = float3.zero };
            if (i < 0 || i >= N - 1) return res;

            int idx_i = y * N + i;
            int idx_ip1 = y * N + i + 1;
            float3 s_i = new float3(eta[idx_i], u[idx_i], w[idx_i]);
            float3 s_ip1 = new float3(eta[idx_ip1], u[idx_ip1], w[idx_ip1]);

            float slopeX = math.abs(s_ip1.x - s_i.x) / dx;
            float excessSlopeX = math.max(0f, slopeX - 0.40f);
            float slopeWeightX = math.saturate(excessSlopeX * 3.0f);
            float kappa2X = slopeWeightX * math.min(0.20f, 30.0f * dt);
            float kappa4X = math.min(0.058f, 12.0f * dt);

            res.f2 = -kappa2X * (s_ip1 - s_i);

            if (i == 0) {
                int idx_2 = y * N + 2;
                float3 s_2 = new float3(eta[idx_2], u[idx_2], w[idx_2]);
                res.f4 = kappa4X * (2f * s_i - 3f * s_ip1 + s_2);
            } else if (i == N - 2) {
                int idx_im1 = y * N + (N - 3);
                float3 s_im1 = new float3(eta[idx_im1], u[idx_im1], w[idx_im1]);
                res.f4 = kappa4X * (-s_im1 + 3f * s_i - 2f * s_ip1);
            } else {
                int idx_im1 = y * N + (i - 1);
                int idx_ip2 = y * N + (i + 2);
                float3 s_im1 = new float3(eta[idx_im1], u[idx_im1], w[idx_im1]);
                float3 s_ip2 = new float3(eta[idx_ip2], u[idx_ip2], w[idx_ip2]);
                res.f4 = kappa4X * (-s_im1 + 3f * s_i - 3f * s_ip1 + s_ip2);
            }
            return res;
        }

        private static CellRatio3 GetCpuCellRatioX(int k, int y, int N, float[] eta, float[] u, float[] w, FaceFlux3 fL, FaceFlux3 fR) {
            int idx_k = y * N + k;
            int idx_prev = y * N + math.max(0, k - 1);
            int idx_next = y * N + math.min(N - 1, k + 1);

            float3 s_k = new float3(eta[idx_k], u[idx_k], w[idx_k]);
            float3 s_prev = new float3(eta[idx_prev], u[idx_prev], w[idx_prev]);
            float3 s_next = new float3(eta[idx_next], u[idx_next], w[idx_next]);

            var r = new CellRatio3();
            for (int ch = 0; ch < 3; ch++) {
                float v_L = s_k[ch] - (fR.f2[ch] - fL.f2[ch]);
                float v_min = math.min(s_k[ch], math.min(s_prev[ch], s_next[ch]));
                float v_max = math.max(s_k[ch], math.max(s_prev[ch], s_next[ch]));

                float p_plus = math.max(0f, -fR.f4[ch]) + math.max(0f, fL.f4[ch]);
                float p_minus = math.max(0f, fR.f4[ch]) + math.max(0f, -fL.f4[ch]);
                float q_plus = math.max(0f, v_max - v_L);
                float q_minus = math.max(0f, v_L - v_min);

                r.rp[ch] = (p_plus > 1e-7f) ? math.min(1.0f, q_plus / p_plus) : 1.0f;
                r.rm[ch] = (p_minus > 1e-7f) ? math.min(1.0f, q_minus / p_minus) : 1.0f;
            }
            return r;
        }

        private static float3 LimitCpuFlux3(FaceFlux3 f, CellRatio3 rL, CellRatio3 rR) {
            float3 fl = float3.zero;
            for (int ch = 0; ch < 3; ch++) {
                float a = f.f4[ch];
                float c = (a > 0f) ? math.min(rL.rm[ch], rR.rp[ch]) : math.min(rL.rp[ch], rR.rm[ch]);
                fl[ch] = f.f2[ch] + c * a;
            }
            return fl;
        }

        private static void ApplyFilterX(float[] etaIn, float[] uIn, float[] wIn, int N, float dx, float dt,
            out float[] etaOut, out float[] uOut, out float[] wOut) {
            etaOut = new float[N * N];
            uOut = new float[N * N];
            wOut = new float[N * N];

            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    var f_m3 = GetCpuFaceFluxX(x - 2, y, N, etaIn, uIn, wIn, dx, dt);
                    var f_m1 = GetCpuFaceFluxX(x - 1, y, N, etaIn, uIn, wIn, dx, dt);
                    var f_p1 = GetCpuFaceFluxX(x, y, N, etaIn, uIn, wIn, dx, dt);
                    var f_p3 = GetCpuFaceFluxX(x + 1, y, N, etaIn, uIn, wIn, dx, dt);

                    var r_m1 = (x > 0) ? GetCpuCellRatioX(x - 1, y, N, etaIn, uIn, wIn, f_m3, f_m1) : new CellRatio3 { rp = new float3(1f), rm = new float3(1f) };
                    var r_0  = GetCpuCellRatioX(x, y, N, etaIn, uIn, wIn, f_m1, f_p1);
                    var r_p1 = (x < N - 1) ? GetCpuCellRatioX(x + 1, y, N, etaIn, uIn, wIn, f_p1, f_p3) : new CellRatio3 { rp = new float3(1f), rm = new float3(1f) };

                    float3 f_L = (x == 0) ? float3.zero : LimitCpuFlux3(f_m1, r_m1, r_0);
                    float3 f_R = (x == N - 1) ? float3.zero : LimitCpuFlux3(f_p1, r_0, r_p1);

                    int idx = y * N + x;
                    float3 s = new float3(etaIn[idx], uIn[idx], wIn[idx]);
                    float3 filtered = s - (f_R - f_L);

                    if (x == 0 || x == N - 1) {
                        filtered.y = 0f; // lateral solid walls
                    }

                    etaOut[idx] = math.clamp(filtered.x, -15f, 15f);
                    uOut[idx] = math.clamp(filtered.y, -30f, 30f);
                    wOut[idx] = math.clamp(filtered.z, -30f, 30f);
                }
            }
        }

        private static FaceFlux3 GetCpuFaceFluxZ(int j, int x, int N, float[] eta, float[] u, float[] w, float dz, float dt) {
            var res = new FaceFlux3 { f2 = float3.zero, f4 = float3.zero };
            if (j < 0 || j >= N - 1) return res;

            int idx_j = j * N + x;
            int idx_jp1 = (j + 1) * N + x;
            float3 s_j = new float3(eta[idx_j], u[idx_j], w[idx_j]);
            float3 s_jp1 = new float3(eta[idx_jp1], u[idx_jp1], w[idx_jp1]);

            float slopeZ = math.abs(s_jp1.x - s_j.x) / dz;
            float excessSlopeZ = math.max(0f, slopeZ - 0.40f);
            float slopeWeightZ = math.saturate(excessSlopeZ * 3.0f);
            float kappa2Z = slopeWeightZ * math.min(0.20f, 30.0f * dt);
            float kappa4Z = math.min(0.058f, 12.0f * dt);

            res.f2 = -kappa2Z * (s_jp1 - s_j);

            if (j == 0) {
                int idx_2 = 2 * N + x;
                float3 s_2 = new float3(eta[idx_2], u[idx_2], w[idx_2]);
                res.f4 = kappa4Z * (2f * s_j - 3f * s_jp1 + s_2);
            } else if (j == N - 2) {
                int idx_jm1 = (N - 3) * N + x;
                float3 s_jm1 = new float3(eta[idx_jm1], u[idx_jm1], w[idx_jm1]);
                res.f4 = kappa4Z * (-s_jm1 + 3f * s_j - 2f * s_jp1);
            } else {
                int idx_jm1 = (j - 1) * N + x;
                int idx_jp2 = (j + 2) * N + x;
                float3 s_jm1 = new float3(eta[idx_jm1], u[idx_jm1], w[idx_jm1]);
                float3 s_jp2 = new float3(eta[idx_jp2], u[idx_jp2], w[idx_jp2]);
                res.f4 = kappa4Z * (-s_jm1 + 3f * s_j - 3f * s_jp1 + s_jp2);
            }
            return res;
        }

        private static CellRatio3 GetCpuCellRatioZ(int k, int x, int N, float[] eta, float[] u, float[] w, FaceFlux3 fD, FaceFlux3 fU) {
            int idx_k = k * N + x;
            int idx_prev = math.max(0, k - 1) * N + x;
            int idx_next = math.min(N - 1, k + 1) * N + x;

            float3 s_k = new float3(eta[idx_k], u[idx_k], w[idx_k]);
            float3 s_prev = new float3(eta[idx_prev], u[idx_prev], w[idx_prev]);
            float3 s_next = new float3(eta[idx_next], u[idx_next], w[idx_next]);

            var r = new CellRatio3();
            for (int ch = 0; ch < 3; ch++) {
                float v_L = s_k[ch] - (fU.f2[ch] - fD.f2[ch]);
                float v_min = math.min(s_k[ch], math.min(s_prev[ch], s_next[ch]));
                float v_max = math.max(s_k[ch], math.max(s_prev[ch], s_next[ch]));

                float p_plus = math.max(0f, -fU.f4[ch]) + math.max(0f, fD.f4[ch]);
                float p_minus = math.max(0f, fU.f4[ch]) + math.max(0f, -fD.f4[ch]);
                float q_plus = math.max(0f, v_max - v_L);
                float q_minus = math.max(0f, v_L - v_min);

                r.rp[ch] = (p_plus > 1e-7f) ? math.min(1.0f, q_plus / p_plus) : 1.0f;
                r.rm[ch] = (p_minus > 1e-7f) ? math.min(1.0f, q_minus / p_minus) : 1.0f;
            }
            return r;
        }

        private static void ApplyFilterZ(float[] etaIn, float[] uIn, float[] wIn, int N, float dz, float dt,
            out float[] etaOut, out float[] uOut, out float[] wOut) {
            etaOut = new float[N * N];
            uOut = new float[N * N];
            wOut = new float[N * N];

            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    var f_m3 = GetCpuFaceFluxZ(y - 2, x, N, etaIn, uIn, wIn, dz, dt);
                    var f_m1 = GetCpuFaceFluxZ(y - 1, x, N, etaIn, uIn, wIn, dz, dt);
                    var f_p1 = GetCpuFaceFluxZ(y, x, N, etaIn, uIn, wIn, dz, dt);
                    var f_p3 = GetCpuFaceFluxZ(y + 1, x, N, etaIn, uIn, wIn, dz, dt);

                    var r_m1 = (y > 0) ? GetCpuCellRatioZ(y - 1, x, N, etaIn, uIn, wIn, f_m3, f_m1) : new CellRatio3 { rp = new float3(1f), rm = new float3(1f) };
                    var r_0  = GetCpuCellRatioZ(y, x, N, etaIn, uIn, wIn, f_m1, f_p1);
                    var r_p1 = (y < N - 1) ? GetCpuCellRatioZ(y + 1, x, N, etaIn, uIn, wIn, f_p1, f_p3) : new CellRatio3 { rp = new float3(1f), rm = new float3(1f) };

                    float3 f_D = (y == 0) ? float3.zero : LimitCpuFlux3(f_m1, r_m1, r_0);
                    float3 f_U = (y == N - 1) ? float3.zero : LimitCpuFlux3(f_p1, r_0, r_p1);

                    int idx = y * N + x;
                    float3 s = new float3(etaIn[idx], uIn[idx], wIn[idx]);
                    float3 filtered = s - (f_U - f_D);

                    if (y == 0 || y == N - 1) {
                        filtered.z = 0f; // offshore / beach solid borders
                    }

                    etaOut[idx] = math.clamp(filtered.x, -15f, 15f);
                    uOut[idx] = math.clamp(filtered.y, -30f, 30f);
                    wOut[idx] = math.clamp(filtered.z, -30f, 30f);
                }
            }
        }

        private static float SmoothDepth(float rawH) {
            float d = rawH - 0.05f;
            return 0.05f + 0.5f * (d + math.sqrt(d * d + 0.0004f));
        }

        private static float2[] SolveTridiagX(float[] fx, float[] fz, float[] depth, int N, float dx, float alpha, bool sideWallSponge) {
            float2[] res = new float2[N * N];
            float invDx2 = alpha / (dx * dx);

            for (int y = 0; y < N; y++) {
                int r = y * N;
                if (y == 0 || y == N - 1) continue;

                float2[] a = new float2[N];
                float2[] b = new float2[N];
                float2[] c = new float2[N];
                float2[] d = new float2[N];

                float h0 = depth[r + 0];
                float beta0 = invDx2 * ((1f / 3f) * h0 * h0);
                float hE = depth[r + N - 1];
                float betaE = invDx2 * ((1f / 3f) * hE * hE);

                b[0] = new float2(1f, 1f + 2f * beta0);
                c[0] = new float2(0f, -2f * beta0);
                d[0] = new float2(0f, fz[r + 0]);

                a[N - 1] = new float2(0f, -2f * betaE);
                b[N - 1] = new float2(1f, 1f + 2f * betaE);
                d[N - 1] = new float2(0f, fz[r + N - 1]);

                for (int x = 1; x < N - 1; x++) {
                    float hC = depth[r + x];
                    float hL = depth[r + x - 1];
                    float hR = depth[r + x + 1];
                    float aVal = -invDx2 * (0.5f * hC * hL - (hC * hC) / 6f);
                    float bVal = 1f + invDx2 * ((2f / 3f) * hC * hC);
                    float cVal = -invDx2 * (0.5f * hC * hR - (hC * hC) / 6f);
                    a[x] = new float2(aVal, aVal);
                    b[x] = new float2(bVal, bVal);
                    c[x] = new float2(cVal, cVal);
                    d[x] = new float2(fx[r + x], fz[r + x]);
                }

                float2[] aCur = (float2[])a.Clone();
                float2[] bCur = (float2[])b.Clone();
                float2[] cCur = (float2[])c.Clone();
                float2[] dCur = (float2[])d.Clone();

                for (int s = 0; s < 9; s++) {
                    int stride = 1 << s;
                    float2[] aNew = new float2[N];
                    float2[] bNew = new float2[N];
                    float2[] cNew = new float2[N];
                    float2[] dNew = new float2[N];

                    for (int i = 0; i < N; i++) {
                        float2 k1 = float2.zero, cL = float2.zero, dL = float2.zero;
                        if (i >= stride) {
                            int idxL = i - stride;
                            k1 = aCur[i] / math.max(1e-8f, bCur[idxL]);
                            cL = cCur[idxL];
                            dL = dCur[idxL];
                            aNew[i] = -k1 * aCur[idxL];
                        }
                        float2 k2 = float2.zero, aR = float2.zero, dR = float2.zero;
                        if (i + stride < N) {
                            int idxR = i + stride;
                            k2 = cCur[i] / math.max(1e-8f, bCur[idxR]);
                            aR = aCur[idxR];
                            dR = dCur[idxR];
                            cNew[i] = -k2 * cCur[idxR];
                        }
                        bNew[i] = bCur[i] - k1 * cL - k2 * aR;
                        dNew[i] = dCur[i] - k1 * dL - k2 * dR;
                    }
                    aCur = aNew; bCur = bNew; cCur = cNew; dCur = dNew;
                }

                for (int x = 0; x < N; x++) {
                    float2 sol = dCur[x] / math.max(1e-8f, bCur[x]);
                    if (x == 0 || x == N - 1) sol.x = 0f;
                    res[r + x] = sol;
                }
            }
            return res;
        }

        private static float2[] SolveTridiagZ(float2[] aStar, float[] depth, int N, float dz, float alpha, bool sideWallSponge) {
            float2[] res = new float2[N * N];
            float invDz2 = alpha / (dz * dz);

            for (int x = 0; x < N; x++) {
                float2[] a = new float2[N];
                float2[] b = new float2[N];
                float2[] c = new float2[N];
                float2[] d = new float2[N];

                float h0 = depth[0 * N + x];
                float beta0 = invDz2 * ((1f / 3f) * h0 * h0);
                float hE = depth[(N - 1) * N + x];
                float betaE = invDz2 * ((1f / 3f) * hE * hE);

                b[0] = new float2(1f + 2f * beta0, 1f);
                c[0] = new float2(-2f * beta0, 0f);
                d[0] = new float2(aStar[0 * N + x].x, 0f);

                a[N - 1] = new float2(-2f * betaE, 0f);
                b[N - 1] = new float2(1f + 2f * betaE, 1f);
                d[N - 1] = new float2(aStar[(N - 1) * N + x].x, 0f);

                for (int y = 1; y < N - 1; y++) {
                    float hC = depth[y * N + x];
                    float hD = depth[(y - 1) * N + x];
                    float hU = depth[(y + 1) * N + x];
                    float aVal = -invDz2 * (0.5f * hC * hD - (hC * hC) / 6f);
                    float bVal = 1f + invDz2 * ((2f / 3f) * hC * hC);
                    float cVal = -invDz2 * (0.5f * hC * hU - (hC * hC) / 6f);
                    a[y] = new float2(aVal, aVal);
                    b[y] = new float2(bVal, bVal);
                    c[y] = new float2(cVal, cVal);
                    d[y] = aStar[y * N + x];
                }

                float2[] aCur = (float2[])a.Clone();
                float2[] bCur = (float2[])b.Clone();
                float2[] cCur = (float2[])c.Clone();
                float2[] dCur = (float2[])d.Clone();

                for (int s = 0; s < 9; s++) {
                    int stride = 1 << s;
                    float2[] aNew = new float2[N];
                    float2[] bNew = new float2[N];
                    float2[] cNew = new float2[N];
                    float2[] dNew = new float2[N];

                    for (int j = 0; j < N; j++) {
                        float2 k1 = float2.zero, cL = float2.zero, dL = float2.zero;
                        if (j >= stride) {
                            int idxL = j - stride;
                            k1 = aCur[j] / math.max(1e-8f, bCur[idxL]);
                            cL = cCur[idxL];
                            dL = dCur[idxL];
                            aNew[j] = -k1 * aCur[idxL];
                        }
                        float2 k2 = float2.zero, aR = float2.zero, dR = float2.zero;
                        if (j + stride < N) {
                            int idxR = j + stride;
                            k2 = cCur[j] / math.max(1e-8f, bCur[idxR]);
                            aR = aCur[idxR];
                            dR = dCur[idxR];
                            cNew[j] = -k2 * cCur[idxR];
                        }
                        bNew[j] = bCur[j] - k1 * cL - k2 * aR;
                        dNew[j] = dCur[j] - k1 * dL - k2 * dR;
                    }
                    aCur = aNew; bCur = bNew; cCur = cNew; dCur = dNew;
                }

                for (int y = 0; y < N; y++) {
                    float2 sol = dCur[y] / math.max(1e-8f, bCur[y]);
                    if (x == 0 || x == N - 1) sol.x = 0f;
                    if (y == 0 || y == N - 1) sol.y = 0f;
                    res[y * N + x] = sol;
                }
            }
            return res;
        }

        private static void CompareStage(List<StageResult> list, string name, NativeArray<float4> gpu, float[] cpu, int ch, float tol) {
            float maxErr = 0f;
            int maxIdx = 0;
            float gvAtMax = 0f, cvAtMax = 0f;
            for (int i = 0; i < cpu.Length; i++) {
                float gv = (ch == 0) ? gpu[i].x : ((ch == 1) ? gpu[i].y : ((ch == 2) ? gpu[i].z : gpu[i].w));
                float err = math.abs(gv - cpu[i]);
                if (err > maxErr) {
                    maxErr = err;
                    maxIdx = i;
                    gvAtMax = gv;
                    cvAtMax = cpu[i];
                }
            }
            if (maxErr > tol) {
                int N = (int)math.sqrt(cpu.Length);
                Debug.LogWarning($"[CompareStage Fail] {name}: maxErr={maxErr:F6} at (x={maxIdx % N}, y={maxIdx / N}), gpu={gvAtMax:F6}, cpu={cvAtMax:F6}");
            }
            list.Add(new StageResult(name, maxErr, tol));
        }

        private static void CompareStage(List<StageResult> list, string name, NativeArray<float2> gpu, float[] cpu, int ch, float tol) {
            float maxErr = 0f;
            int maxIdx = 0;
            float gvAtMax = 0f, cvAtMax = 0f;
            for (int i = 0; i < cpu.Length; i++) {
                float gv = (ch == 0) ? gpu[i].x : gpu[i].y;
                float err = math.abs(gv - cpu[i]);
                if (err > maxErr) {
                    maxErr = err;
                    maxIdx = i;
                    gvAtMax = gv;
                    cvAtMax = cpu[i];
                }
            }
            if (maxErr > tol) {
                int N = (int)math.sqrt(cpu.Length);
                Debug.LogWarning($"[CompareStage Fail] {name}: maxErr={maxErr:F6} at (x={maxIdx % N}, y={maxIdx / N}), gpu={gvAtMax:F6}, cpu={cvAtMax:F6}");
            }
            list.Add(new StageResult(name, maxErr, tol));
        }

        private static void CompareStage(List<StageResult> list, string name, NativeArray<float2> gpu, float2[] cpu, int ch, float tol) {
            float maxErr = 0f;
            int maxIdx = 0;
            float gvAtMax = 0f, cvAtMax = 0f;
            for (int i = 0; i < cpu.Length; i++) {
                float gv = (ch == 0) ? gpu[i].x : gpu[i].y;
                float cv = (ch == 0) ? cpu[i].x : cpu[i].y;
                float err = math.abs(gv - cv);
                if (err > maxErr) {
                    maxErr = err;
                    maxIdx = i;
                    gvAtMax = gv;
                    cvAtMax = cv;
                }
            }
            if (maxErr > tol) {
                int N = (int)math.sqrt(cpu.Length);
                Debug.LogWarning($"[CompareStage Fail] {name}: maxErr={maxErr:F6} at (x={maxIdx % N}, y={maxIdx / N}), gpu={gvAtMax:F6}, cpu={cvAtMax:F6}");
            }
            list.Add(new StageResult(name, maxErr, tol));
        }

        private static NativeArray<float4> ReadbackFloat4(RenderTexture rt) {
            var req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            return req.GetData<float4>();
        }

        private static NativeArray<float2> ReadbackFloat2(RenderTexture rt) {
            var req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGFloat);
            req.WaitForCompletion();
            return req.GetData<float2>();
        }

        private static RenderTextures AllocateTextures(int N) {
            return new RenderTextures {
                stateInRT = CreateRT(N, N, RenderTextureFormat.ARGBFloat),
                statePredRT = CreateRT(N, N, RenderTextureFormat.ARGBFloat),
                stateOutRT = CreateRT(N, N, RenderTextureFormat.ARGBFloat),
                forceOrigRT = CreateRT(N, N, RenderTextureFormat.RGFloat),
                forceRT = CreateRT(N, N, RenderTextureFormat.RGFloat),
                forcePredRT = CreateRT(N, N, RenderTextureFormat.RGFloat),
                accelPingRT = CreateRT(N, N, RenderTextureFormat.RGFloat),
                accelPongRT = CreateRT(N, N, RenderTextureFormat.RGFloat),
                bathyRT = CreateRT(N, N, RenderTextureFormat.ARGBHalf)
            };
        }

        private static void ReleaseTextures(ref RenderTextures rts) {
            ReleaseRT(ref rts.stateInRT);
            ReleaseRT(ref rts.statePredRT);
            ReleaseRT(ref rts.stateOutRT);
            ReleaseRT(ref rts.forceOrigRT);
            ReleaseRT(ref rts.forceRT);
            ReleaseRT(ref rts.forcePredRT);
            ReleaseRT(ref rts.accelPingRT);
            ReleaseRT(ref rts.accelPongRT);
            ReleaseRT(ref rts.bathyRT);
        }

        private static void PopulateTextures(int N, float[] eta, float[] u, float[] w, float[] depth, RenderTextures rts) {
            var stateTex = new Texture2D(N, N, TextureFormat.RGBAFloat, false);
            var bathyTex = new Texture2D(N, N, TextureFormat.RGBAHalf, false);
            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    int i = y * N + x;
                    stateTex.SetPixel(x, y, new Color(eta[i], u[i], w[i], 0f));
                    bathyTex.SetPixel(x, y, new Color(depth[i], -depth[i], 0f, 1f));
                }
            }
            stateTex.Apply();
            bathyTex.Apply();
            Graphics.Blit(stateTex, rts.stateInRT);
            Graphics.Blit(bathyTex, rts.bathyRT);
            UnityEngine.Object.DestroyImmediate(stateTex);
            UnityEngine.Object.DestroyImmediate(bathyTex);
        }

        private static void BindShaderUniforms(ComputeShader shader, int N, float dx, float dz, float dt,
            float g, float alpha, float friction, int parity, float waveAmp, float waveFreq, float wavePhase) {
            shader.SetInt("_Width", N);
            shader.SetInt("_Height", N);
            shader.SetInt("_WavemakerY", (int)(N * 0.08f));
            shader.SetFloat("_DeltaTime", dt);
            shader.SetFloat("_GridSpacing", dx);
            shader.SetFloat("_GridSpacingZ", dz);
            shader.SetFloat("_Gravity", g);
            shader.SetFloat("_DispersionAlpha", alpha);
            shader.SetFloat("_BedFriction", friction);
            shader.SetFloat("_SpongeWidth", 4f);
            shader.SetFloat("_SpongeStrength", 3f);
            shader.SetInt("_SideWallSponge", 0);
            shader.SetInt("_StepParity", parity);
            shader.SetFloat("_WavemakerAmp", waveAmp);
            shader.SetFloat("_WavemakerFreq", waveFreq);
            shader.SetFloat("_WavemakerPhase", wavePhase);
        }

        private static RenderTexture CreateRT(int w, int h, RenderTextureFormat fmt) {
            var rt = new RenderTexture(w, h, 0, fmt, RenderTextureReadWrite.Linear) {
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        private static void ReleaseRT(ref RenderTexture rt) {
            if (rt != null) {
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                rt = null;
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
        private struct RenderTextures {
            public RenderTexture stateInRT;
            public RenderTexture statePredRT;
            public RenderTexture stateOutRT;
            public RenderTexture forceOrigRT;
            public RenderTexture forceRT;
            public RenderTexture forcePredRT;
            public RenderTexture accelPingRT;
            public RenderTexture accelPongRT;
            public RenderTexture bathyRT;
        }

        [Serializable]
        public class VerifierReport {
            public string test;
            public string git_commit;
            public bool is_dirty;
            public string shader_hash;
            public int grid_size;
            public float dx;
            public float dz;
            public float dt;
            public float alpha;
            public float friction;
            public float wave_amplitude;
            public float wave_frequency;
            public List<TestRunResult> test_runs = new List<TestRunResult>();
            public List<MultiSubstepResult> multi_substep_runs = new List<MultiSubstepResult>();
            public bool overall_passed;
        }

        [Serializable]
        public class TestRunResult {
            public string suite_name;
            public int parity;
            public List<StageResult> stages;
            public float max_lateral_wall_u;
            public float max_transverse_wall_w;
            public bool wall_velocities_strictly_zero;
            public float mass_before;
            public float mass_after;
            public float mass_delta;
            public bool nan_free;
            public bool all_passed;
        }

        [Serializable]
        public class MultiSubstepResult {
            public int total_steps;
            public List<TestRunResult> step_reports;
            public bool all_passed;
        }

        [Serializable]
        public class StageResult {
            public string stage;
            public float max_abs_error;
            public float tolerance;
            public bool passed;
            public StageResult(string name, float err, float tol) {
                stage = name;
                max_abs_error = err;
                tolerance = tol;
                passed = err <= tol;
            }
        }
        #endregion
    }
}
