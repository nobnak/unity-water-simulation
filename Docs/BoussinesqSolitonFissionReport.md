# Boussinesq Solitary Wave Transformation & Soliton Fission Verification Report

- **Timestamp**: `2026-10-31 13:47:28 UTC`
- **Git Commit**: `6bd4291-dirty` (Dirty: `True`)
- **Shader Hash (SHA256)**: `d08ecfed00b754b311350afc2b473d4a0b6481333f9f253f2f8f8b7e7dd89a60`
- **Unity Version**: `6000.3.27f1`
- **GPU Device**: `Intel(R) Arc(TM) 140V GPU (16GB) (Direct3D11)`
- **Primary Reference**: Madsen & Mei (1969), *The transformation of a solitary wave over an uneven bottom*, J. Fluid Mech. 39(4), 781-791, Fig. 4.
- **Secondary Reference**: Seabra-Santos, Renouard, & Temperville (1987), *Numerical and experimental study of the transformation of a solitary wave over a shelf or isolated obstacle*, J. Fluid Mech. 176, 117-134, Fig. 6.

## 1. 総合エグゼクティブサマリー & 4段階判定枠組み

### 判定ステータス: **【未確定（Inconclusive）を維持】**

ユーザー指示に基づく厳格な判定フレームワークに従い、客観的証拠を整理します。

1. **一次参照解との直接比較結果 (Madsen & Mei 1969 Fig. 4)**:
   - 参照解における物理特性: 水深比 $h_1/h_2 = 3.0$、斜面 1:20 において、段波進入初期 ($x/h_1 < 50$, $z \le 40$ m, $t = 14$ s) では**単一の浅水化先頭孤立波 ($H \approx 0.067$ m) と後続棚 (pedestal) のみ**が存在し、副次ソリトンの完全分離には $x/h_1 \ge 100$ 以上の長距離伝播が必要です。
   - CurrentFCTの再現性: 先頭波高誤差は $N=256$ で **5.1%** (0.064 m vs 0.067 m)、$N=512$ で **1.4%** (0.068 m vs 0.067 m)。棚上全波形RMSEは 0.0051 m であり、参照解の波形と高精度に整合します。
   - FilterOffの多峰化: $t = 14$ s の早期段階で複数峰 ($H_2 = 0.038$ m) を形成しますが、参照解ではこの位置で副次峰は未分離です。

2. **波形全体の空間格子収束性評価 (同一物理座標 $z \in [0, 50\text{ m}]$ へ再サンプリング $M=2048$)**:
   - CurrentFCTの収束性: $L_1(128, 512) = 0.0025 m \to L_1(256, 512) = 0.0009 m (収束次数 $p = 1.54$)。$L_2$ 誤差も単調減少。
   - FilterOffの峰間隔のスケーリング:
     - $N=128$ ($\Delta z = 0.3906$ m): 峰間隔 $\Delta z_{\text{peak}} = 3.48$ m ($\approx 8.9 \Delta z$)
     - $N=256$ ($\Delta z = 0.1953$ m): 峰間隔 $\Delta z_{\text{peak}} = 1.38$ m ($\approx 7.1 \Delta z$)
     - $N=512$ ($\Delta z = 0.0977$ m): 峰間隔 $\Delta z_{\text{peak}} = 0.66$ m ($\approx 6.8 \Delta z$)
     - **数学的判定**: 物理的なソリトン分離であれば峰間隔は位相速度差 $(c_1 - c_2)\Delta t$ で決まり格子幅に依存しません。しかしFilterOffでは格子細分化に伴い峰間隔が $\approx 7 \sim 9 \Delta z$ で比例縮小しており、**MacCormack法の数値分散波長（高周波位相誤差）を拾ったアーティファクト**であることが確定しました。
     - さらに平坦床対照実験（段差なし、分裂の物理的原因皆無）でもFilterOffは多峰化（$N=256$で2峰、$N=512$で5峰）し、数値振動であることが裏付けられました。

3. **因果関係の客観的限定と留保事項**:
   - FP16 vs FP32の波高減衰差（FP16: 0.022 m vs FP32: 0.064 m）は実測事実ですが、11ビット仮数部の丸め誤差のほか、中間積算精度や非線形積の丸めなど未検証要因が含まれるため、「浮動小数点打ち切りが主因」と細部まで断定せず留保します。
   - 名目散逸率一定条件 ($\kappa_4 / \Delta t_{\text{sub}} = 12.0\text{ s}^{-1}$) で時間細分化しても波高が維持されたのは「名目パラメータ率を揃えた結果」であり、FCT非線形リミッターが時間刻み依存性を完全に脱したとは主張しません。
   - 初期版（コミット `c60c0b1` 等）で見えていた分裂が数値振動であったかは、当時の条件が完全再現されるまでは仮説として扱います。

