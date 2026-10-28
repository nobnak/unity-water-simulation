**[English](WaterSimulation_Algorithm_And_Performance_Spec.md) | 日本語**

# Unity Water Simulation アルゴリズム・数値計算法・性能見積もり完全仕様書

本仕様書は、[`com.nobnak.water-simulation`](../Packages/com.nobnak.water-simulation/package.json) に実装されている3方式の水面シミュレーション（**2D線形波動方程式**、**Boussinesq系浅水・分散波モデル**、**スペクトル/FFT海洋モデル**）および**共通水面レンダリング基盤**の支配方程式、離散化スキーム、GPUテクスチャ構造、カーネル処理フロー、ならびにメモリ帯域・計算量に基づく性能見積もりモデルを網羅した技術仕様書です。

本ドキュメント単体で**同等のシミュレーション・描画システムの再実装**および**任意のグリッド解像度・GPU環境におけるVRAM使用量・実行時間の定量的見積もり**が可能なレベルで記述しています。

---

## 1. システム全体アーキテクチャと共通規格

### 1.1 座標系・単位系・基準面
- **空間座標系**: Unity ワールド座標系（左手系：$X$ = 右/東、$Y$ = 鉛直上向き、$Z$ = 前/北）。
- **計算格子座標**: 領域サイズ $(L_{x}, L_{z})\ [\mathrm{m}]$ を解像度 $(N_{x}, N_{z})$ で等分割。格子間隔は $\Delta x = L_{x} / N_{x},\ \Delta z = L_{z} / N_{z}$（正方形領域・正方格子では $L_{x} = L_{z} = L,\ N_{x} = N_{z} = N,\ \Delta x = \Delta z = L/N$）。
- **テクスチャUVと格子インデックスの対応**:

$$
\mathrm{UV}(i, j) = \left(\frac{i + 0.5}{N_{x}},\ \frac{j + 0.5}{N_{z}}\right), \quad i \in [0, N_{x} - 1],\ j \in [0, N_{z} - 1]
$$

- **基準水位**: 静止水面（Still Water Level）を $y = 0\ [\mathrm{m}]$、鉛直変位（波高）を $\eta(x, z, t)\ [\mathrm{m}]$、静止水深を $h(x, z) > 0\ [\mathrm{m}]$（海底標高 $z_{b}(x, z) = -h(x, z) < 0\ [\mathrm{m}]$）、全水深を $H(x, z, t) = h(x, z) + \eta(x, z, t)$ と定義する。重力加速度は $g = 9.81\ [\mathrm{m/s^{2}}]$。

### 1.2 共通出力テクスチャインターフェース ([`IWaterSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/IWaterSimulation.cs))

全シミュレーション方式は以下の共通 `RenderTexture` フォーマットで計算結果を出力し、描画系（[`WaterRenderer`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterRenderer.cs) / [`WaterProfileViewer`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterProfileViewer.cs)）はシミュレーション方式に依存せずこれらを参照する。

| テクスチャ名 | プロパティ名 | フォーマット | 1テクセル容量 | R | G | B | A |
| :--- | :--- | :---: | :---: | :--- | :--- | :--- | :--- |
| **`WaterDataTexture`** | `_WaterDataTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | 水平X変位 $D_{x}\ [\mathrm{m}]$ | 鉛直変位 $\eta\ [\mathrm{m}]$ | 水平Z変位 $D_{z}\ [\mathrm{m}]$ | 白波強度 $\mathrm{Foam} \in [0,1]$ |
| **`NormalTexture`** | `_NormalTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | 法線 $N_{x}$ | 法線 $N_{y}$ | 法線 $N_{z}$ | 波峰圧縮度 / 勾配 $\in [0,1]$ |
| **`BathymetryTexture`** | `_BathymetryTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | 静止水深 $h\ [\mathrm{m}]$ | 海底標高 $z_{b} = -h\ [\mathrm{m}]$ | 0 | 1 |

### 1.3 描画フレームレート非依存の時間進行 ([`WaterSimulationBase.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterSimulationBase.cs))

```csharp
float dt = Time.deltaTime * timeScale;
if (fixedTimeStep > 0f) {
    accumulator += dt;
    int count = 0;
    while (accumulator >= fixedTimeStep && count < 4) { // 最大4ステップ/フレーム（スパイラル・オブ・デス防止）
        Step(fixedTimeStep);
        accumulator -= fixedTimeStep;
        count++;
    }
} else {
    Step(dt);
}
```

---

## 2. 方式1：2D線形波動方程式 (`WaveEquationSimulation`)

