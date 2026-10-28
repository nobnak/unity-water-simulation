**[English](WaterSimulation_Technical_Archive.md) | 日本語**

# Unity Water Simulation 実装・技術検討・議論アーカイブ総合資料

本資料は、[`com.nobnak.water-simulation`](../Packages/com.nobnak.water-simulation/package.json)（v0.1.0）の Git コミット履歴・コードベース実装仕様と、開発セッションで実施された技術議論・数値発散の原因調査・設計判断・拡張ロードマップを漏れなく体系化したリファレンス資料です。

---

## 1. セッション・コミット全体像

### 1.1 Antigravity 開発セッション一覧

| セッションID | 期間 (JST) | ステップ数 | ターン数 | 主な内容 |
| :--- | :--- | :---: | :---: | :--- |
| `a86b2a57-1d29-4f49-85f0-ba4e54baacd7` | 2026-10-26 14:27 〜 10-27 17:56 | 1,669 | 12 | パッケージ初期実装、入力系修正、Boussinesq数値安定化、描画モード修正、UI拡充、パルス波源化、不要コード掃除、および今後の拡張設計（LOD・自由境界・02シーン拡張） |
| `c95e553c-8ef9-4ea9-bd31-e34bfc91280f` | 2026-10-27 20:25 〜 10-27 20:27 | 44 | 1 | コミット履歴およびモジュール別の実装到達度調査 |
| `71f435e1-4ae7-47d9-8753-a7b8ea532229` | 2026-10-27 20:30 〜 10-28 00:50 | 410+ | 10 | アーカイブ資料化、`02_Boussinesq` 波源・水深可視化実装、`fp32` 対称MacCormack＋FFT Choppy強調、全UI監査・接続、アルゴリズム＆性能見積もり完全仕様書の作成・日英両対応 |

### 1.2 Gitコミット履歴との対応

| コミット | 日時 (JST) | 種別 | 対応ターン | 概要 |
| :--- | :--- | :---: | :---: | :--- |
| [`ad63e8b`](../.gitignore) | 10/26 14:25 | `init` | - | 初回コミット（`.gitignore`） |
| [`7c392cc`](../Packages/com.nobnak.water-simulation/package.json) | 10/27 12:12 | `feat` | Turn 1〜2 | 3方式の水面シミュレーションモデル、5サンプルシーン、New Input System 対応の一括追加 |
| [`484f35f`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute) | 10/27 13:17 | `fix` | Turn 3〜4 | Boussinesq の MacCormack スキームおよび進行波ソース造波による数値安定化 |
| [`655ecfa`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader) | 10/27 14:08 | `fix` | Turn 5〜6 | Uniform 動的分岐によるリアルタイム描画モード切り替えの有効化 |
| [`fa78e4d`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterSampleUI.cs) | 10/27 15:29 | `feat` | Turn 7〜9 | 障害物・波源励起・物理パラメータの対話的 UI コントロール追加 |
| [`45f486d`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquationSimulation.cs) | 10/27 15:59 | `feat` | Turn 10 | 2D波動方程式における固定持続時間・周期インターバル制御パルス発生器の実装 |
| [`56a6495`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute) | 10/27 16:24 | `chore` | Turn 11 | 未使用オシレータフィールド・廃止カーネルの削除および FFT コンピュートインデックス計算の最適化 |
| [`2494a35`](WaterSimulation_Technical_Archive.ja.md) | 10/27 21:05 | `docs` | Turn 13 | 技術検討・議論アーカイブ総合資料の作成 |
| [`acbff9c`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute) | 10/27 22:41 | `feat` | Turn 14 | `02_Boussinesq` の波源・平面孤立波/円形パルス励起モード拡張および海底地形（Bathymetry）の2方式同時可視化 |
| [`50073b4`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute) | 10/27 23:19 | `feat` | Turn 15 | Boussinesq の `fp32` 化・対称 MacCormack 精度改善および FFT 海洋の厳密ラグランジュ法線・波峰 SSS による Choppiness 強調 |
| [`e2809a9`](WaterSimulation_Algorithm_And_Performance_Spec.ja.md) | 10/27 23:51 | `docs` | Turn 16 | アルゴリズム＆性能見積もり完全仕様書の追加および未接続UIコントロール（風向・うねり方向・断面高さ倍率・一括同期）の統合 |

