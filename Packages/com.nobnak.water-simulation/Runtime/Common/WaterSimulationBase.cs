using Unity.Mathematics;
using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Abstract base class for water simulation components.
    /// Manages lifecycle, timing, output textures, and execution state.
    /// </summary>
    [ExecuteAlways]
    public abstract class WaterSimulationBase : MonoBehaviour, IWaterSimulation {
        #region serialized fields
        [Header("Domain Settings")]
        [Tooltip("Simulation domain size in meters (X, Z).")]
        [SerializeField] protected Vector2 areaSize = new Vector2(50f, 50f);

        [Tooltip("Simulation grid resolution.")]
        [SerializeField] protected Vector2Int resolution = new Vector2Int(512, 512);

        [Header("Execution")]
        [Tooltip("Run simulation automatically on start/enable.")]
        [SerializeField] protected bool autoPlay = true;

        [Tooltip("Target fixed simulation time step [s]. 0 uses deltaTime.")]
        [SerializeField] protected float fixedTimeStep = 0.016f;

        [Tooltip("Simulation playback time scale.")]
        [Range(0f, 3f)]
        [SerializeField] protected float timeScale = 1.0f;
        #endregion

        #region private fields
        protected bool isRunning;
        protected float simulationTime;
        protected RenderTexture waterDataTex;
        protected RenderTexture normalTex;
        protected RenderTexture bathymetryTex;
        protected int bathymetryVersion;
        protected int stepCount;
        protected int lastSubStepCount = 1;
        protected int lastDispatchCount;
        protected double lastStepCpuTimeMs;
        protected float accumulator;

        protected Vector2 appliedAreaSize;
        protected Vector2Int appliedResolution;
        protected bool isApplyPending;
        #endregion

        #region properties
        public Vector2 AreaSize => areaSize;
        public Vector2Int Resolution => resolution;
        public Vector2 AppliedAreaSize => appliedAreaSize;
        public Vector2Int AppliedResolution => appliedResolution;
        public bool IsConfigurationPending => resolution != appliedResolution || areaSize != appliedAreaSize || !AreTexturesValidAndMatchingResolution();
        public float SimulationTime => simulationTime;
        public float ReferenceWaterLevel => transform.position.y;
        public RenderTexture WaterDataTexture => waterDataTex;
        public RenderTexture NormalTexture => normalTex;
        public virtual RenderTexture BathymetryTexture => bathymetryTex;
        public int BathymetryVersion => bathymetryVersion;
        public int StepCount => stepCount;
        public int LastSubStepCount => lastSubStepCount;
        public int LastDispatchCount => lastDispatchCount;
        public double LastStepCpuTimeMs => lastStepCpuTimeMs;
        public bool IsRunning => isRunning;
        public float FixedTimeStep => fixedTimeStep;
        public float TimeScale {
            get => timeScale;
            set => timeScale = math.max(0f, value);
        }
        #endregion

        #region Unity lifecycle
        protected virtual void OnEnable() {
            if (!ValidateConfiguration(out string validationError)) {
                Debug.LogError($"[{GetType().Name}] Invalid configuration: {validationError}. Simulation will not run.", this);
                isRunning = false;
                return;
            }
            InitializeTextures();
            ResetSimulation();
            appliedResolution = resolution;
            appliedAreaSize = areaSize;
            isApplyPending = false;
            isRunning = autoPlay;
        }

        protected virtual void OnDisable() {
            ReleaseTextures();
            appliedResolution = Vector2Int.zero;
            appliedAreaSize = Vector2.zero;
            isApplyPending = false;
        }

        protected virtual void OnValidate() {
            if (!ValidateConfiguration(out _)) {
                // Keep existing applied configuration; do not schedule dirty reconstruction with invalid values
                return;
            }
            if (IsConfigurationPending) {
                isApplyPending = true;
#if UNITY_EDITOR
                if (!Application.isPlaying) {
                    UnityEditor.EditorApplication.delayCall -= ProcessPendingConfigurationChange;
                    UnityEditor.EditorApplication.delayCall += ProcessPendingConfigurationChange;
                }
#endif
            }
        }

        private void ProcessPendingConfigurationChange() {
            if (this == null) return;
            if (isApplyPending || IsConfigurationPending) {
                ApplyConfiguration();
            }
        }

        protected virtual void Update() {
            if (isApplyPending || IsConfigurationPending) ApplyConfiguration();
            if (!isRunning || timeScale <= 0f) return;

            float dt = Time.deltaTime * timeScale;
            if (fixedTimeStep > 0f) {
                accumulator += dt;
                // Clamp accumulator to at most 2 steps to avoid spiral of death when frame time spikes
                if (accumulator > fixedTimeStep * 2f) {
                    accumulator = fixedTimeStep * 2f;
                }
                float step = fixedTimeStep;
                int maxStepsPerFrame = 2;
                int count = 0;
                while (accumulator >= step && count < maxStepsPerFrame) {
                    Step(step);
                    accumulator -= step;
                    count++;
                }
            } else {
                Step(dt);
            }
        }
        #endregion

        #region public interface
        public void Play() {
            isRunning = true;
        }

        public void Pause() {
            isRunning = false;
        }

        public virtual bool AreTexturesValidAndMatchingResolution() {
            if (waterDataTex == null || !waterDataTex.IsCreated()) return false;
            return waterDataTex.width == resolution.x && waterDataTex.height == resolution.y;
        }

        /// <summary>
        /// Unified configuration application pipeline.
        /// Applies requested resolution and areaSize to allocated textures and simulation state.
        /// Applied values (appliedResolution, appliedAreaSize) are only updated after successful reconstruction.
        /// </summary>
        public virtual bool ApplyConfiguration() {
            if (!ValidateConfiguration(out string err)) {
                Debug.LogError($"[{GetType().Name}] Cannot apply invalid configuration: {err}", this);
                return false;
            }

            bool resChanged = resolution != appliedResolution || !AreTexturesValidAndMatchingResolution();
            bool areaChanged = areaSize != appliedAreaSize;

            if (!resChanged && !areaChanged) {
                isApplyPending = false;
                return true;
            }

            Vector2 prevArea = appliedAreaSize;
            Vector2Int prevRes = appliedResolution;

            if (resChanged) {
                InitializeTextures();
                ResetSimulation();
                appliedResolution = resolution;
                appliedAreaSize = areaSize;
                isApplyPending = false;
                OnAreaSizeChanged(prevArea, areaSize);
            } else if (areaChanged) {
                appliedAreaSize = areaSize;
                isApplyPending = false;
                OnAreaSizeChanged(prevArea, areaSize);
            }

            return true;
        }

        public void Step(float dt) {
            if (dt <= 0f) return;
            if (isApplyPending || IsConfigurationPending) {
                if (!ApplyConfiguration()) {
                    Debug.LogError($"[{GetType().Name}] Simulation Step blocked: Pending configuration is invalid.", this);
                    return;
                }
            }
            if (!ValidateConfiguration(out string validationError)) {
                Debug.LogError($"[{GetType().Name}] Simulation Step blocked by invalid configuration: {validationError}", this);
                return;
            }
            if (!AreTexturesValidAndMatchingResolution()) {
                Debug.LogError($"[{GetType().Name}] Simulation Step blocked: Textures are not initialized or do not match resolution {resolution}.", this);
                return;
            }
            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            lastDispatchCount = 0;
            OnStepSimulation(dt);
            simulationTime += dt;
            stepCount++;
            long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
            lastStepCpuTimeMs = (double)elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        public void ResetSimulation() {
            simulationTime = 0f;
            stepCount = 0;
            accumulator = 0f;
            OnResetSimulation();
        }

        /// <summary>Sample water surface elevation at local coordinates (x, z).</summary>
        public virtual float SampleElevation(float localX, float localZ) {
            return 0f;
        }

        public virtual bool ValidateConfiguration(out string errorMessage) {
            if (areaSize.x <= 0f || areaSize.y <= 0f ||
                float.IsNaN(areaSize.x) || float.IsNaN(areaSize.y) ||
                float.IsInfinity(areaSize.x) || float.IsInfinity(areaSize.y)) {
                errorMessage = $"AreaSize must have positive finite components, got ({areaSize.x}, {areaSize.y}).";
                return false;
            }
            if (resolution.x <= 0 || resolution.y <= 0) {
                errorMessage = $"Resolution must have positive non-zero components, got ({resolution.x}, {resolution.y}).";
                return false;
            }
            errorMessage = string.Empty;
            return true;
        }

        public virtual bool SetAreaSize(Vector2 size) {
            if (areaSize == size && appliedAreaSize == size && !IsConfigurationPending) return true;
            Vector2 prev = areaSize;
            areaSize = size;
            if (!ValidateConfiguration(out string err)) {
                Debug.LogError($"[{GetType().Name}] Invalid AreaSize ({size.x}, {size.y}): {err}", this);
                areaSize = prev;
                return false;
            }
            return ApplyConfiguration();
        }

        public virtual bool SetResolution(Vector2Int res) {
            if (resolution == res && appliedResolution == res && AreTexturesValidAndMatchingResolution() && !isApplyPending) return true;
            Vector2Int prev = resolution;
            resolution = res;
            if (!ValidateConfiguration(out string err)) {
                Debug.LogError($"[{GetType().Name}] Invalid resolution ({res.x}, {res.y}): {err}", this);
                resolution = prev;
                return false;
            }
            return ApplyConfiguration();
        }

        public void SimulateInspectorSettings(Vector2Int newRes, Vector2 newArea) {
            resolution = newRes;
            areaSize = newArea;
            OnValidate();
        }

        public void SimulateInspectorResolutionChangeOnly(Vector2Int newRes) {
            resolution = newRes;
            OnValidate();
        }

        public void SimulateInspectorAreaSizeChangeOnly(Vector2 newArea) {
            areaSize = newArea;
            OnValidate();
        }

        public void ReleaseTexturesForTest() {
            ReleaseTextures();
            appliedResolution = Vector2Int.zero;
            appliedAreaSize = Vector2.zero;
            isApplyPending = false;
        }

        protected virtual void OnAreaSizeChanged(Vector2 oldSize, Vector2 newSize) {
        }
        #endregion

        #region private methods
        protected abstract void InitializeTextures();
        protected abstract void ReleaseTextures();
        protected abstract void OnStepSimulation(float dt);
        protected abstract void OnResetSimulation();

        protected RenderTexture CreateSimulationTexture(int width, int height, RenderTextureFormat format, FilterMode filter = FilterMode.Bilinear) {
            var rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear) {
                enableRandomWrite = true,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
            return rt;
        }

        protected void ReleaseTexture(ref RenderTexture rt) {
            if (rt != null) {
                if (RenderTexture.active == rt) RenderTexture.active = null;
                rt.Release();
                DestroyImmediate(rt);
                rt = null;
            }
        }
        #endregion

        #region constants / static
        public const float Gravity = 9.81f;
        #endregion
    }
}
