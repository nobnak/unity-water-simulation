# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-10-31 12:09:37 UTC`
- **Git Commit**: `bf0d07b-dirty` (Dirty: `True`)
- **Shader Hash (SHA256)**: `d08ecfed00b754b311350afc2b473d4a0b6481333f9f253f2f8f8b7e7dd89a60`
- **Unity Version**: `6000.3.27f1`
- **GPU Device**: `Intel(R) Arc(TM) 140V GPU (16GB) (Direct3D11)`
- **Primary Reference**: Madsen & Mei (1969), *The transformation of a solitary wave over an uneven bottom*, J. Fluid Mech. 39(4), 781-791.

## 1. エグゼクティブサマリー & 検証ステータス

### 物理的妥当性・散逸要因 客観的検証サマリー
1. **合成入力による急勾配2階散逸切替機能の単体検証**:
   - 切替検証結果: **PASS (正常作動確認)**
   - 詳細: MaxDiff: 0.2424, Enabled Kappa: 0.1500 (65 faces), Disabled Kappa: 0.0000 (0 faces)
   - `_DisableSlopeDissipation = 1.0` において、X/Z両方向で kappa2 が厳密に 0.0 になることを GPU 実機で確認。

2. **実効地形と水深クランプの直接照合結果 (GPU Readback & Diagnostic Kernel)**:
   - 指定水深: h1 = 0.60m, h2 = 0.20m (比率 3.00)
   - テクスチャ格納値 (ARGBHalf readback): h1 = 0.600m, h2 = 0.200m (Half丸め確認)
   - ソルバー実効水深 (`CSEvaluateSampleDepth` readback): h1 = 0.600m, h2 = 0.200m
   - 水深クランプ歪み: **0% (完全一致)**。

3. **全サブステップにおける事前フィルター散逸作動実績の測定**:
   - 全サブステップのフィルター前入力に対し、実際の係数計算と同じ条件で測定。
   - 探索棚状地形（h1=0.6m, h2=0.2m, a0=0.12m）において、孤立波通過中の水面勾配が 0.40 を超えた面数、kappa2>0 の面数を正確に記録。

4. **総合判定ステータス**:
   - **【未確定（Inconclusive）】**
   - 設定による波形差（FilterOffでの第2波出現、CurrentFCTでの単一波維持）は確認したが、一次資料との直接照合前であるため、物理的分裂の妥当性と散逸の主因は未確定とする。本体の散逸係数・しきい値・計算方式は変更しない。

## 2. ケース別客観的測定結果一覧

| ケース識別名 | 地形条件 | 散逸設定 | 切替値 | 格子 | dt (subDt) | 実効水深比 | 浅瀬峰数 | 先頭波高 (m) | 第2波高 (m) | 峰間隔 (m) | 勾配超過面数 (X/Z) | 最大κ2 (X/Z) | 反射前確認 | 判定ステータス |
| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **Shelf_Baseline_CurrentFCT_N256** | ShelfSlopeExploration | CurrentFCT_Full | ON (0.0) | 256² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.022 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.7s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Isolated_NoSlope2nd_N256** | ShelfSlopeExploration | CurrentFCT_NoSlope2nd | OFF (1.0) | 256² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.022 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.7s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Isolated_FilterOff_N256** | ShelfSlopeExploration | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.016s (0.0080s) | 0.60/0.20 | 2 | 0.041 | 0.031 | 1.78 | 0/0 | 0.000/0.000 | 余力8.8s (PASS) | **棚上で第2波分離を確認** |
| **Shelf_Comparison_LegacyFilter_N256** | ShelfSlopeExploration | LegacyUnsplit2D | ON (0.0) | 256² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.024 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Convergence_CurrentFCT_N128** | ShelfSlopeExploration | CurrentFCT_Full | ON (0.0) | 128² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.015 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力10.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Convergence_CurrentFCT_N512** | ShelfSlopeExploration | CurrentFCT_Full | ON (0.0) | 512² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.025 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.2s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Convergence_NoSlope2nd_N128** | ShelfSlopeExploration | CurrentFCT_NoSlope2nd | OFF (1.0) | 128² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.015 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力10.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Convergence_NoSlope2nd_N512** | ShelfSlopeExploration | CurrentFCT_NoSlope2nd | OFF (1.0) | 512² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.025 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.2s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Convergence_FilterOff_N128** | ShelfSlopeExploration | FilterOff_RawMacCormack | ON (0.0) | 128² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.042 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.2s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Convergence_FilterOff_N512** | ShelfSlopeExploration | FilterOff_RawMacCormack | ON (0.0) | 512² | 0.016s (0.0080s) | 0.60/0.20 | 20 | 0.040 | 0.036 | 0.87 | 0/0 | 0.000/0.000 | 余力8.7s (PASS) | **棚上で第2波分離を確認** |
| **Shelf_TimeRefinement_Dt008_CurrentFCT_N256** | ShelfSlopeExploration | CurrentFCT_Full | ON (0.0) | 256² | 0.008s (0.0040s) | 0.60/0.20 | 0 | 0.000 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力32.8s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_TimeRefinement_Dt004_CurrentFCT_N256** | ShelfSlopeExploration | CurrentFCT_Full | ON (0.0) | 256² | 0.004s (0.0020s) | 0.60/0.20 | 0 | 0.000 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力32.8s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_TimeRefinement_Dt004_NoSlope2nd_N256** | ShelfSlopeExploration | CurrentFCT_NoSlope2nd | OFF (1.0) | 256² | 0.004s (0.0020s) | 0.60/0.20 | 0 | 0.000 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力32.8s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_TimeRefinement_Dt004_FilterOff_N256** | ShelfSlopeExploration | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.004s (0.0020s) | 0.60/0.20 | 1 | 0.006 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力11.1s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **FlatBed_Control_CurrentFCT_N256** | FlatBedControl | CurrentFCT_Full | ON (0.0) | 256² | 0.016s (0.0080s) | 0.60/0.60 | 1 | 0.028 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 到達 (WARN) | **平坦床単一波保持 (PASS)** |
| **FlatBed_Control_NoSlope2nd_N256** | FlatBedControl | CurrentFCT_NoSlope2nd | OFF (1.0) | 256² | 0.016s (0.0080s) | 0.60/0.60 | 1 | 0.028 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 到達 (WARN) | **平坦床単一波保持 (PASS)** |
| **FlatBed_Control_FilterOff_N256** | FlatBedControl | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.016s (0.0080s) | 0.60/0.60 | 3 | 0.065 | 0.037 | 2.64 | 0/0 | 0.000/0.000 | 到達 (WARN) | **平坦床擬似分裂 (FAIL)** |
| **FlatBed_Control_FilterOff_N512** | FlatBedControl | FilterOff_RawMacCormack | ON (0.0) | 512² | 0.016s (0.0080s) | 0.60/0.60 | 46 | 0.097 | 0.093 | 1.79 | 0/0 | 0.000/0.000 | 到達 (WARN) | **平坦床擬似分裂 (FAIL)** |
| **Shelf_Friction_005_N256** | ShelfSlopeExploration | CurrentFCT_Full | ON (0.0) | 256² | 0.016s (0.0080s) | 0.60/0.20 | 1 | 0.019 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.8s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Scene02Scale_Exploratory_CurrentFCT_N512** | Scene02ScaleShelf | CurrentFCT_Full | ON (0.0) | 512² | 0.016s (0.0040s) | 3.50/0.80 | 1 | 0.056 | 0.000 | 0.00 | 0/53760 | 0.000/0.052 | 余力5.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |

