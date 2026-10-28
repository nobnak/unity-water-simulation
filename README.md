**English | [日本語](README.ja.md)**

# unity-water-simulation

A Unity UPM package and comparison framework providing 3 independent water surface simulation methods (2D Linear Wave Equation, Boussinesq System, and Spectral / FFT Ocean Surface).

The UPM package is located at [`Packages/com.nobnak.water-simulation`](Packages/com.nobnak.water-simulation).

## Key Features
1. **2D Linear Wave Equation (`WaveEquationSimulation`)**:
   - Lightweight, high-responsiveness ripple simulation using a 9-point isotropic Laplacian stencil and `fp32` (`RGFloat`) symplectic Leapfrog time-stepping.
   - Physical state $(\eta, v = \partial_{t}\eta)$ tracking eliminates discontinuities upon dynamic wave speed or time-step changes and reduces 10s long-wave truncation error down to $0.00008\%$.
   - Double-slit diffraction and interference, obstacle reflection (Dirichlet / Neumann), non-reflective perimeter sponge layer, smooth compact $C^{2}$ polynomial bell impulse (eliminating Gibbs ringing), periodic pulse, moving source, and interactive mouse-click excitation.
2. **Boussinesq Shallow-Water Model (`BoussinesqSimulation`)**:
   - Standard Peregrine (1967) weakly nonlinear, weakly dispersive shallow-water equations ($1/3$ dispersion coefficient, phase speed error $-0.764\%$).
   - Completely removes artificial dispersion coefficient bounds ($\beta \le 6$), factoring the 2D Peregrine operator into an unconditionally stable Alternating Direction Implicit (Peaceman-Rachford ADI) system solved directly in `groupshared` memory via Parallel Cyclic Reduction (PCR).
   - Second-order Heun velocity update couples dispersion symmetrically across predictor and corrector stages, achieving true $O(\Delta t^{2})$ convergence ($3.99\times$ error reduction upon halving $\Delta t$).
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
