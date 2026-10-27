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
| `stateTexA`, `stateTexB` (`_CurrentState`, `_NextState`) | `ARGBHalf` (8 B/px) | Ping-Pong 状態バッファ。`R`: 現在水位 $\eta^{n}$、`G`: 前ステップ水位 $\eta^{n-1}$、`B`: 鉛直速度 $v^{n} = (\eta^{n} - \eta^{n-1})/\Delta t$、`A`: 0 |
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

#### (3) 半陰的減衰 Leapfrog / Verlet 時間積分 (`CSStep`)
2階時間微分を中心差分、1階減衰項を $\eta^{n+1}$ と $\eta^{n-1}$ の中心差分 $\frac{\eta^{n+1} - \eta^{n-1}}{2\Delta t}$ で半陰的に評価し、$\eta^{n+1}$ について陽的に解く：

$$
\frac{\eta^{n+1} - 2\eta^{n} + \eta^{n-1}}{\Delta t^{2}} + \gamma_{\mathrm{eff}} \frac{\eta^{n+1} - \eta^{n-1}}{2\Delta t} = c^{2} \nabla^{2} \eta^{n}
$$

$$
\eta^{n+1} = \frac{2\eta^{n} - \left(1 - \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t\right)\eta^{n-1} + c^{2} \Delta t^{2} \nabla^{2} \eta^{n}}{1 + \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t}
$$

ここで外周スポンジ減衰 $\gamma_{\mathrm{sponge}}$ は領域境界からの最短セル距離 $d_{\mathrm{border}} = \min(i,\ N_{x} - 1 - i,\ j,\ N_{z} - 1 - j)$ に対して：

$$
\gamma_{\mathrm{sponge}} = \begin{cases} S_{\mathrm{sponge}} \left(1 - \frac{d_{\mathrm{border}}}{W_{\mathrm{sponge}}}\right)^{2} & (d_{\mathrm{border}} < W_{\mathrm{sponge}}) \\ 0 & (d_{\mathrm{border}} \ge W_{\mathrm{sponge}}) \end{cases}
$$

また `_ObstacleTex[pos].r > 0.5` の障害物セルでは $\eta^{n+1} = \eta^{n} = 0$（固定端 Dirichlet 境界条件）を課し、全反射およびスリット回折を発生させる。

#### (4) 初速ゼロのガウスインパルス注入 (`CSAddImpulse`)
UV中心 $\mathbf{u_{0}}$、半径 $R_{\mathrm{uv}}$、振幅 $A$ に対し、距離 $d = \|\mathbf{u} - \mathbf{u_{0}}\| < R_{\mathrm{uv}}$ のセルへ：

$$
\Delta \eta = A \exp\!\left(-3\left(\frac{d}{R_{\mathrm{uv}}}\right)^{2}\right)
$$

を **`state.r` ($\eta^{n}$) と `state.g` ($\eta^{n-1}$) の双方に同量加算**する。これにより初期鉛直速度 $\frac{\eta^{n} - \eta^{n-1}}{\Delta t}$ に不連続な衝撃を与えずに純粋な初期変位として波紋を励起する。

#### (5) 法線と曲率フォームの算出 (`CSComputeNormal`)
中心差分で勾配を算出し、5点ラプラシアンによる波峰の曲率から白波強度を算出する（サブステップループ完了後に1回だけ実行）：

$$
\frac{\partial \eta}{\partial x} = \frac{\eta_{i+1,j} - \eta_{i-1,j}}{2\Delta x}, \quad \frac{\partial \eta}{\partial z} = \frac{\eta_{i,j+1} - \eta_{i,j-1}}{2\Delta x}
$$

$$
\mathbf{N} = \mathrm{normalize}\!\left(-2.5\frac{\partial \eta}{\partial x},\ 1.0,\ -2.5\frac{\partial \eta}{\partial z}\right), \quad \mathrm{Foam} = \mathrm{saturate}\!\left(-2.0 \left(\eta_{i-1,j} + \eta_{i+1,j} + \eta_{i,j-1} + \eta_{i,j+1} - 4\eta_{i,j}\right)\right)
$$

---

