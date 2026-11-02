# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-11-02 07:35:00 UTC`
- **Git Commit**: `9d85a12`
- **Unity Version**: `6000.3.27f2`
- **GPU Device**: `NVIDIA GeForce RTX 5070 (Direct3D11)`
- **Reference Solvers & Data Sources**:
  1. **NTHMP BP05 Laboratory Flume Experiment** (CHL / USACE flume, open benchmark `Docs/ReferenceData/NTHMP_BP05/`): Solitary wave shoaling on composite beach (Cases A and B).
  2. **Independent 1D High-Order Boussinesq Reference Solver** (`Docs/IndependentPeregrineReferenceSolver.py`): IEEE 754 FP64, RK4 time stepping, 4th-order spatial advection, exact discrete tridiagonal solve matching GPU $(I - \mathcal{D})$, strictly zero artificial dissipation.
  3. **Discrete Dispersion Operator Verification Suite** (`Docs/VerifyDiscreteDispersionOperator.py`): Operator action analysis on flat bed, linear slope, curved bed ($h_{zz} \ne 0$), and piecewise linear shelf.

---

## 1. 総合暫定結論

### 判定ステータス: **【未確定（Inconclusive）】**

> **【暫定結論】**
> 初期条件の式と報告の整合性は改善したが、分散演算子の不一致が残り、波高不足の主因は未確定。
> 
> ※ 以下の断定はすべて撤回・保留を継続します：
> - 現在版が物理的に正しいことを確認した。
> - 初期版の分裂は数値振動と確定した。
> - 物理的分裂を阻害していないことを確認した。
> - 未解像が主因なので現行係数の妥当性が実証された。

---

## 2. 分散演算子の導出と離散演算子の等価性検証

### 2.1 一次資料（Peregrine 1967）との対応と数学的導出
Peregrine (1967) eq (2.11) における水深平均流速 $u^*$ の分散項は次式で与えられます：
$$\mathcal{D}(u^*) = \frac{1}{2} h \frac{\partial^3}{\partial x^2 \partial t}(h u^*) - \frac{1}{6} h^2 \frac{\partial^3 u^*}{\partial x^2 \partial t}$$

時間微分変数 $\psi = \partial u^* / \partial t$ で表すと：
$$\mathcal{D}(\psi) = \frac{1}{2} h (h \psi)_{xx} - \frac{1}{6} h^2 \psi_{xx}$$

滑らかな水深変化に対して積の微分法則を展開すると：
$$(h \psi)_{xx} = h \psi_{xx} + 2 h_x \psi_x + h_{xx} \psi$$
$$\mathcal{D}(\psi) = \frac{1}{2} h (h \psi_{xx} + 2 h_x \psi_x + h_{xx} \psi) - \frac{1}{6} h^2 \psi_{xx} = \frac{1}{3} h^2 \psi_{xx} + 1.0 h h_x \psi_x + 0.5 h h_{xx} \psi$$

- **係数の訂正と撤回**:
  - 水深平均流速の分散展開において、地形勾配項 $h h_x \psi_x$ の係数は **$1.0$**、地形曲率項 $h h_{xx} \psi$ の係数は **$0.5$** です。
  - 先の報告で「勾配係数0.5が古典的Peregrine」とした説明および比較は誤解に基づくものであり、**正式に撤回**します。
  - GPUの三重対角係数（`RunTridiagonalZ`）は次式を直接2階有限差分したものです：
    $$A_j = -\frac{\alpha}{\Delta z^2} (0.5 h_j h_{j-1} - \frac{1}{6} h_j^2), \quad B_j = 1 + \frac{\alpha}{\Delta z^2} (\frac{2}{3} h_j^2), \quad C_j = -\frac{\alpha}{\Delta z^2} (0.5 h_j h_{j+1} - \frac{1}{6} h_j^2)$$
    これは $\psi - \alpha [ \frac{1}{2} h \delta_z^2(h \psi) - \frac{1}{6} h^2 \delta_z^2 \psi ]$ の厳密な離散表現であり、勾配項 $1.0 h h_z \psi_z$ と曲率項 $0.5 h h_{zz} \psi$ の双方を内包しています。

