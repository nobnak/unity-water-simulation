**English | [日本語](WaterSimulation_Algorithm_And_Performance_Spec.ja.md)**

# Unity Water Simulation: Complete Algorithm, Numerical Methods, and Performance Estimation Specification

This document provides the complete technical specification of the three water surface simulation methods (**2D Linear Wave Equation**, **Boussinesq Shallow-Water & Dispersive Wave System**, and **Spectral / FFT Ocean Model**) and the **Common Water Rendering Pipeline** implemented in [`com.nobnak.water-simulation`](../Packages/com.nobnak.water-simulation/package.json). It covers governing partial differential equations, discretization schemes, GPU texture layouts, compute kernel pipelines, and analytical performance estimation models based on memory bandwidth and arithmetic cost.

It is written in full mathematical and structural detail so that a reader can **re-implement an equivalent simulation and rendering system** and **quantitatively estimate VRAM footprint and GPU execution time across any grid resolution and target hardware** from this document alone.

---

## 1. System Architecture and Common Standards

### 1.1 Coordinate System, Units, and Reference Plane
- **Spatial Coordinate System**: Unity World Space (left-handed: $X$ = Right/East, $Y$ = Vertical Up, $Z$ = Forward/North).
- **Computational Grid Coordinates**: A domain of physical size $(L_x, L_z)\ [\text{m}]$ is uniformly subdivided into $(N_x, N_z)$ cells with grid spacing $\Delta x = L_x / N_x,\ \Delta z = L_z / N_z$ (for a square domain and uniform grid: $L_x = L_z = L,\ N_x = N_z = N,\ \Delta x = \Delta z = L/N$).
- **Mapping Between Texture UV and Grid Cell Indices**:

$$\mathrm{UV}(i, j) = \left(\frac{i + 0.5}{N_x},\ \frac{j + 0.5}{N_z}\right), \quad i \in [0, N_x - 1],\ j \in [0, N_z - 1]$$

- **Reference Water Level**: Still water level (SWL) is defined at $y = 0\ [\text{m}]$, vertical surface elevation as $\eta(x, z, t)\ [\text{m}]$, still water depth as $h(x, z) > 0\ [\text{m}]$ (seabed elevation $z_b(x, z) = -h(x, z) < 0\ [\text{m}]$), and total water column depth as $H(x, z, t) = h(x, z) + \eta(x, z, t)$. Gravitational acceleration is $g = 9.81\ [\text{m/s}^2]$.

### 1.2 Common Output Texture Interface ([`IWaterSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/IWaterSimulation.cs))

All simulation methods output their results in the following standardized `RenderTexture` formats so that the rendering and analysis components ([`WaterRenderer`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterRenderer.cs) / [`WaterProfileViewer`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterProfileViewer.cs)) operate identically regardless of the underlying simulation algorithm.

| Texture Property | Shader Property | Format | Bytes / Texel | R | G | B | A |
| :--- | :--- | :---: | :---: | :--- | :--- | :--- | :--- |
| **`WaterDataTexture`** | `_WaterDataTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | Horizontal X displacement $D_x\ [\text{m}]$ | Vertical elevation $\eta\ [\text{m}]$ | Horizontal Z displacement $D_z\ [\text{m}]$ | Whitecap foam $\mathrm{Foam} \in [0,1]$ |
| **`NormalTexture`** | `_NormalTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | Normal $N_x$ | Normal $N_y$ | Normal $N_z$ | Crest pinch / slope $\in [0,1]$ |
| **`BathymetryTexture`** | `_BathymetryTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | Still water depth $h\ [\text{m}]$ | Seabed elevation $z_b = -h\ [\text{m}]$ | 0 | 1 |

### 1.3 Framerate-Independent Time Stepping ([`WaterSimulationBase.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterSimulationBase.cs))

```csharp
float dt = Time.deltaTime * timeScale;
if (fixedTimeStep > 0f) {
    accumulator += dt;
    int count = 0;
    while (accumulator >= fixedTimeStep && count < 4) { // Cap at 4 steps/frame to prevent spiral of death
        Step(fixedTimeStep);
        accumulator -= fixedTimeStep;
        count++;
    }
} else {
    Step(dt);
}
```

---

## 2. Method 1: 2D Linear Wave Equation (`WaveEquationSimulation`)

