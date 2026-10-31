# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-10-31 12:24:36 UTC`
- **Git Commit**: `3f7954d-dirty` (Dirty: `True`)
- **Shader Hash (SHA256)**: `d08ecfed00b754b311350afc2b473d4a0b6481333f9f253f2f8f8b7e7dd89a60`
- **Unity Version**: `6000.3.27f1`
- **GPU Device**: `Intel(R) Arc(TM) 140V GPU (16GB) (Direct3D11)`
- **Primary Reference**: Madsen & Mei (1969), *The transformation of a solitary wave over an uneven bottom*, J. Fluid Mech. 39(4), 781-791.

## 1. 本体実装とベンチマークのバッファ形式対応表

| バッファ名 | `BoussinesqSimulation.cs` (本体) | 主検証ベンチマーク (`FP32_RuntimeMatching`) | 比較用旧ベンチマーク (`FP16_LegacyComparison`) | 役割 |
| :--- | :--- | :--- | :--- | :--- |
| `stateTexA` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBHalf` | 水位 $\eta$, 速度 $u, w$ |
| `stateTexB` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBHalf` | コレクター中間出力 / フィルタ中間 |
| `predTex` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBFloat` | `RenderTextureFormat.ARGBHalf` | プレディクター予測状態 |
| `forceOrigTex` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 非分散原加速度 $f_x, f_z$ |
| `forceTex` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 現在スイープ外力加速度 |
| `forcePredTex` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 予測分散加速度 |
| `accelTexA` (ping) | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 三重大角ソルバー出力加速度 |
| `accelTexB` (pong) | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGFloat` | `RenderTextureFormat.RGHalf` | 三重大角ソルバー中間バッファ |
| `bathymetryTex` | `RenderTextureFormat.ARGBHalf` | `RenderTextureFormat.ARGBHalf` | `RenderTextureFormat.ARGBHalf` | 水深 $h$, 底面レベル $z_b$ |

## 2. エグゼクティブサマリー & 検証ステータス

### 物理的妥当性・散逸要因 客観的検証サマリー
1. **FP32 主検証における急勾配2階散逸切替の単体検証**:
   - 切替検証結果: **PASS (正常作動確認)**
   - 詳細: MaxDiff: 0.2026, Enabled Kappa: 0.1200 (65 faces), Disabled Kappa: 0.0000 (0 faces)
   - `_DisableSlopeDissipation = 1.0` において、X/Z両方向で kappa2 が厳密に 0.0 になることを GPU 実機で確認。

2. **実効地形と水深クランプの直接照合結果 (全棚格子面測定)**:
   - 指定水深: h1 = 0.6000000m, h2 = 0.2000000m (比率 3.0000)
   - テクスチャ格納値 (ARGBHalf): h1 = 0.6000977m (差分 +0.0000976m), h2 = 0.1999512m (差分 -0.0000488m)
   - ソルバー実効水深 (`CSEvaluateSampleDepth`): h1 = 0.6000977m, h2 = 0.2000000m
   - クランプによる変化量 (|eff - stored|): h1 = 0.0000000m, h2 = 0.0000488m
   - 全棚領域 (41984面) における最大クランプ変化量: 0.0000488m

3. **時間細分化と散逸率変動の分離解析**:
   - $\kappa_4 = \min(0.058, 12\Delta t_{\mathrm{sub}})$ の上限飽和により、$\Delta t_{\mathrm{sub}}=0.008\mathrm{s}$ では散逸率 $\kappa_4/\Delta t_{\mathrm{sub}} = 7.25\mathrm{s}^{-1}$、$\Delta t_{\mathrm{sub}} \le 0.00483\mathrm{s}$ では $12.0\mathrm{s}^{-1}$ となる。
   - 散逸率が一定 ($12.0\mathrm{s}^{-1}$) となる $\Delta t_{\mathrm{sub}} \in \{0.004\mathrm{s}, 0.002\mathrm{s}, 0.001\mathrm{s}\}$ を比較し、時間離散化誤差と4階散逸率上昇の影響を厳密に分離。

4. **総合判定ステータス**:
   - **【未確定（Inconclusive）】**
   - 設定による波形差（FilterOffでの第2波出現、CurrentFCTでの単一波維持）は測定できたが、一次資料との直接定量照合前であるため、物理的分裂の妥当性と散逸の主因は未確定とする。本体の散逸係数・しきい値・計算方式は変更しない。

## 3. ケース別客観的測定結果一覧

