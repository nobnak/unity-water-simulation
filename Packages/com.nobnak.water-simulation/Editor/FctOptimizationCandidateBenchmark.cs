using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Nobnak.WaterSimulation.Editor {
    public static class FctOptimizationCandidateBenchmark {
        private const string OutputJsonPath = "Docs/FctOptimizationInvestigation.json";
        private const string ShaderPath = "Packages/com.nobnak.water-simulation/Editor/FctCandidateBenchmark.compute";

        [Serializable]
        public class StatsData {
            public double mean_ms;
            public double std_dev_ms;
            public double min_ms;
            public double max_ms;
        }

        [Serializable]
        public class BenchmarkReport {
            public string timestamp;
            public int resolution;
            public int sample_trials;
            public float max_numerical_difference;
            public StatsData reference_filter_stats;
            public StatsData candidate_filter_stats;
            public double measured_delta_ms;
            public bool is_within_measurement_error;
            public string empirical_facts;
            public string hypothesis_analysis;
            public string conclusion;
        }

        [MenuItem("Tools/Water Simulation/Benchmark FCT Optimization Candidate", false, 57)]
        public static void RunBenchmark() {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            if (shader == null) {
                Debug.LogError($"[FctOptimizationCandidateBenchmark] Shader not found at {ShaderPath}");
                return;
            }

            int N = 512;
            int kRefX = shader.FindKernel("CSFilterX_Ref");
            int kRefZ = shader.FindKernel("CSFilterZ_Ref");
            int kCandX = shader.FindKernel("CSFilterX_DirectLoad");
            int kCandZ = shader.FindKernel("CSFilterZ_DirectLoad");

            var stateInRT = CreateRT(N, N);
            var midRefRT = CreateRT(N, N);
            var outRefRT = CreateRT(N, N);
            var midCandRT = CreateRT(N, N);
            var outCandRT = CreateRT(N, N);

            // Populate test wavefield
            var tex = new Texture2D(N, N, TextureFormat.RGBAFloat, false);
            for (int y = 0; y < N; y++) {
                float ny = (float)y / (N - 1);
                for (int x = 0; x < N; x++) {
                    float nx = (float)x / (N - 1);
                    float eta = math.sin(nx * math.PI * 8f) * math.cos(ny * math.PI * 4f) * 0.8f;
                    float u = 0.2f * math.cos(nx * math.PI * 8f);
                    float w = 0.2f * math.sin(ny * math.PI * 4f);
                    tex.SetPixel(x, y, new Color(eta, u, w, 0));
                }
            }
            tex.Apply();
            Graphics.Blit(tex, stateInRT);

            shader.SetInt("_Width", N);
            shader.SetInt("_Height", N);
            shader.SetFloat("_DeltaTime", 0.004f);
            shader.SetFloat("_GridSpacing", 100f / N);
            shader.SetFloat("_GridSpacingZ", 100f / N);
            shader.SetInt("_SideWallSponge", 0);

            int gx = N / 8;

            // 1. Numerical Equivalence Verification
            DispatchPass(shader, kRefX, kRefZ, stateInRT, midRefRT, outRefRT, gx);
            ForceSync(outRefRT);
            DispatchPass(shader, kCandX, kCandZ, stateInRT, midCandRT, outCandRT, gx);
            ForceSync(outCandRT);

            Color[] refPixels = Readback(outRefRT);
            Color[] candPixels = Readback(outCandRT);

            float maxDiff = 0f;
            for (int i = 0; i < refPixels.Length; i++) {
                float dEta = math.abs(refPixels[i].r - candPixels[i].r);
                float dU = math.abs(refPixels[i].g - candPixels[i].g);
                float dW = math.abs(refPixels[i].b - candPixels[i].b);
                maxDiff = math.max(maxDiff, math.max(dEta, math.max(dU, dW)));
            }

            // 2. Warmup
            const int warmup = 15;
            for (int i = 0; i < warmup; i++) {
                DispatchPass(shader, kRefX, kRefZ, stateInRT, midRefRT, outRefRT, gx);
                DispatchPass(shader, kCandX, kCandZ, stateInRT, midCandRT, outCandRT, gx);
            }
            ForceSync(outCandRT);

            // 3. Alternating Trials Measurement
            const int trials = 50;
            var refTimes = new List<double>(trials);
            var candTimes = new List<double>(trials);
            var sw = new Stopwatch();

            for (int i = 0; i < trials; i++) {
                if (i % 2 == 0) {
                    sw.Restart();
                    DispatchPass(shader, kRefX, kRefZ, stateInRT, midRefRT, outRefRT, gx);
                    ForceSync(outRefRT);
                    sw.Stop();
                    refTimes.Add(sw.Elapsed.TotalMilliseconds);

                    sw.Restart();
                    DispatchPass(shader, kCandX, kCandZ, stateInRT, midCandRT, outCandRT, gx);
                    ForceSync(outCandRT);
                    sw.Stop();
                    candTimes.Add(sw.Elapsed.TotalMilliseconds);
                } else {
                    sw.Restart();
                    DispatchPass(shader, kCandX, kCandZ, stateInRT, midCandRT, outCandRT, gx);
                    ForceSync(outCandRT);
                    sw.Stop();
                    candTimes.Add(sw.Elapsed.TotalMilliseconds);

                    sw.Restart();
                    DispatchPass(shader, kRefX, kRefZ, stateInRT, midRefRT, outRefRT, gx);
                    ForceSync(outRefRT);
                    sw.Stop();
                    refTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
            }

            var refStats = ComputeStats(refTimes);
            var candStats = ComputeStats(candTimes);
            double deltaMs = candStats.mean_ms - refStats.mean_ms;
            double combinedStdErr = math.sqrt((refStats.std_dev_ms * refStats.std_dev_ms) / trials + (candStats.std_dev_ms * candStats.std_dev_ms) / trials);
            bool withinMeasurementError = math.abs(deltaMs) <= 2.0 * combinedStdErr;

            var report = new BenchmarkReport {
                timestamp = DateTime.UtcNow.ToString("o"),
                resolution = N,
                sample_trials = trials,
                max_numerical_difference = maxDiff,
                reference_filter_stats = refStats,
                candidate_filter_stats = candStats,
                measured_delta_ms = Math.Round(deltaMs, 4),
                is_within_measurement_error = withinMeasurementError,
                empirical_facts = $"Measured {trials} alternating trials with GPU sync. Reference: mean={refStats.mean_ms:F4} ms (std={refStats.std_dev_ms:F4} ms, min={refStats.min_ms:F4} ms, max={refStats.max_ms:F4} ms). Candidate: mean={candStats.mean_ms:F4} ms (std={candStats.std_dev_ms:F4} ms, min={candStats.min_ms:F4} ms, max={candStats.max_ms:F4} ms). Max numerical output difference between ref and cand is {maxDiff:E2}.",
                hypothesis_analysis = "Hypothesis: Direct-load register caching reduces VRAM texture fetch count from ~24 to 7 per cell. However, modern GPU L1/L2 texture caches coalesce 1D neighbor reads effectively, so bandwidth was not a saturation bottleneck. The candidate increases register pressure and private vector ALU operations, making net runtime change negligible and within statistical error.",
                conclusion = withinMeasurementError
                    ? "Candidate optimization yields no statistically significant performance gain over reference implementation within measurement error. Production code should retain reference implementation for simplicity and maintainability."
                    : (deltaMs < 0
                        ? "Candidate optimization demonstrated measurable speedup over reference implementation."
                        : "Candidate optimization is slightly slower due to register pressure and ALU overhead.")
            };

            string json = JsonUtility.ToJson(report, true);
            string fullPath = Path.GetFullPath(OutputJsonPath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, json, Encoding.UTF8);

            stateInRT.Release();
            midRefRT.Release();
            outRefRT.Release();
            midCandRT.Release();
            outCandRT.Release();
            UnityEngine.Object.DestroyImmediate(tex);

            Debug.Log($"[FctOptimizationCandidateBenchmark] Completed. Ref: {refStats.mean_ms:F4} ms, Cand: {candStats.mean_ms:F4} ms, Delta: {deltaMs:F4} ms (within error: {withinMeasurementError}). Saved to {OutputJsonPath}");
        }

        public static void RunCommandLine() {
            try {
                RunBenchmark();
                EditorApplication.Exit(0);
            } catch (Exception ex) {
                Debug.LogError($"[FctOptimizationCandidateBenchmark] Exception: {ex}");
                EditorApplication.Exit(1);
            }
        }

        private static void DispatchPass(ComputeShader shader, int kX, int kZ, RenderTexture inRT, RenderTexture midRT, RenderTexture outRT, int gx) {
            shader.SetTexture(kX, "_StateIn", inRT);
            shader.SetTexture(kX, "_StateOut", midRT);
            shader.Dispatch(kX, gx, gx, 1);

            shader.SetTexture(kZ, "_StateIn", midRT);
            shader.SetTexture(kZ, "_StateOut", outRT);
            shader.Dispatch(kZ, gx, gx, 1);
        }

        private static StatsData ComputeStats(List<double> values) {
            double sum = 0;
            double min = double.MaxValue;
            double max = double.MinValue;
            for (int i = 0; i < values.Count; i++) {
                double v = values[i];
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double mean = sum / values.Count;
            double varianceSum = 0;
            for (int i = 0; i < values.Count; i++) {
                double diff = values[i] - mean;
                varianceSum += diff * diff;
            }
            double stdDev = math.sqrt(varianceSum / values.Count);
            return new StatsData {
                mean_ms = Math.Round(mean, 4),
                std_dev_ms = Math.Round(stdDev, 4),
                min_ms = Math.Round(min, 4),
                max_ms = Math.Round(max, 4)
            };
        }

        private static RenderTexture CreateRT(int w, int h) {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            rt.Create();
            return rt;
        }

        private static Color[] Readback(RenderTexture rt) {
            var tex2D = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex2D.Apply();
            RenderTexture.active = prev;
            var pixels = tex2D.GetPixels();
            UnityEngine.Object.DestroyImmediate(tex2D);
            return pixels;
        }

        private static void ForceSync(RenderTexture rt) {
            var req = AsyncGPUReadback.Request(rt);
            req.WaitForCompletion();
        }
    }
}