Implementation files: [`WaveEquationSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquationSimulation.cs), [`WaveEquation.compute`](../Packages/com.nobnak.water-simulation/Runtime/WaveEquation/WaveEquation.compute)

### 2.1 Governing Equation
Two-dimensional linear hyperbolic wave equation with linear damping and a perimeter sponge absorbing layer:

$$\frac{\partial^2 \eta}{\partial t^2} + \gamma_{\text{eff}}(x, z)\frac{\partial \eta}{\partial t} = c^2 \nabla^2 \eta$$

- $\eta(x, z, t)$: Vertical surface elevation $[\text{m}]$
- $c$: Phase speed $[\text{m/s}]$ (constant across all wavelengths; non-dispersive)
- $\gamma_{\text{eff}}(x, z) = \gamma_0 + \gamma_{\text{sponge}}(x, z)$: Effective damping rate $[1/\text{s}]$

### 2.2 Internal Texture Layout
| Texture Variable | Format | Role & Channel Allocation |
| :--- | :---: | :--- |
| `stateTexA`, `stateTexB` (`_CurrentState`, `_NextState`) | `ARGBHalf` (8 B/px) | Ping-Pong state buffers. `R`: Current elevation $\eta^n$, `G`: Previous elevation $\eta^{n-1}$, `B`: Vertical velocity $v^n = (\eta^n - \eta^{n-1})/\Delta t$, `A`: 0 |
| `obstacleTex` (`_ObstacleTex`) | `R8` (1 B/px) | Obstacle mask (`1.0` = solid wall / slit barrier, `0.0` = open water) |

### 2.3 Numerical Discretization Scheme

#### (1) CFL Condition and Automatic Sub-Stepping
Based on the 2D explicit Courant-Friedrichs-Lewy (CFL) stability limit, each frame step $dt$ is automatically subdivided into $M_{\text{sub}}$ substeps of size $\Delta t$:

$$\Delta t_{\max} = 0.95 \frac{\Delta x}{\sqrt{2}\,c}, \quad M_{\text{sub}} = \max\!\left(1,\ \left\lceil \frac{dt}{\Delta t_{\max}} \right\rceil\right), \quad \Delta t = \frac{dt}{M_{\text{sub}}}$$

#### (2) 9-Point Isotropic Laplacian (`ComputeLaplacian`)
To eliminate grid-axial vs. diagonal phase-velocity anisotropy inherent to the standard 5-point stencil, an $O(\Delta x^2)$ isotropic 9-point stencil is used:

$$\nabla^2 \eta_{i,j}^n \approx \frac{4(\eta_{i-1,j}^n + \eta_{i+1,j}^n + \eta_{i,j-1}^n + \eta_{i,j+1}^n) + (\eta_{i-1,j-1}^n + \eta_{i+1,j-1}^n + \eta_{i-1,j+1}^n + \eta_{i+1,j+1}^n) - 20\eta_{i,j}^n}{6\Delta x^2}$$

#### (3) Semi-Implicit Damped Leapfrog / Verlet Time Integration (`CSStep`)
Discretizing the second time derivative via central differences and evaluating the first-order damping term semi-implicitly via $\frac{\eta^{n+1} - \eta^{n-1}}{2\Delta t}$ yields an explicit update for $\eta^{n+1}$:

$$\frac{\eta^{n+1} - 2\eta^n + \eta^{n-1}}{\Delta t^2} + \gamma_{\text{eff}} \frac{\eta^{n+1} - \eta^{n-1}}{2\Delta t} = c^2 \nabla^2 \eta^n$$

$$\eta^{n+1} = \frac{2\eta^n - \left(1 - \frac{1}{2}\gamma_{\text{eff}}\Delta t\right)\eta^{n-1} + c^2 \Delta t^2 \nabla^2 \eta^n}{1 + \frac{1}{2}\gamma_{\text{eff}}\Delta t}$$

Here, the perimeter sponge damping $\gamma_{\text{sponge}}$ is defined in terms of the minimum cell distance to the domain boundary $d_{\text{border}} = \min(i,\ N_x - 1 - i,\ j,\ N_z - 1 - j)$:

$$\gamma_{\text{sponge}} = \begin{cases} S_{\text{sponge}} \left(1 - \frac{d_{\text{border}}}{W_{\text{sponge}}}\right)^2 & (d_{\text{border}} < W_{\text{sponge}}) \\ 0 & (d_{\text{border}} \ge W_{\text{sponge}}) \end{cases}$$

For obstacle cells where `_ObstacleTex[pos].r > 0.5`, a homogeneous Dirichlet condition $\eta^{n+1} = \eta^n = 0$ is enforced, producing total wall reflection and slit diffraction.

#### (4) Zero-Initial-Velocity Gaussian Impulse Injection (`CSAddImpulse`)
For a normalized UV center $\mathbf{u}_0$, radius $R_{\text{uv}}$, and amplitude $A$, cells within distance $d = \|\mathbf{u} - \mathbf{u}_0\| < R_{\text{uv}}$ receive:

$$\Delta \eta = A \exp\!\left(-3\left(\frac{d}{R_{\text{uv}}}\right)^2\right)$$

which is **added equally to both `state.r` ($\eta^n$) and `state.g` ($\eta^{n-1}$)**. This excites a clean initial surface displacement without introducing a spurious velocity spike in $\frac{\eta^n - \eta^{n-1}}{\Delta t}$.

#### (5) Surface Normal and Curvature Foam Computation (`CSComputeNormal`)
Executed once after the substep loop completes, computing central-difference surface gradients and 5-point Laplacian crest curvature foam:

$$\frac{\partial \eta}{\partial x} = \frac{\eta_{i+1,j} - \eta_{i-1,j}}{2\Delta x}, \quad \frac{\partial \eta}{\partial z} = \frac{\eta_{i,j+1} - \eta_{i,j-1}}{2\Delta x}$$

$$\mathbf{N} = \mathrm{normalize}\!\left(-2.5\frac{\partial \eta}{\partial x},\ 1.0,\ -2.5\frac{\partial \eta}{\partial z}\right), \quad \mathrm{Foam} = \mathrm{saturate}\!\left(-2.0 \left(\eta_{i-1,j} + \eta_{i+1,j} + \eta_{i,j-1} + \eta_{i,j+1} - 4\eta_{i,j}\right)\right)$$

---

## 3. Method 2: Boussinesq Shallow-Water & Dispersive Wave System (`BoussinesqSimulation`)

Implementation files: [`BoussinesqSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs), [`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute)

### 3.1 Governing Equations (Peregrine 1967 Weakly Nonlinear, Weakly Dispersive System)
Conservation of mass (continuity) and conservation of depth-averaged horizontal momentum $\mathbf{u} = (u, w)^T$ over variable bathymetry $h(x, z)$:

$$\frac{\partial \eta}{\partial t} + \frac{\partial (Hu)}{\partial x} + \frac{\partial (Hw)}{\partial z} = S_\eta$$

$$\left(1 - \alpha \frac{h^2}{6}\nabla^2\right)\frac{\partial \mathbf{u}}{\partial t} = -g\nabla\eta - (\mathbf{u}\cdot\nabla)\mathbf{u} - \frac{c_f}{H}\mathbf{u} + \mathbf{S}_u$$

- $H(x, z, t) = \max(0.05,\ \max(0.2, h) + \eta)$: Total water column depth $[\text{m}]$
- $\alpha \in [0, 1.2]$: Dispersion factor ($\alpha = 1.0$: Boussinesq dispersive waves, $\alpha = 0.0$: Non-dispersive Nonlinear Shallow Water equations)
- $c_f$: Seabed friction coefficient (`_BedFriction`)
- $S_\eta,\ \mathbf{S}_u = (0, S_w)^T$: Internal wavemaker mass and momentum source terms

### 3.2 Internal Texture Layout
To prevent floating-point quantization under small substep intervals ($\Delta t \approx 0.004 \sim 0.01\text{ s}$), **`fp32` (`ARGBFloat` / `RGFloat`)** buffers are used for state, force, and acceleration fields.

| Texture Variable | Format | Bytes / Texel | Role & Channel Allocation |
| :--- | :---: | :---: | :--- |
| `stateTexA`, `stateTexB` (`_StateIn`, `_StateOut`) | `ARGBFloat` | 16 Bytes | Ping-Pong state buffers. `R`: $\eta\ [\text{m}]$, `G`: $u\ [\text{m/s}]$, `B`: $w\ [\text{m/s}]$, `A`: 0 |
| `predTex` (`_StatePred`) | `ARGBFloat` | 16 Bytes | Intermediate MacCormack predictor state $(\eta^*, u^*, w^*, 0)$ |
| `forceTex` (`_ForceTex`) | `RGFloat` | 8 Bytes | Non-dispersive momentum RHS force vector $\mathbf{f} = (f_x, f_z)$ |
| `accelTexA`, `accelTexB` (`_AccelPing`, `_AccelPong`) | `RGFloat` | 8 Bytes | Acceleration vector $\mathbf{a} = (a_x, a_z)$ for Helmholtz Jacobi relaxation |
| `bathymetryTex` (`_BathymetryTex`) | `ARGBHalf` | 8 Bytes | `R`: Still water depth $h\ [\text{m}]$, `G`: Seabed elevation $z_b = -h\ [\text{m}]$ |

### 3.3 Numerical Discretization Scheme

#### (1) CFL Condition and Automatic Sub-Stepping
Substeps are determined from the maximum long-wave phase speed $c_{\max} = \sqrt{g h_{\max}}$ at maximum domain depth $h_{\max}$ (`deepWaterDepth`):

$$\Delta t_{\max} = 0.45 \frac{\Delta x}{\sqrt{g h_{\max}}}, \quad M_{\text{sub}} = \max\!\left(1,\ \left\lceil \frac{dt}{\Delta t_{\max}} \right\rceil\right), \quad \Delta t = \frac{dt}{M_{\text{sub}}}$$

#### (2) Symmetric MacCormack Predictor-Corrector Scheme (`CSPredict` & `CSCorrect`)
To achieve second-order accuracy $O(\Delta t^2, \Delta x^2)$ while eliminating odd-even grid decoupling ($2\Delta x$ checkerboard modes) on a collocated grid, a MacCormack Predictor-Corrector scheme is used. Furthermore, the step parity $\sigma_{\text{parity}} \in \{0, 1\}$ (`pingPong ? 1 : 0`) alternates every substep (Forward-Backward on even substeps, Backward-Forward on odd substeps) to cancel directional bias.

1. **Predictor (`CSPredict`)**:
   Using direction sign $s_1 = (\sigma_{\text{parity}} == 0)\ ?\ +1 : -1$ and signed inverse spacing $\tilde{\Delta}_1 = s_1 / \Delta x$, one-sided differences with neighbor cells $(i + s_1, j)$ and $(i, j + s_1)$ are evaluated:

$$\delta_x^{(1)}(Hu)_{i,j} = (H_{i+s_1,j}u_{i+s_1,j} - H_{i,j}u_{i,j})\tilde{\Delta}_1, \quad \delta_z^{(1)}(Hw)_{i,j} = (H_{i,j+s_1}w_{i,j+s_1} - H_{i,j}w_{i,j})\tilde{\Delta}_1$$

$$\eta^* = D_{\text{sponge}} \cdot \left(\eta^n - \Delta t\left(\delta_x^{(1)}(Hu)_{i,j} + \delta_z^{(1)}(Hw)_{i,j} - S_\eta(\phi^n)\right)\right)$$

   Similarly, velocity and surface elevation gradients are evaluated with $\tilde{\Delta}_1$ to store the non-dispersive RHS force $\mathbf{f}^n = (f_x^n, f_z^n)$ in `_ForceTex` and write predicted velocities $\mathbf{u}^* = D_{\text{sponge}} \cdot (\mathbf{u}^n + \Delta t \mathbf{f}^n)$ to `_StatePred`.

2. **Corrector (`CSCorrect`)**:
   Using the opposite direction sign $s_2 = -s_1$ ($\tilde{\Delta}_2 = s_2 / \Delta x$) on `_StatePred`, the corrector averages the forward and backward updates:

$$\eta^{n+1} = D_{\text{sponge}} \cdot \frac{1}{2}\left[\eta^n + \eta^* - \Delta t\left(\delta_x^{(2)}(H^* u^*)_{i,j} + \delta_z^{(2)}(H^* w^*)_{i,j} - S_\eta(\phi^{n+1})\right)\right]$$

   Likewise, the second-order MacCormack-averaged force:

$$\bar{\mathbf{f}} = \frac{1}{2}(\mathbf{f}^n + \mathbf{f}^*)$$

   is written to both `_ForceTex` and the initial Jacobi guess `_AccelPing`.

#### (3) Implicit Helmholtz Dispersion Inverter (`CSJacobiStep` & `CSApplyDispersion`)
Discretizing the elliptic Helmholtz operator $(I - \alpha \frac{h^2}{6}\nabla^2)\mathbf{a} = \bar{\mathbf{f}}$ on the 5-point stencil yields at each cell $(i, j)$:

$$(1 + 4\beta_{i,j})\mathbf{a}_{i,j} - \beta_{i,j}(\mathbf{a}_{i-1,j} + \mathbf{a}_{i+1,j} + \mathbf{a}_{i,j-1} + \mathbf{a}_{i,j+1}) = \bar{\mathbf{f}}_{i,j}, \quad \beta_{i,j} = \mathrm{clamp}\!\left(\alpha \frac{h_{i,j}^2}{6\Delta x^2},\ 0.0,\ 6.0\right)$$

Starting from $\mathbf{a}^{(0)} = \bar{\mathbf{f}}$ (initialized from the current substep's $\bar{\mathbf{f}}$ to avoid temporal phase lag), **6 Jacobi relaxation iterations** ($k = 0, \dots, 5$) are executed:

$$\mathbf{a}_{i,j}^{(k+1)} = \frac{\bar{\mathbf{f}}_{i,j} + \beta_{i,j}(\mathbf{a}_{i-1,j}^{(k)} + \mathbf{a}_{i+1,j}^{(k)} + \mathbf{a}_{i,j-1}^{(k)} + \mathbf{a}_{i,j+1}^{(k)})}{1 + 4\beta_{i,j}}$$

Followed by `CSApplyDispersion` to update the horizontal velocity field:

$$\mathbf{u}^{n+1} = D_{\text{sponge}} \cdot \left(\mathbf{u}^n + \Delta t\,\mathbf{a}^{(6)}\right)$$

where the implicit sponge damping factor for $d_{\text{border}} < W_{\text{sponge}}$ is:

$$D_{\text{sponge}} = \frac{1}{1 + 15\,\Delta t\, S_{\text{sponge}} \left(1 - \frac{d_{\text{border}}}{W_{\text{sponge}}}\right)^2}$$

#### (4) Progressive Wavemaker Source (`GetWavemakerSources`) and GPU Soliton Injection (`CSAddImpulse`)
- **Continuous Progressive Wavemaker (`GetWavemakerSources`)**:
  Within $|j - y_0| \le 10$ cells of the offshore line $y_0 = \lfloor v_{\text{wm}} N_z \rfloor$, coupled mass and momentum sources synthesize a pure $+Z$ progressive wave while remaining transparent to waves reflected from the shoal. Using a Gaussian profile $G(j) = \frac{1}{\sqrt{2\pi}\sigma\Delta x}\exp\!\left(-\frac{(j - y_0)^2}{2\sigma^2}\right)$ ($\sigma = 2.5$ cells) and lateral sponge taper $T_{\text{lat}}(i) = \mathrm{smoothstep}(0,\ 1.5 W_{\text{sponge}},\ \min(i, N_x - 1 - i))$:

$$W(i, j, \phi) = A_{\text{wm}} \sin(\phi)\, G(j)\, T_{\text{lat}}(i), \quad S_\eta = c W, \quad S_w = g W \quad \left(c = \sqrt{g \max(0.5, h)}\right)$$

- **Planar Soliton and Circular Gaussian Pulse Injection (`CSAddImpulse`)**:
  - **Planar Soliton (`_ImpulseShape == 0`)**:

$$\Delta \eta = A\,\mathrm{sech}^2\!\left(\frac{v - v_0}{0.6 R_{\text{uv}}}\right) T_{\text{lat}}(i), \quad c = \sqrt{g(h + \max(0, \Delta\eta))}, \quad \Delta w = \frac{c\,\Delta\eta}{h + \max(0, \Delta\eta)}$$

    is added non-destructively to the existing $(\eta, w)$ state.
  - **Circular Pulse (`_ImpulseShape == 1`)**:
    $\Delta \eta = A \exp(-3 (\|\mathbf{u} - \mathbf{u}_0\| / R_{\text{uv}})^2)$ is added non-destructively to $\eta$.

#### (5) Procedural Bathymetry (`UpdateBathymetryTexture`)
For normalized coordinates $(u, v) \in [0, 1]^2$, deep depth $h_{\text{deep}}$, and shallow depth $h_{\text{shallow}}$:
- **`Flat`**: $h(u, v) = h_{\text{deep}}$
- **`LinearSlope`**: $h(u, v) = \max(0.5,\ \mathrm{lerp}(h_{\text{deep}}, h_{\text{shallow}}, v))$
- **`ShoalAndCape`**:
  - Baseline slope: $h_{\text{base}}(v) = \mathrm{lerp}(h_{\text{deep}},\ h_{\text{shallow}} + 2.0,\ \mathrm{smoothstep}(0.2, 0.9, v))$
  - Diagonal shoal ridge: $d_{\text{shoal}} = (u - 0.5)\cos(0.6) + (v - 0.45)\sin(0.6),\ f_{\text{shoal}} = \exp(-d_{\text{shoal}}^2 / 0.025)$
  - Circular cape / headland: $d_{\text{cape}} = \|(u, v) - (0.72, 0.62)\|,\ f_{\text{cape}} = \exp(-d_{\text{cape}}^2 / 0.015)$
  - Combined depth: $h(u, v) = \max\!\left(0.5,\ \mathrm{lerp}\!\left(\mathrm{lerp}(h_{\text{base}}, h_{\text{shallow}}, 0.75 f_{\text{shoal}}),\ 0.5 h_{\text{shallow}},\ 0.85 f_{\text{cape}}\right)\right)$

#### (6) Surface Normal and Steepness Foam (`CSComputeNormal`)
Evaluates central-difference surface gradients and generates whitecap foam where surface slope exceeds $0.15$:

$$\frac{\partial \eta}{\partial x} = \frac{\eta_{i+1,j} - \eta_{i-1,j}}{2\Delta x}, \quad \frac{\partial \eta}{\partial z} = \frac{\eta_{i,j+1} - \eta_{i,j-1}}{2\Delta x}$$

$$\mathbf{N} = \mathrm{normalize}\!\left(-2.0\frac{\partial \eta}{\partial x},\ 1.0,\ -2.0\frac{\partial \eta}{\partial z}\right), \quad \mathrm{Foam} = \mathrm{saturate}\!\left(3.0\left(\|\nabla\eta\| - 0.15\right)\right)$$

---

## 4. Method 3: Spectral / FFT Ocean Model (`FFTOceanSimulation`)

Implementation files: [`FFTOceanSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOceanSimulation.cs), [`FFTOcean.compute`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute)

