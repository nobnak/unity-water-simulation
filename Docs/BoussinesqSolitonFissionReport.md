# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-10-31 14:02:36 UTC`
- **Git Commit**: `ad49364-dirty` (Dirty: `True`)
- **Shader Hash (SHA256)**: `d08ecfed00b754b311350afc2b473d4a0b6481333f9f253f2f8f8b7e7dd89a60`
- **Unity Version**: `6000.3.27f1`
- **GPU Device**: `Intel(R) Arc(TM) 140V GPU (16GB) (Direct3D11)`
- **文献参照解照合ステータス**: **【未実施】** (追跡可能な元データCSV・デジタイズ点列が未取得のため、合否判定から除外)
- **波形比較対象**: 独自の比較用近似曲線 (Custom Exploratory Curve, t=14.0s) および解像度間 Cauchy 収束性

## 1. 総合エグゼクティブサマリー & 判定枠組み

### 判定ステータス: **【未確定（Inconclusive）を維持】**

ユーザー指示に基づく厳格な判定フレームワークに従い、客観的証拠を整理します。

1. **文献照合ステータスと独自の比較用近似曲線との比較結果**:
   - 追跡可能な文献デジタイズ点列が未取得のため、文献参照解との一致率合否判定は行わず【未実施】とします。
   - $t=14.0$ s における独自の比較用近似曲線 ($z_{\text{peak}}=29.2$ m, $H=0.067$ m, $w=1.6$ m, 後続棚 $0.016$ m) に対する全波形RMSE:
     - CurrentFCT ($N=256$): **0.0247 m** (先頭波高差 -0.003 m)
     - CurrentFCT ($N=512$): **0.0245 m** (先頭波高差 +0.001 m)
     - FilterOff ($N=256$): **0.0219 m** (先頭波高差 +0.003 m)
   - **客観的事実**: 比較用近似曲線の後続棚標高とFilterOffの後続水位低下が近いため、棚上全波形RMSEはFilterOffがCurrentFCTより若干小さい値を示します。RMSE単独の大小をもって物理的妥当性や優劣を判定することはできません。

2. **波形全体の空間格子収束性評価 (同一物理座標 $z \in [0, 50\text{ m}]$、$M=2048$)**:
   - CurrentFCT 隣接格子 Cauchy 差分:
     - $L_1(128, 256) = 0.0021$ m $\to L_1(256, 512) = 0.0009$ m (観測次数 $p = 1.30$)
     - $L_2(128, 256) = 0.0046$ m $\to L_2(256, 512) = 0.0021$ m (観測次数 $p = 1.11$)
     - 格子細分化に伴う差分減少傾向（収束傾向）を確認。ただし時間刻み誤差・補間誤差・リミッター効果等を含むため、漸近収束次数の確定とはみなさず「収束傾向」にとどめます。
   - FilterOff 峰間隔のスケーリング (プロミネンス抽出、minSep 撤廃後):
     - $N=128$ ($\Delta z = 0.3906$ m): 峰間隔 $\Delta z_{\text{peak}} = 3.48$ m ($\approx 8.9 \Delta z$)
     - $N=256$ ($\Delta z = 0.1953$ m): 峰間隔 $\Delta z_{\text{peak}} = 1.38$ m ($\approx 7.1 \Delta z$)
     - $N=512$ ($\Delta z = 0.0977$ m): 峰間隔 $\Delta z_{\text{peak}} = 0.66$ m ($\approx 6.8 \Delta z$)
     - 旧コードの `minSep = max(4*dz, 0.6m)` を撤廃しても、峰間隔が格子幅に比例して縮小する傾向を示します。これは数値分散（高周波位相誤差）の存在を疑わせる有力な根拠ですが、物理的分裂全体を完全に否定する数学的証明とはしません。
     - 平坦床対照実験（段差なし、分裂の物理的原因皆無）でもFilterOffは多峰化（$N=256$で2峰、$N=512$で5峰）し、数値振動成分の混入を示唆します。

3. **因果関係の客観的限定と留保事項**:
   - FP16 vs FP32の波高減衰差（FP16: 0.022 m vs FP32: 0.064 m）は実測事実ですが、11ビット仮数部の丸め誤差のほか、中間積算精度や非線形積の丸めなど未検証要因が含まれるため、「浮動小数点打ち切りが主因」と細部まで断定せず留保します。
   - 名目散逸率一定条件 ($\kappa_4 / \Delta t_{\text{sub}} = 12.0\text{ s}^{-1}$) で時間細分化しても波高が維持されたのは「名目パラメータ率を揃えた結果」であり、FCT非線形リミッターが時間刻み依存性を完全に脱したとは主張しません。
   - 初期版（コミット `c60c0b1` 等）で見えていた分裂が数値振動であったかは、当時の条件が完全再現されるまでは仮説として扱います。

