# Water Simulation Speed vs Precision Balance Master Report

- **Timestamp**: `2026-10-31 16:39:38`
- **Unity Version**: `6000.3.27f1`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)`
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V`
- **必須試験 総合判定**: **`PASS`** (失敗項目数: 0)

## 0. 必須試験 総合判定サマリー (Required Tests Verification)

| 必須検証項目 | カテゴリ | 判定 | 判定根拠・エビデンス |
| :--- | :---: | :---: | :--- |
| WaveEquation: 空間ラプラシアン多項式厳密解一致 & 格子アスペクト比不連続解消 | 空間精度・不連続解消 | **PASS** | 多項式最大誤差 < 3e-5 (PASS), dz=1.0001周波数跳躍 = 0.00499% (< 0.005%) |
| WaveEquation: GPU ComputeShader (CSStep) vs CPU 解析数値等価性 | 本体GPU実測 | **PASS** | 最大絶対差 = 2.98E-007 (< 1e-4, PASS) |
| WaveEquation: CFL安定性限界 (理論限界 dt <= 0.04782s) | 時間安定性 | **PASS** | 1.00x限界比で安定、1.02xで発散を正確に検出 (PASS) |
| SimulationBase: 解像度・領域変更と実動作検証 (WaveEq, Boussinesq, FFTOcean) 【本体GPU実測】 | 本体GPU実動作検証 | **PASS** | 3方式で初期化→1ステップ→解像度変更→1ステップ、Inspector/setter、無効設定拒否/復旧、有限性、テクスチャ解放を確認 (PASS) |
| Boussinesq: 波長・解像度診断 (alpha反映, 遮断判定, パルス未評価, 512上限超過) 【解析・単体】 | 診断ロジック単体テスト | **PASS** | alpha分散反映、遮断判定(長波代用なし)、パルス未評価、512上限超過検出を全て確認 (PASS) |
| ShallowWater: 位相速度測定器 単体テスト (PDE非依存・既知進行波入力) 【CPU参照】 | 測定器単体テスト | **PASS** | 正方向/逆方向/多周期/任意初期位相の4ケース全てで絶対誤差 < 8e-6 (PASS) |
| ShallowWater: 周期境界 Helmholtz 分散ソルバー 単体テスト (Sherman-Morrison 巡回 Thomas 法) 【CPU参照】 | 境界条件・線形ソルバー | **PASS** | 定数解誤差 = 2.86E-006, 離散固有値誤差 = 5.50E-007 (PASS) |
| ShallowWater: 半離散群速度 導関数照合 (dDenom = (2h^2/3Δx)sin(kΔx) vs 数値微分) 【解析・単体】 | 数理解析単体テスト | **PASS** | 全波数条件で解析導関数と数値微分の相対誤差 < 1e-4 (PASS) |
| ShallowWater: 推奨解像度 (>= 32 cells/wl) 精度試験 (位相・群速度誤差 <= 5.0%, 10秒減衰 < 5.0%) 【CPU参照・必須】 | 推奨解像度精度試験 | **PASS** | 正/逆方向・全kh条件で誤差 <= 5.0%, 10秒減衰 < 5.0% (PASS) |
| ShallowWater: 斜面静水平衡 (Lake-at-rest Well-Balancing) & 水量保存性 【CPU参照】 | 静水平衡・保存性 | **PASS** | 偽流速 = 0.00E+000 m/s, 水位変動 = 0.00E+000 m (PASS) |
| ShallowWater: 10秒伝播における波高減衰総合基準 (< 5.0% 減衰 / 振幅保持 > 95.0%) 【CPU参照】 | 波高保持総合基準 | **PASS** | 推奨解像度 (32 cells/wl) 減衰率 = 1.39% (< 5.0%, PASS) |
| FFTOcean: 評価時刻一貫性実フレーム検証 (再生/Pause/Step/Reset) 【本体GPU実測】 | 本体GPU実測 | **PASS** | 変位・法線・白波の最大差分 = 0.000000 (完全一致, PASS) |