## 3. 方式2：Boussinesq系浅水・分散波モデル (`BoussinesqSimulation`)

実装ファイル: [`BoussinesqSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs), [`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute)

### 3.1 支配方程式 (Peregrine 1967 弱非線形・弱分散浅水波方程式)
水深変化のある海底 $h(x, z)$ 上における質量保存則（連続の式）と水深平均水平流速 $\mathbf{u} = (u, w)^{\mathrm{T}}$ の運動量保存則：

$$
\frac{\partial \eta}{\partial t} + \frac{\partial (Hu)}{\partial x} + \frac{\partial (Hw)}{\partial z} = S_{\eta}
$$

$$
\left(1 - \alpha \frac{h^{2}}{6}\nabla^{2}\right)\frac{\partial \mathbf{u}}{\partial t} = -g\nabla\eta - (\mathbf{u}\cdot\nabla)\mathbf{u} - \frac{c_{f}}{H}\mathbf{u} + \mathbf{S_{u}}
$$

- $H(x, z, t) = \max(0.05,\ \max(0.2, h) + \eta)$: 全水深 $[\mathrm{m}]$
- $\alpha \in [0, 1.2]$: 分散係数（$\alpha = 1.0$: Boussinesq 分散波、$\alpha = 0.0$: 非分散浅水波方程式 NLSW）
- $c_{f}$: 海底摩擦係数 (`_BedFriction`)
- $S_{\eta},\ \mathbf{S_{u}} = (0, S_{w})^{\mathrm{T}}$: 内部造波ソース項

### 3.2 内部テクスチャ構成
微小時間刻み（$\Delta t \approx 0.004 \sim 0.01\ \mathrm{s}$）での量子化誤差を防ぐため、状態・外力・加速度バッファには **`fp32` (`ARGBFloat` / `RGFloat`)** を使用する。

| テクスチャ変数 | フォーマット | 1テクセル容量 | 役割・チャンネル割り当て |
| :--- | :---: | :---: | :--- |
| `stateTexA`, `stateTexB` (`_StateIn`, `_StateOut`) | `ARGBFloat` | 16 Bytes | Ping-Pong 状態バッファ。`R`: $\eta\ [\mathrm{m}]$、`G`: $u\ [\mathrm{m/s}]$、`B`: $w\ [\mathrm{m/s}]$、`A`: 0 |
| `predTex` (`_StatePred`) | `ARGBFloat` | 16 Bytes | MacCormack 予測ステップの中間状態 $(\eta^{\ast}, u^{\ast}, w^{\ast}, 0)$ |
| `forceTex` (`_ForceTex`) | `RGFloat` | 8 Bytes | 非分散の運動量右辺外力ベクトル $\mathbf{f} = (f_{x}, f_{z})$ |
| `accelTexA`, `accelTexB` (`_AccelPing`, `_AccelPong`) | `RGFloat` | 8 Bytes | Helmholtz 分散反復用の加速度ベクトル $\mathbf{a} = (a_{x}, a_{z})$ |
| `bathymetryTex` (`_BathymetryTex`) | `ARGBHalf` | 8 Bytes | `R`: 静水深 $h\ [\mathrm{m}]$、`G`: 海底標高 $z_{b} = -h\ [\mathrm{m}]$ |

### 3.3 数値離散化スキーム

#### (1) CFL 条件と自動サブステップ分割
領域内の最大水深 $h_{\max}$ (`deepWaterDepth`) における長波の位相速度上限 $c_{\max} = \sqrt{g h_{\max}}$ に基づきサブステップ分割を行う：

$$
\Delta t_{\max} = 0.45 \frac{\Delta x}{\sqrt{g h_{\max}}}, \quad M_{\mathrm{sub}} = \max\!\left(1,\ \left\lceil \frac{dt}{\Delta t_{\max}} \right\rceil\right), \quad \Delta t = \frac{dt}{M_{\mathrm{sub}}}
$$

