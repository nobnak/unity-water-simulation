# CPU Bottleneck & Water Simulation Performance Report

- **Timestamp**: `2026-10-31 00:42:40`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)` (Direct3D11)
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V` (8 logical cores)
- **Unity Version**: `6000.3.27f1`
- **Environment**: Batchmode = `True`, VSync = `0`, TargetFrameRate = `-1`, TimeScale = `1`, RenderTarget = `Backbuffer (Offscreen/No window)`
- **Display Note**: Unity batchmode実行。OS表示ウィンドウおよびディスプレイスワップチェーンは存在しないため、フレームループ間隔を表示FPSとして扱ってはならない。

## 1. 物理Step単体およびシミュレーション進行費用（独立測定）

通常運転のFPS、1 Stepの費用、一定のシミュレーション時間を進める費用を区別して記録した結果：

| シーン名 | 格子解像度 | fixedDt | SubSteps | Dispatches | 1 Step CPU発行時間 | 1 Step GPU同期スループット | 物理1秒進行費用 (Steps) | 物理1秒 CPU発行 | 物理1秒 GPU同期 | 60Hz想定余力 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | 512x512 | 0.016s | 3 | 4 | 0.0169 ms | 0.6738 ms | 63 steps | 1.07 ms | 42.45 ms | 96.0% |
| Scene 02 (Boussinesq) | 512x512 | 0.016s | 4 | 41 | 0.1000 ms | 5.6423 ms | 63 steps | 6.30 ms | 355.47 ms | 66.1% |
| Scene 03 (FFTOcean) | 256x256 | 0.016s | 1 | 4 | 0.1452 ms | 1.4640 ms | 63 steps | 9.15 ms | 92.23 ms | 91.2% |

> [!NOTE]
> - **1 Step CPU発行時間**: CPUがコンピュートカーネルをディスパッチしドライバキューに積むまでの時間（GPU完了待ちは含まない）。
> - **1 Step GPU同期スループット**: CPUディスパッチ ＋ GPU計算実行 ＋ 最小限(1セル)の `AsyncGPUReadback` 完了待機を含む時間。PCI-e転送オーバーヘッドを最小化してGPU完了バリアを確立。
> - **60Hz想定余力**: リアルタイム描画フレーム枠（16.67ms）に対して1 StepのGPU同期スループットが占める割合の逆数（余力）。

## 2. PlayerLoop 実フレーム計測結果（非制限バッチモード・ループレート）

Unity PlayerLoop（PlayMode）上でフレームをまたいで計測した実フレーム間隔および処理量：

| シーン名 | 測定フェーズ | フレーム数 | 実経過時間 | 平均フレーム間隔 | P95 | P99 | バッチループ回転速度 | 実行Step数 | 進行物理時間 | 描画フレーム数 | CPU Main Thread | GPU Frame | GC Alloc |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | UI OFF | 60 | 11.22 ms | 0.1787 ms | 0.3917 ms | - | - | 0 | 0.000s | 60 | - | 取得不可 | 1895 B |
| Scene 01 (WaveEquation) | UI ON | 60 | 9.39 ms | 0.1551 ms | 0.2090 ms | 0.2245 ms | 6448.8 iter/s | 1 | 0.016s | 60 | 0.0751 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1901 B |
| Scene 02 (Boussinesq) | UI OFF | 60 | 11.71 ms | 0.1942 ms | 0.5402 ms | - | - | 1 | 0.016s | 60 | - | 取得不可 | 1890 B |
| Scene 02 (Boussinesq) | UI ON | 60 | 10.06 ms | 0.1668 ms | 0.3064 ms | 0.4065 ms | 5995.3 iter/s | 0 | 0.000s | 60 | 0.0898 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1889 B |
| Scene 03 (FFTOcean) | UI OFF | 60 | 12.24 ms | 0.2031 ms | 0.4332 ms | - | - | 0 | 0.000s | 60 | - | 取得不可 | 1906 B |
| Scene 03 (FFTOcean) | UI ON | 60 | 9.25 ms | 0.1533 ms | 0.2327 ms | 0.2625 ms | 6524.4 iter/s | 0 | 0.000s | 60 | 0.0698 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1889 B |

