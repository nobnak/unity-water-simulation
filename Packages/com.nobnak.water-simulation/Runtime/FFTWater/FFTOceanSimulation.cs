using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Cascaded statistical FFT Ocean surface simulation.
    /// Uses Tessendorf (2001) spectral model with resolution-independent amplitude discretization,
    /// conjugate-symmetric wavenumber pairing, and single-2D-IFFT complex packing.
    /// </summary>
    [ExecuteAlways]
    public class FFTOceanSimulation : WaterSimulationBase {
        public enum SeaStatePreset {
            Calm = 0,
            Swell = 1,
            Stormy = 2,
            Custom = 3
        }

        #region serialized fields
        [Header("Ocean Presets")]
        [SerializeField] private SeaStatePreset seaState = SeaStatePreset.Swell;

        [Header("Wind & Waves")]
        [Tooltip("Wind speed in m/s (e.g. 5m/s = gentle breeze, 18m/s = gale).")]
        [SerializeField] private float windSpeed = 10.0f;

        [Tooltip("Wind direction angle in degrees [0..360].")]
        [SerializeField] private float windDirectionDegrees = 45.0f;

        [Tooltip("Horizontal choppy wave displacement scale.")]
        [Range(0f, 3.0f)]
        [SerializeField] private float choppiness = 1.35f;

        [Header("Swell Component")]
        [Tooltip("Amplitude of distant swell waves [m].")]
        [SerializeField] private float swellStrength = 1.5f;

        [Tooltip("Distant swell arrival angle in degrees [0..360].")]
        [SerializeField] private float swellDirectionDegrees = 120.0f;

        [Header("Foam & Crests")]
        [SerializeField] private bool enableFoam = true;
        [SerializeField] private float foamThreshold = 0.35f;

        [Header("Compute Shader")]
        [SerializeField] private ComputeShader fftShader;
        #endregion

        #region private fields
        private RenderTexture initialSpectrumTex;
        private RenderTexture spectrumPackedTex;
        private RenderTexture pingPongTexA;
        private RenderTexture pingPongTexB;

        private bool spectrumDirty = true;
        private bool pausedStepDirty;
        private int spectrumRegenerationCount;

        private int kernelSpectrum;
        private int kernelEvolution;
        private int kernelFFTHorizontal;
        private int kernelFFTVertical;
        private int kernelPack;
        #endregion

        #region properties
        public SeaStatePreset SeaState {
            get => seaState;
            set {
                if (seaState == value) return;
                seaState = value;
                ApplySeaStatePreset(value);
            }
        }

        public float WindSpeed {
            get => windSpeed;
            set {
                float clamped = math.max(0.1f, value);
                if (math.abs(windSpeed - clamped) < 1e-4f) return;
                windSpeed = clamped;
                seaState = SeaStatePreset.Custom;
                MarkSpectrumDirty();
            }
        }

        public float WindDirectionDegrees {
            get => windDirectionDegrees;
            set {
                if (math.abs(windDirectionDegrees - value) < 1e-4f) return;
                windDirectionDegrees = value;
                seaState = SeaStatePreset.Custom;
                MarkSpectrumDirty();
            }
        }

        public float SwellStrength {
            get => swellStrength;
            set {
                float clamped = math.max(0f, value);
                if (math.abs(swellStrength - clamped) < 1e-4f) return;
                swellStrength = clamped;
                seaState = SeaStatePreset.Custom;
                MarkSpectrumDirty();
            }
        }

        public float SwellDirectionDegrees {
            get => swellDirectionDegrees;
            set {
                if (math.abs(swellDirectionDegrees - value) < 1e-4f) return;
                swellDirectionDegrees = value;
                seaState = SeaStatePreset.Custom;
                MarkSpectrumDirty();
            }
        }

        public float Choppiness {
            get => choppiness;
            set {
                float clamped = math.clamp(value, 0f, 3.0f);
                if (math.abs(choppiness - clamped) < 1e-4f) return;
                choppiness = clamped;
                if (!isRunning) pausedStepDirty = true;
            }
        }

        public bool EnableFoam {
            get => enableFoam;
            set {
                if (enableFoam == value) return;
                enableFoam = value;
                if (!isRunning) pausedStepDirty = true;
            }
        }

        public int SpectrumRegenerationCount => spectrumRegenerationCount;
        #endregion

        #region Unity lifecycle
#if UNITY_EDITOR
        private void Reset() {
            ResolveComputeShader();
        }
#endif

        protected override void OnEnable() {
            ResolveComputeShader();
            ApplySeaStatePreset(seaState);
            base.OnEnable();
        }

        private void LateUpdate() {
            if (!isRunning && (spectrumDirty || pausedStepDirty)) {
                pausedStepDirty = false;
                OnStepSimulation(0f);
            }
        }
        #endregion

        #region public interface
        public void ApplySeaStatePreset(SeaStatePreset preset) {
            seaState = preset;
            switch (preset) {
                case SeaStatePreset.Calm:
                    windSpeed = 4.0f;
                    windDirectionDegrees = 30.0f;
                    choppiness = 0.6f;
                    swellStrength = 0.2f;
                    swellDirectionDegrees = 60.0f;
                    break;
                case SeaStatePreset.Swell:
                    windSpeed = 8.0f;
                    windDirectionDegrees = 45.0f;
                    choppiness = 1.35f;
                    swellStrength = 2.2f;
                    swellDirectionDegrees = 135.0f;
                    break;
                case SeaStatePreset.Stormy:
                    windSpeed = 18.0f;
                    windDirectionDegrees = 70.0f;
                    choppiness = 1.9f;
                    swellStrength = 3.5f;
                    swellDirectionDegrees = 90.0f;
                    break;
            }
            MarkSpectrumDirty();
        }
        #endregion

        #region private methods
        private void ResolveComputeShader() {
            if (fftShader == null) {
#if UNITY_EDITOR
                fftShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute");
#endif
                if (fftShader == null) {
                    Debug.LogError("[FFTOceanSimulation] fftShader is not assigned. Please assign FFTOcean.compute in Inspector.", this);
                }
            }
        }

        private void MarkSpectrumDirty() {
            spectrumDirty = true;
            if (!isRunning) pausedStepDirty = true;
        }

        protected override void InitializeTextures() {
            ReleaseTextures();

            int n = resolution.x; // Must be power of two
            if (!Mathf.IsPowerOfTwo(n)) {
                n = Mathf.ClosestPowerOfTwo(n);
            }
            resolution = new Vector2Int(n, n);

            initialSpectrumTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat, FilterMode.Point);
            spectrumPackedTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat, FilterMode.Point);
            pingPongTexA = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat, FilterMode.Point);
            pingPongTexB = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat, FilterMode.Point);

            waterDataTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBHalf);
            normalTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBHalf);

            CacheKernels();
            spectrumRegenerationCount = 0;
            spectrumDirty = true;
            FlushInitialSpectrumIfDirty();
        }

        private void CacheKernels() {
            if (fftShader == null) return;
            kernelSpectrum = fftShader.FindKernel("CSGenerateSpectrum");
            kernelEvolution = fftShader.FindKernel("CSTimeEvolution");
            kernelFFTHorizontal = fftShader.FindKernel("CSFFTHorizontal");
            kernelFFTVertical = fftShader.FindKernel("CSFFTVertical");
            kernelPack = fftShader.FindKernel("CSPackOceanData");
        }

        protected override void ReleaseTextures() {
            ReleaseTexture(ref initialSpectrumTex);
            ReleaseTexture(ref spectrumPackedTex);
            ReleaseTexture(ref pingPongTexA);
            ReleaseTexture(ref pingPongTexB);
            ReleaseTexture(ref waterDataTex);
            ReleaseTexture(ref normalTex);
        }

        protected override void OnResetSimulation() {
            simulationTime = 0f;
            spectrumDirty = true;
            pausedStepDirty = false;
            lastDispatchCount = 0;
            OnStepSimulation(0f);
        }

        private void FlushInitialSpectrumIfDirty() {
            if (!spectrumDirty || fftShader == null || initialSpectrumTex == null) return;
            spectrumDirty = false;

            int n = resolution.x;
            float radW = windDirectionDegrees * Mathf.Deg2Rad;
            Vector2 windDir = new Vector2(math.cos(radW), math.sin(radW));

            float radS = swellDirectionDegrees * Mathf.Deg2Rad;
            Vector2 swellDir = new Vector2(math.cos(radS), math.sin(radS));

            fftShader.SetInt(PropN, n);
            fftShader.SetFloat(PropLengthScale1, areaSize.x);
            fftShader.SetFloat(PropWindSpeed, windSpeed);
            fftShader.SetVector(PropWindDir, new Vector4(windDir.x, windDir.y, 0, 0));
            fftShader.SetFloat(PropSwellStrength, swellStrength);
            fftShader.SetVector(PropSwellDir, new Vector4(swellDir.x, swellDir.y, 0, 0));

            fftShader.SetTexture(kernelSpectrum, PropInitialSpectrumRW, initialSpectrumTex);
            int gx = math.max(1, (n + 7) / 8);
            fftShader.Dispatch(kernelSpectrum, gx, gx, 1);
            lastDispatchCount++;
            spectrumRegenerationCount++;
        }

        protected override void OnStepSimulation(float dt) {
            if (fftShader == null || initialSpectrumTex == null) return;

            lastSubStepCount = 1;
            FlushInitialSpectrumIfDirty();

            int n = resolution.x;
            float radW = windDirectionDegrees * Mathf.Deg2Rad;
            Vector2 windDir = new Vector2(math.cos(radW), math.sin(radW));
            float radS = swellDirectionDegrees * Mathf.Deg2Rad;
            Vector2 swellDir = new Vector2(math.cos(radS), math.sin(radS));

            fftShader.SetInt(PropN, n);
            fftShader.SetFloat(PropLengthScale1, areaSize.x);
            fftShader.SetFloat(PropTime, simulationTime);
            fftShader.SetFloat(PropWindSpeed, windSpeed);
            fftShader.SetVector(PropWindDir, new Vector4(windDir.x, windDir.y, 0, 0));
            fftShader.SetFloat(PropSwellStrength, swellStrength);
            fftShader.SetVector(PropSwellDir, new Vector4(swellDir.x, swellDir.y, 0, 0));
            fftShader.SetFloat(PropChoppiness, choppiness);
            fftShader.SetFloat(PropFoamThreshold, foamThreshold);
            fftShader.SetInt(PropEnableFoam, enableFoam ? 1 : 0);

            int gx = math.max(1, (n + 7) / 8);

            // 1. Time evolution & complex packing (RG: Dy + i*Dx, BA: Dz)
            fftShader.SetTexture(kernelEvolution, PropInitialSpectrum, initialSpectrumTex);
            fftShader.SetTexture(kernelEvolution, PropSpectrumPacked, spectrumPackedTex);
            fftShader.Dispatch(kernelEvolution, gx, gx, 1);
            lastDispatchCount++;

            // 2. Perform a single 2D IFFT for all 3 real fields (Dy, Dx, Dz) without Graphics.Blit
            Perform2DIFFT(spectrumPackedTex);

            // 3. Unpack displacement and compute normal + Jacobian foam
            fftShader.SetTexture(kernelPack, PropSpectrumPacked, spectrumPackedTex);
            fftShader.SetTexture(kernelPack, PropWaterDataTex, waterDataTex);
            fftShader.SetTexture(kernelPack, PropNormalTex, normalTex);
            fftShader.Dispatch(kernelPack, gx, gx, 1);
            lastDispatchCount++;
        }

        private void Perform2DIFFT(RenderTexture spectrumTarget) {
            int n = resolution.x;
            int numStages = (int)math.round(math.log2(n));
            int gx = math.max(1, (n + 7) / 8);

            RenderTexture src = pingPongTexA;
            RenderTexture dst = pingPongTexB;

            // Horizontal FFT passes (stage 0 reads directly from spectrumTarget)
            for (int stage = 0; stage < numStages; stage++) {
                int step = 1 << stage;
                RenderTexture inTex = (stage == 0) ? spectrumTarget : src;
                fftShader.SetInt(PropStepSize, step);
                fftShader.SetTexture(kernelFFTHorizontal, PropInputPing, inTex);
                fftShader.SetTexture(kernelFFTHorizontal, PropOutputPong, dst);
                fftShader.Dispatch(kernelFFTHorizontal, gx, gx, 1);
                lastDispatchCount++;

                var temp = src;
                src = dst;
                dst = temp;
            }

            // Vertical FFT passes (final stage writes directly back to spectrumTarget)
            for (int stage = 0; stage < numStages; stage++) {
                int step = 1 << stage;
                RenderTexture outTex = (stage == numStages - 1) ? spectrumTarget : dst;
                fftShader.SetInt(PropStepSize, step);
                fftShader.SetTexture(kernelFFTVertical, PropInputPing, src);
                fftShader.SetTexture(kernelFFTVertical, PropOutputPong, outTex);
                fftShader.Dispatch(kernelFFTVertical, gx, gx, 1);
                lastDispatchCount++;

                var temp = src;
                src = dst;
                dst = temp;
            }
        }
        #endregion

        #region constants / static
        private static readonly int PropN = Shader.PropertyToID("_N");
        private static readonly int PropLengthScale1 = Shader.PropertyToID("_LengthScale1");
        private static readonly int PropTime = Shader.PropertyToID("_Time");
        private static readonly int PropWindSpeed = Shader.PropertyToID("_WindSpeed");
        private static readonly int PropWindDir = Shader.PropertyToID("_WindDir");
        private static readonly int PropSwellStrength = Shader.PropertyToID("_SwellStrength");
        private static readonly int PropSwellDir = Shader.PropertyToID("_SwellDir");
        private static readonly int PropChoppiness = Shader.PropertyToID("_Choppiness");
        private static readonly int PropFoamThreshold = Shader.PropertyToID("_FoamThreshold");
        private static readonly int PropEnableFoam = Shader.PropertyToID("_EnableFoam");
        private static readonly int PropStepSize = Shader.PropertyToID("_StepSize");
        private static readonly int PropInitialSpectrum = Shader.PropertyToID("_InitialSpectrum");
        private static readonly int PropInitialSpectrumRW = Shader.PropertyToID("_InitialSpectrumRW");
        private static readonly int PropSpectrumPacked = Shader.PropertyToID("_SpectrumPacked");
        private static readonly int PropInputPing = Shader.PropertyToID("_InputPing");
        private static readonly int PropOutputPong = Shader.PropertyToID("_OutputPong");
        private static readonly int PropWaterDataTex = Shader.PropertyToID("_WaterDataTex");
        private static readonly int PropNormalTex = Shader.PropertyToID("_NormalTex");
        #endregion
    }
}
