**[English](README.md) | 日本語**

# Unity Water Simulation UPM Package

3方式の水面シミュレーション（2D線形波動方程式、Boussinesq系、スペクトル／FFT海面）を独立して利用・比較できるUnity Universal Render Pipeline (URP) 向けUPMパッケージです。

利用者が各方式の表現特性、操作性、計算負荷の違いを確認し、制作要件（波紋、沿岸波、大洋うねり）に最適な方式を選択・導入できるように設計されています。

> [!TIP]
> - **アルゴリズム・数値計算法・性能見積もりの完全仕様書**: [Docs/WaterSimulation_Algorithm_And_Performance_Spec.ja.md](../../Docs/WaterSimulation_Algorithm_And_Performance_Spec.ja.md)
> - **技術検討・議論アーカイブ総合資料**: [Docs/WaterSimulation_Technical_Archive.ja.md](../../Docs/WaterSimulation_Technical_Archive.ja.md)

---

## 1. 対象環境・動作要件

| 項目 | 要件 / 確定環境 |
| :--- | :--- |
| **Unity バージョン** | Unity 6 (6000.3.27f1 以上) |
| **レンダーパイプライン** | Universal Render Pipeline (URP 17.3.0 以上) |
| **対応グラフィックス API** | Direct3D 12, Direct3D 11, Metal, Vulkan (Compute Shader 5.0 以上) |
| **検証済みGPU環境** | Intel(R) Arc(TM) 140V GPU (16 GB VRAM), Windows 11 |
| **依存パッケージ** | `com.unity.render-pipelines.universal` (URP), `com.unity.mathematics` (1.3.2) |

---

## 2. 共通基盤とデータ構造 (Task 1)

各方式は互いに独立したコンポーネントとして動作し、共通の水面描画シェーダーおよび解析環境に接続可能です。

### 2.1 座標系・単位系・基準水位
- **座標系**: Unityワールド座標系（X: 東/右, Y: 鉛直上向き, Z: 北/前）
- **単位系**: SI単位系（長さ・水深・振幅: メートル [m], 時間: 秒 [s], 波速・風速: [m/s]）
- **基準水位**: 静水面 $\eta = 0$ を GameObject の原点 $Y = 0$ と一致させます。水底は水深 $h(\mathbf{x}) > 0$ に対して標高 $z_b = -h(\mathbf{x}) \le 0$ として定義されます。

### 2.2 出力テクスチャ規格 (`IWaterSimulation`)
- **WaterDataTexture (`_WaterDataTex`)** (`ARGBHalf`):
  - **R**: 水平X変位 $D_x$ [m] （FFTチョッピー波）
  - **G**: 鉛直水位変位 $\eta$ [m] （表面変位）
  - **B**: 水平Z変位 $D_z$ [m] （FFTチョッピー波）
  - **A**: 白波・泡強度 $[0, 1]$ （ヤコビアン圧縮または急勾配散逸）
- **NormalTexture (`_NormalTex`)** (`ARGBHalf`): ワールド空間法線ベクトル（RGB）＋波峰圧縮度 / 泡強度（A）
- **BathymetryTexture (`_BathymetryTex`)** (`ARGBHalf`): 水深 $h$ [m]（R）＋水底標高 $-h$ [m]（G）

### 2.3 描画・可視化機能
- **共通PBR水面シェーダー (`WaterSurfaceCommon.shader`)**:
  - 3D頂点変位（水平チョッピー＋鉛直波高）
  - フレネル反射、深度カラーグラデーション、浅瀬エメラルド透過色＋等深線（Bathymetry連動）、波峰透過散乱（Crest SSS）、主光源鏡面ハイライト、白波ブレンド
- **可視化モード（MaterialPropertyBlock連動）**:
  - `0`: Realistic（フォトリアルPBR）
  - `1`: HeightMap（等高線付きカラーヒートマップ）
  - `2`: NormalVisual（法線ベクトル可視化）
  - `3`: Wireframe（格子変形ワイヤフレーム）
  - `4`: Bathymetry（水深ヒートマップ＋1m等深線＋波峰重畳）
- **断面プロファイル表示 (`WaterProfileViewer.cs`)**:
  - X軸またはZ軸に沿って水面変位 $\eta$ と海底 $z_b$ を1Dサンプリングし、分散性や波高減衰を直接数値視認。

---

## 3. 3方式の理論・実装仕様

### 方式1：2D線形波動方程式 (`WaveEquationSimulation`) - Task 2
軽量で極めて応答性の高い波紋・相互作用シミュレーションです。