## 1. 比較・採用の基準と許容誤差の定義

各シミュレーションモデルの物理適用範囲、目標スケール、および事前に定めた許容誤差基準を以下のように定義して検証を実施しました：

| モデル / シーン | 対象波長・振幅・水深 | 総合波高減衰基準 (10s) | 短時間分散許容誤差 (4s) | 許容質量誤差 | 許容格子振動指標 | 備考 |
| :--- | :--- | :---: | :---: | :---: | :---: | :--- |
| **WaveEquation (Scene 01)** | $\lambda \ge 8\Delta x, a \le 1.0\mathrm{m}$ (線形波・リップル) | $< 1.0\%$ | $< 2.0\%$ (位相) | N/A (電位ポテンシャル) | $< 10^{-4}$ | 線形波・リップル再現 |
| **Boussinesq (Scene 02)** | $h \in [0.5, 5.0]\mathrm{m}, a/h \le 0.5, kh \le 0.8$ (浅水・分散砕波) | $< 5.0\%$ (推奨 $\ge 32$ cells/wl) | $< 5.0\%$ (位相・群速度) | $< 10^{-6}$ (相対) | $< 5 \times 10^{-4}$ | 16セルでは半離散群速度誤差 7.6%、32セルで 1.9% |
| **FFTOcean (Scene 03)** | 深水外洋統計波 ($kh > 1.0, h \gg \lambda$) | 完全保存 | $< 0.1\%$ (解析解) | $< 10^{-6}$ (エネルギー) | なし (平滑統計面) | 深水線形重力波理論に基づく統計面 |

## 2. 不具合修正・改善候補の3区分 採用判断一覧

本検証におけるすべての判定を以下の3区分に明確に整理しました：
- **区分 A: 修正を確認できた不具合** (Bugs confirmed fixed)
- **区分 B: 実測で優位性を確認できた改善** (Improvements with verified empirical superiority)
- **区分 C: 未実施、または結論を出せない候補** (Unimplemented candidates or without conclusive evidence)

