# CPU Bottleneck & Water Simulation Performance Report

- **Timestamp**: `2026-10-31 02:08:15`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)` (Direct3D11)
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V` (8 logical cores)
- **Unity Version**: `6000.3.27f1`
- **Environment**: Batchmode = `True`, VSync = `0`, TargetFrameRate = `-1`, TimeScale = `1`, RenderTarget = `Backbuffer (Offscreen/No window)`
- **Display Note**: Unity batchmode実行。OS表示ウィンドウおよびディスプレイスワップチェーンは存在しないため、フレームループ間隔を表示FPSとして扱ってはならない。

## 1. 物理Step単体およびシミュレーション進行費用（独立測定）

通常運転のFPS、1 Stepの費用、一定のシミュレーション時間を進める費用を区別して記録した結果：

| シーン名 | 格子解像度 | fixedDt | SubSteps | Dispatches | 1 Step CPU発行時間 | 1 Step GPU同期スループット | 物理1秒進行費用 (Steps) | 物理1秒 CPU発行 | 物理1秒 GPU同期 | 16.67ms枠 Step単体マージン |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | 512x512 | 0.016s | 3 | 4 | 0.0152 ms | 0.6594 ms | 63 steps | 0.96 ms | 41.54 ms | 96.0% |
| Scene 02 (Boussinesq) | 512x512 | 0.016s | 4 | 41 | 0.1022 ms | 5.4542 ms | 63 steps | 6.44 ms | 343.62 ms | 67.3% |
| Scene 03 (FFTOcean) | 256x256 | 0.016s | 1 | 4 | 0.1282 ms | 0.6182 ms | 63 steps | 8.08 ms | 38.95 ms | 96.3% |

> [!NOTE]
> - **1 Step CPU発行時間**: CPUがコンピュートカーネルをディスパッチしドライバキューに積むまでの時間（GPU完了待ちは含まない）。
> - **1 Step GPU同期スループット**: CPUディスパッチ ＋ GPU計算実行 ＋ 最小限(1セル)の `AsyncGPUReadback` 完了待機を含む時間。PCI-e転送オーバーヘッドを最小化してGPU完了バリアを確立。
> - **16.67ms枠 Step単体マージン**: 本指標は描画パス、UI、ポストプロセス、表示待機等を含まない『シミュレーションStep単体』の16.67ms(60Hz)枠に対する時間マージンであり、アプリケーション全体の総合的な動作余力を表すものではありません。

## 2. PlayerLoop 実フレーム計測結果（非制限バッチモード・ループレート）と処理量

Unity PlayerLoop（PlayMode）上でフレームをまたいで計測した実フレーム間隔、およびその間に実行された実際の処理量：

| シーン名 | 測定フェーズ | ループ回数 | 実経過時間 | 平均フレーム間隔 | P95 | P99 | バッチループ回転速度 | 実行Step数 | 進行物理時間 | 描画回数 | CPU Main Thread | GPU Frame | GC Alloc |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | UI OFF | 60 | 10.88 ms | 0.1748 ms | 0.3916 ms | - | - | 0 | 0.000s | 未計測 (バッチモード描画コールバック未検知) | - | 取得不可 | 1892 B |
| Scene 01 (WaveEquation) | UI ON | 60 | 12.71 ms | 0.2102 ms | 0.3497 ms | 0.3759 ms | 4757.3 iter/s | 1 | 0.016s | 未計測 (バッチモード描画コールバック未検知) | 0.0881 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1891 B |
| Scene 02 (Boussinesq) | UI OFF | 60 | 11.23 ms | 0.1860 ms | 0.2678 ms | - | - | 1 | 0.016s | 未計測 (バッチモード描画コールバック未検知) | - | 取得不可 | 1900 B |
| Scene 02 (Boussinesq) | UI ON | 60 | 11.11 ms | 0.1841 ms | 0.2989 ms | 0.3242 ms | 5430.4 iter/s | 0 | 0.000s | 未計測 (バッチモード描画コールバック未検知) | 0.0843 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1891 B |
| Scene 03 (FFTOcean) | UI OFF | 60 | 9.30 ms | 0.1541 ms | 0.2712 ms | - | - | 1 | 0.016s | 未計測 (バッチモード描画コールバック未検知) | - | 取得不可 | 1892 B |
| Scene 03 (FFTOcean) | UI ON | 60 | 10.22 ms | 0.1695 ms | 0.2980 ms | 0.3273 ms | 5900.1 iter/s | 0 | 0.000s | 未計測 (バッチモード描画コールバック未検知) | 0.0801 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1891 B |