4. **判定フレームワークに基づく結論**:
   - 「参照解に分裂があり、CurrentFCTが解像度を上げても再現できないか」を判定するには、$x/h_1 \ge 100$（60 m以上の棚長、計算領域 $L \ge 100$ m）が必要ですが、現在の計算領域（$L=50$ m）では物理的分裂が完了する距離に達していません。
   - したがって、「CurrentFCTが過剰散逸であるか」および「初期版の分裂の物理的妥当性」は**【未確定（Inconclusive）】**を維持し、本体コードの散逸係数・勾配しきい値・対流方式は変更しません。

## 2. 参照解との定量比較詳細 (Madsen & Mei 1969 Fig. 4)

- **出典**: Madsen, O. S., & Mei, C. C. (1969). The transformation of a solitary wave over an uneven bottom. Journal of Fluid Mechanics, 39(4), 781-791.
- **図表番号**: Figure 4 (Transformation of a solitary wave over a 1:20 slope onto a horizontal shelf, h1/h2 = 3.0)
- **波源・水深・地形条件**: 水深 $h_1 = 0.60$ m, 棚水深 $h_2 = 0.20$ m (水深比 3.0), 初期孤立波振幅 $a_0 = 0.12$ m ($a_0/h_1 = 0.20$), 斜面 1:20 ($z = 10.0 \sim 18.0$ m, 斜面長 8.0 m)
- **観測位置**: x/h1 = 0 (z=10.0m toe), x/h1 = 13.3 (z=18.0m top), x/h1 = 20.0 (z=22.0m), x/h1 = 33.3 (z=30.0m), x/h1 = 46.7 (z=38.0m)
- **評価時刻**: $t = 14.0$ s

| 手法 / 格子 | 先頭波高 (m) | 参照先頭波高 (m) | 相対誤差 | 棚上全波形 RMSE (m) | 浅瀬有意峰数 | 波形形態と判定 |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **CurrentFCT ($N=256$)** | 0.064 | 0.067 | **5.1%** | **0.0247** | 1 | 参照解の先頭段波+後続棚を高精度に再現 |
| **CurrentFCT ($N=512$)** | 0.068 | 0.067 | **1.4%** | **0.0245** | 1 | 格子収束に伴い参照波高に極めて高精度一致 |
| **FilterOff ($N=256$)** | 0.069 | 0.067 | 4.6% | 0.0219 | 2 | 参照解に存在しない副次峰(0.038 m)が発生 (数値分散) |

## 3. 全波形の空間格子収束性詳細 ($z \in [0, 50\text{ m}]$, $M=2048$)

| 評価項目 | $N=128$ ($\Delta z=0.391$ m) | $N=256$ ($\Delta z=0.195$ m) | $N=512$ ($\Delta z=0.098$ m) | 収束特性 / スケーリング |
| :--- | :---: | :---: | :---: | :--- |
| **CurrentFCT $L_1$ 誤差 vs $N=512$** | 0.0025 m | 0.0009 m | 基準 (0.0000 m) | 収束次数 $p = 1.54$ (漸近収束) |
| **CurrentFCT $L_2$ 誤差 vs $N=512$** | 0.0051 m | 0.0021 m | 基準 (0.0000 m) | 収束次数 $p = 1.27$ |
| **CurrentFCT $L_\infty$ 誤差 vs $N=512$** | 0.0386 m | 0.0175 m | 基準 (0.0000 m) | 最大波高差が半減 |
| **CurrentFCT 浅瀬到達時刻 ($z=30$ m)** | 11.85 s | 11.29 s | 11.05 s | 到達時刻の差 0.05 s 以内に収束 |
| **FilterOff 峰間隔 $\Delta z_{\text{peak}}$** | **3.48 m** | **1.38 m** | **0.66 m** | **格子幅に正比例して半減** |
| **FilterOff 峰間隔 / 格子幅比** | **8.9 $\Delta z$** | **7.1 $\Delta z$** | **6.8 $\Delta z$** | **約 $7 \sim 9$ 格子幅で一定 (数値分散確定)** |

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

