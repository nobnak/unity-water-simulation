# Water Simulation Speed vs Precision Balance Master Report

- **Timestamp**: `2026-10-31 14:49:45`
- **Unity Version**: `6000.3.27f1`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)`
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V`

## 1. 比較・採用の基準と許容誤差の定義

各シミュレーションモデルの物理適用範囲、目標スケール、および事前に定めた許容誤差基準を以下のように定義して検証を実施しました：

| モデル / シーン | 対象波長・振幅・水深 | 許容波高減衰 (10s) | 許容位相/波速誤差 | 許容質量誤差 | 許容格子振動指標 | 目標フレーム予算 |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| **WaveEquation (Scene 01)** | $\lambda \ge 8\Delta x, a \le 1.0\mathrm{m}$ (線形波・リップル) | $< 1.0\%$ | $< 2.0\%$ (位相) | N/A (電位ポテンシャル) | $< 10^{-4}$ | $< 0.8\,\mathrm{ms}$ (1 Step) |
| **Boussinesq (Scene 02)** | $h \in [0.5, 5.0]\mathrm{m}, a/h \le 0.5, kh \le 0.8$ (浅水・分散砕波) | $< 5.0\%$ (滑らかな波) | $< 5.0\%$ (位相・群速度) | $< 10^{-6}$ (相対) | $< 5 \times 10^{-4}$ | $< 6.0\,\mathrm{ms}$ (1 Step) |
| **FFTOcean (Scene 03)** | 深水外洋統計波 ($kh > 1.0, h \gg \lambda$) | 完全保存 | $< 0.1\%$ (解析解) | $< 10^{-6}$ (エネルギー) | なし (平滑統計面) | $< 1.5\,\mathrm{ms}$ (1 Step) |

## 2. 不具合修正・改善候補の3区分 採用判断一覧

本検証におけるすべての判定を以下の3区分に明確に整理しました：
- **区分 A: 修正を確認できた不具合** (Bugs confirmed fixed)
- **区分 B: 実測で優位性を確認できた改善** (Improvements with verified empirical superiority)
- **区分 C: 未実施、または結論を出せない候補** (Unimplemented candidates or without conclusive evidence)

| 区分 | 項目 / 対象モジュール | 施策内容 | 分類 | 判断 | 理由・エビデンス |
| :---: | :--- | :--- | :---: | :---: | :--- |
| **区分 A** | **WaveEquation ラプラシアン** | 正方/非正方格子の統一高次コンパクト化 | 【解析】/【本体GPU実測】 | **採用 (修正確認済み)** | $dx=1, dz=1.0001$ における周波数跳躍が **2.60% から 0.005% 未満へ解消**。定数・線形・2次多項式で厳密解一致。固定物理領域での格子細分化により2次空間精度を確認。 |
| **区分 A** | **Boussinesq FCT 散逸符号・保存性** | 高周波双調和散逸の符号是正と質量保存フラックス | 【本体GPU実測】 | **採用 (修正確認済み)** | 散逸符号反転バグを解消し、数値振動を抑えつつ総質量相対誤差 $< 10^{-6}$ を維持。 |
| **区分 B** | **Boussinesq PCR 特化カーネル** | 解像度特化カーネル (128/256 アンロール) | 【本体GPU実測】 | **採用済み (優位性確認)** | `Docs/BenchmarkResults.csv` 実測にて出力数値差 0.0 を確認しつつ、Generic比 **1.18〜1.27倍** の高速化を達成。 |
| **区分 B** | **FFTOcean 4-Dispatch 集約** | Packed IFFT + ALUオンザフライ計算 | 【解析】/【本体GPU実測】 | **採用中 (基準維持)** | 16 Dispatches を groupshared (16KB) 内完結で 4 Dispatches に集約。評価時刻一貫性（差分 0.0）を確認済み。 |
| **区分 C** | **WaveEquation 時間積分** | Kick-Drift vs Leapfrog vs Velocity Verlet | 【解析】/【CPU参照試験】 | **判断保留 (現行維持)** | CPU単一振動子試験にてKick-Driftの同時刻状態誤差は1次(1.11次)、Verletは2次(2.00次)を確認。GPU ComputeShader実装および帯域比較は【未実施】のため現行Kick-Driftを維持しつつ判断保留。 |
| **区分 C** | **Boussinesq TVD 界面再構成** | 1次風上から 2次TVD (Minmod) への置換 | 【未実施】 | **判断保留 (未実装)** | 1D単独移流基礎試験(CPU)では波高保持率向上が見られるが、本体2D GPU ComputeShader実装・Well-Balancing地形項結合・粗格子(384²)短縮効果は【未実施】。採用不要とは断定しない。 |
| **区分 C** | **Boussinesq ADI 反復数** | 1 Sweep vs 2..4 Sweeps (過剰反復性) | 【解析】/【本体GPU実測】 | **判断保留 (1 Sweep暫定)** | 実測GPU時間(`Docs/BenchmarkResults.csv`): 1 Sweep(41 Disp) 5.34ms、2 Sweeps(65 Disp) 8.61ms、3 Sweeps 12.13ms、4 Sweeps 15.54ms。$kh \le 0.8$ では理論近似誤差が支配的であり反復効果は逓減するが、極端な急勾配条件での必要性は未断定。 |
| **区分 C** | **FFTOcean 事前計算 / Radix-4** | Twiddleテクスチャ事前計算 / Radix-4化 | 【未実施】 | **判断保留 (未実装)** | ComputeShader未実装のためGPU実測値なし。以前の「10%遅延」という断定は撤回し、未実施として判断保留。 |