> [!IMPORTANT]
> - **表示FPSではないことの明記**: バッチモード実行ではモニタ垂直同期および表示スワップチェーンが存在しないため、フレーム間隔は極小（0.1〜0.2ms）でループ回転します。物理アキュムレータが閾値に達しない空フレーム（実行Step数: 0回 または 1回 / 60ループ）を含むため、上記ループ回転速度（iter/s）を実画面の「表示FPS」として報告・解釈してはなりません。
> - **描画回数の実測**: カメラの描画完了イベント（RenderPipelineManager.endCameraRendering）をフックして実際の描画回数を計測。バッチモード等で描画パイプラインが実行されない場合は「未計測」と明記。
> - **GPU Frame Time**: バッチモードで表示スワップチェーン非アクティブのためドライバ非公開（取得不可）。

## 3. UI ON vs UI OFF 比較および GC Alloc 分析

| シーン名 | UI Overhead Delta | Order Bias | GC Alloc (UI ON) | GC Alloc (UI OFF) | GC Delta | 評価・分析 |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| Scene 01 (WaveEquation) | 0.0354 ms | 0.0373 ms | 1891 B | 1892 B | -1 B | UI ON(1891 B) と UI OFF(1892 B) の差分(-1 B)は測定ばらつきの範囲内であり有意差は確認できない。【仮説（コールスタック未取得のため推定）】約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因すると推定される。 |
| Scene 02 (Boussinesq) | -0.0019 ms | 0.0178 ms | 1891 B | 1900 B | -9 B | UI ON(1891 B) と UI OFF(1900 B) の差分(-9 B)は測定ばらつきの範囲内であり有意差は確認できない。【仮説（コールスタック未取得のため推定）】約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因すると推定される。 |
| Scene 03 (FFTOcean) | 0.0153 ms | 0.0079 ms | 1891 B | 1892 B | -1 B | UI ON(1891 B) と UI OFF(1892 B) の差分(-1 B)は測定ばらつきの範囲内であり有意差は確認できない。【仮説（コールスタック未取得のため推定）】約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因すると推定される。 |

## 4. Scene 02 詳細要因分析（CFL・ディスパッチ・定数バインド・Accumulator）

### CFL Substep 数による影響（CPU発行時間 vs GPU同期スループット、実測サブステップ・Dispatch数）

| 試験条件 | 1呼出 dt | 実測Substeps | 1呼出実測Dispatch | 初期化Dispatch | 測定区間合計Dispatch (試行数) | 1呼出 CPU発行時間 | 1呼出 GPU同期スループット | 物理1秒進行 (呼出数) | 物理1秒 CPU発行 | 物理1秒 GPU同期 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Step(0.016) [通常Step, 4サブステップ] | 0.016s | 4 | 41 | 4 | 1230 (30回) | 0.0932 ms | 5.4677 ms | 62.5 | 5.82 ms | 341.73 ms |
| Step(0.008) [通常Step, 2サブステップ] | 0.008s | 2 | 21 | 4 | 630 (30回) | 0.0731 ms | 3.0029 ms | 125.0 | 9.13 ms | 375.36 ms |
| StepSingleSubstep(0.004) [単一サブステップ診断API試験] | 0.004s | 1 | 10 | 4 | 300 (30回) | 0.0231 ms | 1.6873 ms | 250.0 | 5.77 ms | 421.82 ms |

> [!NOTE]
> 1呼び出しあたりの実測Dispatch数、初期化費用(Reset時4 Dispatch)、および測定区間合計(試行数×1呼出)を厳密に区別して集計。診断API(StepSingleSubstep)は物理更新のみを実行する単一サブステップ(10 Dispatch)であり、通常Stepに含まれるフレーム末尾の法線計算・出力パック処理(CSComputeNormal, 1 Dispatch)を含まない。通常Stepでは4サブステップ時に 4×10 + 1 = 41 Dispatch、2サブステップ時に 2×10 + 1 = 21 Dispatch となる。固定値ではなくリセット完了後のカウンターを基準とした実測差分として10 Dispatchを取得し、試行回数(1, 10, 30, 50回)を変更しても1呼出あたりのDispatch数が10で不変であることを実証確認。

