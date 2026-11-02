# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-11-02 07:50:00 UTC`
- **Git Commit**: `402a77c`
- **Unity Version**: `6000.3.27f2`
- **GPU Device**: `NVIDIA GeForce RTX 5070 (Direct3D11)`
- **Reference Solvers & Data Sources**:
  1. **Direct GPU PCR vs FP64 Linear Inversion & Substep Audit** (`Docs/GpuPcrDirectPrecisionReport.json`, `Packages/com.nobnak.water-simulation/Editor/GpuPcrDirectPrecisionVerifier.cs`): Direct readback from GPU ComputeShader (`CSTridiagonalZ_256`, `CSPredict`, `CSCorrect`) compared with double-precision Thomas algorithm.
  2. **Independent 1D High-Order Boussinesq Reference Solver** (`Docs/IndependentPeregrineReferenceSolver.py`): IEEE 754 FP64, RK4 time stepping, 4th-order spatial advection, exact discrete tridiagonal solve matching GPU $(I - \mathcal{D})$, strictly zero artificial dissipation.
  3. **NTHMP BP05 Laboratory Flume Experiment** (CHL / USACE flume, open benchmark `Docs/ReferenceData/NTHMP_BP05/`): Solitary wave shoaling on composite beach (Cases A and B).

---

## 1. 総合暫定結論

### 判定ステータス: **【未確定（Inconclusive）】**

> **【暫定結論】**
> 係数式は整合させ、GPU PCRとFP64線形解の直接一致（$10^{-7}$精度）および1ステップ時間発展の整合を確認したが、長時間の波高不足（BP05 Case Bの$-27.5\%$ FAIL等）における対流・散逸・格子未解像の主因は未確定。
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
| 地形ケース | 外力パターン | 法線最大差 $\max \|a_z^{\text{GPU}} - a_z^{\text{FP64}}\|$ | 法線相対差 | 行列残差ノルム $\| \mathbf{M} a^{\text{GPU}} - f \|_\infty$ | Half水深丸め誤差 | 判定 |
|---|---|---|---|---|---|---|
| **平坦床 ($h=0.60$m)** | 正弦波 ($\sin(2\pi z/L)$) | **$1.83 \times 10^{-7}$** m/s$^2$ | $1.84 \times 10^{-7}$ | $1.18 \times 10^{-6}$ | $2.46 \times 10^{-6}$ m | **PASS** |
| **平坦床 ($h=0.60$m)** | 局所パルス ($\exp(-\Delta z^2)$) | **$1.90 \times 10^{-7}$** m/s$^2$ | $1.92 \times 10^{-7}$ | $9.91 \times 10^{-7}$ | $1.18 \times 10^{-5}$ m | **PASS** |
| **直線斜面 ($0.60 \to 0.20$m)** | 正弦波 | **$7.07 \times 10^{-7}$** m/s$^2$ | $7.07 \times 10^{-7}$ | $1.91 \times 10^{-6}$ | $4.75 \times 10^{-4}$ m | **PASS** |
| **直線斜面 ($0.60 \to 0.20$m)** | 局所パルス | **$4.46 \times 10^{-7}$** m/s$^2$ | $4.48 \times 10^{-7}$ | $1.30 \times 10^{-6}$ | $2.75 \times 10^{-4}$ m | **PASS** |
| **滑らかな曲率地形 ($h_{zz} \ne 0$)** | 正弦波 | **$5.22 \times 10^{-7}$** m/s$^2$ | $5.22 \times 10^{-7}$ | $2.16 \times 10^{-6}$ | $6.51 \times 10^{-4}$ m | **PASS** |
| **滑らかな曲率地形 ($h_{zz} \ne 0$)** | 局所パルス | **$5.60 \times 10^{-7}$** m/s$^2$ | $5.63 \times 10^{-7}$ | $1.26 \times 10^{-6}$ | $3.85 \times 10^{-4}$ m | **PASS** |
| **棚状地形 (折れ点 $z=10, 18$m)** | 正弦波 | **$5.81 \times 10^{-7}$** m/s$^2$ | $5.81 \times 10^{-7}$ | $1.96 \times 10^{-6}$ | $2.86 \times 10^{-4}$ m | **PASS** |
| **棚状地形 (折れ点 $z=10, 18$m)** | 局所パルス | **$3.63 \times 10^{-7}$** m/s$^2$ | $3.63 \times 10^{-7}$ | $5.20 \times 10^{-7}$ | $1.96 \times 10^{-5}$ m | **PASS** |