### 4.1 Governing Equations and Spectral Synthesis

#### (1) Wavenumber Grid and Phillips + Swell Composite Spectrum (`Phillips`, `CSGenerateSpectrum`)
For grid resolution $N \times N$ ($N = 2^M$) and domain length $L$ (`_LengthScale1`), the fundamental wavenumber step is $\Delta k = \frac{2\pi}{L}$ and the wavenumber vector is $\mathbf{k} = (k_x, k_z) = (n_x - \frac{N}{2},\ n_z - \frac{N}{2})\Delta k$ ($k = \|\mathbf{k}\|,\ \hat{\mathbf{k}} = \mathbf{k}/k$).
Given wind speed $V$ (`_WindSpeed`), unit wind direction $\hat{\mathbf{w}}$, swell strength $A_{\text{swell}}$, and unit swell direction $\hat{\mathbf{s}}$, the variance spectrum $P(\mathbf{k})$ is:

$$P_{\text{Phillips}}(\mathbf{k}) = A_P \frac{\exp\!\left(-\frac{1}{(k L_w)^2}\right)}{k^4} D_{\text{wind}}(\hat{\mathbf{k}} \cdot \hat{\mathbf{w}}) \exp(-k^2 l_0^2), \quad L_w = \frac{V^2}{g},\ A_P = 0.8,\ l_0 = 0.05\ [\text{m}]$$

