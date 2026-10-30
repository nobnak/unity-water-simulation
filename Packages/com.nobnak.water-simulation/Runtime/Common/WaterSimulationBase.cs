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
        #endregion

        #region properties
        public Vector2 AreaSize => areaSize;
        public Vector2Int Resolution => resolution;
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
        public float TimeScale {
            get => timeScale;
            set => timeScale = math.max(0f, value);
        }
        #endregion

        #region Unity lifecycle
        protected virtual void OnEnable() {
            InitializeTextures();
            ResetSimulation();
            isRunning = autoPlay;
        }

        protected virtual void OnDisable() {
            ReleaseTextures();
        }

        protected virtual void Update() {
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

        public void Step(float dt) {
            if (dt <= 0f) return;
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
