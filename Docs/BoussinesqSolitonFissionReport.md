# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-11-02 08:05:00 UTC`
- **Git Commit**: `21f36dd` + verifier update
- **Unity Version**: `6000.3.27f2`
- **GPU Device**: `NVIDIA GeForce RTX 5070 (Direct3D11)`
- **Reference Solvers & Data Sources**:
  1. **Direct GPU PCR vs FP64 Linear Inversion & Substep Audit** (`Docs/GpuPcrDirectPrecisionReport.json`, `Packages/com.nobnak.water-simulation/Editor/GpuPcrDirectPrecisionVerifier.cs`): Direct readback from GPU ComputeShader (`CSTridiagonalZ_256`, `CSPredict`, `CSCorrect`, `CSApplyPredictedDispersion`, `CSApplyDispersion`) compared with double-precision Thomas algorithm.
  2. **Independent 1D High-Order Boussinesq Reference Solver** (`Docs/IndependentPeregrineReferenceSolver.py`): IEEE 754 FP64, RK4 time stepping, 4th-order spatial advection, exact discrete tridiagonal solve matching GPU $(I - \mathcal{D})$, strictly zero artificial dissipation.
  3. **NTHMP BP05 Laboratory Flume Experiment** (CHL / USACE flume, open benchmark `Docs/ReferenceData/NTHMP_BP05/`): Solitary wave shoaling on composite beach (Cases A and B).

---

## 1. 総合暫定結論

### 判定ステータス: **【未確定（Inconclusive）】**

> **【暫定結論】**
> 対象条件でGPU PCRとFP64線形解は良好に一致した。一方、時間発展の予測段には境界条件の不一致があり、全段一致と長時間の物理的妥当性は未確認。
> 
> ※ 以下の断定はすべて撤回・保留を継続します：
> - 現在版が物理的に正しいことを確認した。
> - 初期版の分裂は数値振動と確定した。
> - 物理的分裂を阻害していないことを確認した。
> - 未解像が主因なので現行係数の妥当性が実証された。

---

## 2. 同じ線形問題でのGPU PCRとFP64の直接比較

実機GPU（NVIDIA GeForce RTX 5070）上で実際のComputeShaderカーネル（`CSTridiagonalZ_256`）を実行し、`AsyncGPUReadback` により取得した解ベクトルを、同一の格子（$N=256$, $\Delta z=0.196$ m）、地形、右辺外力、および境界条件で解いたFP64（倍精度Thomasアルゴリズム）の直接解と比較検証しました（`Docs/GpuPcrDirectPrecisionReport.json`）。

### 2.1 境界条件の厳密な区別
準1D伝播（Z方向）において、境界行は次のように厳密に分離されています：
- **法線方向成分（$a_z$, float2のY成分）**: 境界 $j=0$ および $j=H-1$ において厳密な **Dirichlet 境界条件 $a_z = 0$**（$b=1.0, c=0.0, d=0.0$）。1D波動の物理流速 $w$ の加速度を支配。
- **接線方向成分（$a_x$, float2のX成分）**: 境界 $j=0$ および $j=H-1$ において **Neumann 境界条件 $\partial a_x / \partial z = 0$**（$b=1+2\beta, c=-2\beta, d=f_x$）。

### 2.2 直接比較測定結果（GPU PCR vs FP64）
合否判定は、法線成分（最大差 $\le 10^{-5}$ または相対差 $\le 10^{-4}$）、接線成分（最大差 $\le 10^{-5}$ または相対差 $\le 10^{-4}$）、および行列残差（$\| \mathbf{M} a - f \|_\infty \le 10^{-4}$）のすべてを満たすことを条件として動的に評価しています。