$$D_{\text{wind}}(\mu) = \begin{cases} \mu^2 & (\mu > 0) \\ 0.05 & (\mu \le 0) \end{cases}$$

$$P_{\text{swell}}(\mathbf{k}) = 0.45 A_{\text{swell}} \exp\!\left(-0.06 \Lambda_{\text{swell}}^2 (k - k_{\text{swell}})^2\right) \max(0, \hat{\mathbf{k}}\cdot\hat{\mathbf{s}})^4, \quad \Lambda_{\text{swell}} = \max(8.0,\ 0.28 L),\ k_{\text{swell}} = \frac{2\pi}{\Lambda_{\text{swell}}}$$

$$P(\mathbf{k}) = P_{\text{Phillips}}(\mathbf{k}) + P_{\text{swell}}(\mathbf{k})$$

Using standard normal random pairs $(\xi_r, \xi_i) \sim \mathcal{N}(0, 1)$ (generated via integer Wang/Jenkins hash + Box-Muller transform), the initial complex amplitude $h_0(\mathbf{k})$ and conjugate term $h_0^*(-\mathbf{k})$ are stored in `_InitialSpectrum` (`ARGBFloat`):

$$h_0(\mathbf{k}) = \sqrt{\frac{P(\mathbf{k})}{2}}(\xi_r + i\xi_i), \quad h_0^*(-\mathbf{k}) = \sqrt{\frac{P(-\mathbf{k})}{2}}(\xi_r - i\xi_i)$$