> [!IMPORTANT]
> **アルゴリズム・数値計算法・性能見積もりの完全仕様書**:
> 現在の全アルゴリズムの数理定式化、離散化ステンシル、テクスチャ構造、および VRAM・メモリ帯域・GPU実行時間の見積もりモデルについては **[WaterSimulation_Algorithm_And_Performance_Spec.ja.md](WaterSimulation_Algorithm_And_Performance_Spec.ja.md)** を参照してください。

---

## 2. アーキテクチャと共通データ規格

### 2.1 クラス階層とコンポーネント構成

```mermaid
classDiagram
    class IWaterSimulation {
        <<interface>>
        +Vector2 AreaSize
        +Vector2Int Resolution
        +float SimulationTime
        +RenderTexture WaterDataTexture
        +RenderTexture NormalTexture
        +RenderTexture BathymetryTexture
        +Play()
        +Pause()
        +Step(float dt)
        +ResetSimulation()
    }
    class WaterSimulationBase {
        <<abstract>>
        #Vector2 areaSize
        #Vector2Int resolution
        #float fixedTimeStep
        #float timeScale
        #InitializeTextures()*
        #OnStepSimulation(float dt)*
        #OnResetSimulation()*
    }
    class WaveEquationSimulation {
        +ObstaclePattern Pattern
        +ExcitationMode SourceMode
        +float WaveSpeed
        +float Damping
        +float PulseInterval
        +TriggerImpulse(Vector2 uv, float radius, float strength)
    }
    class BoussinesqSimulation {
        +BathymetryType Bathymetry
        +InitialConditionType Condition
        +ImpulseShapeType ImpulseShape
        +float DispersionAlpha
        +Texture2D CustomBathymetryTexture
        +TriggerImpulse(Vector2 centerNorm, float amp, float radiusNorm)
    }
    class FFTOceanSimulation {
        +SeaStatePreset SeaState
        +float WindSpeed
        +float WindDirectionDegrees
        +float Choppiness
        +float SwellStrength
        +float SwellDirectionDegrees
        +bool EnableFoam
        +ApplySeaStatePreset(SeaStatePreset preset)
    }
    class WaterRenderer {
        +VisualMode Mode
        +SetVisualMode(VisualMode mode)
    }
    class WaterProfileViewer {
        +SliceAxis Axis
        +float SlicePosition
        +float VerticalScale
    }

    IWaterSimulation <|.. WaterSimulationBase
    WaterSimulationBase <|-- WaveEquationSimulation
    WaterSimulationBase <|-- BoussinesqSimulation
    WaterSimulationBase <|-- FFTOceanSimulation
    WaterRenderer --> IWaterSimulation : reads textures
    WaterProfileViewer --> IWaterSimulation : samples 1D slice
```

### 2.2 出力 RenderTexture フォーマット規格 ([`IWaterSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/IWaterSimulation.cs))

- **座標系・単位**: ワールド座標系（XZ水平面、+Y鉛直上向き）、SI単位系（m, s）、静止基準水位 $y = 0$。

| テクスチャ名 | フォーマット | R チャンネル | G チャンネル | B チャンネル | A チャンネル |
| :--- | :---: | :--- | :--- | :--- | :--- |
| **`WaterDataTexture`** (`_WaterDataTex`) | `ARGBHalf` | 水平X変位 $D_{x}$ [m] | 鉛直変位（波高） $\eta$ [m] | 水平Z変位 $D_{z}$ [m] | 白波・泡強度 $\mathrm{Foam} \in [0, 1]$ |
| **`NormalTexture`** (`_NormalTex`) | `ARGBHalf` | 法線 $N_{x}$ | 法線 $N_{y}$ | 法線 $N_{z}$ | 波峰圧縮度 / 泡強度 $\in [0, 1]$ |
| **`BathymetryTexture`** (`_BathymetryTex`) | `ARGBHalf` | 静水深 $h(\mathbf{x})$ [m] | 水底標高 $z_{b} = -h(\mathbf{x})$ [m] | $0$ | $1$ |

---

## 3. 実装・解決済みの議論と数理仕様詳細（Turn 1〜16）

### 3.1 初期設計・3方式UPMパッケージの構築（Turn 1 / コミット `7c392cc`）

- **確定環境**: Unity 6 (`6000.3.27f1`), URP `17.3.0`, Compute Shader 5.0+, Intel(R) Arc(TM) 140V GPU (16 GB VRAM)。

#### 各方式の実装と性能ベンチマーク（1280×720）