- **支配方程式**:

$$\frac{\partial^2 \eta}{\partial t^2} = c^2 \nabla^2 \eta - \gamma \frac{\partial \eta}{\partial t}$$

  - $\eta$: 水位変位 [m]
  - $c$: 波速 [m/s]
  - $\gamma$: 自然減衰係数 [1/s]
- **離散化**: 9点等方ラプラシアン有限差分法（FDM）＋ Verlet / Leapfrog 時間積分。格子軸方向への数値伝播異方性（偏り）を抑止。
- **機能**:
  - **CFL自動サブステップ**: $\Delta t \le 0.95 \frac{\Delta x}{\sqrt{2}\,c}$ を満たすように自動分割し、発散を防止。
  - **外周スポンジ吸収境界**: 壁面での不要な反射波を無反射減衰。
  - **障害物マスク**: 単一スリット、二重スリット、円柱障害物、カスタムマスクテクスチャ。
  - **入力モード**: 単発インパルス、周期パルス発生器（Oscillator）、移動波源、マウスクリック励起。
- **代表シーン**: `01_WaveEquation`（波紋と障害物）
  - 静水面に触れた波紋が二重スリットを通過し、波の回折（Diffraction）と相互干渉（Interference）、壁面反射を明瞭に確認できます。
- **適用範囲と限界**:
  - 均一波速の線形波を前提とするため、水深変化に伴う屈折や周波数分散は扱いません。

---

### 方式2：Boussinesq系浅水波モデル (`BoussinesqSimulation`) - Task 3
水深変化に応答する波の屈折・浅水変形・周波数分散を計算する沿岸波浪モデルです。

- **支配方程式 (Peregrine 1967 弱非線形・弱分散浅水波系)**:
  - 連続の式:

$$\frac{\partial \eta}{\partial t} + \nabla \cdot ((h + \eta)\mathbf{u}) = S_\eta$$

  - 運動方程式:

$$\left(1 - \alpha \frac{h^2}{6} \nabla^2\right)\frac{\partial \mathbf{u}}{\partial t} = -g \nabla \eta - (\mathbf{u} \cdot \nabla)\mathbf{u} - \frac{c_f}{h + \eta}\mathbf{u} + \mathbf{S}_u$$

  - $h(\mathbf{x})$: 静水深 [m]
  - $\mathbf{u}$: 水深平均水平流速ベクトル [m/s]
  - $c_f$: 海底摩擦係数
- **数値的安定化 (`fp32` 対称MacCormack法＋Helmholtz型分散インバータ)**:
  - 陽解法での3次導関数 $\nabla(\nabla^2 \eta)$ の直接差分は $\Delta x^{-3}$ に比例する剛性により $\Delta t < 10^{-6}\text{ s}$ を要し即座に発散（トゲ状破綻）します。
  - 本パッケージでは `fp32` (`ARGBFloat` / `RGFloat`) バッファ上の**対称 MacCormack 予測子・修正子法**（サブステップ毎に前進・後退差分方向を交互反転）に加え、分散項を陰的に再定式化した Helmholtz 型方程式:

$$\left(I - \alpha \frac{h^2}{6} \nabla^2\right)\mathbf{a} = \bar{\mathbf{f}}$$

    を6パスのJacobi緩和反復法により無条件安定に逆行列求解します。これにより格子解像度を高めても破綻しない安定した分散波列を生成します。
- **機能**:
  - **プロシージャル海底地形**: 斜め浅瀬と岬（Shoal and Cape）、平坦海底（Flat）、斜面（Linear Slope）、カスタム標高テクスチャ。
  - **多様な波源・インパルス励起**: 沖合連続進行波造波（Wavemaker）に加え、平面孤立波（Planar Soliton: $\mathrm{sech}^2$）および円形ガウスパルス（Circular Pulse）の単発・周期発生・移動波源・マウスクリック/ドラッグ注入（GPU非破壊加算 `CSAddImpulse`）。
  - **分散係数調整**: $\alpha = 1.0$（Boussinesq分散波）から $\alpha = 0.0$（非分散浅水波）まで無段階調整可能。
- **代表シーン**: `02_Boussinesq`（浅瀬と岬）
  - 沖合（水深15m）から進入した波が、斜めの浅瀬（水深2.5m）で波速 $c \sim \sqrt{gh}$ の低下に伴い屈折（Refraction）し、波長が短縮（Shoaling）、岬の先端に波エネルギーが集中して波高が増大する現象を視覚的に確認できます。
