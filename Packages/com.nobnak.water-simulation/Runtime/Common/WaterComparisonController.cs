using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Master controller for side-by-side or overview comparison between water simulation methods.
    /// Synchronizes playback, reset, cameras, lighting, and visualization modes.
    /// </summary>
    [ExecuteAlways]
    public class WaterComparisonController : MonoBehaviour {
        #region serialized fields
        [Header("Managed Simulations")]
        [SerializeField] private WaterSimulationBase[] simulations;

        [Header("Managed Renderers")]
        [SerializeField] private WaterRenderer[] waterRenderers;

        [Header("Common Controls")]
        [SerializeField] private bool autoPlay = true;
        [Range(0f, 3f)]
        [SerializeField] private float synchronizedTimeScale = 1.0f;
        [SerializeField] private WaterRenderer.VisualMode synchronizedVisualMode = WaterRenderer.VisualMode.Realistic;

        [Header("UI Overlay")]
        [SerializeField] private bool showComparisonUI = true;
        #endregion

        #region private fields
        private bool isPaused;
        #endregion

        #region properties
        public WaterSimulationBase[] Simulations => simulations;
        public bool IsPaused => isPaused;
        public float TimeScale {
            get => synchronizedTimeScale;
            set {
                synchronizedTimeScale = value;
                ApplyTimeScale();
            }
        }
        #endregion

        #region Unity lifecycle
        private void OnEnable() {
            if (simulations != null) {
                foreach (var sim in simulations) {
                    if (sim != null && autoPlay) sim.Play();
                }
            }
            ApplyTimeScale();
            ApplyVisualMode();
        }

        private void Update() {
            ApplyTimeScale();
        }

        private void OnGUI() {
            if (!showComparisonUI) return;
            DrawGUI();
        }
        #endregion

        #region public interface
        public void PlayAll() {
            isPaused = false;
            if (simulations == null) return;
            foreach (var sim in simulations) {
                if (sim != null) sim.Play();
            }
        }

        public void PauseAll() {
            isPaused = true;
            if (simulations == null) return;
            foreach (var sim in simulations) {
                if (sim != null) sim.Pause();
            }
        }

        public void StepAll(float dt) {
            if (simulations == null) return;
            foreach (var sim in simulations) {
                if (sim != null) sim.Step(dt);
            }
        }

        public void ResetAll() {
            if (simulations == null) return;
            foreach (var sim in simulations) {
                if (sim != null) sim.ResetSimulation();
            }
        }

        public void SetVisualModeAll(WaterRenderer.VisualMode mode) {
            synchronizedVisualMode = mode;
            ApplyVisualMode();
        }
        #endregion

        #region private methods
        private void ApplyTimeScale() {
            if (simulations == null) return;
            foreach (var sim in simulations) {
                if (sim != null) sim.TimeScale = isPaused ? 0f : synchronizedTimeScale;
            }
        }

        private void ApplyVisualMode() {
            if (waterRenderers == null) return;
            foreach (var wr in waterRenderers) {
                if (wr != null) wr.SetVisualMode(synchronizedVisualMode);
            }
        }

        private void DrawGUI() {
            const float padding = 12f;
            const float width = 280f;
            const float height = 210f;
            Rect rect = new Rect(padding, padding, width, height);

            GUI.Box(rect, "Water Comparison Control");
            GUILayout.BeginArea(new Rect(rect.x + 8, rect.y + 24, rect.width - 16, rect.height - 30));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(isPaused ? "Play" : "Pause")) {
                if (isPaused) PlayAll();
                else PauseAll();
            }
            if (GUILayout.Button("Step (0.016s)")) {
                PauseAll();
                StepAll(0.016f);
            }
            if (GUILayout.Button("Reset")) {
                ResetAll();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label($"Time Scale: {synchronizedTimeScale:F2}");
            synchronizedTimeScale = GUILayout.HorizontalSlider(synchronizedTimeScale, 0f, 2.5f);

            GUILayout.Space(6);
            GUILayout.Label("Visualization Mode:");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Realistic")) SetVisualModeAll(WaterRenderer.VisualMode.Realistic);
            if (GUILayout.Button("Height")) SetVisualModeAll(WaterRenderer.VisualMode.HeightMap);
            if (GUILayout.Button("Normal")) SetVisualModeAll(WaterRenderer.VisualMode.NormalVisual);
            if (GUILayout.Button("Wireframe")) SetVisualModeAll(WaterRenderer.VisualMode.Wireframe);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            if (simulations != null && simulations.Length > 0 && simulations[0] != null) {
                GUILayout.Label($"Sim Time: {simulations[0].SimulationTime:F2}s");
            }

            GUILayout.EndArea();
        }
        #endregion
    }
}