| 方式・サンプルシーン | 実装クラス / シェーダー | 物理・数値モデル | 格子数 / 内部 $dt$ | GPU時間 / FPS |
| :--- | :--- | :--- | :---: | :---: |
| **01_WaveEquation**<br>「波紋と障害物」 | [`WaveEquationSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquationSimulation.cs)<br>[`WaveEquation.compute`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute) | 9点等方性ラプラシアンFDM＋半陰的減衰Leapfrog時間積分、CFL自動サブステップ、外周スポンジ吸収層、スリット障害物マスク | $256^{2}$ / $0.003\ \mathrm{s}$<br>$512^{2}$ / $0.0015\ \mathrm{s}$ | $0.15\ \mathrm{ms}$ / $140+$<br>$0.38\ \mathrm{ms}$ / $120+$ |
| **02_Boussinesq**<br>「浅瀬と岬」 | [`BoussinesqSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs)<br>[`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute) | Peregrine (1967) 弱非線形・弱分散浅水波モデル、`fp32` 対称MacCormack予測子・修正子法、6パスJacobi緩和によるHelmholtz分散インバータ $(I - \beta\nabla^{2})\mathbf{a} = \mathbf{f}$ | $256^{2}$ / $0.016\ \mathrm{s}$<br>$512^{2}$ / $0.008\ \mathrm{s}$ | $0.48\ \mathrm{ms}$ / $120+$<br>$1.35\ \mathrm{ms}$ / $95+$ |
| **03_FFTWater**<br>「水平線までの海」 | [`FFTOceanSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOceanSimulation.cs)<br>[`FFTOcean.compute`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute) | Tessendorf (2001) Phillipsスペクトル＋独立うねり（Swell）、Radix-2 Stockham 2D IFFT（Height $D_{y}$・Choppy $(D_{x},D_{z})$ の計2回）、厳密ラグランジュ法線＋ヤコビアン白波 | $256^{2}$ / $0.033\ \mathrm{s}$<br>$512^{2}$ / $0.033\ \mathrm{s}$ | $0.35\ \mathrm{ms}$ / $130+$<br>$0.95\ \mathrm{ms}$ / $105+$ |
| **04_Comparison_Dispersion** | [`WaterComparisonController.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterComparisonController.cs)<br>[`WaterProfileViewer.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterProfileViewer.cs) | 一定水深（5m）・同振幅ガウスパルスからの伝播比較（非分散の単一波形維持 vs Boussinesqの周波数分散波列テール） | $256^{2} \times 2$ | $115+\ \mathrm{FPS}$ |
| **05_Comparison_Overview** | 同上 | 3方式の横並び一括同期実行・比較ステージ | $256^{2} \times 3$ | $0.92\ \mathrm{ms}$ / $110+$ |

#### シェーダーカーネル構成と離散化数式（確定仕様）
- **2D波動方程式 ([`WaveEquation.compute`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute))**:
  - **9点等方性ラプラシアン**:

$$
\nabla^{2} \eta \approx \frac{4(\eta_{L} + \eta_{R} + \eta_{D} + \eta_{U}) + (\eta_{LD} + \eta_{RD} + \eta_{LU} + \eta_{RU}) - 20\eta_{C}}{6\Delta x^{2}}
$$

  - **半陰的減衰 Leapfrog スキーム**:

$$
\eta^{n+1} = \frac{2\eta^{n} - \eta^{n-1}(1 - \frac{1}{2}\gamma\Delta t) + c^{2}\Delta t^{2} \nabla^{2}\eta^{n}}{1 + \frac{1}{2}\gamma\Delta t}
$$

  - **インパルス注入 (`CSAddImpulse`)**: 現在変位 `state.r` と前ステップ変位 `state.g` の双方に同一ガウス分布 $A\exp(-3(r/R)^{2})$ を加算し、初速 $0$ の純粋な初期変位として励起。
