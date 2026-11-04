**English | [日本語](README.ja.md)**

# unity-water-simulation

A Unity UPM package and comparison framework providing 3 independent water surface simulation methods (2D Linear Wave Equation, Boussinesq System, and Spectral / FFT Ocean Surface).

The UPM package is located at [`Packages/com.nobnak.water-simulation`](Packages/com.nobnak.water-simulation).

## Video Demos

Demonstration videos for each sample scene (click thumbnail to watch on YouTube):

| Scene 01: 2D Wave Equation | Scene 02: Boussinesq Shallow Water | Scene 03: Spectral / FFT Ocean |
| :---: | :---: | :---: |
| [![Wave Equation Demo](https://img.youtube.com/vi/s7Eb2UlgJVc/maxresdefault.jpg)](https://youtu.be/s7Eb2UlgJVc) | [![Boussinesq Demo](https://img.youtube.com/vi/QmVq1ucf09M/maxresdefault.jpg)](https://youtu.be/QmVq1ucf09M) | [![FFT Ocean Demo](https://img.youtube.com/vi/j2twZkyc68E/maxresdefault.jpg)](https://youtu.be/j2twZkyc68E) |
| [Wave Equation (YouTube)](https://youtu.be/s7Eb2UlgJVc)<br>Diffraction, interference & ripples | [Boussinesq (YouTube)](https://youtu.be/QmVq1ucf09M)<br>Refraction, shoaling & caustic focusing | [FFT Ocean (YouTube)](https://youtu.be/j2twZkyc68E)<br>Swell, wind-sea & choppy crests |

## Key Features
1. **2D Linear Wave Equation (`WaveEquationSimulation`)**:
   - Lightweight, high-responsiveness ripple simulation using a 9-point isotropic Laplacian stencil and `fp32` (`RGFloat`) symplectic Euler (kick-drift) time-stepping with centered half-step Cayley damping.
   - Simultaneous physical state tracking of $(\eta, v = \partial_{t}\eta)$ exhibits 1st-order convergence $O(\Delta t)$ ($2.01\times$ reduction per halving of $\Delta t$), while eliminating artificial energy jumps upon dynamic wave speed or time-step changes and reducing long-wave truncation drift down to $0.00008\%$.
   - Double-slit diffraction and interference, obstacle reflection (Dirichlet / Neumann), non-reflective perimeter sponge layer, smooth compact $C^{2}$ polynomial bell impulse (eliminating Gibbs ringing), periodic pulse, moving source, and interactive mouse-click excitation.
2. **Boussinesq Shallow-Water Model (`BoussinesqSimulation`)**:
   - Weakly nonlinear, weakly dispersive shallow-water model based on an irrotational, mild-slope, decoupled-component Helmholtz-type Peregrine (1967) formulation $\left(I - \alpha \frac{h^{2}}{3}\nabla^{2}\right)\mathbf{a} = \mathbf{f}$ ($1/3$ dispersion coefficient; velocity cross-derivatives $\partial_{xz}a_z$ and intra-dispersion bathymetric gradients are omitted under irrotational surface gravity wave assumptions).
   - Solved via a direction-split Peaceman-Rachford Alternating Direction Implicit (ADI) scheme with tridiagonal Parallel Cyclic Reduction (PCR) in `groupshared` memory, coupled with a Defect Correction loop (`CSDefectCorrection`, `dispersionSweeps`) that eliminates the 4th-order ADI splitting cross-term $\beta_x \beta_z \partial_x^2 \partial_z^2 \mathbf{a}$ ($49\times$ reduction per sweep; $45^\circ$ phase velocity error reduced from $-1.03\%$ at Sweep 1 down to $-0.021\%$ at Sweep 2 and $-0.0004\%$ at Sweep 3).
   - Symmetric predictor-corrector MacCormack-Heun velocity coupling with automatic CFL sub-stepping ($\Delta t \le 0.55 \frac{\Delta x}{\sqrt{2(c + u)}}$), 4th-order conservative biharmonic spatial filter with $\Delta t$-scaled dissipation, and strict impermeable lateral solid-wall velocity projection ($u \equiv 0$, flux $Hu \equiv 0$).
   - Depth-dependent wave speed ($c \sim \sqrt{gh}$), wave refraction over diagonal shoals, wavelength shortening (shoaling), and wave energy focusing on protruding headlands/capes.
   - Supports continuous offshore progressive wavemaking as well as planar solitary waves (Planar Soliton) and circular Gaussian pulses across single-shot, periodic, moving source, and interactive mouse injection modes.
3. **Spectral / FFT Ocean Surface (`FFTOceanSimulation`)**:
   - Superposition of Tessendorf / Phillips wind-sea spectrum and independent long-wavelength directional swell.
   - Periodic negative-wavenumber mapping enforces strict Hermitian conjugate symmetry (imaginary residual ratio $3.45 \times 10^{-16}$) and discrete wavenumber scaling $\Delta k = \frac{2\pi}{L}$ guarantees resolution-independent significant wave height ($H_{s} \approx 3.63\ \mathrm{m}$).
   - Transforms all 3 displacement components $(D_{y}, D_{x}, D_{z})$ simultaneously in a single 4-channel 2D IFFT pass with zero `Graphics.Blit` passes ($2\log_{2} N + 2$ dispatches). Setter equality guards eliminate redundant spectrum regenerations.
   - Wavenumber-weighted choppy horizontal displacement (trochoidal wave crests), exact Jacobian-coupled Lagrangian normals, wave crest subsurface scattering (SSS), and Jacobian whitecap foam detection.
4. **Common Rendering & Comparison Framework**:
   - Universal PBR water surface shader (3D vertex displacement, Schlick Fresnel, depth gradient, shallow-water emerald transmittance + depth contours, crest SSS, and whitecap foam).
   - 5 visual debug modes (`Realistic`, `HeightMap`, `Normal`, `Wireframe`, `Bathymetry`). `NormalVisual` mode displays the raw unit physical normal.
   - 1D cross-section profile viewer with zero GPU stalls via `AsyncGPUReadback` and CPU-cached bathymetry.
   - Interactive HUD (observation guides, parameter controls, scope notes, and synchronized multi-simulation playback).

## Target Environment
- **Unity**: Unity 6 (6000.3.27f1+)
- **Render Pipeline**: Universal Render Pipeline (URP 17.3.0+)
- **Graphics API**: Direct3D 12 / 11 / Metal / Vulkan (Compute Shader 5.0+ required)
- **Dependencies**: `com.unity.render-pipelines.universal`, `com.unity.mathematics` (1.3.2)

## Documentation
- **[Package Documentation (English)](Packages/com.nobnak.water-simulation/README.md)**: Installation guide, 5 sample scene guides, mathematical overview, and performance benchmarks.
- **[Complete Algorithm, Numerical Methods, and Performance Estimation Specification](Docs/WaterSimulation_Algorithm_And_Performance_Spec.md)**: Governing PDEs, discretization stencils, GPU texture layouts, and analytical VRAM / memory bandwidth / GPU execution time estimation models (written to enable full re-implementation and performance prediction from the document alone).
- **[Comprehensive Technical Investigation & Discussion Archive](Docs/WaterSimulation_Technical_Archive.md)**: Commit history, numerical blow-up root-cause analysis and stabilization, design decisions, and future extension roadmap.
