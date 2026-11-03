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
        [SerializeField] private SeabedRenderer seabedRenderer;
        [SerializeField] private SimpleCameraController cameraController;
        [SerializeField] private WaterProfileViewer profileViewer;

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
        private WaterComparisonController comparisonController;
        private string cachedTitleText;
        private string cachedMetricsText = "";
        private float nextMetricsUpdate;
        #endregion

        #region public interface
        public void RenderGUI() {
            DrawHUD();
        }
        #endregion

        #region Unity lifecycle
        private void OnEnable() {
            comparisonController = FindFirstObjectByType<WaterComparisonController>();
            if (seabedRenderer == null) seabedRenderer = FindFirstObjectByType<SeabedRenderer>();
            if (profileViewer == null) profileViewer = FindFirstObjectByType<WaterProfileViewer>();
            cachedTitleText = $"<b>{sampleTitle}</b>";
        }

        private void Update() {
            frameCount++;
            fpsAccumulator += Time.unscaledDeltaTime;
            if (fpsAccumulator >= 0.5f) {
                currentFps = frameCount / fpsAccumulator;
                frameCount = 0f;
                fpsAccumulator = 0f;
            }

            if (Time.unscaledTime >= nextMetricsUpdate) {
                nextMetricsUpdate = Time.unscaledTime + 0.1f;
                float simTime = simulation != null ? simulation.SimulationTime : 0f;
                int resX = simulation != null ? simulation.Resolution.x : 0;
                int resY = simulation != null ? simulation.Resolution.y : 0;
                cachedMetricsText = $"Render FPS: <b>{currentFps:F1}</b> | Sim Time: <b>{simTime:F2}s</b> | Grid: {resX}x{resY}";
            }
        }

        private void OnGUI() {
            DrawHUD();
        }
        #endregion

        #region private methods
        private void DrawHUD() {
            float margin = 10f;
            float panelWidth = 360f;
            float panelHeight = isFolded ? 40f : math.min(Screen.height - 20f, 680f);

            Rect panelRect = new Rect(Screen.width - panelWidth - margin, margin, panelWidth, panelHeight);
            GUI.Box(panelRect, "");

            GUILayout.BeginArea(new Rect(panelRect.x + 8, panelRect.y + 6, panelRect.width - 16, panelRect.height - 12));

            // Header & FPS
            GUILayout.BeginHorizontal();
            GUILayout.Label(cachedTitleText ?? sampleTitle, OptExpandWidthTrue);
            if (GUILayout.Button(isFolded ? "Open" : "Close", OptWidth50)) {
                isFolded = !isFolded;
            }
            GUILayout.EndHorizontal();

            if (isFolded) {
                GUILayout.EndArea();
                return;
            }

            scrollPos = GUILayout.BeginScrollView(scrollPos);

            // Performance metrics
            GUILayout.Label(string.IsNullOrEmpty(cachedMetricsText) ? sampleTitle : cachedMetricsText);

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
                if (comparisonController != null) {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(comparisonController.IsPaused ? "Play All" : "Pause All")) {
                        if (comparisonController.IsPaused) comparisonController.PlayAll();
                        else comparisonController.PauseAll();
                    }
                    if (GUILayout.Button("Step (0.016s)")) {
                        comparisonController.PauseAll();
                        comparisonController.StepAll(0.016f);
                    }
                    if (GUILayout.Button("Reset All")) {
                        comparisonController.ResetAll();
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"TimeScale: {comparisonController.TimeScale:F1}", GUILayout.Width(90));
                    comparisonController.TimeScale = GUILayout.HorizontalSlider(comparisonController.TimeScale, 0f, 2.5f);
                    GUILayout.EndHorizontal();

                    if (comparisonController.HasProfileViewers) {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"ProfileH: {comparisonController.ProfileVerticalScale:F1}x", GUILayout.Width(90));
                        comparisonController.ProfileVerticalScale = GUILayout.HorizontalSlider(comparisonController.ProfileVerticalScale, 0.2f, 12.0f);
                        GUILayout.EndHorizontal();
                    }
                } else {
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

                DrawSpecializedControls();
            }

            // Visual mode
            if (waterRenderer != null || comparisonController != null) {
                GUILayout.Space(4);
                GUILayout.Label("<b>[表示モード / Display Mode]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Realistic (リアル水面)")) {
                    SetVisualMode(WaterRenderer.VisualMode.Realistic);
                    if (profileViewer != null) profileViewer.gameObject.SetActive(false);
                }
                if ((simulation != null && simulation.BathymetryTexture != null) || comparisonController != null) {
                    if (GUILayout.Button("Bathymetry (水深確認)")) {
                        SetVisualMode(WaterRenderer.VisualMode.Bathymetry);
                        if (profileViewer != null) profileViewer.gameObject.SetActive(false);
                    }
                }
                if (profileViewer != null || comparisonController != null) {
                    if (GUILayout.Button("Profile (断面表示)")) {
                        SetVisualMode(WaterRenderer.VisualMode.Realistic);
                        if (profileViewer != null) profileViewer.gameObject.SetActive(true);
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("HeightMap")) SetVisualMode(WaterRenderer.VisualMode.HeightMap);
                if (GUILayout.Button("Normal")) SetVisualMode(WaterRenderer.VisualMode.NormalVisual);
                if (GUILayout.Button("Wireframe")) SetVisualMode(WaterRenderer.VisualMode.Wireframe);
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

        private void DrawSpecializedControls() {
            if (simulation is WaveEquationSimulation waveSim) {
                GUILayout.Space(4);
                GUILayout.Label("<b>[スリット / 障害物 / Obstacles]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("None")) waveSim.SetObstaclePattern(WaveEquationSimulation.ObstaclePattern.None);
                if (GUILayout.Button("Single")) waveSim.SetObstaclePattern(WaveEquationSimulation.ObstaclePattern.SingleSlit);
                if (GUILayout.Button("Double")) waveSim.SetObstaclePattern(WaveEquationSimulation.ObstaclePattern.DoubleSlit);
                if (GUILayout.Button("Pillars")) waveSim.SetObstaclePattern(WaveEquationSimulation.ObstaclePattern.ObstaclePillars);
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                GUILayout.Label("<b>[波源モード / Source Mode]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Impulse")) waveSim.SourceMode = WaveEquationSimulation.InputMode.SingleImpulse;
                if (GUILayout.Button("Oscillator")) waveSim.SourceMode = WaveEquationSimulation.InputMode.ContinuousOscillator;
                if (GUILayout.Button("Moving")) waveSim.SourceMode = WaveEquationSimulation.InputMode.MovingSource;
                if (GUILayout.Button("Mouse")) waveSim.SourceMode = WaveEquationSimulation.InputMode.InteractiveMouse;
                GUILayout.EndHorizontal();

                if (waveSim.SourceMode == WaveEquationSimulation.InputMode.SingleImpulse) {
                    if (GUILayout.Button("★ Trigger Impulse (波を発生)")) {
                        waveSim.TriggerImpulse();
                    }
                } else if (waveSim.SourceMode == WaveEquationSimulation.InputMode.ContinuousOscillator ||
                           waveSim.SourceMode == WaveEquationSimulation.InputMode.MovingSource) {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Interval: {waveSim.PulseInterval:F2}s ({1f / waveSim.PulseInterval:F1} Hz)", GUILayout.Width(170));
                    waveSim.PulseInterval = GUILayout.HorizontalSlider(waveSim.PulseInterval, 0.2f, 3.0f);
                    GUILayout.EndHorizontal();
                } else if (waveSim.SourceMode == WaveEquationSimulation.InputMode.InteractiveMouse) {
                    GUILayout.Label("<color=#a0f080><size=10>水面を左クリック / ドラッグして波を発生</size></color>");
                }

                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"WaveSpeed: {waveSim.WaveSpeed:F1}", GUILayout.Width(90));
                waveSim.WaveSpeed = GUILayout.HorizontalSlider(waveSim.WaveSpeed, 2.0f, 20.0f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Damping: {waveSim.Damping:F3}", GUILayout.Width(90));
                waveSim.Damping = GUILayout.HorizontalSlider(waveSim.Damping, 0.0f, 0.1f);
                GUILayout.EndHorizontal();
            } else if (simulation is BoussinesqSimulation boussSim) {
                GUILayout.Space(4);
                GUILayout.Label("<b>[対流計算方式 / Advection Scheme]</b>");
                GUILayout.BeginHorizontal();
                var currentAdv = boussSim.Advection;
                bool isBaseline = (currentAdv == BoussinesqSimulation.AdvectionType.FirstOrderUpwind);
                bool isMinmod = (currentAdv == BoussinesqSimulation.AdvectionType.MinmodTVD);

                GUI.color = isMinmod ? new Color(0.7f, 1.0f, 0.7f) : Color.white;
                if (GUILayout.Button(isMinmod ? "● Minmod (2次TVD・標準)" : "Minmod (2次TVD・標準)")) {
                    if (!isMinmod) SwitchBoussinesqAdvection(boussSim, BoussinesqSimulation.AdvectionType.MinmodTVD);
                }

                GUI.color = isBaseline ? new Color(1.0f, 0.85f, 0.6f) : Color.white;
                if (GUILayout.Button(isBaseline ? "● Baseline (1次風上・比較用)" : "Baseline (1次風上・比較用)")) {
                    if (!isBaseline) SwitchBoussinesqAdvection(boussSim, BoussinesqSimulation.AdvectionType.FirstOrderUpwind);
                }
                GUI.color = Color.white;
                GUILayout.EndHorizontal();

                string advStatus = isMinmod
                    ? "<color=#80d0ff>選択中: <b>Minmod (2次TVD)</b> [通常設定・波高保持優先]</color>"
                    : "<color=#ffd080>選択中: <b>Baseline (1次風上)</b> [比較用・数値拡散強/平滑性優先]</color>";
                GUILayout.Label($"<size=10>{advStatus}</size>");
                GUILayout.Label("<size=9><color=#aaaaaa>※ 方式切り替え時に同一初期条件(t=0)からリセット再生されます</color></size>");

                GUILayout.Space(4);
                GUILayout.Label("<b>[比較用 地形プリセット / Bathymetry Preset]</b>");
                GUILayout.BeginHorizontal();
                var curBathy = boussSim.Bathymetry;
                bool isFlat = (curBathy == BoussinesqSimulation.BathymetryType.Flat);
                bool isMound = (curBathy == BoussinesqSimulation.BathymetryType.SubmergedMound);
                bool isShelf = (curBathy == BoussinesqSimulation.BathymetryType.ShallowShelf);

                GUI.color = isFlat ? new Color(0.7f, 1.0f, 0.7f) : Color.white;
                if (GUILayout.Button(isFlat ? "● A.平坦 (Flat)" : "A.平坦 (Flat)")) {
                    SwitchBathymetryPreset(boussSim, BoussinesqSimulation.BathymetryType.Flat);
                }
                GUI.color = isMound ? new Color(1.0f, 0.85f, 0.6f) : Color.white;
                if (GUILayout.Button(isMound ? "● B.水中の丘 (Mound)" : "B.水中の丘 (Mound)")) {
                    SwitchBathymetryPreset(boussSim, BoussinesqSimulation.BathymetryType.SubmergedMound);
                }
                GUI.color = isShelf ? new Color(0.8f, 0.8f, 1.0f) : Color.white;
                if (GUILayout.Button(isShelf ? "● C.浅い棚 (Shelf)" : "C.浅い棚 (Shelf)")) {
                    SwitchBathymetryPreset(boussSim, BoussinesqSimulation.BathymetryType.ShallowShelf);
                }
                GUI.color = Color.white;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Shoal (浅瀬・岬)")) SwitchBathymetryPreset(boussSim, BoussinesqSimulation.BathymetryType.ShoalAndCape);
                if (GUILayout.Button("Slope (直線斜面)")) SwitchBathymetryPreset(boussSim, BoussinesqSimulation.BathymetryType.LinearSlope);
                GUILayout.EndHorizontal();

                if (seabedRenderer != null) {
                    GUILayout.BeginHorizontal();
                    bool showSeabed = seabedRenderer.gameObject.activeSelf;
                    if (GUILayout.Button(showSeabed ? "Seabed: ON" : "Seabed: OFF", GUILayout.Width(100))) {
                        seabedRenderer.gameObject.SetActive(!showSeabed);
                    }
                    if (waterRenderer != null) {
                        var waterMesh = waterRenderer.GetComponent<MeshRenderer>();
                        bool showWater = waterMesh != null && waterMesh.enabled;
                        if (GUILayout.Button(showWater ? "Water: ON" : "Water: OFF", GUILayout.Width(95))) {
                            if (waterMesh != null) waterMesh.enabled = !showWater;
                        }
                    }
                    GUILayout.EndHorizontal();

                    if (showSeabed) {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("海底描画:", GUILayout.Width(65));
                        if (GUILayout.Button("Sand")) seabedRenderer.SetVisualMode(SeabedRenderer.VisualMode.RealisticSand);
                        if (GUILayout.Button("Heatmap")) seabedRenderer.SetVisualMode(SeabedRenderer.VisualMode.BathymetryHeatmap);
                        if (GUILayout.Button("Wire")) seabedRenderer.SetVisualMode(SeabedRenderer.VisualMode.TopographicWireframe);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"起伏強調: {seabedRenderer.HeightExaggeration:F1}x", GUILayout.Width(110));
                        seabedRenderer.HeightExaggeration = GUILayout.HorizontalSlider(seabedRenderer.HeightExaggeration, 1.0f, 4.0f);
                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.Space(2);
                GUILayout.Label("<b>[比較用 波入力 / Wave Input Preset]</b>");
                GUILayout.BeginHorizontal();
                bool isPacket = (boussSim.Condition == BoussinesqSimulation.InitialConditionType.SmoothWavePacket);
                bool isSingleSoliton = (boussSim.Condition == BoussinesqSimulation.InitialConditionType.GaussianPulse &&
                                        boussSim.ImpulseShape == BoussinesqSimulation.ImpulseShapeType.PlanarSoliton);

                GUI.color = isPacket ? new Color(0.7f, 1.0f, 0.7f) : Color.white;
                if (GUILayout.Button(isPacket ? "● ①有限波パケット (3波)" : "①有限波パケット (3波)")) {
                    SwitchWaveInputPreset(boussSim, isPacket: true);
                }
                GUI.color = isSingleSoliton ? new Color(1.0f, 0.85f, 0.6f) : Color.white;
                if (GUILayout.Button(isSingleSoliton ? "● ②単一孤立波パルス" : "②単一孤立波パルス")) {
                    SwitchWaveInputPreset(boussSim, isPacket: false);
                }
                GUI.color = Color.white;
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                GUILayout.Label("<b>[通常波源モード / Other Sources]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("連続造波")) { boussSim.Condition = BoussinesqSimulation.InitialConditionType.ContinuousWavemaker; boussSim.ResetSimulation(); }
                if (GUILayout.Button("周期パルス")) { boussSim.Condition = BoussinesqSimulation.InitialConditionType.PeriodicPulses; boussSim.ResetSimulation(); }
                if (GUILayout.Button("移動波源")) { boussSim.Condition = BoussinesqSimulation.InitialConditionType.MovingSource; boussSim.ResetSimulation(); }
                if (GUILayout.Button("マウス")) { boussSim.Condition = BoussinesqSimulation.InitialConditionType.InteractiveMouse; boussSim.ResetSimulation(); }
                GUILayout.EndHorizontal();

                if (boussSim.Condition == BoussinesqSimulation.InitialConditionType.ContinuousWavemaker) {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Freq: {boussSim.WavemakerFrequency:F2} Hz", GUILayout.Width(90));
                    boussSim.WavemakerFrequency = GUILayout.HorizontalSlider(boussSim.WavemakerFrequency, 0.05f, 0.45f);
                    GUILayout.EndHorizontal();
                } else if (boussSim.Condition == BoussinesqSimulation.InitialConditionType.GaussianPulse ||
                           boussSim.Condition == BoussinesqSimulation.InitialConditionType.PeriodicPulses) {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Shape:", GUILayout.Width(50));
                    if (GUILayout.Button("Planar (平面孤立波)")) boussSim.ImpulseShape = BoussinesqSimulation.ImpulseShapeType.PlanarSoliton;
                    if (GUILayout.Button("Circular (円形波)")) boussSim.ImpulseShape = BoussinesqSimulation.ImpulseShapeType.CircularPulse;
                    GUILayout.EndHorizontal();

                    if (boussSim.Condition == BoussinesqSimulation.InitialConditionType.GaussianPulse) {
                        if (GUILayout.Button("★ Trigger Impulse (孤立波を発生)")) {
                            boussSim.TriggerImpulse();
                        }
                    } else {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"Interval: {boussSim.PulseInterval:F2}s ({1f / boussSim.PulseInterval:F1} Hz)", GUILayout.Width(170));
                        boussSim.PulseInterval = GUILayout.HorizontalSlider(boussSim.PulseInterval, 0.4f, 4.0f);
                        GUILayout.EndHorizontal();
                    }
                } else if (boussSim.Condition == BoussinesqSimulation.InitialConditionType.MovingSource) {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Interval: {boussSim.PulseInterval:F2}s ({1f / boussSim.PulseInterval:F1} Hz)", GUILayout.Width(170));
                    boussSim.PulseInterval = GUILayout.HorizontalSlider(boussSim.PulseInterval, 0.2f, 2.5f);
                    GUILayout.EndHorizontal();
                } else if (boussSim.Condition == BoussinesqSimulation.InitialConditionType.InteractiveMouse) {
                    GUILayout.Label("<color=#a0f080><size=10>水面を左クリック / ドラッグして波を発生</size></color>");
                }

                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Dispersion: {boussSim.DispersionAlpha:F2}", GUILayout.Width(90));
                boussSim.DispersionAlpha = GUILayout.HorizontalSlider(boussSim.DispersionAlpha, 0.0f, 1.2f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Wave Amp: {boussSim.WavemakerAmplitude:F2}m", GUILayout.Width(90));
                boussSim.WavemakerAmplitude = GUILayout.HorizontalSlider(boussSim.WavemakerAmplitude, 0.1f, 2.5f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                boussSim.SideWallSponge = GUILayout.Toggle(boussSim.SideWallSponge, " Side Wall Sponge (側壁減衰)");
                GUILayout.EndHorizontal();

                if (boussSim.IsIntermediateToDeepWater) {
                    GUILayout.Label($"<color=#ffaa40><size=10>⚠️ 相対水深 kh = {boussSim.EstimatedKh:F2} (kh > 1.0)。浅水〜中間水深モデルの適用限界を超え分散精度が低下します。</size></color>");
                    if (GUILayout.Button("パラメータを中間水深域(kh≦1.0)に調整")) {
                        boussSim.AdjustParametersForBoussinesqRegime();
                    }
                }
                if (boussSim.Condition == BoussinesqSimulation.InitialConditionType.ContinuousWavemaker && boussSim.IsFrequencyAboveCutoff) {
                    GUILayout.Label($"<color=#ffaa40><size=10>⚠️ 周波数が遮断限界({boussSim.CutoffFrequency:F2}Hz)付近。波が沖へ進行できず停留・発振します。</size></color>");
                    if (GUILayout.Button("Auto-Clamp Freq (周波数を安全域に自動調整)")) {
                        boussSim.ClampFrequencyToSafeLimit();
                    }
                }
            } else if (simulation is FFTOceanSimulation fftSim) {
                GUILayout.Space(4);
                GUILayout.Label("<b>[海況プリセット / Sea State]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Calm (穏やか)")) fftSim.ApplySeaStatePreset(FFTOceanSimulation.SeaStatePreset.Calm);
                if (GUILayout.Button("Swell (うねり)")) fftSim.ApplySeaStatePreset(FFTOceanSimulation.SeaStatePreset.Swell);
                if (GUILayout.Button("Stormy (荒天)")) fftSim.ApplySeaStatePreset(FFTOceanSimulation.SeaStatePreset.Stormy);
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Wind: {fftSim.WindSpeed:F1}m/s", GUILayout.Width(95));
                fftSim.WindSpeed = GUILayout.HorizontalSlider(fftSim.WindSpeed, 2.0f, 25.0f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Wind Dir: {fftSim.WindDirectionDegrees:F0}°", GUILayout.Width(95));
                fftSim.WindDirectionDegrees = GUILayout.HorizontalSlider(fftSim.WindDirectionDegrees, 0.0f, 360.0f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Choppy: {fftSim.Choppiness:F2}", GUILayout.Width(95));
                fftSim.Choppiness = GUILayout.HorizontalSlider(fftSim.Choppiness, 0.0f, 3.0f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Swell: {fftSim.SwellStrength:F1}m", GUILayout.Width(95));
                fftSim.SwellStrength = GUILayout.HorizontalSlider(fftSim.SwellStrength, 0.0f, 4.0f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Swell Dir: {fftSim.SwellDirectionDegrees:F0}°", GUILayout.Width(95));
                fftSim.SwellDirectionDegrees = GUILayout.HorizontalSlider(fftSim.SwellDirectionDegrees, 0.0f, 360.0f);
                GUILayout.EndHorizontal();

                fftSim.EnableFoam = GUILayout.Toggle(fftSim.EnableFoam, " Foam (白波・泡立ちの描画)");
            }
        }

        private void SetVisualMode(WaterRenderer.VisualMode mode) {
            if (comparisonController != null) {
                comparisonController.SetVisualModeAll(mode);
            } else if (waterRenderer != null) {
                waterRenderer.SetVisualMode(mode);
            }
        }

        private void SwitchBoussinesqAdvection(BoussinesqSimulation sim, BoussinesqSimulation.AdvectionType target) {
            if (sim == null || sim.Advection == target) return;
            bool wasRunning = sim.IsRunning;
            sim.Advection = target;
            sim.ResetSimulation();
            if (wasRunning) sim.Play();
            else sim.Pause();
        }

        private void SwitchBathymetryPreset(BoussinesqSimulation sim, BoussinesqSimulation.BathymetryType target) {
            if (sim == null) return;
            bool wasRunning = sim.IsRunning;
            sim.SetBathymetryType(target);
            sim.ResetSimulation();
            if (wasRunning) sim.Play();
            else sim.Pause();
        }

        private void SwitchWaveInputPreset(BoussinesqSimulation sim, bool isPacket) {
            if (sim == null) return;
            bool wasRunning = sim.IsRunning;
            if (isPacket) {
                sim.Condition = BoussinesqSimulation.InitialConditionType.SmoothWavePacket;
                sim.WavemakerAmplitude = 0.35f;
                sim.WavemakerFrequency = 0.15f;
            } else {
                sim.Condition = BoussinesqSimulation.InitialConditionType.GaussianPulse;
                sim.ImpulseShape = BoussinesqSimulation.ImpulseShapeType.PlanarSoliton;
                sim.WavemakerAmplitude = 0.35f;
            }
            sim.ResetSimulation();
            if (wasRunning) sim.Play();
            else sim.Pause();
        }
        #endregion

        #region constants / static
        private static readonly GUILayoutOption[] OptExpandWidthTrue = new[] { GUILayout.ExpandWidth(true) };
        private static readonly GUILayoutOption[] OptWidth50 = new[] { GUILayout.Width(50f) };
        private static readonly GUILayoutOption[] OptWidth70 = new[] { GUILayout.Width(70f) };
        private static readonly GUILayoutOption[] OptWidth80 = new[] { GUILayout.Width(80f) };
        private static readonly GUILayoutOption[] OptWidth90 = new[] { GUILayout.Width(90f) };
        #endregion
    }
}