実装ファイル: [`WaveEquationSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquationSimulation.cs), [`WaveEquation.compute`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute)

### 2.1 支配方程式
減衰項および外周スポンジ吸収層を伴う2次元線形双曲型波動方程式：

$$
\frac{\partial^{2} \eta}{\partial t^{2}} + \gamma_{\mathrm{eff}}(x, z)\frac{\partial \eta}{\partial t} = c^{2} \nabla^{2} \eta
$$

- $\eta(x, z, t)$: 水面鉛直変位 $[\mathrm{m}]$
- $c$: 位相速度（波速） $[\mathrm{m/s}]$（全波長で一定・非分散）
- $\gamma_{\mathrm{eff}}(x, z) = \gamma_{0} + \gamma_{\mathrm{sponge}}(x, z)$: 有効減衰率 $[1/\mathrm{s}]$

### 2.2 内部テクスチャ構成
| テクスチャ変数 | フォーマット | 役割・チャンネル割り当て |
| :--- | :---: | :--- |
| `stateTexA`, `stateTexB` (`_CurrentState`, `_NextState`) | `RGFloat` (8 B/px) | Ping-Pong 状態バッファ（fp32精度）。`R`: 現在水位 $\eta^{n}\ [\mathrm{m}]$、`G`: 鉛直速度 $v^{n} = \frac{\partial \eta}{\partial t}\ [\mathrm{m/s}]$。fp32化により10秒以上の長波長伝播における打切り誤差蓄積（旧fp16版の $223.78\%$ 誤差）を $0.00008\%$ へ低減。 |
| `obstacleTex` (`_ObstacleTex`) | `R8` (1 B/px) | 障害物マスク（`1.0` = 壁/スリット遮蔽部、`0.0` = 水域） |

### 2.3 数値離散化スキーム

#### (1) CFL 条件と自動サブステップ分割
2次元陽的スキームの安定限界（クーラン条件）に基づき、1ステップの時間幅 $dt$ をサブステップ $\Delta t$ に自動分割する：

$$
\Delta t_{\max} = 0.95 \frac{\Delta x}{\sqrt{2}\,c}, \quad M_{\mathrm{sub}} = \max\!\left(1,\ \left\lceil \frac{dt}{\Delta t_{\max}} \right\rceil\right), \quad \Delta t = \frac{dt}{M_{\mathrm{sub}}}
$$

#### (2) 9点等方性ラプラシアン (`ComputeLaplacian`)
標準の5点ステンシルで生じる格子軸方向と対角方向の位相速度異方性を抑止するため、$O(\Delta x^{2})$ 等方性9点ステンシルを使用する：

$$
\nabla^{2} \eta_{i,j}^{n} \approx \frac{4(\eta_{i-1,j}^{n} + \eta_{i+1,j}^{n} + \eta_{i,j-1}^{n} + \eta_{i,j+1}^{n}) + (\eta_{i-1,j-1}^{n} + \eta_{i+1,j-1}^{n} + \eta_{i-1,j+1}^{n} + \eta_{i+1,j+1}^{n}) - 20\eta_{i,j}^{n}}{6\Delta x^{2}}
$$

#### (3) シンプレクティック Leapfrog 時間積分 (`CSStep`)
状態 $(\eta, v = \partial_{t}\eta)$ を物理的に直接保持し、半陰的減衰を伴うシンプレクティック Leapfrog 法で時間発展させる：

$$
\eta^{n+1} = \eta^{n} + \Delta t\, v^{n} + \frac{1}{2}\Delta t^{2}\, c^{2}\nabla^{2}\eta^{n}
$$

$$
v^{n+1} = \frac{\left(1 - \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t\right)v^{n} + \frac{1}{2}\Delta t\, c^{2}\left(\nabla^{2}\eta^{n} + \nabla^{2}\eta^{n+1}\right)}{1 + \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t}
$$

物理的な速度 $v$ を明示保持するため、シミュレーション実行中に波速 $c$ やサブステップ幅 $\Delta t$ が動的に変化しても、旧2段差分法のような時間間隔不一致による人工的なエネルギー跳びや不連続性が一切発生しない。

ここで外周スポンジ減衰 $\gamma_{\mathrm{sponge}}$ は領域境界からの最短セル距離 $d_{\mathrm{border}} = \min(i,\ N_{x} - 1 - i,\ j,\ N_{z} - 1 - j)$ に対して：

$$
\gamma_{\mathrm{sponge}} = \begin{cases} S_{\mathrm{sponge}} \left(1 - \frac{d_{\mathrm{border}}}{W_{\mathrm{sponge}}}\right)^{2} & (d_{\mathrm{border}} < W_{\mathrm{sponge}}) \\ 0 & (d_{\mathrm{border}} \ge W_{\mathrm{sponge}}) \end{cases}
$$

また `_ObstacleTex[pos].r > 0.5` の障害物セルでは固定端 Dirichlet 条件（$\eta = 0, v = 0$）またはノイマン無反射境界を課し、全反射およびスリット回折を発生させる。

#### (4) 滑らかなコンパクト $C^{2}$ 級インパルス注入 (`CSAddImpulse`)
切断ガウス分布で生じる高周波リンギング（ギブズ現象）を排除するため、UV中心 $\mathbf{u_{0}}$、半径 $R_{\mathrm{uv}}$、振幅 $A$ に対し、境界で2階導関数まで滑らかにゼロへ漸近するコンパクト $C^{2}$ 級多項式ベル関数を採用する：

$$
\Delta \eta = \begin{cases} A \left(1 - \left(\frac{\|\mathbf{u} - \mathbf{u_{0}}\|}{R_{\mathrm{uv}}}\right)^{2}\right)^{3} & (\|\mathbf{u} - \mathbf{u_{0}}\| < R_{\mathrm{uv}}) \\ 0 & (\|\mathbf{u} - \mathbf{u_{0}}\| \ge R_{\mathrm{uv}}) \end{cases}
$$

これを水位 `state.r` ($\eta$) に加算し、初速 $v = 0$ を保ったまま完全等方的な波紋を励起する。

#### (5) 物理法線と曲率フォームの算出 (`CSComputeNormal`)
中心差分で勾配を算出し、真の単位物理法線 $\mathbf{N_{\mathrm{phys}}}$ を算出する：

$$
\frac{\partial \eta}{\partial x} = \frac{\eta_{i+1,j} - \eta_{i-1,j}}{2\Delta x}, \quad \frac{\partial \eta}{\partial z} = \frac{\eta_{i,j+1} - \eta_{i,j-1}}{2\Delta x}
$$

$$
\mathbf{N_{\mathrm{phys}}} = \mathrm{normalize}\!\left(-\frac{\partial \eta}{\partial x},\ 1.0,\ -\frac{\partial \eta}{\partial z}\right), \quad \mathrm{Foam} = \mathrm{saturate}\!\left(-2.0 \left(\eta_{i-1,j} + \eta_{i+1,j} + \eta_{i,j-1} + \eta_{i,j+1} - 4\eta_{i,j}\right)\right)
$$

`NormalVisual` モードでは物理単位法線を表示し、シェーダー描画時の法線強調係数 `_NormalExaggeration`（デフォルト $2.5$）はシェーダー内部でのみ適用される。

---

## 3. 方式2：Boussinesq系浅水・分散波モデル (`BoussinesqSimulation`)

実装ファイル: [`BoussinesqSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs), [`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute)

### 3.1 支配方程式 (Peregrine 1967 弱非線形・標準Boussinesq分散波方程式)
水深変化のある海底 $h(x, z)$ 上における質量保存則（連続の式）と水深平均水平流速 $\mathbf{u} = (u, w)^{\mathrm{T}}$ の運動量保存則：

$$
\frac{\partial \eta}{\partial t} + \frac{\partial (Hu)}{\partial x} + \frac{\partial (Hw)}{\partial z} = S_{\eta}
$$

$$
\left(I - \alpha \left(\frac{1}{2}h\nabla(h\nabla\cdot) - \frac{h^{2}}{6}\nabla^{2}\right)\right)\frac{\partial \mathbf{u}}{\partial t} = -g\nabla\eta - (\mathbf{u}\cdot\nabla)\mathbf{u} - \frac{c_{f}}{H}\mathbf{u} + \mathbf{S_{u}} = \mathbf{f}
$$

- 平坦部（$\nabla h = 0$）において左辺の線形分散作用素は標準 Peregrine (1967) の $1/3$ 係数に厳密一致する：

$$
\left(I - \alpha \frac{h^{2}}{3}\nabla^{2}\right)\frac{\partial \mathbf{u}}{\partial t} = \mathbf{f}
$$

有限水深の線形分散関係 $\omega^{2} = gk\tanh(kh)$ に対する位相速度誤差は、$kh = 1$ においてわずか **$-0.764\%$**（旧 ad-hoc $1/6$ モデルの $+6.09\%$ から大幅改善）であり、長波から中間水深波への周波数分散テールを物理的に正しく再現する。
- $H(x, z, t) = \max(0.05,\ \max(0.2, h) + \eta)$: 全水深 $[\mathrm{m}]$
- $\alpha \in [0, 1.2]$: 分散係数（$\alpha = 1.0$: Peregrine 分散波、$\alpha = 0.0$: 非分散浅水波 NLSW）
- $c_{f}$: 海底摩擦係数 (`_BedFriction`)
- $S_{\eta},\ \mathbf{S_{u}} = (0, S_{w})^{\mathrm{T}}$: 内部造波ソース項

### 3.1.1 物理的適用範囲と相対水深 $kh$ 診断基準
Peregrine Boussinesq 方程式は浅水〜中間水深波条件（$\mu = h/\lambda \le 0.16$、$kh \le 1.0$）を前提とする多項式分散モデルである。短波長極限（$kh \to \infty$）において周波数が上限遮断周波数 $\omega_{\mathrm{cutoff}} = \sqrt{3g/h}$（$f_{\mathrm{cutoff}} = \frac{1}{2\pi}\sqrt{3g/h}$）に漸近し、群速度 $c_{g} = \frac{\sqrt{gh}}{(1 + \frac{1}{3}h^{2}k^{2})^{3/2}} \to 0$ となる物理的性質を持つ。
浅水性の物理的判定基準は領域長 $L$ ではなく、対象波の波長 $\lambda$ に対する水深比（相対水深 $kh = 2\pi h / \lambda$）によって一意に診断される：

1. **浅水波領域 ($kh < 0.31$, $h/\lambda < 0.05$)**:
   Airy / NLSW 非分散長波極限。分散効果は微小であり、位相速度 $c \approx \sqrt{gh}$ で伝播する。
2. **中間水深波領域 ($0.31 \le kh \le 1.0$, $h/\lambda \le 0.16$)**:
   Peregrine (1967) 1/3 多項式分散モデルの妥当領域。微小振幅波に対する位相速度誤差は $< 2\%$（$kh=1.0$ で $-0.76\%$）であり、周波数分散・ソリトン伝播・浅水変形を高精度に再現する。
3. **深水波領域 ($1.0 < kh \le 1.5$)**:
   多項式分散近似の適用限界を超え、Airy 分散との乖離が増大（誤差 $> 2\%$〜$10\%$）。Boussinesq モデルの精度が低下する領域であり、インスペクターおよび UI にて黄色警告を表示。
4. **遮断・エバネッセント限界 ($kh > 1.5$, $f \ge 0.85 f_{\mathrm{cutoff}}$)**:
   Peregrine 特異点 $kh = \sqrt{3} \approx 1.732$（$k^{2} < 0$）に接近し、群速度が急減して波が沖へ進行できず局所停留・発振する。
5. **パラメータ調整ガイド機能 (`AdjustParametersForBoussinesqRegime`)**:
   波長 $\lambda$ または周波数 $f$ に基づき、中間水深妥当域（$kh \le 1.0$、目標 $kh \approx 0.8$）に収まる推奨水深を算出し自動調整する。本機能は多項式分散モデルの物理的適用範囲内にパラメータを誘導する工学的ガイドであり、砕波や急峻度破綻等の非線形現象に対する無条件の安定保証ではない。

### 3.2 内部テクスチャ構成
微小時間刻み（$\Delta t \approx 0.004 \sim 0.01\ \mathrm{s}$）での量子化誤差を防ぐため、状態・外力・加速度バッファには **`fp32` (`ARGBFloat` / `RGFloat`)** を使用する。

| テクスチャ変数 | フォーマット | 1テクセル容量 | 役割・チャンネル割り当て |
| :--- | :---: | :---: | :--- |
| `stateTexA`, `stateTexB` (`_StateIn`, `_StateOut`) | `ARGBFloat` | 16 Bytes | Ping-Pong 状態バッファ。`R`: $\eta\ [\mathrm{m}]$、`G`: $u\ [\mathrm{m/s}]$、`B`: $w\ [\mathrm{m/s}]$、`A`: 0 |
| `predTex` (`_StatePred`) | `ARGBFloat` | 16 Bytes | MacCormack 予測ステップの中間状態 $(\eta^{\ast}, u^{\ast}, w^{\ast}, 0)$ |
| `forceTex` (`_ForceTex`) | `RGFloat` | 8 Bytes | 予測子・修正子の外力右辺ベクトル $\mathbf{f} = (f_{x}, f_{z})$ |
| `forcePredTex` (`_ForcePredTex`) | `RGFloat` | 8 Bytes | 予測ステップで求めた分散補正加速度 $\mathbf{a}^{(1)} = L^{-1}\mathbf{f}^{n}$ の保存バッファ |
| `accelTexA`, `accelTexB` (`_AccelPing`, `_AccelPong`) | `RGFloat` | 8 Bytes | ADI 三重対角 PCR 分散ソルバーの入出力 Ping-Pong バッファ（$\mathbf{a}^{\ast}, \mathbf{a}$） |
| `bathymetryTex` (`_BathymetryTex`) | `ARGBHalf` | 8 Bytes | `R`: 静水深 $h\ [\mathrm{m}]$、`G`: 海底標高 $z_{b} = -h\ [\mathrm{m}]$ |

### 3.3 数値離散化スキーム

#### (1) CFL 条件と自動サブステップ分割
領域内の最大水深 $h_{\max}$ (`deepWaterDepth`) と波振幅上限 $A_{\max}$、および浅水流速上限 $u_{\max}$ を含めた非線形波速 $c_{\max} = \sqrt{g(h_{\max} + A_{\max})} + u_{\max}$ に基づきサブステップ分割を行う：

$$
\Delta t_{\max} = 0.55 \frac{\min(\Delta x, \Delta z)}{\sqrt{2}\,c_{\max}}, \quad M_{\mathrm{sub}} = \max\!\left(1,\ \left\lceil \frac{dt}{\Delta t_{\max}} \right\rceil\right), \quad \Delta t = \frac{dt}{M_{\mathrm{sub}}}
$$

#### (2) 対称 MacCormack 予測子・修正子法 (`CSPredict` & `CSCorrect`)
コロケート格子上の奇偶デカップリング（$2\Delta x$ 市松模様ノイズ）を消去しつつ空間2次精度を得るため、サブステップごとにパリティ $\sigma_{\mathrm{parity}} \in \lbrace 0, 1 \rbrace$（`pingPong ? 1 : 0`）を交互に切り替える MacCormack 予測子・修正子スキームを採用する。

1. **Predictor (`CSPredict`)**:
   方向符号 $s_{1} = (\sigma_{\mathrm{parity}} == 0)\ ?\ +1 : -1$ を用いて隣接セル $(i + s_{1}, j),\ (i, j + s_{1})$ との片側前進/後退差分（符号付き逆格子間隔 $\tilde{\Delta_{1}} = s_{1} / \Delta x$）で質量流速発散および運動量移流・外力を評価する：

$$
\delta_{x}^{(1)}(Hu) = (H_{i+s_{1},j}u_{i+s_{1},j} - H_{i,j}u_{i,j})\tilde{\Delta_{1}}, \quad \delta_{z}^{(1)}(Hw) = (H_{i,j+s_{1}}w_{i,j+s_{1}} - H_{i,j}w_{i,j})\tilde{\Delta_{1}}
$$

$$
\eta^{\ast} = \eta^{n} - \Delta t\left(\delta_{x}^{(1)}(Hu) + \delta_{z}^{(1)}(Hw) - S_{\eta}(\phi^{n})\right)
$$

   同様に予測外力 $\mathbf{f^{n}} = (f_{x}^{n}, f_{z}^{n})$ を `_ForceTex` に保存する。分散なし（$\alpha \le 0.001$）の場合は直ちに陽的に予測流速 $\mathbf{u^{\ast}} = (\mathbf{u^{n}} + \Delta t\,\mathbf{f^{n}}) D_{\mathrm{sponge}}$ を `_StatePred` に格納する。分散あり（$\alpha > 0.001$）の場合は後述の分散インバータへ渡す。

2. **Corrector (`CSCorrect`)**:
   逆方向符号 $s_{2} = -s_{1}$（$\tilde{\Delta_{2}} = s_{2} / \Delta x$）を用いて予測状態 `_StatePred` の逆向き片側差分を評価し、水位を2次精度平均で更新する：

$$
\eta^{n+1} = D_{\mathrm{sponge}} \cdot \frac{1}{2}\left[\eta^{n} + \eta^{\ast} - \Delta t\left(\delta_{x}^{(2)}(H^{\ast} u^{\ast}) + \delta_{z}^{(2)}(H^{\ast} w^{\ast}) - S_{\eta}(\phi^{n+1})\right)\right]
$$

   修正外力 $\mathbf{f^{\ast}} = (f_{x}^{\ast}, f_{z}^{\ast})$ を `_ForceTex` に格納する。

#### (3) ADI 三重対角並列巡回簡約 (PCR) 分散ソルバー & 欠損補正 (`CSTridiagonalX`, `CSTridiagonalZ`, `CSDefectCorrection`)
旧実装で高解像度時に物理分散係数を最大 $99.4\%$ 切捨てていた人工上限 $\beta \le 6$ を完全撤廃し、2次元 Peregrine 分散作用素 $(I - \frac{h^{2}}{3}\nabla^{2})\mathbf{a} = \mathbf{f}$ を方向分離交替方向陰解法（Peaceman-Rachford ADI）により求解する：

$$
\left(I - \beta_{x}\partial_{x}^{2}\right)\left(I - \beta_{z}\partial_{z}^{2}\right)\mathbf{a} = \mathbf{f}, \quad \beta_{x} = \frac{\alpha h^{2}}{3\Delta x^{2}}, \quad \beta_{z} = \frac{\alpha h^{2}}{3\Delta z^{2}}
$$

- **方向分離 ADI 近似の連続極限と等方性特性**:
  連続 Fourier 空間における ADI 因数分解作用素のゲインは $G_{\mathrm{ADI}} = \frac{1}{(1 + \frac{1}{3}k_{x}^{2}h^{2})(1 + \frac{1}{3}k_{z}^{2}h^{2})}$ で与えられる。
  - 格子軸方向（$0^{\circ}, 90^{\circ}$、すなわち $k_{x}=0$ または $k_{z}=0$）: 交差項がゼロとなり、標準 Peregrine 連成ゲイン $G_{\mathrm{Peregrine}} = \frac{1}{1 + \frac{1}{3}k^{2}h^{2}}$ と完全一致（位相速度誤差 $0.00\%$）。
  - $45^{\circ}$ 斜め方向: 高次交差項 $\frac{1}{9}k_{x}^{2}k_{z}^{2}h^{4}$ に起因して、$kh = 1.0$ において位相速度が標準 Peregrine 比で約 **$-1.03\%$** 遅くなる方向バイアスが存在する。
- **ADI 欠損補正 (Defect Correction: `CSDefectCorrection`)**:
  `dispersionSweeps > 1`（設定範囲 1〜4、インスペクター指定）の場合、交差微分欠損 $\mathcal{R} = \beta_{x}\beta_{z}\partial_{x}^{2}\partial_{z}^{2}\mathbf{a}$ を `CSDefectCorrection` カーネルにより計算して外力右辺に加算し、PCR 求解を反復する。
  1回の補正スイープ（`dispersionSweeps = 2`）により、$kh = 1.0$ における $45^{\circ}$ 方向位相速度誤差は $-1.03\%$ から **$-0.02\%$** へ $49$ 倍低減し、連成作用素の等方性を極めて高精度に再現する（スイープ 1 回の標準 ADI は高速リアルタイム向け、スイープ 2 回以上は高精度解析向けとして機能）。
- **側壁境界条件の法線・接線完全分離**:
  水路側壁（$x = 0, W-1$、`SideWallSponge = false`）において、法線加速度 $a_{x} = 0$（厳密ディリクレ境界条件）と接線加速度 $\partial_{x} a_{z} = 0$（ノイマン境界条件）を行ごとに独立して処理する。
  `groupshared float2` による PCR ベクトル演算において、法線成分は境界行 $b_{x}=1, c_{x}=0, d_{x}=0$ の独立ディリクレ行として解かれるため、壁上の法線加速度は数値的に厳密な **$0.00000000$**（旧実装で残存していた約 $0.844$ の隣接セル結合を完全根絶）に拘束され、不透過固体壁条件が厳密に成立する。

#### (4) 時間発展法と数値収束特性 (`CSApplyPredictedDispersion` & `CSApplyDispersion`)
- **Boussinesq 系**:
  予測子・修正子と分散作用素を連成させるため、MacCormack 予測子外力 $\mathbf{f}^{n}$ と修正子外力 $\mathbf{f}^{\ast}$ の各段階で PCR 分散逆行列を求解し、Heun 型の2次時間更新を適用：

$$
\mathbf{u}^{\ast} = D_{\mathrm{sponge}} \cdot \left(\mathbf{u}^{n} + \Delta t\,\mathbf{a}^{(1)}\right)
$$
$$
\mathbf{u}^{n+1} = D_{\mathrm{sponge}} \cdot \left(\mathbf{u}^{n} + \frac{1}{2}\Delta t\left(\mathbf{a}^{(1)} + \mathbf{a}^{(2)}\right)\right)
$$

  ここで外周スポンジ減衰係数は $d_{\mathrm{border}} < W_{\mathrm{sponge}}$ において陰的分数式 $D_{\mathrm{sponge}} = \frac{1}{1 + 15\,\Delta t\, S_{\mathrm{sponge}} \left(1 - \frac{d_{\mathrm{border}}}{W_{\mathrm{sponge}}}\right)^{2}}$ で与えられる。

- **波動方程式系 (`CSStep`)**:
  物理流速 $v = \partial\eta/\partial t$ を `RGFloat` テクスチャに直接保持し、シンプレクティック Euler（Kick-Drift）時間積分を適用：

$$
v^{n+1} = \frac{v^{n}(1 - \frac{1}{2}\gamma\Delta t) + \Delta t\, c^{2}\nabla^{2}\eta^{n}}{1 + \frac{1}{2}\gamma\Delta t} \quad \text{[Kick]}
$$
$$
\eta^{n+1} = \eta^{n} + \Delta t\, v^{n+1} \quad \text{[Drift]}
$$

  シンプレクティック性によりハミルトン系の相空間面積（$\mathrm{d}\eta \wedge \mathrm{d}v$）が厳密に保存され、長時間のシミュレーションでも人工的な数値減衰を生じない。
  同一時刻 $t_{n+1}$ で評価した物理状態対 $(\eta, v)$ の局所打切り誤差は半ステップ千鳥配置に起因して $O(\Delta t)$（実測収束率 $2.01\times$、1次収束）となり、千鳥格子での水位シーケンス単独では $O(\Delta t^{2})$ を維持する（再現検証コード: `Docs/NumericalVerificationSuite.py`）。

#### (4b) 時間刻み不変・質量保存 4階バイハーモニック空間散逸フィルター (`CSFilterHighFrequency`)
Peregrine 分散逆行列演算 $(I - \alpha \frac{h^2}{3}\nabla^2)^{-1}$ により高周波に対する復元加速度が $1/(1+4\beta) \approx 1/400$ に抑制される結果、MacCormack法本来の3次数値粘性が消失し、$2\Delta x$ ノコギリ型モードがグリッド上に残留する。これを解消するため、毎サブステップ終了時に各状態量 $S \in \{\eta, u, w\}$ に対し4階バイハーモニック散逸フィルターを適用する：

1. **時間刻み幅 $\Delta t$ スケーリング（物理時間あたりの減衰不変性）**:
   連続散逸方程式 $\partial S / \partial t = -\nu \nabla^{4} S$ の離散化に基づき、フィルター係数を時間刻み幅に比例させてスケーリング：

$$
\kappa(\Delta t) = 0.025 \cdot \mathrm{clamp}\left(\frac{\Delta t}{0.008}, 0.05, 2.0\right)
$$

   これにより、時間刻み幅 $\Delta t$ を変化させても物理時間 1秒あたりの減衰率が一定に保たれる（16セル波長において $\Delta t = 0.008, 0.004, 0.002\ \mathrm{s}$ いずれも 1秒後残存振幅 **$93.01\%$**、32セル波長で **$99.54\%$**）。時間刻み収束試験において減衰モデルが変動する問題を根絶した。

2. **ゼロフラックス保守境界ステンシル（完全質量保存）**:
   外周境界面におけるバイハーモニック流束 $J_{-1/2} = J_{W-1/2} = 0$ を厳密に満たすゼロフラックス有限体積ステンシルを採用：

$$
D_{4, x}[0] = 2S_{0} - 3S_{1} + S_{2}
$$
$$
D_{4, x}[1] = -3S_{0} + 6S_{1} - 4S_{2} + S_{3}
$$
$$
D_{4, x}[i] = S_{i-2} - 4S_{i-1} + 6S_{i} - 4S_{i+1} + S_{i+2} \quad (2 \le i \le W-3)
$$
$$
D_{4, x}[W-2] = S_{W-4} - 4S_{W-3} + 6S_{W-2} - 3S_{W-1}
$$
$$
D_{4, x}[W-1] = S_{W-3} - 3S_{W-2} + 2S_{W-1}
$$

   境界端点における単一インパルスおよび任意の一様・ランダム水面変位に対し、和 $\sum_{x=0}^{W-1} D_{4, x} S \equiv 0$、$\sum_{z=0}^{H-1} D_{4, z} S \equiv 0$ がマシンイプシロン精度（実測 $\le 1.95 \times 10^{-14}$）で厳密にゼロとなり、境界セルを含む全水量が完全に保存される。

#### (5) 内部質量ソース造波 (`GetWavemakerSources`) と GPU ソリトン注入 (`CSAddImpulse`)
- **Wei & Kirby (1999) 型内部質量ソース造波 (`GetWavemakerSources`)**:
  沖合ライン $y_{0} = \lfloor v_{\mathrm{wm}} N_{z} \rfloor$ の前後 $|j - y_{0}| \le 18$ セルにおいて、ガウス内部質量ソース $G(j) = \frac{1}{\sqrt{2\pi}\sigma\Delta z}\exp\!\left(-\frac{(j - y_{0})^{2}}{2\sigma^{2}}\right)$（$\sigma = 4.0$ セル）により波を生成する。流速 $w$ への直接点源注入（$S_w$）を行わず質量注入のみとすることで、Helmholtz作用素による運動量不均衡ショックと局所発散スパイクを根絶する：

$$
S_{\eta} = 2 c A_{\mathrm{wm}} \sin(\phi)\, G(j)\, T_{\mathrm{lat}}(i), \quad S_{w} = 0 \quad \left(c = \sqrt{g \max(0.5, h)}\right)
$$

  係数 2 は $+Z$ 進行波と後方 $-Z$ 波（背後スポンジ層で完全吸収）への質量等配分を補償する。
  また側壁境界条件 `SideWallSponge` を無効化（デフォルト）した水路モードでは $T_{\mathrm{lat}} = 1.0$ かつ左右壁面でノイマン条件（$\partial_x a_z = 0$）が成立し、水路幅全域にわたり完全な1D平面波面が維持される。

- **平面孤立波（Planar Soliton）および円形ガウスパルス注入 (`CSAddImpulse`)**:
  - **平面孤立波 (`_ImpulseShape == 0`)**:

$$
\Delta \eta = A\,{\mathrm{sech}}^{2}\!\left(\frac{v - v_{0}}{0.6 R_{\mathrm{uv}}}\right) T_{\mathrm{lat}}(i), \quad c = \sqrt{g(h + \max(0, \Delta\eta))}, \quad \Delta w = \frac{c\,\Delta\eta}{h + \max(0, \Delta\eta)}
$$

    を既存の $(\eta, w)$ に非破壊加算する。
  - **円形パルス (`_ImpulseShape == 1`)**:
    $\Delta \eta = A \exp(-3 (\|\mathbf{u} - \mathbf{u_{0}}\| / R_{\mathrm{uv}})^{2})$ を既存の $\eta$ に非破壊加算する。

#### (5) プロシージャル海底地形 (`UpdateBathymetryTexture`)
正規化座標 $(u, v) \in [0, 1]^{2}$、深水深 $h_{\mathrm{deep}}$、浅水深 $h_{\mathrm{shallow}}$ に対し：
- **`Flat`**: $h(u, v) = h_{\mathrm{deep}}$
- **`LinearSlope`**: $h(u, v) = \max(0.5,\ \mathrm{lerp}(h_{\mathrm{deep}}, h_{\mathrm{shallow}}, v))$
- **`ShoalAndCape`**:
  - 基本傾斜: $h_{\mathrm{base}}(v) = \mathrm{lerp}(h_{\mathrm{deep}},\ h_{\mathrm{shallow}} + 2.0,\ \mathrm{smoothstep}(0.2, 0.9, v))$
  - 斜め浅瀬尾根（Shoal）: $d_{\mathrm{shoal}} = (u - 0.5)\cos(0.6) + (v - 0.45)\sin(0.6),\ f_{\mathrm{shoal}} = \exp(-d_{\mathrm{shoal}}^{2} / 0.025)$
  - 岬・円形海山（Cape）: $d_{\mathrm{cape}} = \|(u, v) - (0.72, 0.62)\|,\ f_{\mathrm{cape}} = \exp(-d_{\mathrm{cape}}^{2} / 0.015)$
  - 合成水深: $h(u, v) = \max\!\left(0.5,\ \mathrm{lerp}\!\left(\mathrm{lerp}(h_{\mathrm{base}}, h_{\mathrm{shallow}}, 0.75 f_{\mathrm{shoal}}),\ 0.5 h_{\mathrm{shallow}},\ 0.85 f_{\mathrm{cape}}\right)\right)$

#### (6) 法線と勾配フォームの算出 (`CSComputeNormal`)
中心差分で勾配を算出し、急傾斜部（$\|\nabla \eta\| > 0.15$）から砕波・白波強度を算出する：

$$
\frac{\partial \eta}{\partial x} = \frac{\eta_{i+1,j} - \eta_{i-1,j}}{2\Delta x}, \quad \frac{\partial \eta}{\partial z} = \frac{\eta_{i,j+1} - \eta_{i,j-1}}{2\Delta x}
$$

$$
\mathbf{N} = \mathrm{normalize}\!\left(-2.0\frac{\partial \eta}{\partial x},\ 1.0,\ -2.0\frac{\partial \eta}{\partial z}\right), \quad \mathrm{Foam} = \mathrm{saturate}\!\left(3.0\left(\|\nabla\eta\| - 0.15\right)\right)
$$

---

## 4. 方式3：スペクトル／FFT海洋モデル (`FFTOceanSimulation`)

実装ファイル: [`FFTOceanSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOceanSimulation.cs), [`FFTOcean.compute`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute)