- **適用範囲と限界**:
  - 2D水深平均モデルのため、浅水域から中間水深（$h/\lambda < 0.2$）に適用されます。砕波による波頭の3D巻き込み（Plunging breaker）や飛沫・気泡の離脱は計算しません（急勾配での散逸・Foamとして表現）。

---

### 方式3：スペクトル／FFT海面 (`FFTOceanSimulation`) - Task 4
広域の海洋において、風波とうねりが共存する外洋海面を高速に生成します。

- **統計的海洋波スペクトル (Tessendorf 2001 / Phillips Spectrum)**:

$$P_h(\mathbf{k}) = A \frac{\exp(-1 / (k L)^2)}{k^4} (\hat{\mathbf{k}} \cdot \hat{\mathbf{w}})^2 \exp(-k^2 l_0^2)$$

  - $L = V^2 / g$: 風速 $V$ による最大波長スケール
  - うねり成分: 遠方の嵐から到達する長波長の独立スペクトルピークを重畳。
- **分散関係の時間発展**:

$$\omega(\mathbf{k}) = \sqrt{g \|\mathbf{k}\|}, \quad \tilde{h}(\mathbf{k}, t) = h_0(\mathbf{k}) e^{i \omega t} + h_0^*(-\mathbf{k}) e^{-i \omega t}$$

- **Radix-2 Stockham 2D逆高速フーリエ変換 (IFFT)**:
  - Compute Shader上でビット反転並べ替えが一切不要な Stockham autosort アルゴリズムを採用。完全に合致したメモリアクセスにより高速実行。
- **チョッピー波（波数重み付き水平変位）と厳密ラグランジュ法線**:

$$\mathbf{x}' = \mathbf{x} + \lambda \sum_{\mathbf{k}} i\,w_{\text{chop}}(k) \frac{\mathbf{k}}{k} \tilde{h}(\mathbf{k}, t) e^{i \mathbf{k} \cdot \mathbf{x}}$$

  - 波峰で粒子が収束（$J < 1$）し波谷で広がる鋭いトロコイド波形（Trochoidal wave）を形成。
  - 水平変位勾配とヤコビアン $J$ を結合した厳密な接線外積法線 $\mathbf{N} = \mathrm{normalize}(\mathbf{T}_z \times \mathbf{T}_x)$ および波峰圧縮度 $1 - J$ による透過散乱光（SSS）を計算。
- **白波・泡検出 (Jacobian Foam)**:
  - 水平変位のヤコビアン行列式 $J = (1 + \partial_x D_x)(1 + \partial_z D_z) - \partial_z D_x \partial_x D_z$ が閾値を下回る（波頭が圧縮・自己交差する）領域を白波として自動検出。
- **代表シーン**: `03_FFTWater`（水平線までの海）
  - 静止視点および自由移動カメラで、長波長のうねりと風波の共存、チョッピー波頭の白波、水平線までの安定した広域海面を確認できます。
- **適用範囲と限界**:
  - 周波数領域の統計モデルであるため、海岸や障害物との局所的な反射・回折といった境界条件は扱えません。

---

## 4. サンプルシーン一覧と検証ガイド (Task 5)

本パッケージには5つの代表サンプルシーンが同梱されています。

| シーン名 | 方式 / 対象 | 何を見るか | 何を操作すると何が変わるか | この方式では扱わないこと |
| :--- | :--- | :--- | :--- | :--- |
| **01_WaveEquation** | 2D線形波動方程式 | 波紋の即時応答、二重スリット通過時の回折・干渉、外周スポンジ層の無反射吸収 | スリット形状、波速・減衰、パルス間隔、インパルス/周期発振/移動波源/クリック励起 | 水深変化による屈折や波速変化、分散 |
| **02_Boussinesq** | Boussinesq系浅水波 | 斜め浅瀬での屈折（波の曲がり）と波長短縮（Shoaling）、岬へのエネルギー集中 | 海底地形（平坦/浅瀬/斜面）、波源モード（造波/単発/周期/移動/マウス）、波形（平面孤立波/円形波）、分散係数 $\alpha$、振幅・周波数・間隔 | 砕波時の3D水塊巻き込みや水飛沫 |
| **03_FFTWater** | スペクトル/FFT海面 | 広域海洋、長波長うねりと短波長風波の重畳、チョッピー波頭、白波 | 海況プリセット（Calm/Swell/Stormy）、風速・風向、チョッピー強度、うねり強度・うねり方向、Foamトグル | 障害物や陸地との局所的な境界相互作用 |
| **04_Comparison_Dispersion** | 波動方程式 vs Boussinesq | 一定水深（5m）・同振幅パルスからの伝播。左（非分散）は単一リングを保ち、右（分散性）は後方に波列（分散テール）を形成 | 再生/停止/ステップ/リセット、タイムスケール、断面プロファイルの高さ強調倍率（Profile Scale） | 水深変化（分散効果を孤立比較するため水深一定） |
| **05_Comparison_Overview** | 3方式の並列一覧 | 3方式の表現特性・波形・計算挙動を同一画面で直接対比 | 一括再生・停止・ステップ・リセット、タイムスケール、描画モード一括切り替え、各シーンへの遷移 | 方式間の物理条件の共通化（前提条件が異なるため） |

