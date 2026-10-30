# CPU Bottleneck & Water Simulation Performance Report

- **Timestamp**: `2026-10-31 01:14:32`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)` (Direct3D11)
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V` (8 logical cores)
- **Unity Version**: `6000.3.27f1`
- **Environment**: Batchmode = `True`, VSync = `0`, TargetFrameRate = `-1`, TimeScale = `1`, RenderTarget = `Backbuffer (Offscreen/No window)`
- **Display Note**: Unity batchmode実行。OS表示ウィンドウおよびディスプレイスワップチェーンは存在しないため、フレームループ間隔を表示FPSとして扱ってはならない。

## 1. 物理Step単体およびシミュレーション進行費用（独立測定）

通常運転のFPS、1 Stepの費用、一定のシミュレーション時間を進める費用を区別して記録した結果：

| シーン名 | 格子解像度 | fixedDt | SubSteps | Dispatches | 1 Step CPU発行時間 | 1 Step GPU同期スループット | 物理1秒進行費用 (Steps) | 物理1秒 CPU発行 | 物理1秒 GPU同期 | 16.67ms枠 Step単体マージン |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | 512x512 | 0.016s | 3 | 4 | 0.0123 ms | 1.5403 ms | 63 steps | 0.78 ms | 97.04 ms | 90.8% |
| Scene 02 (Boussinesq) | 512x512 | 0.016s | 4 | 41 | 0.1032 ms | 5.6586 ms | 63 steps | 6.50 ms | 356.49 ms | 66.0% |
| Scene 03 (FFTOcean) | 256x256 | 0.016s | 1 | 4 | 0.1412 ms | 0.9376 ms | 63 steps | 8.89 ms | 59.07 ms | 94.4% |

> [!NOTE]
> - **1 Step CPU発行時間**: CPUがコンピュートカーネルをディスパッチしドライバキューに積むまでの時間（GPU完了待ちは含まない）。
> - **1 Step GPU同期スループット**: CPUディスパッチ ＋ GPU計算実行 ＋ 最小限(1セル)の `AsyncGPUReadback` 完了待機を含む時間。PCI-e転送オーバーヘッドを最小化してGPU完了バリアを確立。
> - **16.67ms枠 Step単体マージン**: 本指標は描画パス、UI、ポストプロセス、表示待機等を含まない『シミュレーションStep単体』の16.67ms(60Hz)枠に対する時間マージンであり、アプリケーション全体の総合的な動作余力を表すものではありません。

## 2. PlayerLoop 実フレーム計測結果（非制限バッチモード・ループレート）と処理量

Unity PlayerLoop（PlayMode）上でフレームをまたいで計測した実フレーム間隔、およびその間に実行された実際の処理量：

| シーン名 | 測定フェーズ | ループ回数 | 実経過時間 | 平均フレーム間隔 | P95 | P99 | バッチループ回転速度 | 実行Step数 | 進行物理時間 | 描画回数 | CPU Main Thread | GPU Frame | GC Alloc |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | UI OFF | 60 | 15.30 ms | 0.2454 ms | 0.5400 ms | - | - | 1 | 0.016s | 未計測 (バッチモード描画コールバック未検知) | - | 取得不可 | 1892 B |
| Scene 01 (WaveEquation) | UI ON | 60 | 8.67 ms | 0.1432 ms | 0.2590 ms | 0.3512 ms | 6985.7 iter/s | 0 | 0.000s | 未計測 (バッチモード描画コールバック未検知) | 0.0652 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1891 B |
| Scene 02 (Boussinesq) | UI OFF | 60 | 12.49 ms | 0.2070 ms | 0.4360 ms | - | - | 1 | 0.016s | 未計測 (バッチモード描画コールバック未検知) | - | 取得不可 | 1892 B |
| Scene 02 (Boussinesq) | UI ON | 60 | 9.53 ms | 0.1580 ms | 0.3333 ms | 0.3954 ms | 6328.6 iter/s | 0 | 0.000s | 未計測 (バッチモード描画コールバック未検知) | 0.0727 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1891 B |
| Scene 03 (FFTOcean) | UI OFF | 60 | 10.13 ms | 0.1680 ms | 0.2490 ms | - | - | 0 | 0.000s | 未計測 (バッチモード描画コールバック未検知) | - | 取得不可 | 1892 B |
| Scene 03 (FFTOcean) | UI ON | 60 | 11.57 ms | 0.1919 ms | 0.3671 ms | 0.5346 ms | 5210.1 iter/s | 0 | 0.000s | 未計測 (バッチモード描画コールバック未検知) | 0.0912 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1891 B |

> [!IMPORTANT]
> - **表示FPSではないことの明記**: バッチモード実行ではモニタ垂直同期および表示スワップチェーンが存在しないため、フレーム間隔は極小（0.1〜0.2ms）でループ回転します。物理アキュムレータが閾値に達しない空フレーム（実行Step数: 0回 または 1回 / 60ループ）を含むため、上記ループ回転速度（iter/s）を実画面の「表示FPS」として報告・解釈してはなりません。
> - **描画回数の実測**: カメラの描画完了イベント（RenderPipelineManager.endCameraRendering）をフックして実際の描画回数を計測。バッチモード等で描画パイプラインが実行されない場合は「未計測」と明記。
> - **GPU Frame Time**: バッチモードで表示スワップチェーン非アクティブのためドライバ非公開（取得不可）。

## 3. UI ON vs UI OFF 比較および GC Alloc 分析

