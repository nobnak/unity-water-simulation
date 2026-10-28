using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Boussinesq water wave simulation.
    /// Incorporates frequency dispersion and bathymetric refraction, shoaling, and energy convergence.
    /// Based on standard Peregrine (1967) weakly nonlinear weakly dispersive Boussinesq equations
    /// solved via 1D Tridiagonal Parallel Cyclic Reduction (PCR) + cross-derivative iterations in both
    /// Predictor and Corrector stages for 2nd-order temporal accuracy.
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
            GaussianPulse = 2,
            PeriodicPulses = 3,
            MovingSource = 4,
            InteractiveMouse = 5
        }

        public enum ImpulseShapeType {
            PlanarSoliton = 0,
            CircularPulse = 1
        }

        #region serialized fields
        [Header("Boussinesq Physics")]
        [Tooltip("Dispersion factor alpha (1.0 = standard Peregrine 1/3 Boussinesq dispersion, 0.0 = non-dispersive shallow water).")]
        [Range(0f, 1.2f)]
        [SerializeField] private float dispersionAlpha = 1.0f;

        [Tooltip("Number of alternating X/Z tridiagonal PCR sweeps per stage (1 sweep direct ADI).")]
        [Range(1, 4)]
        [SerializeField] private int dispersionSweeps = 1;

        [Tooltip("Bed friction / bottom drag coefficient.")]
        [Range(0f, 0.1f)]
        [SerializeField] private float bedFriction = 0.005f;

        [Header("Bathymetry (Sea Bed)")]
        [SerializeField] private BathymetryType bathymetryMode = BathymetryType.ShoalAndCape;
        [Tooltip("Deep sea still water depth [m]. In shallow water Boussinesq regime, keep depth <= 3-5% of domain size to avoid wave trapping.")]
        [SerializeField] private float deepWaterDepth = 3.5f;
        [Tooltip("Shallow shoal minimum water depth [m].")]
        [SerializeField] private float shallowWaterDepth = 0.8f;
        [SerializeField] private Texture2D customBathymetryTexture;

        [Header("Wave Excitation")]
        [SerializeField] private InitialConditionType initialCondition = InitialConditionType.ContinuousWavemaker;
        [SerializeField] private ImpulseShapeType impulseShape = ImpulseShapeType.PlanarSoliton;
        [Tooltip("Wavemaker / impulse wave amplitude [m].")]
        [SerializeField] private float wavemakerAmplitude = 0.35f;
        [Tooltip("Wavemaker wave frequency [Hz]. Keep below Peregrine cutoff frequency fc = sqrt(3g/h)/(2*pi) (~0.46Hz at 3.5m depth).")]
        [SerializeField] private float wavemakerFrequency = 0.15f;
        [Tooltip("Wavemaker grid Y position normalized [0..1] (0.08 = near offshore boundary).")]
        [Range(0.01f, 0.5f)]
        [SerializeField] private float wavemakerLocationNorm = 0.08f;
        [SerializeField] private Vector2 sourceCenter = new Vector2(0.5f, 0.12f);
        [SerializeField] private float sourceRadius = 0.06f;
        [SerializeField] private float pulseInterval = 4.5f;
        [SerializeField] private float movingSourceSpeed = 0.25f;

        [Header("Boundary")]
        [Range(4, 64)]
        [SerializeField] private int spongeWidth = 24;
        [SerializeField] private float spongeStrength = 3.0f;
        [Tooltip("If enabled, absorbs waves at left and right boundaries too. Disable for wave flume / planar waves to keep crest flat across width.")]
        [SerializeField] private bool sideWallSponge = false;

        [Header("Compute Shader")]
        [SerializeField] private ComputeShader boussinesqShader;
        #endregion

        #region private fields
        private RenderTexture stateTexA;
        private RenderTexture stateTexB;
        private RenderTexture predTex;
        private RenderTexture forceTex;
        private RenderTexture forcePredTex;
        private RenderTexture accelTexA;
        private RenderTexture accelTexB;
        private bool pingPong;
        private float wavemakerPhase;
        private float pulseTimer;
        private float movingPhase;

        private int kernelInit;
        private int kernelPredict;
        private int kernelCorrect;
        private int kernelTridiagonalX;
        private int kernelTridiagonalZ;
        private int kernelApplyPredictedDispersion;
        private int kernelApplyDispersion;
        private int kernelImpulse;
        private int kernelNormal;
        private int kernelFilter;
        #endregion

        #region properties
        public float DispersionAlpha {
            get => dispersionAlpha;
            set => dispersionAlpha = math.clamp(value, 0f, 1.5f);
        }

        public int DispersionSweeps {
            get => dispersionSweeps;
            set => dispersionSweeps = math.clamp(value, 1, 6);
        }

        public bool SideWallSponge {
            get => sideWallSponge;
            set => sideWallSponge = value;
        }

        public float BedFriction {
            get => bedFriction;
            set => bedFriction = math.max(0f, value);
        }

        public BathymetryType Bathymetry {
            get => bathymetryMode;
            set {
                if (bathymetryMode == value) return;
                bathymetryMode = value;
                UpdateBathymetryTexture();
            }
        }

        public Texture2D CustomBathymetryTexture {
            get => customBathymetryTexture;
            set {
                customBathymetryTexture = value;
                if (bathymetryMode == BathymetryType.CustomTexture) UpdateBathymetryTexture();
            }
        }

        public InitialConditionType Condition {
            get => initialCondition;
            set => initialCondition = value;
        }

        public ImpulseShapeType ImpulseShape {
            get => impulseShape;
            set => impulseShape = value;
        }

        public float WavemakerAmplitude {
            get => wavemakerAmplitude;
            set => wavemakerAmplitude = math.max(0f, value);
        }

        public float WavemakerFrequency {
            get => wavemakerFrequency;
            set => wavemakerFrequency = math.max(0.01f, value);
        }

        public float PulseInterval {
            get => pulseInterval;
            set => pulseInterval = math.max(0.1f, value);
        }

        public float DeepWaterDepth {
            get => deepWaterDepth;
            set {
                if (math.abs(deepWaterDepth - value) < 1e-4f) return;
                deepWaterDepth = math.max(0.2f, value);
                UpdateBathymetryTexture();
            }
        }

        public float ShallowWaterDepth {
            get => shallowWaterDepth;
            set {
                if (math.abs(shallowWaterDepth - value) < 1e-4f) return;
                shallowWaterDepth = math.clamp(value, 0.1f, deepWaterDepth);
                UpdateBathymetryTexture();
            }
        }

        public float CutoffFrequency => (1.0f / (2.0f * math.PI)) * math.sqrt(3.0f * Gravity / math.max(0.1f, deepWaterDepth));
        public float DepthToDomainRatio => deepWaterDepth / math.max(0.1f, math.min(areaSize.x, areaSize.y));
        public bool IsDepthTooDeepForShallowWater => DepthToDomainRatio > 0.05f;
        public bool IsFrequencyAboveCutoff => wavemakerFrequency >= CutoffFrequency * 0.85f;
        #endregion

        #region Unity lifecycle
        protected virtual void OnValidate() {
            if (deepWaterDepth < shallowWaterDepth) shallowWaterDepth = deepWaterDepth;
            if (wavemakerFrequency <= 0f) wavemakerFrequency = 0.01f;
        }

        protected override void OnEnable() {
            ResolveComputeShader();
            base.OnEnable();
            UpdateBathymetryTexture();
        }

        protected override void Update() {
            HandleMouseInput();
            base.Update();
        }
        #endregion

        #region public interface
        public void ClampDepthToSafeLimit() {
            float maxSafeDepth = math.min(areaSize.x, areaSize.y) * 0.04f;
            DeepWaterDepth = math.max(0.5f, maxSafeDepth);
            if (shallowWaterDepth > deepWaterDepth * 0.5f) {
                ShallowWaterDepth = deepWaterDepth * 0.25f;
            }
        }

        public void ClampFrequencyToSafeLimit() {
            WavemakerFrequency = CutoffFrequency * 0.6f;
        }

        public void SetBathymetryType(BathymetryType type) {
            Bathymetry = type;
        }

        public void TriggerImpulse() {
            Vector2 center = (impulseShape == ImpulseShapeType.PlanarSoliton)
                ? new Vector2(0.5f, math.max(0.10f, wavemakerLocationNorm))
                : sourceCenter;
            float amp = (impulseShape == ImpulseShapeType.PlanarSoliton)
                ? wavemakerAmplitude * 1.5f
                : wavemakerAmplitude;
            TriggerImpulseOnTarget(CurrentStateTex, center, amp, sourceRadius, impulseShape);
            ComputeNormalAndPackOutput();
        }

        public void TriggerImpulse(Vector2 uvCenter, float amplitude, float radius, ImpulseShapeType shape) {
            TriggerImpulseOnTarget(CurrentStateTex, uvCenter, amplitude, radius, shape);
            ComputeNormalAndPackOutput();
        }

        public void TriggerGaussianPulse(Vector2 uvCenter, float amplitude, float radius) {
            TriggerImpulseOnTarget(CurrentStateTex, uvCenter, amplitude, radius, ImpulseShapeType.CircularPulse);
            ComputeNormalAndPackOutput();
        }

        public void TriggerImpulseOnTarget(RenderTexture targetTex, Vector2 uvCenter, float amplitude, float radius, ImpulseShapeType shape) {
            if (boussinesqShader == null || targetTex == null || bathymetryTex == null) return;

            boussinesqShader.SetInt(PropWidth, resolution.x);
            boussinesqShader.SetInt(PropHeight, resolution.y);
            boussinesqShader.SetFloat(PropGravity, Gravity);
            boussinesqShader.SetFloat(PropSpongeWidth, spongeWidth);
            boussinesqShader.SetInt(PropSideWallSponge, sideWallSponge ? 1 : 0);
            boussinesqShader.SetVector(PropImpulseCenter, new Vector4(uvCenter.x, uvCenter.y, 0f, 0f));
            boussinesqShader.SetFloat(PropImpulseRadius, radius);
            boussinesqShader.SetFloat(PropImpulseStrength, amplitude);
            boussinesqShader.SetInt(PropImpulseShape, (int)shape);

            boussinesqShader.SetTexture(kernelImpulse, PropBathymetryTex, bathymetryTex);
            boussinesqShader.SetTexture(kernelImpulse, PropStateOut, targetTex);
            DispatchCompute2D(kernelImpulse);
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

            int w = math.min(resolution.x, MaxTridiagonalN);
            int h = math.min(resolution.y, MaxTridiagonalN);
            resolution = new Vector2Int(w, h);

            stateTexA = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBFloat);
            stateTexB = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBFloat);
            predTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBFloat);
            forceTex = CreateSimulationTexture(w, h, RenderTextureFormat.RGFloat);
            forcePredTex = CreateSimulationTexture(w, h, RenderTextureFormat.RGFloat);
            accelTexA = CreateSimulationTexture(w, h, RenderTextureFormat.RGFloat);
            accelTexB = CreateSimulationTexture(w, h, RenderTextureFormat.RGFloat);
            normalTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            waterDataTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            bathymetryTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);

            CacheKernels();
        }

        private void CacheKernels() {
            if (boussinesqShader == null) return;
            kernelInit = boussinesqShader.FindKernel("CSInit");
            kernelPredict = boussinesqShader.FindKernel("CSPredict");
            kernelCorrect = boussinesqShader.FindKernel("CSCorrect");
            kernelTridiagonalX = boussinesqShader.FindKernel("CSTridiagonalX");
            kernelTridiagonalZ = boussinesqShader.FindKernel("CSTridiagonalZ");
            kernelApplyPredictedDispersion = boussinesqShader.FindKernel("CSApplyPredictedDispersion");
            kernelApplyDispersion = boussinesqShader.FindKernel("CSApplyDispersion");
            kernelImpulse = boussinesqShader.FindKernel("CSAddImpulse");
            kernelNormal = boussinesqShader.FindKernel("CSComputeNormal");
            kernelFilter = boussinesqShader.FindKernel("CSFilterHighFrequency");
        }

        protected override void ReleaseTextures() {
            ReleaseTexture(ref stateTexA);
            ReleaseTexture(ref stateTexB);
            ReleaseTexture(ref predTex);
            ReleaseTexture(ref forceTex);
            ReleaseTexture(ref forcePredTex);
            ReleaseTexture(ref accelTexA);
            ReleaseTexture(ref accelTexB);
            ReleaseTexture(ref normalTex);
            ReleaseTexture(ref waterDataTex);
            ReleaseTexture(ref bathymetryTex);
        }

        protected override void OnResetSimulation() {
            if (boussinesqShader == null || stateTexA == null) return;

            wavemakerPhase = 0f;
            pulseTimer = 0f;
            movingPhase = 0f;
            pingPong = false;
            lastDispatchCount = 0;

            boussinesqShader.SetInt(PropWidth, resolution.x);
            boussinesqShader.SetInt(PropHeight, resolution.y);

            boussinesqShader.SetTexture(kernelInit, PropStateOut, stateTexA);
            boussinesqShader.SetTexture(kernelInit, PropNormalTex, normalTex);
            boussinesqShader.SetTexture(kernelInit, PropWaterDataTex, waterDataTex);
            boussinesqShader.SetTexture(kernelInit, PropForceTex, forceTex);
            boussinesqShader.SetTexture(kernelInit, PropForcePredTex, forcePredTex);
            boussinesqShader.SetTexture(kernelInit, PropAccelPing, accelTexA);
            boussinesqShader.SetTexture(kernelInit, PropAccelPong, accelTexB);
            DispatchCompute2D(kernelInit);

            boussinesqShader.SetTexture(kernelInit, PropStateOut, stateTexB);
            DispatchCompute2D(kernelInit);

            boussinesqShader.SetTexture(kernelInit, PropStateOut, predTex);
            DispatchCompute2D(kernelInit);

            UpdateBathymetryTexture();

            if (initialCondition == InitialConditionType.GaussianPulse) {
                if (bathymetryMode == BathymetryType.Flat && math.abs(deepWaterDepth - 5.0f) < 0.1f) {
                    // Scene 04 comparison compatibility: centered circular C^2 pulse with exact wavemakerAmplitude
                    TriggerImpulseOnTarget(CurrentStateTex, sourceCenter, wavemakerAmplitude, sourceRadius, ImpulseShapeType.CircularPulse);
                } else {
                    TriggerImpulse();
                }
            } else if (initialCondition == InitialConditionType.PeriodicPulses) {
                TriggerImpulse();
            }
            ComputeNormalAndPackOutput();
        }

        protected override void OnStepSimulation(float dt) {
            if (boussinesqShader == null || stateTexA == null) return;

            float dx = areaSize.x / resolution.x;
            float dz = areaSize.y / resolution.y;
            float minGrid = math.min(dx, dz);

            // Nonlinear CFL stability check including wave amplitude and orbital particle velocity
            float ampBound = math.max(0.1f, wavemakerAmplitude * 1.5f);
            float hMax = math.max(0.5f, deepWaterDepth + ampBound);
            float cMax = math.sqrt(Gravity * hMax);
            float uBound = cMax * (ampBound / math.max(0.5f, shallowWaterDepth));
            float maxWaveSpeed = (cMax + uBound) * math.SQRT2;
            float maxDt = (minGrid / maxWaveSpeed) * 0.55f;

            int subSteps = math.max(1, (int)math.ceil(dt / maxDt));
            lastSubStepCount = subSteps;
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
            float dz = areaSize.y / resolution.y;
            int wavemakerY = (int)(resolution.y * wavemakerLocationNorm);
            float activeAmp = (initialCondition == InitialConditionType.ContinuousWavemaker) ? wavemakerAmplitude : 0f;

            boussinesqShader.SetInt(PropWidth, resolution.x);
            boussinesqShader.SetInt(PropHeight, resolution.y);
            boussinesqShader.SetFloat(PropDeltaTime, subDt);
            boussinesqShader.SetFloat(PropGridSpacing, dx);
            boussinesqShader.SetFloat(PropGridSpacingZ, dz);
            boussinesqShader.SetFloat(PropGravity, Gravity);
            boussinesqShader.SetFloat(PropDispersionAlpha, dispersionAlpha);
            boussinesqShader.SetFloat(PropBedFriction, bedFriction);
            boussinesqShader.SetFloat(PropSpongeWidth, spongeWidth);
            boussinesqShader.SetFloat(PropSpongeStrength, spongeStrength);
            boussinesqShader.SetInt(PropSideWallSponge, sideWallSponge ? 1 : 0);
            boussinesqShader.SetInt(PropStepParity, pingPong ? 1 : 0);

            boussinesqShader.SetFloat(PropWavemakerAmp, activeAmp);
            boussinesqShader.SetFloat(PropWavemakerFreq, wavemakerFrequency);
            boussinesqShader.SetFloat(PropWavemakerPhase, wavemakerPhase);
            boussinesqShader.SetInt(PropWavemakerY, wavemakerY);

            // 1. Predictor step (Forward/Backward differences -> forceTex & predTex)
            boussinesqShader.SetTexture(kernelPredict, PropStateIn, current);
            boussinesqShader.SetTexture(kernelPredict, PropStatePred, predTex);
            boussinesqShader.SetTexture(kernelPredict, PropBathymetryTex, bathymetryTex);
            boussinesqShader.SetTexture(kernelPredict, PropForceTex, forceTex);
            boussinesqShader.SetTexture(kernelPredict, PropAccelPing, accelTexA);
            DispatchCompute2D(kernelPredict);

            // 1b. Predictor Peregrine dispersion solve a^(1) = L^{-1}(f^n) -> u*, w*
            if (dispersionAlpha > 0.001f) {
                SolvePeregrineDispersion();

                boussinesqShader.SetTexture(kernelApplyPredictedDispersion, PropStatePred, predTex);
                boussinesqShader.SetTexture(kernelApplyPredictedDispersion, PropAccelPing, accelTexA);
                boussinesqShader.SetTexture(kernelApplyPredictedDispersion, PropForcePredTex, forcePredTex);
                DispatchCompute2D(kernelApplyPredictedDispersion);
            }

            // 2. Corrector step (Opposite differences on predTex -> forceTex & next)
            boussinesqShader.SetTexture(kernelCorrect, PropStateIn, current);
            boussinesqShader.SetTexture(kernelCorrect, PropStatePred, predTex);
            boussinesqShader.SetTexture(kernelCorrect, PropStateOut, next);
            boussinesqShader.SetTexture(kernelCorrect, PropBathymetryTex, bathymetryTex);
            boussinesqShader.SetTexture(kernelCorrect, PropForceTex, forceTex);
            boussinesqShader.SetTexture(kernelCorrect, PropForcePredTex, forcePredTex);
            boussinesqShader.SetTexture(kernelCorrect, PropAccelPing, accelTexA);
            DispatchCompute2D(kernelCorrect);

            // 3. Corrector Peregrine dispersion solve a^(2) = L^{-1}(f*) -> u^{n+1} = u^n + 0.5*dt*(a^(1) + a^(2))
            if (dispersionAlpha > 0.001f) {
                SolvePeregrineDispersion();

                boussinesqShader.SetTexture(kernelApplyDispersion, PropStateIn, next);
                boussinesqShader.SetTexture(kernelApplyDispersion, PropStateOut, predTex);
                boussinesqShader.SetTexture(kernelApplyDispersion, PropForcePredTex, forcePredTex);
                boussinesqShader.SetTexture(kernelApplyDispersion, PropAccelPing, accelTexA);
                boussinesqShader.SetTexture(kernelApplyDispersion, PropBathymetryTex, bathymetryTex);
                DispatchCompute2D(kernelApplyDispersion);

                // 4. Biharmonic high-frequency filter (eliminates 2dx sawtooth modes)
                boussinesqShader.SetTexture(kernelFilter, PropStateIn, predTex);
                boussinesqShader.SetTexture(kernelFilter, PropStateOut, next);
                DispatchCompute2D(kernelFilter);
            } else {
                boussinesqShader.SetTexture(kernelFilter, PropStateIn, next);
                boussinesqShader.SetTexture(kernelFilter, PropStateOut, predTex);
                DispatchCompute2D(kernelFilter);
                Graphics.CopyTexture(predTex, next);
            }

            if (initialCondition == InitialConditionType.ContinuousWavemaker) {
                wavemakerPhase += 2.0f * math.PI * wavemakerFrequency * subDt;
            } else {
                ApplySourceExcitation(next, subDt);
            }

            pingPong = !pingPong;
        }

        private void SolvePeregrineDispersion() {
            int rowsZ = math.max(1, resolution.y);
            int colsX = math.max(1, resolution.x);

            // Pass 1: CSTridiagonalX along rows (solves (I - beta_x d_x^2) a* = f)
            boussinesqShader.SetTexture(kernelTridiagonalX, PropBathymetryTex, bathymetryTex);
            boussinesqShader.SetTexture(kernelTridiagonalX, PropForceTex, forceTex);
            boussinesqShader.SetTexture(kernelTridiagonalX, PropAccelPong, accelTexB);
            boussinesqShader.Dispatch(kernelTridiagonalX, rowsZ, 1, 1);
            lastDispatchCount++;

            // Pass 2: CSTridiagonalZ along columns (solves (I - beta_z d_z^2) a = a*)
            boussinesqShader.SetTexture(kernelTridiagonalZ, PropBathymetryTex, bathymetryTex);
            boussinesqShader.SetTexture(kernelTridiagonalZ, PropAccelPong, accelTexB);
            boussinesqShader.SetTexture(kernelTridiagonalZ, PropAccelPing, accelTexA);
            boussinesqShader.Dispatch(kernelTridiagonalZ, colsX, 1, 1);
            lastDispatchCount++;
        }

        private void ApplySourceExcitation(RenderTexture target, float subDt) {
            float interval = PulseInterval;
            if (initialCondition == InitialConditionType.PeriodicPulses) {
                pulseTimer += subDt;
                if (pulseTimer >= interval) {
                    pulseTimer %= interval;
                    Vector2 center = (impulseShape == ImpulseShapeType.PlanarSoliton)
                        ? new Vector2(0.5f, math.max(0.10f, wavemakerLocationNorm))
                        : sourceCenter;
                    TriggerImpulseOnTarget(target, center, wavemakerAmplitude * 1.5f, sourceRadius, impulseShape);
                }
            } else if (initialCondition == InitialConditionType.MovingSource) {
                movingPhase += movingSourceSpeed * subDt;
                float mx = 0.5f + 0.28f * math.cos(movingPhase);
                float mz = 0.48f + 0.22f * math.sin(movingPhase * 2f);
                pulseTimer += subDt;
                if (pulseTimer >= interval) {
                    pulseTimer %= interval;
                    TriggerImpulseOnTarget(target, new Vector2(mx, mz), wavemakerAmplitude * 1.2f, sourceRadius * 0.85f, ImpulseShapeType.CircularPulse);
                }
            }
        }

        private void HandleMouseInput() {
            if (initialCondition != InitialConditionType.InteractiveMouse) return;

            Vector3 screenPos = Vector3.zero;
            bool isPressed = false;

#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed) {
                Vector2 pos = mouse.position.ReadValue();
                screenPos = new Vector3(pos.x, pos.y, 0f);
                isPressed = true;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetMouseButton(0)) {
                screenPos = Input.mousePosition;
                isPressed = true;
            }
