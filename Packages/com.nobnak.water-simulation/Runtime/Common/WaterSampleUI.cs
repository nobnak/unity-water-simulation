using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// In-game UI overlay providing interactive controls, performance metrics,
    /// and pedagogical information ("What to observe", "Controls", "Scope & Limitations").
    /// </summary>
    [ExecuteAlways]
    public class WaterSampleUI : MonoBehaviour {
        #region serialized fields
        [Header("Scene Information")]
        [SerializeField] private string sampleTitle = "Water Simulation";
        [TextArea(2, 4)]
        [SerializeField] private string whatToObserve = "";
        [TextArea(2, 4)]
        [SerializeField] private string whatCanBeControlled = "";
        [TextArea(2, 4)]
        [SerializeField] private string scopeAndLimitations = "";

        [Header("Target Simulation")]
        [SerializeField] private WaterSimulationBase simulation;
        [SerializeField] private WaterRenderer waterRenderer;
        [SerializeField] private SimpleCameraController cameraController;

        [Header("UI Display")]
        [SerializeField] private bool showInfoPanel = true;
        [SerializeField] private bool showControls = true;
        #endregion

        #region private fields
        private float frameCount;
        private float fpsAccumulator;
        private float currentFps;
        private bool isFolded;
        private Vector2 scrollPos;
        #endregion

        #region Unity lifecycle
        private void Update() {
            frameCount++;
            fpsAccumulator += Time.unscaledDeltaTime;
            if (fpsAccumulator >= 0.5f) {
                currentFps = frameCount / fpsAccumulator;
                frameCount = 0f;
                fpsAccumulator = 0f;
            }
        }

        private void OnGUI() {
            DrawHUD();
        }
        #endregion

        #region private methods
        private void DrawHUD() {
            float margin = 10f;
            float panelWidth = 340f;
            float panelHeight = isFolded ? 40f : math.min(Screen.height - 20f, 560f);

            Rect panelRect = new Rect(Screen.width - panelWidth - margin, margin, panelWidth, panelHeight);
            GUI.Box(panelRect, "");

            GUILayout.BeginArea(new Rect(panelRect.x + 8, panelRect.y + 6, panelRect.width - 16, panelRect.height - 12));

            // Header & FPS
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{sampleTitle}</b>", GUILayout.ExpandWidth(true));
            if (GUILayout.Button(isFolded ? "Open" : "Close", GUILayout.Width(50))) {
                isFolded = !isFolded;
            }
            GUILayout.EndHorizontal();

            if (isFolded) {
                GUILayout.EndArea();
                return;
            }

            scrollPos = GUILayout.BeginScrollView(scrollPos);

            // Performance metrics
            float simTime = simulation != null ? simulation.SimulationTime : 0f;
            int resX = simulation != null ? simulation.Resolution.x : 0;
            int resY = simulation != null ? simulation.Resolution.y : 0;
            GUILayout.Label($"Render FPS: <b>{currentFps:F1}</b> | Sim Time: <b>{simTime:F2}s</b> | Grid: {resX}x{resY}");

            GUILayout.Space(6);

            // Pedagogical notes
            if (showInfoPanel && !string.IsNullOrEmpty(whatToObserve)) {
                GUILayout.Label("<color=#70d0ff><b>[何を見るか / What to observe]</b></color>");
                GUILayout.Label(whatToObserve);
                GUILayout.Space(4);
            }

            if (showInfoPanel && !string.IsNullOrEmpty(whatCanBeControlled)) {
                GUILayout.Label("<color=#a0f080><b>[何を操作すると何が変わるか / Controls]</b></color>");
                GUILayout.Label(whatCanBeControlled);
                GUILayout.Space(4);
            }

            if (showInfoPanel && !string.IsNullOrEmpty(scopeAndLimitations)) {
                GUILayout.Label("<color=#ffaa70><b>[この方式で扱わないこと / Limitations]</b></color>");
                GUILayout.Label(scopeAndLimitations);
                GUILayout.Space(4);
            }

            // Common Simulation Controls
            if (showControls && simulation != null) {
                GUILayout.Label("<b>[シミュレーション操作 / Controls]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(simulation.IsRunning ? "Pause" : "Play")) {
                    if (simulation.IsRunning) simulation.Pause();
                    else simulation.Play();
                }
                if (GUILayout.Button("Step (0.016s)")) {
                    simulation.Pause();
                    simulation.Step(0.016f);
                }
                if (GUILayout.Button("Reset")) {
                    simulation.ResetSimulation();
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"TimeScale: {simulation.TimeScale:F1}", GUILayout.Width(90));
                simulation.TimeScale = GUILayout.HorizontalSlider(simulation.TimeScale, 0f, 2.5f);
                GUILayout.EndHorizontal();
            }

            // Visual mode
            if (waterRenderer != null) {
                GUILayout.Space(4);
                GUILayout.Label("<b>[描画モード / Visual Mode]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Realistic")) waterRenderer.SetVisualMode(WaterRenderer.VisualMode.Realistic);
                if (GUILayout.Button("HeightMap")) waterRenderer.SetVisualMode(WaterRenderer.VisualMode.HeightMap);
                if (GUILayout.Button("Normal")) waterRenderer.SetVisualMode(WaterRenderer.VisualMode.NormalVisual);
                if (GUILayout.Button("Wireframe")) waterRenderer.SetVisualMode(WaterRenderer.VisualMode.Wireframe);
                GUILayout.EndHorizontal();
            }

            // Camera Viewpoints
            if (cameraController != null) {
                GUILayout.Space(4);
                GUILayout.Label("<b>[視点 / Camera View]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Default View")) cameraController.ResetToDefaultView();
                if (GUILayout.Button("Low Angle View")) cameraController.SetLowAngleView();
                GUILayout.EndHorizontal();
                GUILayout.Label("<size=10>右ドラッグ: 回転 | WASD: 移動 | QE: 昇降</size>");
            }

            // Quick Scene Navigator
            GUILayout.Space(8);
            GUILayout.Label("<b>[サンプル切り替え / Sample Scenes]</b>");
            int sceneCount = SceneManager.sceneCountInBuildSettings;
            if (sceneCount > 0) {
                for (int i = 0; i < sceneCount; i++) {
                    string path = SceneUtility.GetScenePathByBuildIndex(i);
                    string sceneName = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (GUILayout.Button(sceneName)) {
                        SceneManager.LoadScene(i);
                    }
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
        #endregion
    }
}