### 4.1 支配方程式・スペクトル合成モデル

#### (1) 波数格子と Phillips + Swell 複合スペクトル (`Phillips`, `CSGenerateSpectrum`)
解像度 $N \times N$（$N = 2^{M}$）、領域長 $L$ (`_LengthScale1`) に対し、基本波数刻み $\Delta k = \frac{2\pi}{L}$、波数ベクトル $\mathbf{k} = (k_{x}, k_{z}) = (n_{x} - \frac{N}{2},\ n_{z} - \frac{N}{2})\Delta k$（$k = \|\mathbf{k}\|,\ \hat{\mathbf{k}} = \mathbf{k}/k$）を定義する。
風速 $V$ (`_WindSpeed`)、風向単位ベクトル $\hat{\mathbf{w}}$、うねり強度 $A_{\mathrm{swell}}$、うねり方向単位ベクトル $\hat{\mathbf{s}}$ に対する波高分散スペクトル $P(\mathbf{k})$：

$$
P_{\mathrm{Phillips}}(\mathbf{k}) = A_{P} \frac{\exp\!\left(-\frac{1}{(k L_{w})^{2}}\right)}{k^{4}} D_{\mathrm{wind}}(\hat{\mathbf{k}} \cdot \hat{\mathbf{w}}) \exp(-k^{2} l_{0}^{2}), \quad L_{w} = \frac{V^{2}}{g},\ A_{P} = 0.8,\ l_{0} = 0.05\ [\mathrm{m}]
$$