#### (2) 対称 MacCormack 予測子・修正子法 (`CSPredict` & `CSCorrect`)
コロケート格子上の奇偶デカップリング（$2\Delta x$ 市松模様ノイズ）を消去しつつ時間・空間2次精度 $O(\Delta t^{2}, \Delta x^{2})$ を得るため、MacCormack 法を採用する。さらにサブステップごとにパリティ $\sigma_{\mathrm{parity}} \in \lbrace 0, 1 \rbrace$（`pingPong ? 1 : 0`）を交互に切り替え、差分方向の非対称バイアスを完全に相殺する。

1. **Predictor (`CSPredict`)**:
   方向符号 $s_{1} = (\sigma_{\mathrm{parity}} == 0)\ ?\ +1 : -1$ を用いて隣接セル $(i + s_{1}, j),\ (i, j + s_{1})$ との片側差分（符号付き逆格子間隔 $\tilde{\Delta_{1}} = s_{1} / \Delta x$）を評価する：

$$
\delta_{x}^{(1)}(Hu) = (H_{i+s_{1},j}u_{i+s_{1},j} - H_{i,j}u_{i,j})\tilde{\Delta_{1}}, \quad \delta_{z}^{(1)}(Hw) = (H_{i,j+s_{1}}w_{i,j+s_{1}} - H_{i,j}w_{i,j})\tilde{\Delta_{1}}
$$

$$
\eta^{\ast} = D_{\mathrm{sponge}} \cdot \left(\eta^{n} - \Delta t\left(\delta_{x}^{(1)}(Hu) + \delta_{z}^{(1)}(Hw) - S_{\eta}(\phi^{n})\right)\right)
$$

   同様に速度勾配・水位勾配を片側差分 $\tilde{\Delta_{1}}$ で評価して右辺外力 $\mathbf{f^{n}} = (f_{x}^{n}, f_{z}^{n})$ を `_ForceTex` に保存し、予測速度 $\mathbf{u^{\ast}} = D_{\mathrm{sponge}} \cdot (\mathbf{u^{n}} + \Delta t \mathbf{f^{n}})$ を `_StatePred` に書き出す。

2. **Corrector (`CSCorrect`)**:
   逆方向符号 $s_{2} = -s_{1}$（$\tilde{\Delta_{2}} = s_{2} / \Delta x$）を用いて予測状態 `_StatePred` の逆向き片側差分を評価し、現在状態 $\eta^{n}$ と平均化する：

$$
\eta^{n+1} = D_{\mathrm{sponge}} \cdot \frac{1}{2}\left[\eta^{n} + \eta^{\ast} - \Delta t\left(\delta_{x}^{(2)}(H^{\ast} u^{\ast}) + \delta_{z}^{(2)}(H^{\ast} w^{\ast}) - S_{\eta}(\phi^{n+1})\right)\right]
$$

   同様に予測状態から評価した外力 $\mathbf{f^{\ast}} = (f_{x}^{\ast}, f_{z}^{\ast})$ と予測ステップの外力 $\mathbf{f^{n}}$ の2次精度平均：

$$
\bar{\mathbf{f}} = \frac{1}{2}(\mathbf{f^{n}} + \mathbf{f^{\ast}})
$$

   を `_ForceTex` および初期推定値 `_AccelPing` に格納する。

#### (3) 陰的 Helmholtz 分散インバータ (`CSJacobiStep` & `CSApplyDispersion`)
運動方程式左辺の分散作用素 $(I - \alpha \frac{h^{2}}{6}\nabla^{2})\mathbf{a} = \bar{\mathbf{f}}$ を離散化すると、各格子点 $(i, j)$ について以下の5点楕円型方程式となる：

$$
(1 + 4\beta_{i,j})\mathbf{a_{i,j}} - \beta_{i,j}(\mathbf{a_{i-1,j}} + \mathbf{a_{i+1,j}} + \mathbf{a_{i,j-1}} + \mathbf{a_{i,j+1}}) = \bar{\mathbf{f_{i,j}}}, \quad \beta_{i,j} = \mathrm{clamp}\!\left(\alpha \frac{h_{i,j}^{2}}{6\Delta x^{2}},\ 0.0,\ 6.0\right)
$$