### 2.3 事実と考察
1. **GPU PCR解の精度**:
   平坦床、直線斜面、滑らかな曲率地形、折れ点を含む棚状地形の**すべてにおいて、GPU PCRの解はFP64参照解と $1.8 \times 10^{-7} \sim 7.1 \times 10^{-7}$（単精度浮動小数点数の機械限界 $1.19 \times 10^{-7}$ 付近）で一致**し、相対残差も $10^{-6}$ オーダーに収まっています。曲率や折れ点によるPCRアルゴリズム自体の数値的乖離は生じていません。
2. **Half水深格納の影響**:
   水深テクスチャを16ビットHalf（`RGHalf`）に格納することによる丸め誤差（$\Delta h \approx 10^{-4}$ m）は、斜面・曲率・棚で $2.8 \times 10^{-4} \sim 6.5 \times 10^{-4}$ の解の変動を生じさせます。これはGPU PCR自体の解法誤差（$\approx 10^{-7}$）より約3桁大きく、水深表現の主たる数値誤差要因であることが確認されました。

---

## 3. 時間発展の段階的照合（Substep 1 の各Phase）

FilterOff・同一の1次風上移流方式下で、第1サブステップ（$\Delta t = 0.004$ s）の内部フェーズをGPUから逐次readbackし、FP64 CPU参照ソルバーと段階的に照合しました。

### 3.1 フェーズ別差分監査結果
| フェーズ | 変数 | 最大差分 | RMS差分 | 相対差分 | 観察事項 |
|---|---|---|---|---|---|
| **Phase 1: 予測段水面** | $\eta^*$ | $4.28 \times 10^{-9}$ m | $5.43 \times 10^{-10}$ m | $3.58 \times 10^{-8}$ | 単精度限界で完全一致 |
| **Phase 2: 予測段外力** | $f_z^*$ | $1.08 \times 10^{-2}$ m/s$^2$ | $6.75 \times 10^{-4}$ m/s$^2$ | $1.67 \times 10^{-2}$ | 単精度丸めと風上分岐による局所差 |
| **Phase 3: 予測段反転加速度** | $a_z^*$ | $1.12 \times 10^{-7}$ m/s$^2$ | $1.97 \times 10^{-8}$ m/s$^2$ | $2.05 \times 10^{-7}$ | GPU PCR反転精度は極めて良好 |
| **Phase 4: 予測段流速** | $w^*$ | $3.33 \times 10^{-3}$ m/s | $2.08 \times 10^{-4}$ m/s | $7.53 \times 10^{-3}$ | 予測段流速更新の一致 |
| **Phase 5: 修正段水面** | $\eta^1$ | $2.04 \times 10^{-5}$ m | $1.27 \times 10^{-6}$ m | $1.70 \times 10^{-4}$ | 水面更新差分は $O(10^{-5}$m$)$ |
| **Phase 6: 修正段外力** | $f_z^{**}$ | $7.21 \times 10^{-5}$ m/s$^2$ | $4.50 \times 10^{-6}$ m/s$^2$ | $1.10 \times 10^{-4}$ | 外力差分も微小 |
| **Phase 7: 修正段反転加速度** | $a_z^{**}$ | $1.32 \times 10^{-5}$ m/s$^2$ | $1.01 \times 10^{-6}$ m/s$^2$ | $2.41 \times 10^{-5}$ | 反転加速度の整合 |
| **Phase 8: 最終更新流速** | $w^1$ | **$2.66 \times 10^{-8}$ m/s** | $3.08 \times 10^{-9}$ m/s | $6.02 \times 10^{-8}$ | **最終流速は単精度限界で一致** |

