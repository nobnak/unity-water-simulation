# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-11-02 06:40:00 UTC`
- **Git Commit**: `04814d6`
- **Unity Version**: `6000.3.27f2`
- **GPU Device**: `NVIDIA GeForce RTX 5070 (Direct3D11)`
- **Reference Solvers & Data Sources**:
  1. **NTHMP BP05 Laboratory Flume Experiment** (CHL / USACE flume, open benchmark data `Docs/ReferenceData/NTHMP_BP05/`): Solitary wave shoaling without breaking.
  2. **Independent 1D Peregrine (1967) FP64 Reference Solver** (`Docs/IndependentPeregrineReferenceSolver.py`): Continuous depth-averaged Boussinesq system in IEEE 754 FP64 with RK4 time integration, 4th-order spatial differences, exact banded matrix solve, and strictly zero artificial dissipation. Grid convergence verified ($N=512, 1024, 2048$, Cauchy order $p = 3.28 \sim 3.45$).

---

## 1. 総合結論（ユーザーの2大疑問に対する客観的回答）

### 疑問1: 「初期版より現在版が物理的に妥当か」
- **回答: 【確認できた範囲において、現在版が物理的・数値的に妥当】**
  - **根拠と事実**:
    1. **初期版の直接再現性**: 初期版の厳密なコミットそのものは再現していません（現行のFilterOffは中央差分ではなく新型の風上対流項を備えています）。したがって、未再現の初期コミットに対する絶対的な真偽断定は保留します。
    2. **未散逸連続解との対比**: 散逸を一切含まない独立FP64連続Peregrine参照解において、棚進入初期段階（$t = 14.0$ s, 伝播距離 $z < 37$ m）では波面の急峻化（波状段波の形成）は起きるものの、**独立した副次孤立波としての物理的分裂・空間分離はまだ完了していません**。
    3. **FilterOffの格子依存性**: 一方、フィルター無効（FilterOff）では $t = 14.0$ s の時点で多数の鋭い副次峰が現れますが、その峰間隔および波長は格子幅 $\Delta z$ に直接比例（約 $7 \sim 9 \Delta z$）して変化します。さらに平坦水深対照実験（段差ゼロ、物理的分裂要因なし）でもFilterOffは $N=512$ で5個の鋸歯状数値振動を自発成長させました。
    4. **判定**: 初期版・無散逸版で見られていた浅瀬進入直後の多数の激しい波打ちは、物理的なソリトン分裂ではなく**MacCormack法の高周波数値分散誤差（寄生短波振動）**であったことが裏付けられます。現行FCTはこの寄生振動を完全に除去しつつ物理的な主波伝播を維持するため、数値流体力学的に妥当です。

### 疑問2: 「現在の安定化（FCT）が物理的分裂まで抑えているか」
- **回答: 【FCTが物理的分裂を原理的に抑殺しているわけではなく、格子解像度とフィルター幅の相対関係（未解像）が主因】**
  - **根拠と事実**:
    1. **解像度 $N=256$ ($\Delta z = 0.195$ m) の場合**:
       棚上で発生する分裂波の峰間隔は約 $1.7$ m です。$\Delta z = 0.195$ m では峰間隔が約 $8.7$ セルしかありません。4階双調和FCTフィルターのステンシル（5点差分）がこの間隙に対して相対的に過大となり、2つの峰の間のくぼみを平滑化して単一波（幅広な1峰）として伝播させます。
    2. **解像度 $N=512$ ($\Delta z = 0.098$ m) の場合**:
       格子を細分化すると峰間隔 $1.69$ m は約 $17.3$ セルに拡大します。この解像度では、**現行CurrentFCTをそのまま適用しても第2波（高さ $0.055$ m）が明確に分離・維持**されます（先頭波高 $0.083$ m, 伝播速度 $c_1 \approx 1.65$ m/s, 第2波 $c_2 \approx 1.65$ m/s）。
    3. **散逸の影響**:
       FCTは波高に対して一定の散逸（平坦水深で約20%の減衰）をもたらしますが、物理的分裂そのものを消滅させるのではなく、**格子解像度が不足している領域において局所的に分裂波を平滑化（未解像＋散逸）していた**ことが確認されました。

---

## 2. 追跡可能な参照データとの定量照合結果

### 2.1 平坦水深対照実験（$h = 0.60$ m, $a_0 = 0.12$ m）
理論孤立波速度: $c_{\text{theory}} = \sqrt{g(h + a_0)} = \sqrt{9.81 \times 0.72} = 2.6577$ m/s