これを初期値 $\mathbf{a^{(0)}} = \bar{\mathbf{f}}$（時間位相遅れを防ぐため毎サブステップ現時刻の $\bar{\mathbf{f}}$ から開始）として **6回の Jacobi 反復**（$k = 0, \dots, 5$）：

$$
\mathbf{a_{i,j}^{(k+1)}} = \frac{\bar{\mathbf{f_{i,j}}} + \beta_{i,j}(\mathbf{a_{i-1,j}^{(k)}} + \mathbf{a_{i+1,j}^{(k)}} + \mathbf{a_{i,j-1}^{(k)}} + \mathbf{a_{i,j+1}^{(k)}})}{1 + 4\beta_{i,j}}
$$

で解き、`CSApplyDispersion` にて流速を更新する：

$$
\mathbf{u^{n+1}} = D_{\mathrm{sponge}} \cdot \left(\mathbf{u^{n}} + \Delta t\,\mathbf{a^{(6)}}\right)
$$

ここで外周スポンジ減衰係数は $d_{\mathrm{border}} < W_{\mathrm{sponge}}$ において陰的分数式：

$$
D_{\mathrm{sponge}} = \frac{1}{1 + 15\,\Delta t\, S_{\mathrm{sponge}} \left(1 - \frac{d_{\mathrm{border}}}{W_{\mathrm{sponge}}}\right)^{2}}
$$

で与えられる。

#### (4) 進行波造波ソース (`GetWavemakerSources`) と GPU ソリトン注入 (`CSAddImpulse`)
- **連続進行波造波 (`GetWavemakerSources`)**:
  沖合ライン $y_{0} = \lfloor v_{\mathrm{wm}} N_{z} \rfloor$ の前後 $|j - y_{0}| \le 10$ セルにおいて、浅瀬からの反射波を透過させつつ $+Z$ 方向への純粋な進行波を合成するため、ガウス分布 $G(j) = \frac{1}{\sqrt{2\pi}\sigma\Delta x}\exp\!\left(-\frac{(j - y_{0})^{2}}{2\sigma^{2}}\right)$（$\sigma = 2.5$ セル）と横方向スポンジ干渉防止テーパー $T_{\mathrm{lat}}(i) = \mathrm{smoothstep}(0,\ 1.5 W_{\mathrm{sponge}},\ \min(i, N_{x} - 1 - i))$ を用いて質量ソースと運動量ソースを同時注入する：

$$
W(i, j, \phi) = A_{\mathrm{wm}} \sin(\phi)\, G(j)\, T_{\mathrm{lat}}(i), \quad S_{\eta} = c W, \quad S_{w} = g W \quad \left(c = \sqrt{g \max(0.5, h)}\right)
$$

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

標準正規乱数ペア $(\xi_{r}, \xi_{i}) \sim \mathcal{N}(0, 1)$（整数の Wang / Jenkins ハッシュ＋Box-Muller 変換で生成）を用いて、初期複素振幅 $h_{0}(\mathbf{k})$ と共役対称項 $h_{0}^{\ast}(-\mathbf{k})$ を `_InitialSpectrum` (`ARGBFloat`) に格納する：

$$
h_{0}(\mathbf{k}) = \sqrt{\frac{P(\mathbf{k})}{2}}(\xi_{r} + i\xi_{i}), \quad h_{0}^{\ast}(-\mathbf{k}) = \sqrt{\frac{P(-\mathbf{k})}{2}}(\xi_{r} - i\xi_{i})
$$

#### (2) 深水波分散関係による時間発展と波数重み付き Choppy スペクトル (`CSTimeEvolution`)
深水重力波の分散関係 $\omega(k) = \sqrt{g k}$ に従い、時刻 $t$ における複素波高スペクトル $\tilde{h}(\mathbf{k}, t)$ を合成する（エルミート対称性 $\tilde{h}(-\mathbf{k}, t)^{\ast} = \tilde{h}(\mathbf{k}, t)$ により逆変換後の実数性が保証される）：

$$
\tilde{h}(\mathbf{k}, t) = h_{0}(\mathbf{k})e^{i\omega(k)t} + h_{0}^{\ast}(-\mathbf{k})e^{-i\omega(k)t}
$$