$$
D_{\mathrm{wind}}(\mu) = \begin{cases} \mu^{2} & (\mu > 0) \\ 0.05 & (\mu \le 0) \end{cases}
$$

$$
P_{\mathrm{swell}}(\mathbf{k}) = 0.45 A_{\mathrm{swell}} \exp\!\left(-0.06 \Lambda_{\mathrm{swell}}^{2} (k - k_{\mathrm{swell}})^{2}\right) \max(0, \hat{\mathbf{k}}\cdot\hat{\mathbf{s}})^{4}, \quad \Lambda_{\mathrm{swell}} = \max(8.0,\ 0.28 L),\ k_{\mathrm{swell}} = \frac{2\pi}{\Lambda_{\mathrm{swell}}}
$$

$$
P(\mathbf{k}) = P_{\mathrm{Phillips}}(\mathbf{k}) + P_{\mathrm{swell}}(\mathbf{k})
$$

標準正規乱数ペア $(\xi_{r}, \xi_{i}) \sim \mathcal{N}(0, 1)$（整数の Wang / Jenkins ハッシュ＋Box-Muller 変換で生成）と離散波数刻み $\Delta k = \frac{2\pi}{L}$ を用いて、初期複素振幅 $h_{0}(\mathbf{k})$ と共役対称項 $h_{0}^{\ast}(-\mathbf{k})$ を `_InitialSpectrum` (`ARGBFloat`) に格納する：

