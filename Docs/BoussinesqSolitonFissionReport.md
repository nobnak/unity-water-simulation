# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-11-02 08:25:00 UTC`
- **Git Commit**: `caef994` + progression analysis
- **Unity Version**: `6000.3.27f2`
- **GPU Device**: `NVIDIA GeForce RTX 5070 (Direct3D11)`
- **Reference Solvers & Data Sources**:
  1. **Direct GPU PCR vs FP64 Linear Inversion & Substep Audit** (`Docs/GpuPcrDirectPrecisionReport.json`, `Packages/com.nobnak.water-simulation/Editor/GpuPcrDirectPrecisionVerifier.cs`): Direct readback from GPU ComputeShader (`CSTridiagonalZ_256`, `CSPredict`, `CSCorrect`, `CSApplyPredictedDispersion`, `CSApplyDispersion`) compared with double-precision Thomas algorithm.
  2. **Shoaling Wave Progression & Factor Isolation Analysis** (`Docs/AnalyzeShoalingWaveProgression.py`): Time-series progression tracking (Pre-shelf $t=3$s, On-slope $t=6$s, On-shelf $t=10, 14$s) comparing FP64 Reference ($N=1024$), GPU-equivalent FP64 ($N=256, 512$), and candidate advection schemes.
  3. **NTHMP BP05 Laboratory Flume Experiment** (CHL / USACE flume, open benchmark `Docs/ReferenceData/NTHMP_BP05/`): Solitary wave shoaling on composite beach (Cases A and B).

---

## 1. 総合暫定結論

### 判定ステータス: **【未確定（Inconclusive）】**

> **【暫定結論】**
> 対象条件においてGPU PCRとFP64線形解、および短時間の離散時間発展（逆Parity・壁面近傍・複数サブステップ）の実装整合を確認した。一方、長時間の波高不足（BP05 Case Bの$-27.5\%$ FAIL等）の原因は未解決であり、物理的妥当性は未確定。
> 
> ※ 以下の断定はすべて撤回・保留を継続します：
> - 現在版が物理的に正しいことを確認した。
> - 初期版の分裂は数値振動と確定した。
> - 物理的分裂を阻害していないことを確認した。
> - 未解像が主因なので現行係数の妥当性が実証された。

---

## 2. 同じ線形問題でのGPU PCRとFP64の直接比較

実機GPU（NVIDIA GeForce RTX 5070）上で実際のComputeShaderカーネル（`CSTridiagonalZ_256`）を実行し、同一格子（$N=256$, $\Delta z=0.196$ m）、実効水深、外力、境界条件における倍精度Thomasアルゴリズム直接解と照合しました（`Docs/GpuPcrDirectPrecisionReport.json`）。

### 2.1 境界条件の厳密分離
- **法線方向（$a_z$）**: 境界 $j=0, H-1$ で **Dirichlet 境界条件 $a_z = 0$**（物理流速 $w$ の加速度）
- **接線方向（$a_x$）**: 境界 $j=0, H-1$ で **Neumann 境界条件 $\partial a_x / \partial z = 0$**

### 2.2 測定結果と合否判定
合否判定は法線成分（最大差 $\le 10^{-5}$ または相対差 $\le 10^{-4}$）、接線成分（最大差 $\le 10^{-5}$ または相対差 $\le 10^{-4}$）、行列残差（$\| \mathbf{M} a - f \|_\infty \le 10^{-4}$）のすべてを満たすことを条件に動的判定。

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

- **Half格納誤差の区別**:
  - `halfStorageDepthRoundingDiff` [m]: 16ビット Half 格納に伴う水深自体の丸め誤差（$\approx 4 \times 10^{-4}$ m）
  - `halfStorageAccelRoundingDiff` [m/s$^2$]: 水深丸めに起因する加速度解の差（$10^{-6} \sim 10^{-4}$ m/s$^2$）
- **検証範囲の限定**: $N=256$ 線形 PCR 試験の合格を他解像度・時間発展全体・物理的妥当性へ拡大解釈しません。