> [!IMPORTANT]
> - **表示FPSではないことの明記**: バッチモード実行ではモニタ垂直同期および表示スワップチェーンが存在しないため、フレーム間隔は極小（0.1〜0.2ms）でループ回転します。物理アキュムレータが閾値に達しない空フレーム（実行Step数: 0回/60フレーム）を含むため、上記ループ回転速度を実画面の「表示FPS」として報告・解釈してはなりません。
> - **GPU Frame Time**: バッチモードで表示スワップチェーン非アクティブのためドライバ非公開（取得不可）。

## 3. UI ON vs UI OFF 比較および GC Alloc 分析

| シーン名 | UI Overhead Delta | Order Bias | GC Alloc (UI ON) | GC Alloc (UI OFF) | GC Delta | 評価 |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| Scene 01 (WaveEquation) | -0.0236 ms | 0.1854 ms | 1901 B | 1895 B | 6 B | UI ON(1901 B) と UI OFF(1895 B) の差分(6 B)は測定ばらつきの範囲内であり有意差は確認できない。バッチモードではOSのGUIイベントが発行されないためIMGUI描画処理はスキップされており、約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因する。 |
| Scene 02 (Boussinesq) | -0.0274 ms | 0.0164 ms | 1889 B | 1890 B | -1 B | UI ON(1889 B) と UI OFF(1890 B) の差分(-1 B)は測定ばらつきの範囲内であり有意差は確認できない。バッチモードではOSのGUIイベントが発行されないためIMGUI描画処理はスキップされており、約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因する。 |
| Scene 03 (FFTOcean) | -0.0498 ms | 0.0443 ms | 1889 B | 1906 B | -17 B | UI ON(1889 B) と UI OFF(1906 B) の差分(-17 B)は測定ばらつきの範囲内であり有意差は確認できない。バッチモードではOSのGUIイベントが発行されないためIMGUI描画処理はスキップされており、約1.9KBのアロケーションはEditor PlayerLoop/コルーチン/ProfilerRecorder読み出し等のフレーム固定内部処理に起因する。 |

## 4. Scene 02 詳細要因分析（CFL・ディスパッチ・定数バインド・Accumulator）

### CFL Substep 数による影響（CPU発行時間 vs GPU同期スループット）
- **4 サブステップ (デフォルト)**: CPU発行 `0.1347 ms`, GPU同期スループット `5.6976 ms` (ディスパッチ数: 64)
- **2 サブステップ**: CPU発行 `0.0533 ms`, GPU同期スループット `2.9818 ms`
- **1 サブステップ**: CPU発行 `0.0525 ms`, GPU同期スループット `3.0248 ms`
- **備考**: GPU同期スループットは最小1セル(1x1 pixel)のAsyncGPUReadback完了待機を含み、PCI-e転送オーバーヘッドを最小化してGPU完了バリアを確立。CPU発行時間は同期なしのコマンドキュー積込時間。

### StepPhysics 内のディスパッチ CPU 発行時間内訳（同期なし）
- Predictor Dispatch (8x8 threadgroups): `0.0027 ms`
- CSTridiagonalX Dispatch (rows): `0.0017 ms`
- CSTridiagonalZ Dispatch (cols): `0.0018 ms`
- Filter 2-Pass Dispatch: `0.0039 ms`

### UI OnGUI & GC アロケーション分析
- UI 負荷 Delta: `-0.0274 ms/frame`
- GC Allocated In Frame (UI ON): `1889 B`
- GC Allocated In Frame (UI OFF): `1890 B`
- GC 差分: `-1 B`
- 割り当て元分析: バッチモード環境下ではGUIイベントが発行されないため、UI ON / OFF による有意なGCアロケーション差は確認できない（差は測定ばらつき内）。実測された約1.9KB/frameのアロケーションは、Unity EditorのPlayerLoop内部処理、コルーチン（MoveNext）、ProfilerRecorderの内部バッファ管理等に起因するフレーム固定オーバーヘッド。シミュレーション本体ホットパスの単独アロケーションは別途プロファイラコールスタックで確認が必要。

### Accumulator ラグとフレームレート別物理処理負荷
- 60 FPS時: 1 Step/frame (CPU発行 `0.1347 ms`, GPU同期 `5.6976 ms`)
- 30 FPS時: 2 Steps/frame (CPU発行 `0.2694 ms`, GPU同期 `11.3952 ms`)
- 20 FPS時: 3 Steps/frame (CPU発行 `0.4040 ms`, GPU同期 `17.0928 ms`)
- 最大遅延時 (Max Lag = 4): 4 Steps/frame (CPU発行 `0.5387 ms`, GPU同期 `22.7903 ms`)