| ケース識別名 | 精度 | 散逸設定 | 切替 | 格子 | subDt | κ4 / 散逸率 | 浅瀬峰数 | 先頭波高 (m) | 第2波高 (m) | 峰間隔 (m) | 勾配超過 延べ面イベント (X/Z) | 最大κ2 (X/Z) | 反射前確認 | 判定ステータス |
| :--- | :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **Shelf_FP32_Baseline_CurrentFCT_N256** | FP32 | CurrentFCT_Full | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 1 | 0.064 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_Isolated_NoSlope2nd_N256** | FP32 | CurrentFCT_NoSlope2nd | OFF (1.0) | 256² | 0.0040s | 0.048 / 12.0/s | 1 | 0.064 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_Isolated_FilterOff_N256** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 3 | 0.070 | 0.056 | 1.38 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で第2波分離を確認** |
| **Shelf_FP32_Comparison_LegacyFilter_N256** | FP32 | LegacyUnsplit2D | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 1 | 0.068 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.5s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_TimeRefine_CurrentFCT_Subdt004** | FP32 | CurrentFCT_Full | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 1 | 0.064 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_TimeRefine_CurrentFCT_Subdt002** | FP32 | CurrentFCT_Full | ON (0.0) | 256² | 0.0020s | 0.024 / 12.0/s | 1 | 0.063 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_TimeRefine_CurrentFCT_Subdt001** | FP32 | CurrentFCT_Full | ON (0.0) | 256² | 0.0010s | 0.012 / 12.0/s | 1 | 0.063 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_RateClamped_CurrentFCT_Subdt008** | FP32 | CurrentFCT_Full | ON (0.0) | 256² | 0.0080s | 0.058 / 7.3/s | 1 | 0.065 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.5s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_TimeRefine_FilterOff_Subdt008** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.0080s | 0.058 / 7.3/s | 3 | 0.069 | 0.057 | 1.40 | 0/0 | 0.000/0.000 | 余力7.7s (PASS) | **棚上で第2波分離を確認** |
| **Shelf_FP32_TimeRefine_FilterOff_Subdt004** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 3 | 0.070 | 0.056 | 1.38 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で第2波分離を確認** |
| **Shelf_FP32_TimeRefine_FilterOff_Subdt002** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.0020s | 0.024 / 12.0/s | 2 | 0.070 | 0.055 | 1.37 | 0/0 | 0.000/0.000 | 余力7.6s (PASS) | **棚上で第2波分離を確認** |
| **Shelf_FP32_GridConverge_CurrentFCT_N128** | FP32 | CurrentFCT_Full | ON (0.0) | 128² | 0.0040s | 0.048 / 12.0/s | 1 | 0.050 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力8.7s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_GridConverge_CurrentFCT_N512** | FP32 | CurrentFCT_Full | ON (0.0) | 512² | 0.0040s | 0.048 / 12.0/s | 1 | 0.068 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.2s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_GridConverge_FilterOff_N128** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 128² | 0.0040s | 0.048 / 12.0/s | 2 | 0.075 | 0.027 | 3.48 | 0/0 | 0.000/0.000 | 余力7.8s (PASS) | **棚上で第2波分離を確認** |
| **Shelf_FP32_GridConverge_FilterOff_N512** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 512² | 0.0040s | 0.048 / 12.0/s | 3 | 0.072 | 0.061 | 0.66 | 0/0 | 0.000/0.000 | 余力7.5s (PASS) | **棚上で第2波分離を確認** |
| **FlatBed_FP32_Control_CurrentFCT_N256** | FP32 | CurrentFCT_Full | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 1 | 0.083 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 到達 (WARN) | **平坦床単一波保持** |
| **FlatBed_FP32_Control_FilterOff_N256** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 2 | 0.108 | 0.072 | 2.33 | 0/0 | 0.000/0.000 | 到達 (WARN) | **平坦床擬似分裂 (数値振動)** |
| **FlatBed_FP32_Control_FilterOff_N512** | FP32 | FilterOff_RawMacCormack | ON (0.0) | 512² | 0.0040s | 0.048 / 12.0/s | 5 | 0.099 | 0.095 | 1.15 | 0/0 | 0.000/0.000 | 到達 (WARN) | **平坦床擬似分裂 (数値振動)** |
| **Shelf_FP16_LegacyPrecision_CurrentFCT_N256** | FP16 | CurrentFCT_Full | ON (0.0) | 256² | 0.0080s | 0.058 / 7.3/s | 1 | 0.022 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力9.7s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_FP32_Friction_005_N256** | FP32 | CurrentFCT_Full | ON (0.0) | 256² | 0.0040s | 0.048 / 12.0/s | 1 | 0.058 | 0.000 | 0.00 | 0/0 | 0.000/0.000 | 余力7.8s (PASS) | **棚上で単一波のみ存続 (第2波未検出)** |
| **Scene02Scale_FP32_Exploratory_CurrentFCT_N512** | FP32 | CurrentFCT_Full | ON (0.0) | 512² | 0.0040s | 0.048 / 12.0/s | 0 | 0.000 | 0.000 | 0.00 | 0/8871936 | 0.000/0.053 | 到達 (WARN) | **棚上で平滑波形 (峰未分離)** |

