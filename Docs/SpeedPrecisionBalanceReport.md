# Water Simulation Speed vs Precision Balance Master Report

- **Timestamp**: `2026-10-31 13:17:40`
- **Unity Version**: `6000.3.27f1`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)`
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V`

## 1. 比較・採用の基準と許容誤差の定義

各シミュレーションモデルの物理適用範囲、目標スケール、および許容誤差基準を以下のように定義して検証を実施しました：

| モデル / シーン | 対象波長・振幅・水深 | 許容波高減衰 (10s) | 許容位相/波速誤差 | 許容質量誤差 | 許容格子振動指標 | 目標フレーム予算 |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| **WaveEquation (Scene 01)** | $\lambda \ge 8\Delta x, a \le 1.0\mathrm{m}$ (線形波・リップル) | $< 1.0\%$ | $< 2.0\%$ (位相) | N/A (電位ポテンシャル) | $< 10^{-4}$ | $< 0.8\,\mathrm{ms}$ (1 Step) |
| **Boussinesq (Scene 02)** | $h \in [0.5, 5.0]\mathrm{m}, a/h \le 0.5, kh \le 0.8$ (浅水・分散砕波) | $< 5.0\%$ (滑らかな波) | $< 3.0\%$ (群速度) | $< 10^{-6}$ (相対) | $< 5 \times 10^{-4}$ | $< 6.0\,\mathrm{ms}$ (1 Step) |
| **FFTOcean (Scene 03)** | 深水外洋統計波 ($kh > 1.0, h \gg \lambda$) | 完全保存 | $< 0.1\%$ (解析解) | $< 10^{-6}$ (エネルギー) | なし (平滑統計面) | $< 1.5\,\mathrm{ms}$ (1 Step) |

## 2. 不具合修正・改善候補の3区分 採用判断一覧

本検証におけるすべての判定を以下の3区分に明確に整理しました：
- **区分 A: 修正を確認できた不具合** (Bugs confirmed fixed)
- **区分 B: 実測で優位性を確認できた改善** (Improvements with verified empirical superiority)
- **区分 C: 未実施、または結論を出せない候補** (Unimplemented candidates or without conclusive evidence)

| 区分 | 項目 / 対象モジュール | 施策内容 | 分類 | 判断 | 理由・エビデンス |
| :---: | :--- | :--- | :---: | :---: | :--- |
| **区分 A** | **WaveEquation ラプラシアン** | 正方/非正方格子の統一高次コンパクト化 | 【実測】/【解析計算】 | **採用 (修正確認済み)** | $dx=1, dz=1.0001$ における周波数跳躍が **2.60% から 0.005% 未満へ解消**。定数・線形・2次多項式で厳密解一致。格子細分化による空間収束次数（正方4次、非正方2次）を確認。 |
| **区分 A** | **Boussinesq FCT 散逸符号・保存性** | 高周波双調和散逸の符号是正と質量保存フラックス | 【実測】 | **採用 (修正確認済み)** | 散逸符号の反転バグを解消し、数値振動を抑えつつ総質量相対誤差 $< 10^{-6}$ を維持。 |
| **区分 B** | **Boussinesq PCR 特化カーネル** | 解像度特化カーネル (128/256 アンロール) | 【実測】 | **採用済み (優位性確認)** | `Docs/BenchmarkResults.csv` 実測にて出力数値差 0.0 を確認しつつ、Generic比 **1.18〜1.27倍** の高速化を達成。 |
| **区分 B** | **FFTOcean 4-Dispatch 集約** | Packed IFFT + ALUオンザフライ計算 | 【実測】/【解析計算】 | **採用中 (基準維持)** | 16 Dispatches を groupshared (16KB) 内完結で 4 Dispatches に集約。評価時刻一貫性誤差 0.0 を確認済み。 |
| **区分 C** | **WaveEquation 時間積分** | Kick-Drift vs Leapfrog vs Velocity Verlet | 【解析計算】/【未実施】 | **判断保留 (現行維持)** | CPU基礎検証で3方式とも2次シンプレクティック収束($\approx 2.0$)を確認。GPU複数Dispatch・テクスチャ帯域比較は【未実施】のため現行1-Dispatch Kick-Driftを維持。 |
| **区分 C** | **Boussinesq TVD 界面再構成** | 1次風上から 2次TVD (Minmod) への置換 | 【未実施】 | **判断保留 (未実装)** | 1D単独移流基礎試験では波高保持率向上が見られるが、2D GPU ComputeShader実装・Well-Balancing地形項結合・粗格子(384²)短縮効果は【未実施】。 |
| **区分 C** | **Boussinesq ADI 反復数** | 1 Sweep vs 2..4 Sweeps (過剰反復性) | 【実測】/【解析計算】 | **判断保留 (1 Sweep暫定)** | 実測GPU時間(`Docs/BenchmarkResults.csv`): 1 Sweep(41 Disp) 5.34ms、2 Sweeps(65 Disp) 8.61ms、3 Sweeps 12.13ms、4 Sweeps 15.54ms。$kh \le 0.8$ では理論近似誤差が支配的であり反復効果は逓減するが、極端な急勾配条件での必要性は未断定。 |
| **区分 C** | **FFTOcean 事前計算 / Radix-4** | Twiddleテクスチャ事前計算 / Radix-4化 | 【未実施】 | **判断保留 (未実装)** | シェーダー未実装のためGPU実測値なし。以前の「10%遅延」という断定は撤回し、未実施として扱う。 |

