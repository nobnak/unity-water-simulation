# CPU Bottleneck Profiling & Causal Investigation Report

- **Timestamp**: 2026-10-30 10:06:44
- **CPU**: Intel(R) Core(TM) Ultra 7 258V (8 cores)
- **GPU / API**: Intel(R) Arc(TM) 140V GPU (16GB) / Direct3D11
- **Unity**: 6000.3.27f1

## 1. 3 Scenes CPU Profile Comparison

| Scene | Resolution | Total Frame (ms) | Sim Step (ms) | WaterRenderer (ms) | SeabedRenderer (ms) | UI OnGUI (ms) | Cam Render (ms) | SubSteps | Dispatches | GC Alloc/Frame |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| Scene 01 (WaveEquation) | 512x512 | 2.188 | 0.031 | 0.003 | 0.000 | 1.283 | 0.869 | 3 | 4 | 231697 B |
| Scene 02 (Boussinesq) | 512x512 | 1.564 | 0.167 | 0.004 | 0.003 | 0.534 | 0.856 | 4 | 41 | 388574 B |
| Scene 03 (FFTOcean) | 256x256 | 1.968 | 0.032 | 0.004 | 0.000 | 0.954 | 0.978 | 1 | 4 | 405367 B |

## 2. Scene 02 Multi-Cause Investigation

### Cause A: CFL Substepping & Step Count
- 4 SubSteps (Current default, dt=0.016s): **0.275 ms** (41 dispatches)
- 2 SubSteps (dt=0.008s x2): **0.117 ms**
- 1 SubStep (dt=0.004s x4): **0.263 ms**

### Cause B: FixedTimeStep Accumulator Catch-Up (Spiral of Death)
- 60 FPS (dt=0.016s, 1 step): **0.275 ms**
- 30 FPS drop (dt=0.033s, 2 steps): **0.549 ms** (8 substeps, 82 dispatches)
- 20 FPS drop (dt=0.050s, 3 steps): **0.824 ms** (12 substeps, 123 dispatches)
- Max Lag Clamp (4 steps): **1.099 ms** (16 substeps, 164 dispatches)

### Cause C: Compute Shader Dispatch Overhead
- CSPredict Dispatch: 0.0030 ms
- CSTridiagonalX Dispatch (512 threadgroups): 0.0023 ms
- CSTridiagonalZ Dispatch (512 threadgroups): 0.0024 ms
- CSFilter 2-Pass Dispatch: 0.0051 ms

### Cause D: Redundant Uniform & Constant Bindings
- 16 Constant Set* calls per substep: 0.0021 ms
- Over 4 substeps (64 API calls): 0.0086 ms

### Cause E: Graphics.CopyTexture Overhead
- Single CopyTexture: 0.0026 ms
- Over 4 substeps (4 copies): 0.0105 ms

### Cause F: SeabedRenderer SharedMaterial Mutation vs PropertyBlock
- Current Full Update (sharedMaterial mutation): 0.0007 ms
- PropertyBlock Only: 0.0007 ms

### Cause G: WaterSampleUI IMGUI Overhead & GC Allocations
- UI OnGUI CPU Time: 0.532 ms/frame
- UI GC Allocations: 81347 bytes/frame

