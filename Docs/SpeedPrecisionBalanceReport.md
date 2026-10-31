# Water Simulation Speed vs Precision Balance Master Report

- **Timestamp**: `2026-10-31 12:31:13`
- **Unity Version**: `6000.3.27f1`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)`
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V`

## 1. 比較・採用の基準と許容誤差の定義

シミュレーションモデルごとに、目標とする対象現象、物理スケール、および許容誤差基準を以下のように定義して評価を実施しました：

| モデル / シーン | 対象波長・振幅・水深 | 許容波高減衰 (10s) | 許容位相/波速誤差 | 許容質量誤差 | 許容格子振動指標 | 目標フレーム予算 |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| **WaveEquation (Scene 01)** | $\lambda \ge 8\Delta x, a \le 1.0\mathrm{m}$ (線形波・リップル) | $< 1.0\%$ | $< 2.0\%$ (位相) | N/A (電位ポテンシャル) | $< 10^{-4}$ | $< 0.8\,\mathrm{ms}$ (1 Step) |
| **Boussinesq (Scene 02)** | $h \in [0.5, 5.0]\mathrm{m}, a/h \le 0.5$ (浅瀬・分散砕波) | $< 5.0\%$ (滑らかな波) | $< 3.0\%$ (群速度) | $< 10^{-6}$ (相対) | $< 5 \times 10^{-4}$ | $< 6.0\,\mathrm{ms}$ (1 Step) |
| **FFTOcean (Scene 03)** | 深水外洋統計波 ($kh > 1.0, h \gg \lambda$) | 完全保存 | $< 0.1\%$ (解析解) | $< 10^{-6}$ (エネルギー) | なし (平滑統計面) | $< 1.5\,\mathrm{ms}$ (1 Step) |

## 2. 不具合修正・改善候補の採用 / 不採用 判定一覧

| 項目 / 対象モジュール | 施策内容 | 分類 | 判定 | 理由・エビデンス |
| :--- | :--- | :---: | :---: | :--- |
| **WaveEquation ラプラシアン** | 正方/非正方格子の統一高次コンパクト化 | **不具合修正** | **採用** | $dx=1, dz=1.0001$ における周波数跳躍が **2.60% から 0.005% 未満へ解消**。定数・線形・2次多項式で厳密解一致。 |
| **WaveEquation 時間積分** | Kick-Drift から Leapfrog/Verlet への置換 | 改善候補 | **不採用 (現行維持)** | Kick-Drift は 1 Dispatch・2面テクスチャで 2次シンプレクティック構造を保ち、Velocity Verlet (2 Dispatch) と同等の位相精度を半分のディスパッチ費用で達成。 |
| **Boussinesq 移流スキーム** | 1次風上から 2次TVD (Minmod) への置換 | 改善候補 | **評価・保持** | 浅瀬急勾配での波高保持率が 92.8% から **98.2%** へ向上。ただし命令数 +18% 増となるため、粗い格子で同等精度を得る最適化オプションとして位置付け。 |
| **Boussinesq ADI 反復数** | 通常 1 Sweep (2 Dispatches) の維持 | 改善候補 | **採用 (現行維持)** | 方向分離誤差は $kh \le 0.8$ で $0.2\% \sim 1.5\%$ と極小であり、Airy理論近似誤差 (0.5%〜4.2%) より小さい。過剰な 3〜4 反復は処理時間を 2.5〜5倍 浪費。 |
| **Boussinesq PCR カーネル** | 解像度特化カーネル (128/256 アンロール) | 最適化 | **採用済み** | 出力数値差 0.0 で Generic比 **1.18〜1.27倍** の高速化。 |
| **FFTOcean アーキテクチャ** | Packed IFFT 4-Dispatch + ALU直接計算 | 改善候補 | **採用 (現行維持)** | 事前計算 Twiddle テクスチャ参照は帯域競合で 10% 低速化。ALU オンザフライ計算が最速。 |

## 3. 「同じ誤差を達成する費用」と「同じ時間予算で得られる精度」の総合比較

| 手法 | 達成相対波高誤差 | 格子解像度 | 所要時間 (物理1秒進行) | 状態メモリ量 | Dispatch数 (物理1秒) | 総合コスト効率 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **WaveEq (修正後 Compact 9pt)** | **< 0.01%** | 512x512 | **41.5 ms** | 16 MB (RGFloat x 2) | 252 (63 steps x 4) | **極めて高効率 (最適解)** |
| **Boussinesq (1st Upwind + FCT)** | ~3.8% | 512x512 | **343.6 ms** | 32 MB | 2583 (63 steps x 41) | **標準堅牢解 (ショック安定)** |
| **Boussinesq (TVD-Minmod)** | ~1.8% | 384x384 (粗格子化) | **268.0 ms** | 18 MB | 2583 (63 steps x 41) | **同等誤差を粗い格子で達成可能 (-22%時間節約)** |
| **FFTOcean (4 Dispatch Packed)** | < 0.001% | 256x256 | **38.9 ms** | 12 MB | 252 (63 steps x 4) | **深水最適解 (4 Dispatch集約)** |

## 4. 各モジュールの詳細検証サマリー

各モジュールの詳細な測定値、誤差テーブル、および検証エビデンスは以下の個別レポートに保存されています：
- [`Docs/WaveEquationPrecisionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/WaveEquationPrecisionReport.md)
- [`Docs/ShallowWaterAdvectionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/ShallowWaterAdvectionReport.md)
- [`Docs/BoussinesqDispersionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/BoussinesqDispersionReport.md)
- [`Docs/FftOceanBenchmarkReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/FftOceanBenchmarkReport.md)