### 2.2 離散演算子の等価性検証結果（`VerifyDiscreteDispersionOperator.py`）
GPUの三重対角係数と同じ離散行列 $\mathbf{M}$ をPython側で構築し、各テスト入力に対する作用結果を検証しました：
1. **平坦床（$h = 0.60$ m 一定）**:
   - 定数入力 $\psi = 1$: $\mathbf{M}\mathbf{1} - \mathbf{1} = 0$（残差 $< 10^{-14}$）
   - 正弦波入力 $\psi = \sin(kz)$: 理論値 $-\frac{1}{3} h^2 \psi_{zz}$ との差分残差 $O(\Delta z^2)$
2. **線形斜面（$h_z = -0.05$, $h_{zz} = 0$）**:
   - 定数入力 $\psi = 1$: $\mathbf{M}\mathbf{1} - \mathbf{1} = 0$（残差 $< 10^{-14}$）
   - 線形入力 $\psi = z$: 作用値が理論値 $-h h_z \cdot 1$ と完全一致（勾配係数 $1.0$ を確認）
3. **曲率地形（$h(z) = h_0 + \delta h \sin(kz)$, $h_{zz} \ne 0$）**:
   - 定数入力 $\psi = 1$:
     - 離散作用値 $\mathbf{M}\mathbf{1} - \mathbf{1}$ と理論曲率項 $-0.5 h h_{zz}$ の差: **$9.55 \times 10^{-8}$**
     - $\Delta z^2$ に対する比: **$0.0000$**
     - これにより、GPU演算子内に曲率項 $\frac{1}{2} h h_{zz} \psi$ が厳密に含まれていることを確認。
4. **棚状折れ点（$z=10$ m, $18$ m）**:
   - 滑らかな展開式では折れ点で $h_z$ が不連続、$h_{zz}$ が特異（Dirac delta）となりますが、3点差分流束形式 $\delta_z^2(h \psi)$ は折れ点を跨いで保存的に連続作用し、特異点なく安定に反転されることを確認。

---

## 3. 対流移流項・散逸要因の厳密な孤立検証

多変数の格子細分化実験から「風上移流が主因」と断定することを保留し、格子（$N=512$, $\Delta z=0.0977$ m）、時間刻み（$\Delta t=0.004$ s）、連続の式、厳密離散分散反転（$A_j, B_j, C_j$）、および地形を**完全に同一に固定**した上で、移流項・散逸項のみを孤立させた比較を実施しました（`Docs/DiagnoseShoalingHeightDifference.py`）。

### 3.1 要因分離実験結果（$t = 14.0$ s）
| 条件 / 設定 | 対流移流方式 | 空間フィルター | 先頭位置 $z$ | 先頭波高 $H$ | 質量保存誤差 | 2グリッド粗さ |
|---|---|---|---|---|---|---|
| **Case 1 (MacCormack標準)** | 2次方向中心差分 | なし | $36.08$ m | **$0.2172$ m** | $1.15 \times 10^{-5}$ | $7.60 \times 10^{-4}$ |
| **Case 2 (GPU移流方式)** | 1次風上移流 | なし | $34.33$ m | **$0.0943$ m** | $1.15 \times 10^{-5}$ | $3.00 \times 10^{-4}$ |
| **Case 3 (GPU移流 + 散逸)** | 1次風上移流 | 非制限4階散逸 | $34.33$ m | **$0.0837$ m** | $6.23 \times 10^{-3}$ | $1.26 \times 10^{-4}$ |
| **Case 4 (線形運動量参照)** | 移流項なし ($w w_z = 0$) | なし | $34.03$ m | **$0.2488$ m** | $1.15 \times 10^{-5}$ | $7.42 \times 10^{-4}$ |

### 3.2 観察事実と要因の位置付け
1. **対流移流項の孤立差**:
   - 2次中心MacCormackと1次風上移流の比較（Case 1 vs Case 2）において、先頭波高は **$+0.1229$ m ($+130.2\%$)** 増加し、先頭位置は $1.75$ m 前進して高次参照解（$z=36.52$ m）に大きく接近しました。
   - 1次風上移流による数値拡散 $\nu_{\text{num}} = \frac{1}{2} |w| \Delta z$ が波高抑制と位相遅れの非常に大きな要因であることを示しています。
   - ただし、時間積分方式（2段MacCormack vs 4段RK4）や斜面不連続点での多変数相互作用が残るため、**「主因」ではなく「有力候補（Leading Candidate）」**と位置付けます。
