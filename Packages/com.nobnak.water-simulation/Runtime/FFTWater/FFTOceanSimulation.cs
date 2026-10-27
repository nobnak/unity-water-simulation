using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Cascaded statistical FFT Ocean surface simulation.
    /// Uses Tessendorf (2001) spectral model with customizable swell, wind, choppiness, and foam.
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
        private RenderTexture spectrumDyTex;
        private RenderTexture spectrumDxDzTex;
        private RenderTexture pingPongTexA;
        private RenderTexture pingPongTexB;

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
                seaState = value;
                ApplySeaStatePreset(value);
            }
        }

        public float WindSpeed {
            get => windSpeed;
            set {
                windSpeed = math.max(0.1f, value);
                RegenerateInitialSpectrum();
            }
        }

        public float WindDirectionDegrees {
            get => windDirectionDegrees;
            set {
                windDirectionDegrees = value;
                RegenerateInitialSpectrum();
            }
        }

        public float SwellStrength {
            get => swellStrength;
            set {
                swellStrength = math.max(0f, value);
                RegenerateInitialSpectrum();
            }
        }

        public float SwellDirectionDegrees {
            get => swellDirectionDegrees;
            set {
                swellDirectionDegrees = value;
                RegenerateInitialSpectrum();
            }
        }

        public float Choppiness {
            get => choppiness;
            set {
                choppiness = math.clamp(value, 0f, 3.0f);
                if (!isRunning) OnStepSimulation(0f);
            }
        }

        public bool EnableFoam {
            get => enableFoam;
            set {
                enableFoam = value;
                if (!isRunning) OnStepSimulation(0f);
            }
        }
        #endregion

        #region Unity lifecycle
        protected override void OnEnable() {
            ResolveComputeShader();
            ApplySeaStatePreset(seaState);
            base.OnEnable();
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
            RegenerateInitialSpectrum();
        }
        #endregion

        #region private methods
        private void ResolveComputeShader() {
            if (fftShader == null) {
                fftShader = Resources.Load<ComputeShader>("FFTOcean");
            }
        }

        protected override void InitializeTextures() {
            ReleaseTextures();

            int n = resolution.x; // Must be power of two
            if (!Mathf.IsPowerOfTwo(n)) {
                n = Mathf.ClosestPowerOfTwo(n);
                resolution = new Vector2Int(n, n);
            }

            initialSpectrumTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat);
            spectrumDyTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat);
            spectrumDxDzTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat);
            pingPongTexA = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat);
            pingPongTexB = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBFloat);

            waterDataTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBHalf);
            normalTex = CreateSimulationTexture(n, n, RenderTextureFormat.ARGBHalf);

            CacheKernels();
            RegenerateInitialSpectrum();
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
            ReleaseTexture(ref spectrumDyTex);
            ReleaseTexture(ref spectrumDxDzTex);
            ReleaseTexture(ref pingPongTexA);
            ReleaseTexture(ref pingPongTexB);
            ReleaseTexture(ref waterDataTex);
            ReleaseTexture(ref normalTex);
        }

        protected override void OnResetSimulation() {
            simulationTime = 0f;
            RegenerateInitialSpectrum();
            OnStepSimulation(0.001f);
        }

        private void RegenerateInitialSpectrum() {
            if (fftShader == null || initialSpectrumTex == null) return;

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
            int gx = math.max(1, n / 8);
            fftShader.Dispatch(kernelSpectrum, gx, gx, 1);

            if (!isRunning) {
                OnStepSimulation(0f);
            }
        }

        protected override void OnStepSimulation(float dt) {
            if (fftShader == null || initialSpectrumTex == null) return;

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

            // 1. Time evolution
            fftShader.SetTexture(kernelEvolution, PropInitialSpectrum, initialSpectrumTex);
            fftShader.SetTexture(kernelEvolution, PropSpectrumDy, spectrumDyTex);
            fftShader.SetTexture(kernelEvolution, PropSpectrumDxDz, spectrumDxDzTex);
            int gx = math.max(1, n / 8);
            fftShader.Dispatch(kernelEvolution, gx, gx, 1);

            // 2. Perform 2D IFFT for Height (Dy)
            Perform2DIFFT(spectrumDyTex);

            // 3. Perform 2D IFFT for Choppy Displacements (Dx, Dz)
            Perform2DIFFT(spectrumDxDzTex);

            // 4. Pack displacement and compute normal + foam
            fftShader.SetTexture(kernelPack, PropSpectrumDy, spectrumDyTex);
            fftShader.SetTexture(kernelPack, PropSpectrumDxDz, spectrumDxDzTex);
            fftShader.SetTexture(kernelPack, PropWaterDataTex, waterDataTex);
            fftShader.SetTexture(kernelPack, PropNormalTex, normalTex);
            fftShader.Dispatch(kernelPack, gx, gx, 1);
        }

        private void Perform2DIFFT(RenderTexture spectrumTarget) {
            int n = resolution.x;
            int numStages = (int)math.log2(n);
            int gx = math.max(1, n / 8);

            Graphics.Blit(spectrumTarget, pingPongTexA);

            RenderTexture src = pingPongTexA;
            RenderTexture dst = pingPongTexB;

            // Horizontal FFT passes
            for (int stage = 0; stage < numStages; stage++) {
                int step = 1 << stage;
                fftShader.SetInt(PropStepSize, step);
                fftShader.SetTexture(kernelFFTHorizontal, PropInputPing, src);
                fftShader.SetTexture(kernelFFTHorizontal, PropOutputPong, dst);
                fftShader.Dispatch(kernelFFTHorizontal, gx, gx, 1);

                // Swap ping-pong
                var temp = src;
                src = dst;
                dst = temp;
            }

            // Vertical FFT passes
            for (int stage = 0; stage < numStages; stage++) {
                int step = 1 << stage;
                fftShader.SetInt(PropStepSize, step);
                fftShader.SetTexture(kernelFFTVertical, PropInputPing, src);
                fftShader.SetTexture(kernelFFTVertical, PropOutputPong, dst);
                fftShader.Dispatch(kernelFFTVertical, gx, gx, 1);

                var temp = src;
                src = dst;
                dst = temp;
            }

            Graphics.Blit(src, spectrumTarget);
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
        private static readonly int PropSpectrumDy = Shader.PropertyToID("_SpectrumDy");
        private static readonly int PropSpectrumDxDz = Shader.PropertyToID("_SpectrumDxDz");
        private static readonly int PropInputPing = Shader.PropertyToID("_InputPing");
        private static readonly int PropOutputPong = Shader.PropertyToID("_OutputPong");
        private static readonly int PropWaterDataTex = Shader.PropertyToID("_WaterDataTex");
        private static readonly int PropNormalTex = Shader.PropertyToID("_NormalTex");
        #endregion
    }
}