#### (2) Deep-Water Dispersion Time Evolution and Wavenumber-Weighted Choppy Spectrum (`CSTimeEvolution`)
Following the deep-water gravity wave dispersion relation $\omega(k) = \sqrt{g k}$, the complex elevation spectrum $\tilde{h}(\mathbf{k}, t)$ at time $t$ is synthesized as (preserving Hermitian symmetry $\tilde{h}^*(-\mathbf{k}, t) = \tilde{h}(\mathbf{k}, t)$ so the IFFT output is real):

$$\tilde{h}(\mathbf{k}, t) = h_0(\mathbf{k})e^{i\omega(k)t} + h_0^*(-\mathbf{k})e^{-i\omega(k)t}$$

Because the 2D IFFT kernels use the $+i$ twiddle exponent $\exp(+i 2\pi \mathbf{n}\cdot\mathbf{m}/N)$, the horizontal displacement spectrum $(\tilde{D}_x, \tilde{D}_z)$ that converges horizontal particles toward wave crests ($D_y > 0,\ \nabla\cdot\mathbf{D}_h < 0$) to form sharp trochoidal crests is given by the **`+i` phase factor**:

$$\begin{pmatrix} \tilde{D}_x(\mathbf{k}, t) \\ \tilde{D}_z(\mathbf{k}, t) \end{pmatrix} = +i\, w_{\text{chop}}(k) \frac{\mathbf{k}}{k} \tilde{h}(\mathbf{k}, t) = w_{\text{chop}}(k)\frac{\mathbf{k}}{k}\left(-\mathrm{Im}\,\tilde{h} + i\,\mathrm{Re}\,\tilde{h}\right)$$

Here $w_{\text{chop}}(k)$ is a wavenumber envelope that amplifies horizontal convergence on primary wind and swell waves while preventing high-wavenumber capillary waves from self-intersecting ($J \ll 0$):

$$w_{\text{chop}}(k) = \frac{2.1}{1 + k / k_{\text{ref}}}, \quad k_{\text{ref}} = 0.42\ [\text{rad/m}]$$

Because both $D_x(\mathbf{x})$ and $D_z(\mathbf{x})$ are real fields with Hermitian-symmetric spectra, $\tilde{D}_x$ is packed into `RG` and $\tilde{D}_z$ into `BA` of `_SpectrumDxDz` and transformed simultaneously in a single 4-channel 2D IFFT.

### 4.2 Radix-2 Stockham Autosort 2D IFFT (`CSFFTHorizontal`, `CSFFTVertical`)
To eliminate the bit-reversal permutation pass required by Cooley-Tukey FFTs, the **Stockham Autosort algorithm** is used, automatically sorting indices between Ping-Pong buffers at each butterfly stage.
For stage $s = 0, 1, \dots, \log_2 N - 1$ with stride $R = 2^s$ and $P = 2R$, the input indices and twiddle factor for output index $x \in [0, N-1]$ are computed directly (implemented via bitwise operations `j = x & (R - 1u)`, `k = x >> s`, `t = k & 1u`, `g = k >> 1u` in HLSL):

$$j = x - R\lfloor x / R \rfloor, \quad k = \lfloor x / R \rfloor, \quad t = k - 2\lfloor k / 2 \rfloor, \quad g = \lfloor k / 2 \rfloor$$

$$i_0 = g R + j, \quad i_1 = i_0 + \frac{N}{2}, \quad \theta = \frac{2\pi j}{P}, \quad W = \cos\theta + i\sin\theta$$

$$\mathrm{Out}[x] = \begin{cases} \mathrm{In}[i_0] + W \cdot \mathrm{In}[i_1] & (t == 0) \\ \mathrm{In}[i_0] - W \cdot \mathrm{In}[i_1] & (t == 1) \end{cases}$$

This executes $\log_2 N$ horizontal passes followed by $\log_2 N$ vertical passes ($2\log_2 N$ passes per 2D IFFT).

### 4.3 Exact Lagrangian Normal, Jacobian Whitecap Foam, and Crest Pinch (`CSPackOceanData`)
Applying the spatial checkerboard sign $(-1)^{x+z}$ (due to centering the wavenumber origin at $(N/2, N/2)$, evaluated as `((x + z) & 1) != 0 ? -1.0 : 1.0`), normalization factor $1/N$, and choppiness scale $\lambda$ (`_Choppiness`) yields the spatial displacement vector $\mathbf{D} = (D_x, D_y, D_z)$:

$$s(x, z) = \frac{(-1)^{x + z}}{N}, \quad D_y = s \cdot \mathrm{Re}(\mathrm{IFFT}(\tilde{h})), \quad D_x = \lambda s \cdot \mathrm{Re}(\mathrm{IFFT}(\tilde{D}_x)), \quad D_z = \lambda s \cdot \mathrm{Re}(\mathrm{IFFT}(\tilde{D}_z))$$