$$
h_{0}(\mathbf{k}) = \sqrt{\frac{P(\mathbf{k})}{2}}\,\Delta k\,(\xi_{r} + i\xi_{i}), \quad h_{0}^{\ast}(-\mathbf{k}) = \sqrt{\frac{P(-\mathbf{k})}{2}}\,\Delta k\,(\xi_{r} - i\xi_{i})
$$

ここで周期境界条件を満たす厳密な負波数インデックス：

$$
i_{-} = (N - i) \bmod N, \quad j_{-} = (N - j) \bmod N
$$

から逆波数成分を参照し、DC成分 $(N/2, N/2)$ およびナイキスト境界の反対称非物理モードを零化する。これにより逆変換後の虚数成分残差が機械精度（実数部に対する RMS 比 $3.45 \times 10^{-16}$）に抑え込まれ、完全な実数水面が保証される。またスペクトル離散化に $\Delta k$ を組み込むことで、格子解像度 $N$ を変更しても有義波高 $H_{s} \approx 3.63\ \mathrm{m}$ が一定に保たれる（解像度独立性の確立）。

#### (2) 深水波分散関係による時間発展と複素パッキング単一 2D IFFT (`CSTimeEvolution`)
深水重力波の分散関係 $\omega(k) = \sqrt{g k}$ に従い、時刻 $t$ における複素波高スペクトル $\tilde{h}(\mathbf{k}, t)$ を合成する：

$$
\tilde{h}(\mathbf{k}, t) = h_{0}(\mathbf{k})e^{i\omega(k)t} + h_{0}^{\ast}(-\mathbf{k})e^{-i\omega(k)t}
$$

波峰（$D_{y} > 0$）に向かって水平粒子が収束し鋭いトロコイド波峰を形成する水平変位スペクトル $(\tilde{D_{x}}, \tilde{D_{z}})$ は `+i` 位相で与えられる：

$$
\begin{pmatrix} \tilde{D_{x}}(\mathbf{k}, t) \\ \tilde{D_{z}}(\mathbf{k}, t) \end{pmatrix} = +i\, w_{\mathrm{chop}}(k) \frac{\mathbf{k}}{k} \tilde{h}(\mathbf{k}, t) = w_{\mathrm{chop}}(k)\frac{\mathbf{k}}{k}\left(-\mathrm{Im}\,\tilde{h} + i\,\mathrm{Re}\,\tilde{h}\right)
$$

ここで $w_{\mathrm{chop}}(k) = \frac{2.1}{1 + k / 0.42}$ である。

旧実装では $D_{y}$ と $(D_{x}, D_{z})$ のために2回の 2D IFFT（および計4回の `Graphics.Blit`）を行っていたが、本実装では3つの実関数場を以下の単一4チャンネル複素バッファ `_SpectrumPacked` (`ARGBFloat`) に合成・パッキングする：

$$
\mathrm{Packed.rg} = \tilde{h}(\mathbf{k}, t) + i\,\tilde{D_{x}}(\mathbf{k}, t) = \left(\mathrm{Re}\,\tilde{h} - \mathrm{Im}\,\tilde{D_{x}}\right) + i\left(\mathrm{Im}\,\tilde{h} + \mathrm{Re}\,\tilde{D_{x}}\right)
$$

$$
\mathrm{Packed.ba} = \tilde{D_{z}}(\mathbf{k}, t) + 0i = \mathrm{Re}\,\tilde{D_{z}} + i\,\mathrm{Im}\,\tilde{D_{z}}
$$

これにより、**単一の 4ch 2D IFFT パス（`Graphics.Blit` ゼロ回）** で全3変位成分を同時に空間領域へ逆変換する。

### 4.2 Radix-2 Stockham Autosort 2D IFFT (`CSFFTHorizontal`, `CSFFTVertical`)
Cooley-Tukey 法で必要となるビット反転並べ替えパス（Bit-Reversal Permutation）を完全に排除するため、Ping-Pong バッファ間で自動的に整列しながらバタフライ演算を進める **Stockham Autosort アルゴリズム**を使用する。
ステージ $s = 0, 1, \dots, \log_{2} N - 1$ に対し、ストライド $R = 2^{s},\ P = 2R$ とすると、出力インデックス $x \in [0, N-1]$ に対する読み出しインデックスと回転因子は以下の式（HLSL上ではビット演算 `j = x & (R - 1u)`, `k = x >> s`, `t = k & 1u`, `g = k >> 1u`）のみで求まる：