#### 単一サブステップ診断API 試行回数不変性検証 (Trial Invariance Verification)

| 試行回数 (Trials) | 開始カウンター (初期化後) | 終了カウンター | 測定区間合計Dispatch | 1呼び出しあたり実測Dispatch | 不変性判定 |
| :---: | :---: | :---: | :---: | :---: | :---: |
| 1 回 | 4 | 14 | 10 | **10** | PASS (一致) |
| 10 回 | 4 | 104 | 100 | **10** | PASS (一致) |
| 30 回 | 4 | 304 | 300 | **10** | PASS (一致) |
| 50 回 | 4 | 504 | 500 | **10** | PASS (一致) |

> [!TIP]
> 試行回数を1回、10回、30回、50回と変更しても、1呼び出しあたりの実測Dispatch数は常に厳密に10 Dispatchで一致しており、初期化時カウンター(4)や試行回数の累積が混入していないことを実証。

### StepPhysics 内のディスパッチ CPU 発行時間内訳（同期なし）
- Predictor Dispatch (8x8 threadgroups): `0.0022 ms`
- CSTridiagonalX Dispatch (rows): `0.0017 ms`
- CSTridiagonalZ Dispatch (cols): `0.0018 ms`
- Filter 2-Pass Dispatch: `0.0036 ms`

### UI OnGUI & GC アロケーション分析
- UI 負荷 Delta: `-0.0019 ms/frame`
- GC Allocated In Frame (UI ON): `1891 B`
- GC Allocated In Frame (UI OFF): `1900 B`
- GC 差分: `-9 B`
- 割り当て元分析: 【仮説（コールスタック未取得のため推定）】UI ON と UI OFF の差分は測定ばらつきの範囲内であり、有意なUI固有アロケーション差は確認できない。実測された約1.9KB/frameは、Unity EditorのPlayerLoop内部処理、コルーチン（MoveNext）、ProfilerRecorderの内部バッファ管理等に起因するフレーム固定内部オーバーヘッドと推定される。シミュレーション本体ホットパスの単独アロケーションは別途プロファイラコールスタックで確認が必要。

### Accumulator スケジューリングとフレームレート別物理処理負荷（現行実装整合）

- **実装仕様**: 1フレームあたりの最大更新回数上限 `maxStepsPerFrame = 2`, アキュムレータクランプ上限 `0.032s` (最大2ステップ分)

| シナリオ | 想定FPS | フレームdt | 実行Step数/frame | 進行物理時間/frame | 切り捨て(ドロップ)時間 | 推定CPU発行費用 | 推定GPU同期スループット |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| 60 FPS 運転時 (dt ≈ 0.0167s) | 60 FPS | 0.0167s | 1 Step | 0.016s | 0.0000s | 0.0932 ms (推定) | 5.4677 ms (推定) |
| 30 FPS 運転時 (dt ≈ 0.0333s) | 30 FPS | 0.0333s | 2 Steps | 0.032s | 0.0013s | 0.1863 ms (推定) | 10.9355 ms (推定) |
| 20 FPS 運転時 (dt = 0.0500s) | 20 FPS | 0.0500s | 2 Steps | 0.032s | 0.0180s | 0.1863 ms (推定) | 10.9355 ms (推定) |
| 最大遅延時 (10 FPS, dt = 0.100s) | 10 FPS | 0.1000s | 2 Steps | 0.032s | 0.0680s | 0.1863 ms (推定) | 10.9355 ms (推定) |

> [!NOTE]
> 現行のWaterSimulationBase実装では、maxStepsPerFrame=2の上限およびaccumulatorクランプ(最大2ステップ分=0.032s)により、1フレームで実行されるStep数は最大2回に制限されている。フレームレートが20 FPS以下に低下した場合でも3〜4ステップは実行されず、超過した物理時間は切り捨て(ドロップ)されてスパイラル・オブ・デスを防止する。単純な費用の掛け算は実測値ではなく線形推定値である。