Central differences with periodic wrap mask `& (N - 1)` over spacing $2\Delta x = 2L/N$ yield the displacement gradient tensor $D_{\alpha,\beta} = \frac{\partial D_\alpha}{\partial \beta}$ and the **Jacobian determinant $J$** of the horizontal mapping $\mathbf{X}_h(x,z) = (x + D_x, z + D_z)$:

$$J = \left(1 + \frac{\partial D_x}{\partial x}\right)\left(1 + \frac{\partial D_z}{\partial z}\right) - \frac{\partial D_x}{\partial z}\frac{\partial D_z}{\partial x}$$

- **Whitecap Foam Intensity**: $\mathrm{Foam} = \mathrm{saturate}\!\left(2.2(\tau_{\text{foam}} - J)\right)$ ($\tau_{\text{foam}}$: `_FoamThreshold`)
- **Horizontal Crest Pinch**: $P_{\text{crest}} = \mathrm{saturate}(1.0 - J)$
- **Exact Jacobian-Coupled Lagrangian Normal $\mathbf{N}$**:
  Computed from the cross product $\mathbf{T}_z \times \mathbf{T}_x$ of the displaced surface tangent vectors $\mathbf{T}_x = (1 + D_{x,x},\ D_{y,x},\ D_{z,x})$ and $\mathbf{T}_z = (D_{x,z},\ D_{y,z},\ 1 + D_{z,z})$:

$$\mathbf{N} = \mathrm{normalize}\!\begin{pmatrix} -\frac{\partial D_y}{\partial x}\left(1 + \frac{\partial D_z}{\partial z}\right) + \frac{\partial D_y}{\partial z}\frac{\partial D_z}{\partial x} \\ \max(0.08,\ J) \\ -\frac{\partial D_y}{\partial z}\left(1 + \frac{\partial D_x}{\partial x}\right) + \frac{\partial D_y}{\partial x}\frac{\partial D_x}{\partial z} \end{pmatrix}$$

  As horizontal particles converge at wave crests ($J \to 0.08$), the horizontal normal components sharpen dramatically, producing authentic trochoidal ridge shading and Fresnel grazing highlights.

### 4.4 Internal Texture Layout (`FFTOceanSimulation`)
| Texture Variable | Format | Bytes / Texel | Role & Channel Allocation |
| :--- | :---: | :---: | :--- |
| `initialSpectrumTex` (`_InitialSpectrum`) | `ARGBFloat` | 16 Bytes | `RG`: $h_0(\mathbf{k})$, `BA`: $h_0^*(-\mathbf{k})$ |
| `spectrumDyTex` (`_SpectrumDy`) | `ARGBFloat` | 16 Bytes | `RG`: $\tilde{h}(\mathbf{k}, t)$ (post-IFFT $D_y$) |
| `spectrumDxDzTex` (`_SpectrumDxDz`) | `ARGBFloat` | 16 Bytes | `RG`: $\tilde{D}_x \to D_x$, `BA`: $\tilde{D}_z \to D_z$ |
| `pingPongTexA`, `pingPongTexB` | `ARGBFloat` | 16 Bytes | Stockham 2D IFFT work buffers |

---

## 5. Common Water Surface Shader and Visualization Modes (`WaterSurfaceCommon.shader`)

