# unity-water-simulation

3方式の水面シミュレーション（2D線形波動方程式、Boussinesq系、スペクトル／FFT海面）を独立して利用・比較できるUnity UPMパッケージおよび検証環境です。

UPMパッケージ本体は [`Packages/com.nobnak.water-simulation`](file:///c:/Users/nakata/Repo/unity-water-simulation/Packages/com.nobnak.water-simulation) に配置されています。

## 特徴
1. **2D線形波動方程式 (`WaveEquationSimulation`)**:
   - 9点等方ラプラシアンによる軽量・高応答な波紋シミュレーション。
   - スリット通過時の回折・干渉、障害物反射、外周スポンジ無反射吸収、マウス励起。
2. **Boussinesq系浅水波モデル (`BoussinesqSimulation`)**:
   - Peregrine弱非線形・弱分散浅水波方程式。
   - Helmholtz分散インバータ（Jacobi緩和法）による無条件数値安定化。
   - 水深変化に応じた波速変化（$c \sim \sqrt{gh}$）、斜め浅瀬での屈折（Refraction）、波長短縮（Shoaling）、岬への波エネルギー集中。
3. **スペクトル／FFT海面 (`FFTOceanSimulation`)**:
   - Tessendorf統計海洋波スペクトル＋長波長うねり（Swell）の重畳。
   - Compute Shaderによるビット反転不要のStockham autosort 2D IFFT。
   - チョッピー変位（Trochoidal波頭）およびヤコビアン白波（Foam）検出。
4. **共通比較・検証基盤**:
   - 共通PBR水面シェーダー（3D頂点変位、フレネル、深度グラデーション、法線、白波）。
   - 形状確認用可視化モード（フォトリアル、等高線カラーヒートマップ、法線、ワイヤフレーム）。
   - 1D断面プロファイルビューア（分散テールや水深の直接数値視認）。
   - 比較用HUD（観察ポイント、操作ガイド、適用限界の表示）。

## 対象環境
- **Unity**: Unity 6 (6000.3.27f1 以上)
- **レンダーパイプライン**: Universal Render Pipeline (URP 17.3.0 以上)
- **グラフィックスAPI**: Direct3D 12 / 11 / Metal / Vulkan (Compute Shader 5.0 以上)
- **依存パッケージ**: `com.unity.render-pipelines.universal`, `com.unity.mathematics` (1.3.2)

詳細な数式理論、性能ベンチマーク、利用手順については [パッケージ ドキュメント (日本語)](file:///c:/Users/nakata/Repo/unity-water-simulation/Packages/com.nobnak.water-simulation/README.ja.md) をご覧ください。
