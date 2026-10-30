# CPU Bottleneck Profiling & Causal Investigation Report

- **Timestamp**: 2026-10-30 21:08:56
- **CPU**: Intel(R) Core(TM) Ultra 7 258V (8 cores)
- **GPU / API**: Intel(R) Arc(TM) 140V GPU (16GB) / Direct3D11
- **Unity**: 6000.3.27f1

## 1. 3 Scenes CPU Profile Comparison

| Scene | Resolution | Mean Frame (ms) | P95 (ms) | P99 (ms) | FPS | Sim Step (ms) | WaterRenderer (ms) | SeabedRenderer (ms) | UI OnGUI (ms) | Cam Render (ms) | SubSteps | Dispatches | GC Alloc/Frame |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| Scene 01 (WaveEquation) | 512x512 | 0.666 | 1.044 | 3.425 | 1500.7 | 0.024 | 0.002 | 0.000 | 0.000 | 0.637 | 3 | 4 | 0 B |
| Scene 02 (Boussinesq) | 512x512 | 0.809 | 1.191 | 1.745 | 1236.4 | 0.123 | 0.003 | 0.001 | 0.000 | 0.681 | 4 | 41 | 0 B |
| Scene 03 (FFTOcean) | 256x256 | 0.698 | 0.946 | 1.119 | 1433.1 | 0.025 | 0.003 | 0.000 | 0.000 | 0.669 | 1 | 4 | 0 B |

## 2. Scene 02 Multi-Cause Investigation

### Cause A: CFL Substepping & Step Count
- 4 SubSteps (Current default, dt=0.016s): **0.325 ms** (41 dispatches)
- 2 SubSteps (dt=0.008s x2): **0.116 ms**
- 1 SubStep (dt=0.004s x4): **0.239 ms**

### Cause B: FixedTimeStep Accumulator Catch-Up (Spiral of Death)
- 60 FPS (dt=0.016s, 1 step): **0.325 ms**
- 30 FPS drop (dt=0.033s, 2 steps): **0.650 ms** (8 substeps, 82 dispatches)
- 20 FPS drop (dt=0.050s, 3 steps): **0.975 ms** (12 substeps, 123 dispatches)
- Max Lag Clamp (4 steps): **1.300 ms** (16 substeps, 164 dispatches)

### Cause C: Compute Shader Dispatch Overhead
- CSPredict Dispatch: 0.0036 ms
- CSTridiagonalX Dispatch (512 threadgroups): 0.0023 ms
- CSTridiagonalZ Dispatch (512 threadgroups): 0.0022 ms
- CSFilter 2-Pass Dispatch: 0.0043 ms

### Cause D: Redundant Uniform & Constant Bindings
- 16 Constant Set* calls per substep: 0.0016 ms
- Over 4 substeps (64 API calls): 0.0066 ms

### Cause E: Graphics.CopyTexture Overhead
- Single CopyTexture: 0.0020 ms
- Over 4 substeps (4 copies): 0.0080 ms

### Cause F: SeabedRenderer SharedMaterial Mutation vs PropertyBlock
- Current Full Update (sharedMaterial mutation): 0.0002 ms
- PropertyBlock Only: 0.0006 ms

### Cause G: WaterSampleUI IMGUI Overhead & GC Allocations
- UI OnGUI CPU Time: 0.013 ms/frame
- UI GC Allocations: 3604 bytes/frame

