**[English](README.md) | 日本語**

# unity-water-simulation

3方式の水面シミュレーション（2D線形波動方程式、Boussinesq系、スペクトル／FFT海面）を独立して利用・比較できるUnity UPMパッケージおよび検証環境です。

UPMパッケージ本体は [`Packages/com.nobnak.water-simulation`](Packages/com.nobnak.water-simulation) に配置されています。

## 特徴
1. **2D線形波動方程式 (`WaveEquationSimulation`)**:
   - 9点等方ラプラシアン＋`fp32` (`RGFloat`) シンプレクティック Leapfrog 時間積分による軽量・高応答な波紋シミュレーション。
   - 物理量 $(\eta, v = \partial_{t}\eta)$ の明示保持により波速や時間刻み変更時にも不連続跳びが発生せず、10秒以上の長波伝播打切り誤差を $0.00008\%$ へ低減。
   - スリット通過時の回折・干渉、障害物反射（Dirichlet / Neumann）、外周スポンジ無反射吸収、滑らかなコンパクト $C^{2}$ 級ベル型インパルス（高周波ギブズリンギングなし）・周期パルス・移動波源・マウスクリック励起。
2. **Boussinesq系浅水波モデル (`BoussinesqSimulation`)**:
   - 標準 Peregrine (1967) 弱非線形・弱分散浅水波方程式（$1/3$ 分散係数準拠、位相速度誤差 $-0.764\%$）。
   - 高解像度での係数切捨て上限を完全撤廃し、2次元 Peregrine 作用素を Peaceman-Rachford ADI 型に分解して `groupshared` メモリ内の三重対角並列巡回簡約（PCR）により無条件安定に直接求解。
   - 予測子・修正子の双方で分散逆行列を解く Heun 2次時間更新により、時間刻み半減で誤差が $3.99\times$ 低減する真の $O(\Delta t^{2})$ 2次収束を達成。
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