本実装の 2D IFFT カーネルは回転因子 $\exp(+i 2\pi \mathbf{n}\cdot\mathbf{m}/N)$（$+i$ 規約）を採用しているため、波峰（$D_{y} > 0$）に向かって水平粒子が収束（$\nabla\cdot\mathbf{D_{h}} < 0$）し鋭いトロコイド波峰を形成する水平変位スペクトル $(\tilde{D_{x}}, \tilde{D_{z}})$ は **`+i` 位相**で与えられる：

$$
\begin{pmatrix} \tilde{D_{x}}(\mathbf{k}, t) \\ \tilde{D_{z}}(\mathbf{k}, t) \end{pmatrix} = +i\, w_{\mathrm{chop}}(k) \frac{\mathbf{k}}{k} \tilde{h}(\mathbf{k}, t) = w_{\mathrm{chop}}(k)\frac{\mathbf{k}}{k}\left(-\mathrm{Im}\,\tilde{h} + i\,\mathrm{Re}\,\tilde{h}\right)
$$

ここで $w_{\mathrm{chop}}(k)$ は、長〜中波長のうねり・主波に十分な水平収束を与えつつ高波数（短波長）成分の過剰な折り返し（ループ自己交差 $J \ll 0$）を抑制する波数エンベロープである：

$$
w_{\mathrm{chop}}(k) = \frac{2.1}{1 + k / k_{\mathrm{ref}}}, \quad k_{\mathrm{ref}} = 0.42\ [\mathrm{rad/m}]
$$

なお、2つの実関数場 $D_{x}(\mathbf{x}), D_{z}(\mathbf{x})$ のスペクトルはエルミート対称であるため、`_SpectrumDxDz` の `RG` に $\tilde{D_{x}}$、`BA` に $\tilde{D_{z}}$ を格納して1回の 4ch 2D IFFT で同時に逆変換を行う。

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

これを X 方向に $\log_{2} N$ パス、Y 方向に $\log_{2} N$ パス実行する（計 $2\log_{2} N$ パス/IFFT）。

### 4.3 厳密ラグランジュ法線・ヤコビアン白波・波峰圧縮度の算出 (`CSPackOceanData`)
波数原点を $(N/2, N/2)$ にシフトしていたことによる空間チェッカーボード符号 $(-1)^{x+z}$（HLSL上では `((x + z) & 1) != 0 ? -1.0 : 1.0`）と正規化係数 $1/N$、および Choppiness スケール $\lambda$ (`_Choppiness`) を乗じて空間変位ベクトル $\mathbf{D} = (D_{x}, D_{y}, D_{z})$ を取り出す：

$$
s(x, z) = \frac{(-1)^{x + z}}{N}, \quad D_{y} = s \cdot \mathrm{Re}(\mathrm{IFFT}(\tilde{h})), \quad D_{x} = \lambda s \cdot \mathrm{Re}(\mathrm{IFFT}(\tilde{D_{x}})), \quad D_{z} = \lambda s \cdot \mathrm{Re}(\mathrm{IFFT}(\tilde{D_{z}}))
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

  波峰が収束して $J \to 0.08$ に近づくと法線の水平成分が鋭く立ち上がり、トロコイド波特有の鋭利な稜線陰影とフレネル反射が生成される。

### 4.4 内部テクスチャ構成 (`FFTOceanSimulation`)
| テクスチャ変数 | フォーマット | 1テクセル容量 | 役割・チャンネル割り当て |
| :--- | :---: | :---: | :--- |
| `initialSpectrumTex` (`_InitialSpectrum`) | `ARGBFloat` | 16 Bytes | `RG`: $h_{0}(\mathbf{k})$、`BA`: $h_{0}^{\ast}(-\mathbf{k})$ |
| `spectrumDyTex` (`_SpectrumDy`) | `ARGBFloat` | 16 Bytes | `RG`: $\tilde{h}(\mathbf{k}, t)$（IFFT後 $D_{y}$） |
| `spectrumDxDzTex` (`_SpectrumDxDz`) | `ARGBFloat` | 16 Bytes | `RG`: $\tilde{D_{x}} \to D_{x}$、`BA`: $\tilde{D_{z}} \to D_{z}$ |
| `pingPongTexA`, `pingPongTexB` | `ARGBFloat` | 16 Bytes | Stockham 2D IFFT ワークバッファ |

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

