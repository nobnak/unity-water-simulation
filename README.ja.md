**[English](README.md) | 日本語**

# unity-water-simulation

3方式の水面シミュレーション（2D線形波動方程式、Boussinesq系、スペクトル／FFT海面）を独立して利用・比較できるUnity UPMパッケージおよび検証環境です。

UPMパッケージ本体は [`Packages/com.nobnak.water-simulation`](Packages/com.nobnak.water-simulation) に配置されています。

## デモ動画 / Video Demos

各シーンの動作デモ映像です（画像クリックでYouTube再生）。

| シーン01: 2D線形波動方程式 | シーン02: Boussinesq系浅水波 | シーン03: スペクトル／FFT海面 |
| :---: | :---: | :---: |
| [![波動方程式 デモ](https://img.youtube.com/vi/s7Eb2UlgJVc/maxresdefault.jpg)](https://youtu.be/s7Eb2UlgJVc) | [![浅水波 デモ](https://img.youtube.com/vi/1DWCYRkK0RM/maxresdefault.jpg)](https://youtu.be/1DWCYRkK0RM) | [![FFT海面 デモ](https://img.youtube.com/vi/j2twZkyc68E/maxresdefault.jpg)](https://youtu.be/j2twZkyc68E) |
| [波動方程式 (YouTube)](https://youtu.be/s7Eb2UlgJVc)<br>スリット回折・干渉・波紋 | [浅水波 (YouTube)](https://youtu.be/1DWCYRkK0RM)<br>地形による屈折・波長短縮・集中 | [FFT海面 (YouTube)](https://youtu.be/j2twZkyc68E)<br>うねり・風波・チョッピー波頭 |

## 特徴
1. **2D線形波動方程式 (`WaveEquationSimulation`)**:
   - 9点等方ラプラシアン＋`fp32` (`RGFloat`) シンプレクティック Euler (kick-drift) 時間積分（半ステップ中央化 Cayley 減衰）による軽量・高応答な波紋シミュレーション。
   - 物理状態対 $(\eta, v = \partial_{t}\eta)$ の同時保持により、同時刻サンプリングにおいて厳密な1次収束 $O(\Delta t)$（時間刻み半減時の誤差低減比 $\approx 2.01\times$）を示しつつ、波速や時間刻み変更時にも不連続跳びが発生せず、長波長伝播打切り誤差を $0.00008\%$ へ低減。
   - スリット通過時の回折・干渉、障害物反射（Dirichlet / Neumann）、外周スポンジ無反射吸収、滑らかなコンパクト $C^{2}$ 級ベル型インパルス（高周波ギブズリンギングなし）・周期パルス・移動波源・マウスクリック励起。
2. **Boussinesq系浅水波モデル (`BoussinesqSimulation`)**:
   - 無回転・緩勾配海底を前提とする成分分離 Helmholtz 型 Peregrine (1967) 弱非線形・弱分散浅水波モデル（$\left(I - \alpha \frac{h^{2}}{3}\nabla^{2}\right)\mathbf{a} = \mathbf{f}$、$1/3$ 分散係数準拠。表面重力波前提に基づき速度成分間交差微分 $\partial_{xz}a_z$ や分散作用素内の水深勾配は省略）。
   - 方向分離 Peaceman-Rachford ADI 分解と `groupshared` メモリ内の三重対角並列巡回簡約（PCR）により無条件安定な分散逆行列求解を行い、欠損補正（`CSDefectCorrection`, `dispersionSweeps`）によって方向分離に伴う4階交差項 $\beta_x \beta_z \partial_x^2 \partial_z^2 \mathbf{a}$ を補正（スイープ毎に誤差約49倍低減、$45^\circ$ 斜め位相速度誤差を Sweep 1 の $-1.03\%$ から Sweep 2 で $-0.021\%$、Sweep 3 で $-0.0004\%$ へ低減）。
   - 予測子・修正子の対称結合（MacCormack-Heun更新）、CFL条件に基づく自動サブステップ分割（$\Delta t \le 0.55 \frac{\Delta x}{\sqrt{2(c + u)}}$）、物理時間あたり減衰不変な4階保存バイハーモニック空間フィルター、および不透過固体側壁での厳密な法線流速拘束（$u \equiv 0$、流束 $Hu \equiv 0$）。
   - 水深変化に応じた波速変化（$c \sim \sqrt{gh}$）、斜め浅瀬での屈折（Refraction）、波長短縮（Shoaling）、岬への波エネルギー集中。
   - 沖合進行波造波に加え、平面孤立波（Planar Soliton）・円形ガウスパルスの単発・周期・移動・マウス注入に対応。
3. **スペクトル／FFT海面 (`FFTOceanSimulation`)**:
   - Tessendorf統計海洋波スペクトル＋長波長うねりの重畳。
   - 周期負波数マッピングによる厳密なエルミート共役対称性（虚数残差比 $3.45 \times 10^{-16}$）と離散波数刻み $\Delta k = \frac{2\pi}{L}$ に基づく振幅統一（格子解像度非依存の波高）。
   - 単一4ch 2D IFFT パス（`Graphics.Blit` ゼロ回）により変位3成分 $(D_{y}, D_{x}, D_{z})$ を一括変換。セッター同値判定と dirty フラグにより定常シミュレーション中の不要な再生成ディスパッチをゼロ化。
   - 波数重み付きチョッピー変位（Trochoidal波頭）、ヤコビアン結合の厳密ラグランジュ法線、波峰透過散乱（SSS）およびヤコビアン白波（Foam）検出。
4. **共通比較・検証基盤**:
   - 共通PBR水面シェーダー（3D頂点変位、フレネル、深度グラデーション、浅瀬エメラルド透過色＋等深線、波峰SSS、白波）。
   - 5種の可視化モード（`Realistic`, `HeightMap`, `Normal`, `Wireframe`, `Bathymetry`）。`NormalVisual` モードでは強調のない単位物理法線を表示。
   - `AsyncGPUReadback` と静水深CPUキャッシュによる同期ストールなしの1D断面プロファイルビューア。
   - 比較用HUD（観察ポイント、操作ガイド、適用限界の表示、複数水面の同期再生・制御）。

## 対象環境
- **Unity**: Unity 6 (6000.3.27f1 以上)
- **レンダーパイプライン**: Universal Render Pipeline (URP 17.3.0 以上)
- **グラフィックスAPI**: Direct3D 12 / 11 / Metal / Vulkan (Compute Shader 5.0 以上)
- **依存パッケージ**: `com.unity.render-pipelines.universal`, `com.unity.mathematics` (1.3.2)

## ドキュメント一覧
- **[パッケージ ドキュメント (日本語)](Packages/com.nobnak.water-simulation/README.ja.md)**: 導入手順、5つのサンプルシーンガイド、数理モデル概要、性能ベンチマーク。
- **[アルゴリズム・数値計算法・性能見積もり完全仕様書](Docs/WaterSimulation_Algorithm_And_Performance_Spec.ja.md)**: 支配方程式、離散化ステンシル、テクスチャ構造、およびVRAM・メモリ帯域・GPU実行時間の定量的見積もりモデル（本資料単体で同等システムの再実装と性能予測が可能）。
- **[実装・技術検討・議論アーカイブ総合資料](Docs/WaterSimulation_Technical_Archive.ja.md)**: コミット履歴、数値発散の原因分析と解決策、設計判断、今後の拡張ロードマップ。
