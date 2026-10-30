**English | [日本語](WaterSimulation_Algorithm_And_Performance_Spec.ja.md)**

# Unity Water Simulation: Complete Algorithm, Numerical Methods, and Performance Estimation Specification

This document provides the complete technical specification of the three water surface simulation methods (**2D Linear Wave Equation**, **Boussinesq Shallow-Water & Dispersive Wave System**, and **Spectral / FFT Ocean Model**) and the **Common Water Rendering Pipeline** implemented in [`com.nobnak.water-simulation`](../Packages/com.nobnak.water-simulation/package.json). It covers governing partial differential equations, discretization schemes, GPU texture layouts, compute kernel pipelines, and analytical performance estimation models based on memory bandwidth and arithmetic cost.

It is written in full mathematical and structural detail so that a reader can **re-implement an equivalent simulation and rendering system** and **quantitatively estimate VRAM footprint and GPU execution time across any grid resolution and target hardware** from this document alone.

---

## 1. System Architecture and Common Standards

### 1.1 Coordinate System, Units, and Reference Plane
- **Spatial Coordinate System**: Unity World Space (left-handed: $X$ = Right/East, $Y$ = Vertical Up, $Z$ = Forward/North).
- **Computational Grid Coordinates**: A domain of physical size $(L_{x}, L_{z})\ [\mathrm{m}]$ is uniformly subdivided into $(N_{x}, N_{z})$ cells with grid spacing $\Delta x = L_{x} / N_{x},\ \Delta z = L_{z} / N_{z}$ (for a square domain and uniform grid: $L_{x} = L_{z} = L,\ N_{x} = N_{z} = N,\ \Delta x = \Delta z = L/N$).
- **Mapping Between Texture UV and Grid Cell Indices**:

$$
\mathrm{UV}(i, j) = \left(\frac{i + 0.5}{N_{x}},\ \frac{j + 0.5}{N_{z}}\right), \quad i \in [0, N_{x} - 1],\ j \in [0, N_{z} - 1]
$$

- **Reference Water Level**: Still water level (SWL) is defined at $y = 0\ [\mathrm{m}]$, vertical surface elevation as $\eta(x, z, t)\ [\mathrm{m}]$, still water depth as $h(x, z) > 0\ [\mathrm{m}]$ (seabed elevation $z_{b}(x, z) = -h(x, z) < 0\ [\mathrm{m}]$), and total water column depth as $H(x, z, t) = h(x, z) + \eta(x, z, t)$. Gravitational acceleration is $g = 9.81\ [\mathrm{m/s^{2}}]$.

### 1.2 Common Output Texture Interface ([`IWaterSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/IWaterSimulation.cs))

All simulation methods output their results in the following standardized `RenderTexture` formats so that the rendering and analysis components ([`WaterRenderer`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterRenderer.cs) / [`WaterProfileViewer`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterProfileViewer.cs)) operate identically regardless of the underlying simulation algorithm.

| Texture Property | Shader Property | Format | Bytes / Texel | R | G | B | A |
| :--- | :--- | :---: | :---: | :--- | :--- | :--- | :--- |
| **`WaterDataTexture`** | `_WaterDataTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | Horizontal X displacement $D_{x}\ [\mathrm{m}]$ | Vertical elevation $\eta\ [\mathrm{m}]$ | Horizontal Z displacement $D_{z}\ [\mathrm{m}]$ | Whitecap foam $\mathrm{Foam} \in [0,1]$ |
| **`NormalTexture`** | `_NormalTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | Normal $N_{x}$ | Normal $N_{y}$ | Normal $N_{z}$ | Crest pinch / slope $\in [0,1]$ |
| **`BathymetryTexture`** | `_BathymetryTex` | `ARGBHalf` (`fp16x4`) | 8 Bytes | Still water depth $h\ [\mathrm{m}]$ | Seabed elevation $z_{b} = -h\ [\mathrm{m}]$ | 0 | 1 |

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

$$
\frac{\partial^{2} \eta}{\partial t^{2}} + \gamma_{\mathrm{eff}}(x, z)\frac{\partial \eta}{\partial t} = c^{2} \nabla^{2} \eta
$$

- $\eta(x, z, t)$: Vertical surface elevation $[\mathrm{m}]$
- $c$: Phase speed $[\mathrm{m/s}]$ (constant across all wavelengths; non-dispersive)
- $\gamma_{\mathrm{eff}}(x, z) = \gamma_{0} + \gamma_{\mathrm{sponge}}(x, z)$: Effective damping rate $[1/\mathrm{s}]$

### 2.2 Internal Texture Layout
| Texture Variable | Format | Role & Channel Allocation |
| :--- | :---: | :--- |
| `stateTexA`, `stateTexB` (`_CurrentState`, `_NextState`) | `RGFloat` (8 B/px) | Ping-Pong state buffers in fp32 precision. `R`: Current surface elevation $\eta^{n}\ [\mathrm{m}]$, `G`: Vertical velocity $v^{n} = \frac{\partial \eta}{\partial t}\ [\mathrm{m/s}]$. The fp32 upgrade reduces 10-second cumulative truncation drift for long waves from $223.78\%$ down to $0.00008\%$. |
| `obstacleTex` (`_ObstacleTex`) | `R8` (1 B/px) | Obstacle mask (`1.0` = solid wall / slit barrier, `0.0` = open water) |

### 2.3 Numerical Discretization Scheme

#### (1) CFL Condition and Automatic Sub-Stepping
Based on the 2D explicit Courant-Friedrichs-Lewy (CFL) stability limit, each frame step $dt$ is automatically subdivided into $M_{\mathrm{sub}}$ substeps of size $\Delta t$:

$$
\Delta t_{\max} = 0.95 \frac{\Delta x}{\sqrt{2}\,c}, \quad M_{\mathrm{sub}} = \max\!\left(1,\ \left\lceil \frac{dt}{\Delta t_{\max}} \right\rceil\right), \quad \Delta t = \frac{dt}{M_{\mathrm{sub}}}
$$

#### (2) 9-Point Isotropic Laplacian (`ComputeLaplacian`)
To eliminate grid-axial vs. diagonal phase-velocity anisotropy inherent to the standard 5-point stencil, an $O(\Delta x^{2})$ isotropic 9-point stencil is used:

$$
\nabla^{2} \eta_{i,j}^{n} \approx \frac{4(\eta_{i-1,j}^{n} + \eta_{i+1,j}^{n} + \eta_{i,j-1}^{n} + \eta_{i,j+1}^{n}) + (\eta_{i-1,j-1}^{n} + \eta_{i+1,j-1}^{n} + \eta_{i-1,j+1}^{n} + \eta_{i+1,j+1}^{n}) - 20\eta_{i,j}^{n}}{6\Delta x^{2}}
$$

#### (3) Symplectic Euler (Kick-Drift) Time Integration (`CSStep`)
Tracking physical velocity $v = \partial_{t}\eta$ directly in `RGFloat` state textures, the system is advanced via a symplectic Euler (Kick-Drift) scheme with centered half-step Cayley damping:

$$
v^{n+1} = \frac{v^{n}\left(1 - \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t\right) + \Delta t\, c^{2}\nabla^{2}\eta^{n}}{1 + \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t} \quad \text{[Kick]}
$$

$$
\eta^{n+1} = \eta^{n} + \Delta t\, v^{n+1} \quad \text{[Drift]}
$$

This update formula exactly matches the shader implementation in `WaveEquation.compute` (lines 118–121) and Section 4.4.
- **Undamped regime ($\gamma_{\mathrm{eff}} = 0$)**: Exhibits a strict symplectic geometric structure, conserving phase-space area ($\mathrm{d}\eta \wedge \mathrm{d}v = \mathrm{const}$) and preserving Hamiltonian energy over extended runtimes without artificial amplitude decay.
- **Damped regime ($\gamma_{\mathrm{eff}} > 0$)**: Governed by the centered half-step Cayley factor $\frac{1 - \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t}{1 + \frac{1}{2}\gamma_{\mathrm{eff}}\Delta t} \in (-1, 1)$, ensuring unconditional numerical stability and 2nd-order local damping accuracy matching Crank-Nicolson formulation without numerical oscillation or blow-up.
- **State preservation & convergence**: Because physical velocity $v$ is stored directly, dynamic adjustments to wave speed $c$ or substep width $\Delta t$ during runtime do not cause the artificial energy discontinuities associated with two-step backward difference formulations.
When evaluated on the simultaneous state pair $(\eta^{n}, v^{n})$ at integer time steps $n$, the Kick-Drift time integration exhibits strict 1st-order convergence (error reduction ratio $\approx 2.01\times$ upon halving $\Delta t$, empirical order $1.00$). While it is 2nd-order as a staggered leapfrog sequence for $v^{n+1/2}$, storing and rendering simultaneous $(h, v)$ on GPU textures matches the 1st-order simultaneous state convergence. Spatial discretization error ($O(\Delta x^{2})$ from the 9-point Laplacian) and temporal integration error ($O(\Delta t)$ on the simultaneous pair) are verified independently (reproducible test: `Docs/NumericalVerificationSuite.py`).