| 地形ケース | 外力パターン | 法線最大差 $\max \|a_z^{\text{GPU}} - a_z^{\text{FP64}}\|$ | 法線相対差 | 行列残差ノルム $\| \mathbf{M} a^{\text{GPU}} - f \|_\infty$ | 水深格納誤差 [m] | 水深丸め加速度差 [m/s$^2$] | 判定 |
|---|---|---|---|---|---|---|---|
| **平坦床 ($h=0.60$m)** | 正弦波 ($\sin(2\pi z/L)$) | **$1.83 \times 10^{-7}$** m/s$^2$ | $1.84 \times 10^{-7}$ | $1.18 \times 10^{-6}$ | $3.91 \times 10^{-4}$ m | $2.46 \times 10^{-6}$ m/s$^2$ | **PASS** |
| **平坦床 ($h=0.60$m)** | 局所パルス ($\exp(-\Delta z^2)$) | **$1.90 \times 10^{-7}$** m/s$^2$ | $1.92 \times 10^{-7}$ | $9.91 \times 10^{-7}$ | $3.91 \times 10^{-4}$ m | $1.18 \times 10^{-5}$ m/s$^2$ | **PASS** |
| **直線斜面 ($0.60 \to 0.20$m)** | 正弦波 | **$7.07 \times 10^{-7}$** m/s$^2$ | $7.07 \times 10^{-7}$ | $1.91 \times 10^{-6}$ | $4.88 \times 10^{-4}$ m | $4.75 \times 10^{-4}$ m/s$^2$ | **PASS** |
| **直線斜面 ($0.60 \to 0.20$m)** | 局所パルス | **$4.46 \times 10^{-7}$** m/s$^2$ | $4.48 \times 10^{-7}$ | $1.30 \times 10^{-6}$ | $4.88 \times 10^{-4}$ m | $2.75 \times 10^{-4}$ m/s$^2$ | **PASS** |
| **滑らかな曲率地形 ($h_{zz} \ne 0$)** | 正弦波 | **$5.22 \times 10^{-7}$** m/s$^2$ | $5.22 \times 10^{-7}$ | $2.16 \times 10^{-6}$ | $4.87 \times 10^{-4}$ m | $6.51 \times 10^{-4}$ m/s$^2$ | **PASS** |
| **滑らかな曲率地形 ($h_{zz} \ne 0$)** | 局所パルス | **$5.60 \times 10^{-7}$** m/s$^2$ | $5.63 \times 10^{-7}$ | $1.26 \times 10^{-6}$ | $4.87 \times 10^{-4}$ m | $3.85 \times 10^{-4}$ m/s$^2$ | **PASS** |
| **棚状地形 (折れ点 $z=10, 18$m)** | 正弦波 | **$5.81 \times 10^{-7}$** m/s$^2$ | $5.81 \times 10^{-7}$ | $1.96 \times 10^{-6}$ | $3.91 \times 10^{-4}$ m | $2.86 \times 10^{-4}$ m/s$^2$ | **PASS** |
| **棚状地形 (折れ点 $z=10, 18$m)** | 局所パルス | **$3.63 \times 10^{-7}$** m/s$^2$ | $3.63 \times 10^{-7}$ | $5.20 \times 10^{-7}$ | $3.91 \times 10^{-4}$ m | $1.96 \times 10^{-5}$ m/s$^2$ | **PASS** |

### 2.3 Half格納影響の分離と検証範囲の限定
1. **水深格納誤差と加速度差の区別**:
   - `halfStorageDepthRoundingDiff` [m]: 16ビット Half（`RGHalf`）による水深値自体の丸め誤差であり、全ケースで $3.91 \times 10^{-4} \sim 4.88 \times 10^{-4}$ m です。
   - `halfStorageAccelRoundingDiff` [m/s$^2$]: この水深丸めによって生じる分散反転加速度の変動であり、斜面・曲率・棚で $2.46 \times 10^{-6} \sim 6.51 \times 10^{-4}$ m/s$^2$ です。PCRアルゴリズムの残差（$10^{-6}$）より大きく、テクスチャ量子化に起因します。
2. **検証範囲の限定**:
   - 今回確認できたのは $N=256$ の特定線形問題における PCR 反転精度（単精度限界）です。
   - この合格結果をもって、他解像度、時間発展全体、あるいは物理的妥当性全般が保証されたと拡大解釈しません。

---

## 3. 時間発展の段階的照合と境界処理の厳密一致

### 3.1 予測段境界処理の不一致の解明と訂正
- **要因の解明**:
  - 先の報告で測定された予測段外力差 $1.08 \times 10^{-2}$ m/s$^2$ および流速差 $3.33 \times 10^{-3}$ m/s は、**CPU参照ソルバー側のみが両端（$j=0, H-1$）で `fzPred` と `wPred` を人工的にゼロに固定していたため**に生じたものです。
  - 孤立波初期条件の左端流速は $w_0[0] \approx 0.00332896$ m/s であり、以前の流速差 $0.00333$ m/s と完全に一致していました。
  - したがって、この差を「単精度丸めや風上分岐の影響」とする説明は誤りであり撤回します。