| シーン名 | UI Overhead Delta | Order Bias | GC Alloc (UI ON) | GC Alloc (UI OFF) | GC Delta | 評価・分析 |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| Scene 01 (WaveEquation) | -0.1023 ms | 0.2034 ms | 1891 B | 1892 B | -1 B | UI ON(1891 B) と UI OFF(1892 B) の差分(-1 B)は測定ばらつきの範囲内であり有意差は確認できない。【仮説（コールスタック未取得のため推定）】約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因すると推定される。 |
| Scene 02 (Boussinesq) | -0.0490 ms | 0.0403 ms | 1891 B | 1892 B | -1 B | UI ON(1891 B) と UI OFF(1892 B) の差分(-1 B)は測定ばらつきの範囲内であり有意差は確認できない。【仮説（コールスタック未取得のため推定）】約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因すると推定される。 |
| Scene 03 (FFTOcean) | 0.0240 ms | 0.0033 ms | 1891 B | 1892 B | -1 B | UI ON(1891 B) と UI OFF(1892 B) の差分(-1 B)は測定ばらつきの範囲内であり有意差は確認できない。【仮説（コールスタック未取得のため推定）】約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因すると推定される。 |

## 4. Scene 02 詳細要因分析（CFL・ディスパッチ・定数バインド・Accumulator）

### CFL Substep 数による影響（CPU発行時間 vs GPU同期スループット、実測サブステップ・Dispatch数）

| 試験条件 | 1呼出 dt | 実測Substeps | 実測Dispatches | 1呼出 CPU発行時間 | 1呼出 GPU同期スループット | 物理1秒進行 (呼出数) | 物理1秒 CPU発行 | 物理1秒 GPU同期 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Step(0.016) [通常Step, 4サブステップ] | 0.016s | 4 | 41 | 0.0879 ms | 5.7226 ms | 62.5 | 5.49 ms | 357.66 ms |
| Step(0.008) [通常Step, 2サブステップ] | 0.008s | 2 | 21 | 0.0505 ms | 2.9951 ms | 125.0 | 6.31 ms | 374.39 ms |
| StepSingleSubstep(0.004) [単一サブステップ診断API試験] | 0.004s | 1 | 304 | 0.0306 ms | 1.6940 ms | 250.0 | 7.64 ms | 423.51 ms |

> [!NOTE]
> 通常のStep(0.004)はMacCormack偶数化規則により最低2サブステップとなるため、単一サブステップ(substeps=1)の費用は診断用API StepSingleSubstep(0.004)を用いて別試験として実測。サブステップ数・Dispatch数は固定値ではなく各条件実行後の実測値を保存。

### StepPhysics 内のディスパッチ CPU 発行時間内訳（同期なし）
- Predictor Dispatch (8x8 threadgroups): `0.0024 ms`
- CSTridiagonalX Dispatch (rows): `0.0016 ms`
- CSTridiagonalZ Dispatch (cols): `0.0018 ms`
- Filter 2-Pass Dispatch: `0.0037 ms`

### UI OnGUI & GC アロケーション分析
- UI 負荷 Delta: `-0.0490 ms/frame`
- GC Allocated In Frame (UI ON): `1891 B`
- GC Allocated In Frame (UI OFF): `1892 B`
- GC 差分: `-1 B`
- 割り当て元分析: 【仮説（コールスタック未取得のため推定）】UI ON と UI OFF の差分は測定ばらつきの範囲内であり、有意なUI固有アロケーション差は確認できない。実測された約1.9KB/frameは、Unity EditorのPlayerLoop内部処理、コルーチン（MoveNext）、ProfilerRecorderの内部バッファ管理等に起因するフレーム固定内部オーバーヘッドと推定される。シミュレーション本体ホットパスの単独アロケーションは別途プロファイラコールスタックで確認が必要。

### Accumulator スケジューリングとフレームレート別物理処理負荷（現行実装整合）

- **実装仕様**: 1フレームあたりの最大更新回数上限 `maxStepsPerFrame = 2`, アキュムレータクランプ上限 `0.032s` (最大2ステップ分)

| シナリオ | 想定FPS | フレームdt | 実行Step数/frame | 進行物理時間/frame | 切り捨て(ドロップ)時間 | 推定CPU発行費用 | 推定GPU同期スループット |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| 60 FPS 運転時 (dt ≈ 0.0167s) | 60 FPS | 0.0167s | 1 Step | 0.016s | 0.0000s | 0.0879 ms (推定) | 5.7226 ms (推定) |
| 30 FPS 運転時 (dt ≈ 0.0333s) | 30 FPS | 0.0333s | 2 Steps | 0.032s | 0.0013s | 0.1757 ms (推定) | 11.4452 ms (推定) |
| 20 FPS 運転時 (dt = 0.0500s) | 20 FPS | 0.0500s | 2 Steps | 0.032s | 0.0180s | 0.1757 ms (推定) | 11.4452 ms (推定) |
| 最大遅延時 (10 FPS, dt = 0.100s) | 10 FPS | 0.1000s | 2 Steps | 0.032s | 0.0680s | 0.1757 ms (推定) | 11.4452 ms (推定) |

> [!NOTE]
> 現行のWaterSimulationBase実装では、maxStepsPerFrame=2の上限およびaccumulatorクランプ(最大2ステップ分=0.032s)により、1フレームで実行されるStep数は最大2回に制限されている。フレームレートが20 FPS以下に低下した場合でも3〜4ステップは実行されず、超過した物理時間は切り捨て(ドロップ)されてスパイラル・オブ・デスを防止する。単純な費用の掛け算は実測値ではなく線形推定値である。