4. **総合結論**:
   - 「FP32で設定間の波形差と格子依存性を測定した。ただし追跡可能な参照データとの照合が未完了（未実施）のため、物理的分裂の妥当性と過剰散逸は未確定」とします。
   - 本体コードの散逸係数・勾配しきい値・対流方式は変更しません。

## 2. 独自の比較用近似曲線との波形差記述 (t = 14.0s)

- **近似曲線仕様**: 独自の比較用近似曲線 (Custom Exploratory Curve): t=14.0s における単一峰モデル (z_peak=29.2m, H=0.067m, w=1.6m, 後続棚=0.016m)
- **文献照合ステータス**: 未実施 (追跡可能な元データCSV・デジタイズ点列が未取得のため、文献照合および文献一致率の合否判定は未実施)
- **比較領域**: toe=10.0m, top=18.0m, mid-shelf=22.0m, 30.0m, 38.0m (棚進入部 $z=18.0 \sim 40.0$ m)
- **RMSE定義式**: RMSE = sqrt( (1/M) * sum (eta_sim - eta_custom)^2 ), z in [18.0m, 40.0m], M=200

| 手法 / 格子 | 先頭波高 (m) | 近似曲線先頭波高 (m) | 波高差 (m) | 浅瀬到達時刻 ($z=30$m) | 棚上全波形 RMSE (m) | 浅瀬有意峰数 | 波形形態と観察結果 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **CurrentFCT ($N=256$)** | 0.064 | 0.067 | -0.003 | 11.29 s | 0.0247 | 1 | 先頭波と後続棚を保持 (第2波未分離) |
| **CurrentFCT ($N=512$)** | 0.068 | 0.067 | +0.001 | 11.05 s | 0.0245 | 1 | 先頭波高が微増し単一峰維持 |
| **FilterOff ($N=256$)** | 0.069 | 0.067 | +0.003 | 11.33 s | 0.0219 | 2 | 副次峰が発生。後続平坦部水位が低くRMSEは最小 |

## 3. 全波形の空間格子収束性詳細 ($z \in [0, 50\text{ m}]$, $M=2048$)

| 評価項目 | $N=128 \to 256$ | $N=256 \to 512$ | 観測次数 / スケーリング | 評価解釈 |
| :--- | :---: | :---: | :---: | :--- |
| **CurrentFCT $L_1$ 隣接差分** | 0.0021 m | 0.0009 m | 観測次数 $p = 1.30$ | 差分の減少傾向 (収束傾向) |
| **CurrentFCT $L_2$ 隣接差分** | 0.0046 m | 0.0021 m | 観測次数 $p = 1.11$ | 差分の減少傾向 (収束傾向) |
| **CurrentFCT $L_\infty$ 隣接差分** | 0.0274 m | 0.0175 m | 最大波高差が半減 | 収束傾向 |
| **CurrentFCT 浅瀬到達時刻 ($z=30$ m)** | 11.85 s ($N=128$) | 11.29 s ($N=256$) | 11.05 s ($N=512$) | 到達時刻の差 0.24 s 以内に収束 |
| **FilterOff 峰間隔 $\Delta z_{\text{peak}}$** | **3.48 m** ($N=128$) | **1.38 m** ($N=256$) | **0.66 m** ($N=512$) | **格子幅に比例して縮小** |
| **FilterOff 峰間隔 / 格子幅比** | **8.9 $\Delta z$** | **7.1 $\Delta z$** | **6.8 $\Delta z$** | **約 $7 \sim 9$ 格子幅で推移 (数値成分示唆)** |

## 4. 本体実装とベンチマークのバッファ形式対応表

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

## 5. ケース別客観的測定結果一覧（全21ケース）

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

## 6. 実効水深の直接測定詳細（全41,984面 GPU Readback 照合）

- **測定方法**: `AsyncGPUReadback` により ARGBHalf の生テクスチャを読み戻し、さらに GPU カーネル `CSEvaluateSampleDepth` をディスパッチしてソルバーが参照する実効水深を RFloat として直接照合。
- **深水域 ($z=0$)**: 指定値 = 0.6000000m, 格納値 = 0.6000977m (差分 +0.0000976m), 実効値 = 0.6000977m, クランプ変化 = 0.0000000m
- **浅瀬棚 ($z=L$)**: 指定値 = 0.2000000m, 格納値 = 0.1999512m (差分 -0.0000488m), 実効値 = 0.2000000m, クランプ変化 = 0.0000488m
- **全棚領域 (41984面) における最大クランプ変化量**: 0.0000488m (クランプによる強制変形皆無)

## 7. 急勾配2階散逸切替の単体検証 (合成ステップ入力)

- **検証結果**: **PASS (正常作動確認)**
- **詳細**: MaxDiff: 0.2026, Enabled Kappa: 0.1200 (65 faces), Disabled Kappa: 0.0000 (0 faces)
- `_DisableSlopeDissipation = 1.0` において、X/Z両方向で kappa2 が厳密に 0.0 になることを GPU 実機で確認。

## 8. 歴代コミットの実装実態と初期版の比較整理

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

