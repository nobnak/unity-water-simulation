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
    /// Direct precision verification comparing actual GPU PCR compute shader execution
    /// against IEEE 754 FP64 tridiagonal / banded reference solutions.
    ///
    /// Verifies:
    /// 1. Direct Linear Dispersion Inversion on identical grid, bathymetry, RHS force, and boundary conditions.
    ///    Distinguishes normal border (Dirichlet az = 0) and tangential border (Neumann d(ax)/dz = 0).
    ///    Tests: Flat bed, Linear slope, Curved bed (h_zz != 0), and Piecewise linear shelf (kinks at z=10, 18m).
    ///    Separates GPU PCR numerical precision from Half-precision depth storage rounding.
    /// 2. Step-by-step Time Evolution Breakdown:
    ///    FilterOff, 1st-order upwind advection, isolating Half storage, SampleDepth clamp,
    ///    SmoothTotalDepth, and boundary clamping phase-by-phase.
    ///    Audits Parity 0, Parity 1, Boundary Wave, and Multi-substep error accumulation.
    /// </summary>
    public static class GpuPcrDirectPrecisionVerifier {
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
                    Debug.Log("[GpuPcrDirectPrecisionVerifier] Trigger detected, running verification in active Editor...");
                    var report = RunVerification();
                    File.WriteAllText(DoneMarker, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                    Debug.Log("[GpuPcrDirectPrecisionVerifier] Execution finished successfully!");
                } catch (Exception ex) {
                    Debug.LogError($"[GpuPcrDirectPrecisionVerifier] Trigger execution error: {ex}");
                    File.WriteAllText(DoneMarker, "ERROR: " + ex.ToString(), Encoding.UTF8);
                }
            }
        }
        #endregion

        #region public interface
        [MenuItem("Tools/Water Simulation/Verify GPU PCR vs FP64 Direct Precision", false, 57)]
        public static void RunVerificationMenu() {
            RunVerification();
        }

        public static void RunCommandLine() {
            try {
                Debug.Log("[GpuPcrDirectPrecisionVerifier] Starting CLI direct precision verification...");
                var report = RunVerification();

                bool allLinearPassed = true;
                foreach (var lt in report.linearDispersionTests) {
                    if (!lt.passed) allLinearPassed = false;
                }
                bool allTimeEvolutionPassed = report.timeEvolutionBreakdown.allPassed;

                if (!allLinearPassed || !allTimeEvolutionPassed) {
                    Debug.LogError($"[GpuPcrDirectPrecisionVerifier] Verification FAILED (LinearPassed={allLinearPassed}, TimeEvolutionPassed={allTimeEvolutionPassed})");
                    EditorApplication.Exit(1);
                } else {
                    Debug.Log("[GpuPcrDirectPrecisionVerifier] All verifications PASSED!");
                    EditorApplication.Exit(0);
                }
            } catch (Exception ex) {
                Debug.LogError($"[GpuPcrDirectPrecisionVerifier] Fatal exception: {ex}");
                EditorApplication.Exit(1);
            }
        }

        public static PrecisionVerificationReport RunVerification() {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            if (shader == null) {
                throw new FileNotFoundException($"Cannot find Boussinesq compute shader at {ShaderPath}");
            }

            var report = new PrecisionVerificationReport {
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                gitCommit = QueryGitCommit(out bool dirty),
                gitDirty = dirty,
                computeShaderHash = ComputeFileSha256(ShaderPath),
                gpuDevice = SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")",
                unityVersion = Application.unityVersion
            };

            Debug.Log($"[GpuPcrDirectPrecisionVerifier] GPU: {report.gpuDevice}, ShaderHash: {report.computeShaderHash}");

            // 1. Direct Linear Dispersion Inversion comparison
            report.linearDispersionTests = RunLinearDispersionTests(shader);

            // 2. Step-by-step Time Evolution breakdown
            report.timeEvolutionBreakdown = RunTimeEvolutionBreakdown(shader);

            // Save JSON report
            string json = JsonUtility.ToJson(report, true);
            string fullPath = Path.GetFullPath(OutputJsonPath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, json, Encoding.UTF8);

            Debug.Log($"[GpuPcrDirectPrecisionVerifier] Wrote precision report to {fullPath}");
            return report;
        }
        #endregion

        #region private methods - linear dispersion tests
        private static List<LinearDispersionCaseResult> RunLinearDispersionTests(ComputeShader shader) {
            var list = new List<LinearDispersionCaseResult>();
            int W = 16;
            int H = 256;
            float L = 50.0f;
            float dz = L / (H - 1);
            float dx = L / (W - 1);

            int kSpec256 = shader.FindKernel("CSTridiagonalZ_256");

            var bathymetries = new (string name, Func<float, float> profile)[] {
                ("FlatBed", z => 0.60f),
                ("LinearSlope", z => math.lerp(0.60f, 0.20f, z / L)),
                ("CurvedBed", z => 0.45f + 0.15f * math.sin(4.0f * math.PI * z / L)),
                ("PiecewiseShelf", z => (z <= 10.0f) ? 0.60f : ((z <= 18.0f) ? 0.60f - 0.05f * (z - 10.0f) : 0.20f))
            };

            var forceCases = new (string name, Func<float, float> fzFunc, Func<float, float> fxFunc)[] {
                ("Sinusoidal", z => math.sin(2.0f * math.PI * z / L), z => math.cos(2.0f * math.PI * z / L)),
                ("LocalizedPulse", z => math.exp(-math.pow((z - 25.0f) / 5.0f, 2.0f)), z => 0.5f * math.exp(-math.pow((z - 25.0f) / 5.0f, 2.0f)))
            };

            foreach (var bathy in bathymetries) {
                foreach (var force in forceCases) {
                    var result = RunSingleLinearCase(shader, kSpec256, W, H, L, dx, dz,
                                                     bathy.name, bathy.profile,
                                                     force.name, force.fzFunc, force.fxFunc);
                    list.Add(result);
                    Debug.Log($"[GpuPcrDirectPrecisionVerifier] {result.caseName}: MaxNormDiff={result.maxNormalDiff:E4}, RelNormErr={result.relNormalDiff:E4}, ResidualNorm={result.maxNormalResidual:E4}, Passed={result.passed}");
                }
            }

            return list;
        }

        private static LinearDispersionCaseResult RunSingleLinearCase(
            ComputeShader shader, int kSpec256, int W, int H, float L, float dx, float dz,
            string bathyName, Func<float, float> bathyFunc,
            string forceName, Func<float, float> fzFunc, Func<float, float> fxFunc) {

            var bathyRT = CreateRT(W, H, RenderTextureFormat.RGHalf);
            var pongRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var pingRT = CreateRT(W, H, RenderTextureFormat.RGFloat);

            var bathyTex = new Texture2D(W, H, TextureFormat.RGFloat, false);
            var forceTex = new Texture2D(W, H, TextureFormat.RGFloat, false);

            double[] hSpec = new double[H];
            double[] fzSpec = new double[H];
            double[] fxSpec = new double[H];

            for (int y = 0; y < H; y++) {
                float z = y * dz;
                float d = bathyFunc(z);
                float fz = fzFunc(z);
                float fx = fxFunc(z);
                hSpec[y] = d;
                fzSpec[y] = fz;
                fxSpec[y] = fx;

                for (int x = 0; x < W; x++) {
                    bathyTex.SetPixel(x, y, new Color(d, -d, 0, 0));
                    forceTex.SetPixel(x, y, new Color(fx, fz, 0, 0));
                }
            }
            bathyTex.Apply();
            forceTex.Apply();
            Graphics.Blit(bathyTex, bathyRT);
            Graphics.Blit(forceTex, pongRT);

            // Bind uniforms
            shader.SetInt(PropWidth, W);
            shader.SetInt(PropHeight, H);
            shader.SetFloat(PropGridSpacing, dx);
            shader.SetFloat(PropGridSpacingZ, dz);
            shader.SetFloat(PropDispersionAlpha, 1.0f);
            shader.SetInt(PropSideWallSponge, 0);

            // Run CSTridiagonalZ_256
            shader.SetTexture(kSpec256, PropBathymetryTex, bathyRT);
            shader.SetTexture(kSpec256, PropAccelPong, pongRT);
            shader.SetTexture(kSpec256, PropAccelPing, pingRT);
            shader.Dispatch(kSpec256, W, 1, 1);

            // Readback GPU results
            var req = AsyncGPUReadback.Request(pingRT);
            var reqBathy = AsyncGPUReadback.Request(bathyRT);
            req.WaitForCompletion();
            reqBathy.WaitForCompletion();

            var gpuFloats = req.GetData<float>();
            var gpuBathyHalf = reqBathy.GetData<ushort>();

            bathyRT.Release();
            pongRT.Release();
            pingRT.Release();
            UnityEngine.Object.DestroyImmediate(bathyTex);
            UnityEngine.Object.DestroyImmediate(forceTex);

            int col = W / 2;
            double[] gpuAz = new double[H];
            double[] gpuAx = new double[H];
            double[] actualGpuH = new double[H];

            for (int y = 0; y < H; y++) {
                int idxRG = (y * W + col) * 2;
                gpuAx[y] = gpuFloats[idxRG];
                gpuAz[y] = gpuFloats[idxRG + 1];
                actualGpuH[y] = Mathf.HalfToFloat(gpuBathyHalf[idxRG]);
            }

            // Solve exact FP64 tridiagonal reference matching GPU sampled depth
            double invDz2 = 1.0 / (dz * dz);

            // 1. Normal direction az (Dirichlet at y=0 and y=H-1)
            double[] A_norm = new double[H];
            double[] B_norm = new double[H];
            double[] C_norm = new double[H];
            double[] d_norm = new double[H];

            B_norm[0] = 1.0;
            C_norm[0] = 0.0;
            d_norm[0] = 0.0;

            A_norm[H - 1] = 0.0;
            B_norm[H - 1] = 1.0;
            d_norm[H - 1] = 0.0;

            for (int j = 1; j < H - 1; j++) {
                double hC = math.max(0.2, actualGpuH[j]);
                double hD = math.max(0.2, actualGpuH[j - 1]);
                double hU = math.max(0.2, actualGpuH[j + 1]);

                A_norm[j] = -invDz2 * (0.5 * hC * hD - (hC * hC) / 6.0);
                B_norm[j] = 1.0 + invDz2 * ((2.0 / 3.0) * hC * hC);
                C_norm[j] = -invDz2 * (0.5 * hC * hU - (hC * hC) / 6.0);
                d_norm[j] = fzSpec[j];
            }

            double[] refAz = SolveTridiagonalFP64(A_norm, B_norm, C_norm, d_norm);

            // 2. Tangential direction ax (Neumann d(ax)/dz = 0 at y=0 and y=H-1)
            double[] A_tang = new double[H];
            double[] B_tang = new double[H];
            double[] C_tang = new double[H];
            double[] d_tang = new double[H];

            double beta0 = invDz2 * ((1.0 / 3.0) * math.max(0.2, actualGpuH[0]) * math.max(0.2, actualGpuH[0]));
            double betaEnd = invDz2 * ((1.0 / 3.0) * math.max(0.2, actualGpuH[H - 1]) * math.max(0.2, actualGpuH[H - 1]));

            B_tang[0] = 1.0 + 2.0 * beta0;
            C_tang[0] = -2.0 * beta0;
            d_tang[0] = fxSpec[0];

            A_tang[H - 1] = -2.0 * betaEnd;
            B_tang[H - 1] = 1.0 + 2.0 * betaEnd;
            d_tang[H - 1] = fxSpec[H - 1];

            for (int j = 1; j < H - 1; j++) {
                double hC = math.max(0.2, actualGpuH[j]);
                double hD = math.max(0.2, actualGpuH[j - 1]);
                double hU = math.max(0.2, actualGpuH[j + 1]);

                A_tang[j] = -invDz2 * (0.5 * hC * hD - (hC * hC) / 6.0);
                B_tang[j] = 1.0 + invDz2 * ((2.0 / 3.0) * hC * hC);
                C_tang[j] = -invDz2 * (0.5 * hC * hU - (hC * hC) / 6.0);
                d_tang[j] = fxSpec[j];
            }

            double[] refAx = SolveTridiagonalFP64(A_tang, B_tang, C_tang, d_tang);

            // Error statistics against matched discrete problem
            double maxNormDiff = 0.0;
            double sumSqNorm = 0.0;
            double maxRefNorm = 0.0;

            double maxTangDiff = 0.0;
            double sumSqTang = 0.0;
            double maxRefTang = 0.0;

            for (int j = 0; j < H; j++) {
                double errNorm = math.abs(gpuAz[j] - refAz[j]);
                maxNormDiff = math.max(maxNormDiff, errNorm);
                sumSqNorm += errNorm * errNorm;
                maxRefNorm = math.max(maxRefNorm, math.abs(refAz[j]));

                double errTang = math.abs(gpuAx[j] - refAx[j]);
                maxTangDiff = math.max(maxTangDiff, errTang);
                sumSqTang += errTang * errTang;
                maxRefTang = math.max(maxRefTang, math.abs(refAx[j]));
            }

            double rmsNormDiff = math.sqrt(sumSqNorm / H);
            double relNormDiff = (maxRefNorm > 1e-12) ? maxNormDiff / maxRefNorm : 0.0;

            double rmsTangDiff = math.sqrt(sumSqTang / H);
            double relTangDiff = (maxRefTang > 1e-12) ? maxTangDiff / maxRefTang : 0.0;

            // Operator residual of GPU solution: r = M_FP64 * a_GPU - f
            double maxNormRes = 0.0;
            for (int j = 0; j < H; j++) {
                double rowVal;
                if (j == 0) rowVal = B_norm[0] * gpuAz[0] + C_norm[0] * gpuAz[1];
                else if (j == H - 1) rowVal = A_norm[H - 1] * gpuAz[H - 2] + B_norm[H - 1] * gpuAz[H - 1];
                else rowVal = A_norm[j] * gpuAz[j - 1] + B_norm[j] * gpuAz[j] + C_norm[j] * gpuAz[j + 1];

                double res = math.abs(rowVal - d_norm[j]);
                maxNormRes = math.max(maxNormRes, res);
            }

            // Measure effect of Half-precision depth storage vs exact analytic depth
            // 1. Water depth error due to FP16 quantization [m]
            double maxDepthHalfErr = 0.0;
            for (int j = 0; j < H; j++) {
                maxDepthHalfErr = math.max(maxDepthHalfErr, math.abs(actualGpuH[j] - hSpec[j]));
            }

            // 2. Inverted acceleration difference due to FP16 bathymetry [m/s^2]
            double[] A_exact = new double[H];
            double[] B_exact = new double[H];
            double[] C_exact = new double[H];
            B_exact[0] = 1.0; C_exact[0] = 0.0;
            A_exact[H - 1] = 0.0; B_exact[H - 1] = 1.0;
            for (int j = 1; j < H - 1; j++) {
                double hC = math.max(0.2, hSpec[j]);
                double hD = math.max(0.2, hSpec[j - 1]);
                double hU = math.max(0.2, hSpec[j + 1]);
                A_exact[j] = -invDz2 * (0.5 * hC * hD - (hC * hC) / 6.0);
                B_exact[j] = 1.0 + invDz2 * ((2.0 / 3.0) * hC * hC);
                C_exact[j] = -invDz2 * (0.5 * hC * hU - (hC * hC) / 6.0);
            }
            double[] analyticRefAz = SolveTridiagonalFP64(A_exact, B_exact, C_exact, d_norm);
            double maxHalfStorageAccelDiff = 0.0;
            for (int j = 0; j < H; j++) {
                maxHalfStorageAccelDiff = math.max(maxHalfStorageAccelDiff, math.abs(refAz[j] - analyticRefAz[j]));
            }

            bool normPass = (maxNormDiff <= 1e-5) || (relNormDiff <= 1e-4);
            bool tangPass = (maxTangDiff <= 1e-5) || (relTangDiff <= 1e-4);
            bool resPass = (maxNormRes <= 1e-4);

            return new LinearDispersionCaseResult {
                caseName = $"{bathyName}_{forceName}",
                bathymetryName = bathyName,
                forceType = forceName,
                gridH = H,
                maxNormalDiff = maxNormDiff,
                rmsNormalDiff = rmsNormDiff,
                relNormalDiff = relNormDiff,
                maxNormalResidual = maxNormRes,
                maxTangentialDiff = maxTangDiff,
                rmsTangentialDiff = rmsTangDiff,
                relTangentialDiff = relTangDiff,
                halfStorageDepthRoundingDiff = maxDepthHalfErr,
                halfStorageAccelRoundingDiff = maxHalfStorageAccelDiff,
                passed = normPass && tangPass && resPass
            };
        }
        #endregion

        #region private methods - time evolution breakdown
        private static TimeEvolutionBreakdownReport RunTimeEvolutionBreakdown(ComputeShader shader) {
            int W = 16;
            int H = 256;
            float L = 50.0f;
            float dt = 0.004f;
            float g = 9.81f;
            float dz = L / (H - 1);
            float dx = L / (W - 1);

            var scenarios = new List<TimeEvolutionScenarioReport>();

            // Scenario 1: Substep 1 with Parity 0 (solitary wave at z0=5.0m)
            var s1 = RunSingleSubstepScenario(shader, W, H, L, dx, dz, dt, g,
                                              parity: 0, z0: 5.0f, a0: 0.12f,
                                              scenarioName: "Substep1_Parity0",
                                              desc: "Substep 1 with StepParity=0 (Forward predictor, Backward corrector), solitary wave at z0=5m");
            scenarios.Add(s1);

            // Scenario 2: Substep 1 with Parity 1 (solitary wave at z0=5.0m)
            var s2 = RunSingleSubstepScenario(shader, W, H, L, dx, dz, dt, g,
                                              parity: 1, z0: 5.0f, a0: 0.12f,
                                              scenarioName: "Substep1_Parity1",
                                              desc: "Substep 1 with StepParity=1 (Backward predictor, Forward corrector), solitary wave at z0=5m");
            scenarios.Add(s2);

            // Scenario 3: Boundary Wave Test (solitary wave centered at z0=0.8m, exciting the solid border at j=0)
            var s3 = RunSingleSubstepScenario(shader, W, H, L, dx, dz, dt, g,
                                              parity: 0, z0: 0.8f, a0: 0.12f,
                                              scenarioName: "BoundaryWave_NearWall",
                                              desc: "Boundary wave test with solitary crest at z0=0.8m, generating active non-zero amplitude at border j=0");
            scenarios.Add(s3);

            // Scenario 4: Multi-substep Error Accumulation (5 consecutive substeps, alternating parities)
            var s4 = RunMultiSubstepScenario(shader, W, H, L, dx, dz, dt, g,
                                             numSteps: 5, z0: 5.0f, a0: 0.12f,
                                             scenarioName: "MultiSubstep_5Steps",
                                             desc: "Multi-substep error accumulation test (5 consecutive substeps alternating parity 0,1,0,1,0)");
            scenarios.Add(s4);

            bool allPassed = true;
            foreach (var sc in scenarios) {
                if (!sc.allPhasesPassed) allPassed = false;
            }

            string conclusionText = allPassed
                ? "対象条件においてGPU PCRとFP64線形解、および短時間の離散時間発展（逆Parity・壁面近傍・複数サブステップ）の実装整合を確認した。一方、長時間の波高不足（BP05 Case Bの-27.5% FAIL等）の原因は未解決であり、物理的妥当性は未確定"
                : "時間発展検証において許容値を超える不一致が検出された（FAILフェーズあり）";

            return new TimeEvolutionBreakdownReport {
                gridH = H,
                dt = dt,
                scenarios = scenarios,
                maxSmoothTotalDepthDeltaEta = s1.maxSmoothTotalDepthDeltaEta,
                allPassed = allPassed,
                conclusion = conclusionText
            };
        }

        private static TimeEvolutionScenarioReport RunSingleSubstepScenario(
            ComputeShader shader, int W, int H, float L, float dx, float dz, float dt, float g,
            int parity, float z0, float a0, string scenarioName, string desc) {

            int kPredict = shader.FindKernel("CSPredict");
            int kCorrect = shader.FindKernel("CSCorrect");
            int kTridiagX = shader.FindKernel("CSTridiagonalX_128");
            int kTridiagZ = shader.FindKernel("CSTridiagonalZ_256");
            int kApplyPred = shader.FindKernel("CSApplyPredictedDispersion");
            int kApplyDisp = shader.FindKernel("CSApplyDispersion");

            var stateInRT = CreateRT(W, H, RenderTextureFormat.ARGBFloat);
            var statePredRT = CreateRT(W, H, RenderTextureFormat.ARGBFloat);
            var stateOutRT = CreateRT(W, H, RenderTextureFormat.ARGBFloat);
            var bathyRT = CreateRT(W, H, RenderTextureFormat.RGHalf);
            var forceOrigRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var forceRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var forcePredRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var accelPongRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var accelPingRT = CreateRT(W, H, RenderTextureFormat.RGFloat);

            float h1 = 0.60f;
            float c0 = math.sqrt(g * (h1 + a0));
            float kappa0 = math.sqrt(3.0f * a0 / (4.0f * h1 * h1 * h1));

            double[] zArr = new double[H];
            double[] hSpec = new double[H];
            double[] eta0 = new double[H];
            double[] w0 = new double[H];

            var bathyTex = new Texture2D(W, H, TextureFormat.RGFloat, false);
            var stateInitTex = new Texture2D(W, H, TextureFormat.RGBAFloat, false);

            for (int y = 0; y < H; y++) {
                float z = y * dz;
                zArr[y] = z;
                float d = (z <= 10.0f) ? 0.60f : ((z <= 18.0f) ? 0.60f - 0.05f * (z - 10.0f) : 0.20f);
                hSpec[y] = d;

                float arg = math.clamp(kappa0 * (z - z0), -25.0f, 25.0f);
                float sech = 1.0f / math.cosh(arg);
                float eta = a0 * sech * sech;
                float w = c0 * eta / (d + eta);
                eta0[y] = eta;
                w0[y] = w;

                for (int x = 0; x < W; x++) {
                    bathyTex.SetPixel(x, y, new Color(d, -d, 0, 0));
                    stateInitTex.SetPixel(x, y, new Color(eta, 0, w, 0));
                }
            }
            bathyTex.Apply();
            stateInitTex.Apply();
            Graphics.Blit(bathyTex, bathyRT);
            Graphics.Blit(stateInitTex, stateInRT);

            shader.SetInt(PropWidth, W);
            shader.SetInt(PropHeight, H);
            shader.SetFloat(PropDeltaTime, dt);
            shader.SetFloat(PropGridSpacing, dx);
            shader.SetFloat(PropGridSpacingZ, dz);
            shader.SetFloat(PropGravity, g);
            shader.SetFloat(PropDispersionAlpha, 1.0f);
            shader.SetFloat(PropBedFriction, 0.0f);
            shader.SetInt(PropStepParity, parity);
            shader.SetInt(PropSideWallSponge, 0);
            shader.SetFloat(PropWavemakerAmp, 0.0f);

            // 1. Predictor: CSPredict
            shader.SetTexture(kPredict, PropStateIn, stateInRT);
            shader.SetTexture(kPredict, PropStatePred, statePredRT);
            shader.SetTexture(kPredict, PropBathymetryTex, bathyRT);
            shader.SetTexture(kPredict, PropForceOrigTex, forceOrigRT);
            shader.SetTexture(kPredict, PropForceTex, forceRT);
            shader.SetTexture(kPredict, PropAccelPing, accelPingRT);
            shader.Dispatch(kPredict, W / 8, H / 8, 1);

            var reqPredState = AsyncGPUReadback.Request(statePredRT);
            var reqForcePred = AsyncGPUReadback.Request(forceRT);
            reqPredState.WaitForCompletion();
            reqForcePred.WaitForCompletion();
            var gpuStatePredA = reqPredState.GetData<float>();
            var gpuForceA = reqForcePred.GetData<float>();

            // 2. Dispersion Inversion on Predictor Force
            shader.SetTexture(kTridiagX, PropBathymetryTex, bathyRT);
            shader.SetTexture(kTridiagX, PropForceTex, forceRT);
            shader.SetTexture(kTridiagX, PropAccelPong, accelPongRT);
            shader.Dispatch(kTridiagX, H, 1, 1);

            shader.SetTexture(kTridiagZ, PropBathymetryTex, bathyRT);
            shader.SetTexture(kTridiagZ, PropAccelPong, accelPongRT);
            shader.SetTexture(kTridiagZ, PropAccelPing, accelPingRT);
            shader.Dispatch(kTridiagZ, W, 1, 1);

            var reqA1 = AsyncGPUReadback.Request(accelPingRT);
            reqA1.WaitForCompletion();
            var gpuAccel1 = reqA1.GetData<float>();

            // 3. CSApplyPredictedDispersion
            shader.SetTexture(kApplyPred, PropStatePred, statePredRT);
            shader.SetTexture(kApplyPred, PropAccelPing, accelPingRT);
            shader.SetTexture(kApplyPred, PropForcePredTex, forcePredRT);
            shader.Dispatch(kApplyPred, W / 8, H / 8, 1);

            var reqPredFinal = AsyncGPUReadback.Request(statePredRT);
            reqPredFinal.WaitForCompletion();
            var gpuStatePredFinal = reqPredFinal.GetData<float>();

            // 4. CSCorrect
            shader.SetTexture(kCorrect, PropStateIn, stateInRT);
            shader.SetTexture(kCorrect, PropStatePred, statePredRT);
            shader.SetTexture(kCorrect, PropStateOut, stateOutRT);
            shader.SetTexture(kCorrect, PropBathymetryTex, bathyRT);
            shader.SetTexture(kCorrect, PropForceOrigTex, forceOrigRT);
            shader.SetTexture(kCorrect, PropForceTex, forceRT);
            shader.SetTexture(kCorrect, PropForcePredTex, forcePredRT);
            shader.SetTexture(kCorrect, PropAccelPing, accelPingRT);
            shader.Dispatch(kCorrect, W / 8, H / 8, 1);

            var reqCorrState = AsyncGPUReadback.Request(stateOutRT);
            var reqForceCorr = AsyncGPUReadback.Request(forceRT);
            reqCorrState.WaitForCompletion();
            reqForceCorr.WaitForCompletion();
            var gpuStateCorr = reqCorrState.GetData<float>();
            var gpuForceCorr = reqForceCorr.GetData<float>();

            // 5. Dispersion Inversion on Corrector Force
            shader.SetTexture(kTridiagX, PropBathymetryTex, bathyRT);
            shader.SetTexture(kTridiagX, PropForceTex, forceRT);
            shader.SetTexture(kTridiagX, PropAccelPong, accelPongRT);
            shader.Dispatch(kTridiagX, H, 1, 1);

            shader.SetTexture(kTridiagZ, PropBathymetryTex, bathyRT);
            shader.SetTexture(kTridiagZ, PropAccelPong, accelPongRT);
            shader.SetTexture(kTridiagZ, PropAccelPing, accelPingRT);
            shader.Dispatch(kTridiagZ, W, 1, 1);

            var reqA2 = AsyncGPUReadback.Request(accelPingRT);
            reqA2.WaitForCompletion();
            var gpuAccel2 = reqA2.GetData<float>();

            // 6. CSApplyDispersion
            // In BoussinesqSimulation.cs, CSApplyDispersion takes PropStateIn = stateOutRT (holding etaNext, uN, wN)
            // and writes to statePredRT which receives (etaNext, uNext, wNext).
            shader.SetTexture(kApplyDisp, PropStateIn, stateOutRT);
            shader.SetTexture(kApplyDisp, PropStateOut, statePredRT);
            shader.SetTexture(kApplyDisp, PropForcePredTex, forcePredRT);
            shader.SetTexture(kApplyDisp, PropAccelPing, accelPingRT);
            shader.Dispatch(kApplyDisp, W / 8, H / 8, 1);

            var reqFinalState = AsyncGPUReadback.Request(statePredRT);
            var reqBathy = AsyncGPUReadback.Request(bathyRT);
            reqFinalState.WaitForCompletion();
            reqBathy.WaitForCompletion();

            var gpuStateFinal = reqFinalState.GetData<float>();
            var gpuBathyHalf = reqBathy.GetData<ushort>();

            int col = W / 2;
            double[] gpuEtaPred = new double[H];
            double[] gpuFzPred = new double[H];
            double[] gpuAzPred = new double[H];
            double[] gpuWPred = new double[H];
            double[] gpuEta1 = new double[H];
            double[] gpuFzCorr = new double[H];
            double[] gpuAzCorr = new double[H];
            double[] gpuW1 = new double[H];
            double[] actualH = new double[H];

            for (int y = 0; y < H; y++) {
                int idxRGBA = (y * W + col) * 4;
                int idxRG = (y * W + col) * 2;

                gpuEtaPred[y] = gpuStatePredA[idxRGBA];
                gpuFzPred[y] = gpuForceA[idxRG + 1];
                gpuAzPred[y] = gpuAccel1[idxRG + 1];
                gpuWPred[y] = gpuStatePredFinal[idxRGBA + 2];

                gpuEta1[y] = gpuStateCorr[idxRGBA];
                gpuFzCorr[y] = gpuForceCorr[idxRG + 1];
                gpuAzCorr[y] = gpuAccel2[idxRG + 1];
                gpuW1[y] = gpuStateFinal[idxRGBA + 2];

                actualH[y] = Mathf.HalfToFloat(gpuBathyHalf[idxRG]);
            }

            stateInRT.Release();
            statePredRT.Release();
            stateOutRT.Release();
            bathyRT.Release();
            forceOrigRT.Release();
            forceRT.Release();
            forcePredRT.Release();
            accelPongRT.Release();
            accelPingRT.Release();
            UnityEngine.Object.DestroyImmediate(bathyTex);
            UnityEngine.Object.DestroyImmediate(stateInitTex);

            var cpuRefStep = RunCpuReferenceSubstepFP64(H, dz, dt, g, actualH, eta0, w0, parity, useSmoothTotalDepth: true);
            var cpuRefNoSmooth = RunCpuReferenceSubstepFP64(H, dz, dt, g, actualH, eta0, w0, parity, useSmoothTotalDepth: false);

            var phases = new List<SubstepPhaseAudit> {
                ComparePhase("Phase 1: Predictor Surface (eta*)", "eta", "m", 1e-4, 1e-3, gpuEtaPred, cpuRefStep.etaPred),
                ComparePhase("Phase 2: Predictor RHS Force (fz*)", "fz", "m/s^2", 1e-3, 1e-3, gpuFzPred, cpuRefStep.fzPred),
                ComparePhase("Phase 3: Inverted Predictor Accel (az*)", "az", "m/s^2", 1e-4, 1e-3, gpuAzPred, cpuRefStep.azPred),
                ComparePhase("Phase 4: Predicted Velocity (w*)", "w", "m/s", 1e-4, 1e-3, gpuWPred, cpuRefStep.wPred),
                ComparePhase("Phase 5: Corrected Surface (eta^1)", "eta", "m", 1e-4, 1e-3, gpuEta1, cpuRefStep.eta1),
                ComparePhase("Phase 6: Corrector RHS Force (fz**)", "fz", "m/s^2", 1e-3, 1e-3, gpuFzCorr, cpuRefStep.fzCorr),
                ComparePhase("Phase 7: Inverted Corrector Accel (az**)", "az", "m/s^2", 1e-4, 1e-3, gpuAzCorr, cpuRefStep.azCorr),
                ComparePhase("Phase 8: Final Step Velocity (w^1)", "w", "m/s", 1e-4, 1e-3, gpuW1, cpuRefStep.w1)
            };

            double maxSmoothDiffEta = 0.0;
            for (int y = 0; y < H; y++) {
                maxSmoothDiffEta = math.max(maxSmoothDiffEta, math.abs(cpuRefStep.eta1[y] - cpuRefNoSmooth.eta1[y]));
            }

            bool allPhasesPassed = true;
            foreach (var p in phases) {
                if (!p.passed) allPhasesPassed = false;
            }

            return new TimeEvolutionScenarioReport {
                scenarioName = scenarioName,
                description = desc,
                stepParity = parity,
                numSubsteps = 1,
                phaseAudits = phases,
                multiStepAudits = new List<TimeEvolutionMultiStepAudit>(),
                maxSmoothTotalDepthDeltaEta = maxSmoothDiffEta,
                allPhasesPassed = allPhasesPassed
            };
        }

        private static TimeEvolutionScenarioReport RunMultiSubstepScenario(
            ComputeShader shader, int W, int H, float L, float dx, float dz, float dt, float g,
            int numSteps, float z0, float a0, string scenarioName, string desc) {

            int kPredict = shader.FindKernel("CSPredict");
            int kCorrect = shader.FindKernel("CSCorrect");
            int kTridiagX = shader.FindKernel("CSTridiagonalX_128");
            int kTridiagZ = shader.FindKernel("CSTridiagonalZ_256");
            int kApplyPred = shader.FindKernel("CSApplyPredictedDispersion");
            int kApplyDisp = shader.FindKernel("CSApplyDispersion");

            var stateInRT = CreateRT(W, H, RenderTextureFormat.ARGBFloat);
            var statePredRT = CreateRT(W, H, RenderTextureFormat.ARGBFloat);
            var stateOutRT = CreateRT(W, H, RenderTextureFormat.ARGBFloat);
            var bathyRT = CreateRT(W, H, RenderTextureFormat.RGHalf);
            var forceOrigRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var forceRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var forcePredRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var accelPongRT = CreateRT(W, H, RenderTextureFormat.RGFloat);
            var accelPingRT = CreateRT(W, H, RenderTextureFormat.RGFloat);

            float h1 = 0.60f;
            float c0 = math.sqrt(g * (h1 + a0));
            float kappa0 = math.sqrt(3.0f * a0 / (4.0f * h1 * h1 * h1));

            double[] hSpec = new double[H];
            double[] etaCurr = new double[H];
            double[] wCurr = new double[H];

            var bathyTex = new Texture2D(W, H, TextureFormat.RGFloat, false);
            var stateInitTex = new Texture2D(W, H, TextureFormat.RGBAFloat, false);

            for (int y = 0; y < H; y++) {
                float z = y * dz;
                float d = (z <= 10.0f) ? 0.60f : ((z <= 18.0f) ? 0.60f - 0.05f * (z - 10.0f) : 0.20f);
                hSpec[y] = d;

                float arg = math.clamp(kappa0 * (z - z0), -25.0f, 25.0f);
                float sech = 1.0f / math.cosh(arg);
                float eta = a0 * sech * sech;
                float w = c0 * eta / (d + eta);
                etaCurr[y] = eta;
                wCurr[y] = w;

                for (int x = 0; x < W; x++) {
                    bathyTex.SetPixel(x, y, new Color(d, -d, 0, 0));
                    stateInitTex.SetPixel(x, y, new Color(eta, 0, w, 0));
                }
            }
            bathyTex.Apply();
            stateInitTex.Apply();
            Graphics.Blit(bathyTex, bathyRT);
            Graphics.Blit(stateInitTex, stateInRT);

            shader.SetInt(PropWidth, W);
            shader.SetInt(PropHeight, H);
            shader.SetFloat(PropDeltaTime, dt);
            shader.SetFloat(PropGridSpacing, dx);
            shader.SetFloat(PropGridSpacingZ, dz);
            shader.SetFloat(PropGravity, g);
            shader.SetFloat(PropDispersionAlpha, 1.0f);
            shader.SetFloat(PropBedFriction, 0.0f);
            shader.SetInt(PropSideWallSponge, 0);
            shader.SetFloat(PropWavemakerAmp, 0.0f);

            var reqBathy = AsyncGPUReadback.Request(bathyRT);
            reqBathy.WaitForCompletion();
            var gpuBathyHalf = reqBathy.GetData<ushort>();

            int col = W / 2;
            double[] actualH = new double[H];
            for (int y = 0; y < H; y++) {
                int idxRG = (y * W + col) * 2;
                actualH[y] = Mathf.HalfToFloat(gpuBathyHalf[idxRG]);
            }

            var stepAudits = new List<TimeEvolutionMultiStepAudit>();
            bool allStepsPassed = true;

            for (int s = 0; s < numSteps; s++) {
                int parity = s % 2;
                shader.SetInt(PropStepParity, parity);

                // CSPredict
                shader.SetTexture(kPredict, PropStateIn, stateInRT);
                shader.SetTexture(kPredict, PropStatePred, statePredRT);
                shader.SetTexture(kPredict, PropBathymetryTex, bathyRT);
                shader.SetTexture(kPredict, PropForceOrigTex, forceOrigRT);
                shader.SetTexture(kPredict, PropForceTex, forceRT);
                shader.SetTexture(kPredict, PropAccelPing, accelPingRT);
                shader.Dispatch(kPredict, W / 8, H / 8, 1);

                // Dispersion Inversion on Predictor Force
                shader.SetTexture(kTridiagX, PropBathymetryTex, bathyRT);
                shader.SetTexture(kTridiagX, PropForceTex, forceRT);
                shader.SetTexture(kTridiagX, PropAccelPong, accelPongRT);
                shader.Dispatch(kTridiagX, H, 1, 1);

                shader.SetTexture(kTridiagZ, PropBathymetryTex, bathyRT);
                shader.SetTexture(kTridiagZ, PropAccelPong, accelPongRT);
                shader.SetTexture(kTridiagZ, PropAccelPing, accelPingRT);
                shader.Dispatch(kTridiagZ, W, 1, 1);

                // CSApplyPredictedDispersion
                shader.SetTexture(kApplyPred, PropStatePred, statePredRT);
                shader.SetTexture(kApplyPred, PropAccelPing, accelPingRT);
                shader.SetTexture(kApplyPred, PropForcePredTex, forcePredRT);
                shader.Dispatch(kApplyPred, W / 8, H / 8, 1);

                // CSCorrect
                shader.SetTexture(kCorrect, PropStateIn, stateInRT);
                shader.SetTexture(kCorrect, PropStatePred, statePredRT);
                shader.SetTexture(kCorrect, PropStateOut, stateOutRT);
                shader.SetTexture(kCorrect, PropBathymetryTex, bathyRT);
                shader.SetTexture(kCorrect, PropForceOrigTex, forceOrigRT);
                shader.SetTexture(kCorrect, PropForceTex, forceRT);
                shader.SetTexture(kCorrect, PropForcePredTex, forcePredRT);
                shader.SetTexture(kCorrect, PropAccelPing, accelPingRT);
                shader.Dispatch(kCorrect, W / 8, H / 8, 1);

                // Dispersion Inversion on Corrector Force
                shader.SetTexture(kTridiagX, PropBathymetryTex, bathyRT);
                shader.SetTexture(kTridiagX, PropForceTex, forceRT);
                shader.SetTexture(kTridiagX, PropAccelPong, accelPongRT);
                shader.Dispatch(kTridiagX, H, 1, 1);

                shader.SetTexture(kTridiagZ, PropBathymetryTex, bathyRT);
                shader.SetTexture(kTridiagZ, PropAccelPong, accelPongRT);
                shader.SetTexture(kTridiagZ, PropAccelPing, accelPingRT);
                shader.Dispatch(kTridiagZ, W, 1, 1);

                // CSApplyDispersion
                shader.SetTexture(kApplyDisp, PropStateIn, stateOutRT);
                shader.SetTexture(kApplyDisp, PropStateOut, statePredRT);
                shader.SetTexture(kApplyDisp, PropForcePredTex, forcePredRT);
                shader.SetTexture(kApplyDisp, PropAccelPing, accelPingRT);
                shader.Dispatch(kApplyDisp, W / 8, H / 8, 1);

                // Readback state after full substep
                var reqFinal = AsyncGPUReadback.Request(statePredRT);
                reqFinal.WaitForCompletion();
                var gpuState = reqFinal.GetData<float>();

                // Execute reference CPU substep
                var cpuRes = RunCpuReferenceSubstepFP64(H, dz, dt, g, actualH, etaCurr, wCurr, parity, useSmoothTotalDepth: true);

                // Measure differences
                double maxDiffEta = 0.0;
                double sumSqEta = 0.0;
                double maxRefEta = 0.0;
                double maxDiffW = 0.0;
                double sumSqW = 0.0;
                double maxRefW = 0.0;

                for (int y = 0; y < H; y++) {
                    int idxRGBA = (y * W + col) * 4;
                    double gpuEta = gpuState[idxRGBA];
                    double gpuW = gpuState[idxRGBA + 2];

                    double diffEta = math.abs(gpuEta - cpuRes.eta1[y]);
                    maxDiffEta = math.max(maxDiffEta, diffEta);
                    sumSqEta += diffEta * diffEta;
                    maxRefEta = math.max(maxRefEta, math.abs(cpuRes.eta1[y]));

                    double diffW = math.abs(gpuW - cpuRes.w1[y]);
                    maxDiffW = math.max(maxDiffW, diffW);
                    sumSqW += diffW * diffW;
                    maxRefW = math.max(maxRefW, math.abs(cpuRes.w1[y]));
                }

                double rmsDiffEta = math.sqrt(sumSqEta / H);
                double relDiffEta = (maxRefEta > 1e-12) ? maxDiffEta / maxRefEta : 0.0;
                double rmsDiffW = math.sqrt(sumSqW / H);
                double relDiffW = (maxRefW > 1e-12) ? maxDiffW / maxRefW : 0.0;

                bool stepPass = (maxDiffEta <= 1e-4 || relDiffEta <= 1e-3) && (maxDiffW <= 1e-4 || relDiffW <= 1e-3);
                if (!stepPass) allStepsPassed = false;

                stepAudits.Add(new TimeEvolutionMultiStepAudit {
                    substepIndex = s + 1,
                    stepParity = parity,
                    maxDiffEta = maxDiffEta,
                    rmsDiffEta = rmsDiffEta,
                    relDiffEta = relDiffEta,
                    maxDiffW = maxDiffW,
                    rmsDiffW = rmsDiffW,
                    relDiffW = relDiffW,
                    passed = stepPass
                });

                // Update CPU state & Blit GPU statePred to stateIn for next substep
                etaCurr = cpuRes.eta1;
                wCurr = cpuRes.w1;
                Graphics.Blit(statePredRT, stateInRT);
            }

            stateInRT.Release();
            statePredRT.Release();
            stateOutRT.Release();
            bathyRT.Release();
            forceOrigRT.Release();
            forceRT.Release();
            forcePredRT.Release();
            accelPongRT.Release();
            accelPingRT.Release();
            UnityEngine.Object.DestroyImmediate(bathyTex);
            UnityEngine.Object.DestroyImmediate(stateInitTex);

            return new TimeEvolutionScenarioReport {
                scenarioName = scenarioName,
                description = desc,
                stepParity = 0,
                numSubsteps = numSteps,
                phaseAudits = new List<SubstepPhaseAudit>(),
                multiStepAudits = stepAudits,
                maxSmoothTotalDepthDeltaEta = 0.0,
                allPhasesPassed = allStepsPassed
            };
        }

        private static SubstepPhaseAudit ComparePhase(
            string phaseName, string variableName, string unit, double absTol, double relTol,
            double[] gpuArr, double[] cpuArr) {

            int H = gpuArr.Length;
            double maxDiffAll = 0.0;
            double sumSqAll = 0.0;
            double maxValAll = 0.0;
            int maxDiffIndex = 0;

            double maxDiffInt = 0.0;
            double sumSqInt = 0.0;
            double maxValInt = 0.0;
            int maxDiffIntIndex = 1;

            for (int i = 0; i < H; i++) {
                double diff = math.abs(gpuArr[i] - cpuArr[i]);
                if (diff > maxDiffAll) {
                    maxDiffAll = diff;
                    maxDiffIndex = i;
                }
                sumSqAll += diff * diff;
                maxValAll = math.max(maxValAll, math.abs(cpuArr[i]));

                if (i > 0 && i < H - 1) {
                    if (diff > maxDiffInt) {
                        maxDiffInt = diff;
                        maxDiffIntIndex = i;
                    }
                    sumSqInt += diff * diff;
                    maxValInt = math.max(maxValInt, math.abs(cpuArr[i]));
                }
            }

            double rmsAll = math.sqrt(sumSqAll / H);
            double relAll = (maxValAll > 1e-12) ? maxDiffAll / maxValAll : 0.0;

            double rmsInt = math.sqrt(sumSqInt / math.max(1, H - 2));
            double relInt = (maxValInt > 1e-12) ? maxDiffInt / maxValInt : 0.0;

            bool isBoundary = (maxDiffIndex == 0 || maxDiffIndex == H - 1);
            bool passAll = (maxDiffAll <= absTol) || (relAll <= relTol);
            bool passInt = (maxDiffInt <= absTol) || (relInt <= relTol);

            return new SubstepPhaseAudit {
                phaseName = phaseName,
                variableName = variableName,
                unit = unit,
                absTol = absTol,
                relTol = relTol,
                maxDiffAll = maxDiffAll,
                rmsDiffAll = rmsAll,
                relDiffAll = relAll,
                maxDiffIndex = maxDiffIndex,
                isBoundary = isBoundary,
                gpuValAtMax = gpuArr[maxDiffIndex],
                cpuValAtMax = cpuArr[maxDiffIndex],
                maxDiffInterior = maxDiffInt,
                rmsDiffInterior = rmsInt,
                relDiffInterior = relInt,
                maxDiffInteriorIndex = maxDiffIntIndex,
                gpuValAtMaxInterior = gpuArr[maxDiffIntIndex],
                cpuValAtMaxInterior = cpuArr[maxDiffIntIndex],
                passed = passAll && passInt
            };
        }

        private struct CpuSubstepResult {
            public double[] etaPred;
            public double[] fzPred;
            public double[] azPred;
            public double[] wPred;
            public double[] eta1;
            public double[] fzCorr;
            public double[] azCorr;
            public double[] w1;
        }

        private static CpuSubstepResult RunCpuReferenceSubstepFP64(
            int H, double dz, double dt, double g, double[] hSpec, double[] etaN, double[] wN,
            int parity, bool useSmoothTotalDepth) {

            // SampleDepth: max(0.2, h)
            double[] h = new double[H];
            for (int i = 0; i < H; i++) h[i] = math.max(0.2, hSpec[i]);

            double EvalTotalH(double rawH) {
                if (!useSmoothTotalDepth) return math.max(0.01, rawH);
                double d = rawH - 0.05;
                return 0.05 + 0.5 * (d + math.sqrt(d * d + 0.0004));
            }

            // 1. Predictor: dir = (parity == 0) ? 1 : -1
            int dirPred = (parity == 0) ? 1 : -1;
            double signedInvDzPred = (double)dirPred / dz;
            double invDz = 1.0 / dz;

            double[] etaPred = new double[H];
            double[] fzPred = new double[H];

            for (int j = 0; j < H; j++) {
                int jZ = math.clamp(j + dirPred, 0, H - 1);
                int jD = math.max(0, j - 1);
                int jU = math.min(H - 1, j + 1);

                double totalHC = EvalTotalH(h[j] + etaN[j]);
                double totalHZ = EvalTotalH(h[jZ] + etaN[jZ]);

                double dHwdz = (totalHZ * wN[jZ] - totalHC * wN[j]) * signedInvDzPred;
                etaPred[j] = etaN[j] - dt * dHwdz;

                double deta_dz = (etaN[jZ] - etaN[j]) * signedInvDzPred;

                double dwdz = (wN[j] >= 0.0) ? (wN[j] - wN[jD]) * invDz : (wN[jU] - wN[j]) * invDz;
                double advectZ = wN[j] * dwdz;

                // GPU CSPredict does not zero fzPred at boundaries; it computes -g * deta_dz - advectZ
                fzPred[j] = -g * deta_dz - advectZ;
            }

            // Invert dispersion for a*:
            // Tridiagonal boundary rows enforce az = 0 at j=0 and j=H-1 (matching CSTridiagonalZ)
            double[] azPred = SolveDispersionZ_FP64(H, dz, h, fzPred);

            // Predictor velocity (CSApplyPredictedDispersion):
            // GPU CSApplyPredictedDispersion updates wPred = wN + dt * azPred without boundary zeroing
            double[] wPred = new double[H];
            for (int j = 0; j < H; j++) {
                wPred[j] = wN[j] + dt * azPred[j];
            }

            // 2. Corrector: dir = (parity == 0) ? -1 : 1
            int dirCorr = (parity == 0) ? -1 : 1;
            double signedInvDzCorr = (double)dirCorr / dz;

            double[] eta1 = new double[H];
            double[] fzCorr = new double[H];

            for (int j = 0; j < H; j++) {
                int jZ = math.clamp(j + dirCorr, 0, H - 1);
                int jD = math.max(0, j - 1);
                int jU = math.min(H - 1, j + 1);

                double totalHC = EvalTotalH(h[j] + etaPred[j]);
                double totalHZ = EvalTotalH(h[jZ] + etaPred[jZ]);

                double dHwdzStar = (totalHZ * wPred[jZ] - totalHC * wPred[j]) * signedInvDzCorr;
                eta1[j] = 0.5 * (etaN[j] + etaPred[j] - dt * dHwdzStar);

                double deta_dz_star = (etaPred[jZ] - etaPred[j]) * signedInvDzCorr;

                double dwdz_star = (wPred[j] >= 0.0) ? (wPred[j] - wPred[jD]) * invDz : (wPred[jU] - wPred[j]) * invDz;
                double advectStar = wPred[j] * dwdz_star;

                // GPU CSCorrect does not zero fzCorr at boundaries; stores it directly into _ForceTex
                fzCorr[j] = -g * deta_dz_star - advectStar;
            }

            // Invert dispersion for a**:
            double[] azCorr = SolveDispersionZ_FP64(H, dz, h, fzCorr);

            // Corrector velocity (CSApplyDispersion):
            double[] w1 = new double[H];
            for (int j = 0; j < H; j++) {
                w1[j] = wN[j] + dt * 0.5 * (azPred[j] + azCorr[j]);
            }
            // GPU CSApplyDispersion explicitly enforces w1 = 0 at j=0 and j=H-1:
            w1[0] = 0.0;
            w1[H - 1] = 0.0;

            return new CpuSubstepResult {
                etaPred = etaPred,
                fzPred = fzPred,
                azPred = azPred,
                wPred = wPred,
                eta1 = eta1,
                fzCorr = fzCorr,
                azCorr = azCorr,
                w1 = w1
            };
        }

        private static double[] SolveDispersionZ_FP64(int H, double dz, double[] h, double[] fz) {
            double invDz2 = 1.0 / (dz * dz);
            double[] A = new double[H];
            double[] B = new double[H];
            double[] C = new double[H];
            double[] d = new double[H];

            B[0] = 1.0;
            C[0] = 0.0;
            d[0] = 0.0; // Strictly zero normal acceleration az = 0 at j=0

            A[H - 1] = 0.0;
            B[H - 1] = 1.0;
            d[H - 1] = 0.0; // Strictly zero normal acceleration az = 0 at j=H-1

            for (int j = 1; j < H - 1; j++) {
                double hC = h[j];
                double hD = h[j - 1];
                double hU = h[j + 1];

                A[j] = -invDz2 * (0.5 * hC * hD - (hC * hC) / 6.0);
                B[j] = 1.0 + invDz2 * ((2.0 / 3.0) * hC * hC);
                C[j] = -invDz2 * (0.5 * hC * hU - (hC * hC) / 6.0);
                d[j] = fz[j];
            }

            return SolveTridiagonalFP64(A, B, C, d);
        }

        private static double[] SolveTridiagonalFP64(double[] a, double[] b, double[] c, double[] d) {
            int n = d.Length;
            double[] cPrime = new double[n];
            double[] dPrime = new double[n];
            double[] x = new double[n];

            cPrime[0] = c[0] / b[0];
            dPrime[0] = d[0] / b[0];

            for (int i = 1; i < n; i++) {
                double denom = b[i] - a[i] * cPrime[i - 1];
                if (math.abs(denom) < 1e-15) denom = 1e-15;
                if (i < n - 1) {
                    cPrime[i] = c[i] / denom;
                }
                dPrime[i] = (d[i] - a[i] * dPrime[i - 1]) / denom;
            }

            x[n - 1] = dPrime[n - 1];
            for (int i = n - 2; i >= 0; i--) {
                x[i] = dPrime[i] - cPrime[i] * x[i + 1];
            }

            return x;
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

        private static string QueryGitCommit(out bool isDirty) {
            isDirty = false;
            try {
                var p = new Process {
                    StartInfo = new ProcessStartInfo("git", "rev-parse --short HEAD") {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                string rev = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();

                var pStatus = new Process {
                    StartInfo = new ProcessStartInfo("git", "status --porcelain") {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                pStatus.Start();
                string status = pStatus.StandardOutput.ReadToEnd().Trim();
                pStatus.WaitForExit();
                isDirty = !string.IsNullOrEmpty(status);

                return rev;
            } catch {
                return "unknown";
            }
        }

        private static string ComputeFileSha256(string path) {
            if (!File.Exists(path)) return "missing";
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            byte[] hash = sha.ComputeHash(stream);
            var sb = new StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        #endregion

        #region constants / static
        public const string TriggerFile = "Temp/run_precision_verifier.trigger";
        public const string DoneMarker = "Temp/precision_verifier_done.marker";
        private const string OutputJsonPath = "Docs/GpuPcrDirectPrecisionReport.json";
        private const string ShaderPath = "Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute";

        private static readonly int PropWidth = Shader.PropertyToID("_Width");
        private static readonly int PropHeight = Shader.PropertyToID("_Height");
        private static readonly int PropDeltaTime = Shader.PropertyToID("_DeltaTime");
        private static readonly int PropGridSpacing = Shader.PropertyToID("_GridSpacing");
        private static readonly int PropGridSpacingZ = Shader.PropertyToID("_GridSpacingZ");
        private static readonly int PropGravity = Shader.PropertyToID("_Gravity");
        private static readonly int PropDispersionAlpha = Shader.PropertyToID("_DispersionAlpha");
        private static readonly int PropBedFriction = Shader.PropertyToID("_BedFriction");
        private static readonly int PropStepParity = Shader.PropertyToID("_StepParity");
        private static readonly int PropSideWallSponge = Shader.PropertyToID("_SideWallSponge");
        private static readonly int PropWavemakerAmp = Shader.PropertyToID("_WavemakerAmp");
        private static readonly int PropStateIn = Shader.PropertyToID("_StateIn");
        private static readonly int PropStatePred = Shader.PropertyToID("_StatePred");
        private static readonly int PropStateOut = Shader.PropertyToID("_StateOut");
        private static readonly int PropBathymetryTex = Shader.PropertyToID("_BathymetryTex");
        private static readonly int PropForceOrigTex = Shader.PropertyToID("_ForceOrigTex");
        private static readonly int PropForceTex = Shader.PropertyToID("_ForceTex");
        private static readonly int PropForcePredTex = Shader.PropertyToID("_ForcePredTex");
        private static readonly int PropAccelPing = Shader.PropertyToID("_AccelPing");
        private static readonly int PropAccelPong = Shader.PropertyToID("_AccelPong");
        #endregion

        #region data structures
        [Serializable]
        public class PrecisionVerificationReport {
            public string timestamp;
            public string gitCommit;
            public bool gitDirty;
            public string computeShaderHash;
            public string gpuDevice;
            public string unityVersion;
            public List<LinearDispersionCaseResult> linearDispersionTests;
            public TimeEvolutionBreakdownReport timeEvolutionBreakdown;
        }

        [Serializable]
        public class LinearDispersionCaseResult {
            public string caseName;
            public string bathymetryName;
            public string forceType;
            public int gridH;
            public double maxNormalDiff;
            public double rmsNormalDiff;
            public double relNormalDiff;
            public double maxNormalResidual;
            public double maxTangentialDiff;
            public double rmsTangentialDiff;
            public double relTangentialDiff;
            public double halfStorageDepthRoundingDiff; // [m] 水深自体の格納誤差
            public double halfStorageAccelRoundingDiff; // [m/s^2] 水深丸めによる加速度解の差
            public bool passed;
        }

        [Serializable]
        public class TimeEvolutionBreakdownReport {
            public int gridH;
            public double dt;
            public List<TimeEvolutionScenarioReport> scenarios;
            public double maxSmoothTotalDepthDeltaEta;
            public bool allPassed;
            public string conclusion;
        }

        [Serializable]
        public class TimeEvolutionScenarioReport {
            public string scenarioName;
            public string description;
            public int stepParity;
            public int numSubsteps;
            public List<SubstepPhaseAudit> phaseAudits;
            public List<TimeEvolutionMultiStepAudit> multiStepAudits;
            public double maxSmoothTotalDepthDeltaEta;
            public bool allPhasesPassed;
        }

        [Serializable]
        public class TimeEvolutionMultiStepAudit {
            public int substepIndex;
            public int stepParity;
            public double maxDiffEta;
            public double rmsDiffEta;
            public double relDiffEta;
            public double maxDiffW;
            public double rmsDiffW;
            public double relDiffW;
            public bool passed;
        }

        [Serializable]
        public class SubstepPhaseAudit {
            public string phaseName;
            public string variableName;
            public string unit;
            public double absTol;
            public double relTol;

            public double maxDiffAll;
            public double rmsDiffAll;
            public double relDiffAll;
            public int maxDiffIndex;
            public bool isBoundary;
            public double gpuValAtMax;
            public double cpuValAtMax;

            public double maxDiffInterior;
            public double rmsDiffInterior;
            public double relDiffInterior;
            public int maxDiffInteriorIndex;
            public double gpuValAtMaxInterior;
            public double cpuValAtMaxInterior;

            public bool passed;
        }
        #endregion
    }
}