Here, the perimeter sponge damping $\gamma_{\mathrm{sponge}}$ is defined in terms of the minimum cell distance to the domain boundary $d_{\mathrm{border}} = \min(i,\ N_{x} - 1 - i,\ j,\ N_{z} - 1 - j)$:

$$
\gamma_{\mathrm{sponge}} = \begin{cases} S_{\mathrm{sponge}} \left(1 - \frac{d_{\mathrm{border}}}{W_{\mathrm{sponge}}}\right)^{2} & (d_{\mathrm{border}} < W_{\mathrm{sponge}}) \\ 0 & (d_{\mathrm{border}} \ge W_{\mathrm{sponge}}) \end{cases}
$$

For obstacle cells where `_ObstacleTex[pos].r > 0.5`, either a homogeneous Dirichlet condition ($\eta = 0, v = 0$) or zero-flux Neumann reflection is enforced.

#### (4) Smooth Compact $C^{2}$-Polynomial Impulse Injection (`CSAddImpulse`)
To eliminate high-frequency Gibbs ringing produced by truncated Gaussians, an isotropic compact $C^{2}$ polynomial bell function is used:

$$
\Delta \eta = \begin{cases} A \left(1 - \left(\frac{\|\mathbf{u} - \mathbf{u_{0}}\|}{R_{\mathrm{uv}}}\right)^{2}\right)^{3} & (\|\mathbf{u} - \mathbf{u_{0}}\| < R_{\mathrm{uv}}) \\ 0 & (\|\mathbf{u} - \mathbf{u_{0}}\| \ge R_{\mathrm{uv}}) \end{cases}
$$

This function has vanishing first and second derivatives at the boundary ($\left.\nabla \Delta \eta\right|_{d = R_{\mathrm{uv}}} = 0$), exciting perfectly smooth wave ripples with zero initial velocity shock ($v = 0$).

#### (5) Physical Normal and Curvature Foam Computation (`CSComputeNormal`)
Executed once after the substep loop completes, computing central-difference surface gradients and unit physical normal $\mathbf{N_{\mathrm{phys}}}$:

$$
\frac{\partial \eta}{\partial x} = \frac{\eta_{i+1,j} - \eta_{i-1,j}}{2\Delta x}, \quad \frac{\partial \eta}{\partial z} = \frac{\eta_{i,j+1} - \eta_{i,j-1}}{2\Delta x}
$$

$$
\mathbf{N_{\mathrm{phys}}} = \mathrm{normalize}\!\left(-\frac{\partial \eta}{\partial x},\ 1.0,\ -\frac{\partial \eta}{\partial z}\right), \quad \mathrm{Foam} = \mathrm{saturate}\!\left(-2.0 \left(\eta_{i-1,j} + \eta_{i+1,j} + \eta_{i,j-1} + \eta_{i,j+1} - 4\eta_{i,j}\right)\right)
$$

The `NormalVisual` mode displays this true unit physical normal, while visual exaggeration factor `_NormalExaggeration` (default $2.5$) is applied only inside the realistic shading pass.

---

## 3. Method 2: Boussinesq Shallow-Water & Dispersive Wave System (`BoussinesqSimulation`)

