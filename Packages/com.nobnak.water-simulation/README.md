**English | [日本語](README.ja.md)**

# Unity Water Simulation UPM Package

A comprehensive Unity Universal Render Pipeline (URP) UPM package providing 3 independent water simulation methods:
1. **2D Linear Wave Equation** (Interactive ripples, obstacle reflection, diffraction & interference)
2. **Boussinesq System** (Depth-dependent coastal waves, shoaling, refraction, frequency dispersion)
3. **Spectral / FFT Ocean Surface** (Horizon-scale ocean waves, swell & wind-sea superposition, choppy trochoidal crests, Jacobian foam)

Designed to let developers, technical artists, and researchers compare visual fidelity, runtime performance, and interactive capabilities to choose the right method for their project.

## Video Demos

Demonstration videos for each sample scene (click thumbnail to watch on YouTube):

| Scene 01: 2D Wave Equation | Scene 02: Boussinesq Shallow Water | Scene 03: Spectral / FFT Ocean |
| :---: | :---: | :---: |
| [![Wave Equation Demo](https://img.youtube.com/vi/s7Eb2UlgJVc/maxresdefault.jpg)](https://youtu.be/s7Eb2UlgJVc) | [![Boussinesq Demo](https://img.youtube.com/vi/1DWCYRkK0RM/maxresdefault.jpg)](https://youtu.be/1DWCYRkK0RM) | [![FFT Ocean Demo](https://img.youtube.com/vi/j2twZkyc68E/maxresdefault.jpg)](https://youtu.be/j2twZkyc68E) |
| [Wave Equation (YouTube)](https://youtu.be/s7Eb2UlgJVc)<br>Diffraction, interference & ripples | [Boussinesq (YouTube)](https://youtu.be/1DWCYRkK0RM)<br>Refraction, shoaling & caustic focusing | [FFT Ocean (YouTube)](https://youtu.be/j2twZkyc68E)<br>Swell, wind-sea & choppy crests |

> [!TIP]
> - **Complete Algorithm, Numerical Methods, and Performance Estimation Specification**: [Docs/WaterSimulation_Algorithm_And_Performance_Spec.md](../../Docs/WaterSimulation_Algorithm_And_Performance_Spec.md)
> - **Comprehensive Technical Investigation & Discussion Archive**: [Docs/WaterSimulation_Technical_Archive.md](../../Docs/WaterSimulation_Technical_Archive.md)

---

## 1. Target Environment & Requirements

| Specification | Target / Verified Environment |
| :--- | :--- |
| **Unity Version** | Unity 6 (6000.3.27f1+) |
| **Render Pipeline** | Universal Render Pipeline (URP 17.3.0+) |
| **Graphics API** | Direct3D 12, Direct3D 11, Metal, Vulkan (Compute Shader 5.0+ required) |
| **Tested Hardware** | Intel(R) Arc(TM) 140V GPU (16 GB VRAM), Windows 11 |
| **Dependencies** | `com.unity.render-pipelines.universal`, `com.unity.mathematics` (1.3.2) |

---

## 2. Common Architecture (Task 1)

All three methods operate completely independently with no shared runtime coupling, yet share a standardized data format and visualization pipeline.

### 2.1 Coordinate System, Units, and Still Water Level
- **Coordinate System**: Unity World Space (X: East/Right, Y: Up/Normal, Z: North/Forward).
- **Units**: Standard SI units (Lengths, depths, amplitudes: meters [m]; Time: seconds [s]; Wave speed and wind velocity: [m/s]).
- **Reference Water Level**: Still water level $\eta = 0$ is aligned with GameObject origin $Y = 0$. Seabed elevation is defined as $z_{b} = -h(\mathbf{x}) \le 0$ for positive water depth $h(\mathbf{x}) > 0$.

### 2.2 Texture Standards (`IWaterSimulation`)
- **WaterDataTexture (`_WaterDataTex`)** (`ARGBHalf`):
  - **R**: Horizontal X displacement $D_{x}$ [m] (FFT choppy waves)
  - **G**: Vertical surface elevation $\eta$ [m]
  - **B**: Horizontal Z displacement $D_{z}$ [m] (FFT choppy waves)
  - **A**: Whitecap / foam intensity $[0, 1]$ (Jacobian compression or steep slope dissipation)
- **NormalTexture (`_NormalTex`)** (`ARGBHalf`): World-space surface normal vector (RGB) + crest pinch / foam intensity (A).
- **BathymetryTexture (`_BathymetryTex`)** (`ARGBHalf`): Still water depth $h$ [m] (R) + seabed elevation $-h$ [m] (G).

### 2.3 Rendering & Visualization Modes
- **Universal PBR Water Surface Shader (`WaterSurfaceCommon.shader`)**:
  - Full 3D vertex displacement (horizontal choppiness + vertical height).
  - Schlick Fresnel reflection, depth gradient color blending, shallow-water emerald transmittance + depth contours (coupled with Bathymetry), wave crest subsurface scattering (Crest SSS), directional specular highlights, and foam blending.
- **Visual Debug Modes (`WaterRenderer.cs` via `MaterialPropertyBlock`)**:
  - `0`: Realistic (Photorealistic PBR)
  - `1`: HeightMap (Contour lines + elevation colormap)
  - `2`: NormalVisual (World-space normal vector visualization)
  - `3`: Wireframe (Dynamic displacement grid)
  - `4`: Bathymetry (Water depth heatmap + 1m isobath contours + wave crest overlay)
- **1D Cross-Section Profile Slicer (`WaterProfileViewer.cs`)**:
  - Slices through the domain along X or Z to directly inspect 1D surface waveforms $\eta$ and seabed topography $z_{b}$.

---

## 3. Simulation Methods & Mathematical Foundations

### Method 1: 2D Linear Wave Equation (`WaveEquationSimulation`) - Task 2
High-speed, low-overhead interactive ripple simulation.

- **Governing Equation**:

$$
\frac{\partial^{2} \eta}{\partial t^{2}} = c^{2} \nabla^{2} \eta - \gamma \frac{\partial \eta}{\partial t}
$$

  - $\eta$: Surface elevation [m]
  - $c$: Wave phase speed [m/s]
  - $\gamma$: Damping rate [1/s]
- **Numerical Scheme**: 9-point isotropic Laplacian FDM + `fp32` (`RGFloat`) symplectic Euler (kick-drift) time-stepping with centered half-step Cayley damping. Simultaneous physical state tracking $(\eta, v = \partial_{t}\eta)$ exhibits 1st-order convergence $O(\Delta t)$ ($2.01\times$ reduction per halving of $\Delta t$), preserving continuity across variable $\Delta t$ and wave speed $c$ without artificial time-step discontinuities. The fp32 upgrade reduces 10-second cumulative truncation drift for long waves down to $0.00008\%$.
- **Key Features**:
  - **CFL Auto-Substepping**: Automatically subdivides time steps to satisfy $\Delta t \le 0.95 \frac{\Delta x}{\sqrt{2}\,c}$, ensuring unconditional numerical stability.
  - **Sponge Absorbing Perimeter**: Suppresses boundary reflections smoothly.
  - **Obstacle Masks**: Single slit, double slit, circular pillars, or custom obstacle mask texture (Dirichlet or Neumann reflection).
  - **Excitation Modes**: Compact $C^{2}$ polynomial bell impulse (eliminating high-frequency Gibbs ringing), periodic pulse oscillator, moving wave source, interactive mouse clicking.
  - **Physical Normal Decoupling**: Displays true unit physical normals in `NormalVisual` mode, applying visual normal exaggeration only within the realistic shading pass.
- **Representative Scene**: `01_WaveEquation` (Ripples and Obstacles)
  - Demonstrates circular ripples passing through a double-slit barrier with clear diffraction, interference fringes, and wall reflection.
- **Scope & Limitations**:
  - Assumes linear waves with uniform phase speed; does not model depth-dependent refraction or frequency dispersion.

---

### Method 2: Boussinesq System (`BoussinesqSimulation`) - Task 3
Coastal shallow-water wave model capturing depth-dependent wave refraction, shoaling, and frequency dispersion.

- **Governing Equations (Peregrine 1967 Weakly Nonlinear, Weakly Dispersive System)**:
  - Continuity:

$$
\frac{\partial \eta}{\partial t} + \nabla \cdot ((h + \eta)\mathbf{u}) = S_{\eta}
$$

  - Momentum:

$$
\left(I - \alpha \frac{h^{2}}{3} \nabla^{2}\right)\frac{\partial \mathbf{u}}{\partial t} = -g \nabla \eta - (\mathbf{u} \cdot \nabla)\mathbf{u} - \frac{c_{f}}{h + \eta}\mathbf{u} + \mathbf{S_{u}}
$$

  - $h(\mathbf{x})$: Still water depth [m]
  - $\mathbf{u}$: Depth-averaged horizontal velocity vector [m/s]
  - $c_{f}$: Seabed friction coefficient
  - Decoupled-component Helmholtz-type Peregrine (1967) $1/3$ dispersion model with phase velocity error at $kh=1$ of only $-0.764\%$ (velocity cross-derivatives $\partial_{xz}a_z$ and intra-operator bathymetric gradients are omitted under irrotational mild-slope wave propagation assumptions).
- **Numerical Stabilization (`fp32` Symmetric MacCormack + Direction-Split ADI PCR Dispersion Solver & Defect Correction)**:
  - Explicit evaluation of the 3rd-order spatial derivative $\nabla(\nabla^{2} \eta)$ suffers from severe stiffness ($\propto \Delta x^{-3}$), requiring $\Delta t < 10^{-6}\ \mathrm{s}$ and causing explosive checkerboard spikes.
  - The 2D Peregrine dispersion operator is factored into an Alternating Direction Implicit (Peaceman-Rachford ADI) system solved directly in `groupshared` memory via tridiagonal Parallel Cyclic Reduction (PCR).
  - An iterative Defect Correction loop (`CSDefectCorrection`, `dispersionSweeps`) eliminates the 4th-order directional-splitting artifact $\beta_x \beta_z \partial_x^2 \partial_z^2 \mathbf{a}$ with geometric convergence ($\approx 49\times$ reduction per sweep; reducing $45^\circ$ oblique phase speed error from $-1.03\%$ at Sweep 1 to $-0.021\%$ at Sweep 2 and $-0.0004\%$ at Sweep 3).
  - Coupled with MacCormack-Heun predictor-corrector time stepping, CFL automatic sub-stepping ($\Delta t \le 0.55 \frac{\Delta x}{\sqrt{2(c + u)}}$), a $\Delta t$-scaled 4th-order conservative biharmonic filter, and post-filter lateral solid wall velocity zeroing ($u \equiv 0$).
- **Key Features**:
  - **Procedural Bathymetry**: Shoal and Cape, Flat bed, Linear Slope, or custom bathymetry texture.
  - **Rich Wave Excitation & Soliton Modes**: Offshore progressive wavemaker plus planar solitary waves (Planar Soliton: ${\mathrm{sech}}^{2}$) and circular Gaussian pulses (Circular Pulse) across single-shot, periodic, moving source, and interactive mouse click/drag modes via non-destructive GPU injection (`CSAddImpulse`).
  - **Dispersion Control**: Continuous slider $\alpha \in [0, 1.2]$ ($\alpha = 1.0$: Boussinesq dispersive waves, $\alpha = 0.0$: non-dispersive shallow water).
- **Representative Scene**: `02_Boussinesq` (Shoal and Cape)
  - Offshore wave trains enter shallow water (15m to 2.5m), decelerating ($c \sim \sqrt{gh}$), refracting around a diagonal shoal, shortening in wavelength (shoaling), and focusing wave energy onto a protruding headland/cape.
- **Scope & Limitations**:
  - As a 2D depth-averaged model ($h/\lambda < 0.2$), it does not simulate 3D overturning plunging breakers or detached spray particles (breaking is represented via steep-slope dissipation and surface foam).

---

### Method 3: Spectral / FFT Ocean Surface (`FFTOceanSimulation`) - Task 4
Deep-ocean statistical spectral model for vast open-water expanses where wind waves and distant swell coexist.

- **Statistical Ocean Wave Spectrum (Tessendorf 2001 / Phillips Spectrum) & Normalization**:

$$
P_{h}(\mathbf{k}) = A \frac{\exp(-1 / (k L)^{2})}{k^{4}} (\hat{\mathbf{k}} \cdot \hat{\mathbf{w}})^{2} \exp(-k^{2} l_{0}^{2})
$$

  - Exact Hermitian conjugate symmetry enforced via periodic negative-wavenumber mapping $i_{-} = (N - i) \bmod N, j_{-} = (N - j) \bmod N$, dropping imaginary post-IFFT residual to $3.45 \times 10^{-16}$ (machine precision).
  - Discrete amplitude scaling by $\Delta k = \frac{2\pi}{L}$ guarantees resolution-independent significant wave height ($H_{s} \approx 3.63\ \mathrm{m}$ across $N = 64, 128, 256, 512$).
- **Complex-Packed Single 2D IFFT Pass**:
  - Packs $(\tilde{D}_{y} + i\tilde{D}_{x})$ into `RG` and $\tilde{D}_{z}$ into `BA`, evaluating all 3 displacement fields in a single 4-channel 2D IFFT with zero `Graphics.Blit` passes ($2\log_{2} N + 2$ dispatches per step).
  - Setter equality checks and dirty flags eliminate redundant spectrum regeneration during steady simulation (`RedundantSpectrumRegen = 0`).
- **Wavenumber-Weighted Choppy Waves and Exact Lagrangian Normals**:

$$
\mathbf{X}(\mathbf{x}, t) = \mathbf{x} + \lambda \sum_{\mathbf{k}} i\,w_{\mathrm{chop}}(k) \frac{\mathbf{k}}{k} \tilde{h}(\mathbf{k}, t) e^{i \mathbf{k} \cdot \mathbf{x}}
$$

  - Converges horizontal particles at wave crests ($J < 1$) and expands troughs into sharp trochoidal wave profiles.
  - Computes the exact Jacobian-coupled tangent cross-product normal $\mathbf{N} = \mathrm{normalize}(\mathbf{T_{z}} \times \mathbf{T_{x}})$ and crest-pinch subsurface scattering ($1 - J$).
- **Jacobian Whitecap Foam**:
  - Automatically detects breaking wave crests where the horizontal Jacobian determinant $J = (1 + \partial_{x} D_{x})(1 + \partial_{z} D_{z}) - \partial_{z} D_{x} \partial_{x} D_{z}$ falls below the threshold.
- **Representative Scene**: `03_FFTWater` (Horizon Ocean)
  - Demonstrates coexistence of long-wavelength swell and local wind waves, sharp choppy crests with whitecaps, and stable horizon-scale rendering under both stationary and free-flying cameras.
- **Scope & Limitations**:
  - Because it operates in the periodic frequency domain, it does not model local boundary reflections or diffraction around coastlines and obstacles.

---

## 4. Sample Scenes Overview (Task 5)

Five sample scenes are included in the package:

| Scene | Simulation Model | What to Observe | Controls & Parameters | Scope & Limitations |
| :--- | :--- | :--- | :--- | :--- |
| **01_WaveEquation** | 2D Linear Wave Equation | Immediate ripple response, double-slit diffraction & interference, non-reflective sponge boundary | Obstacle patterns, wave speed & damping, pulse interval, impulse / periodic / moving / mouse modes | Non-dispersive; uniform wave speed; no depth refraction |
| **02_Boussinesq** | Boussinesq Shallow Water | Wave refraction over a diagonal shoal, wavelength shortening (shoaling), energy focusing on a cape | Bathymetry (Flat/Shoal/Slope), source mode (Wavemaker/Impulse/Periodic/Moving/Mouse), shape (Planar Soliton/Circular), dispersion $\alpha$, amplitude, frequency, interval | 2D depth-averaged; no 3D overturning plunging breakers or detached spray |
| **03_FFTWater** | Spectral / FFT Ocean | Vast ocean surface, superposition of distant swell and local wind waves, choppy crests, whitecaps | Sea state presets (Calm/Swell/Stormy), wind speed & direction, choppiness, swell strength & direction, foam toggle | Periodic statistical model; no local obstacle reflection or shoreline interaction |
| **04_Comparison_Dispersion** | Wave Eq vs. Boussinesq | Propagation from identical pulses over constant 5m depth: left (non-dispersive) stays a single ring; right (dispersive) develops a trailing wave train | Play/pause/step/reset, time scale, 1D cross-section profile vertical scale (Profile Scale) | Constant depth to isolate frequency dispersion physics |
| **05_Comparison_Overview** | 3-in-1 Side-by-Side | Side-by-side comparison of waveforms, visual characteristics, and behavior of all 3 methods | Master play/pause/step/reset, time scale, global visual mode switcher, direct scene transitions | Each method operates under its own physical regime |

---

## 5. Performance Benchmarks

Measured on Intel(R) Arc(TM) 140V GPU (16 GB VRAM), Windows 11, Unity 6 (6000.3.27f1) batchmode execution (from `Docs/BenchmarkResults.csv`):

| Simulation Method | Grid Resolution | Numerical Mode / Scheme | Substeps | Dispatches | Wall Time per Step |
| :--- | :---: | :--- | :---: | :---: | :---: |
| **2D Linear Wave Equation** | $256 \times 256$ | RGFloat Symplectic | $2$ | $3$ | **$0.183\ \mathrm{ms}$** |
| **2D Linear Wave Equation** | $512 \times 512$ | RGFloat Symplectic | $3$ | $4$ | **$0.298\ \mathrm{ms}$** |
| **Boussinesq System** | $256 \times 256$ | Peregrine ADI PCR (2nd-order) | $2$ | $17$ | **$1.055\ \mathrm{ms}$** |
| **Boussinesq System** | $512 \times 512$ | Peregrine ADI PCR (2nd-order) | $4$ | $33$ | **$4.544\ \mathrm{ms}$** |
| **Spectral / FFT Ocean** | $256 \times 256$ | Single-Pass Packed IFFT | $1$ | $18$ | **$0.317\ \mathrm{ms}$** |
| **Spectral / FFT Ocean** | $512 \times 512$ | Single-Pass Packed IFFT | $1$ | $20$ | **$0.869\ \mathrm{ms}$** |

#### Composite Scene 04 Breakdown (512×512 Dual Simulation)
- **1_SolverOnly** (WaveEq + Boussinesq each 512²): **$12.73\ \mathrm{ms}$**
- **2_SolverPlusRender** (Solvers + 1280×720 camera rendering): **$8.30\ \mathrm{ms}$**
- **3_SolverRenderProfileAsync** (Solvers + Render + `AsyncGPUReadback` Profile): **$11.80\ \mathrm{ms}$**

> **Note**: Simulation time stepping is decoupled from rendering framerate so integration accuracy is preserved even under heavy rendering load. Cross-section profile display uses `AsyncGPUReadback` and cached static bathymetry to eliminate CPU-GPU pipeline bubbles.

---

## 6. Getting Started

### 6.1 Installation via UPM
1. In Unity Editor, open `Window > Package Manager`.
2. Click the `+` button in the top-left corner and choose `Add package from git URL...` or `Add package from disk...`.
3. For a local checkout, select `Packages/com.nobnak.water-simulation/package.json`.

### 6.2 Importing Sample Scenes
In the Package Manager details view for `Water Simulation`, expand the `Samples` tab and click `Import` next to any of the 5 sample scenes to copy them into `Assets/Samples/Water Simulation/...`.

### 6.3 Adding Water to a Custom Scene
1. From the menu bar, choose `GameObject > 3D Object > Water Simulation` and select:
   - `2D Wave Equation Water`
   - `Boussinesq Water`
   - `FFT Ocean Water`
2. Unity automatically creates a configured GameObject with the simulation component, dedicated compute shader, water mesh renderer, and PBR material.
3. Adjust domain size, grid resolution, wave speed, or sea state presets in the Inspector.

---

## 7. Out-of-Scope Items (Specification Boundaries)
- **Two-Way Coupling Between Methods**: Each simulation method is designed for independent operation and side-by-side comparison; two-way boundary coupling between offshore FFT ocean and coastal Boussinesq grids is not included.
- **3D Overturning Breakers and Particle Spray**: Because the models are 2D depth-averaged (Boussinesq) and spectral (FFT), 3D fluid-particle overturning (plunging breakers) and airborne spray are not simulated (wave breaking is modeled via steep-slope energy dissipation and surface foam textures).