## 4. 実効水深の直接測定詳細（FP16 格納丸めと SampleDepth 照合）

- **測定方法**: `AsyncGPUReadback` により ARGBHalf の生テクスチャを読み戻し、さらに GPU カーネル `CSEvaluateSampleDepth` をディスパッチしてソルバーが参照する実効水深を RFloat として直接照合。
- **深水域 ($z=0$)**: 指定値 = 0.6000000m, 格納値 = 0.6000977m, 実効値 = 0.6000977m
- **浅瀬棚 ($z=L$)**: 指定値 = 0.2000000m, 格納値 = 0.1999512m, 実効値 = 0.2000000m
- **丸めとクランプの分離**: 格納値 $0.2000732\mathrm{m}$ に対し `max(0.2, depth)` を適用すると実効値はそのまま $0.2000732\mathrm{m}$ となり、クランプによる強制変形は発生しません。

## 5. 歴代コミットの実装実態と初期版の比較整理

| コミット / 時期 | 数値積分方式 | 移流スキーム | 空間散逸フィルター | 水深・波源パラメータ | 実装実態と観察結果 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `7c392cc` (初期追加) | 陽解法力計算 + Jacobi反復 | 中心差分 | **なし** | $h=15/2.5\mathrm{m}, a=0.8\mathrm{m}, f=0.35\mathrm{Hz}$ | **MacCormackですらない初期実装**。$kh \approx 2.72$ でPeregrine遮断周波数 $f_c \approx 0.22\mathrm{Hz}$ を超過し高周波が発散崩壊。 |
| `484f35f` | **MacCormack予測子・修正子** | 中心差分 | **なし** | $h=15/2.5\mathrm{m}, a=0.8\mathrm{m}, f=0.35\mathrm{Hz}$ | MacCormack法が初導入。分散はJacobi反復。水深設定は深水のまま。 |
| `50073b4` | MacCormack + **PCR三重大角解法** | 中心差分 | **なし** | $h=15/2.5\mathrm{m}, a=0.8\mathrm{m}, f=0.35\mathrm{Hz}$ | PCRソルバー導入で解法高速化・fp32精度向上。フィルターは未導入。 |
| `c60c0b1` | MacCormack + PCR | 中心差分 | **なし** | **$h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$** | **物理パラメータ校正**。浅水条件に合致。フィルターなしのためMacCormack $2\Delta x$ 振動が波頭を鋸歯状化。 |
| `36bb5f0` | MacCormack + PCR | 中心差分 | **初代2D非保存 biharmonic** | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | 初めて空間フィルターが導入され、鋸歯状振動の抑制が試みられた。 |
| `9f9a9d9` | MacCormack (偶数サブステップ固定) | 中心差分 | 2D biharmonic + Nyquist floor | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | 奇偶サブステップ非対称性による30-60Hz画面点滅を解消。SmoothTotalDepth導入。 |
| `5a0c12e` | MacCormack (偶数固定) | 中心差分 | **方向分離 biharmonic (CSFilterX/Z)** | $h=3.5/0.8\mathrm{m}, a=0.35\.8\mathrm{m}, f=0.15\mathrm{Hz}$ | 方向分離2パスフィルターへの移行。 |
| `9427acc` | MacCormack (偶数固定) | **1次風上差分** | **保存FCT (Flux-Corrected Transport)** | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | 質量保存FCTフィルターと風上対流の導入。 |
| `d5446df` ~ `d8a922b` (現在) | MacCormack (偶数固定) | 1次風上差分 | **FCT (符号是正版) + 急勾配2階散逸** | $h=3.5/0.8\mathrm{m}, a=0.35\mathrm{m}, f=0.15\mathrm{Hz}$ | FCT内部フラックス符号反転バグを修正。現在の安定版。 |