| 区分 | 項目 / 対象モジュール | 施策内容 | 分類 | 判断 | 理由・エビデンス |
| :---: | :--- | :--- | :---: | :---: | :--- |
| **区分 A** | **WaveEquation ラプラシアン** | 正方/非正方格子の統一高次コンパクト化 | 【解析】/【本体GPU実測】 | **採用 (修正確認済み)** | $dx=1, dz=1.0001$ における周波数跳躍が **2.60% から 0.005% 未満へ解消**。定数・線形・2次多項式で厳密解一致。固定物理領域での格子細分化により2次空間精度を確認。 |
| **区分 A** | **Boussinesq FCT 散逸符号・保存性** | 高周波双調和散逸の符号是正と質量保存フラックス | 【本体GPU実測】 | **採用 (修正確認済み)** | 散逸符号反転バグを解消し、数値振動を抑えつつ総質量相対誤差 $< 10^{-6}$ を維持。 |
| **区分 A** | **SimulationBase 実装制約バリデーション** | 不正解像度・非正領域の早期拒絶・不正状態防止 | 【単体テスト】/【実機検証】 | **採用 (バリデーション強化)** | WaveEquation (16..), Boussinesq (32..512), FFTOcean (正方・2の冪..512) の制約拒絶・受理を確認。無効設定での不正実行を防止。 |
| **区分 B** | **Boussinesq PCR 特化カーネル** | 解像度特化カーネル (128/256 アンロール) | 【本体GPU実測】 | **採用済み (優位性確認)** | `Docs/BenchmarkResults.csv` 実測にて出力数値差 0.0 を確認しつつ、Generic比 **1.18〜1.27倍** の高速化を達成。 |
| **区分 B** | **FFTOcean 4-Dispatch 集約** | Packed IFFT + ALUオンザフライ計算 | 【解析】/【本体GPU実測】 | **採用中 (基準維持)** | 16 Dispatches を groupshared (16KB) 内完結で 4 Dispatches に集約。評価時刻一貫性（差分 0.0）を確認済み。 |
| **区分 C** | **WaveEquation 時間積分** | Kick-Drift vs Leapfrog vs Velocity Verlet | 【解析】/【CPU参照試験】 | **判断保留 (現行維持)** | CPU単一振動子試験にてKick-Driftの同時刻状態誤差は1次(1.11次)、Verletは2次(2.00次)を確認。GPU ComputeShader実装および帯域比較は【未実施】のため現行Kick-Driftを維持しつつ判断保留。 |
| **区分 C** | **Boussinesq 群速度精度と推奨格子解像度** | 16セル波長群速度誤差(7.6%)の原因切り分けと推奨解像度 | 【解析】/【CPU参照試験】 | **設計基準整理 (32セル推奨)** | 半離散分散関係と一致。32セル波長以上では誤差 1.9% (<= 5.0%) で合格。ただしこれはCPU参照モデルの基礎特性であり、本体GPUおよび実シーンでの正式対応範囲判定は実機測定後に決定。 |
| **区分 C** | **Boussinesq TVD 界面再構成** | 1次風上から 2次TVD (Minmod) への置換 | 【未実施】 | **判断保留 (未実装)** | 1D単独移流基礎試験(CPU)では波高保持率向上が見られるが、本体2D GPU ComputeShader実装・Well-Balancing地形項結合・粗格子(384²)短縮効果は【未実施】。採用不要とは断定しない。 |
| **区分 C** | **Boussinesq ADI 反復数** | 1 Sweep vs 2..4 Sweeps (過剰反復性) | 【解析】/【本体実測】 | **判断保留 (1 Sweep暫定)** | 実測壁時計時間(`Docs/BenchmarkResults.csv`): 1 Sweep(41 Disp) 5.34ms、2 Sweeps(65 Disp) 8.61ms、3 Sweeps 12.13ms、4 Sweeps 15.54ms。$kh \le 0.8$ では理論近似誤差が支配的であり反復効果は逓減するが、極端な急勾配条件での必要性は未断定。 |
| **区分 C** | **FFTOcean 事前計算 / Radix-4** | Twiddleテクスチャ事前計算 / Radix-4化 | 【未実施】 | **判断保留 (未実装)** | ComputeShader未実装のためGPU実測値なし。以前の「10%遅延」という断定は撤回し、未実施として判断保留。 |

## 3. 実装実態に基づくリソース・計算費用の総合比較

各手法のメモリ使用量はシェーダーコードおよびスクリプトの RenderTexture 定義から厳密に計算（【解析】）、処理時間は `Docs/BenchmarkResults.csv` の実測値に基づき、同期を含む壁時計時間（AvgWallMsPerStep）として記載しています（GPU単独時間は別途GPU時間計測が必要なため【未実測】と区分）：

