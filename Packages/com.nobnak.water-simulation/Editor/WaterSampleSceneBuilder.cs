using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Nobnak.WaterSimulation.Editor {
    /// <summary>
    /// Automated builder for the 5 sample scenes.
    /// Can be invoked via Editor menu or CLI:
    /// -executeMethod Nobnak.WaterSimulation.Editor.WaterSampleSceneBuilder.BuildAllSampleScenes
    /// </summary>
    public static class WaterSampleSceneBuilder {
        private const string SceneOutputDir = "Assets/Scenes";
        private const string PackageSamplesDir = "Packages/com.nobnak.water-simulation/Samples~";

        [MenuItem("Tools/Water Simulation/Build All Sample Scenes", false, 50)]
        public static void BuildAllSampleScenes() {
            if (!Directory.Exists(SceneOutputDir)) Directory.CreateDirectory(SceneOutputDir);

            string s1 = BuildScene01_WaveEquation();
            string s2 = BuildScene02_Boussinesq();
            string s3 = BuildScene03_FFTWater();
            string s4 = BuildScene04_Comparison_Dispersion();
            string s5 = BuildScene05_Comparison_Overview();

            // Register in EditorBuildSettings
            var sceneList = new EditorBuildSettingsScene[] {
                new EditorBuildSettingsScene(s1, true),
                new EditorBuildSettingsScene(s2, true),
                new EditorBuildSettingsScene(s3, true),
                new EditorBuildSettingsScene(s4, true),
                new EditorBuildSettingsScene(s5, true)
            };
            EditorBuildSettings.scenes = sceneList;

            // Copy to Package Samples~ directory
            CopyScenesToPackageSamples();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[WaterSimulation] Successfully built and registered all 5 sample scenes!");
        }

        private static string BuildScene01_WaveEquation() {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateDirectionalLight();
            var cam = CreateMainCamera(new Vector3(0f, 35f, -35f), new Vector3(45f, 0f, 0f));

            var waterGO = new GameObject("WaveEquationWater");
            var sim = waterGO.AddComponent<WaveEquationSimulation>();
            var renderer = waterGO.AddComponent<WaterRenderer>();
            renderer.TargetSimulation = sim;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute");
            var so = new SerializedObject(sim);
            so.FindProperty("waveShader").objectReferenceValue = compute;
            so.FindProperty("areaSize").vector2Value = new Vector2(50f, 50f);
            so.FindProperty("resolution").vector2IntValue = new Vector2Int(512, 512);
            so.FindProperty("waveSpeed").floatValue = 8.0f;
            so.FindProperty("damping").floatValue = 0.02f;
            so.FindProperty("obstaclePattern").enumValueIndex = (int)WaveEquationSimulation.ObstaclePattern.DoubleSlit;
            so.FindProperty("sourceMode").enumValueIndex = (int)WaveEquationSimulation.InputMode.SingleImpulse;
            so.FindProperty("sourceCenter").vector2Value = new Vector2(0.5f, 0.22f);
            so.FindProperty("sourceAmplitude").floatValue = 2.5f;
            so.FindProperty("sourceRadius").floatValue = 0.045f;
            so.ApplyModifiedProperties();

            var mat = CreateWaterMaterial();
            renderer.WaterMaterial = mat;

            var uiGO = new GameObject("SampleUI");
            var ui = uiGO.AddComponent<WaterSampleUI>();
            var soUI = new SerializedObject(ui);
            soUI.FindProperty("sampleTitle").stringValue = "01 2D Wave Equation - Ripple and Obstacles";
            soUI.FindProperty("whatToObserve").stringValue = "静水面に生じる波紋の即時応答、二重スリットを通過する波の回折（Diffraction）と相互干渉（Interference）、壁面での反射と外周スポンジ層での吸収。";
            soUI.FindProperty("whatCanBeControlled").stringValue = "スリット形状の変更（なし/単スリット/二重スリット/柱）、入力モード（単発インパルス/連続発振器/移動波源/マウスクリック）、波速と減衰の調整。";
            soUI.FindProperty("scopeAndLimitations").stringValue = "水深変化による屈折や分散は考慮されず、すべての波長が同一速度で伝播する線形波動方程式です。";
            soUI.FindProperty("simulation").objectReferenceValue = sim;
            soUI.FindProperty("waterRenderer").objectReferenceValue = renderer;
            soUI.FindProperty("cameraController").objectReferenceValue = cam.GetComponent<SimpleCameraController>();
            soUI.ApplyModifiedProperties();

            string path = $"{SceneOutputDir}/01_WaveEquation.unity";
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        private static string BuildScene02_Boussinesq() {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateDirectionalLight();
            var cam = CreateMainCamera(new Vector3(0f, 45f, -50f), new Vector3(40f, 0f, 0f));

            var waterGO = new GameObject("BoussinesqWater");
            var sim = waterGO.AddComponent<BoussinesqSimulation>();
            var renderer = waterGO.AddComponent<WaterRenderer>();
            renderer.TargetSimulation = sim;

            var seabedGO = new GameObject("Seabed");
            seabedGO.transform.SetParent(waterGO.transform, false);
            var seabedRenderer = seabedGO.AddComponent<SeabedRenderer>();
            seabedRenderer.TargetSimulation = sim;
            var seabedMat = CreateSeabedMaterial();
            seabedRenderer.SeabedMaterial = seabedMat;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            var so = new SerializedObject(sim);
            so.FindProperty("boussinesqShader").objectReferenceValue = compute;
            so.FindProperty("areaSize").vector2Value = new Vector2(100f, 100f);
            so.FindProperty("resolution").vector2IntValue = new Vector2Int(512, 512);
            so.FindProperty("dispersionAlpha").floatValue = 1.0f;
            so.FindProperty("bathymetryMode").enumValueIndex = (int)BoussinesqSimulation.BathymetryType.ShoalAndCape;
            so.FindProperty("deepWaterDepth").floatValue = 3.5f;
            so.FindProperty("shallowWaterDepth").floatValue = 0.8f;
            so.FindProperty("initialCondition").enumValueIndex = (int)BoussinesqSimulation.InitialConditionType.ContinuousWavemaker;
            so.FindProperty("impulseShape").enumValueIndex = (int)BoussinesqSimulation.ImpulseShapeType.PlanarSoliton;
            so.FindProperty("wavemakerAmplitude").floatValue = 0.35f;
            so.FindProperty("wavemakerFrequency").floatValue = 0.15f;
            so.FindProperty("pulseInterval").floatValue = 4.5f;
            so.ApplyModifiedProperties();

            var mat = CreateWaterMaterial();
            renderer.WaterMaterial = mat;

            var profGO = new GameObject("ProfileViewer");
            profGO.transform.SetParent(waterGO.transform, false);
            var prof = profGO.AddComponent<WaterProfileViewer>();
            prof.Configure(sim, WaterProfileViewer.ProfileAxis.ZAxis, 0.5f);
            prof.VerticalScale = 4.0f;
            prof.transform.position = waterGO.transform.position + new Vector3(0f, 2f, 0f);
            profGO.SetActive(false); // Initially hidden, toggleable via UI Display Mode

            var uiGO = new GameObject("SampleUI");
            var ui = uiGO.AddComponent<WaterSampleUI>();
            var soUI = new SerializedObject(ui);
            soUI.FindProperty("sampleTitle").stringValue = "02 Boussinesq Model - Shoal and Cape";
            soUI.FindProperty("whatToObserve").stringValue = "沖合から進入した波列・孤立波が、斜めの浅瀬（エメラルド透過色）で水深 h に応じた波速減速（c ~ sqrt(gh)）により屈折（Refraction）し波長が短縮する現象（Shoaling）、および岬の先端に波エネルギーが集中して波高が増大する現象。";
            soUI.FindProperty("whatCanBeControlled").stringValue = "対流方式（Baseline 1次風上 / Minmod 2次TVD・比較用）、海底地形（平坦/浅瀬と岬/斜面）、波源モード（連続造波/単発インパルス/周期パルス/移動波源/マウスクリック）、波形（平面孤立波/円形波）、分散係数 alpha、振幅・周波数・インターバル、Bathymetry水深マップ表示。";
            soUI.FindProperty("scopeAndLimitations").stringValue = "深さ平均モデルのため、砕波による波頭の巻き込み（Plunging breaker）や飛沫・気泡の3D流体計算は含まれません。強い非線形浅水変形（BP05 Case B等）において波高を過小評価する既知の精度限界があり、定量的な分裂波高の再現は保証されません。";
            soUI.FindProperty("simulation").objectReferenceValue = sim;
            soUI.FindProperty("waterRenderer").objectReferenceValue = renderer;
            soUI.FindProperty("seabedRenderer").objectReferenceValue = seabedRenderer;
            soUI.FindProperty("cameraController").objectReferenceValue = cam.GetComponent<SimpleCameraController>();
            soUI.FindProperty("profileViewer").objectReferenceValue = prof;
            soUI.ApplyModifiedProperties();

            string path = $"{SceneOutputDir}/02_Boussinesq.unity";
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        private static string BuildScene03_FFTWater() {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateDirectionalLight();
            var cam = CreateMainCamera(new Vector3(0f, 12f, -40f), new Vector3(15f, 0f, 0f));

            var waterGO = new GameObject("FFTOceanWater");
            var sim = waterGO.AddComponent<FFTOceanSimulation>();
            var renderer = waterGO.AddComponent<WaterRenderer>();
            renderer.TargetSimulation = sim;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute");
            var so = new SerializedObject(sim);
            so.FindProperty("fftShader").objectReferenceValue = compute;
            so.FindProperty("areaSize").vector2Value = new Vector2(200f, 200f);
            so.FindProperty("resolution").vector2IntValue = new Vector2Int(256, 256);
            so.FindProperty("seaState").enumValueIndex = (int)FFTOceanSimulation.SeaStatePreset.Swell;
            so.FindProperty("windSpeed").floatValue = 8.0f;
            so.FindProperty("windDirectionDegrees").floatValue = 45.0f;
            so.FindProperty("swellStrength").floatValue = 2.2f;
            so.FindProperty("swellDirectionDegrees").floatValue = 135.0f;
            so.FindProperty("choppiness").floatValue = 1.1f;
            so.FindProperty("enableFoam").boolValue = true;
            so.ApplyModifiedProperties();

            var mat = CreateWaterMaterial();
            renderer.WaterMaterial = mat;

            var uiGO = new GameObject("SampleUI");
            var ui = uiGO.AddComponent<WaterSampleUI>();
            var soUI = new SerializedObject(ui);
            soUI.FindProperty("sampleTitle").stringValue = "03 FFT Ocean Surface - Horizon Ocean";
            soUI.FindProperty("whatToObserve").stringValue = "遠方の嵐から届く長波長のうねり（Swell）と、局所的な風波（Wind sea）の共存。チョッピー波による尖った波頭（Trochoidal wave）とヤコビアンによる白波（Foam）。";
            soUI.FindProperty("whatCanBeControlled").stringValue = "海況プリセット（穏やか/うねり/荒れた海）、うねり強度・方向、風速・風向、チョッピー係数、Foamのトグル（泡を無効にして波形のみを評価可能）。";
            soUI.FindProperty("scopeAndLimitations").stringValue = "統計的周波数領域モデルのため、海岸線や障害物との反射・回折といった局所的な境界相互作用は直接扱えません。";
            soUI.FindProperty("simulation").objectReferenceValue = sim;
            soUI.FindProperty("waterRenderer").objectReferenceValue = renderer;
            soUI.FindProperty("cameraController").objectReferenceValue = cam.GetComponent<SimpleCameraController>();
            soUI.ApplyModifiedProperties();

            string path = $"{SceneOutputDir}/03_FFTWater.unity";
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        private static string BuildScene04_Comparison_Dispersion() {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateDirectionalLight();
            var cam = CreateMainCamera(new Vector3(0f, 30f, -45f), new Vector3(35f, 0f, 0f));

            // Left: Wave Equation
            var leftGO = new GameObject("Left_WaveEquation");
            leftGO.transform.position = new Vector3(-28f, 0f, 0f);
            var leftSim = leftGO.AddComponent<WaveEquationSimulation>();
            var leftRenderer = leftGO.AddComponent<WaterRenderer>();
            leftRenderer.TargetSimulation = leftSim;

            var computeWave = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute");
            var soL = new SerializedObject(leftSim);
            soL.FindProperty("waveShader").objectReferenceValue = computeWave;
            soL.FindProperty("areaSize").vector2Value = new Vector2(50f, 50f);
            soL.FindProperty("resolution").vector2IntValue = new Vector2Int(512, 512);
            soL.FindProperty("waveSpeed").floatValue = Mathf.Sqrt(9.81f * 5.0f); // Exact c = sqrt(g * 5m) = 7.00357 m/s
            soL.FindProperty("damping").floatValue = 0.0f; // Zero artificial damping for strict dispersion comparison
            soL.FindProperty("obstaclePattern").enumValueIndex = (int)WaveEquationSimulation.ObstaclePattern.None;
            soL.FindProperty("sourceMode").enumValueIndex = (int)WaveEquationSimulation.InputMode.SingleImpulse;
            soL.FindProperty("sourceCenter").vector2Value = new Vector2(0.5f, 0.5f);
            soL.FindProperty("sourceAmplitude").floatValue = 0.1f; // Linear small-amplitude regime (A/h = 0.02)
            soL.FindProperty("sourceRadius").floatValue = 0.05f;
            soL.ApplyModifiedProperties();
            leftRenderer.WaterMaterial = CreateWaterMaterial();

            // Right: Boussinesq
            var rightGO = new GameObject("Right_Boussinesq");
            rightGO.transform.position = new Vector3(28f, 0f, 0f);
            var rightSim = rightGO.AddComponent<BoussinesqSimulation>();
            var rightRenderer = rightGO.AddComponent<WaterRenderer>();
            rightRenderer.TargetSimulation = rightSim;

            var computeBous = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            var soR = new SerializedObject(rightSim);
            soR.FindProperty("boussinesqShader").objectReferenceValue = computeBous;
            soR.FindProperty("areaSize").vector2Value = new Vector2(50f, 50f);
            soR.FindProperty("resolution").vector2IntValue = new Vector2Int(512, 512);
            soR.FindProperty("dispersionAlpha").floatValue = 1.0f;
            soR.FindProperty("bedFriction").floatValue = 0.0f; // Zero friction to match undamped WaveEquation
            soR.FindProperty("bathymetryMode").enumValueIndex = (int)BoussinesqSimulation.BathymetryType.Flat;
            soR.FindProperty("deepWaterDepth").floatValue = 5.0f;
            soR.FindProperty("shallowWaterDepth").floatValue = 5.0f;
            soR.FindProperty("initialCondition").enumValueIndex = (int)BoussinesqSimulation.InitialConditionType.GaussianPulse;
            soR.FindProperty("impulseShape").enumValueIndex = (int)BoussinesqSimulation.ImpulseShapeType.CircularPulse;
            soR.FindProperty("sourceCenter").vector2Value = new Vector2(0.5f, 0.5f);
            soR.FindProperty("sourceRadius").floatValue = 0.05f;
            soR.FindProperty("wavemakerAmplitude").floatValue = 0.1f; // Exact 0.1m peak pulse matching Left_WaveEquation
            soR.ApplyModifiedProperties();
            rightRenderer.WaterMaterial = CreateWaterMaterial();

            // Profile Viewers
            var profL = new GameObject("ProfileViewer_WaveEq").AddComponent<WaterProfileViewer>();
            profL.Configure(leftSim, WaterProfileViewer.ProfileAxis.XAxis, 0.5f);
            profL.VerticalScale = 8.0f;
            profL.transform.position = leftGO.transform.position + new Vector3(0f, 4f, 0f);

            var profR = new GameObject("ProfileViewer_Boussinesq").AddComponent<WaterProfileViewer>();
            profR.Configure(rightSim, WaterProfileViewer.ProfileAxis.XAxis, 0.5f);
            profR.VerticalScale = 8.0f;
            profR.transform.position = rightGO.transform.position + new Vector3(0f, 4f, 0f);

            // Comparison Controller
            var compGO = new GameObject("ComparisonController");
            var comp = compGO.AddComponent<WaterComparisonController>();
            var soC = new SerializedObject(comp);
            soC.FindProperty("profileVerticalScale").floatValue = 8.0f;
            var simArr = soC.FindProperty("simulations");
            simArr.arraySize = 2;
            simArr.GetArrayElementAtIndex(0).objectReferenceValue = leftSim;
            simArr.GetArrayElementAtIndex(1).objectReferenceValue = rightSim;

            var renArr = soC.FindProperty("waterRenderers");
            renArr.arraySize = 2;
            renArr.GetArrayElementAtIndex(0).objectReferenceValue = leftRenderer;
            renArr.GetArrayElementAtIndex(1).objectReferenceValue = rightRenderer;
            soC.ApplyModifiedProperties();

            // Sample UI
            var uiGO = new GameObject("SampleUI");
            var ui = uiGO.AddComponent<WaterSampleUI>();
            var soUI = new SerializedObject(ui);
            soUI.FindProperty("sampleTitle").stringValue = "04 Comparison - Wave Eq vs Boussinesq (Dispersion)";
            soUI.FindProperty("whatToObserve").stringValue = "同一の一定水深（h = 5m）・小振幅の初期ガウスパルスから生じる波の伝播比較。左（波動方程式）は非分散性のためパルスの形が崩れず外側に広がる。右（Boussinesq系）は周波数分散性により短波長が遅れ、主波頭の後方に波列（分散テール）が形成される。";
            soUI.FindProperty("whatCanBeControlled").stringValue = "シミュレーションの一括開始・停止・リセット・時間進行速度。断面表示（プロファイル）の高さ強調。";
            soUI.FindProperty("scopeAndLimitations").stringValue = "分散関係の違いに焦点を当てた物理比較です。水深や初期パルス形状などの物理条件を厳密に揃えています。";
            soUI.FindProperty("simulation").objectReferenceValue = leftSim;
            soUI.FindProperty("cameraController").objectReferenceValue = cam.GetComponent<SimpleCameraController>();
            soUI.ApplyModifiedProperties();

            string path = $"{SceneOutputDir}/04_Comparison_Dispersion.unity";
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        private static string BuildScene05_Comparison_Overview() {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateDirectionalLight();
            var cam = CreateMainCamera(new Vector3(0f, 50f, -75f), new Vector3(34f, 0f, 0f));

            // Three water planes placed side by side (40m x 40m each, spaced at X = -48, 0, 48)
            // 1. Wave Equation
            var go1 = new GameObject("01_WaveEquation");
            go1.transform.position = new Vector3(-48f, 0f, 0f);
            var sim1 = go1.AddComponent<WaveEquationSimulation>();
            var ren1 = go1.AddComponent<WaterRenderer>();
            ren1.TargetSimulation = sim1;
            ren1.WaterMaterial = CreateWaterMaterial();

            var computeWave = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute");
            var so1 = new SerializedObject(sim1);
            so1.FindProperty("waveShader").objectReferenceValue = computeWave;
            so1.FindProperty("areaSize").vector2Value = new Vector2(40f, 40f);
            so1.FindProperty("resolution").vector2IntValue = new Vector2Int(256, 256);
            so1.FindProperty("waveSpeed").floatValue = 8.0f;
            so1.FindProperty("damping").floatValue = 0.02f;
            so1.FindProperty("obstaclePattern").enumValueIndex = (int)WaveEquationSimulation.ObstaclePattern.DoubleSlit;
            so1.FindProperty("sourceMode").enumValueIndex = (int)WaveEquationSimulation.InputMode.ContinuousOscillator;
            so1.FindProperty("sourceCenter").vector2Value = new Vector2(0.5f, 0.22f);
            so1.FindProperty("pulseInterval").floatValue = 0.625f;
            so1.FindProperty("sourceAmplitude").floatValue = 0.5f;
            so1.ApplyModifiedProperties();

            // 2. Boussinesq
            var go2 = new GameObject("02_Boussinesq");
            go2.transform.position = new Vector3(0f, 0f, 0f);
            var sim2 = go2.AddComponent<BoussinesqSimulation>();
            var ren2 = go2.AddComponent<WaterRenderer>();
            ren2.TargetSimulation = sim2;
            ren2.WaterMaterial = CreateWaterMaterial();

            var computeBous = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute");
            var so2 = new SerializedObject(sim2);
            so2.FindProperty("boussinesqShader").objectReferenceValue = computeBous;
            so2.FindProperty("areaSize").vector2Value = new Vector2(40f, 40f);
            so2.FindProperty("resolution").vector2IntValue = new Vector2Int(256, 256);
            so2.FindProperty("dispersionAlpha").floatValue = 1.0f;
            so2.FindProperty("bathymetryMode").enumValueIndex = (int)BoussinesqSimulation.BathymetryType.ShoalAndCape;
            so2.FindProperty("deepWaterDepth").floatValue = 1.8f;
            so2.FindProperty("shallowWaterDepth").floatValue = 0.5f;
            so2.FindProperty("initialCondition").enumValueIndex = (int)BoussinesqSimulation.InitialConditionType.ContinuousWavemaker;
            so2.FindProperty("wavemakerAmplitude").floatValue = 0.15f;
            so2.FindProperty("wavemakerFrequency").floatValue = 0.25f;
            so2.ApplyModifiedProperties();

            var seabedGO2 = new GameObject("Seabed");
            seabedGO2.transform.SetParent(go2.transform, false);
            var seabedRen2 = seabedGO2.AddComponent<SeabedRenderer>();
            seabedRen2.TargetSimulation = sim2;

            // 3. FFT Ocean
            var go3 = new GameObject("03_FFTOcean");
            go3.transform.position = new Vector3(48f, 0f, 0f);
            var sim3 = go3.AddComponent<FFTOceanSimulation>();
            var ren3 = go3.AddComponent<WaterRenderer>();
            ren3.TargetSimulation = sim3;
            ren3.WaterMaterial = CreateWaterMaterial();

            var computeFFT = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute");
            var so3 = new SerializedObject(sim3);
            so3.FindProperty("fftShader").objectReferenceValue = computeFFT;
            so3.FindProperty("areaSize").vector2Value = new Vector2(40f, 40f);
            so3.FindProperty("resolution").vector2IntValue = new Vector2Int(256, 256);
            so3.FindProperty("seaState").enumValueIndex = (int)FFTOceanSimulation.SeaStatePreset.Swell;
            so3.FindProperty("windSpeed").floatValue = 14.0f;
            so3.FindProperty("swellStrength").floatValue = 2.4f;
            so3.FindProperty("choppiness").floatValue = 1.25f;
            so3.ApplyModifiedProperties();

            // Comparison Controller
            var compGO = new GameObject("ComparisonController");
            var comp = compGO.AddComponent<WaterComparisonController>();
            var soC = new SerializedObject(comp);
            var simArr = soC.FindProperty("simulations");
            simArr.arraySize = 3;
            simArr.GetArrayElementAtIndex(0).objectReferenceValue = sim1;
            simArr.GetArrayElementAtIndex(1).objectReferenceValue = sim2;
            simArr.GetArrayElementAtIndex(2).objectReferenceValue = sim3;

            var renArr = soC.FindProperty("waterRenderers");
            renArr.arraySize = 3;
            renArr.GetArrayElementAtIndex(0).objectReferenceValue = ren1;
            renArr.GetArrayElementAtIndex(1).objectReferenceValue = ren2;
            renArr.GetArrayElementAtIndex(2).objectReferenceValue = ren3;
            soC.ApplyModifiedProperties();

            // UI
            var uiGO = new GameObject("SampleUI");
            var ui = uiGO.AddComponent<WaterSampleUI>();
            var soUI = new SerializedObject(ui);
            soUI.FindProperty("sampleTitle").stringValue = "05 Comparison - 3 Water Methods Overview";
            soUI.FindProperty("whatToObserve").stringValue = "3方式（左：2D波動方程式、中：Boussinesq系、右：FFT海面）の表現・特性一覧。\n・波動方程式：軽量、波紋即時応答、壁反射・回折。\n・Boussinesq系：水深応答、浅瀬屈折、波高集中、分散波列。\n・FFT海面：広域不規則海面、うねり・風波重畳、チョッピー波頭。";
            soUI.FindProperty("whatCanBeControlled").stringValue = "一括再生・停止・リセット、タイムスケール、描画モード切り替え、各個別シーンへのジャンプ。";
            soUI.FindProperty("scopeAndLimitations").stringValue = "各方式は用途・前提が異なるため同一物理条件ではありません。表現・操作・負荷の違いを確認し、制作に必要な方式を選定するためのショーケースです。";
            soUI.FindProperty("simulation").objectReferenceValue = sim2;
            soUI.FindProperty("waterRenderer").objectReferenceValue = ren2;
            soUI.FindProperty("cameraController").objectReferenceValue = cam.GetComponent<SimpleCameraController>();
            soUI.ApplyModifiedProperties();

            string path = $"{SceneOutputDir}/05_Comparison_Overview.unity";
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        private static GameObject CreateMainCamera(Vector3 position, Vector3 rotation) {
            var camGO = new GameObject("Main Camera");
            camGO.tag = "MainCamera";
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;

            camGO.AddComponent<UniversalAdditionalCameraData>();
            camGO.AddComponent<SimpleCameraController>();

            camGO.transform.position = position;
            camGO.transform.eulerAngles = rotation;
            return camGO;
        }

        private static void CreateDirectionalLight() {
            var lightGO = new GameObject("Directional Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.color = new Color(1f, 0.96f, 0.9f);
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static Material CreateWaterMaterial() {
            var shader = Shader.Find("Nobnak/WaterSimulation/WaterSurfaceCommon");
            if (shader != null) {
                var mat = new Material(shader) {
                    name = "M_WaterSurfaceCommon"
                };
                return mat;
            }
            return null;
        }

        private static Material CreateSeabedMaterial() {
            var assetMat = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.nobnak.water-simulation/Runtime/Materials/M_Seabed.mat");
            if (assetMat != null) return assetMat;
            var shader = Shader.Find("Nobnak/WaterSimulation/Seabed");
            if (shader != null) {
                var mat = new Material(shader) {
                    name = "M_Seabed"
                };
                return mat;
            }
            return null;
        }

        private static void CopyScenesToPackageSamples() {
            string[] sampleNames = new string[] {
                "01_WaveEquation",
                "02_Boussinesq",
                "03_FFTWater",
                "04_Comparison_Dispersion",
                "05_Comparison_Overview"
            };

            for (int i = 0; i < sampleNames.Length; i++) {
                string name = sampleNames[i];
                string srcScene = $"{SceneOutputDir}/{name}.unity";
                string destDir = $"{PackageSamplesDir}/{name}";
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                string destScene = $"{destDir}/{name}.unity";

                if (File.Exists(srcScene)) {
                    File.Copy(srcScene, destScene, true);
                }
            }
        }
    }
}
