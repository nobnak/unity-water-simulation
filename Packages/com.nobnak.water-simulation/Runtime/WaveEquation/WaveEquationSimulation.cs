using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// 2D Linear Wave Equation simulation.
    /// Fast and responsive ripple simulation with obstacle reflection, diffraction, and interference.
    /// </summary>
    [ExecuteAlways]
    public class WaveEquationSimulation : WaterSimulationBase {
        public enum InputMode {
            None = 0,
            SingleImpulse = 1,
            ContinuousOscillator = 2,
            MovingSource = 3,
            InteractiveMouse = 4
        }

        public enum ObstaclePattern {
            None = 0,
            SingleSlit = 1,
            DoubleSlit = 2,
            ObstaclePillars = 3,
            CustomTexture = 4
        }

        #region serialized fields
        [Header("Wave Physics")]
        [Tooltip("Wave propagation speed c [m/s].")]
        [SerializeField] private float waveSpeed = 8.0f;

        [Tooltip("Natural damping coefficient gamma [1/s].")]
        [Range(0f, 2f)]
        [SerializeField] private float damping = 0.05f;

        [Header("Boundary")]
        [Tooltip("Enable sponge absorbing boundary layer on perimeter.")]
        [SerializeField] private bool enableSpongeBoundary = true;

        [Tooltip("Width of sponge boundary layer in cells.")]
        [Range(4, 64)]
        [SerializeField] private int spongeWidth = 24;

        [Tooltip("Damping factor at boundary edge.")]
        [SerializeField] private float spongeStrength = 3.0f;

        [Header("Obstacles & Slits")]
        [SerializeField] private ObstaclePattern obstaclePattern = ObstaclePattern.DoubleSlit;
        [SerializeField] private Texture2D customObstacleTexture;

        [Header("Wave Source / Excitation")]
        [SerializeField] private InputMode sourceMode = InputMode.SingleImpulse;
        [SerializeField] private Vector2 sourceCenter = new Vector2(0.5f, 0.25f);
        [SerializeField] private float sourceRadius = 0.03f;
        [SerializeField] private float sourceAmplitude = 1.0f;
        [SerializeField] private float oscillatorFrequency = 1.5f;
        [SerializeField] private float movingSourceSpeed = 0.25f;

        [Header("Compute Shader")]
        [SerializeField] private ComputeShader waveShader;
        #endregion

        #region private fields
        private RenderTexture stateTexA;
        private RenderTexture stateTexB;
        private RenderTexture obstacleTex;
        private bool pingPong;
        private float oscillatorPhase;
        private float movingPhase;

        private int kernelInit;
        private int kernelStep;
        private int kernelImpulse;
        private int kernelOscillator;
        private int kernelNormal;
        #endregion

        #region properties
        public float WaveSpeed {
            get => waveSpeed;
            set => waveSpeed = math.max(0.1f, value);
        }

        public float Damping {
            get => damping;
            set => damping = math.max(0f, value);
        }

        public InputMode SourceMode {
            get => sourceMode;
            set => sourceMode = value;
        }

        public ObstaclePattern Pattern {
            get => obstaclePattern;
            set {
                obstaclePattern = value;
                UpdateObstacleTexture();
            }
        }
        #endregion

        #region Unity lifecycle
        protected override void OnEnable() {
            ResolveComputeShader();
            base.OnEnable();
            UpdateObstacleTexture();
        }

        protected override void Update() {
            HandleMouseInput();
            base.Update();
        }
        #endregion

        #region public interface
        public void TriggerImpulse(Vector2 uvPosition, float amplitude, float radius) {
            if (waveShader == null || stateTexA == null) return;

            waveShader.SetInt(PropWidth, resolution.x);
            waveShader.SetInt(PropHeight, resolution.y);
            waveShader.SetVector(PropInputCenter, new Vector4(uvPosition.x, uvPosition.y, 0, 0));
            waveShader.SetFloat(PropInputRadius, radius);
            waveShader.SetFloat(PropInputStrength, amplitude);
            waveShader.SetTexture(kernelImpulse, PropNextState, CurrentStateTex);

            DispatchCompute(kernelImpulse);
        }

        public void TriggerImpulse() {
            TriggerImpulse(sourceCenter, sourceAmplitude, sourceRadius);
        }

        public void SetObstaclePattern(ObstaclePattern pattern) {
            Pattern = pattern;
        }
        #endregion

        #region private methods
        private void ResolveComputeShader() {
            if (waveShader == null) {
                waveShader = Resources.Load<ComputeShader>("WaveEquation");
            }
        }

        protected override void InitializeTextures() {
            ReleaseTextures();

            int w = resolution.x;
            int h = resolution.y;

            stateTexA = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            stateTexB = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            normalTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            waterDataTex = CreateSimulationTexture(w, h, RenderTextureFormat.ARGBHalf);
            obstacleTex = CreateSimulationTexture(w, h, RenderTextureFormat.R8);

            CacheKernels();
        }

        private void CacheKernels() {
            if (waveShader == null) return;
            kernelInit = waveShader.FindKernel("CSInit");
            kernelStep = waveShader.FindKernel("CSStep");
            kernelImpulse = waveShader.FindKernel("CSAddImpulse");
            kernelOscillator = waveShader.FindKernel("CSAddOscillator");
            kernelNormal = waveShader.FindKernel("CSComputeNormal");
        }

        protected override void ReleaseTextures() {
            ReleaseTexture(ref stateTexA);
            ReleaseTexture(ref stateTexB);
            ReleaseTexture(ref normalTex);
            ReleaseTexture(ref waterDataTex);
            ReleaseTexture(ref obstacleTex);
        }

        protected override void OnResetSimulation() {
            if (waveShader == null || stateTexA == null) return;

            oscillatorPhase = 0f;
            movingPhase = 0f;
            pingPong = false;

            waveShader.SetInt(PropWidth, resolution.x);
            waveShader.SetInt(PropHeight, resolution.y);

            waveShader.SetTexture(kernelInit, PropNextState, stateTexA);
            waveShader.SetTexture(kernelInit, PropNormalTex, normalTex);
            waveShader.SetTexture(kernelInit, PropWaterDataTex, waterDataTex);
            DispatchCompute(kernelInit);

            waveShader.SetTexture(kernelInit, PropNextState, stateTexB);
            waveShader.SetTexture(kernelInit, PropWaterDataTex, waterDataTex);
            DispatchCompute(kernelInit);

            UpdateObstacleTexture();

            if (sourceMode == InputMode.SingleImpulse) {
                TriggerImpulse(sourceCenter, sourceAmplitude, sourceRadius);
            }
            ComputeNormalAndPackOutput();
        }

        protected override void OnStepSimulation(float dt) {
            if (waveShader == null || stateTexA == null) return;

            float dx = areaSize.x / resolution.x;
            // CFL stability check: dt <= dx / (sqrt(2) * c)
            float maxDt = (dx / (math.SQRT2 * waveSpeed)) * 0.95f;
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

            waveShader.SetInt(PropWidth, resolution.x);
            waveShader.SetInt(PropHeight, resolution.y);
            waveShader.SetFloat(PropDeltaTime, subDt);
            waveShader.SetFloat(PropGridSpacing, dx);
            waveShader.SetFloat(PropWaveSpeed, waveSpeed);
            waveShader.SetFloat(PropDamping, damping);
            waveShader.SetFloat(PropSpongeWidth, spongeWidth);
            waveShader.SetFloat(PropSpongeStrength, spongeStrength);
            waveShader.SetInt(PropIsSpongeEnabled, enableSpongeBoundary ? 1 : 0);

            waveShader.SetTexture(kernelStep, PropCurrentState, current);
            waveShader.SetTexture(kernelStep, PropNextState, next);
            waveShader.SetTexture(kernelStep, PropObstacleTex, obstacleTex);

            DispatchCompute(kernelStep);

            // Apply source excitation
            ApplySourceExcitation(next, subDt);

            pingPong = !pingPong;
        }

        private void ApplySourceExcitation(RenderTexture target, float subDt) {
            if (sourceMode == InputMode.ContinuousOscillator) {
                oscillatorPhase += 2.0f * math.PI * oscillatorFrequency * subDt;
                waveShader.SetVector(PropInputCenter, new Vector4(sourceCenter.x, sourceCenter.y, 0, 0));
                waveShader.SetFloat(PropInputRadius, sourceRadius);
                waveShader.SetFloat(PropInputStrength, sourceAmplitude);
                waveShader.SetFloat(PropOscillatorPhase, oscillatorPhase);
                waveShader.SetFloat(PropDeltaTime, subDt);
                waveShader.SetTexture(kernelOscillator, PropNextState, target);
                DispatchCompute(kernelOscillator);
            } else if (sourceMode == InputMode.MovingSource) {
                movingPhase += movingSourceSpeed * subDt;
                // Move along an oval or figure-8 path
                float mx = 0.5f + 0.3f * math.cos(movingPhase);
                float mz = 0.5f + 0.2f * math.sin(movingPhase * 2f);
                oscillatorPhase += 2.0f * math.PI * oscillatorFrequency * subDt;

                waveShader.SetVector(PropInputCenter, new Vector4(mx, mz, 0, 0));
                waveShader.SetFloat(PropInputRadius, sourceRadius);
                waveShader.SetFloat(PropInputStrength, sourceAmplitude);
                waveShader.SetFloat(PropOscillatorPhase, oscillatorPhase);
                waveShader.SetFloat(PropDeltaTime, subDt);
                waveShader.SetTexture(kernelOscillator, PropNextState, target);
                DispatchCompute(kernelOscillator);
            }
        }

        private void ComputeNormalAndPackOutput() {
            RenderTexture current = CurrentStateTex;
            float dx = areaSize.x / resolution.x;

            waveShader.SetInt(PropWidth, resolution.x);
            waveShader.SetInt(PropHeight, resolution.y);
            waveShader.SetFloat(PropGridSpacing, dx);
            waveShader.SetTexture(kernelNormal, PropNextState, current);
            waveShader.SetTexture(kernelNormal, PropNormalTex, normalTex);
            waveShader.SetTexture(kernelNormal, PropWaterDataTex, waterDataTex);
            DispatchCompute(kernelNormal);
        }

        private void DispatchCompute(int kernel) {
            int gx = math.max(1, resolution.x / 8);
            int gy = math.max(1, resolution.y / 8);
            waveShader.Dispatch(kernel, gx, gy, 1);
        }

        private void UpdateObstacleTexture() {
            if (obstacleTex == null) return;

            int w = resolution.x;
            int h = resolution.y;
            var pixels = new Color[w * h];

            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.black;

            if (obstaclePattern == ObstaclePattern.SingleSlit || obstaclePattern == ObstaclePattern.DoubleSlit) {
                // Wall across middle Z (y in texture)
                int wallY = (int)(h * 0.5f);
                int wallThick = math.max(2, h / 64);
                int slitHalfWidth = math.max(2, w / 40);

                int slit1 = w / 2;
                int slit2 = w / 2;
                if (obstaclePattern == ObstaclePattern.DoubleSlit) {
                    slit1 = w / 3;
                    slit2 = (2 * w) / 3;
                }

                for (int y = wallY - wallThick; y <= wallY + wallThick; y++) {
                    if (y < 0 || y >= h) continue;
                    for (int x = 0; x < w; x++) {
                        bool isSlit = false;
                        if (math.abs(x - slit1) <= slitHalfWidth) isSlit = true;
                        if (obstaclePattern == ObstaclePattern.DoubleSlit && math.abs(x - slit2) <= slitHalfWidth) isSlit = true;

                        if (!isSlit) {
                            pixels[y * w + x] = Color.white; // 1 = obstacle
                        }
                    }
                }
            } else if (obstaclePattern == ObstaclePattern.ObstaclePillars) {
                // Pillars / breakwater blocks
                float2 c1 = new float2(w * 0.4f, h * 0.5f);
                float2 c2 = new float2(w * 0.6f, h * 0.5f);
                float r = w * 0.06f;

                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) {
                        float2 p = new float2(x, y);
                        if (math.distance(p, c1) <= r || math.distance(p, c2) <= r) {
                            pixels[y * w + x] = Color.white;
                        }
                    }
                }
            }

            var tempTex = new Texture2D(w, h, TextureFormat.R8, false);
            tempTex.SetPixels(pixels);
            tempTex.Apply(false);
            Graphics.Blit(tempTex, obstacleTex);
            DestroyImmediate(tempTex);
        }

        private void HandleMouseInput() {
            if (sourceMode != InputMode.InteractiveMouse) return;

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
                    TriggerImpulse(new Vector2(u, v), sourceAmplitude * 0.5f, sourceRadius);
                }
            }
        }
        #endregion

        #region constants / static
        private static readonly int PropWidth = Shader.PropertyToID("_Width");
        private static readonly int PropHeight = Shader.PropertyToID("_Height");
        private static readonly int PropDeltaTime = Shader.PropertyToID("_DeltaTime");
        private static readonly int PropGridSpacing = Shader.PropertyToID("_GridSpacing");
        private static readonly int PropWaveSpeed = Shader.PropertyToID("_WaveSpeed");
        private static readonly int PropDamping = Shader.PropertyToID("_Damping");
        private static readonly int PropSpongeWidth = Shader.PropertyToID("_SpongeWidth");
        private static readonly int PropSpongeStrength = Shader.PropertyToID("_SpongeStrength");
        private static readonly int PropIsSpongeEnabled = Shader.PropertyToID("_IsSpongeEnabled");
        private static readonly int PropInputCenter = Shader.PropertyToID("_InputCenter");
        private static readonly int PropInputRadius = Shader.PropertyToID("_InputRadius");
        private static readonly int PropInputStrength = Shader.PropertyToID("_InputStrength");
        private static readonly int PropOscillatorPhase = Shader.PropertyToID("_OscillatorPhase");
        private static readonly int PropCurrentState = Shader.PropertyToID("_CurrentState");
        private static readonly int PropNextState = Shader.PropertyToID("_NextState");
        private static readonly int PropObstacleTex = Shader.PropertyToID("_ObstacleTex");
        private static readonly int PropNormalTex = Shader.PropertyToID("_NormalTex");
        private static readonly int PropWaterDataTex = Shader.PropertyToID("_WaterDataTex");
        #endregion
    }
}
