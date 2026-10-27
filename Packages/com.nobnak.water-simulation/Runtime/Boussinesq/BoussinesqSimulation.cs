using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Boussinesq water wave simulation.
    /// Incorporates frequency dispersion and bathymetric refraction, shoaling, and energy convergence.
    /// Based on Peregrine (1967) weakly nonlinear weakly dispersive Boussinesq equations.
    /// </summary>
    [ExecuteAlways]
    public class BoussinesqSimulation : WaterSimulationBase {
        public enum BathymetryType {
            Flat = 0,
            ShoalAndCape = 1,
            LinearSlope = 2,
            CustomTexture = 3
        }

        public enum InitialConditionType {
            Rest = 0,
            ContinuousWavemaker = 1,
            GaussianPulse = 2
        }

        #region serialized fields
        [Header("Boussinesq Physics")]
        [Tooltip("Dispersion factor alpha (1.0 = full Boussinesq dispersion, 0.0 = non-dispersive shallow water).")]
        [Range(0f, 1.2f)]
        [SerializeField] private float dispersionAlpha = 1.0f;

        [Tooltip("Bed friction / bottom drag coefficient.")]
        [Range(0f, 0.1f)]
        [SerializeField] private float bedFriction = 0.005f;

        [Header("Bathymetry (Sea Bed)")]
        [SerializeField] private BathymetryType bathymetryMode = BathymetryType.ShoalAndCape;
        [Tooltip("Deep sea still water depth [m].")]
        [SerializeField] private float deepWaterDepth = 15.0f;
        [Tooltip("Shallow shoal minimum water depth [m].")]
        [SerializeField] private float shallowWaterDepth = 2.5f;
        [SerializeField] private Texture2D customBathymetryTexture;

        [Header("Wave Excitation")]
        [SerializeField] private InitialConditionType initialCondition = InitialConditionType.ContinuousWavemaker;
        [Tooltip("Wavemaker wave amplitude [m].")]
        [SerializeField] private float wavemakerAmplitude = 0.8f;
        [Tooltip("Wavemaker wave frequency [Hz].")]
        [SerializeField] private float wavemakerFrequency = 0.4f;
        [Tooltip("Wavemaker grid Y position normalized [0..1] (0.05 = near offshore boundary).")]
        [Range(0.01f, 0.5f)]
        [SerializeField] private float wavemakerLocationNorm = 0.08f;

        [Header("Boundary")]
        [Range(4, 64)]
        [SerializeField] private int spongeWidth = 24;
        [SerializeField] private float spongeStrength = 3.0f;

        [Header("Compute Shader")]
        [SerializeField] private ComputeShader boussinesqShader;
        #endregion

        #region private fields
        private RenderTexture stateTexA;
        private RenderTexture stateTexB;
        private RenderTexture forceTex;
        private RenderTexture accelTexA;
        private RenderTexture accelTexB;
        private bool pingPong;
        private float wavemakerPhase;

        private int kernelInit;
        private int kernelComputeForce;
        private int kernelJacobiStep;
        private int kernelStep;
        private int kernelWavemaker;
        private int kernelNormal;
        #endregion

        #region properties
        public float DispersionAlpha {
            get => dispersionAlpha;
            set => dispersionAlpha = math.clamp(value, 0f, 1.5f);
        }

        public BathymetryType Bathymetry {
            get => bathymetryMode;
            set {
                bathymetryMode = value;
                UpdateBathymetryTexture();
            }
        }

        public InitialConditionType Condition {
            get => initialCondition;
            set => initialCondition = value;
        }

        public float WavemakerAmplitude {
            get => wavemakerAmplitude;
            set => wavemakerAmplitude = math.max(0f, value);
        }

        public float WavemakerFrequency {
            get => wavemakerFrequency;
            set => wavemakerFrequency = math.max(0.01f, value);
        }
        #endregion

        #region Unity lifecycle
        protected override void OnEnable() {
            ResolveComputeShader();
            base.OnEnable();
            UpdateBathymetryTexture();
        }
        #endregion

        #region public interface
        public void SetBathymetryType(BathymetryType type) {
            Bathymetry = type;
        }

        public void TriggerGaussianPulse(Vector2 uvCenter, float amplitude, float radius) {
            if (boussinesqShader == null || stateTexA == null) return;
            RenderTexture current = CurrentStateTex;

            // Initialize pulse directly
            int w = resolution.x;
            int h = resolution.y;
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    float2 uv = new float2((float)x / w, (float)y / h);
                    float d = math.distance(uv, (float2)uvCenter);
                    float rNorm = d / math.max(1e-4f, radius);
                    float eta = amplitude * math.exp(-3.0f * rNorm * rNorm);
                    pixels[y * w + x] = new Color(eta, 0f, 0f, 0f);
                }
            }

            var temp = new Texture2D(w, h, TextureFormat.RGBAHalf, false);
            temp.SetPixels(pixels);
            temp.Apply(false);
            Graphics.Blit(temp, current);
            DestroyImmediate(temp);
        }
        #endregion

        #region private methods
        private void ResolveComputeShader() {
            if (boussinesqShader == null) {
                boussinesqShader = Resources.Load<ComputeShader>("Boussinesq");
            }
        }

        protected override void InitializeTextures() {
            ReleaseTextures();

            int w = resolution.x;
            int h = resolution.y;

            stateTexA = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            stateTexB = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            forceTex = CreateSimulationTexture(w, h, RenderTextureFormat.RGHalf);
            accelTexA = CreateSimulationTexture(w, h, RenderTextureFormat.RGHalf);
            accelTexB = CreateSimulationTexture(w, h, RenderTextureFormat.RGHalf);
            normalTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            waterDataTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            bathymetryTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);

            CacheKernels();
        }

        private void CacheKernels() {
            if (boussinesqShader == null) return;
            kernelInit = boussinesqShader.FindKernel("CSInit");
            kernelComputeForce = boussinesqShader.FindKernel("CSComputeForce");
            kernelJacobiStep = boussinesqShader.FindKernel("CSJacobiStep");
            kernelStep = boussinesqShader.FindKernel("CSStep");
            kernelWavemaker = boussinesqShader.FindKernel("CSAddWavemaker");
            kernelNormal = boussinesqShader.FindKernel("CSComputeNormal");
        }

        protected override void ReleaseTextures() {
            ReleaseTexture(ref stateTexA);
            ReleaseTexture(ref stateTexB);
            ReleaseTexture(ref forceTex);
            ReleaseTexture(ref accelTexA);
            ReleaseTexture(ref accelTexB);
            ReleaseTexture(ref normalTex);
            ReleaseTexture(ref waterDataTex);
            ReleaseTexture(ref bathymetryTex);
        }

        protected override void OnResetSimulation() {
            if (boussinesqShader == null || stateTexA == null) return;

            wavemakerPhase = 0f;
            pingPong = false;

            boussinesqShader.SetInt(PropWidth, resolution.x);
            boussinesqShader.SetInt(PropHeight, resolution.y);

            boussinesqShader.SetTexture(kernelInit, PropStateOut, stateTexA);
            boussinesqShader.SetTexture(kernelInit, PropNormalTex, normalTex);
            boussinesqShader.SetTexture(kernelInit, PropWaterDataTex, waterDataTex);
            boussinesqShader.SetTexture(kernelInit, PropForceTex, forceTex);
            boussinesqShader.SetTexture(kernelInit, PropAccelPing, accelTexA);
            boussinesqShader.SetTexture(kernelInit, PropAccelPong, accelTexB);
            DispatchCompute(kernelInit);

            boussinesqShader.SetTexture(kernelInit, PropStateOut, stateTexB);
            DispatchCompute(kernelInit);

            UpdateBathymetryTexture();

            if (initialCondition == InitialConditionType.GaussianPulse) {
                TriggerGaussianPulse(new Vector2(0.5f, 0.5f), wavemakerAmplitude * 1.5f, 0.05f);
            }
            ComputeNormalAndPackOutput();
        }

        protected override void OnStepSimulation(float dt) {
            if (boussinesqShader == null || stateTexA == null) return;

            float dx = areaSize.x / resolution.x;
            // CFL stability check based on maximum wave speed c = sqrt(g * h_max)
            float maxC = math.sqrt(Gravity * deepWaterDepth);
            float maxDt = (dx / maxC) * 0.45f; // Safety factor for dispersion term
            int subSteps = math.max(1, (int)math.ceil(dt / maxDt));
            float subDt = dt / subSteps;

            for (int s = 0; s < subSteps; s++) {
                StepPhysics(subDt);
            }

            ComputeNormalAndPackOutput();
        }

        private RenderTexture CurrentStateTex => pingPong ? stateTexB : stateTexA;
        private RenderTexture NextStateTex    => pingPong ? stateTexA : stateTexB;

        private void StepPhysics(float subDt) {
            RenderTexture current = CurrentStateTex;
            RenderTexture next    = NextStateTex;

            float dx = areaSize.x / resolution.x;
            int wavemakerY = (int)(resolution.y * wavemakerLocationNorm);

            boussinesqShader.SetInt(PropWidth, resolution.x);
            boussinesqShader.SetInt(PropHeight, resolution.y);
            boussinesqShader.SetFloat(PropDeltaTime, subDt);
            boussinesqShader.SetFloat(PropGridSpacing, dx);
            boussinesqShader.SetFloat(PropGravity, Gravity);
            boussinesqShader.SetFloat(PropDispersionAlpha, dispersionAlpha);
            boussinesqShader.SetFloat(PropBedFriction, bedFriction);
            boussinesqShader.SetFloat(PropSpongeWidth, spongeWidth);
            boussinesqShader.SetFloat(PropSpongeStrength, spongeStrength);

            // 1. Compute Force
            boussinesqShader.SetTexture(kernelComputeForce, PropStateIn, current);
            boussinesqShader.SetTexture(kernelComputeForce, PropBathymetryTex, bathymetryTex);
            boussinesqShader.SetTexture(kernelComputeForce, PropForceTex, forceTex);
            boussinesqShader.SetTexture(kernelComputeForce, PropAccelPing, accelTexA);
            DispatchCompute(kernelComputeForce);

            // 2. Jacobi relaxation (4 iterations)
            RenderTexture jSrc = accelTexA;
            RenderTexture jDst = accelTexB;
            if (dispersionAlpha > 0.001f) {
                for (int iter = 0; iter < 4; iter++) {
                    boussinesqShader.SetTexture(kernelJacobiStep, PropBathymetryTex, bathymetryTex);
                    boussinesqShader.SetTexture(kernelJacobiStep, PropForceTex, forceTex);
                    boussinesqShader.SetTexture(kernelJacobiStep, PropAccelPing, jSrc);
                    boussinesqShader.SetTexture(kernelJacobiStep, PropAccelPong, jDst);
                    DispatchCompute(kernelJacobiStep);

                    var tmp = jSrc;
                    jSrc = jDst;
                    jDst = tmp;
                }
            }

            // 3. Step velocities and eta
            boussinesqShader.SetTexture(kernelStep, PropStateIn, current);
            boussinesqShader.SetTexture(kernelStep, PropStateOut, next);
            boussinesqShader.SetTexture(kernelStep, PropBathymetryTex, bathymetryTex);
            boussinesqShader.SetTexture(kernelStep, PropAccelPing, jSrc);
            DispatchCompute(kernelStep);

            // 4. Wavemaker
            if (initialCondition == InitialConditionType.ContinuousWavemaker) {
                wavemakerPhase += 2.0f * math.PI * wavemakerFrequency * subDt;
                boussinesqShader.SetFloat(PropWavemakerAmp, wavemakerAmplitude);
                boussinesqShader.SetFloat(PropWavemakerFreq, wavemakerFrequency);
                boussinesqShader.SetFloat(PropWavemakerPhase, wavemakerPhase);
                boussinesqShader.SetInt(PropWavemakerY, wavemakerY);

                boussinesqShader.SetTexture(kernelWavemaker, PropStateOut, next);
                boussinesqShader.SetTexture(kernelWavemaker, PropBathymetryTex, bathymetryTex);
                DispatchCompute(kernelWavemaker);
            }

            pingPong = !pingPong;
        }

        private void ComputeNormalAndPackOutput() {
            RenderTexture current = CurrentStateTex;
            float dx = areaSize.x / resolution.x;

            boussinesqShader.SetInt(PropWidth, resolution.x);
            boussinesqShader.SetInt(PropHeight, resolution.y);
            boussinesqShader.SetFloat(PropGridSpacing, dx);
            boussinesqShader.SetTexture(kernelNormal, PropStateOut, current);
            boussinesqShader.SetTexture(kernelNormal, PropNormalTex, normalTex);
            boussinesqShader.SetTexture(kernelNormal, PropWaterDataTex, waterDataTex);
            DispatchCompute(kernelNormal);
        }

        private void DispatchCompute(int kernel) {
            int gx = math.max(1, resolution.x / 8);
            int gy = math.max(1, resolution.y / 8);
            boussinesqShader.Dispatch(kernel, gx, gy, 1);
        }

        public void UpdateBathymetryTexture() {
            if (bathymetryTex == null) return;

            int w = resolution.x;
            int h = resolution.y;
            var pixels = new Color[w * h];

            for (int y = 0; y < h; y++) {
                float ny = (float)y / (h - 1); // 0 = offshore (deep), 1 = nearshore (shallow)
                for (int x = 0; x < w; x++) {
                    float nx = (float)x / (w - 1);
                    float depth = deepWaterDepth;

                    if (bathymetryMode == BathymetryType.Flat) {
                        depth = deepWaterDepth;
                    } else if (bathymetryMode == BathymetryType.LinearSlope) {
                        depth = math.lerp(deepWaterDepth, shallowWaterDepth, ny);
                    } else if (bathymetryMode == BathymetryType.ShoalAndCape) {
                        // Base slope from deep (ny = 0) to shallow (ny = 1)
                        float baseSlope = math.lerp(deepWaterDepth, shallowWaterDepth * 1.5f, ny);

                        // Diagonal shallow shoal: extends diagonally across middle
                        float shoalLine = math.abs((nx - 0.35f) - (ny - 0.45f) * 0.7f);
                        float shoalFactor = math.exp(-shoalLine * shoalLine / 0.02f);
                        float shoalDepth = shallowWaterDepth;

                        // Cape / Headland: jutting peninsula protruding from right shore (nx = 0.75, ny = 0.6)
                        float2 capeCenter = new float2(0.72f, 0.62f);
                        float distToCape = math.distance(new float2(nx, ny), capeCenter);
                        float capeFactor = math.exp(-distToCape * distToCape / 0.015f);

                        depth = math.lerp(baseSlope, shoalDepth, shoalFactor * 0.75f);
                        depth = math.lerp(depth, shallowWaterDepth * 0.5f, capeFactor * 0.85f);
                        depth = math.max(0.5f, depth);
                    }

                    // R: still water depth h, G: bed elevation z_b = -h
                    pixels[y * w + x] = new Color(depth, -depth, 0f, 1f);
                }
            }

            var tempTex = new Texture2D(w, h, TextureFormat.RGBAHalf, false);
            tempTex.SetPixels(pixels);
            tempTex.Apply(false);
            Graphics.Blit(tempTex, bathymetryTex);
            DestroyImmediate(tempTex);
        }
        #endregion

        #region constants / static
        private static readonly int PropWidth = Shader.PropertyToID("_Width");
        private static readonly int PropHeight = Shader.PropertyToID("_Height");
        private static readonly int PropDeltaTime = Shader.PropertyToID("_DeltaTime");
        private static readonly int PropGridSpacing = Shader.PropertyToID("_GridSpacing");
        private static readonly int PropGravity = Shader.PropertyToID("_Gravity");
        private static readonly int PropDispersionAlpha = Shader.PropertyToID("_DispersionAlpha");
        private static readonly int PropBedFriction = Shader.PropertyToID("_BedFriction");
        private static readonly int PropSpongeWidth = Shader.PropertyToID("_SpongeWidth");
        private static readonly int PropSpongeStrength = Shader.PropertyToID("_SpongeStrength");
        private static readonly int PropWavemakerAmp = Shader.PropertyToID("_WavemakerAmp");
        private static readonly int PropWavemakerFreq = Shader.PropertyToID("_WavemakerFreq");
        private static readonly int PropWavemakerPhase = Shader.PropertyToID("_WavemakerPhase");
        private static readonly int PropWavemakerY = Shader.PropertyToID("_WavemakerY");
        private static readonly int PropStateIn = Shader.PropertyToID("_StateIn");
        private static readonly int PropStateOut = Shader.PropertyToID("_StateOut");
        private static readonly int PropBathymetryTex = Shader.PropertyToID("_BathymetryTex");
        private static readonly int PropNormalTex = Shader.PropertyToID("_NormalTex");
        private static readonly int PropWaterDataTex = Shader.PropertyToID("_WaterDataTex");
        private static readonly int PropForceTex = Shader.PropertyToID("_ForceTex");
        private static readonly int PropAccelPing = Shader.PropertyToID("_AccelPing");
        private static readonly int PropAccelPong = Shader.PropertyToID("_AccelPong");
        #endregion
    }
}