Implementation files: [`WaterSurfaceCommon.shader`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader), [`WaterRenderer.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterRenderer.cs)

### 5.1 3D Vertex Displacement (`Vert`)
Samples `_WaterDataTex` at mip level 0 and adds the 3D displacement vector $\mathbf{D} = (D_x, \eta, D_z)$ to the world-space vertex position:

$$\mathbf{p}_{\text{WS}} = \mathbf{p}_{\text{WS},0} + s_{\text{disp}} \cdot (D_x,\ \eta,\ D_z)$$

### 5.2 Pixel Shading and 5 Visualization Modes (`Frag`)
1. **`Realistic` (`_VisualMode == 0`)**:
   - **Schlick Fresnel**: $F = 0.02 + 0.98(1 - \mathrm{saturate}(\mathbf{N}\cdot\mathbf{V}))^5$
   - **Elevation + Crest Pinch Color Blend**: Interpolates between `_DeepColor` and `_BaseColor` via $f_{\text{depth}} = \mathrm{saturate}(0.35\eta + 0.25 P_{\text{crest}} + 0.45)$.
   - **Shallow-Water Emerald Transmittance (when `_HasBathymetry > 0.5`)**:
     Combines exponential depth transmittance $f_{\text{shoal}} = \mathrm{saturate}(\exp(-0.26(h - 1.2)))$ with 2m seabed depth contours to tint shallow shoals and headlands toward emerald $(0.16, 0.62, 0.56)$.
   - **Wave Crest Subsurface Scattering (SSS)**:
     Adds turquoise subsurface scattering $\mathbf{C}_{\text{SSS}} = (0.14, 0.72, 0.68) \cdot \mathbf{L}_{\text{color}} \cdot P_{\text{crest}}\,\mathrm{saturate}(0.45\eta + 0.35)\,(0.45 + 0.55\,\mathrm{saturate}(-\mathbf{V}\cdot\mathbf{L}\cdot 0.5 + 0.5)) \cdot 0.55$ on pinched choppy wave crests ($P_{\text{crest}} = \mathrm{saturate}(1 - J) > 0$).
   - **Blinn-Phong Specular & Foam Blend**: $\mathrm{Spec} = (\mathbf{N}\cdot\mathbf{H})^{64} \cdot \mathrm{Smoothness} \cdot (0.85 F + 0.15) \cdot 2.0$, and blends `_FoamColor` by $\mathrm{Foam} = \mathrm{saturate}(\mathrm{disp.a} \cdot I_{\text{foam}})$.
2. **`HeightMap` (`_VisualMode == 1`)**: Jet colormap of normalized elevation $\eta / R_{\text{height}} \in [-1, 1]$ with $0.2\text{ m}$ contour lines.
3. **`NormalVisual` (`_VisualMode == 2`)**: World-space normal RGB visualization $0.5\mathbf{N} + 0.5$.
4. **`Wireframe` (`_VisualMode == 3`)**: $64\times 64$ UV grid visualizing horizontal choppy convergence and surface deformation.
5. **`Bathymetry` (`_VisualMode == 4`)**: Warm (shallow) to cool (deep) heatmap of still water depth $h \in [1\text{ m}, 15\text{ m}]$ with $1\text{ m}$ isobath contours and $+0.12\eta$ wave crest overlay.

---

## 6. Performance Estimation Model (VRAM, Memory Bandwidth, Dispatch Count, and Execution Time)

Grid-based and FFT-based GPU Compute Shader water simulations are primarily **memory-bandwidth bound** and **dispatch-overhead bound** rather than arithmetic-compute bound. Consequently, performance at any resolution $N \times N$ can be accurately predicted from the analytical formulas below.

### 6.1 VRAM Footprint Formulas and Resolution Table

VRAM usage for resolution $N \times N$ ($P = N^2$ pixels):

1. **Water Surface Mesh (`WaterRenderer`, resolution $M \times M$, $4.50\text{ MB}$ at default $M = 256$)**:
   - Vertex buffer (`pos`: 12B, `normal`: 12B, `tangent`: 16B, `uv`: 8B = **48 Bytes/vertex**) + Index buffer (32-bit UInt, 6 indices/cell = **24 Bytes/cell**)

$$\mathrm{VRAM}_{\text{Mesh}}(M) = 48 M^2 + 24 (M - 1)^2 \approx 72 M^2\ [\text{Bytes}]$$

2. **Method 1: 2D Wave Equation (`WaveEquationSimulation`)**:
   - `stateTexA` (8B) + `stateTexB` (8B) + `normalTex` (8B) + `waterDataTex` (8B) + `obstacleTex` (1B) = **33 Bytes/px**

$$\mathrm{VRAM}_{\text{WaveEq}}(N) = 33 N^2\ [\text{Bytes}]$$

3. **Method 2: Boussinesq System (`BoussinesqSimulation`)**:
   - `stateTexA` (16B) + `stateTexB` (16B) + `predTex` (16B) + `forceTex` (8B) + `accelTexA` (8B) + `accelTexB` (8B) + `normalTex` (8B) + `waterDataTex` (8B) + `bathymetryTex` (8B) = **96 Bytes/px**

$$\mathrm{VRAM}_{\text{Boussinesq}}(N) = 96 N^2\ [\text{Bytes}]$$

4. **Method 3: FFT Ocean Model (`FFTOceanSimulation`)**:
   - `initialSpectrumTex` (16B) + `spectrumDyTex` (16B) + `spectrumDxDzTex` (16B) + `pingPongTexA` (16B) + `pingPongTexB` (16B) + `waterDataTex` (8B) + `normalTex` (8B) = **96 Bytes/px**

$$\mathrm{VRAM}_{\text{FFT}}(N) = 96 N^2\ [\text{Bytes}]$$

| Grid Resolution $N \times N$ | Total Cells $N^2$ | Method 1: 2D Wave Eq | Method 2: Boussinesq | Method 3: FFT Ocean | Shared Mesh ($256^2$) |
| :---: | :---: | :---: | :---: | :---: | :---: |
| **$256 \times 256$** | $65,536$ | **$2.06\text{ MB}$** | **$6.00\text{ MB}$** | **$6.00\text{ MB}$** | $4.50\text{ MB}$ |
| **$512 \times 512$** | $262,144$ | **$8.25\text{ MB}$** | **$24.00\text{ MB}$** | **$24.00\text{ MB}$** | $4.50\text{ MB}$ |
| **$1024 \times 1024$** | $1,048,576$ | **$33.00\text{ MB}$** | **$96.00\text{ MB}$** | **$96.00\text{ MB}$** | $4.50\text{ MB}$ |

---

### 6.2 Per-Step Dispatch Count and Memory Bandwidth Model

Effective DRAM/L2 memory traffic per cell (Read + Write after L1/L2 spatial cache locality within $8\times 8$ thread groups) and total dispatches $D$ per step:

#### Method 1: 2D Wave Equation ($M_{\text{sub}}$ substeps)
- **`CSStep`** ($\times M_{\text{sub}}$): Read `_CurrentState` (8B) + `_ObstacleTex` (1B) + Write `_NextState` (8B) $\approx \mathbf{17\text{ B/px}}$ (9-point stencil neighbor reads hit L1 cache within the $8\times 8$ thread group).
- **`CSComputeNormal`** ($\times 1$): Read `_NextState` (8B) + Write `_NormalTex` (8B) + Write `_WaterDataTex` (8B) $\approx \mathbf{24\text{ B/px}}$.

$$D_{\text{WaveEq}} = M_{\text{sub}} + 1, \quad \mathrm{Traffic}_{\text{WaveEq}}(N, M_{\text{sub}}) = (17 M_{\text{sub}} + 24) N^2\ [\text{Bytes/step}]$$

#### Method 2: Boussinesq System ($M_{\text{sub}}$ substeps, $K_J = 6$ Jacobi iterations)
- **`CSPredict`** ($\times M_{\text{sub}}$): Read `_StateIn` (16B) + Read `_BathymetryTex` (8B) + Write `_ForceTex` (8B) + Write `_StatePred` (16B) = $\mathbf{48\text{ B/px}}$
- **`CSCorrect`** ($\times M_{\text{sub}}$): Read `_StateIn` (16B) + Read `_StatePred` (16B) + Read `_BathymetryTex` (8B) + R/W `_ForceTex` (16B) + Write `_AccelPing` (8B) + Write `_StateOut` (16B) = $\mathbf{80\text{ B/px}}$
- **`CSJacobiStep`** ($\times K_J M_{\text{sub}}$): Read `_BathymetryTex` (8B) + Read `_ForceTex` (8B) + Read `_AccelPing` (8B) + Write `_AccelPong` (8B) = $\mathbf{32\text{ B/px}} \times 6 = \mathbf{192\text{ B/px}}$
- **`CSApplyDispersion`** ($\times M_{\text{sub}}$): R/W `_StateOut` (32B) + Read `_AccelPing` (8B) = $\mathbf{40\text{ B/px}}$
- **`CSComputeNormal`** ($\times 1$): Read `_StateOut` (16B) + Write `_NormalTex` (8B) + Write `_WaterDataTex` (8B) = $\mathbf{32\text{ B/px}}$

$$D_{\text{Boussinesq}} = (3 + K_J) M_{\text{sub}} + 1 = 9 M_{\text{sub}} + 1$$

$$\mathrm{Traffic}_{\text{Boussinesq}}(N, M_{\text{sub}}) = (360 M_{\text{sub}} + 32) N^2\ [\text{Bytes/step}]$$

#### Method 3: Spectral / FFT Ocean (two 2D IFFTs per step, $S = \log_2 N$ stages)
- **`CSTimeEvolution`** ($\times 1$): Read `_InitialSpectrum` (16B) + Write `_SpectrumDy` (16B) + Write `_SpectrumDxDz` (16B) = $\mathbf{48\text{ B/px}}$
- **`Perform2DIFFT`** ($\times 2$: $2\log_2 N$ butterfly passes + 2 I/O Blits per 2D IFFT):
  - Each butterfly pass (`CSFFTHorizontal` / `CSFFTVertical`): Read `_InputPing` (2 points $\to$ 24B effective with cache) + Write `_OutputPong` (16B) $\approx \mathbf{40\text{ B/px}}$
  - Per 2D IFFT: $(40 \times 2\log_2 N + 64)\text{ B/px}$; total for 2 IFFTs: $(160\log_2 N + 128)\text{ B/px}$
- **`CSPackOceanData`** ($\times 1$): Read `_SpectrumDy` (16B) + Read `_SpectrumDxDz` (16B) + Write `_WaterDataTex` (8B) + Write `_NormalTex` (8B) = $\mathbf{48\text{ B/px}}$

$$D_{\text{FFT}} = 4 \log_2 N + 6, \quad \mathrm{Traffic}_{\text{FFT}}(N) = (160 \log_2 N + 224) N^2\ [\text{Bytes/step}]$$

(Note: $D_{\text{FFT}}$ includes the 4 I/O `Graphics.Blit` operations across the two 2D IFFTs.)

---

### 6.3 GPU Execution Time Formula and Measured Benchmark Verification

Given effective GPU memory bandwidth $B_{\text{eff}}\ [\text{GB/s}]$ and per-dispatch driver/barrier overhead $t_{\text{disp}}\ [\text{ms}]$ (typically $0.004 \sim 0.008\text{ ms}$ on D3D11/D3D12), the GPU execution time per simulation step $T_{\text{GPU}}$ is estimated by:

$$T_{\text{GPU}} \approx \frac{\mathrm{Traffic}}{B_{\text{eff}}} + D_{\text{step}} \cdot t_{\text{disp}}$$

#### Analytical Estimate vs. Measured GPU Time on Reference Hardware (Intel(R) Arc(TM) 140V GPU: $B_{\text{eff}} \approx 110\text{ GB/s}$, $t_{\text{disp}} \approx 0.006\text{ ms}$)

| Simulation Method | Resolution $N^2$ | Substeps / Config | Dispatches $D_{\text{step}}$ | Memory Traffic | Analytical Estimate $T_{\text{est}}$ | Measured GPU Time $T_{\text{meas}}$ | Render FPS (1280×720) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **01 2D Wave Equation** | $256^2$ | $M_{\text{sub}} = 2$ | $3$ | $3.6\text{ MB}$ | $0.05\text{ ms}$ (×2–3/frame $\approx 0.14\text{ ms}$) | **$0.15\text{ ms}$** | $140+\text{ FPS}$ |
| **01 2D Wave Equation** | $512^2$ | $M_{\text{sub}} = 2$ | $3$ | $14.5\text{ MB}$ | $0.15\text{ ms}$ (×2–3/frame $\approx 0.38\text{ ms}$) | **$0.38\text{ ms}$** | $120+\text{ FPS}$ |
| **02 Boussinesq** | $256^2$ | $M_{\text{sub}} = 2, K_J = 6$ | $19$ | $46.9\text{ MB}$ | $0.54\text{ ms}$ | **$0.48\text{ ms}$** | $120+\text{ FPS}$ |
| **02 Boussinesq** | $512^2$ | $M_{\text{sub}} = 2, K_J = 6$ | $19$ | $187.7\text{ MB}$ | $1.82\text{ ms}$ | **$1.35\text{ ms}$** | $95+\text{ FPS}$ |
| **03 FFT Ocean** | $256^2$ | $\log_2 N = 8$ | $38$ | $94.1\text{ MB}$ | $1.08\text{ ms}$ (with L2 residency $\approx 0.38\text{ ms}$) | **$0.35\text{ ms}$** | $130+\text{ FPS}$ |
| **03 FFT Ocean** | $512^2$ | $\log_2 N = 9$ | $42$ | $419.0\text{ MB}$ | $4.06\text{ ms}$ (with L2 residency $\approx 1.05\text{ ms}$) | **$0.95\text{ ms}$** | $105+\text{ FPS}$ |

> [!TIP]
> **Resolution Scaling Laws**:
> - **Doubling grid resolution along each axis ($2\times$, e.g., $256^2 \to 512^2$)**:
>   - For the **2D Wave Equation** and **Boussinesq** models, total cells $N^2$ increase by $4\times$ while the CFL limit $\Delta t \propto \Delta x = L/N$ doubles the required substep count $M_{\text{sub}}$ ($2\times$) for the same domain size $L$, scaling computational cost per unit physical time by **$4 \times 2 = 8\times$** (stepping up whenever $\Delta t_{\max}$ crosses an integer divisor of `fixedTimeStep`).
>   - For the **FFT Ocean** model, there is no CFL restriction; doubling $N$ leaves the time step unchanged and increases compute cost by only **$4 \frac{\log_2 N + 1}{\log_2 N} \approx 4.4\times$**.

---

## 7. Complete UI Controls to Implementation Mapping Matrix

All interactive HUD controls across the 5 sample scenes ([`WaterSampleUI.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterSampleUI.cs) / [`WaterComparisonController.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterComparisonController.cs)) and their bound properties/methods:

| Scene | UI Section & Control | Bound Property / Method |
| :--- | :--- | :--- |
| **Common** | `Play` / `Pause`, `Step (0.016s)`, `Reset`, `TimeScale` | `WaterSimulationBase.Play/Pause/Step/ResetSimulation/TimeScale` (synchronizes all simulations via `WaterComparisonController` in Scenes 04/05) |
| **Common** | `Visual Mode`: `Realistic`, `HeightMap`, `Normal`, `Wireframe`, `Bathymetry` | `WaterRenderer.SetVisualMode` (`WaterComparisonController.SetVisualModeAll` in Scenes 04/05) |
| **Common** | `Camera View`: `Default View`, `Low Angle View` | `SimpleCameraController.ResetToDefaultView` / `SetLowAngleView` |
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