#endif

            if (!isPressed) return;

            Ray ray = Camera.main != null ? Camera.main.ScreenPointToRay(screenPos) : default;
            Plane plane = new Plane(Vector3.up, transform.position);

            if (plane.Raycast(ray, out float enter)) {
                Vector3 hitPoint = ray.GetPoint(enter);
                Vector3 local = transform.InverseTransformPoint(hitPoint);
                float u = (local.x / areaSize.x) + 0.5f;
                float v = (local.z / areaSize.y) + 0.5f;

                if (u >= 0f && u <= 1f && v >= 0f && v <= 1f) {
                    TriggerImpulse(new Vector2(u, v), wavemakerAmplitude * 0.7f, sourceRadius * 0.85f, ImpulseShapeType.CircularPulse);
                }
            }
        }

        private void ComputeNormalAndPackOutput() {
            RenderTexture current = CurrentStateTex;
            float dx = areaSize.x / resolution.x;
            float dz = areaSize.y / resolution.y;

            boussinesqShader.SetInt(PropWidth, resolution.x);
            boussinesqShader.SetInt(PropHeight, resolution.y);
            boussinesqShader.SetFloat(PropGridSpacing, dx);
            boussinesqShader.SetFloat(PropGridSpacingZ, dz);
            boussinesqShader.SetTexture(kernelNormal, PropStateOut, current);
            boussinesqShader.SetTexture(kernelNormal, PropNormalTex, normalTex);
            boussinesqShader.SetTexture(kernelNormal, PropWaterDataTex, waterDataTex);
            DispatchCompute2D(kernelNormal);
        }

        private void DispatchCompute2D(int kernel) {
            int gx = math.max(1, (resolution.x + 7) / 8);
            int gy = math.max(1, (resolution.y + 7) / 8);
            boussinesqShader.Dispatch(kernel, gx, gy, 1);
            lastDispatchCount++;
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
                        float baseSlope = math.lerp(deepWaterDepth, shallowWaterDepth * 1.5f, ny);

                        float shoalLine = math.abs((nx - 0.35f) - (ny - 0.45f) * 0.7f);
                        float shoalFactor = math.exp(-shoalLine * shoalLine / 0.02f);
                        float shoalDepth = shallowWaterDepth;

                        float2 capeCenter = new float2(0.72f, 0.62f);
                        float distToCape = math.distance(new float2(nx, ny), capeCenter);
                        float capeFactor = math.exp(-distToCape * distToCape / 0.015f);

                        depth = math.lerp(baseSlope, shoalDepth, shoalFactor * 0.75f);
                        depth = math.lerp(depth, shallowWaterDepth * 0.5f, capeFactor * 0.85f);
                        depth = math.max(0.5f, depth);
                    } else if (bathymetryMode == BathymetryType.CustomTexture && customBathymetryTexture != null) {
                        float t = customBathymetryTexture.GetPixelBilinear(nx, ny).r;
                        depth = math.max(0.5f, math.lerp(shallowWaterDepth, deepWaterDepth, t));
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

            bathymetryVersion++;
        }
        #endregion

        #region constants / static
        private const int MaxTridiagonalN = 512;

        private static readonly int PropWidth = Shader.PropertyToID("_Width");
        private static readonly int PropHeight = Shader.PropertyToID("_Height");
        private static readonly int PropDeltaTime = Shader.PropertyToID("_DeltaTime");
        private static readonly int PropGridSpacing = Shader.PropertyToID("_GridSpacing");
        private static readonly int PropGridSpacingZ = Shader.PropertyToID("_GridSpacingZ");
        private static readonly int PropGravity = Shader.PropertyToID("_Gravity");
        private static readonly int PropDispersionAlpha = Shader.PropertyToID("_DispersionAlpha");
        private static readonly int PropBedFriction = Shader.PropertyToID("_BedFriction");
        private static readonly int PropSpongeWidth = Shader.PropertyToID("_SpongeWidth");
        private static readonly int PropSpongeStrength = Shader.PropertyToID("_SpongeStrength");
        private static readonly int PropSideWallSponge = Shader.PropertyToID("_SideWallSponge");
        private static readonly int PropStepParity = Shader.PropertyToID("_StepParity");
        private static readonly int PropWavemakerAmp = Shader.PropertyToID("_WavemakerAmp");
        private static readonly int PropWavemakerFreq = Shader.PropertyToID("_WavemakerFreq");
        private static readonly int PropWavemakerPhase = Shader.PropertyToID("_WavemakerPhase");
        private static readonly int PropWavemakerY = Shader.PropertyToID("_WavemakerY");
        private static readonly int PropImpulseCenter = Shader.PropertyToID("_ImpulseCenter");
        private static readonly int PropImpulseRadius = Shader.PropertyToID("_ImpulseRadius");
        private static readonly int PropImpulseStrength = Shader.PropertyToID("_ImpulseStrength");
        private static readonly int PropImpulseShape = Shader.PropertyToID("_ImpulseShape");
        private static readonly int PropStateIn = Shader.PropertyToID("_StateIn");
        private static readonly int PropStatePred = Shader.PropertyToID("_StatePred");
        private static readonly int PropStateOut = Shader.PropertyToID("_StateOut");
        private static readonly int PropBathymetryTex = Shader.PropertyToID("_BathymetryTex");
        private static readonly int PropNormalTex = Shader.PropertyToID("_NormalTex");
        private static readonly int PropWaterDataTex = Shader.PropertyToID("_WaterDataTex");
        private static readonly int PropForceTex = Shader.PropertyToID("_ForceTex");
        private static readonly int PropForcePredTex = Shader.PropertyToID("_ForcePredTex");
        private static readonly int PropAccelPing = Shader.PropertyToID("_AccelPing");
        private static readonly int PropAccelPong = Shader.PropertyToID("_AccelPong");
        #endregion
    }
}