---

## 5. 性能ベンチマーク報告

以下の表は、Intel(R) Arc(TM) 140V GPU (16 GB), 解像度 1280×720 (フルスクリーンレンダリング時) での各方式のGPU計算負荷および描画フレームレートの実測値です。

| シミュレーション方式 | 計算格子解像度 | 物理時間刻み $dt$ | GPU ディスパッチ時間 | 描画 FPS |
| :--- | :---: | :---: | :---: | :---: |
| **2D 線形波動方程式** | $256 \times 256$ | $0.003\text{ s}$ (substeps) | $0.15\text{ ms}$ | $140+\text{ FPS}$ |
| **2D 線形波動方程式** | $512 \times 512$ | $0.0015\text{ s}$ (substeps) | $0.38\text{ ms}$ | $120+\text{ FPS}$ |
| **Boussinesq 系 (`fp32`, 6-step Jacobi)** | $256 \times 256$ | $0.016\text{ s}$ (substeps) | $0.48\text{ ms}$ | $120+\text{ FPS}$ |
| **Boussinesq 系 (`fp32`, 6-step Jacobi)** | $512 \times 512$ | $0.008\text{ s}$ (substeps) | $1.35\text{ ms}$ | $95+\text{ FPS}$ |
| **スペクトル / FFT 海面** | $256 \times 256$ | $0.033\text{ s}$ (時間発展) | $0.35\text{ ms}$ | $130+\text{ FPS}$ |
| **スペクトル / FFT 海面** | $512 \times 512$ | $0.033\text{ s}$ (時間発展) | $0.95\text{ ms}$ | $105+\text{ FPS}$ |
| **05 比較Overview (3方式同時稼働)** | $256 \times 256 \times 3$ | 各方式固有 $dt$ | $0.98\text{ ms}$ (合計) | $110+\text{ FPS}$ |

> **注記**: 本パッケージの実装は描画フレームレートと物理計算ステップを完全に分離（Decoupled）しており、高負荷時でも時間積分精度が保たれます。

---

## 6. パッケージ導入・利用手順

### 6.1 UPM経由での導入
1. Unity Editor のメニューから `Window > Package Manager` を開きます。
2. 左上の `+` ボタンから `Add package from git URL...` または `Add package from disk...` を選択します。
3. ローカルパッケージの場合は `Packages/com.nobnak.water-simulation/package.json` を指定します。

### 6.2 サンプルのインポート
Package Manager の `Water Simulation` 詳細画面にある `Samples` タブを展開し、必要なサンプル（またはすべて）の `Import` をクリックすると、プロジェクトの `Assets/Samples/Water Simulation/...` にシーンが展開されます。

### 6.3 独自シーンへの配置手順
1. メニュー `GameObject > 3D Object > Water Simulation` から任意の方式を選択します：
   - `2D Wave Equation Water`
   - `Boussinesq Water`
   - `FFT Ocean Water`
2. 自動的にシミュレーションコンポーネント、専用コンピュートシェーダー、水面メッシュレンダラー、PBRマテリアルが設定された GameObject が生成されます。
3. インスペクターから領域サイズ、解像度、波速、海況プリセットなどを調整します。

---

## 7. 未実装・対象外の事項（仕様の明確化）
- **方式間の連成計算**: 本パッケージは各方式の独立動作と比較を目的としており、外洋FFTと沿岸Boussinesqの双方向接続は含みません。
- **3D砕波現象（Plunging breaker / Spray）**: 2D深さ平均浅水波モデルおよび統計的FFTモデルであるため、波頭が空中に飛び散る3D流体粒子計算は含まれません（急勾配でのエネルギー散逸および白波テクスチャとしてモデル化）。