---

## 3. 時間発展の段階的照合と境界処理の厳密一致

### 3.1 予測段境界処理の不一致の解明
- 以前の予測段外力差 $1.08 \times 10^{-2}$ m/s$^2$ および流速差 $3.33 \times 10^{-3}$ m/s は、**CPU参照側のみが端点（$j=0, H-1$）で `fzPred` と `wPred` をゼロにしていたため**に生じたものです。初期条件の端点流速 $w_0[0] \approx 0.003329$ m/s と完全に一致しており、単精度丸めや風上分岐によるものではありませんでした。
- GPU本体では予測段で端点流速をゼロ化せず、最終段 `CSApplyDispersion` でのみゼロクランプします。CPU参照をGPUと同じタイミング・条件に一致させた結果、予測段の差分は単精度限界に収まりました。

### 3.2 段階的検証シナリオ結果

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

#### シナリオ 2: 逆Parity試験（Parity 1）
- 全8フェーズで誤差 $\le 3.27 \times 10^{-7}$: **【全フェーズ PASS】**

#### シナリオ 3: 境界近傍波試験（$z_0=0.8$m, 境界 $j=0$ で波高 $\approx 0.09$m）
- 境界セル $j=0$ での水面差 $3.60 \times 10^{-9}$ m、流速差 $1.36 \times 10^{-8}$ m/s: **【全フェーズ PASS】**

#### シナリオ 4: 複数サブステップ累積試験（5連続サブステップ）
- Step 1: $\max |\Delta \eta| = 3.99 \times 10^{-9}$ m, $\max |\Delta w| = 1.37 \times 10^{-8}$ m/s (PASS)
- Step 2: $\max |\Delta \eta| = 1.25 \times 10^{-8}$ m, $\max |\Delta w| = 2.53 \times 10^{-8}$ m/s (PASS)
- Step 3: $\max |\Delta \eta| = 1.16 \times 10^{-8}$ m, $\max |\Delta w| = 3.39 \times 10^{-8}$ m/s (PASS)
- Step 4: $\max |\Delta \eta| = 1.15 \times 10^{-8}$ m, $\max |\Delta w| = 4.05 \times 10^{-8}$ m/s (PASS)
- Step 5: $\max |\Delta \eta| = 1.70 \times 10^{-8}$ m, $\max |\Delta w| = 4.35 \times 10^{-8}$ m/s (PASS)

---

## 4. 長時間の波高不足の時系列進展と要因分離

短時間の離散整合（$10^{-7}$精度）が確認されたため、主課題を「長時間の波高不足」へ移行し、棚への到達前・斜面通過中・棚上の3段階において時系列比較を実施しました（`Docs/AnalyzeShoalingWaveProgression.py`）。

### 4.1 段階別波高・ピーク位置の時系列追跡
| 時刻 $t$ [s] | 物理段階 | FP64 参照解 ($N=1024$) | FP64 風上移流 ($N=256$) | FP64 風上移流 ($N=512$) | FP64 逆向片側差分 ($N=256$) | FP64 逆向片側差分 ($N=512$) |
|---|---|---|---|---|---|---|
| **$t = 3.0$ s** | 棚前平坦床 ($z \approx 13$m) | $H=0.1272$m ($z=12.95$) | $H=0.1212$m ($z=12.94$) | $H=0.1247$m ($z=12.92$) | $H=0.1251$m ($z=12.94$) | $H=0.1269$m ($z=12.92$) |
| **$t = 6.0$ s** | 斜面通過中 ($10 \le z \le 18$m) | $H=0.2202$m ($z=19.99$) | $H=0.1429$m ($z=19.61$) | $H=0.1701$m ($z=19.86$) | $H=0.1728$m ($z=19.80$) | $H=0.2023$m ($z=19.96$) |
| **$t = 10.0$ s** | 棚上浅水域 ($z \ge 18$m) | $H=0.2674$m ($z=28.20$) | $H=0.1135$m ($z=27.06$) | $H=0.1307$m ($z=27.40$) | $H=0.2022$m ($z=27.65$) | $H=0.2333$m ($z=28.08$) |
| **$t = 14.0$ s** | 棚上分裂領域 ($z \ge 18$m) | $H=0.2652$m ($z=36.41$) | $H=0.0859$m ($z=33.92$) | $H=0.0946$m ($z=34.34$) | $H=0.1935$m ($z=35.49$) | $H=0.2307$m ($z=36.11$) |