## 3. 実装実態に基づくリソース・計算費用の総合比較

各手法のメモリ使用量はシェーダーコードおよびスクリプトの RenderTexture 定義から厳密に計算（【解析計算】）、Dispatch数および実行時間は実測値（【実測】）に基づいて整理しています：

| モジュール | 解像度 | 状態メモリ (VRAM) | スクラッチ/出力 | 合計 VRAM | 1 Step Dispatch数 | 実測 GPU 時間 (1 Step) | 備考・エビデンス |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **WaveEquation** | 512x512 | **4.0 MB** (RGFloat x2) | 4.25 MB (ARGBHalf x2 + R8 x1) | **8.25 MB** 【解析計算】 | 4 Dispatches (Init/Step/Norm/Imp) | ~0.65 ms 【実測】 | Compact 9pt ラプラシアン不連続修正済み |
| **Boussinesq (1 Sweep)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析計算】 | **41 Dispatches** 【実測】 | **5.341 ms** 【実測】 | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **Boussinesq (2 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析計算】 | **65 Dispatches** 【実測】 | **8.609 ms** 【実測】 | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **Boussinesq (3 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析計算】 | **89 Dispatches** 【実測】 | **12.128 ms** 【実測】 | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **Boussinesq (4 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析計算】 | **113 Dispatches** 【実測】 | **15.541 ms** 【実測】 | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **FFTOcean** | 256x256 | **3.0 MB** (ARGBFloat x3) | 2.0 MB (ARGBHalf x2) | **5.0 MB** 【解析計算】 | **4 Dispatches** 【実測】 | ~0.82 ms 【実測】 | 512x512 時は 20.0 MB 【解析計算】 |

> [!IMPORTANT]
> 未実装候補（TVD 384² による22%時間短縮、FFT事前計算Twiddleの10%遅延など）の未実測固定値はすべて撤回し、上記の実装・実測テーブルに一元化しました。

## 4. 各モジュールの詳細検証サマリー

各モジュールの詳細な測定コード、生データ、および検証エビデンスは以下の個別レポートに保存されています：
- [`Docs/WaveEquationPrecisionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/WaveEquationPrecisionReport.md)
- [`Docs/ShallowWaterAdvectionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/ShallowWaterAdvectionReport.md)
- [`Docs/BoussinesqDispersionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/BoussinesqDispersionReport.md)
- [`Docs/FftOceanBenchmarkReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/FftOceanBenchmarkReport.md)