- **GPU本体の挙動**:
  - GPUの `CSPredict` および `CSApplyPredictedDispersion` では境界流速 $w^*$ のゼロクランプは行われません（$w^*$ は $w_N + \Delta t \cdot a_z^*$ として計算され、端点流速が保持されます）。
  - 不透過壁条件 $w=0$ の強制クランプは、サブステップ最終段の `CSApplyDispersion` でのみ実行されます。
  - CPU参照側をGPUと同一のタイミング・条件（予測段で端点をゼロ化せず、PCRのDirichlet $a_z=0$ のみ適用し、最終段で端点流速をゼロ化）に修正した結果、予測段の不一致は解消されました。

### 3.2 フェーズ別許容値と動的合否判定基準
各物理量に応じた絶対許容値および相対許容値を設定し、全域（境界含む）と内部領域（$1 \le j \le H-2$）の双方で合否を判定：
- 水位 $\eta$ [m]: `absTol = 1.0e-4` m, `relTol = 1.0e-3`
- 外力 $f_z$ [m/s$^2$]: `absTol = 1.0e-3` m/s$^2$, `relTol = 1.0e-3`
- 加速度 $a_z$ [m/s$^2$]: `absTol = 1.0e-4` m/s$^2$, `relTol = 1.0e-3`
- 流速 $w$ [m/s]: `absTol = 1.0e-4` m/s, `relTol = 1.0e-3`

### 3.3 段階的検証シナリオの測定結果

#### シナリオ 1: 第1サブステップ（Parity 0, 先頭波 $z_0=5$m）
| フェーズ | 変数 | 単位 | 全域最大差 (Idx, 境界?) | 内部最大差 (Idx) | 相対差 | GPU値 | CPU値 | 判定 |
|---|---|---|---|---|---|---|---|---|
| **Phase 1: 予測段水面** | $\eta^*$ | m | $4.281 \times 10^{-9}$ (26, no) | $4.281 \times 10^{-9}$ (26) | $3.576 \times 10^{-8}$ | $0.1197$ | $0.1197$ | **PASS** |
| **Phase 2: 予測段外力** | $f_z^*$ | m/s$^2$ | $3.844 \times 10^{-8}$ (29, no) | $3.844 \times 10^{-8}$ (29) | $5.949 \times 10^{-8}$ | $0.6119$ | $0.6119$ | **PASS** |
| **Phase 3: 予測段反転加速度** | $a_z^*$ | m/s$^2$ | $1.118 \times 10^{-7}$ (31, no) | $1.118 \times 10^{-7}$ (31) | $2.050 \times 10^{-7}$ | $0.5454$ | $0.5454$ | **PASS** |
| **Phase 4: 予測段流速** | $w^*$ | m/s | $1.446 \times 10^{-8}$ (21, no) | $1.446 \times 10^{-8}$ (21) | $3.272 \times 10^{-8}$ | $0.3385$ | $0.3385$ | **PASS** |
| **Phase 5: 修正段水面** | $\eta^1$ | m | $3.990 \times 10^{-9}$ (24, no) | $3.990 \times 10^{-9}$ (24) | $3.335 \times 10^{-8}$ | $0.1155$ | $0.1155$ | **PASS** |
| **Phase 6: 修正段外力** | $f_z^{**}$ | m/s$^2$ | $3.515 \times 10^{-7}$ (27, no) | $3.515 \times 10^{-7}$ (27) | $5.382 \times 10^{-7}$ | $0.2026$ | $0.2026$ | **PASS** |
| **Phase 7: 修正段反転加速度** | $a_z^{**}$ | m/s$^2$ | $1.697 \times 10^{-7}$ (22, no) | $1.697 \times 10^{-7}$ (22) | $3.088 \times 10^{-7}$ | $-0.5074$ | $-0.5074$ | **PASS** |
| **Phase 8: 最終更新流速** | $w^1$ | m/s | $1.369 \times 10^{-8}$ (27, no) | $1.369 \times 10^{-8}$ (27) | $3.100 \times 10^{-8}$ | $0.4308$ | $0.4308$ | **PASS** |

#### シナリオ 2: 逆Parity試験（Parity 1, 差分方向反転）
- 全8フェーズで単精度限界の一致を確認（水面最大差 $4.43 \times 10^{-9}$ m, 外力最大差 $3.27 \times 10^{-7}$ m/s$^2$, 流速最大差 $1.49 \times 10^{-8}$ m/s）: **【全フェーズ PASS】**

