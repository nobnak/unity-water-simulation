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
        private WaterComparisonController comparisonController;
        #endregion

        #region Unity lifecycle
        private void OnEnable() {
            comparisonController = FindFirstObjectByType<WaterComparisonController>();
        }

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
            float panelWidth = 360f;
            float panelHeight = isFolded ? 40f : math.min(Screen.height - 20f, 680f);

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
                GUILayout.Label("<b>[描画モード / Visual Mode]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Realistic")) SetVisualMode(WaterRenderer.VisualMode.Realistic);
                if (GUILayout.Button("HeightMap")) SetVisualMode(WaterRenderer.VisualMode.HeightMap);
                if (GUILayout.Button("Normal")) SetVisualMode(WaterRenderer.VisualMode.NormalVisual);
                if (GUILayout.Button("Wireframe")) SetVisualMode(WaterRenderer.VisualMode.Wireframe);
                if ((simulation != null && simulation.BathymetryTexture != null) || comparisonController != null) {
                    if (GUILayout.Button("Bathymetry")) SetVisualMode(WaterRenderer.VisualMode.Bathymetry);
                }
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
                GUILayout.Label("<b>[海底地形 / Bathymetry]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Flat (平坦)")) boussSim.SetBathymetryType(BoussinesqSimulation.BathymetryType.Flat);
                if (GUILayout.Button("Shoal (浅瀬・岬)")) boussSim.SetBathymetryType(BoussinesqSimulation.BathymetryType.ShoalAndCape);
                if (GUILayout.Button("Slope (斜面)")) boussSim.SetBathymetryType(BoussinesqSimulation.BathymetryType.LinearSlope);
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                GUILayout.Label("<b>[波源モード / Source Mode]</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Wavemaker")) boussSim.Condition = BoussinesqSimulation.InitialConditionType.ContinuousWavemaker;
                if (GUILayout.Button("Impulse")) boussSim.Condition = BoussinesqSimulation.InitialConditionType.GaussianPulse;
                if (GUILayout.Button("Periodic")) boussSim.Condition = BoussinesqSimulation.InitialConditionType.PeriodicPulses;
                if (GUILayout.Button("Moving")) boussSim.Condition = BoussinesqSimulation.InitialConditionType.MovingSource;
                if (GUILayout.Button("Mouse")) boussSim.Condition = BoussinesqSimulation.InitialConditionType.InteractiveMouse;
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

                if (boussSim.IsDepthTooDeepForShallowWater) {
                    GUILayout.Label("<color=#ffaa40><size=10>⚠️ 水深が領域長の5%を超過（深水波領域）。群速度が低下し停留します。</size></color>");
                    if (GUILayout.Button("Auto-Clamp Depth (水深を安全域に自動調整)")) {
                        boussSim.ClampDepthToSafeLimit();
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
        #endregion
    }
}