- **`SmoothTotalDepth` の影響**:
  $C^1$ 平滑化水深（$0.05 + 0.5(d + \sqrt{d^2 + 0.0004})$）と生の全水深（$h + \eta$）の差による第1ステップの水面変化量は $\max |\eta_{\text{smooth}} - \eta_{\text{raw}}| = 1.17 \times 10^{-7}$ m であり、非砕波浅水化の初期段階では無視できる水準です。

---

## 4. 対流方式の表記と判定基準の改訂

### 4.1 差分方式の表記訂正
- `maccormack_directional` は「中心差分」ではありません。
- **「予測段と修正段で逆向きの片側差分を使う方式（Alternating Forward/Backward Directional One-Sided Difference）」** と記述を改訂します。
- **精度の区別**:
  - 各段単体（予測段または修正段）では $O(\Delta z)$ の1次精度片側差分です。
  - 予測段（前進）と修正段（後退）を合成した2段Heun型時間積分全体として、対流項の数値粘性が打ち消され、時間・空間2次精度 $O(\Delta t^2, \Delta z^2)$ を達成します。
  - 一方、現行GPUの「1次風上移流」は予測段・修正段の双方で風上方向の片側差分を固定適用するため、2段合成後も数値粘性 $\nu_{\text{num}} = \frac{1}{2} |w| \Delta z$ が残り、1次精度に退化します。

### 4.2 比較判定基準（波高単独評価の廃止）
- 先頭波高の増加（$0.0943$ m $\to 0.2172$ m, $+130\%$）のみをもって優位と判定しません。
- 以下の全項目を評価軸とします：
  1. **波形全体の整合と位相**: 先頭波位置（$z=36.08$ m vs 高次解 $z=36.52$ m）
  2. **高周波振動（粗さ）**: 逆向き片側差分（粗さ $7.60 \times 10^{-4}$）vs 風上移流（粗さ $3.00 \times 10^{-4}$）。急勾配での数値振動耐性。
  3. **質量・体積保存性**: 誤差 $1.15 \times 10^{-5}$（保存型流束）
  4. **格子・時間収束性**: 格子細分化による波形と散逸の安定性。
- 本体（Runtime）への採用判断は、これら全波形比較と安定性検証が完了した後に実施します。

---

## 5. NTHMP BP05 水槽実験ベンチマークの状況保持

- **Case A（微小振幅非砕波 $a/h \approx 0.04$）: 【PASS】**
  - g7波高誤差: FCT $N512 = +5.6\%$, FilterOff $N512 = +6.1\%$（許容10%以内）
  - g5→g7相対到達時刻: FCT $N512 = -0.10$ s（許容0.15s以内）
  - 特記事項: 方式間（FCT, FilterOff, Legacy2D）の波高差は $\le 0.5\%$ であり、Case Aは散逸スキーム間の差異を識別できません。
- **Case B（中振幅非砕波浅水化 $a/h \approx 0.26$）: 【FAIL】**
  - **g7波高誤差: FCT $N512 = -27.5\%$, FilterOff $N512 = -22.2\%$, Legacy2D = -24.6% (FAIL)**
  - FCT $N256$ 波高誤差: $-49.2\%$ (FAIL)
  - 特記事項: 実測では波高が g5 (5.31cm) から g7 (7.05cm) へ $+33\%$ 急成長するのに対し、GPU計算では $+3 \sim +8\%$ しか成長せず、重大な波高過小評価（振幅欠損）が発生しています。
- **到達時刻判定の制約**:
  - g5を時間合わせ原点としているため、評価されているのは斜面上での相対伝播速度であり、造波板からの絶対到達時刻の独立検証ではありません。