$$
j = x - R\lfloor x / R \rfloor, \quad k = \lfloor x / R \rfloor, \quad t = k - 2\lfloor k / 2 \rfloor, \quad g = \lfloor k / 2 \rfloor
$$

$$
i_{0} = g R + j, \quad i_{1} = i_{0} + \frac{N}{2}, \quad \theta = \frac{2\pi j}{P}, \quad W = \cos\theta + i\sin\theta
$$

$$
\mathrm{Out}[x] = \begin{cases} \mathrm{In}[i_{0}] + W \cdot \mathrm{In}[i_{1}] & (t == 0) \\ \mathrm{In}[i_{0}] - W \cdot \mathrm{In}[i_{1}] & (t == 1) \end{cases}
$$

これを X 方向に $\log_{2} N$ パス、Y 方向に $\log_{2} N$ パス実行する（計 $2\log_{2} N$ パス/ステップ）。

### 4.3 厳密ラグランジュ法線・ヤコビアン白波・波峰圧縮度の算出 (`CSPackOceanData`)
波数原点を $(N/2, N/2)$ にシフトしていたことによる空間チェッカーボード符号 $(-1)^{x+z}$（HLSL上では `((x + z) & 1) != 0 ? -1.0 : 1.0`）と Choppiness スケール $\lambda$ (`_Choppiness`) を乗じて空間変位ベクトル $\mathbf{D} = (D_{x}, D_{y}, D_{z})$ を復元する：

$$
s(x, z) = (-1)^{x + z}, \quad D_{y} = s \cdot \mathrm{Re}(\mathrm{Packed.rg}), \quad D_{x} = \lambda s \cdot \mathrm{Im}(\mathrm{Packed.rg}), \quad D_{z} = \lambda s \cdot \mathrm{Re}(\mathrm{Packed.ba})
$$

周期境界マスク `& (N - 1)` を用いた中心差分（間隔 $2\Delta x = 2L/N$）から変位勾配テンソル $D_{\alpha,\beta} = \frac{\partial D_{\alpha}}{\partial \beta}$ を計算し、水平写像 $\mathbf{X_{h}}(x,z) = (x + D_{x}, z + D_{z})$ の**ヤコビアン行列式 $J$** を求める：

$$
J = \left(1 + \frac{\partial D_{x}}{\partial x}\right)\left(1 + \frac{\partial D_{z}}{\partial z}\right) - \frac{\partial D_{x}}{\partial z}\frac{\partial D_{z}}{\partial x}
$$

- **白波強度 (Foam)**: $\mathrm{Foam} = \mathrm{saturate}\!\left(2.2(\tau_{\mathrm{foam}} - J)\right)$（$\tau_{\mathrm{foam}}$: `_FoamThreshold`）
- **波峰水平圧縮度 (Crest Pinch)**: $P_{\mathrm{crest}} = \mathrm{saturate}(1.0 - J)$
- **ヤコビアン結合の厳密ラグランジュ法線 $\mathbf{N}$**:
  変位曲面 $\mathbf{X}(x, z) = (x + D_{x},\ D_{y},\ z + D_{z})$ の接線ベクトル $\mathbf{T_{x}} = (1 + D_{x,x},\ D_{y,x},\ D_{z,x})$ と $\mathbf{T_{z}} = (D_{x,z},\ D_{y,z},\ 1 + D_{z,z})$ の外積 $\mathbf{T_{z}} \times \mathbf{T_{x}}$ から真の幾何法線を算出する：

$$
\mathbf{N} = \mathrm{normalize}\!\begin{pmatrix} -\frac{\partial D_{y}}{\partial x}\left(1 + \frac{\partial D_{z}}{\partial z}\right) + \frac{\partial D_{y}}{\partial z}\frac{\partial D_{z}}{\partial x} \\ \max(0.08,\ J) \\ -\frac{\partial D_{y}}{\partial z}\left(1 + \frac{\partial D_{x}}{\partial x}\right) + \frac{\partial D_{y}}{\partial x}\frac{\partial D_{x}}{\partial z} \end{pmatrix}
$$

### 4.4 内部テクスチャ構成 (`FFTOceanSimulation`)
| テクスチャ変数 | フォーマット | 1テクセル容量 | 役割・チャンネル割り当て |
| :--- | :---: | :---: | :--- |
| `initialSpectrumTex` (`_InitialSpectrum`) | `ARGBFloat` | 16 Bytes | `RG`: $h_{0}(\mathbf{k})$、`BA`: $h_{0}^{\ast}(-\mathbf{k})$ |
| `spectrumPackedTex` (`_SpectrumPacked`) | `ARGBFloat` | 16 Bytes | `RG`: $\tilde{h} + i\tilde{D}_{x}$、`BA`: $\tilde{D}_{z} + 0i$ |
| `pingPongTexA`, `pingPongTexB` | `ARGBFloat` | 16 Bytes | Stockham 2D IFFT ワークバッファ |
| `waterDataTex` (`_WaterDataTex`) | `ARGBHalf` | 8 Bytes | 出力変位 $(D_{x}, \eta, D_{z}, \mathrm{Foam})$ |
| `normalTex` (`_NormalTex`) | `ARGBHalf` | 8 Bytes | 出力法線 $(N_{x}, N_{y}, N_{z}, P_{\mathrm{crest}})$ |

---

## 5. 共通水面シェーダー・可視化仕様 (`WaterSurfaceCommon.shader`)