### 4.2 GPU実機実行結果との照合 ($t = 14.0$ s)
- **GPU FilterOff ($N=256$)**: $H = 0.0864$m（FP64 風上 $N=256$ の $0.0859$m と差わずか $+0.0005$m）
- **GPU FilterOff ($N=512$)**: $H = 0.0947$m（FP64 風上 $N=512$ の $0.0946$m と差わずか $+0.0002$m）
- **GPU CurrentFCT ($N=256$)**: $H = 0.0703$m（FCT散逸減衰: $-0.0161$m, $-18.7\%$）
- **GPU CurrentFCT ($N=512$)**: $H = 0.0832$m（FCT散逸減衰: $-0.0116$m, $-12.2\%$）

### 4.3 波高減衰要因の定量的分離
1. **平坦床（$t \le 3$s）**: 風上移流の波高低下は $-4.7\%$（$N=256$）および $-2.0\%$（$N=512$）と極めて軽微。
2. **斜面〜棚上（$t > 3$s）**: 水深急減に伴い空間速度勾配 $\partial w/\partial z$ が急増し、1次風上移流の数値粘性 $\nu_{\text{num}} = \frac{1}{2}|w|\Delta z$ が急拡大。
3. **要因寄与率の分解（$t = 14$s, 全減衰量 $\Delta H = 0.2652 - 0.0703 = 0.1949$m に対する寄与）**:
   - **移流数値粘性**: $\Delta H \approx 0.1076$m（**寄与率 約55%**）
   - **格子未解像**: $\Delta H \approx 0.0717$m（**寄与率 約37%**）
   - **FCT散逸フィルター**: $\Delta H \approx 0.0161$m（**寄与率 約8%**）

---

## 5. BP05 Case B を改善判断の基準とする方針

### 5.1 未解決項目としての固定
- **Case B（中振幅非砕波浅水化 $a/h \approx 0.26$）**: 【FAIL 継続】
  - 実水槽実験値: g5 ($h=0.20$m, $H=0.0531$m) から g7 ($h=0.144$m, $H=0.0705$m) へ **$+32.8\%$ 急成長**。
  - GPU CurrentFCT ($N=512$): $H_{\text{peak}} = 0.0511$m（**g7波高誤差 $-27.5\%$ FAIL**）。
  - GPU FilterOff ($N=512$): $H_{\text{peak}} = 0.0548$m（**g7波高誤差 $-22.2\%$ FAIL**）。
- **事実**: 散逸フィルターを完全に切っても（FilterOff）波高は $+8\%$ しか成長せず、$-22.2\%$ の大幅過小評価となる。したがって、FCT散逸フィルターのみを犯人とする説明は成立しない。

### 5.2 改善候補の評価軸（波高単独評価の厳禁）
今後、対流方式などの改善候補を試す場合は、必ず一要因ずつ検証用設定で比較し、以下の多面的評価軸を満たすことを採用条件とします：
1. **BP05実験波形・波高誤差**: g7波高誤差（許容10%以内）およびNRMSE。
2. **位相・波速**: 到達時刻誤差（許容0.15s以内）。
3. **格子振動（粗さ）**: 2グリッド振動指標（逆向片側差分では粗さ $1.6 \times 10^{-3}$ と風上移流 $6.3 \times 10^{-4}$ より2.5倍粗くなる点に留意）。
4. **質量・体積保存性**: 水量収支誤差（$10^{-3}$ 以内）。
5. **計算安定性と負荷**: 急勾配でのブローアップ耐性とGPUメモリ・スループット。

通常設定の変更は、これら全項目において改善が確認できた後に判断します。