2. **空間散逸フィルターの孤立差**:
   - 1次風上移流下での非制限4階散逸の有無（Case 2 vs Case 3）による波高差は **$0.0106$ m ($11.3\%$)** です。
   - ※ Python側の本処理はGPUの流束制限付きFCTとは異なり「非制限4階散逸（高周波フィルター）」です。GPU自体のFCT過剰散逸評価は、GPUの `CurrentFCT_Full` vs `FilterOff_RawMacCormack` 実測値（$0.0832$ m vs $0.0947$ m、差 $0.0115$ m）に基づきます。

---

## 4. 独立ソルバーの生データ・ピーク抽出事実

### 4.1 平坦水深対照実験（$h = 0.60$ m, $a_0 = 0.12$ m, $t = 14.0$ s）
- **速度の区別**:
  - 理論孤立波速度: $c_{\text{th}} = \sqrt{g(h + a_0)} = 2.6577$ m/s
  - 独立ソルバー測定速度: $c_{\text{meas}} = 2.6487$ m/s (理論比 $-0.34\%$)
  - GPU CurrentFCT ($N=256$) 測定速度: $2.6179$ m/s (理論比 $-1.50\%$)
  - GPU FilterOff ($N=512$) 測定速度: $2.6380$ m/s (理論比 $-0.74\%$)
- **ピーク抽出基準と実波形**:
  - プロミネンス基準 $p \ge 0.005$ m では、独立ソルバーは主波（$z = 42.08$ m, 高さ $0.1211$ m, プロミネンス $0.1210$ m）の**1峰のみ**が検出されます。
  - プロミネンス基準なしの極小閾値（$0.001$ m）では、主波の後方に波高 $0.0015 \sim 0.002$ m の微小な分散尾部リップルが存在します。
  - GPU FilterOff ($N=512$) では平坦床にもかかわらず振幅 $0.01 \sim 0.03$ m の鋸歯状数値振動が5峰自発成長しました。

### 4.2 棚地形ベンチマーク（$h_1=0.60$ m $\to h_2=0.20$ m, 斜面 1:20, $t=14.0$ s）
- **独立ソルバー（$N=2048$, 人工散逸なし, 完全一致離散分散行列）**:
  - 有意峰数（プロミネンス $p \ge 0.005$ m, $z \ge 18$ m）: **4峰**（段波状に連なる未分離の波状段波）
    - 第1峰: $z = 36.52$ m, 高さ $0.2904$ m, プロミネンス $0.2904$ m
    - 第2峰: $z = 33.44$ m, 高さ $0.1424$ m, プロミネンス $0.1423$ m
    - 第3峰: $z = 31.31$ m, 高さ $0.0687$ m, プロミネンス $0.0658$ m
    - 第4峰: $z = 29.50$ m, 高さ $0.0260$ m, プロミネンス $0.0151$ m
- **GPU CurrentFCT**:
  - $N=256$: 先頭波 $z = 33.79$ m, 高さ $0.0703$ m（1峰のみ存続、第2峰は平滑化）
  - $N=512$: 先頭波 $z = 34.33$ m, 高さ $0.0832$ m, 第2峰 $z = 32.64$ m, 高さ $0.0552$ m, プロミネンス $0.0161$ m（2峰検出）
- **GPU FilterOff**:
  - $N=256$: 先頭波 $z = 33.88$ m, 高さ $0.0864$ m, 第2峰 $z = 31.59$ m, 高さ $0.0541$ m（2峰検出）
  - $N=512$: 先頭波 $z = 34.33$ m, 高さ $0.0947$ m, 第2峰 $z = 32.53$ m, 高さ $0.0755$ m, 第3峰 $z = 30.60$ m, 高さ $0.0383$ m（3峰検出）

---

## 5. NTHMP BP05 水槽実験ベンチマークの総合反映（成功と失敗）

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