## 3. 実効水深の確認方法とテクスチャ精度

- **確認方法**: `AsyncGPUReadback` により ARGBHalf の生テクスチャをデコードし、さらに GPU カーネル `CSEvaluateSampleDepth` をディスパッチしてソルバーが参照する実効水深を RFloat として直接 readback 照合。
- **テクスチャ丸め**: Half-precision float (FP16) において、0.20m は `0x3266` (0.19995m) または `0x3267` (0.20007m) として格納されます。`SampleDepth` の `max(0.2, depth)` 評価により、実効水深は厳密に 0.20000m に保たれ、クランプ歪み 0% であることを確認しました。

## 4. 歴代コミットの実装実態と初期版の比較整理

| コミット / 時期 | 数値積分方式 | 移流スキーム | 空間散逸フィルター | 水深・波源パラメータ | 実装実態と観察結果 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `7c392cc` (初期追加) | 陽解法力計算 + Jacobi反復 | 中心差分 | **なし** | $h=15/2.5\mathrm{m}, a=0.8\mathrm{m}, f=0.35\mathrm{Hz}$ | **MacCormackですらない初期実装**。$kh \approx 2.72$ でPeregrine遮断周波数 $f_c \approx 0.22\mathrm{Hz}$ を超過し高周波が発散崩壊。 |
| `484f35f` | **MacCormack予測子・修正子** | 中心差分 | **なし** | $h=15/2.5\mathrm{m}, a=0.8\mathrm{m}, f=0.35\mathrm{Hz}$ | MacCormack法が初導入。分散はJacobi反復。水深設定は深水のまま。 |
| `50073b4` | MacCormack + **PCR三重大角解法** | 中心差分 | **なし** | $h=15/2.5\mathrm{m}, a=0.8\mathrm{m}, f=0.35\mathrm{Hz}$ | PCRソルバー導入で解法高速化・fp32精度向上。フィルターは未導入。 |
| `c60c0b1` | MacCormack + PCR | 中心差分 | **なし** | **$h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$** | **物理パラメータ校正**。浅水条件に合致。フィルターなしのためMacCormack $2\Delta x$ 振動が波頭を鋸歯状化。 |
| `36bb5f0` | MacCormack + PCR | 中心差分 | **初代2D非保存 biharmonic** | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | 初めて空間フィルターが導入され、鋸歯状振動の抑制が試みられた。 |
| `9f9a9d9` | MacCormack (偶数サブステップ固定) | 中心差分 | 2D biharmonic + Nyquist floor | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | 奇偶サブステップ非対称性による30-60Hz画面点滅を解消。SmoothTotalDepth導入。 |
| `5a0c12e` | MacCormack (偶数固定) | 中心差分 | **方向分離 biharmonic (CSFilterX/Z)** | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | 方向分離2パスフィルターへの移行。 |
| `9427acc` | MacCormack (偶数固定) | **1次風上差分** | **保存FCT (Flux-Corrected Transport)** | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | 質量保存FCTフィルターと風上対流の導入。 |
| `d5446df` ~ `d8a922b` (現在) | MacCormack (偶数固定) | 1次風上差分 | **FCT (符号是正版) + 急勾配2階散逸** | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | FCT内部フラックス符号反転バグを修正。現在の安定版。 |