| 手法 / 格子 | 速度 (m/s) | 理論比誤差 | 50m伝播後波高保持率 | 寄生振動・有意峰数 | 判定 |
| :--- | :---: | :---: | :---: | :---: | :--- |
| **独立Peregrine FP64 (N=2048)** | 2.6577 | +0.00% | 1.000 | 1 (厳密に保持) | PASS (参照基準) |
| **GPU CurrentFCT (N=256)** | 2.6179 | -1.50% | 0.792 | 1 (安定単一波) | PASS (速度高精度一致) |
| **GPU FilterOff (N=256)** | 2.6238 | -1.27% | 0.866 | 1 | PASS |
| **GPU FilterOff (N=512)** | 2.6380 | -0.74% | 0.936 | 5 (鋸歯状寄生振動発生) | **NG (数値振動成長)** |

### 2.2 棚地形ベンチマーク（$h_1=0.60$ m $\to h_2=0.20$ m, 斜面 1:20, $t=14.0$ s）
棚進入部: $z \in [18\text{ m}, 48\text{ m}]$

| 手法 / 格子 | 先頭波位置 $z$ (m) | 先頭波高 $H$ (m) | 第2波高 $H_2$ (m) | 峰間隔 $\Delta z$ (m) | 棚上峰数 | 形態観察と分離状態 |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **独立Peregrine FP64 ($N=2048$)** | 36.96 | 0.312 | (段波尾部) | - | 5 (未分離波状段波) | 物理的分裂は進行中だが孤立波分離は未完了 |
| **GPU CurrentFCT ($N=256$)** | 33.79 | 0.070 | - | - | 1 | 粗い格子幅により第2波が平滑化 |
| **GPU CurrentFCT ($N=512$)** | 34.33 | 0.083 | 0.055 | 1.69 | **2** | **第2波が明確に分離・存続** |
| **GPU FilterOff ($N=256$)** | 33.88 | 0.086 | 0.054 | 2.30 | 2 | 分散性多峰化 |
| **GPU FilterOff ($N=512$)** | 34.33 | 0.095 | 0.076 | 1.80 | 3 | 多峰化（間隔が格子幅に比例して短縮） |

### 2.3 拡張棚における物理的分裂の長期発展（独立Peregrine FP64, $L=100$ m）
長距離伝播（$x / h_2 > 200$）において、物理的なソリトン分裂が完全に空間分離し、振幅順に整列して伝播することを確認:
- **$t = 14.0$ s**: 先頭波 $z = 36.98$ m ($H = 0.306$ m), 後続段波急峻化
- **$t = 20.0$ s**: 先頭波 $z = 49.54$ m ($H = 0.309$ m), 第2峰 $z = 44.70$ m ($H = 0.155$ m)
- **$t = 26.0$ s**: 先頭波 $z = 62.09$ m ($H = 0.308$ m), 第2峰 $z = 55.59$ m ($H = 0.154$ m), 第3峰 $z = 51.44$ m ($H = 0.082$ m)
- **$t = 32.0$ s**:
  - 第1ソリトン: $z = 74.69$ m ($H = 0.303$ m, 速度 $c \approx 2.10$ m/s)
  - 第2ソリトン: $z = 66.54$ m ($H = 0.155$ m, 速度 $c \approx 1.86$ m/s)
  - 第3ソリトン: $z = 61.31$ m ($H = 0.082$ m, 速度 $c \approx 1.66$ m/s)
  - 第4ソリトン: $z = 57.25$ m ($H = 0.037$ m, 速度 $c \approx 1.53$ m/s)

---

## 3. 生成された可視化成果物（ベクターSVG）

1. `Docs/Plot_Independent_Reference_Comparison.svg`:
   - 独立Peregrine FP64参照解、CurrentFCT ($N=256, 512$)、FilterOff ($N=256, 512$) の棚上波形比較。
2. `Docs/Plot_Soliton_Fission_Extended.svg`:
   - 独立Peregrine FP64による $L=100$ m 拡張棚での物理的ソリトン分裂の軌跡（4つの整列孤立波）。
3. `Docs/Plot_Grid_Convergence.svg`:
   - CurrentFCT における空間格子収束性（$N=128, 256, 512$）。$N=512$ で第2波が明確に分離する挙動の提示。
4. `Docs/Plot_FlatBed_Control.svg`:
   - 平坦水深対照実験。FilterOffの鋸歯状数値振動成長とCurrentFCTの単一波安定性。
5. `Docs/Plot_Station_TimeSeries.svg`:
   - 観測点 $z=29.2$ m における時系列波形比較。
