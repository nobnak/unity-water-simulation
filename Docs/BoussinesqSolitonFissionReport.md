# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-10-31 11:16:20 UTC`
- **Git Commit**: `d8a922b` (Dirty: `True`)
- **Unity Version**: `6000.3.27f1`
- **GPU Device**: `Intel(R) Arc(TM) 140V GPU (16GB) (Direct3D11)`
- **Primary Reference**: Madsen & Mei (1969), *The transformation of a solitary wave over an uneven bottom*, J. Fluid Mech. 39(4), 781-791.

## 1. エグゼクティブサマリー & 検証ステータス

### 物理的妥当性・散逸要因 客観的検証サマリー
1. **実効地形と水深クランプの照合結果**:
   - 指定水深: h1 = 0.60m, h2 = 0.20m (比率 3.00)
   - ソルバー実効水深: h1 = 0.60m, h2 = 0.20m (比率 3.00)
   - `SampleDepth` (max(0.2, depth)) による水深歪み: **0% (完全一致)**。実効水深の不一致を解消。
2. **平坦床（対照実験）における数値振動の確認**:
   - Current FCT (N=256): 峰数 = 1, 波高保持率 = 74.4%, ノイズ指標 = 1.52E-004 (平坦床単一波保持 (PASS))
   - Filter OFF (N=256): 峰数 = 3, ノイズ指標 = 1.03E-003 (平坦床での擬似分裂・数値振動 (FAIL))
   - **確認済み事実**: Filter OFF (生MacCormack相当) では平坦床でも数値振動により波が割れる。したがって初期に見えた細かな波打ち・ギザギザの一部は数値振動である。
3. **棚状地形における散逸要因の個別分離結果**:
   - [Baseline] Current FCT (N=256): 先頭波高 = 0.065m, 第2波高 = 0.000m, 勾配発動セル数 = 0
   - [Factor 1] 急勾配2階散逸OFF (N=256): 先頭波高 = 0.065m, 第2波高 = 0.000m
   - [Factor 2] フィルターOFF (生MacCormack, N=256): 先頭波高 = 0.068m, 第2波高 = 0.057m, 波間隔 = 1.37m
   - **客観的観察**: フィルターOFFでは棚上で先頭波の後方に第2波が分離する波形差が観測される。一方、Current FCTでは第2波の突出度が小さく単一波に近い波形となる。
   - **判定ステータス**: 【未確定】。参照解（一次資料の厳密な実験波形）との直接照合前であるため、現段階で物理的ソリトン分裂の完全再現や過剰散逸を断定せず、波形差の確認にとどめる。


## 2. ケース別客観的測定結果一覧

| ケース識別名 | 地形条件 | 散逸設定 | 格子解像度 | 指定水深比 | 実効水深比 | 浅瀬峰数 | 先頭波高 (m) | 第2波高 (m) | 峰間隔 (m) | 勾配作動セル数 | 曲率ノイズ | 判定ステータス |
| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **Shelf_Baseline_CurrentFCT_N256** | ShelfSlopeExploration | CurrentFCT_Full | 256² | 0.60/0.20 | 0.60/0.20 | 1 | 0.065 | 0.000 | 0.00 | 0 | 1.15E-004 | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Isolated_NoSlope2nd_N256** | ShelfSlopeExploration | CurrentFCT_NoSlope2nd | 256² | 0.60/0.20 | 0.60/0.20 | 1 | 0.065 | 0.000 | 0.00 | 0 | 1.15E-004 | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Isolated_FilterOff_N256** | ShelfSlopeExploration | FilterOff_RawMacCormack | 256² | 0.60/0.20 | 0.60/0.20 | 3 | 0.068 | 0.057 | 1.37 | 0 | 5.93E-004 | **棚上で第2波分離を確認** |
| **Shelf_Comparison_LegacyFilter_N256** | ShelfSlopeExploration | LegacyUnsplit2D | 256² | 0.60/0.20 | 0.60/0.20 | 1 | 0.068 | 0.000 | 0.00 | 0 | 1.57E-004 | **棚上で単一波のみ存続 (第2波未検出)** |
| **FlatBed_Control_CurrentFCT_N256** | FlatBedControl | CurrentFCT_Full | 256² | 0.60/0.60 | 0.60/0.60 | 1 | 0.089 | 0.000 | 0.00 | 0 | 1.52E-004 | **平坦床単一波保持 (PASS)** |
| **FlatBed_Control_FilterOff_N256** | FlatBedControl | FilterOff_RawMacCormack | 256² | 0.60/0.60 | 0.60/0.60 | 3 | 0.116 | 0.084 | 2.54 | 0 | 1.03E-003 | **平坦床での擬似分裂・数値振動 (FAIL)** |
| **Shelf_Convergence_CurrentFCT_N128** | ShelfSlopeExploration | CurrentFCT_Full | 128² | 0.60/0.20 | 0.60/0.20 | 1 | 0.054 | 0.000 | 0.00 | 0 | 1.78E-004 | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Convergence_CurrentFCT_N512** | ShelfSlopeExploration | CurrentFCT_Full | 512² | 0.60/0.20 | 0.60/0.20 | 1 | 0.070 | 0.000 | 0.00 | 0 | 7.75E-005 | **棚上で単一波のみ存続 (第2波未検出)** |
| **Shelf_Friction_005_N256** | ShelfSlopeExploration | CurrentFCT_Full | 256² | 0.60/0.20 | 0.60/0.20 | 1 | 0.060 | 0.000 | 0.00 | 0 | 9.59E-005 | **棚上で単一波のみ存続 (第2波未検出)** |
| **Scene02Scale_Shelf_CurrentFCT_N512** | Scene02ScaleShelf | CurrentFCT_Full | 512² | 3.50/0.80 | 3.50/0.80 | 1 | 0.570 | 0.000 | 0.00 | 5 | 4.36E-004 | **棚上で単一波のみ存続 (第2波未検出)** |

## 3. 実効地形の照合と水深下限の検証

本体 `Boussinesq.compute` の `SampleDepth` は `max(0.2, depth)` の下限処理を持っています。
前回の試験では $h_2 = 0.15\mathrm{m}$ を指定したため、テクスチャ値 ($0.15\mathrm{m}$) とソルバー実効水深 ($0.20\mathrm{m}$) が乖離し、実効水深比が $2.25$ に歪んでいました。
今回の改訂では、本体コードの水深下限 `0.20m` を尊重し、無次元水深比 $h_1/h_2 = 3.0$ を保つ縮尺設計（$h_1 = 0.60\mathrm{m}, h_2 = 0.20\mathrm{m}, a_0 = 0.12\mathrm{m}$）を採用しました。
これにより、全領域でテクスチャ値とソルバー実効水深が完全一致（クランプ歪み 0%）することを確認しました。

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