実装ファイル: [`WaterSurfaceCommon.shader`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader), [`WaterRenderer.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterRenderer.cs)

### 5.1 3D頂点変位 (`Vert`)
`_WaterDataTex` をミップレベル0でサンプリングし、3次元変位ベクトル $\mathbf{D} = (D_{x}, \eta, D_{z})$ をワールド座標に加算する：

$$
\mathbf{p_{\mathrm{WS}}} = \mathbf{p_{\mathrm{WS,0}}} + s_{\mathrm{disp}} \cdot (D_{x},\ \eta,\ D_{z})
$$

### 5.2 ピクセルシェーディングと5種の可視化モード (`Frag`)
1. **`Realistic` (`_VisualMode == 0`)**:
   - **Schlick フレネル**: $F = 0.02 + 0.98(1 - \mathrm{saturate}(\mathbf{N}\cdot\mathbf{V}))^{5}$
   - **波高＋波峰圧縮カラーブレンド**: $f_{\mathrm{depth}} = \mathrm{saturate}(0.35\eta + 0.25 P_{\mathrm{crest}} + 0.45)$ により `_DeepColor` と `_BaseColor` を補間。
   - **浅瀬エメラルド透過色（`_HasBathymetry > 0.5` 時）**:
     水深 $h$ に応じた透過係数 $f_{\mathrm{shoal}} = \mathrm{saturate}(\exp(-0.26(h - 1.2)))$ と 2m 間隔の海底等深線コントラストを合成し、浅瀬・岬をエメラルド色 $(0.16, 0.62, 0.56)$ に透過させる。
   - **波峰 Subsurface Scattering (SSS)**:
     Choppy 波の鋭く薄くなった波峰（$P_{\mathrm{crest}} = \mathrm{saturate}(1 - J) > 0$）に対して、逆光・順光に応じたターコイズ透過散乱光 $\mathbf{C_{\mathrm{SSS}}} = (0.14, 0.72, 0.68) \cdot \mathbf{L_{\mathrm{color}}} \cdot P_{\mathrm{crest}}\,\mathrm{saturate}(0.45\eta + 0.35)\,(0.45 + 0.55\,\mathrm{saturate}(-\mathbf{V}\cdot\mathbf{L}\cdot 0.5 + 0.5)) \cdot 0.55$ を加算。
   - **Blinn-Phong 鏡面反射＋Foam ブレンド**: $\mathrm{Spec} = (\mathbf{N}\cdot\mathbf{H})^{64} \cdot \mathrm{Smoothness} \cdot (0.85 F + 0.15) \cdot 2.0$、および $\mathrm{Foam} = \mathrm{saturate}(\mathrm{disp.a} \cdot I_{\mathrm{foam}})$ で白波色を線形補間。
2. **`HeightMap` (`_VisualMode == 1`)**: 正規化波高 $\eta / R_{\mathrm{height}} \in [-1, 1]$ のジェットカラーヒートマップ＋$0.2\ \mathrm{m}$ 間隔の等高線。
3. **`NormalVisual` (`_VisualMode == 2`)**: $0.5\mathbf{N} + 0.5$ の法線RGB表示。
4. **`Wireframe` (`_VisualMode == 3`)**: UV空間 $64\times 64$ グリッドによる水平変位・格子収束の可視化。
5. **`Bathymetry` (`_VisualMode == 4`)**: 静水深 $h \in [1\ \mathrm{m}, 15\ \mathrm{m}]$ の暖色（浅瀬）〜寒色（深海）ヒートマップ＋$1\ \mathrm{m}$ 等深線＋波峰ハイライト（$+0.12\eta$）の重畳表示。

---

## 6. 性能見積もりモデル（VRAM・メモリ帯域・計算量・実行時間）

GPU Compute Shader による格子型・FFT型水面シミュレーションは、算術演算密度（Arithmetic Intensity）よりも**VRAM / キャッシュへのテクスチャ読み書き帯域（Memory Bandwidth）およびディスパッチ起動オーバーヘッド**が支配的（Memory-Bound）である。したがって、以下の解析式から任意の解像度 $N \times N$ における性能を高精度に見積もることができる。

### 6.1 VRAM 使用量の算出式と解像度別一覧

解像度 $N \times N$（ピクセル数 $P = N^{2}$）における各コンポーネントのテクスチャおよびメッシュの VRAM 容量：

1. **水面描画メッシュ (`WaterRenderer`, 解像度 $M \times M$, デフォルト $M = 256$ で $4.50\ \mathrm{MB}$)**:
   - 頂点バッファ（`pos`: 12B, `normal`: 12B, `tangent`: 16B, `uv`: 8B = **48 Bytes/頂点**）＋ インデックスバッファ（32-bit UInt, 6 indices/cell = **24 Bytes/セル**）

$$
\mathrm{VRAM_{Mesh}}(M) = 48 M^{2} + 24 (M - 1)^{2} \approx 72 M^{2}\ [\mathrm{Bytes}]
$$

2. **方式1：2D波動方程式 (`WaveEquationSimulation`)**:
   - `stateTexA` (8B) + `stateTexB` (8B) + `normalTex` (8B) + `waterDataTex` (8B) + `obstacleTex` (1B) = **33 Bytes/px**

$$
\mathrm{VRAM_{WaveEq}}(N) = 33 N^{2}\ [\mathrm{Bytes}]
$$

3. **方式2：Boussinesqモデル (`BoussinesqSimulation`)**:
   - `stateTexA` (16B) + `stateTexB` (16B) + `predTex` (16B) + `forceTex` (8B) + `accelTexA` (8B) + `accelTexB` (8B) + `normalTex` (8B) + `waterDataTex` (8B) + `bathymetryTex` (8B) = **96 Bytes/px**

$$
\mathrm{VRAM_{Boussinesq}}(N) = 96 N^{2}\ [\mathrm{Bytes}]
$$

4. **方式3：FFT海洋モデル (`FFTOceanSimulation`)**:
   - `initialSpectrumTex` (16B) + `spectrumDyTex` (16B) + `spectrumDxDzTex` (16B) + `pingPongTexA` (16B) + `pingPongTexB` (16B) + `waterDataTex` (8B) + `normalTex` (8B) = **96 Bytes/px**

$$
\mathrm{VRAM_{FFT}}(N) = 96 N^{2}\ [\mathrm{Bytes}]
$$

| 解像度 $N \times N$ | 総セル数 $N^{2}$ | 方式1: 2D波動方程式 | 方式2: Boussinesq | 方式3: FFT海面 | 共通メッシュ ($256^{2}$) |
| :---: | :---: | :---: | :---: | :---: | :---: |
| **$256 \times 256$** | $65,536$ | **$2.06\ \mathrm{MB}$** | **$6.00\ \mathrm{MB}$** | **$6.00\ \mathrm{MB}$** | $4.50\ \mathrm{MB}$ |
| **$512 \times 512$** | $262,144$ | **$8.25\ \mathrm{MB}$** | **$24.00\ \mathrm{MB}$** | **$24.00\ \mathrm{MB}$** | $4.50\ \mathrm{MB}$ |
| **$1024 \times 1024$** | $1,048,576$ | **$33.00\ \mathrm{MB}$** | **$96.00\ \mathrm{MB}$** | **$96.00\ \mathrm{MB}$** | $4.50\ \mathrm{MB}$ |

---

### 6.2 1フレームあたりのディスパッチ回数とメモリ転送量（Bandwidth）モデル

各カーネル1パスあたりの1セル平均実効メモリアクセス量（L1/L2テクスチャキャッシュヒット考慮後のDRAM/L2転送量：Read + Write）とディスパッチ回数 $D_{\mathrm{frame}}$ を以下に示す。

#### 方式1：2D波動方程式（サブステップ数 $M_{\mathrm{sub}}$）
- **`CSStep`** ($\times M_{\mathrm{sub}}$ 回): Read `_CurrentState` (8B) + `_ObstacleTex` (1B) + Write `_NextState` (8B) $\approx \mathbf{17\ \mathrm{B/px}}$（9点ステンシルの隣接読み出しは $8\times 8$ スレッドグループ内で L1 キャッシュにヒット）。
- **`CSComputeNormal`** ($\times 1$ 回): Read `_NextState` (8B) + Write `_NormalTex` (8B) + Write `_WaterDataTex` (8B) $\approx \mathbf{24\ \mathrm{B/px}}$。

$$
D_{\mathrm{WaveEq}} = M_{\mathrm{sub}} + 1, \quad \mathrm{Traffic_{WaveEq}}(N, M_{\mathrm{sub}}) = (17 M_{\mathrm{sub}} + 24) N^{2}\ [\mathrm{Bytes/step}]
$$

#### 方式2：Boussinesqモデル（サブステップ数 $M_{\mathrm{sub}}$, ADI PCR分散解法）
- サブステップ内処理 ($\times M_{\mathrm{sub}}$ 回):
  1. `CSPredict`: Read `_StateIn` (16B) + Read `_BathymetryTex` (8B) + Write `_ForceTex` (8B) + Write `_StatePred` (16B) = $\mathbf{48\ \mathrm{B/px}}$
  2. `CSTridiagonalX` (Predictor X-PCR): Read `_ForceTex` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPong` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  3. `CSTridiagonalZ` (Predictor Z-PCR): Read `_AccelPong` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPing` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  4. `CSApplyPredictedDispersion`: R/W `_StatePred` (32B) + Read `_AccelPing` (8B) + Write `_ForcePredTex` (8B) = $\mathbf{48\ \mathrm{B/px}}$
  5. `CSCorrect`: Read `_StateIn` (16B) + Read `_StatePred` (16B) + Read `_BathymetryTex` (8B) + Write `_ForceTex` (8B) + Write `_StateOut` (16B) = $\mathbf{64\ \mathrm{B/px}}$
  6. `CSTridiagonalX` (Corrector X-PCR): Read `_ForceTex` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPong` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  7. `CSTridiagonalZ` (Corrector Z-PCR): Read `_AccelPong` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPing` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  8. `CSApplyDispersion`: R/W `_StateOut` (32B) + Read `_ForcePredTex` (8B) + Read `_AccelPing` (8B) = $\mathbf{48\ \mathrm{B/px}}$
- ステップ完了時 ($\times 1$ 回): `CSComputeNormal`: Read `_StateOut` (16B) + Write `_NormalTex` (8B) + Write `_WaterDataTex` (8B) = $\mathbf{32\ \mathrm{B/px}}$

$$
D_{\mathrm{Boussinesq}} = 8 M_{\mathrm{sub}} + 1
$$

$$
\mathrm{Traffic_{Boussinesq}}(N, M_{\mathrm{sub}}) = (304 M_{\mathrm{sub}} + 32) N^{2}\ [\mathrm{Bytes/step}]
$$

#### 方式3：スペクトル／FFT海面（1ステップあたり単一複素パッキング 2D IFFT：ステージ数 $S = \log_{2} N$）
- **`CSTimeEvolution`** ($\times 1$ 回): Read `_InitialSpectrum` (16B) + Write `_SpectrumPacked` (16B) = $\mathbf{32\ \mathrm{B/px}}$
- **`Perform2DIFFT`** ($\times 1$ 回：各 $2\log_{2} N$ ステージ、`Graphics.Blit` ゼロ回):
  - 各バタフライパス (`CSFFTHorizontal` / `CSFFTVertical`): Read `_InputPing` (2点 $\to$ キャッシュ考慮実効 24B) + Write `_OutputPong` (16B) $\approx \mathbf{40\ \mathrm{B/px}}$
  - 2D IFFT 1回あたり: $(40 \times 2\log_{2} N)\ \mathrm{B/px} = (80\log_{2} N)\ \mathrm{B/px}$
- **`CSPackOceanData`** ($\times 1$ 回): Read `_SpectrumPacked` (16B) + Write `_WaterDataTex` (8B) + Write `_NormalTex` (8B) = $\mathbf{32\ \mathrm{B/px}}$

$$
D_{\mathrm{FFT}} = 2 \log_{2} N + 2, \quad \mathrm{Traffic_{FFT}}(N) = (80 \log_{2} N + 64) N^{2}\ [\mathrm{Bytes/step}]
$$

（※ 不要なスペクトル再生成をdirtyフラグとセッター同値判定で遮断しており、定常時の再生成ディスパッチはゼロ `RedundantSpectrumRegen = 0`）

---

### 6.3 GPU 実行時間の見積もり式と実測ベンチマークとの照合

GPU の実効メモリ帯域幅を $B_{\mathrm{eff}}\ [\mathrm{GB/s}]$、1ディスパッチあたりのドライバ・バリア同期オーバーヘッドを $t_{\mathrm{disp}}\ [\mathrm{ms}]$（D3D11/D3D12 で通常 $0.004 \sim 0.008\ \mathrm{ms}$）とすると、1ステップあたりの GPU 計算時間 $T_{\mathrm{GPU}}$ は以下の式で見積もられる：

$$
T_{\mathrm{GPU}} \approx \frac{\mathrm{Traffic}}{B_{\mathrm{eff}}} + D_{\mathrm{step}} \cdot t_{\mathrm{disp}}
$$

#### 実測ベンチマーク値（Unity 6000.3.27f1 バッチモード実行、`Docs/BenchmarkResults.csv` より抜粋）

| カテゴリ | 方式 | 解像度 | 構成・モード | サブステップ数 | ディスパッチ数 | 1ステップ時間 (Wall Clock) | 備考 |
| :--- | :--- | :---: | :--- | :---: | :---: | :---: | :--- |
| **SolverOnly** | 01 2D波動方程式 | $256 \times 256$ | RGFloat・シンプレクティック | $2$ | $3$ | **$0.183\ \mathrm{ms}$** | $c=8\ \mathrm{m/s}, L=50\ \mathrm{m}$ |
| **SolverOnly** | 01 2D波動方程式 | $512 \times 512$ | RGFloat・シンプレクティック | $3$ | $4$ | **$0.298\ \mathrm{ms}$** | $c=8\ \mathrm{m/s}, L=50\ \mathrm{m}$ |
| **SolverOnly** | 02 Boussinesq | $256 \times 256$ | Peregrine ADI PCR | $2$ | $17$ | **$1.055\ \mathrm{ms}$** | $\alpha=1.0, h=3.5\ \mathrm{m}$ |
| **SolverOnly** | 02 Boussinesq | $512 \times 512$ | Peregrine ADI PCR | $4$ | $33$ | **$4.544\ \mathrm{ms}$** | $\alpha=1.0, h=3.5\ \mathrm{m}$ |
| **SolverOnly** | 03 FFT海面 | $256 \times 256$ | 単一IFFT複素パッキング | $1$ | $18$ | **$0.317\ \mathrm{ms}$** | 再生成回数 $0$ |
| **SolverOnly** | 03 FFT海面 | $512 \times 512$ | 単一IFFT複素パッキング | $1$ | $20$ | **$0.869\ \mathrm{ms}$** | 再生成回数 $0$ |

#### 複合シーン04（512×512 2水面連成）におけるソルバー・描画・非同期断面読み戻しの分離計測

| 計測モード | 実行対象 | 1ステップ時間 | ボトルネック要因と考察 |
| :--- | :--- | :---: | :--- |
| **1_SolverOnly** | 波動方程式 + Boussinesq (各512²) | **$12.73\ \mathrm{ms}$** | 2つの高解像度ソルバーの純粋なCompute Shader実行時間 |
| **2_SolverPlusRender** | ソルバー + カメラ描画 (1280×720) | **$8.30\ \mathrm{ms}$** | URPレンダーパスとシェーダー頂点変位の並列実行 |
| **3_SolverRenderProfileAsync** | ソルバー + 描画 + 断面プロファイル | **$11.80\ \mathrm{ms}$** | `AsyncGPUReadback` による非同期取得＋静水深CPUキャッシュ |

> [!TIP]
> **ターゲット環境へのスケーリング則**:
> - **解像度を $2\times$（例：$256^{2} \to 512^{2}$）にした場合**:
>   - 2D波動方程式およびBoussinesq系は、セル数 $N^{2}$ が $4\times$ になるのに加え、CFL条件 $\Delta t \propto \Delta x = L/N$ によりサブステップ数 $M_{\mathrm{sub}}$ が $2\times$ になるため、同一領域サイズ $L$ での単位時間あたり計算コストは **$4 \times 2 = 8\times$** にスケールする（固定 `fixedTimeStep` 内で $M_{\mathrm{sub}}$ が繰り上がる閾値で階段状に増加）。
>   - FFT海面は CFL 制約が存在しないため、解像度を $2\times$ にしても時間刻みは変わらず、計算コストは **$4 \frac{\log_{2} N + 1}{\log_{2} N} \approx 4.4\times$** のみの増加で済む。

---

## 7. 全シーン UI 機能と実装の対応一覧（検証済みマトリクス）

全5シーンの HUD（[`WaterSampleUI.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterSampleUI.cs) / [`WaterComparisonController.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterComparisonController.cs)）に表示される全操作項目と実装メソッドの対応関係：

| シーン | UIセクション・操作項目 | バインド先プロパティ / メソッド |
| :--- | :--- | :--- |
| **共通** | `Play` / `Pause`, `Step (0.016s)`, `Reset`, `TimeScale` | `WaterSimulationBase.Play/Pause/Step/ResetSimulation/TimeScale`（シーン04/05では `WaterComparisonController` 経由で全水面を同期制御） |
| **共通** | `Visual Mode`: `Realistic`, `HeightMap`, `Normal`, `Wireframe`, `Bathymetry` | `WaterRenderer.SetVisualMode`（シーン04/05では `WaterComparisonController.SetVisualModeAll`） |
| **共通** | `Camera View`: `Default View`, `Low Angle View` | `SimpleCameraController.ResetToDefaultView` / `SetLowAngleView` |
| **01_WaveEquation** | `Obstacles`: `None`, `Single`, `Double`, `Pillars` | `WaveEquationSimulation.SetObstaclePattern` |
| **01_WaveEquation** | `Source Mode`: `Impulse`, `Oscillator`, `Moving`, `Mouse` | `WaveEquationSimulation.SourceMode` |
| **01_WaveEquation** | `★ Trigger Impulse`, `Interval`, `WaveSpeed`, `Damping` | `TriggerImpulse()`, `PulseInterval`, `WaveSpeed`, `Damping` |
| **02_Boussinesq** | `Bathymetry`: `Flat`, `Shoal`, `Slope` | `BoussinesqSimulation.SetBathymetryType` |
| **02_Boussinesq** | `Source Mode`: `Wavemaker`, `Impulse`, `Periodic`, `Moving`, `Mouse` | `BoussinesqSimulation.Condition` |
| **02_Boussinesq** | `Shape`: `Planar (平面孤立波)`, `Circular (円形波)` | `BoussinesqSimulation.ImpulseShape` (`CSAddImpulse`) |
| **02_Boussinesq** | `Freq`, `Interval`, `★ Trigger Impulse`, `Dispersion`, `Wave Amp` | `WavemakerFrequency`, `PulseInterval`, `TriggerImpulse()`, `DispersionAlpha`, `WavemakerAmplitude` |
| **02_Boussinesq** | `Sweeps (ADI PCR)` | `BoussinesqSimulation.DispersionSweeps` (1 sweep direct solve) |
| **03_FFTWater** | `Sea State`: `Calm`, `Swell`, `Stormy` | `FFTOceanSimulation.ApplySeaStatePreset` |
| **03_FFTWater** | `Wind`, `Wind Dir`, `Choppy`, `Swell`, `Swell Dir`, `Foam` | `WindSpeed`, `WindDirectionDegrees`, `Choppiness`, `SwellStrength`, `SwellDirectionDegrees`, `EnableFoam` |
| **04_Comparison** | `Profile Scale (断面高さ強調)` / `ProfileH` | `WaterComparisonController.ProfileVerticalScale` $\to$ `WaterProfileViewer.VerticalScale` |
| **04_Comparison** | `Profile Viewer Mode` | `WaterProfileViewer` (`AsyncGPUReadback` & 静水深CPUキャッシュ) |