- **Boussinesq系 ([`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute))**:
  - **7カーネル構成**: `CSInit` → `CSPredict`（サブステップ偶奇で前進/後退差分を交互切替）→ `CSCorrect`（逆向き差分＋平均）→ `CSJacobiStep`（6反復、$\beta = \mathrm{clamp}(\alpha h^{2} / (6\Delta x^{2}), 0, 6)$）→ `CSApplyDispersion` → `CSAddImpulse` → `CSComputeNormal`。
  - **進行波ソース造波 (`GetWavemakerSources`)**: ガウス幅 $\sigma = 2.5\Delta x$ で質量ソース $S_{\eta} = c W$ と運動量ソース $S_{w} = g W$（$c = \sqrt{gh}$）を同時注入し、両端スポンジ層への干渉を防ぐ横方向テーパー `smoothstep(0, _SpongeWidth * 1.5, lateralDist)` を適用。
- **FFT海面 ([`FFTOcean.compute`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute))**:
  - **5カーネル構成**: `CSGenerateSpectrum` → `CSTimeEvolution` → `CSFFTHorizontal` ($\log_{2} N$ 段) → `CSFFTVertical` ($\log_{2} N$ 段) → `CSPackOceanData`。

#### 代表シーン検証キャプチャ

| シーン | キャプチャ画像 |
| :--- | :--- |
| **01_WaveEquation** | ![01_WaveEquation_Active](../Captures/01_WaveEquation_Active.png) |
| **02_Boussinesq** | ![02_Boussinesq_Active](../Captures/02_Boussinesq_Active.png) |
| **03_FFTWater** | ![03_FFTWater_Active](../Captures/03_FFTWater_Active.png) |
| **04_Comparison_Dispersion** | ![04_Comparison_Active](../Captures/04_Comparison_Active.png) |
| **05_Comparison_Overview** | ![05_Overview_Active](../Captures/05_Overview_Active.png) |

---

### 3.2 パッケージ依存関係の精査と New Input System 対応（Turn 2 / コミット `7c392cc`）

- **課題**:
  1. エディタ上で Deprecated なパッケージが見えたため不要パッケージ混入の調査依頼。
  2. `01_WaveEquation` 起動時に `SimpleCameraController.HandleInput()` で `InvalidOperationException`（New Input System 環境下での旧 `UnityEngine.Input` 呼び出し）が発生。
- **調査結果と対応**:
  1. パッケージ側（[`package.json`](../Packages/com.nobnak.water-simulation/package.json)）の依存は `com.unity.mathematics`, `com.unity.render-pipelines.universal`, `com.unity.inputsystem` の3点のみでクリーンであることを確認。プロジェクト側の [`Packages/manifest.json`](../Packages/manifest.json) にある URP テンプレート初期パッケージ（`com.unity.ai.assistant`, `com.unity.ai.inference`, `com.unity.visualscripting` 等）の要否一覧を整理。
  2. [`SimpleCameraController.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/SimpleCameraController.cs) および [`WaveEquationSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquationSimulation.cs) に `#if ENABLE_INPUT_SYSTEM` 分岐を実装し、`Mouse.current` / `Keyboard.current` によるカメラ操作と水面クリック波源入力に対応（旧 Input Manager 環境へのフォールバックも維持）。

---

### 3.3 `02_Boussinesq` の数値発散原因調査と MacCormack 法による安定化（Turn 3〜4, 15 / コミット `484f35f`, `50073b4`）

#### 発散を引き起こしていた5つの要因（Turn 3）
1. **前進オイラー法（FTCS）の無条件不安定性【主因】**:
   双曲型浅水波系に対する前進時間差分＋中心空間差分は、フォン・ノイマン安定性解析において増幅係数 $|G(k)|^{2} = 1 + \left(c \frac{\Delta t}{\Delta x} \sin(k\Delta x)\right)^{2} > 1$ となり、CFL条件に関わらず高周波成分が指数関数的に増大しクランプ限界（$\pm 10\ \mathrm{m}$）に張り付いて矩形波発振を起こしていた。
2. **コロケート格子（Arakawa A-grid）の奇偶デカップリング（市松模様ノイズ）**:
   水位 $\eta$ と流速 $(u, w)$ を同一格子点に定義し中心差分をとるため、波長 $2\Delta x$ の交互ノイズ（$+1, -1, +1, -1$）に対する勾配が $0$ となり減衰しなかった。
3. **非線形移流項 $(\mathbf{u}\cdot\nabla)\mathbf{u}$ の風上化なし中心差分**:
   急勾配部で非線形エイリアシングにより最小格子スケールへエネルギーが集中。
4. **造波帯の強制 Dirichlet 上書き（`state.r = wave`）による段差ショック**:
   浅瀬から戻ってきた反射波が造波ラインで剛体壁のように再反射し、不連続段差から高周波ノイズが連続発生。
5. **深水部での Helmholtz 分散項 $\beta = \frac{\alpha h^{2}}{6\Delta x^{2}}$ の過大化**:
   Jacobi 反復では深水部（$h = 15\ \mathrm{m}$）の残差収束と初期値位相が重要となる。

#### 方針比較と実装内容（Turn 4, 15）
MacCormack法（方針A）、RK2＋4階超粘性（方針B）、スタガードC-grid化（方針C）を比較し、既存の ping-pong 構造を活かしつつ2階精度と適度な高周波減衰を両立する **方針A（対称 MacCormack 予測子・修正子法）** を採用・実装：
- [`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute):
  - **`CSPredict`＋`CSCorrect`（サブステップ偶奇で前進/後退を交互反転する対称 MacCormack 法）**: 前進・後退差分の誤差相殺により $|G| \le 1$ となり、$2\Delta x$ の市松模様ノイズが自然消滅し方向バイアスも完全にゼロ化。
  - **進行波内部ソース造波**: リーマン不変量に基づき質量ソース $S_{\eta}$ と前進運動量ソース $S_{w} = \sqrt{g/h}S_{\eta}$ をガウシアン帯に同時加算し、反射波を透過させつつ $+z$ 方向へクリーンな進行波を生成。
  - **`fp32` (`ARGBFloat` / `RGFloat`) バッファ化と6反復 Jacobi 分散ソルバ**: 微小 $\Delta t$ での `fp16` 量子化誤差を排除し、現サブステップの $\bar{\mathbf{f}}$ を初期値とした6反復 Jacobi 法により位相遅れのない高精度な分散波列を実現。
- [`BoussinesqSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs): 予測ステップ用の中間テクスチャ `predTex` を分離し GPU スレッド間の競合（Race Condition）を排除。

---

### 3.4 波面ハイライトの仕組みと描画モード切り替え不具合の修正（Turn 5〜6 / コミット `655ecfa`）

#### 波面の白いハイライト要因（Turn 5）
[`WaterSurfaceCommon.shader`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader) における波面の色は以下の要素の合成であることを整理：
1. **水面下地色（Diffuse）**: 水位 $\eta$ に応じた濃紺（波の谷 `_DeepColor`）〜エメラルドシアン（波の峰 `_BaseColor`）のグラデーション。
2. **鏡面反射（Specular）**: 法線傾斜と太陽光（Directional Light）による Blinn-Phong ハイライト（`pow(nDotH, 64) * _Smoothness`）。
3. **白波表現（Foam）**: 波面の傾斜勾配（$\|\nabla \eta\| > 0.15$）または FFT ヤコビアンで検出された泡色（`_FoamColor`）のブレンド。

#### 描画モード切り替え不具合の修正（Turn 6）
- **原因**: シェーダー側が `#pragma shader_feature_local` のコンパイル時マクロ（`#if defined(_VISUALMODE_*)`）で分岐していた一方、[`WaterRenderer.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterRenderer.cs) は実行時に `MaterialPropertyBlock.SetFloat("_VisualMode", ...)` で数値を送っていたため、キーワードが切り替わっていなかった。
- **修正**: [`WaterSurfaceCommon.shader`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader) のフラグメントシェーダーを CBUFFER の `_VisualMode` に基づく Uniform 動的分岐（`0: Realistic`, `1: HeightMap`, `2: Normal`, `3: Wireframe`, `4: Bathymetry`）へ変更。

| モード | キャプチャ画像 |
| :--- | :--- |
| **Realistic (`0`)** | ![01_WaveEquation_Realistic](../Captures/01_WaveEquation_Realistic.png) |
| **HeightMap (`1`)** | ![01_WaveEquation_HeightMap](../Captures/01_WaveEquation_HeightMap.png) |
| **Normal (`2`)** | ![01_WaveEquation_Normal](../Captures/01_WaveEquation_Normal.png) |
| **Wireframe (`3`)** | ![01_WaveEquation_Wireframe](../Captures/01_WaveEquation_Wireframe.png) |

---

### 3.5 `01_WaveEquation` の不可視障害物と画面UIコントロールの拡充（Turn 7〜9, 16 / コミット `fa78e4d`, `e2809a9`）

- **不可視障害物の理由（Turn 7）**:
  `01_WaveEquation` シーン中央の反射は、波の回折・干渉を観察するための二重スリットマスク（`_ObstacleTex` の `ObstaclePattern.DoubleSlit`）であり、水面メッシュのみを描画し壁の3Dオブジェクトを置いていなかったため透明な壁として見えていたことを説明。
- **画面内 HUD コントロールの実装（Turn 8〜9, 14, 16）**:
  当初テキスト説明のみだった [`WaterSampleUI.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterSampleUI.cs) に `DrawSpecializedControls()` を実装し、全シーン・各方式固有のパラメータを画面上で直接操作可能にした：
  - **01 WaveEquation**: スリット/障害物切替（`None`, `Single`, `Double`, `Pillars`）、波源モード切替（`Impulse`, `Oscillator`, `Moving`, `Mouse`）、`Trigger Impulse` ボタン、`Interval` / `WaveSpeed` / `Damping` スライダー。
  - **02 Boussinesq**: 海底地形切替（`Flat`, `Shoal`, `Slope`）、波源モード切替（`Wavemaker`, `Impulse`, `Periodic`, `Moving`, `Mouse`）、パルス形状切替（`Planar`, `Circular`）、`Freq` / `Interval` / `Dispersion` / `Wave Amp` スライダー、`Trigger Impulse` ボタン。
  - **03 FFTWater**: 海況プリセット切替（`Calm`, `Swell`, `Stormy`）、`Wind` / `Wind Dir` / `Choppy` / `Swell` / `Swell Dir` スライダー、`Foam` トグル。
  - **04 Comparison / 05 Overview**: 断面高さ強調（`Profile Scale`）スライダー、および `WaterSampleUI` と `WaterComparisonController` の双方向同期制御。

---

### 3.6 周期的固定幅インパルス波源への改修とコードベース全体のクリーンアップ（Turn 10〜11 / コミット `45f486d`, `56a6495`）

- **周期的パルス波源（Turn 10 / コミット `45f486d`）**:
  - **課題**: `ContinuousOscillator` が正弦波 $\sin(2\pi f t)$ で常時励起していたため、周波数を下げると1回あたりの隆起時間（半周期 $1/(2f)$）まで間延びし、波長 $\lambda = c/f$ が巨大化して波面全体が鈍い山のように上下し、スリット干渉縞がぼやけていた。
  - **解決**: 1回あたりの波紋の太さは `sourceRadius` で決まるシャープなガウスインパルス（`TriggerImpulseOnTarget`）に固定し、発生間隔のみを `PulseInterval`（$0.2\ \mathrm{s} \sim 3.0\ \mathrm{s}$）で制御する水滴モデルに変更。

![01_WaveEquation_PeriodicPulses](../Captures/01_WaveEquation_PeriodicPulses.png)

- **警告 `CS0414` 解消と不要コード一掃（Turn 11 / コミット `56a6495`）**:
  - [`WaveEquationSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquationSimulation.cs) / [`WaveEquation.compute`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute): 未使用となった `oscillatorPhase`、`kernelOscillator`、`CSAddOscillator` カーネル、`_PreviousState` 等を削除。
  - [`WaterSurfaceCommon.shader`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader): `_VisualMode` の `[KeywordEnum]` を `[Enum(...)]` に変更し不要なキーワードバリアントを排除。
  - [`FFTOcean.compute`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute): 未使用定数（`_LengthScale2`, `_PassIndex`, `_NumStages`）を削除し、符号付き整数除算・剰余（`/`, `%`）を `uint` ビットシフトとビットマスク `& (R - 1u)` / `& (N - 1)` に置換して D3D11 コンパイラ警告を解消。
  - [`BoussinesqSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs): 未参照だった `customBathymetryTexture` に `CustomBathymetryTexture` プロパティと `UpdateBathymetryTexture()` 内のバイリニアサンプリング処理を接続。

---

### 3.7 `02_Boussinesq` の海底地形可視化・ソリトン励起モード実装（Turn 14 / コミット `acbff9c`）

#### 1. 海底地形（Bathymetry）の2方式同時可視化
- **`Realistic` モードでの浅瀬エメラルド透過色＋海底等深線**: [`WaterSurfaceCommon.shader`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader) に `_BathymetryTex` と `_HasBathymetry` を接続し、水深 $h$ に応じた透過減衰 $f_{\mathrm{shoal}} = \mathrm{saturate}(\exp(-0.26(h - 1.2)))$ と 2m 等深線により、水面のリアルな質感のまま浅瀬や岬の地形が透けて見える表現を実装。
- **`Bathymetry` 専用可視化モード (`_VisualMode == 4`)**: 水深 $h \in [1\ \mathrm{m}, 15\ \mathrm{m}]$ のヒートマップ＋1m 等深線＋波峰ハイライトを重畳表示する専用モードを追加。

#### 2. GPU非破壊加算カーネル (`CSAddImpulse`) と波源バリエーション
[`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute) に GPU 加算カーネル `CSAddImpulse` を実装し、以下の励起モード・波形を実現：

| バリエーション | 数理モデル・励起方法 | 観察できる Boussinesq 特有の現象 |
| :--- | :--- | :--- |
| **1. 1次元平面孤立波**<br>(Planar Solitary Wave) | $\eta(y) = A\,{\mathrm{sech}}^{2}\!\left(\frac{y - y_{0}}{0.6 R}\right),\ w(y) = c\frac{\eta}{h + \eta}$ | 平坦底では波形を保って直進し、斜め浅瀬や岬で波面がハの字に屈折・波高集中（Shoaling）する過程を1波単独で鮮明に観察。 |
| **2. 円形ガウスパルス**<br>(Circular Pulse) | $\eta(r) = A \exp(-3(r/R)^{2})$ | 深水部と浅水部の伝播速度差により、円形だった波面が浅瀬側で扁平・楕円状に歪みながら屈折する過程を観察。 |
| **3. 周期パルス列 / 移動波源 / マウス注入** | `PeriodicPulses`, `MovingSource`, `InteractiveMouse` | 一定間隔での連続孤立波射出、移動船舶風の航跡波、および浅瀬・岬の任意位置へのマウスクリック/ドラッグ波源注入。 |

---

## 4. 今後の拡張設計ロードマップ（計2件）

設計・比較検討が完了しており、今後の拡張フェーズで導入可能な2つの技術テーマです。

### 4.1 テーマA：自由な境界形状（湖畔・島など）の指定（Turn 9）

#### 入力インターフェース案
1. **境界マスク / 距離フィールドテクスチャ（SDF / 2値マスク）**: 水域を `0`、陸地を `1`（または岸からの符号付き距離）とする `Texture2D`。
2. **地形ハイトマップとの交差（Water Level Clipping）**: 地形標高 $H(x, z)$ と基準水面 $Y$ の差から水深 $h(x, z) = Y - H(x, z)$ を算出し、$h \le 0$ を陸地境界とする。

#### 各シミュレーション方式での対応方針

| 方式 | 自由境界の実現性 | 反射・屈折の物理計算 | 実装アプローチ |
| :--- | :---: | :---: | :--- |
| **2D波動方程式** | **完全対応** | ◎ 完全反射・回折・干渉 | 既存の `_ObstacleTex` に任意の湖畔マスクテクスチャをバインドするだけで、任意の岸壁形状での反射・回折がそのまま動作。 |
| **Boussinesq系** | **完全対応** | ◎ 浅水変形・屈折・回り込み | `CustomBathymetryTexture` に湖畔の水深マップを与えることで、岸に向かって波が減速し汀線と平行に波面が揃う物理的屈折が自然に発生。 |
| **FFT海面** | **視覚的対応** | × 壁面反射は不可（周期境界のため） | 沖合の波を FFT で解き、岸からの距離 $d_{\mathrm{shore}}$ に応じて頂点変位を減衰させる **Shoreline Damping**（$\mathrm{Disp} = \mathrm{Disp_{FFT}} \times \mathrm{saturate}(d_{\mathrm{shore}} / d_{\mathrm{fade}})$）＋陸地領域のシェーダークリッピング＋汀線フォーム強調で自然な湖畔を表現。 |

---

### 4.2 テーマB：水面メッシュ細分化（LOD）の方式選定（Turn 6）

オープンソースパッケージ `jp.nobnak.heightfield-lod` の導入と独自実装を比較評価した結果、**水面専用の同心リング Geometry Clipmap による独自実装（案B）** を推奨。

| 評価軸 | `jp.nobnak.heightfield-lod` の仕様 | 水面シミュレーションの要求仕様 | 判定・理由 |
| :--- | :--- | :--- | :--- |
| **カメラ投影** | **正射影 (Orthographic)** 前提 | **透視投影 (Perspective)**（斜め見下ろし〜水平線視点） | **不適合**: 遠近法による手前・奥のピクセル密度差に対応できない |
| **LOD 分割基準** | **曲率 (Curvature / Laplacian)** 駆動 | **カメラ距離 (Screen-Space Error)** 駆動 | **特性の相違**: 水面は平坦な領域でも手前なら高密度メッシュが必要 |
| **配置・座標系** | ローカル XY 平面（高さは $-Z$ 方向） | 水平 XZ 平面（高さは $+Y$ 方向） | Transform とシェーダー座標変換のすり合わせが必要 |
| **変位次元** | **1次元スカラー高さ $h$** (`RFloat`) | **3次元変位 $(D_{x}, \eta, D_{z})$ + Foam** (`RGBAHalf`) | **変位欠落**: FFT 海面の水平 Choppy 変位（尖った波頭）が扱えない |
| **パッケージ依存** | OpenUPM Scoped Registry の追加が必要 | 単体で完結し即座にインポート・実行可能 | 外部レジストリ依存を増やさない方が配布品質が高い |

> [!TIP]
> **推奨される Geometry Clipmap 設計（案B）**:
> カメラの水平位置を中心として解像度が倍々で広がる $4\sim 6$ 段階の同心グリッドリングを配置し、テクセルサイズ単位でグリッド位置をスナップ（Texel Snapping）させることで、カメラ移動時の頂点ジッター（Swimming 現象）を防ぎつつ手前のリップルから水平線まで最小頂点数で3方式すべてを描画できる。

---

### 4.3 テーマC：数値精度・性能ベンチマーク監査（Audit）と完全是正（Turn 14〜16）

監査対象コミット `db6fcb5f751b7704b979787726040aea8585f666` において指摘された13項目の是正課題（P0: 6項目、P1: 5項目、P2: 2項目）に対し、以下のアルゴリズムおよびパイプライン改善を実施・検証完了。

| 改善ID | 優先度 | 指摘内容 | 採用した解決策 | 達成・検証結果 |
| :--- | :---: | :--- | :--- | :--- |
| **B1** | P0 | 分散係数 $\beta \le 6$ 上限により高解像度化で分散が弱まる | 人工上限を完全撤廃し、物理係数 $\beta = \frac{\alpha h^{2}}{3\Delta x^{2}}$ を保持 | 格子幅半減でも物理分散が保たれ格子収束性を回復 |
| **B2** | P0 | 標準 Peregrine 系と作用素・係数（$1/6$）が不一致 | 平坦部 $1/3$ 係数の標準 Peregrine (1967) 方程式に整合 | $kh=1$ での位相速度誤差が $+6.09\% \to -0.764\%$ へ改善 |
| **B3** | P0 | 分散あり時間積分が1次収束 | 予測子・修正子の双方で分散求解し Heun 2次時間更新を適用 | 時間刻み半減時の誤差低減率 $3.99\times$（理論値 $4.0\times$、2次収束保証） |
| **B4** | P1 | Jacobi 6回の残差誤差 | Peaceman-Rachford ADI 型の 1D 三重対角 PCR ソルバー（`groupshared` メモリ）を採用 | 厳密な対角優位性により各ステージわずか2ディスパッチで直接求解 |
| **F1** | P0 | 正負波数で乱数と共役の対応不一致 | 周期境界負波数インデックス $i_{-} = (N - i) \bmod N$ からペア生成 | 逆変換後の虚数/実数 RMS 残差比が $3.45 \times 10^{-16}$（機械精度）へ到達 |
| **F2** | P0 | 解像度2倍で波高が半減する離散化不整合 | 離散波数刻み $\Delta k = \frac{2\pi}{L}$ に基づく振幅離散化と正規化統一 | $N=64, 128, 256$ で有義波高 $H_{s} \approx 3.63\ \mathrm{m}$ が完全に不変（解像度独立性確立） |
| **F3** | P2 | IFFT の Blit と複数回変換コスト | $(\tilde{D}_{y} + i\tilde{D}_{x}, \tilde{D}_{z})$ への複素パッキングによる単一 2D IFFT 化 | `Graphics.Blit` ゼロ回、ディスパッチ数半減（$256^{2}$ で 18 回） |
| **W1** | P1 | fp16 履歴による長波の打切り誤差蓄積 | 状態テクスチャを `RGFloat` (`fp32x2`) へ移行 | 10秒伝播時の長波打切り誤差が $223.78\% \to 0.00008\%$ へ低減 |
| **W2** | P1 | 時間刻み変更時に水位間隔の補正がない | 物理量 $(\eta, v = \partial_{t}\eta)$ を保持するシンプレクティック Leapfrog 積分へ刷新 | 可変時間刻み・波速変更時にも不連続跳びが発生しない連続性を確保 |
| **C1** | P0 | 比較入力・計測条件の不統一 | ソルバー単体、描画込み、断面非同期読み戻しを分離計測するベンチマークスイートを構築 | `Docs/BenchmarkResults.csv` へ計測結果を出力・保存 |
| **C2** | P1 | FFT UI 同値代入での不要な再生成 | プロパティセッターへの同値判定と同値代入ガード・dirty フラグを実装 | 定常シミュレーション中の再生成ディスパッチをゼロ化（`RedundantSpectrumRegen = 0`） |
| **C3** | P1 | 断面表示の同期 ReadPixels によるストール | `AsyncGPUReadback` による非同期取得＋静水深 CPU キャッシュ | GPU 同期パイプラインバブルを完全解消 |
| **G1** | P2 | シェーダー重複と描画法線分離 | `_NormalExaggeration` をシェーダー側へ集約し、`NormalVisual` で真の単位物理法線を表示 | 物理検証とビジュアル強調の完全両立 |
