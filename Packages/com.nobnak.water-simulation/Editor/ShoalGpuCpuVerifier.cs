using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Executes a rigorous 1-substep GPU Compute Shader vs CPU continuum solver comparison
    /// across all intermediate pipeline stages on a non-uniform bathymetry grid.
    /// Verifies:
    /// 1. CSPredict (etaPred, fx, fz)
    /// 2. CSTridiagonalX & CSTridiagonalZ (a*)
    /// 3. CSApplyPredictedDispersion (uPred, wPred)
    /// 4. CSCorrect (etaNext, fxStar, fzStar)
    /// 5. CSTridiagonalX & CSTridiagonalZ (a^(2))
    /// 6. CSApplyDispersion (uNext, wNext)
    /// 7. CSFilterHighFrequency (filtered eta, u, w)
    /// </summary>
    public static class ShoalGpuCpuVerifier {
        private const string OutputJsonPath = "Docs/ShoalInvestigation/GpuCpuSubstepMatch.json";
        private const string TriggerFile = "Temp/run_shoal_verifier.trigger";
        private const string DoneMarker = "Temp/shoal_verifier_done.marker";

        [InitializeOnLoadMethod]
        private static void OnEditorInit() {
            EditorApplication.delayCall += () => {
                if (File.Exists(TriggerFile)) {
                    try {
                        File.Delete(TriggerFile);
                        if (File.Exists(DoneMarker)) File.Delete(DoneMarker);
                        Debug.Log("[ShoalGpuCpuVerifier] Trigger detected, executing 1-substep verification...");
                        RunVerification();
                        File.WriteAllText(DoneMarker, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                        EditorApplication.Exit(0);
                    } catch (Exception ex) {
                        Debug.LogError($"[ShoalGpuCpuVerifier] Trigger execution error: {ex}");
                        File.WriteAllText(DoneMarker, "ERROR: " + ex.ToString(), Encoding.UTF8);
                        EditorApplication.Exit(1);
                    }
                }
            };
        }

        public static void RunBatch() {
            RunVerification();
        }

        [MenuItem("Tools/Water Simulation/Verify GPU vs CPU 1-Substep", false, 53)]
        public static void RunVerification() {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            if (shader == null) {
                Debug.LogError("[ShoalGpuCpuVerifier] Boussinesq.compute not found.");
                return;
            }

            int kernelInit = shader.FindKernel("CSInit");
            int kernelPredict = shader.FindKernel("CSPredict");
            int kernelTridiagonalX = shader.FindKernel("CSTridiagonalX");
            int kernelTridiagonalZ = shader.FindKernel("CSTridiagonalZ");
            int kernelApplyPredictedDispersion = shader.FindKernel("CSApplyPredictedDispersion");
            int kernelCorrect = shader.FindKernel("CSCorrect");
            int kernelApplyDispersion = shader.FindKernel("CSApplyDispersion");
            int kernelFilter = shader.FindKernel("CSFilterHighFrequency");

            int N = 32;
            float L = 50.0f;
            float dx = L / N;
            float dz = L / N;
            float dt = 0.008f;
            float g = 9.81f;
            float alpha = 1.0f;
            float frictionCoeff = 0.005f;

            // Generate initial state and bathymetry
            float[] depth = new float[N * N];
            float[] initEta = new float[N * N];
            float[] initU = new float[N * N];
            float[] initW = new float[N * N];

            for (int y = 0; y < N; y++) {
                float ny = (float)y / (N - 1);
                for (int x = 0; x < N; x++) {
                    float nx = (float)x / (N - 1);
                    int idx = y * N + x;

                    // Sloping shoal depth from 3.5m to 0.8m
                    depth[idx] = math.max(0.5f, math.lerp(3.5f, 0.8f, ny) + 0.3f * math.sin(nx * math.PI * 2f));

                    // Gaussian wave bump
                    float dxDist = (nx - 0.5f) * 4f;
                    float dyDist = (ny - 0.4f) * 4f;
                    initEta[idx] = 0.35f * math.exp(-(dxDist * dxDist + dyDist * dyDist));
                    initU[idx] = 0.05f * math.cos(nx * math.PI * 2f);
                    initW[idx] = 0.10f * math.sin(ny * math.PI * 2f);
                }
            }

            // Create RenderTextures
            var stateInRT = CreateRT(N, N, RenderTextureFormat.ARGBFloat);
            var statePredRT = CreateRT(N, N, RenderTextureFormat.ARGBFloat);
            var stateOutRT = CreateRT(N, N, RenderTextureFormat.ARGBFloat);
            var forceOrigRT = CreateRT(N, N, RenderTextureFormat.RGFloat);
            var forceRT = CreateRT(N, N, RenderTextureFormat.RGFloat);
            var forcePredRT = CreateRT(N, N, RenderTextureFormat.RGFloat);
            var accelPingRT = CreateRT(N, N, RenderTextureFormat.RGFloat);
            var accelPongRT = CreateRT(N, N, RenderTextureFormat.RGFloat);
            var bathyRT = CreateRT(N, N, RenderTextureFormat.ARGBHalf);

            // Populate initial textures
            var initStateTex = new Texture2D(N, N, TextureFormat.RGBAFloat, false);
            var initBathyTex = new Texture2D(N, N, TextureFormat.RGBAHalf, false);
            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    int idx = y * N + x;
                    initStateTex.SetPixel(x, y, new Color(initEta[idx], initU[idx], initW[idx], 0f));
                    initBathyTex.SetPixel(x, y, new Color(depth[idx], -depth[idx], 0f, 1f));
                }
            }
            initStateTex.Apply();
            initBathyTex.Apply();
            Graphics.Blit(initStateTex, stateInRT);
            Graphics.Blit(initBathyTex, bathyRT);
            UnityEngine.Object.DestroyImmediate(initStateTex);
            UnityEngine.Object.DestroyImmediate(initBathyTex);

            // Set shader uniforms
            shader.SetInt("_Width", N);
            shader.SetInt("_Height", N);
            shader.SetFloat("_DeltaTime", dt);
            shader.SetFloat("_GridSpacing", dx);
            shader.SetFloat("_GridSpacingZ", dz);
            shader.SetFloat("_Gravity", g);
            shader.SetFloat("_DispersionAlpha", alpha);
            shader.SetFloat("_BedFriction", frictionCoeff);
            shader.SetFloat("_SpongeWidth", 4f);
            shader.SetFloat("_SpongeStrength", 3f);
            shader.SetInt("_SideWallSponge", 0);
            shader.SetInt("_StepParity", 0);
            shader.SetFloat("_WavemakerAmp", 0f);

            var stageResults = new List<StageResult>();

            // ==================== STAGE 1: Predictor ====================
            shader.SetTexture(kernelPredict, "_StateIn", stateInRT);
            shader.SetTexture(kernelPredict, "_StatePred", statePredRT);
            shader.SetTexture(kernelPredict, "_BathymetryTex", bathyRT);
            shader.SetTexture(kernelPredict, "_ForceOrigTex", forceOrigRT);
            shader.SetTexture(kernelPredict, "_ForceTex", forceRT);
            shader.SetTexture(kernelPredict, "_AccelPing", accelPingRT);
            shader.Dispatch(kernelPredict, N / 8, N / 8, 1);

            // Readback GPU Stage 1
            var predReq = AsyncGPUReadback.Request(statePredRT, 0, TextureFormat.RGBAFloat);
            var forceReq = AsyncGPUReadback.Request(forceRT, 0, TextureFormat.RGFloat);
            predReq.WaitForCompletion();
            forceReq.WaitForCompletion();
            var gpuStatePred = predReq.GetData<float4>();
            var gpuForcePred = forceReq.GetData<float2>();

            // CPU Predictor
            float[] cpuEtaPred = new float[N * N];
            float[] cpuFxPred = new float[N * N];
            float[] cpuFzPred = new float[N * N];
            int dirPred = 1; // StepParity == 0 -> forward difference
            float invDx = (float)dirPred / dx;
            float invDz = (float)dirPred / dz;

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

                    float uC = initU[idxC], uX = initU[idxX], uZ = initU[idxZ];
                    float wC = initW[idxC], wX = initW[idxX], wZ = initW[idxZ];

                    float dHudx = (totHX * uX - totHC * uC) * invDx;
                    float dHwdz = (totHZ * wZ - totHC * wC) * invDz;
                    cpuEtaPred[idxC] = initEta[idxC] - dt * (dHudx + dHwdz);

                    float detadx = (initEta[idxX] - initEta[idxC]) * invDx;
                    float detadz = (initEta[idxZ] - initEta[idxC]) * invDz;
                    float dudx = (uX - uC) * invDx;
                    float dudz = (uZ - uC) * invDz;
                    float dwdx = (wX - wC) * invDx;
                    float dwdz = (wZ - wC) * invDz;

                    float advX = uC * dudx + wC * dudz;
                    float advZ = uC * dwdx + wC * dwdz;
                    float fric = frictionCoeff / totHC;

                    cpuFxPred[idxC] = -g * detadx - advX - fric * uC;
                    cpuFzPred[idxC] = -g * detadz - advZ - fric * wC;
                }
            }

            // Compare Stage 1
            float errEtaPred = 0f, errFx = 0f, errFz = 0f;
            for (int i = 0; i < N * N; i++) {
                errEtaPred = math.max(errEtaPred, math.abs(gpuStatePred[i].x - cpuEtaPred[i]));
                errFx = math.max(errFx, math.abs(gpuForcePred[i].x - cpuFxPred[i]));
                errFz = math.max(errFz, math.abs(gpuForcePred[i].y - cpuFzPred[i]));
            }
            stageResults.Add(new StageResult("1_Predictor_Eta", errEtaPred, 1e-4f));
            stageResults.Add(new StageResult("1_Predictor_ForceX", errFx, 1e-4f));
            stageResults.Add(new StageResult("1_Predictor_ForceZ", errFz, 1e-4f));

            // ==================== STAGE 2 & 3: PCR Dispersion ====================
            shader.SetTexture(kernelTridiagonalX, "_BathymetryTex", bathyRT);
            shader.SetTexture(kernelTridiagonalX, "_ForceTex", forceRT);
            shader.SetTexture(kernelTridiagonalX, "_AccelPong", accelPongRT);
            shader.Dispatch(kernelTridiagonalX, N, 1, 1);

            shader.SetTexture(kernelTridiagonalZ, "_BathymetryTex", bathyRT);
            shader.SetTexture(kernelTridiagonalZ, "_AccelPong", accelPongRT);
            shader.SetTexture(kernelTridiagonalZ, "_AccelPing", accelPingRT);
            shader.Dispatch(kernelTridiagonalZ, N, 1, 1);

            var a1Req = AsyncGPUReadback.Request(accelPingRT, 0, TextureFormat.RGFloat);
            a1Req.WaitForCompletion();
            var gpuA1 = a1Req.GetData<float2>();

            // CPU Tridiagonal Solve
            float2[] cpuAStar = SolveTridiagX(cpuFxPred, cpuFzPred, depth, N, dx, alpha, false);
            float2[] cpuA1 = SolveTridiagZ(cpuAStar, depth, N, dz, alpha, false);

            float errA1x = 0f, errA1z = 0f;
            for (int i = 0; i < N * N; i++) {
                errA1x = math.max(errA1x, math.abs(gpuA1[i].x - cpuA1[i].x));
                errA1z = math.max(errA1z, math.abs(gpuA1[i].y - cpuA1[i].y));
            }
            stageResults.Add(new StageResult("2_Dispersion_Ax1", errA1x, 5e-4f));
            stageResults.Add(new StageResult("2_Dispersion_Az1", errA1z, 5e-4f));

            // ==================== STAGE 4: CSApplyPredictedDispersion ====================
            shader.SetTexture(kernelApplyPredictedDispersion, "_StatePred", statePredRT);
            shader.SetTexture(kernelApplyPredictedDispersion, "_AccelPing", accelPingRT);
            shader.SetTexture(kernelApplyPredictedDispersion, "_ForcePredTex", forcePredRT);
            shader.Dispatch(kernelApplyPredictedDispersion, N / 8, N / 8, 1);

            var applyPredReq = AsyncGPUReadback.Request(statePredRT, 0, TextureFormat.RGBAFloat);
            applyPredReq.WaitForCompletion();
            var gpuStatePredFinal = applyPredReq.GetData<float4>();

            float[] cpuUPred = new float[N * N];
            float[] cpuWPred = new float[N * N];
            float errUPred = 0f, errWPred = 0f;
            for (int y = 0; y < N; y++) {
                float distY = math.min(y, N - 1 - y);
                float damp = (distY < 4f) ? (1f / (1f + math.pow(1f - distY / 4f, 2f) * 3f * dt * 15f)) : 1f;
                for (int x = 0; x < N; x++) {
                    int i = y * N + x;
                    cpuUPred[i] = math.clamp((initU[i] + dt * cpuA1[i].x) * damp, -30f, 30f);
                    cpuWPred[i] = math.clamp((initW[i] + dt * cpuA1[i].y) * damp, -30f, 30f);
                    cpuEtaPred[i] = math.clamp(cpuEtaPred[i] * damp, -15f, 15f);
                    errUPred = math.max(errUPred, math.abs(gpuStatePredFinal[i].y - cpuUPred[i]));
                    errWPred = math.max(errWPred, math.abs(gpuStatePredFinal[i].z - cpuWPred[i]));
                }
            }
            stageResults.Add(new StageResult("3_ApplyPred_UPred", errUPred, 5e-4f));
            stageResults.Add(new StageResult("3_ApplyPred_WPred", errWPred, 5e-4f));

            // ==================== STAGE 5: CSCorrect ====================
            shader.SetTexture(kernelCorrect, "_StateIn", stateInRT);
            shader.SetTexture(kernelCorrect, "_StatePred", statePredRT);
            shader.SetTexture(kernelCorrect, "_StateOut", stateOutRT);
            shader.SetTexture(kernelCorrect, "_BathymetryTex", bathyRT);
            shader.SetTexture(kernelCorrect, "_ForceOrigTex", forceOrigRT);
            shader.SetTexture(kernelCorrect, "_ForceTex", forceRT);
            shader.SetTexture(kernelCorrect, "_ForcePredTex", forcePredRT);
            shader.SetTexture(kernelCorrect, "_AccelPing", accelPingRT);
            shader.Dispatch(kernelCorrect, N / 8, N / 8, 1);

            var corrReq = AsyncGPUReadback.Request(stateOutRT, 0, TextureFormat.RGBAFloat);
            var fStarReq = AsyncGPUReadback.Request(forceRT, 0, TextureFormat.RGFloat);
            corrReq.WaitForCompletion();
            fStarReq.WaitForCompletion();
            var gpuStateOut = corrReq.GetData<float4>();
            var gpuFStar = fStarReq.GetData<float2>();

            int dirCorr = -1; // Opposite difference
            float invDxC = (float)dirCorr / dx;
            float invDzC = (float)dirCorr / dz;

            float[] cpuEtaNext = new float[N * N];
            float[] cpuFxStar = new float[N * N];
            float[] cpuFzStar = new float[N * N];

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

                    float totHC = SmoothDepth(hC + cpuEtaPred[idxC]);
                    float totHX = SmoothDepth(hX + cpuEtaPred[idxX]);
                    float totHZ = SmoothDepth(hZ + cpuEtaPred[idxZ]);

                    float uC = cpuUPred[idxC], uX = cpuUPred[idxX], uZ = cpuUPred[idxZ];
                    float wC = cpuWPred[idxC], wX = cpuWPred[idxX], wZ = cpuWPred[idxZ];

                    float dHudx = (totHX * uX - totHC * uC) * invDxC;
                    float dHwdz = (totHZ * wZ - totHC * wC) * invDzC;

                    float distY = math.min(y, N - 1 - y);
                    float damp = (distY < 4f) ? (1f / (1f + math.pow(1f - distY / 4f, 2f) * 3f * dt * 15f)) : 1f;

                    cpuEtaNext[idxC] = math.clamp(0.5f * (initEta[idxC] + cpuEtaPred[idxC] - dt * (dHudx + dHwdz)) * damp, -15f, 15f);

                    float detadx = (cpuEtaPred[idxX] - cpuEtaPred[idxC]) * invDxC;
                    float detadz = (cpuEtaPred[idxZ] - cpuEtaPred[idxC]) * invDzC;
                    float dudx = (uX - uC) * invDxC;
                    float dudz = (uZ - uC) * invDzC;
                    float dwdx = (wX - wC) * invDxC;
                    float dwdz = (wZ - wC) * invDzC;

                    float advX = uC * dudx + wC * dudz;
                    float advZ = uC * dwdx + wC * dwdz;
                    float fric = frictionCoeff / totHC;

                    cpuFxStar[idxC] = -g * detadx - advX - fric * uC;
                    cpuFzStar[idxC] = -g * detadz - advZ - fric * wC;
                }
            }

            float errEtaNext = 0f, errFxStar = 0f, errFzStar = 0f;
            for (int i = 0; i < N * N; i++) {
                errEtaNext = math.max(errEtaNext, math.abs(gpuStateOut[i].x - cpuEtaNext[i]));
                errFxStar = math.max(errFxStar, math.abs(gpuFStar[i].x - cpuFxStar[i]));
                errFzStar = math.max(errFzStar, math.abs(gpuFStar[i].y - cpuFzStar[i]));
            }
            stageResults.Add(new StageResult("4_Corrector_EtaNext", errEtaNext, 1e-4f));
            stageResults.Add(new StageResult("4_Corrector_ForceXStar", errFxStar, 1e-4f));
            stageResults.Add(new StageResult("4_Corrector_ForceZStar", errFzStar, 1e-4f));

            // ==================== STAGE 6: Corrector Dispersion ====================
            shader.SetTexture(kernelTridiagonalX, "_BathymetryTex", bathyRT);
            shader.SetTexture(kernelTridiagonalX, "_ForceTex", forceRT);
            shader.SetTexture(kernelTridiagonalX, "_AccelPong", accelPongRT);
            shader.Dispatch(kernelTridiagonalX, N, 1, 1);

            shader.SetTexture(kernelTridiagonalZ, "_BathymetryTex", bathyRT);
            shader.SetTexture(kernelTridiagonalZ, "_AccelPong", accelPongRT);
            shader.SetTexture(kernelTridiagonalZ, "_AccelPing", accelPingRT);
            shader.Dispatch(kernelTridiagonalZ, N, 1, 1);

            float2[] cpuAStarCorr = SolveTridiagX(cpuFxStar, cpuFzStar, depth, N, dx, alpha, false);
            float2[] cpuA2 = SolveTridiagZ(cpuAStarCorr, depth, N, dz, alpha, false);

            // ==================== STAGE 7: CSApplyDispersion ====================
            shader.SetTexture(kernelApplyDispersion, "_StateIn", stateOutRT);
            shader.SetTexture(kernelApplyDispersion, "_StateOut", statePredRT);
            shader.SetTexture(kernelApplyDispersion, "_ForcePredTex", forcePredRT);
            shader.SetTexture(kernelApplyDispersion, "_AccelPing", accelPingRT);
            shader.SetTexture(kernelApplyDispersion, "_BathymetryTex", bathyRT);
            shader.Dispatch(kernelApplyDispersion, N / 8, N / 8, 1);

            var applyDispReq = AsyncGPUReadback.Request(statePredRT, 0, TextureFormat.RGBAFloat);
            applyDispReq.WaitForCompletion();
            var gpuStateDisp = applyDispReq.GetData<float4>();

            float[] cpuUNext = new float[N * N];
            float[] cpuWNext = new float[N * N];
            float errUNext = 0f, errWNext = 0f;
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
                    errUNext = math.max(errUNext, math.abs(gpuStateDisp[i].y - cpuUNext[i]));
                    errWNext = math.max(errWNext, math.abs(gpuStateDisp[i].z - cpuWNext[i]));
                }
            }
            stageResults.Add(new StageResult("5_ApplyDispersion_UNext", errUNext, 5e-4f));
            stageResults.Add(new StageResult("5_ApplyDispersion_WNext", errWNext, 5e-4f));

            // ==================== STAGE 8: CSFilterHighFrequency ====================
            shader.SetTexture(kernelFilter, "_StateIn", statePredRT);
            shader.SetTexture(kernelFilter, "_StateOut", stateOutRT);
            shader.Dispatch(kernelFilter, N / 8, N / 8, 1);

            var filterReq = AsyncGPUReadback.Request(stateOutRT, 0, TextureFormat.RGBAFloat);
            filterReq.WaitForCompletion();
            var gpuFinal = filterReq.GetData<float4>();

            // CPU Invariant Biharmonic Filter
            float kappa = math.min(0.031f, 5.0f * dt);
            float[] cpuEtaFinal = ApplyFilter1D_2D(cpuEtaNext, N, kappa);
            float[] cpuUFinal   = ApplyFilter1D_2D(cpuUNext, N, kappa);
            float[] cpuWFinal   = ApplyFilter1D_2D(cpuWNext, N, kappa);

            float errEtaFinal = 0f, errUFinal = 0f, errWFinal = 0f;
            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    int i = y * N + x;
                    if (x == 0 || x == N - 1) cpuUFinal[i] = 0f;
                    if (y == 0 || y == N - 1) cpuWFinal[i] = 0f;
                    errEtaFinal = math.max(errEtaFinal, math.abs(gpuFinal[i].x - cpuEtaFinal[i]));
                    errUFinal = math.max(errUFinal, math.abs(gpuFinal[i].y - cpuUFinal[i]));
                    errWFinal = math.max(errWFinal, math.abs(gpuFinal[i].z - cpuWFinal[i]));
                }
            }
            stageResults.Add(new StageResult("6_Filter_EtaFinal", errEtaFinal, 1e-4f));
            stageResults.Add(new StageResult("6_Filter_UFinal", errUFinal, 5e-4f));
            stageResults.Add(new StageResult("6_Filter_WFinal", errWFinal, 5e-4f));

            // Clean up RTs
            ReleaseRT(ref stateInRT);
            ReleaseRT(ref statePredRT);
            ReleaseRT(ref stateOutRT);
            ReleaseRT(ref forceOrigRT);
            ReleaseRT(ref forceRT);
            ReleaseRT(ref forcePredRT);
            ReleaseRT(ref accelPingRT);
            ReleaseRT(ref accelPongRT);
            ReleaseRT(ref bathyRT);

            // Serialize report
            bool allPassed = true;
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"test\": \"GPU Compute Shader vs CPU Continuum Solver 1-Substep Match\",");
            sb.AppendLine($"  \"grid_size\": {N},");
            sb.AppendLine($"  \"dx\": {dx:F4},");
            sb.AppendLine($"  \"dt\": {dt:F5},");
            sb.AppendLine("  \"stages\": [");
            for (int i = 0; i < stageResults.Count; i++) {
                var sr = stageResults[i];
                if (!sr.passed) allPassed = false;
                sb.AppendLine($"    {{ \"stage\": \"{sr.name}\", \"max_abs_error\": {sr.maxError:E3}, \"tolerance\": {sr.tolerance:E3}, \"passed\": {sr.passed.ToString().ToLowerInvariant()} }}{(i < stageResults.Count - 1 ? "," : "")}");
            }
            sb.AppendLine("  ],");
            sb.AppendLine($"  \"all_passed\": {allPassed.ToString().ToLowerInvariant()}");
            sb.AppendLine("}");

            string outDir = Path.GetDirectoryName(OutputJsonPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(OutputJsonPath, sb.ToString(), Encoding.UTF8);

            Debug.Log($"[ShoalGpuCpuVerifier] Verification Complete! All Passed = {allPassed}\nSaved to: {OutputJsonPath}");
            if (!allPassed) throw new Exception("One or more stages failed GPU-CPU equivalence check.");
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

        private static float SmoothDepth(float rawH) {
            float d = rawH - 0.05f;
            return 0.05f + 0.5f * (d + math.sqrt(d * d + 0.0004f));
        }

        private static float2[] SolveTridiagX(float[] fx, float[] fz, float[] depth, int N, float dx, float alpha, bool sideWallSponge) {
            float2[] res = new float2[N * N];
            float invDx2 = alpha / (dx * dx);

            for (int y = 0; y < N; y++) {
                int r = y * N;
                if (y == 0 || y == N - 1) {
                    continue;
                }

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

                // PCR simulation on CPU to match shader PCR rounding identically
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

        private static float[] ApplyFilter1D_2D(float[] field, int N, float kappa) {
            float[] d4x = new float[N * N];
            for (int y = 0; y < N; y++) {
                int r = y * N;
                for (int x = 0; x < N; x++) {
                    if (x == 0) d4x[r + 0] = 2f * field[r + 0] - 3f * field[r + 1] + field[r + 2];
                    else if (x == 1) d4x[r + 1] = -3f * field[r + 0] + 6f * field[r + 1] - 4f * field[r + 2] + field[r + 3];
                    else if (x == N - 2) d4x[r + N - 2] = field[r + N - 4] - 4f * field[r + N - 3] + 6f * field[r + N - 2] - 3f * field[r + N - 1];
                    else if (x == N - 1) d4x[r + N - 1] = field[r + N - 3] - 3f * field[r + N - 2] + 2f * field[r + N - 1];
                    else d4x[r + x] = field[r + x - 2] - 4f * field[r + x - 1] + 6f * field[r + x] - 4f * field[r + x + 1] + field[r + x + 2];
                }
            }

            float[] d4z = new float[N * N];
            for (int x = 0; x < N; x++) {
                for (int y = 0; y < N; y++) {
                    if (y == 0) d4z[0 * N + x] = 2f * field[0 * N + x] - 3f * field[1 * N + x] + field[2 * N + x];
                    else if (y == 1) d4z[1 * N + x] = -3f * field[0 * N + x] + 6f * field[1 * N + x] - 4f * field[2 * N + x] + field[3 * N + x];
                    else if (y == N - 2) d4z[(N - 2) * N + x] = field[(N - 4) * N + x] - 4f * field[(N - 3) * N + x] + 6f * field[(N - 2) * N + x] - 3f * field[(N - 1) * N + x];
                    else if (y == N - 1) d4z[(N - 1) * N + x] = field[(N - 3) * N + x] - 3f * field[(N - 2) * N + x] + 2f * field[(N - 1) * N + x];
                    else d4z[y * N + x] = field[(y - 2) * N + x] - 4f * field[(y - 1) * N + x] + 6f * field[y * N + x] - 4f * field[(y + 1) * N + x] + field[(y + 2) * N + x];
                }
            }

            float[] res = new float[N * N];
            for (int i = 0; i < N * N; i++) {
                res[i] = field[i] - kappa * (d4x[i] + d4z[i]);
            }
            return res;
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

        private class StageResult {
            public string name;
            public float maxError;
            public float tolerance;
            public bool passed;
            public StageResult(string n, float err, float tol) {
                name = n;
                maxError = err;
                tolerance = tol;
                passed = err <= tol;
            }
        }
    }
}
