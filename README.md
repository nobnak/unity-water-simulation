# unity-water-simulation

Unity UPM package and comparison framework featuring 3 independent water surface simulation methods:
1. **2D Linear Wave Equation** (Real-time ripples, obstacle diffraction, interference, wall reflection)
2. **Boussinesq System** (Depth-dependent coastal waves, shoaling, refraction around shoals and headlands, frequency dispersion)
3. **Spectral / FFT Ocean Surface** (Horizon-scale ocean expanse, swell & wind-sea superposition, choppy trochoidal wave crests, Jacobian foam)

The package is located at [`Packages/com.nobnak.water-simulation`](file:///c:/Users/nakata/Repo/unity-water-simulation/Packages/com.nobnak.water-simulation).

## Target Environment
- **Unity**: Unity 6 (6000.3.27f1+)
- **Render Pipeline**: Universal Render Pipeline (URP 17.3.0+)
- **Graphics API**: Direct3D 12 / 11 / Metal / Vulkan (Compute Shader 5.0+ required)
- **Dependencies**: `com.unity.render-pipelines.universal`, `com.unity.mathematics` (1.3.2)

For detailed mathematical formulations, performance benchmarks, and user instructions, see the [Package Documentation](file:///c:/Users/nakata/Repo/unity-water-simulation/Packages/com.nobnak.water-simulation/README.md).
