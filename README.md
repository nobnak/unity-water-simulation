**English | [日本語](README.ja.md)**

# unity-water-simulation

A Unity UPM package and comparison framework providing 3 independent water surface simulation methods (2D Linear Wave Equation, Boussinesq System, and Spectral / FFT Ocean Surface).

The UPM package is located at [`Packages/com.nobnak.water-simulation`](Packages/com.nobnak.water-simulation).

## Key Features
1. **2D Linear Wave Equation (`WaveEquationSimulation`)**:
   - Lightweight, high-responsiveness ripple simulation using a 9-point isotropic Laplacian stencil.
   - Double-slit diffraction and interference, obstacle reflection, non-reflective perimeter sponge layer, and single-shot, periodic pulse, moving source, and interactive mouse-click excitation.
2. **Boussinesq Shallow-Water Model (`BoussinesqSimulation`)**:
   - Peregrine weakly nonlinear, weakly dispersive shallow-water equations.
   - Unconditionally stable numerical integration via `fp32` Symmetric MacCormack Predictor-Corrector and a 6-pass Jacobi Helmholtz dispersion inverter.
   - Depth-dependent wave speed ($c \sim \sqrt{gh}$), wave refraction over diagonal shoals, wavelength shortening (shoaling), and wave energy focusing on protruding headlands/capes.
   - Supports continuous offshore progressive wavemaking as well as planar solitary waves (Planar Soliton) and circular Gaussian pulses across single-shot, periodic, moving source, and interactive mouse injection modes.
3. **Spectral / FFT Ocean Surface (`FFTOceanSimulation`)**:
   - Superposition of Tessendorf / Phillips wind-sea spectrum (with wind speed and direction controls) and independent long-wavelength swell (with swell strength and direction controls).
   - Bit-reversal-free Radix-2 Stockham autosort 2D IFFT on Compute Shaders.
   - Wavenumber-weighted choppy horizontal displacement (trochoidal wave crests), exact Jacobian-coupled Lagrangian normals, wave crest subsurface scattering (SSS), and Jacobian whitecap foam detection.
4. **Common Rendering & Comparison Framework**:
   - Universal PBR water surface shader (3D vertex displacement, Schlick Fresnel, depth gradient, shallow-water emerald transmittance + depth contours, crest SSS, and whitecap foam).
   - 5 visual debug modes (`Realistic`, `HeightMap`, `Normal`, `Wireframe`, `Bathymetry`).
   - 1D cross-section profile viewer (direct waveform and seabed inspection with adjustable vertical amplitude scale).
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