#### 方式2：Boussinesqモデル（サブステップ数 $M_{\mathrm{sub}}$, Jacobi反復数 $K_{J} = 6$）
- **`CSPredict`** ($\times M_{\mathrm{sub}}$ 回): Read `_StateIn` (16B) + Read `_BathymetryTex` (8B) + Write `_ForceTex` (8B) + Write `_StatePred` (16B) = $\mathbf{48\ \mathrm{B/px}}$
- **`CSCorrect`** ($\times M_{\mathrm{sub}}$ 回): Read `_StateIn` (16B) + Read `_StatePred` (16B) + Read `_BathymetryTex` (8B) + R/W `_ForceTex` (16B) + Write `_AccelPing` (8B) + Write `_StateOut` (16B) = $\mathbf{80\ \mathrm{B/px}}$
- **`CSJacobiStep`** ($\times K_{J} M_{\mathrm{sub}}$ 回): Read `_BathymetryTex` (8B) + Read `_ForceTex` (8B) + Read `_AccelPing` (8B) + Write `_AccelPong` (8B) = $\mathbf{32\ \mathrm{B/px}} \times 6 = \mathbf{192\ \mathrm{B/px}}$
- **`CSApplyDispersion`** ($\times M_{\mathrm{sub}}$ 回): R/W `_StateOut` (32B) + Read `_AccelPing` (8B) = $\mathbf{40\ \mathrm{B/px}}$
- **`CSComputeNormal`** ($\times 1$ 回): Read `_StateOut` (16B) + Write `_NormalTex` (8B) + Write `_WaterDataTex` (8B) = $\mathbf{32\ \mathrm{B/px}}$

$$
D_{\mathrm{Boussinesq}} = (3 + K_{J}) M_{\mathrm{sub}} + 1 = 9 M_{\mathrm{sub}} + 1
$$

$$
\mathrm{Traffic_{Boussinesq}}(N, M_{\mathrm{sub}}) = (360 M_{\mathrm{sub}} + 32) N^{2}\ [\mathrm{Bytes/step}]
$$

#### 方式3：スペクトル／FFT海面（1ステップあたり 2D IFFT 2回：ステージ数 $S = \log_{2} N$）
- **`CSTimeEvolution`** ($\times 1$ 回): Read `_InitialSpectrum` (16B) + Write `_SpectrumDy` (16B) + Write `_SpectrumDxDz` (16B) = $\mathbf{48\ \mathrm{B/px}}$
- **`Perform2DIFFT`** ($\times 2$ 回：各 $2\log_{2} N$ ステージ + 入出力 Blit 2回):
  - 各バタフライパス (`CSFFTHorizontal` / `CSFFTVertical`): Read `_InputPing` (2点 $\to$ キャッシュ考慮実効 24B) + Write `_OutputPong` (16B) $\approx \mathbf{40\ \mathrm{B/px}}$
  - 2D IFFT 1回あたり: $(40 \times 2\log_{2} N + 64)\ \mathrm{B/px}$、2回合計で $(160\log_{2} N + 128)\ \mathrm{B/px}$
- **`CSPackOceanData`** ($\times 1$ 回): Read `_SpectrumDy` (16B) + Read `_SpectrumDxDz` (16B) + Write `_WaterDataTex` (8B) + Write `_NormalTex` (8B) = $\mathbf{48\ \mathrm{B/px}}$

$$
D_{\mathrm{FFT}} = 4 \log_{2} N + 6, \quad \mathrm{Traffic_{FFT}}(N) = (160 \log_{2} N + 224) N^{2}\ [\mathrm{Bytes/step}]
$$

（※ $D_{\mathrm{FFT}}$ には2回の 2D IFFT に伴う入出力 Blit 計4回を含む）

---

### 6.3 GPU 実行時間の見積もり式と実測ベンチマークとの照合