#### シナリオ 3: 境界近傍波試験（$z_0=0.8$m, 境界 $j=0$ に有意振幅が存在する条件）
- 境界 $j=0$ において $\eta_0[0] \approx 0.092$ m, $w_0[0] \approx 0.35$ m/s の状態で実行。
- 境界セル $j=0$ での差分: 水面差 $3.60 \times 10^{-9}$ m, 最終流速差 $1.36 \times 10^{-8}$ m/s。境界励起下でもGPUとFP64は整合: **【全フェーズ PASS】**

#### シナリオ 4: 複数サブステップ累積試験（5連続サブステップ, Parity交互切替）
| サブステップ | Parity | 水位最大差 $\max |\Delta \eta|$ [m] | 水位RMS差 [m] | 流速最大差 $\max |\Delta w|$ [m/s] | 流速RMS差 [m/s] | 判定 |
|---|---|---|---|---|---|---|
| **Step 1** | 0 | $3.990 \times 10^{-9}$ | $6.904 \times 10^{-10}$ | $1.369 \times 10^{-8}$ | $2.335 \times 10^{-9}$ | **PASS** |
| **Step 2** | 1 | $1.253 \times 10^{-8}$ | $1.197 \times 10^{-9}$ | $2.531 \times 10^{-8}$ | $4.130 \times 10^{-9}$ | **PASS** |
| **Step 3** | 0 | $1.160 \times 10^{-8}$ | $1.276 \times 10^{-9}$ | $3.394 \times 10^{-8}$ | $4.997 \times 10^{-9}$ | **PASS** |
| **Step 4** | 1 | $1.149 \times 10^{-8}$ | $1.386 \times 10^{-9}$ | $4.050 \times 10^{-8}$ | $6.212 \times 10^{-9}$ | **PASS** |
| **Step 5** | 0 | $1.697 \times 10^{-8}$ | $1.796 \times 10^{-9}$ | $4.351 \times 10^{-8}$ | $5.981 \times 10^{-9}$ | **PASS** |

---

## 4. 対流方式の表記と多面的評価基準

### 4.1 差分方式の表記訂正
- `maccormack_directional` は「中心差分」ではありません。
- **「予測段と修正段で逆向きの片側差分を使う方式（Alternating Forward/Backward Directional One-Sided Difference）」** と記述します。
- **精度の区別**:
  - 各段単体では $O(\Delta z)$ の片側差分ですが、予測段（前進）と修正段（後退）を合成した2段Heun型時間積分全体として対流項の数値粘性が打ち消され、時間・空間2次精度 $O(\Delta t^2, \Delta z^2)$ を達成します。
  - 現行GPUの「1次風上移流」は予測・修正の両段で風上片側差分を固定適用するため、2段合成後も数値粘性 $\nu_{\text{num}} = \frac{1}{2} |w| \Delta z$ が残り1次精度に退化します。

### 4.2 比較判定基準（波高単独評価の廃止）
- 先頭波高の増加（$0.0943$ m $\to 0.2172$ m, $+130\%$）のみをもって優位と判定しません。
- 以下の全項目を評価軸とします：
  1. **波形全体の整合と位相**: 先頭波位置（$z=36.08$ m vs 高次解 $z=36.52$ m）
  2. **高周波振動（粗さ）**: 逆向き片側差分（粗さ $7.60 \times 10^{-4}$）vs 風上移流（粗さ $3.00 \times 10^{-4}$）
  3. **質量・体積保存性**: 誤差 $1.15 \times 10^{-5}$（保存型流束）
  4. **格子・時間収束性**: 格子細分化による波形と散逸の安定性

---

## 5. NTHMP BP05 水槽実験ベンチマークの状況保持

- **Case A（微小振幅非砕波 $a/h \approx 0.04$）: 【PASS】**
  - g7波高誤差: FCT $N512 = +5.6\%$, FilterOff $N512 = +6.1\%$（許容10%以内）
  - g5→g7相対到達時刻: FCT $N512 = -0.10$ s（許容0.15s以内）
  - 特記事項: 方式間差は $\le 0.5\%$ であり、Case Aは散逸スキーム間の差異を識別できません。
- **Case B（中振幅非砕波浅水化 $a/h \approx 0.26$）: 【FAIL】**
  - **g7波高誤差: FCT $N512 = -27.5\%$, FilterOff $N512 = -22.2\%$, Legacy2D = -24.6% (FAIL)**
  - FCT $N256$ 波高誤差: $-49.2\%$ (FAIL)
  - 特記事項: 実測では波高が g5 (5.31cm) から g7 (7.05cm) へ $+33\%$ 急成長するのに対し、GPU計算では $+3 \sim +8\%$ しか成長せず、重大な波高過小評価が発生しています。