| モジュール | 解像度 | 状態メモリ (VRAM) | スクラッチ/出力 | 合計 VRAM | 1 Step Dispatch数 | 同期を含む壁時計時間 (AvgWallMsPerStep) | GPU単独時間 | 測定コミット / 物理条件 / 測定元 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **WaveEquation** | 512x512 | **4.0 MB** (RGFloat x2) | 4.25 MB (ARGBHalf x2 + R8 x1) | **8.25 MB** 【解析】 | 4 Dispatches | **0.174 ms** (1 Step) 【本体実測】 | 【未実測】 | Commit `d5446df` (512x512, 3 substeps, 100 steps, $c=8\mathrm{m/s}, L=50\mathrm{m}$, Arc 140V) |
| **Boussinesq (1 Sweep)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **41 Dispatches** | **5.341 ms** (1 Step) 【本体実測】 | 【未実測】 | Commit `d5446df` (512x512, 4 substeps, $\Delta t_{\mathrm{step}}=0.016\mathrm{s}$, 1 Sweep, $\alpha=1, h=3.5\mathrm{m}$, Arc 140V) |
| **Boussinesq (2 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **65 Dispatches** | **8.609 ms** (1 Step) 【本体実測】 | 【未実測】 | Commit `d5446df` (512x512, 4 substeps, 2 Sweeps, Arc 140V) |
| **Boussinesq (3 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **89 Dispatches** | **12.128 ms** (1 Step) 【本体実測】 | 【未実測】 | Commit `d5446df` (512x512, 4 substeps, 3 Sweeps, Arc 140V) |
| **Boussinesq (4 Sweeps)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 16.0 MB (RGFloat x5 + ARGBHalf x3) | **28.0 MB** 【解析】 | **113 Dispatches** | **15.541 ms** (1 Step) 【本体実測】 | 【未実測】 | Commit `d5446df` (512x512, 4 substeps, 4 Sweeps, Arc 140V) |
| **FFTOcean (256x256)** | 256x256 | **3.0 MB** (ARGBFloat x3) | 2.0 MB (ARGBHalf x2) | **5.0 MB** 【解析】 | **4 Dispatches** | **0.122 ms** (1 Step) 【本体実測】 | 【未実測】 | Commit `d5446df` (256x256, 1 substep, 100 steps, Arc 140V) |
| **FFTOcean (512x512)** | 512x512 | **12.0 MB** (ARGBFloat x3) | 8.0 MB (ARGBHalf x2) | **20.0 MB** 【解析】 | **4 Dispatches** | **0.528 ms** (1 Step) 【本体実測】 | 【未実測】 | Commit `d5446df` (512x512, 1 substep, 100 steps, Arc 140V) |

> [!IMPORTANT]
> `Docs/BenchmarkResults.csv` に記録されている値は同期を含む壁時計時間 (AvgWallMsPerStep) であり、GPU単独実行時間ではありません。根拠のない推計値 (~0.65ms, ~0.82ms等) はすべて撤回し、コミット番号・解像度・物理条件を明記した測定元データに統一しました。

## 4. 本体GPUで実行した試験と未実行の試験の一覧

### 4.1 本体GPUで実行した試験 (Verified on Actual GPU)
1. **WaveEquation GPU vs CPU 数値等価性試験**: ComputeShader `CSStep` と CPU 参照の最大差分が $2.98 \times 10^{-7}$ (完全一致) であることを実機実行で確認 (`RunGpuCpuEquivalence`, PASS)。
2. **Boussinesq ADI 反復別 壁時計時間 & Dispatch 数計測**: 512x512、4サブステップ条件において、1 Sweep (41 Disp / 5.341ms) から 4 Sweeps (113 Disp / 15.541ms) までの同期を含む壁時計時間を実機計測 (`Docs/BenchmarkResults.csv`, Commit `d5446df`)。
3. **Boussinesq PCR 特化カーネル高速化実測**: 解像度固定アンロールカーネル (128/256) で出力差 0.0 を確認しつつ 1.18〜1.27倍 の高速化を確認 (`Docs/BenchmarkResults.csv`, Commit `d5446df`)。
4. **FFTOcean 評価時刻一貫性実フレーム検証**: 通常再生、Pause、Step、Reset の各遷移において変位・法線・白波の最大差分が 0.0 (完全一致) であることを実機検証 (`Docs/FftEvaluationTimeVerification.json`, Commit `18ef6b8`, PASS)。
5. **SimulationBase 解像度・領域変更と実動作検証**: WaveEquation, Boussinesq, FFTOcean の3方式において、初期化→1ステップ→解像度変更→1ステップ、Inspector/setter、無効設定拒否/復旧、有限性、テクスチャ解放を本体GPU上で実機検証 (`RunConfigurationRealExecutionValidationTests`, PASS)。

### 4.2 CPU参照モデルで実行した試験 (CPU Reference Model Studies)
1. **ShallowWater 位相速度測定器 単体テスト**: PDE を解かず既知進行波 $\cos(kx - \omega t)$ を入力し、正方向・逆方向・多周期・任意位相で既知速度を完全復元できることを確認 (4ケース全て PASS)。
2. **ShallowWater 周期境界 Helmholtz 分散ソルバー 単体テスト**: Sherman-Morrison 巡回 Thomas 法により定数右辺解および Fourier モード解析固有値と厳密一致することを確認 (PASS)。
3. **ShallowWater 群速度FAIL要因の数理的切り分け試験**: 中心差分＋離散Helmholtz演算子の半離散分散関係と照合し、16セル波長での群速度低下 (7.6%) が空間2階中心差分の打ち切り誤差 $\cos(k\Delta x) - 1$ と一致することを確認。32セル波長では 1.9% ($\le 5.0\%$) に収束することを実証。
4. **ShallowWater 10秒伝播波高減衰総合基準試験**: 推奨解像度 (32 cells/wl) において 10秒伝播後の減衰率が 2.9% となり、総合目標基準 (< 5.0%) を満たすことを確認 (PASS)。
5. **ShallowWater 4階線形散逸フィルター単体選択性試験**: 100ステップで 99.77%、1000ステップで 97.71% を保持し、2セル格子ノイズは10ステップで 0.8510、100ステップで 0.1989 となる選択性を確認。
6. **ShallowWater 単独1D非線形移流基礎試験**: Upwind, TVD Minmod, TVD Superbee のセル界面再構成特性を比較。
7. **ShallowWater 斜面静水 Well-Balancing 基礎試験**: 斜面静水状態での最大偽流速・水位変動がゼロであることを確認 (PASS)。
8. **WaveEquation 空間格子細分化収束試験**: 固定物理領域 ($L=6.4\mathrm{m}$) 上で解析関数に対する誤差を評価し、一般的な2次空間精度を確認。
9. **WaveEquation 時間積分スキーム比較**: 一般時刻 ($T=1.25\mathrm{s}$)・非零初期位相 ($\pi/4$) でKick-Driftの同時刻状態誤差が1次、Velocity Verletが2次であることを確認。

### 4.3 未実施の試験 (Unexecuted / Unimplemented Candidates)
1. **GPU単独実行時間 (GPU Timers)**: CPU同期待機やDispatch発行オーバーヘッドを除去したGPUパイプライン単独実行時間のプロファイリング。
2. **WaveEquation**: Velocity Verlet / Staggered Leapfrog の GPU ComputeShader 実装、および GPU 実行時間・テクスチャ帯域の比較。
3. **Boussinesq**: 2nd-Order TVD (Minmod) の 2D GPU ComputeShader 実装、および 384² 粗格子化時の実測 GPU 時間。
4. **Boussinesq**: 転置 PCR (Col-Major Transpose) の GPU ComputeShader 実装。
5. **FFTOcean**: Radix-4 化および Twiddle 事前計算テクスチャの GPU 実装・キャッシュ効率実測。
6. **FFTOcean**: スペクトル総エネルギー保存誤差の実測。

## 5. 残る課題と未実測項目

1. **FFT スペクトル総エネルギー保存誤差**: 過去検証で未取得のため現在 **【未実測】**。固定値 1.2e-7 を撤去済み。
2. **Boussinesq 16セル波長における群速度精度 (7.6% 誤差)**: 16セル波長では2階中心差分の半離散誤差 $\cos(k\Delta x)$ により群速度誤差が 7.6% (許容閾値 5.0% 超過) となります。ただし32セル波長では 1.9% に収束し合格するため、本体GPUの不具合ではなく空間離散化解像度の設計基準として整理されます。
3. **TVD 界面再構成の 2D GPU 実装と Well-Balancing 結合**: 1D CPU 試験では波高保持率向上が見られるが、2D 斜面地形項との Well-Balanced な結合および GPU 実装は未着手（未実施）。
4. **Boussinesq 適用限界域 ($kh > 0.8$) における Airy 理論との乖離**: 深水域 ($kh = 1.2, 1.6$) では Peregrine 理論自体の物理的限界により Airy 理論との乖離が生じる。これは数値誤差ではなく弱分散物理モデル自体の適用限界。
5. **Scene 02 Shoal の波高波打ち・高速上下動の要因**: 設定検証やCPU参照試験の合格（主波 94.4 cells/wl 確保）のみで解消済みと結論せず、非線形浅水変形に伴う高調波成分の急峻化、地形屈折、砕波モデル、および2D FCT散逸フィルターの相互作用として継続課題とする。

## 6. 解像度スケーリングと計算負荷の評価 (Resolution Scaling vs Computational Workload)

同一の物理領域 ($100\mathrm{m} \times 100\mathrm{m}$)、物理時間 ($1.0\mathrm{s}$)、造波周波数 ($f = 0.15\mathrm{Hz}$)、水深条件 (深水 $h=3.5\mathrm{m}$, 浅瀬 $h=0.8\mathrm{m}$) において、格子解像度をスケーリングさせた場合の計算負荷と波長解像度の関係を評価しました：

| 解像度 | 格子間隔 $\Delta x$ | 深水波長解像度 ($h=3.5\mathrm{m}$) | 浅瀬波長解像度 ($h=0.8\mathrm{m}$) | 推奨解像度 ($\ge 32$) | 状態VRAM | CFL許容 $dt$ | 0.016sあたり必要サブステップ | 物理1秒あたり相対総演算量 | PCR共有メモリ負荷 |
| :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **128x128** | 0.781 m | 47.2 cells/wl | 23.6 cells/wl | 浅瀬で未達 (< 32) | 1.75 MB | ~0.040 s | 2 (最低偶数化) | $1.0\times$ (基準) | 最小 (4KB groupshared) |
| **256x256** | 0.391 m | 94.4 cells/wl | 47.2 cells/wl | 達成 ($\ge 32$) | 7.00 MB | ~0.020 s | 2 | $\approx 4.0\times$ | 中 (8KB groupshared) |
| **512x512** (Scene 02) | 0.195 m | 189.0 cells/wl | **94.4 cells/wl** | **十分 ($\gg 32$)** | 28.00 MB | ~0.010 s | 4 | $\approx 32.0\times$ ($256^2$ 比 $\approx 8\times$) | 大 (16KB groupshared, 特化展開必須) |

> [!IMPORTANT]
> 【8倍スケーリング則と Scene 02 浅瀬の解像度実態】
> 1. **スケーリング則**: 格子を縦横2倍にするとセル数は4倍になり、CFL条件維持のため許容時間刻み $dt$ が半分になってサブステップ数が2倍になります。したがって、**物理時間1秒を進める総演算回数は約8倍**に増大します。さらに PCR ソルバーでは行長が2倍になるため共有メモリ占有量と同期ステップ数が増加します。
> 2. **Scene 02 浅瀬の解像度**: Scene 02 の標準設定 ($512\times 512, L=100\mathrm{m}$) では、最も浅い瀬 ($h=0.8\mathrm{m}$) においても主波 ($\lambda \approx 18.45\mathrm{m}$) は **波長あたり約 94.4 セル** を確保しています。これは暫定推奨基準 (32 cells/wl) の約3倍の解像度です。
> 3. **結論・評価方針**: したがって、Scene 02 の浅瀬で生じる波形変形や砕波の挙動を一概に『主波の解像度不足』と帰定することはできません。非線形高調波成分の急峻化、地形勾配による屈折、および FCT 面散逸フィルターの挙動を本体GPUおよび実シーン上で個別に切り分けて検証した上で、正式な対応範囲を決定する必要があります。

## 7. 各モジュールの詳細検証サマリー

各モジュールの詳細な測定コード、生データ、および検証エビデンスは以下の個別レポートに保存されています：
- [`Docs/WaveEquationPrecisionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/WaveEquationPrecisionReport.md)
- [`Docs/ShallowWaterAdvectionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/ShallowWaterAdvectionReport.md)
- [`Docs/BoussinesqDispersionReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/BoussinesqDispersionReport.md)
- [`Docs/FftOceanBenchmarkReport.md`](file:///C:/Users/nakata/Repo/unity-water-simulation/Docs/FftOceanBenchmarkReport.md)