GPU の実効メモリ帯域幅を $B_{\mathrm{eff}}\ [\mathrm{GB/s}]$、1ディスパッチあたりのドライバ・バリア同期オーバーヘッドを $t_{\mathrm{disp}}\ [\mathrm{ms}]$（D3D11/D3D12 で通常 $0.004 \sim 0.008\ \mathrm{ms}$）とすると、1ステップあたりの GPU 計算時間 $T_{\mathrm{GPU}}$ は以下の式で見積もられる：

$$
T_{\mathrm{GPU}} \approx \frac{\mathrm{Traffic}}{B_{\mathrm{eff}}} + D_{\mathrm{step}} \cdot t_{\mathrm{disp}}
$$

#### 検証環境（Intel(R) Arc(TM) 140V GPU: 実効帯域 $B_{\mathrm{eff}} \approx 110\ \mathrm{GB/s}$, $t_{\mathrm{disp}} \approx 0.006\ \mathrm{ms}$）での見積もり値と実測値の比較

| シミュレーション方式 | 解像度 $N^{2}$ | サブステップ / 構成 | ディスパッチ数 $D_{\mathrm{step}}$ | メモリ転送量 $\mathrm{Traffic}$ | 理論見積もり時間 $T_{\mathrm{est}}$ | 実測 GPU 時間 $T_{\mathrm{meas}}$ | 描画 FPS (1280×720) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **01 2D波動方程式** | $256^{2}$ | $M_{\mathrm{sub}} = 2$ | $3$ | $3.6\ \mathrm{MB}$ | $0.05\ \mathrm{ms}$ (×2〜3/frame $\approx 0.14\ \mathrm{ms}$) | **$0.15\ \mathrm{ms}$** | $140+\ \mathrm{FPS}$ |
| **01 2D波動方程式** | $512^{2}$ | $M_{\mathrm{sub}} = 2$ | $3$ | $14.5\ \mathrm{MB}$ | $0.15\ \mathrm{ms}$ (×2〜3/frame $\approx 0.38\ \mathrm{ms}$) | **$0.38\ \mathrm{ms}$** | $120+\ \mathrm{FPS}$ |
| **02 Boussinesq** | $256^{2}$ | $M_{\mathrm{sub}} = 2, K_{J} = 6$ | $19$ | $46.9\ \mathrm{MB}$ | $0.54\ \mathrm{ms}$ | **$0.48\ \mathrm{ms}$** | $120+\ \mathrm{FPS}$ |
| **02 Boussinesq** | $512^{2}$ | $M_{\mathrm{sub}} = 2, K_{J} = 6$ | $19$ | $187.7\ \mathrm{MB}$ | $1.82\ \mathrm{ms}$ | **$1.35\ \mathrm{ms}$** | $95+\ \mathrm{FPS}$ |
| **03 FFT海面** | $256^{2}$ | $\log_{2} N = 8$ | $38$ | $94.1\ \mathrm{MB}$ | $1.08\ \mathrm{ms}$ (L2ヒット時 $\approx 0.38\ \mathrm{ms}$) | **$0.35\ \mathrm{ms}$** | $130+\ \mathrm{FPS}$ |
| **03 FFT海面** | $512^{2}$ | $\log_{2} N = 9$ | $42$ | $419.0\ \mathrm{MB}$ | $4.06\ \mathrm{ms}$ (L2ヒット時 $\approx 1.05\ \mathrm{ms}$) | **$0.95\ \mathrm{ms}$** | $105+\ \mathrm{FPS}$ |

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
| **03_FFTWater** | `Sea State`: `Calm`, `Swell`, `Stormy` | `FFTOceanSimulation.ApplySeaStatePreset` |
| **03_FFTWater** | `Wind`, `Wind Dir`, `Choppy`, `Swell`, `Swell Dir`, `Foam` | `WindSpeed`, `WindDirectionDegrees`, `Choppiness`, `SwellStrength`, `SwellDirectionDegrees`, `EnableFoam` |
| **04_Comparison** | `Profile Scale (断面高さ強調)` / `ProfileH` | `WaterComparisonController.ProfileVerticalScale` $\to$ `WaterProfileViewer.VerticalScale` |