## 3. 実装実態に基づくリソース・計算費用の総合比較

各手法のメモリ使用量はシェーダーコードおよびスクリプトの RenderTexture 定義から厳密に計算（【解析】）、Dispatch数および実行時間は実測値（【本体GPU実測】）に基づいて整理しています：

| モジュール | 解像度 | 状態メモリ (VRAM) | スクラッチ/出力 | 合計 VRAM | 1 Step Dispatch数 | 実測 GPU 時間 (1 Step) | 推定 CPU 発行時間 | 備考・エビデンス |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **WaveEquation** | 512x512 | **4.0 MB** (RGFloat x2) | 4.25 MB (ARGBHalf x2 + R8 x1) | **8.25 MB** 【解析】 | 4 Dispatches (Init/Step/Norm/Imp) | ~0.65 ms 【本体GPU実測】 | ~0.02 ms | Compact 9pt ラプラシアン不連続修正済み |
| **Boussinesq (1 Sweep)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **41 Dispatches** 【本体GPU実測】 | **5.341 ms** 【本体GPU実測】 | 0.102 ms | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **Boussinesq (2 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **65 Dispatches** 【本体GPU実測】 | **8.609 ms** 【本体GPU実測】 | 0.162 ms | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **Boussinesq (3 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **89 Dispatches** 【本体GPU実測】 | **12.128 ms** 【本体GPU実測】 | 0.221 ms | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **Boussinesq (4 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **113 Dispatches** 【本体GPU実測】 | **15.541 ms** 【本体GPU実測】 | 0.281 ms | `Docs/BenchmarkResults.csv` Arc 140V 実測 |
| **FFTOcean** | 256x256 | **3.0 MB** (ARGBFloat x3) | 2.0 MB (ARGBHalf x2) | **5.0 MB** 【解析】 | **4 Dispatches** 【本体GPU実測】 | ~0.82 ms 【本体GPU実測】 | ~0.02 ms | 512x512 時は 20.0 MB 【解析】 |

> [!IMPORTANT]
> 未実装候補（TVD 384² による22%時間短縮、FFT事前計算Twiddleの10%遅延など）の未実測固定値はすべて撤回し、上記の実装・実測テーブルに一元化しました。比較実装が未実装または未計測の項目については「最速」「最適」「採用不要」とは結論しません。

## 4. 本体GPUで実行した試験と未実行の試験の一覧

### 4.1 本体GPUで実行した試験 (Verified on Actual GPU)
1. **WaveEquation GPU vs CPU 数値等価性試験**: ComputeShader `CSStep` と CPU 参照の最大差分が $2.98 \times 10^{-7}$ (完全一致) であることを実機実行で確認 (`RunGpuCpuEquivalence`, PASS)。
2. **Boussinesq ADI 反復別 GPU 時間 & Dispatch 数計測**: 512x512、4サブステップ条件において、1 Sweep (41 Disp / 5.341ms) から 4 Sweeps (113 Disp / 15.541ms) までの GPU 時間を実機計測 (`Docs/BenchmarkResults.csv`)。
3. **Boussinesq PCR 特化カーネル高速化実測**: 解像度固定アンロールカーネル (128/256) で出力差 0.0 を確認しつつ 1.18〜1.27倍 の高速化を確認 (`Docs/BenchmarkResults.csv`)。
4. **FFTOcean 評価時刻一貫性実フレーム検証**: 通常再生、Pause、Step、Reset の各遷移において変位・法線・白波の最大差分が 0.0 (完全一致) であることを実機検証 (`Docs/FftEvaluationTimeVerification.json`, PASS)。

### 4.2 CPU参照モデルで実行した試験 (CPU Reference Model Studies)
1. **ShallowWater 位相速度測定器 単体テスト**: PDE を解かず既知進行波 $\cos(kx - \omega t)$ を入力し、正方向・逆方向・多周期・任意位相で既知速度を完全復元できることを確認 (4ケース全て PASS)。
2. **ShallowWater 周期境界 Helmholtz 分散ソルバー 単体テスト**: Sherman-Morrison 巡回 Thomas 法により定数右辺解および Fourier モード解析固有値と厳密一致することを確認 (PASS)。
3. **ShallowWater 線形波動伝播・分散精度試験**: 線形微小振幅 ($a_0/h = 0.005$)・周期境界条件の下で、アンラップ位相による位相速度および波束重心トラッキングによる群速度を測定し、許容値判定を実施。
4. **ShallowWater 4階線形散逸フィルター単体選択性試験**: 16セル波長を99%以上保持しつつ2セル格子ノイズを10ステップで急減衰させる選択性を確認。
5. **ShallowWater 単独1D非線形移流基礎試験**: Upwind, TVD Minmod, TVD Superbee のセル界面再構成特性を比較。
6. **ShallowWater 斜面静水 Well-Balancing 基礎試験**: 斜面静水状態での最大偽流速・水位変動がゼロであることを確認 (PASS)。
7. **WaveEquation 空間格子細分化収束試験**: 固定物理領域 ($L=6.4\mathrm{m}$) 上で解析関数に対する誤差を評価し、一般的な2次空間精度を確認。
8. **WaveEquation 時間積分スキーム比較**: 一般時刻 ($T=1.25\mathrm{s}$)・非零初期位相 ($\pi/4$) でKick-Driftの同時刻状態誤差が1次、Velocity Verletが2次であることを確認。

### 4.3 未実施の試験 (Unexecuted / Unimplemented Candidates)
1. **WaveEquation**: Velocity Verlet / Staggered Leapfrog の GPU ComputeShader 実装、および GPU 実行時間・テクスチャ帯域の比較。
2. **Boussinesq**: 2nd-Order TVD (Minmod) の 2D GPU ComputeShader 実装、および 384² 粗格子化時の実測 GPU 時間。
3. **Boussinesq**: 転置 PCR (Col-Major Transpose) の GPU ComputeShader 実装。
4. **FFTOcean**: Radix-4 化および Twiddle 事前計算テクスチャの GPU 実装・キャッシュ効率実測。
5. **FFTOcean**: スペクトル総エネルギー保存誤差の実測。

## 5. 残るFAIL／未実測項目と課題

1. **FFT スペクトル総エネルギー保存誤差**: 過去検証で未取得のため現在 **【未実測】**。固定値 1.2e-7 を撤去済み。
2. **Boussinesq 波束群速度誤差の許容値未達 (FAIL)**: 単一波束（$kh = 0.5$）の重心トラッキングにおいて、群速度誤差が 7.6%〜9.7%（許容値 $\le 5.0\%$）となり許容基準を超過（FAIL）。位相速度（誤差 2.4% $\le 5.0\%$）は合格しているが、波束包絡線の分散変形における数値群速度推定精度に課題が残る。
3. **TVD 界面再構成の 2D GPU 実装と Well-Balancing 結合**: 1D CPU 試験では波高保持率向上が見られるが、2D 斜面地形項との Well-Balanced な結合および GPU 実装は未着手（未実施）。
4. **Boussinesq 適用限界域 ($kh > 0.8$) における Airy 理論との乖離**: 深水域 ($kh = 1.2, 1.6$) では Peregrine 理論自体の物理的限界により Airy 理論との乖離が生じる。これは数値誤差ではなく弱分散物理モデル自体の適用限界。

## 6. 各モジュールの詳細検証サマリー

各モジュールの詳細な測定コード、生データ、および検証エビデンスは以下の個別レポートに保存されています：
- [`Docs/WaveEquationPrecisionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/WaveEquationPrecisionReport.md)
- [`Docs/ShallowWaterAdvectionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/ShallowWaterAdvectionReport.md)
- [`Docs/BoussinesqDispersionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/BoussinesqDispersionReport.md)
- [`Docs/FftOceanBenchmarkReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/FftOceanBenchmarkReport.md)