Implementation files: [`BoussinesqSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/BoussinesqSimulation.cs), [`Boussinesq.compute`](../Packages/com.nobnak.water-simulation/Runtime/Boussinesq/Boussinesq.compute)

### 3.1 Governing Equations (Peregrine 1967 Standard Boussinesq Dispersive System)
Conservation of mass (continuity) and conservation of depth-averaged horizontal momentum $\mathbf{u} = (u, w)^{\mathrm{T}}$ over variable bathymetry $h(x, z)$:

$$
\frac{\partial \eta}{\partial t} + \frac{\partial (Hu)}{\partial x} + \frac{\partial (Hw)}{\partial z} = S_{\eta}
$$

$$
\left(I - \alpha \left(\frac{1}{2}h\nabla(h\nabla\cdot) - \frac{h^{2}}{6}\nabla^{2}\right)\right)\frac{\partial \mathbf{u}}{\partial t} = -g\nabla\eta - (\mathbf{u}\cdot\nabla)\mathbf{u} - \frac{c_{f}}{H}\mathbf{u} + \mathbf{S_{u}} = \mathbf{f}
$$

- Over flat bathymetry ($\nabla h = 0$), the linear dispersion operator exactly matches the standard Peregrine (1967) $1/3$ coefficient:

$$
\left(I - \alpha \frac{h^{2}}{3}\nabla^{2}\right)\frac{\partial \mathbf{u}}{\partial t} = \mathbf{f}
$$

At $kh = 1$, the phase velocity error relative to the finite-depth linear dispersion relation $\omega^{2} = gk\tanh(kh)$ is only **$-0.764\%$** (substantially improved from $+6.09\%$ in the previous ad-hoc $1/6$ model), accurately capturing physical frequency dispersion from long waves into intermediate depths.
- $H(x, z, t) = \max(0.05,\ \max(0.2, h) + \eta)$: Total water column depth $[\mathrm{m}]$
- $\alpha \in [0, 1.2]$: Dispersion factor ($\alpha = 1.0$: Peregrine dispersive waves, $\alpha = 0.0$: Non-dispersive Nonlinear Shallow Water equations)
- $c_{f}$: Seabed friction coefficient (`_BedFriction`)
- $S_{\eta},\ \mathbf{S_{u}} = (0, S_{w})^{\mathrm{T}}$: Internal wavemaker mass and momentum source terms

### 3.1.1 Physical Applicability Regime & Relative Depth $kh$ Diagnostics
The Peregrine Boussinesq equations assume shallow-to-intermediate water conditions ($\mu = h/\lambda \le 0.16$, $kh \le 1.0$). In the short-wave limit ($kh \to \infty$), the frequency saturates at the cutoff frequency $\omega_{\mathrm{cutoff}} = \sqrt{3g/h}$ ($f_{\mathrm{cutoff}} = \frac{1}{2\pi}\sqrt{3g/h}$), and the group velocity drops to zero: $c_{g} = \frac{\sqrt{gh}}{(1 + \frac{1}{3}h^{2}k^{2})^{3/2}} \to 0$.
The physical applicability of shallow-water dispersive models is diagnosed directly from the wavenumber-depth product (relative depth $kh = 2\pi h / \lambda$) rather than domain size:

1. **Shallow-Water Limit ($kh < 0.31$, $h/\lambda < 0.05$)**:
   Airy / NLSW non-dispersive shallow water limit. Dispersion is negligible; waves propagate at $c \approx \sqrt{gh}$.
2. **Intermediate-Water Regime ($0.31 \le kh \le 1.0$, $h/\lambda \le 0.16$)**:
   Optimal validity range for Peregrine (1967) 1/3 polynomial dispersion. Phase speed error against Airy linear wave theory is $< 2\%$ ($-0.76\%$ at $kh=1.0$), accurately capturing frequency dispersion, solitons, and shoaling.
3. **Deep-Water Transition ($1.0 < kh \le 1.5$)**:
   Exceeds polynomial approximation bounds; phase speed error increases ($> 2\%$ to $10\%$). Flagged with an Inspector/UI warning.
4. **Cutoff / Evanescent Limit ($kh > 1.5$, $f \ge 0.85 f_{\mathrm{cutoff}}$)**:
   Approaches Peregrine singularity $kh = \sqrt{3} \approx 1.732$ ($k^{2} < 0$), where group velocity vanishes, trapping wave energy into local standing oscillations.
5. **Parameter Adjustment Guide (`AdjustParametersForBoussinesqRegime`)**:
   Automatically calculates and applies depth adjustments targeting the valid intermediate regime ($kh \le 1.0$, target $kh \approx 0.8$). This provides an engineering parameter guideline for model validity rather than an unconditional stability guarantee against nonlinear wave breaking.

### 3.2 Internal Texture Layout
To prevent floating-point quantization under small substep intervals ($\Delta t \approx 0.004 \sim 0.01\ \mathrm{s}$), **`fp32` (`ARGBFloat` / `RGFloat`)** buffers are used for state, force, and acceleration fields.

| Texture Variable | Format | Bytes / Texel | Role & Channel Allocation |
| :--- | :---: | :---: | :--- |
| `stateTexA`, `stateTexB` (`_StateIn`, `_StateOut`) | `ARGBFloat` | 16 Bytes | Ping-Pong state buffers. `R`: $\eta\ [\mathrm{m}]$, `G`: $u\ [\mathrm{m/s}]$, `B`: $w\ [\mathrm{m/s}]$, `A`: 0 |
| `predTex` (`_StatePred`) | `ARGBFloat` | 16 Bytes | Intermediate MacCormack predictor state $(\eta^{\ast}, u^{\ast}, w^{\ast}, 0)$ |
| `forceOrigTex` (`_ForceOrigTex`) | `RGFloat` | 8 Bytes | Original non-dispersive acceleration $\mathbf{f}_{\mathrm{orig}} = (f_{x}, f_{z})$ (immutable across sweeps) |
| `forceTex` (`_ForceTex`) | `RGFloat` | 8 Bytes | Working RHS force vector for ADI PCR solver (holds $\mathbf{f}_{\mathrm{orig}} + \text{defect}$ at sweep $s$) |
| `forcePredTex` (`_ForcePredTex`) | `RGFloat` | 8 Bytes | Predictor dispersed acceleration $\mathbf{a}^{(1)} = L^{-1}\mathbf{f}^{n}$ storage |
| `accelTexA`, `accelTexB` (`_AccelPing`, `_AccelPong`) | `RGFloat` | 8 Bytes | ADI tridiagonal PCR dispersion solver Ping-Pong buffers ($\mathbf{a}^{\ast}, \mathbf{a}$) |
| `bathymetryTex` (`_BathymetryTex`) | `ARGBHalf` | 8 Bytes | `R`: Still water depth $h\ [\mathrm{m}]$, `G`: Seabed elevation $z_{b} = -h\ [\mathrm{m}]$ |

### 3.3 Numerical Discretization Scheme

#### (1) CFL Condition and Automatic Sub-Stepping
Substeps are determined from the nonlinear wave speed $c_{\max} = \sqrt{g(h_{\max} + A_{\max})} + u_{\max}$ including wave amplitude and orbital velocity:

$$
\Delta t_{\max} = 0.55 \frac{\min(\Delta x, \Delta z)}{\sqrt{2}\,c_{\max}}, \quad M_{\mathrm{sub}} = \max\!\left(1,\ \left\lceil \frac{dt}{\Delta t_{\max}} \right\rceil\right), \quad \Delta t = \frac{dt}{M_{\mathrm{sub}}}
$$

#### (2) Symmetric MacCormack Predictor-Corrector Scheme (`CSPredict` & `CSCorrect`)
To achieve second-order spatial accuracy while eliminating odd-even grid decoupling ($2\Delta x$ checkerboard modes) on a collocated grid, an alternating MacCormack Predictor-Corrector scheme is used:

1. **Predictor (`CSPredict`)**:
   Using direction sign $s_{1} = (\sigma_{\mathrm{parity}} == 0)\ ?\ +1 : -1$ and signed inverse spacing $\tilde{\Delta_{1}} = s_{1} / \Delta x$, one-sided differences with neighbor cells $(i + s_{1}, j)$ and $(i, j + s_{1})$ evaluate mass flux divergence and momentum advection:

$$
\delta_{x}^{(1)}(Hu) = (H_{i+s_{1},j}u_{i+s_{1},j} - H_{i,j}u_{i,j})\tilde{\Delta_{1}}, \quad \delta_{z}^{(1)}(Hw) = (H_{i,j+s_{1}}w_{i,j+s_{1}} - H_{i,j}w_{i,j})\tilde{\Delta_{1}}
$$

$$
\eta^{\ast} = \eta^{n} - \Delta t\left(\delta_{x}^{(1)}(Hu) + \delta_{z}^{(1)}(Hw) - S_{\eta}(\phi^{n})\right)
$$

   Non-dispersive RHS force $\mathbf{f^{n}} = (f_{x}^{n}, f_{z}^{n})$ is written to `_ForceTex`. When dispersion is active ($\alpha > 0.001$), this force is routed to the ADI PCR inverter.

2. **Corrector (`CSCorrect`)**:
   Using the opposite direction sign $s_{2} = -s_{1}$ ($\tilde{\Delta_{2}} = s_{2} / \Delta x$) on `_StatePred`, the corrector averages the forward and backward updates:

$$
\eta^{n+1} = D_{\mathrm{sponge}} \cdot \frac{1}{2}\left[\eta^{n} + \eta^{\ast} - \Delta t\left(\delta_{x}^{(2)}(H^{\ast} u^{\ast}) + \delta_{z}^{(2)}(H^{\ast} w^{\ast}) - S_{\eta}(\phi^{n+1})\right)\right]
$$

   Corrector RHS force $\mathbf{f^{\ast}} = (f_{x}^{\ast}, f_{z}^{\ast})$ is stored into `_ForceTex`.

#### (3) Irrotational Componentwise Helmholtz Dispersion Model, ADI PCR Solver & True Defect Correction (`CSTridiagonalX`, `CSTridiagonalZ`, `CSDefectCorrection`)
The continuous PDE solved by this implementation is the **irrotational componentwise Helmholtz Boussinesq model** under the assumptions of irrotational flow ($\nabla \times \mathbf{u} = \mathbf{0}$, meaning $\nabla(\nabla \cdot \mathbf{a}) \equiv \nabla^{2}\mathbf{a}$) and slowly varying bathymetry ($|\nabla h| \ll 1$):

$$
\left(I - \alpha \frac{h^{2}}{3}\nabla^{2}\right)\mathbf{a} = \mathbf{f} \iff \begin{cases} \left(I - \alpha \frac{h^{2}}{3}\nabla^{2}\right) a_{x} = f_{x} \\ \left(I - \alpha \frac{h^{2}}{3}\nabla^{2}\right) a_{z} = f_{z} \end{cases}
$$

In full variable-bathymetry Peregrine (1967) systems, the 2D dispersion operator includes bathymetric gradient couplings and potential rotational velocity cross-derivatives ($\partial_{xz} a_{z}$). Under the physical assumptions of irrotational surface gravity wave propagation on mild beds, those cross-coupling terms are omitted.

The 2D isotropic Helmholtz operator is solved via Peaceman-Rachford Alternating Direction Implicit (ADI) splitting:

$$
\left(I - \beta_{x}\partial_{x}^{2}\right)\left(I - \beta_{z}\partial_{z}^{2}\right)\mathbf{a} = \mathbf{f} + \beta_{x}\beta_{z}\partial_{x}^{2}\partial_{z}^{2}\mathbf{a}, \quad \beta_{x} = \frac{\alpha h^{2}}{3\Delta x^{2}}, \quad \beta_{z} = \frac{\alpha h^{2}}{3\Delta z^{2}}
$$

- **Continuum Symbol & Directional Properties**:
  In Fourier space, the direct ADI factored operator exhibits gain $G_{\mathrm{ADI}} = \frac{1}{(1 + \frac{1}{3}k_{x}^{2}h^{2})(1 + \frac{1}{3}k_{z}^{2}h^{2})}$.
  - Grid Axes ($0^{\circ}, 90^{\circ}$, $k_{x}=0$ or $k_{z}=0$): Cross-terms vanish, matching the target Helmholtz gain $G_{\mathrm{Helmholtz}} = \frac{1}{1 + \frac{1}{3}k^{2}h^{2}}$ exactly ($0.00\%$ error, zero residual).
  - $45^{\circ}$ Diagonal: Directional splitting error $\frac{1}{9}k_{x}^{2}k_{z}^{2}h^{4}$ introduces a phase speed bias of **$-1.03\%$** at $kh=1.0$.
- **True ADI Defect Correction (`CSDefectCorrection`)**:
  The defect correction compensates for the **4th-order directional factorization splitting error $\beta_{x}\beta_{z}\partial_{x}^{2}\partial_{z}^{2}\mathbf{a}$**, NOT general 2D rotational cross-component coupling.
  In each predictor and corrector stage, the non-dispersive force $\mathbf{f}_{\mathrm{orig}}$ is stored immutably in `_ForceOrigTex`. At sweep $s \ge 2$, the working RHS is assembled as $\mathbf{f}_{\mathrm{work}} = \mathbf{f}_{\mathrm{orig}} + \beta_{x}\beta_{z}\partial_{x}^{2}\partial_{z}^{2}\mathbf{a}^{(s-1)}$. Because previous sweep defects are not accumulated into $\mathbf{f}_{\mathrm{orig}}$, the iteration is a strict contraction mapping with rate $\rho = \frac{\beta^{2}}{(1+\beta)^{2}} \approx \frac{1}{49}$:
  - **Sweeps 1 (Direct ADI)**: $45^{\circ}$ error $-1.0257\%$, residual $2.04 \times 10^{-2}$ (recommended for fast real-time rendering)
  - **Sweeps 2 (1 Correction)**: $45^{\circ}$ error **$-0.0208\%$**, residual $4.16 \times 10^{-4}$ ($49\times$ error reduction, recommended for high precision)
  - **Sweeps 3 (2 Corrections)**: $45^{\circ}$ error **$-0.000425\%$**, residual $8.50 \times 10^{-6}$
  - **Sweeps 4 (3 Corrections)**: $45^{\circ}$ error **$-0.000009\%$**, residual $1.73 \times 10^{-7}$
- **Boundary Separation & Final Normal Velocity Enforcement**:
  At solid lateral boundaries ($x = 0, W-1$, `SideWallSponge = false`), the PCR dispersion solver enforces $a_{x} = 0.00000000$ strictly on decoupled Dirichlet rows.
  Furthermore, the final biharmonic spatial filter (`CSFilterHighFrequency`) strictly re-enforces zero normal velocity ($u=0$ on lateral solid walls, $w=0$ on offshore/beach borders) on the final output state. This prevents filter-induced velocity re-injection, guaranteeing that final wall normal velocity and normal flux $H u$ are strictly **$0.00000000$** across all stages, while conserving total water mass $\sum \eta$ to machine precision ($\Delta \mathrm{Mass} < 10^{-14}$).

#### (4) Time Stepping & Numerical Convergence Properties (`CSApplyPredictedDispersion` & `CSApplyDispersion`)
- **Boussinesq System**:
  Couples MacCormack predictor forces $\mathbf{f}^{n}$ and corrector forces $\mathbf{f}^{\ast}$ with PCR dispersion solves, applying a second-order Heun velocity update:

$$
\mathbf{u}^{\ast} = D_{\mathrm{sponge}} \cdot \left(\mathbf{u}^{n} + \Delta t\,\mathbf{a}^{(1)}\right)
$$
$$
\mathbf{u}^{n+1} = D_{\mathrm{sponge}} \cdot \left(\mathbf{u}^{n} + \frac{1}{2}\Delta t\left(\mathbf{a}^{(1)} + \mathbf{a}^{(2)}\right)\right)
$$

  where $D_{\mathrm{sponge}} = \frac{1}{1 + 15\,\Delta t\, S_{\mathrm{sponge}} \left(1 - \frac{d_{\mathrm{border}}}{W_{\mathrm{sponge}}}\right)^{2}}$ is the implicit sponge factor.

- **Wave Equation System (`CSStep`)**:
  Stores physical velocity $v = \partial\eta/\partial t$ in `RGFloat` format, integrated via the Symplectic Euler (Kick-Drift) method:

$$
v^{n+1} = \frac{v^{n}(1 - \frac{1}{2}\gamma\Delta t) + \Delta t\, c^{2}\nabla^{2}\eta^{n}}{1 + \frac{1}{2}\gamma\Delta t} \quad \text{[Kick]}
$$
$$
\eta^{n+1} = \eta^{n} + \Delta t\, v^{n+1} \quad \text{[Drift]}
$$

  Symplectic preservation of phase space volume ($\mathrm{d}\eta \wedge \mathrm{d}v$) ensures zero numerical dissipation over long runs.
  Due to half-step staggering, the simultaneous state pair $(\eta, v)$ evaluated at $t_{n+1}$ exhibits $O(\Delta t)$ truncation error (measured error reduction ratio $2.01\times$ per halving of $\Delta t$, 1st-order convergence), while the staggered elevation sequence maintains $O(\Delta t^{2})$ (reproducible test suite: `Docs/NumericalVerificationSuite.py`).

#### (4b) Time-Step Scaled Conservative Directional-Split 4th-Order Biharmonic Filter (`CSFilterX` & `CSFilterZ`)
Inverse Helmholtz dispersion suppresses natural numerical viscosity for modes near $2\Delta x$. In the previous unsplit 2D filter, the maximum eigenvalue of 32 for the 2D checkerboard mode constrained the explicit stability limit to $\kappa \le 1/32 = 0.03125$, restricting single-step damping of 1D along-crest corrugations (eigenvalue 16) to at most $32\%$ ($68\%$ surviving). To eliminate along-crest ripples and high-frequency oscillations without introducing time-step distortion or mass loss, a directional-split biharmonic filter is applied at the end of every substep:

1. **Directional Splitting (Pass X $\to$ Pass Z) and $L^2$ Stability Limit**:
   Executes Pass 1 along X $(I - \kappa_x D_{4, x})$ followed by Pass 2 along Z $(I - \kappa_z D_{4, z})$. Since the 1D 4th-difference operator has maximum eigenvalue 16, the explicit $L^2$ stability limit is expanded to $\kappa \le 1/16 = 0.0625$. The combined 2D amplification factor $G(k_x, k_z) = (1 - 16\kappa_x \sin^4(k_x \Delta x / 2))(1 - 16\kappa_z \sin^4(k_z \Delta z / 2)) \in [0, 1]$ ensures high-frequency mode non-amplification ($L^2$ stability). Single-substep damping for $2\Delta x$ along-crest sawtooth ripples is increased from $32\%$ up to **$77\%$** ($23\%$ residual).

   > [!IMPORTANT] Mathematical Monotonicity and Spatial Extrema Note
   > A Fourier amplification factor bounded in $[0, 1]$ does not imply spatial monotonicity or Total Variation Diminishing (TVD) behavior in physical space. The 4th-order biharmonic stencil $[-\kappa, 4\kappa, 1-6\kappa, 4\kappa, -\kappa]$ has negative outer lobes ($-\kappa$). Consequently, sharp peaks ($\sigma \lesssim 1.5\,\Delta x$), delta impulses, and step discontinuities inherently generate $O(\kappa)$ undershoots (e.g. depth $-\kappa = -0.058$ for a unit delta pulse) and overshoots. While the filter suppresses high-frequency ripples, it does not guarantee monotonic extrema preservation, and increasing filter strength alone cannot replace addressing the underlying discretization dynamics in shoaling regions (verification suite: `Docs/NumericalVerificationSuite.py`).

2. **Time-Step Scaling and Explicit Stability Limit**:
   Filter dissipation scales linearly with $\Delta t$ to match physical continuum diffusion $\partial S / \partial t = -\nu \nabla^{4} S$:

$$
\kappa(\Delta t) = \min(0.058, 12.0\,\Delta t)
$$

   In the operational range $\Delta t \le 0.00483\ \mathrm{s}$, the dissipation rate $\kappa / \Delta t = 12.0\ \mathrm{s}^{-1}$ remains strictly constant across arbitrary substep durations without artificial lower clamps, while respecting the 1D explicit stability boundary $\kappa \le 0.058 < 0.0625$.

3. **Conservative Zero-Flux Boundary Stencil (Exact Mass Conservation)**:
   Enforces boundary fluxes $J_{-1/2} = J_{W-1/2} = 0$:

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

   For any surface elevation field, $\sum D_{4, x} S \equiv 0$ and $\sum D_{4, z} S \equiv 0$ hold independently across every row and column to machine precision (measured $\le 1.95 \times 10^{-14}$), guaranteeing exact conservation of total water volume. Lateral solid walls re-enforce $u = 0$ after Pass X, and offshore/beach borders re-enforce $w = 0$ after Pass Z.

4. **Upwind Differencing for Convective Advection (Elimination of Parity Oscillations)**:
   In `CSPredict` and `CSCorrect`, non-conservative convective velocity advection $(\mathbf{u} \cdot \nabla)\mathbf{u} = (u \partial_x u + w \partial_z u, u \partial_x w + w \partial_z w)$ previously used the alternating MacCormack parity direction `invDx = dir / dx` ($dir \in \{-1, +1\}$). For forward-moving waves ($w > 0$), forward differencing ($dir = +1$) acts as downwind differencing, introducing negative numerical viscosity $-\frac{1}{2} w \Delta z \partial_{z}^2 w$ (artificial amplification). Backward differencing ($dir = -1$) acts as upwind differencing (positive numerical damping). Alternating `_StepParity` every substep flipped between artificial amplification and damping, causing the crest peak height to jump up and down rapidly.
   Decoupling the convective velocity derivatives to follow velocity-sign upwind differencing ($dir = -\mathrm{sgn}(u)$) ensures strictly positive numerical dissipation, reducing maximum substep peak jumps from $0.01668\,\mathrm{m}$ to **$0.00345\,\mathrm{m}$** ($79.3\%$ reduction, $\le 3.5\,\mathrm{mm}$).

5. **Strictly Mass-Conservative Flux-Corrected Transport (FCT Face-Flux Limiter)**:
   While cell-wise clamping ($[ \min(S_{i-1}, S_i, S_{i+1}), \max(S_{i-1}, S_i, S_{i+1}) ]$) truncates spurious local extrema in physical space, clamping cells independently destroys the finite-volume conservation relation, leading to $+9.6\% \sim +33.6\%$ artificial mass gain for positive solitary waves. To resolve this, a strictly conservative Zalesak (1979) FCT face-flux limiter is implemented directly on inter-cell faces:
   - **Low-Order Diffusive Face Flux $F^{(2)}_{i+1/2}$**: When the face slope $|\eta_{i+1} - \eta_i| / \Delta x > 0.40$ (breaking threshold), a monotonic 2nd-order diffusive flux $F^{(2)}_{i+1/2} = -\kappa_{2, i+1/2} (S_{i+1} - S_i)$ is evaluated across shared cell boundaries.
   - **High-Order Biharmonic Face Flux $F^{(4)}_{i+1/2}$**: Evaluates anti-diffusive biharmonic fluxes satisfying exact zero-flux boundary conditions $F^{(4)}_{-1/2} = F^{(4)}_{W-1/2} = 0$.
   - **Face-Flux Limiter Coefficient $C_{i+1/2} \in [0, 1]$**: Applied to the high-order face flux to guarantee that the low-order solution $S^L_i = S_i - (F^{(2)}_{i+1/2} - F^{(2)}_{i-1/2})$ remains within the local monotonic bounds $[S^{\min}_i, S^{\max}_i]$ without cell-wise clamping.
   - **Strict Global Mass Conservation**: Because updates occur purely through face fluxes $\Delta S_i = -(F_{i+1/2} - F_{i-1/2})$, the domain sum satisfies $\sum_{i=0}^{W-1} \Delta S_i = -(F_{W-1/2} - F_{-1/2}) \equiv 0$, preserving water mass to machine precision ($\Delta M \le 4.13 \times 10^{-8}$, relative mass error $\sim 10^{-16}$).
   - **Velocity Boundary Decoupling**: Separates $\eta$ mass conservation from velocity boundary projections ($u=0$ on solid lateral walls after Pass X, $w=0$ on offshore/beach borders after Pass Z).

#### (5) Internal Mass Source Wavemaker (`GetWavemakerSources`) and GPU Soliton Injection (`CSAddImpulse`)
- **Wei & Kirby (1999) Internal Mass Source Wavemaker (`GetWavemakerSources`)**:
  Within $|j - y_{0}| \le 18$ cells of the offshore line $y_{0} = \lfloor v_{\mathrm{wm}} N_{z} \rfloor$, waves are generated via an internal Gaussian mass source $G(j) = \frac{1}{\sqrt{2\pi}\sigma\Delta z}\exp\!\left(-\frac{(j - y_{0})^{2}}{2\sigma^{2}}\right)$ ($\sigma = 4.0$ cells). Pure mass injection ($S_w = 0$) eliminates momentum imbalance shocks and localized divergence spikes:

$$
S_{\eta} = 2 c A_{\mathrm{wm}} \sin(\phi)\, G(j)\, T_{\mathrm{lat}}(i), \quad S_{w} = 0 \quad \left(c = \sqrt{g \max(0.5, h)}\right)
$$

  The factor of 2 accounts for equal partition between the forward $+Z$ wave and backward $-Z$ wave (absorbed by the offshore sponge).
  Furthermore, when `SideWallSponge` is disabled (flume mode default), $T_{\mathrm{lat}} = 1.0$ and lateral boundaries enforce Neumann conditions ($\partial_x a_z = 0$), preserving a perfectly flat 1D planar crest across the full width.

- **Planar Soliton and Circular Gaussian Pulse Injection (`CSAddImpulse`)**:
  - **Planar Soliton (`_ImpulseShape == 0`)**:

$$
\Delta \eta = A\,{\mathrm{sech}}^{2}\!\left(\frac{v - v_{0}}{0.6 R_{\mathrm{uv}}}\right) T_{\mathrm{lat}}(i), \quad c = \sqrt{g(h + \max(0, \Delta\eta))}, \quad \Delta w = \frac{c\,\Delta\eta}{h + \max(0, \Delta\eta)}
$$

    is added non-destructively to the existing $(\eta, w)$ state.
  - **Circular Pulse (`_ImpulseShape == 1`)**:
    $\Delta \eta = A \exp(-3 (\|\mathbf{u} - \mathbf{u_{0}}\| / R_{\mathrm{uv}})^{2})$ is added non-destructively to $\eta$.

#### (5) Procedural Bathymetry (`UpdateBathymetryTexture`)
For normalized coordinates $(u, v) \in [0, 1]^{2}$, deep depth $h_{\mathrm{deep}}$, and shallow depth $h_{\mathrm{shallow}}$:
- **`Flat`**: $h(u, v) = h_{\mathrm{deep}}$
- **`LinearSlope`**: $h(u, v) = \max(0.5,\ \mathrm{lerp}(h_{\mathrm{deep}}, h_{\mathrm{shallow}}, v))$
- **`ShoalAndCape`**:
  - Baseline slope: $h_{\mathrm{base}}(v) = \mathrm{lerp}(h_{\mathrm{deep}},\ h_{\mathrm{shallow}} + 2.0,\ \mathrm{smoothstep}(0.2, 0.9, v))$
  - Diagonal shoal ridge: $d_{\mathrm{shoal}} = (u - 0.5)\cos(0.6) + (v - 0.45)\sin(0.6),\ f_{\mathrm{shoal}} = \exp(-d_{\mathrm{shoal}}^{2} / 0.025)$
  - Circular cape / headland: $d_{\mathrm{cape}} = \|(u, v) - (0.72, 0.62)\|,\ f_{\mathrm{cape}} = \exp(-d_{\mathrm{cape}}^{2} / 0.015)$
  - Combined depth: $h(u, v) = \max\!\left(0.5,\ \mathrm{lerp}\!\left(\mathrm{lerp}(h_{\mathrm{base}}, h_{\mathrm{shallow}}, 0.75 f_{\mathrm{shoal}}),\ 0.5 h_{\mathrm{shallow}},\ 0.85 f_{\mathrm{cape}}\right)\right)$

#### (6) Surface Normal and Steepness Foam (`CSComputeNormal`)
Evaluates central-difference surface gradients and generates whitecap foam where surface slope exceeds $0.15$:

$$
\frac{\partial \eta}{\partial x} = \frac{\eta_{i+1,j} - \eta_{i-1,j}}{2\Delta x}, \quad \frac{\partial \eta}{\partial z} = \frac{\eta_{i,j+1} - \eta_{i,j-1}}{2\Delta x}
$$

$$
\mathbf{N} = \mathrm{normalize}\!\left(-2.0\frac{\partial \eta}{\partial x},\ 1.0,\ -2.0\frac{\partial \eta}{\partial z}\right), \quad \mathrm{Foam} = \mathrm{saturate}\!\left(3.0\left(\|\nabla\eta\| - 0.15\right)\right)
$$

---

## 4. Method 3: Spectral / FFT Ocean Model (`FFTOceanSimulation`)

Implementation files: [`FFTOceanSimulation.cs`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOceanSimulation.cs), [`FFTOcean.compute`](../Packages/com.nobnak.water-simulation/Runtime/FFTWater/FFTOcean.compute)

### 4.1 Governing Equations and Spectral Synthesis

#### (1) Wavenumber Grid and Phillips + Swell Composite Spectrum (`Phillips`, `CSGenerateSpectrum`)
For grid resolution $N \times N$ ($N = 2^{M}$) and domain length $L$ (`_LengthScale1`), the fundamental wavenumber step is $\Delta k = \frac{2\pi}{L}$ and the wavenumber vector is $\mathbf{k} = (k_{x}, k_{z}) = (n_{x} - \frac{N}{2},\ n_{z} - \frac{N}{2})\Delta k$ ($k = \|\mathbf{k}\|,\ \hat{\mathbf{k}} = \mathbf{k}/k$).
Given wind speed $V$ (`_WindSpeed`), unit wind direction $\hat{\mathbf{w}}$, swell strength $A_{\mathrm{swell}}$, and unit swell direction $\hat{\mathbf{s}}$, the variance spectrum $P(\mathbf{k})$ is:

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

Using standard normal random pairs $(\xi_{r}, \xi_{i}) \sim \mathcal{N}(0, 1)$ (generated via integer Wang/Jenkins hash + Box-Muller transform) and discrete wavenumber spacing $\Delta k = \frac{2\pi}{L}$, the initial complex amplitude $h_{0}(\mathbf{k})$ and conjugate term $h_{0}^{\ast}(-\mathbf{k})$ are stored in `_InitialSpectrum` (`ARGBFloat`):

$$
h_{0}(\mathbf{k}) = \sqrt{\frac{P(\mathbf{k})}{2}}\,\Delta k\,(\xi_{r} + i\xi_{i}), \quad h_{0}^{\ast}(-\mathbf{k}) = \sqrt{\frac{P(-\mathbf{k})}{2}}\,\Delta k\,(\xi_{r} - i\xi_{i})
$$

Here, the opposite wavenumber index is mapped with periodic wrap-around:

$$
i_{-} = (N - i) \bmod N, \quad j_{-} = (N - j) \bmod N
$$

and the DC mode $(N/2, N/2)$ and asymmetric Nyquist modes are set to zero. This ensures the imaginary residual after inverse transformation remains at machine precision (imaginary-to-real RMS ratio $3.45 \times 10^{-16}$). Incorporating $\Delta k$ into discrete amplitudes makes the significant wave height $H_{s} \approx 3.63\ \mathrm{m}$ completely independent of grid resolution $N$.

#### (2) Deep-Water Dispersion Time Evolution and Complex-Packed Single 2D IFFT (`CSTimeEvolution`)
Following the deep-water gravity wave dispersion relation $\omega(k) = \sqrt{g k}$, the complex elevation spectrum $\tilde{h}(\mathbf{k}, t)$ at time $t$ is synthesized:

$$
\tilde{h}(\mathbf{k}, t) = h_{0}(\mathbf{k})e^{i\omega(k)t} + h_{0}^{\ast}(-\mathbf{k})e^{-i\omega(k)t}
$$

The horizontal displacement spectrum $(\tilde{D_{x}}, \tilde{D_{z}})$ converging particles toward wave crests is given by the `+i` phase factor:

$$
\begin{pmatrix} \tilde{D_{x}}(\mathbf{k}, t) \\ \tilde{D_{z}}(\mathbf{k}, t) \end{pmatrix} = +i\, w_{\mathrm{chop}}(k) \frac{\mathbf{k}}{k} \tilde{h}(\mathbf{k}, t) = w_{\mathrm{chop}}(k)\frac{\mathbf{k}}{k}\left(-\mathrm{Im}\,\tilde{h} + i\,\mathrm{Re}\,\tilde{h}\right)
$$

where $w_{\mathrm{chop}}(k) = \frac{2.1}{1 + k / 0.42}$.

In this implementation, all three real displacement fields $(D_{y}, D_{x}, D_{z})$ are packed into a single 4-channel complex buffer `_SpectrumPacked` (`ARGBFloat`):

$$
\mathrm{Packed.rg} = \tilde{h}(\mathbf{k}, t) + i\,\tilde{D_{x}}(\mathbf{k}, t) = \left(\mathrm{Re}\,\tilde{h} - \mathrm{Im}\,\tilde{D_{x}}\right) + i\left(\mathrm{Im}\,\tilde{h} + \mathrm{Re}\,\tilde{D_{x}}\right)
$$

$$
\mathrm{Packed.ba} = \tilde{D_{z}}(\mathbf{k}, t) + 0i = \mathrm{Re}\,\tilde{D_{z}} + i\,\mathrm{Im}\,\tilde{D_{z}}
$$

This executes in a **single 4-channel 2D IFFT pass with zero `Graphics.Blit` passes**, transforming all three displacement components simultaneously.

### 4.2 Group-Shared Memory Radix-2 2D IFFT (`CSFFTRow`, `CSFFTCol`) and Multi-Pass Fallback
To eliminate the bit-reversal permutation pass required by Cooley-Tukey FFTs, the **Stockham Autosort algorithm** is used, automatically sorting indices between Ping-Pong buffers at each butterfly stage.
In the legacy multi-pass approach, every stage $s = 0, 1, \dots, \log_{2} N - 1$ requires reading/writing to global VRAM and binding ping-pong textures, resulting in 16 compute dispatches for the 2D IFFT and 18 dispatches total per step at $N=256$.

To optimize CPU dispatch submission and GPU pipeline transitions, resolutions $N \le 512$ employ an on-chip **Group-Shared Memory Ping-Pong 1D IFFT** (`CSFFTRow` and `CSFFTCol`):
- **Shared Memory Ping-Pong (`s_fftDataA`, `s_fftDataB`)**: Two 512-element $\times 16\ \mathrm{Bytes} = 8\ \mathrm{KB}$ buffers ($16\ \mathrm{KB} \le 32\ \mathrm{KB}$ limit) allocated within the threadgroup.
- **Single Dispatch per Axis**: Row IFFT (`CSFFTRow`) dispatches $(1, N, 1)$ threadgroups and column IFFT (`CSFFTCol`) dispatches $(N, 1, 1)$, executing all butterfly stages on-chip via `GroupMemoryBarrierWithGroupSync()`.
- **$77.8\%$ Dispatch Reduction**: Total dispatches for $N=256$ drop from 18 down to **4 dispatches per step** (1 evolution, 1 row IFFT, 1 col IFFT, 1 pack), halving CPU driver overhead.
- **Multi-Pass Fallback**: Grids with $N > 512$ automatically fall back to the multi-pass implementation (`CSFFTHorizontal` / `CSFFTVertical`).

For stage $s = 0, 1, \dots, \log_{2} N - 1$ with stride $R = 2^{s}$ and $P = 2R$, the input indices and twiddle factor for output index $x \in [0, N-1]$ are computed directly (implemented via bitwise operations `j = x & (R - 1u)`, `k = x >> s`, `t = k & 1u`, `g = k >> 1u` in HLSL):

$$
j = x - R\lfloor x / R \rfloor, \quad k = \lfloor x / R \rfloor, \quad t = k - 2\lfloor k / 2 \rfloor, \quad g = \lfloor k / 2 \rfloor
$$

$$
i_{0} = g R + j, \quad i_{1} = i_{0} + \frac{N}{2}, \quad \theta = \frac{2\pi j}{P}, \quad W = \cos\theta + i\sin\theta
$$

$$
\mathrm{Out}[x] = \begin{cases} \mathrm{In}[i_{0}] + W \cdot \mathrm{In}[i_{1}] & (t == 0) \\ \mathrm{In}[i_{0}] - W \cdot \mathrm{In}[i_{1}] & (t == 1) \end{cases}
$$

This executes $\log_{2} N$ horizontal passes followed by $\log_{2} N$ vertical passes ($2\log_{2} N$ passes per step).

### 4.3 Exact Lagrangian Normal, Jacobian Whitecap Foam, and Crest Pinch (`CSPackOceanData`)
Applying the spatial checkerboard sign $(-1)^{x+z}$ and choppiness scale $\lambda$ (`_Choppiness`) unpacks the spatial displacement vector $\mathbf{D} = (D_{x}, D_{y}, D_{z})$:

$$
s(x, z) = (-1)^{x + z}, \quad D_{y} = s \cdot \mathrm{Re}(\mathrm{Packed.rg}), \quad D_{x} = \lambda s \cdot \mathrm{Im}(\mathrm{Packed.rg}), \quad D_{z} = \lambda s \cdot \mathrm{Re}(\mathrm{Packed.ba})
$$

Central differences with periodic wrap mask `& (N - 1)` over spacing $2\Delta x = 2L/N$ yield the displacement gradient tensor $D_{\alpha,\beta} = \frac{\partial D_{\alpha}}{\partial \beta}$ and the **Jacobian determinant $J$** of the horizontal mapping $\mathbf{X_{h}}(x,z) = (x + D_{x}, z + D_{z})$:

$$
J = \left(1 + \frac{\partial D_{x}}{\partial x}\right)\left(1 + \frac{\partial D_{z}}{\partial z}\right) - \frac{\partial D_{x}}{\partial z}\frac{\partial D_{z}}{\partial x}
$$

- **Whitecap Foam Intensity**: $\mathrm{Foam} = \mathrm{saturate}\!\left(2.2(\tau_{\mathrm{foam}} - J)\right)$ ($\tau_{\mathrm{foam}}$: `_FoamThreshold`)
- **Horizontal Crest Pinch**: $P_{\mathrm{crest}} = \mathrm{saturate}(1.0 - J)$
- **Exact Jacobian-Coupled Lagrangian Normal $\mathbf{N}$**:
  Computed from the cross product $\mathbf{T_{z}} \times \mathbf{T_{x}}$ of the displaced surface tangent vectors $\mathbf{T_{x}} = (1 + D_{x,x},\ D_{y,x},\ D_{z,x})$ and $\mathbf{T_{z}} = (D_{x,z},\ D_{y,z},\ 1 + D_{z,z})$:

$$
\mathbf{N} = \mathrm{normalize}\!\begin{pmatrix} -\frac{\partial D_{y}}{\partial x}\left(1 + \frac{\partial D_{z}}{\partial z}\right) + \frac{\partial D_{y}}{\partial z}\frac{\partial D_{z}}{\partial x} \\ \max(0.08,\ J) \\ -\frac{\partial D_{y}}{\partial z}\left(1 + \frac{\partial D_{x}}{\partial x}\right) + \frac{\partial D_{y}}{\partial x}\frac{\partial D_{x}}{\partial z} \end{pmatrix}
$$

### 4.4 Internal Texture Layout (`FFTOceanSimulation`)
| Texture Variable | Format | Bytes / Texel | Role & Channel Allocation |
| :--- | :---: | :---: | :--- |
| `initialSpectrumTex` (`_InitialSpectrum`) | `ARGBFloat` | 16 Bytes | `RG`: $h_{0}(\mathbf{k})$, `BA`: $h_{0}^{\ast}(-\mathbf{k})$ |
| `spectrumPackedTex` (`_SpectrumPacked`) | `ARGBFloat` | 16 Bytes | `RG`: $\tilde{h} + i\tilde{D}_{x}$, `BA`: $\tilde{D}_{z} + 0i$ |
| `pingPongTexA`, `pingPongTexB` | `ARGBFloat` | 16 Bytes | Stockham 2D IFFT work buffers |
| `waterDataTex` (`_WaterDataTex`) | `ARGBHalf` | 8 Bytes | Output displacements $(D_{x}, \eta, D_{z}, \mathrm{Foam})$ |
| `normalTex` (`_NormalTex`) | `ARGBHalf` | 8 Bytes | Output normals $(N_{x}, N_{y}, N_{z}, P_{\mathrm{crest}})$ |

---

## 5. Common Water Surface Shader and Visualization Modes (`WaterSurfaceCommon.shader`)

Implementation files: [`WaterSurfaceCommon.shader`](../Packages/com.nobnak.water-simulation/Runtime/Shaders/WaterSurfaceCommon.shader), [`WaterRenderer.cs`](../Packages/com.nobnak.water-simulation/Runtime/Common/WaterRenderer.cs)

### 5.1 3D Vertex Displacement (`Vert`)
Samples `_WaterDataTex` at mip level 0 and adds the 3D displacement vector $\mathbf{D} = (D_{x}, \eta, D_{z})$ to the world-space vertex position:

$$
\mathbf{p_{\mathrm{WS}}} = \mathbf{p_{\mathrm{WS,0}}} + s_{\mathrm{disp}} \cdot (D_{x},\ \eta,\ D_{z})
$$

### 5.2 Pixel Shading and 5 Visualization Modes (`Frag`)
1. **`Realistic` (`_VisualMode == 0`)**:
   - **Schlick Fresnel**: $F = 0.02 + 0.98(1 - \mathrm{saturate}(\mathbf{N}\cdot\mathbf{V}))^{5}$
   - **Elevation + Crest Pinch Color Blend**: Interpolates between `_DeepColor` and `_BaseColor` via $f_{\mathrm{depth}} = \mathrm{saturate}(0.35\eta + 0.25 P_{\mathrm{crest}} + 0.45)$.
   - **Shallow-Water Emerald Transmittance (when `_HasBathymetry > 0.5`)**:
     Combines exponential depth transmittance $f_{\mathrm{shoal}} = \mathrm{saturate}(\exp(-0.26(h - 1.2)))$ with 2m seabed depth contours to tint shallow shoals and headlands toward emerald $(0.16, 0.62, 0.56)$.
   - **Wave Crest Subsurface Scattering (SSS)**:
     Adds turquoise subsurface scattering $\mathbf{C_{\mathrm{SSS}}} = (0.14, 0.72, 0.68) \cdot \mathbf{L_{\mathrm{color}}} \cdot P_{\mathrm{crest}}\,\mathrm{saturate}(0.45\eta + 0.35)\,(0.45 + 0.55\,\mathrm{saturate}(-\mathbf{V}\cdot\mathbf{L}\cdot 0.5 + 0.5)) \cdot 0.55$ on pinched choppy wave crests ($P_{\mathrm{crest}} = \mathrm{saturate}(1 - J) > 0$).
   - **Blinn-Phong Specular & Foam Blend**: $\mathrm{Spec} = (\mathbf{N}\cdot\mathbf{H})^{64} \cdot \mathrm{Smoothness} \cdot (0.85 F + 0.15) \cdot 2.0$, and blends `_FoamColor` by $\mathrm{Foam} = \mathrm{saturate}(\mathrm{disp.a} \cdot I_{\mathrm{foam}})$.
2. **`HeightMap` (`_VisualMode == 1`)**: Jet colormap of normalized elevation $\eta / R_{\mathrm{height}} \in [-1, 1]$ with $0.2\ \mathrm{m}$ contour lines.
3. **`NormalVisual` (`_VisualMode == 2`)**: World-space normal RGB visualization $0.5\mathbf{N} + 0.5$.
4. **`Wireframe` (`_VisualMode == 3`)**: $64\times 64$ UV grid visualizing horizontal choppy convergence and surface deformation.
5. **`Bathymetry` (`_VisualMode == 4`)**: Warm (shallow) to cool (deep) heatmap of still water depth $h \in [1\ \mathrm{m}, 15\ \mathrm{m}]$ with $1\ \mathrm{m}$ isobath contours and $+0.12\eta$ wave crest overlay.

### 5.3 3D Seabed Mesh Visualization (`SeabedRenderer.cs`, `Seabed.shader`)
Provides explicit 3D mesh rendering of the seabed bathymetry $z_{b}(x, z) = -h(x, z)$ beneath the water surface for physical and educational comprehension of wave-bottom interactions (shoaling, refraction, energy focusing):

- **GPU Vertex Displacement**: Samples bed elevation $z_{b} = -h$ directly from `_BathymetryTex` in the vertex shader, dynamically updating without CPU mesh reallocations upon parameter edits.
- **Analytical Terrain Normal**: Computes surface normals via texture gradients $\nabla(y - z_{b}) = (\partial h/\partial x,\ 1,\ \partial h/\partial z)$ for smooth directional and ambient shading.
- **Basin Skirt Walls (`includeBasinWalls`)**: Extends boundary edges down to a perimeter basin floor (default $-12\ \mathrm{m}$) to form an architectural tank slice.
- **3 Visual Modes**:
  1. `RealisticSand`: Shallow warm sand to deep shelf sediment gradient, 1m isobath contours, and procedural sand ripples.
  2. `BathymetryHeatmap`: Hypsometric depth chart coloring.
  3. `TopographicWireframe`: Mesh wireframe highlighting terrain slopes and grid resolution.
- **Water Transmittance Coupling**: Dynamically raises water surface transparency over shallow areas ($h \le 1.5\ \mathrm{m}$) to clearly reveal the underlying seabed topography through waves.

---

## 6. Performance Estimation Model (VRAM, Memory Bandwidth, Dispatch Count, and Execution Time)

Grid-based and FFT-based GPU Compute Shader water simulations are primarily **memory-bandwidth bound** and **dispatch-overhead bound** rather than arithmetic-compute bound. Consequently, performance at any resolution $N \times N$ can be accurately predicted from the analytical formulas below.

### 6.1 VRAM Footprint Formulas and Resolution Table

VRAM usage for resolution $N \times N$ ($P = N^{2}$ pixels):

1. **Water Surface Mesh (`WaterRenderer`, resolution $M \times M$, $4.50\ \mathrm{MB}$ at default $M = 256$)**:
   - Vertex buffer (`pos`: 12B, `normal`: 12B, `tangent`: 16B, `uv`: 8B = **48 Bytes/vertex**) + Index buffer (32-bit UInt, 6 indices/cell = **24 Bytes/cell**)

$$
\mathrm{VRAM_{Mesh}}(M) = 48 M^{2} + 24 (M - 1)^{2} \approx 72 M^{2}\ [\mathrm{Bytes}]
$$

2. **Method 1: 2D Wave Equation (`WaveEquationSimulation`)**:
   - `stateTexA` (8B) + `stateTexB` (8B) + `normalTex` (8B) + `waterDataTex` (8B) + `obstacleTex` (1B) = **33 Bytes/px**

$$
\mathrm{VRAM_{WaveEq}}(N) = 33 N^{2}\ [\mathrm{Bytes}]
$$

3. **Method 2: Boussinesq System (`BoussinesqSimulation`)**:
   - `stateTexA` (16B) + `stateTexB` (16B) + `predTex` (16B) + `forceOrigTex` (8B) + `forceTex` (8B) + `forcePredTex` (8B) + `accelTexA` (8B) + `accelTexB` (8B) + `normalTex` (8B) + `waterDataTex` (8B) + `bathymetryTex` (8B) = **112 Bytes/px**

$$
\mathrm{VRAM_{Boussinesq}}(N) = 112 N^{2}\ [\mathrm{Bytes}]
$$

4. **Method 3: FFT Ocean Model (`FFTOceanSimulation`)**:
   - `initialSpectrumTex` (16B) + `spectrumDyTex` (16B) + `spectrumDxDzTex` (16B) + `pingPongTexA` (16B) + `pingPongTexB` (16B) + `waterDataTex` (8B) + `normalTex` (8B) = **96 Bytes/px**

$$
\mathrm{VRAM_{FFT}}(N) = 96 N^{2}\ [\mathrm{Bytes}]
$$

| Grid Resolution $N \times N$ | Total Cells $N^{2}$ | Method 1: 2D Wave Eq | Method 2: Boussinesq | Method 3: FFT Ocean | Shared Mesh ($256^{2}$) |
| :---: | :---: | :---: | :---: | :---: | :---: |
| **$256 \times 256$** | $65,536$ | **$2.06\ \mathrm{MB}$** | **$7.34\ \mathrm{MB}$** | **$6.00\ \mathrm{MB}$** | $4.50\ \mathrm{MB}$ |
| **$512 \times 512$** | $262,144$ | **$8.25\ \mathrm{MB}$** | **$29.36\ \mathrm{MB}$** | **$24.00\ \mathrm{MB}$** | $4.50\ \mathrm{MB}$ |
| **$1024 \times 1024$** | $1,048,576$ | **$33.00\ \mathrm{MB}$** | **$117.44\ \mathrm{MB}$** | **$96.00\ \mathrm{MB}$** | $4.50\ \mathrm{MB}$ |

---

### 6.2 Per-Step Dispatch Count and Memory Bandwidth Model

Effective DRAM/L2 memory traffic per cell (Read + Write after L1/L2 spatial cache locality within $8\times 8$ thread groups) and total dispatches $D$ per step:

#### Method 1: 2D Wave Equation ($M_{\mathrm{sub}}$ substeps)
- **`CSStep`** ($\times M_{\mathrm{sub}}$): Read `_CurrentState` (8B) + `_ObstacleTex` (1B) + Write `_NextState` (8B) $\approx \mathbf{17\ \mathrm{B/px}}$ (9-point stencil neighbor reads hit L1 cache within the $8\times 8$ thread group).
- **`CSComputeNormal`** ($\times 1$): Read `_NextState` (8B) + Write `_NormalTex` (8B) + Write `_WaterDataTex` (8B) $\approx \mathbf{24\ \mathrm{B/px}}$.

$$
D_{\mathrm{WaveEq}} = M_{\mathrm{sub}} + 1, \quad \mathrm{Traffic_{WaveEq}}(N, M_{\mathrm{sub}}) = (17 M_{\mathrm{sub}} + 24) N^{2}\ [\mathrm{Bytes/step}]
$$

#### Method 2: Boussinesq System ($M_{\mathrm{sub}}$ substeps, ADI PCR direct dispersion solver)
- Per-substep operations ($\times M_{\mathrm{sub}}$):
  1. `CSPredict`: Read `_StateIn` (16B) + Read `_BathymetryTex` (8B) + Write `_ForceTex` (8B) + Write `_StatePred` (16B) = $\mathbf{48\ \mathrm{B/px}}$
  2. `CSTridiagonalX` (Predictor X-PCR): Read `_ForceTex` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPong` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  3. `CSTridiagonalZ` (Predictor Z-PCR): Read `_AccelPong` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPing` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  4. `CSApplyPredictedDispersion`: R/W `_StatePred` (32B) + Read `_AccelPing` (8B) + Write `_ForcePredTex` (8B) = $\mathbf{48\ \mathrm{B/px}}$
  5. `CSCorrect`: Read `_StateIn` (16B) + Read `_StatePred` (16B) + Read `_BathymetryTex` (8B) + Write `_ForceTex` (8B) + Write `_StateOut` (16B) = $\mathbf{64\ \mathrm{B/px}}$
  6. `CSTridiagonalX` (Corrector X-PCR): Read `_ForceTex` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPong` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  7. `CSTridiagonalZ` (Corrector Z-PCR): Read `_AccelPong` (8B) + Read `_BathymetryTex` (8B) + Write `_AccelPing` (8B) = $\mathbf{24\ \mathrm{B/px}}$
  8. `CSApplyDispersion`: R/W `_StateOut` (32B) + Read `_ForcePredTex` (8B) + Read `_AccelPing` (8B) = $\mathbf{48\ \mathrm{B/px}}$
- Step completion ($\times 1$): `CSComputeNormal`: Read `_StateOut` (16B) + Write `_NormalTex` (8B) + Write `_WaterDataTex` (8B) = $\mathbf{32\ \mathrm{B/px}}$

$$
D_{\mathrm{Boussinesq}} = 8 M_{\mathrm{sub}} + 1
$$

$$
\mathrm{Traffic_{Boussinesq}}(N, M_{\mathrm{sub}}) = (304 M_{\mathrm{sub}} + 32) N^{2}\ [\mathrm{Bytes/step}]
$$

#### Method 3: Spectral / FFT Ocean (single complex-packed 2D IFFT per step, $S = \log_{2} N$ stages)
- **`CSTimeEvolution`** ($\times 1$): Read `_InitialSpectrum` (16B) + Write `_SpectrumPacked` (16B) = $\mathbf{32\ \mathrm{B/px}}$
- **`Perform2DIFFT`** ($\times 1$: $2\log_{2} N$ butterfly passes, zero `Graphics.Blit` passes):
  - Each butterfly pass (`CSFFTHorizontal` / `CSFFTVertical`): Read `_InputPing` (2 points $\to$ 24B effective with cache) + Write `_OutputPong` (16B) $\approx \mathbf{40\ \mathrm{B/px}}$
  - Per 2D IFFT: $(40 \times 2\log_{2} N)\ \mathrm{B/px} = (80\log_{2} N)\ \mathrm{B/px}$
- **`CSPackOceanData`** ($\times 1$): Read `_SpectrumPacked` (16B) + Write `_WaterDataTex` (8B) + Write `_NormalTex` (8B) = $\mathbf{32\ \mathrm{B/px}}$

$$
D_{\mathrm{FFT}} = 2 \log_{2} N + 2, \quad \mathrm{Traffic_{FFT}}(N) = (80 \log_{2} N + 64) N^{2}\ [\mathrm{Bytes/step}]
$$

(Note: Redundant spectrum regenerations are guarded by dirty flags and value equality checks; steady-state simulations have `RedundantSpectrumRegen = 0`.)

---

### 6.3 GPU Execution Time Formula and Measured Benchmark Verification

Given effective GPU memory bandwidth $B_{\mathrm{eff}}\ [\mathrm{GB/s}]$ and per-dispatch driver/barrier overhead $t_{\mathrm{disp}}\ [\mathrm{ms}]$ (typically $0.004 \sim 0.008\ \mathrm{ms}$ on D3D11/D3D12), the GPU execution time per simulation step $T_{\mathrm{GPU}}$ is estimated by:

$$
T_{\mathrm{GPU}} \approx \frac{\mathrm{Traffic}}{B_{\mathrm{eff}}} + D_{\mathrm{step}} \cdot t_{\mathrm{disp}}
$$

#### Measured Benchmark Results (Unity 6000.3.27f1 Batchmode Execution, extracted from `Docs/BenchmarkResults.csv`)

| Category | Method | Resolution | Mode / Scheme | Substeps | Dispatches | Wall Time per Step | Notes |
| :--- | :--- | :---: | :--- | :---: | :---: | :---: | :--- |
| **SolverOnly** | 01 2D Wave Equation | $256 \times 256$ | RGFloat Symplectic | $2$ | $3$ | **$0.183\ \mathrm{ms}$** | $c=8\ \mathrm{m/s}, L=50\ \mathrm{m}$ |
| **SolverOnly** | 01 2D Wave Equation | $512 \times 512$ | RGFloat Symplectic | $3$ | $4$ | **$0.298\ \mathrm{ms}$** | $c=8\ \mathrm{m/s}, L=50\ \mathrm{m}$ |
| **SolverOnly** | 02 Boussinesq | $256 \times 256$ | Peregrine ADI PCR | $2$ | $17$ | **$1.055\ \mathrm{ms}$** | $\alpha=1.0, h=3.5\ \mathrm{m}$ |
| **SolverOnly** | 02 Boussinesq | $512 \times 512$ | Peregrine ADI PCR | $4$ | $33$ | **$4.544\ \mathrm{ms}$** | $\alpha=1.0, h=3.5\ \mathrm{m}$ |
| **SolverOnly** | 03 FFT Ocean | $256 \times 256$ | Single-Pass Packed IFFT | $1$ | $18$ | **$0.317\ \mathrm{ms}$** | Redundant regen $0$ |
| **SolverOnly** | 03 FFT Ocean | $512 \times 512$ | Single-Pass Packed IFFT | $1$ | $20$ | **$0.869\ \mathrm{ms}$** | Redundant regen $0$ |

#### Composite Scene 04 Breakdown (512×512 Dual Simulation: Wave Equation + Boussinesq)

| Mode | Measured Target | Wall Time per Step | Breakdown Insights |
| :--- | :--- | :---: | :--- |
| **1_SolverOnly** | WaveEq + Boussinesq (each 512²) | **$12.73\ \mathrm{ms}$** | Pure GPU compute shader solver time for both high-resolution grids |
| **2_SolverPlusRender** | Solvers + Camera Render (1280×720) | **$8.30\ \mathrm{ms}$** | URP render pass executing concurrently with vertex displacement |
| **3_SolverRenderProfileAsync** | Solvers + Render + Profile Readback | **$11.80\ \mathrm{ms}$** | `AsyncGPUReadback` ring-buffered transfer with CPU-cached static bathymetry |

> [!TIP]
> **Resolution Scaling Laws**:
> - **Doubling grid resolution along each axis ($2\times$, e.g., $256^{2} \to 512^{2}$)**:
>   - For the **2D Wave Equation** and **Boussinesq** models, total cells $N^{2}$ increase by $4\times$ while the CFL limit $\Delta t \propto \Delta x = L/N$ doubles the required substep count $M_{\mathrm{sub}}$ ($2\times$) for the same domain size $L$, scaling computational cost per unit physical time by **$4 \times 2 = 8\times$** (stepping up whenever $\Delta t_{\max}$ crosses an integer divisor of `fixedTimeStep`).
>   - For the **FFT Ocean** model, there is no CFL restriction; doubling $N$ leaves the time step unchanged and increases compute cost by only **$4 \frac{\log_{2} N + 1}{\log_{2} N} \approx 4.4\times$**.

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
| **02_Boussinesq** | `Sweeps (ADI PCR)` | `BoussinesqSimulation.DispersionSweeps` (1 sweep direct solve) |
| **03_FFTWater** | `Sea State`: `Calm`, `Swell`, `Stormy` | `FFTOceanSimulation.ApplySeaStatePreset` |
| **03_FFTWater** | `Wind`, `Wind Dir`, `Choppy`, `Swell`, `Swell Dir`, `Foam` | `WindSpeed`, `WindDirectionDegrees`, `Choppiness`, `SwellStrength`, `SwellDirectionDegrees`, `EnableFoam` |
| **04_Comparison** | `Profile Scale (断面高さ強調)` / `ProfileH` | `WaterComparisonController.ProfileVerticalScale` $\to$ `WaterProfileViewer.VerticalScale` |
| **04_Comparison** | `Profile Viewer Mode` | `WaterProfileViewer` (`AsyncGPUReadback` & CPU-cached bathymetry) |
