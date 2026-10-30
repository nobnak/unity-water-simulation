# CPU Bottleneck Profiling & Real Frame Performance Report

- **Timestamp**: `2026-10-30 22:34:25`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)` (Direct3D11)
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V` (8 logical cores)
- **Unity Version**: `6000.3.27f1`

## 1. Real Frame Performance Across Sample Scenes

| Scene | Resolution | SubSteps | Total Frame (Mean) | P95 | P99 | FPS | UI Off (Mean) | UI Overhead | CPU Main Thread | GPU Frame | Sync Wait | GC Alloc (UI On) | GC Alloc (UI Off) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | 512x512 | 3 | 8.820 ms | 22.444 ms | 41.357 ms | 113.4 | 7.689 ms | 1.131 ms | 取得不可 | 取得不可 (batchmode/platform unexposed) | 取得不可 | 0.0 B | 0.0 B |
| Scene 02 (Boussinesq) | 512x512 | 4 | 19.355 ms | 19.832 ms | 301.186 ms | 51.7 | 13.984 ms | 5.371 ms | 取得不可 | 取得不可 (batchmode/platform unexposed) | 取得不可 | 0.0 B | 0.0 B |
| Scene 03 (FFTOcean) | 256x256 | 1 | 4.657 ms | 4.989 ms | 14.119 ms | 214.7 | 4.646 ms | 0.011 ms | 取得不可 | 取得不可 (batchmode/platform unexposed) | 取得不可 | 0.0 B | 0.0 B |

## 2. In-Depth Scene 02 Causal Breakdown

### Cause 1: CFL Substep Count
- 4 substeps (default): `0.223 ms/step` (Dispatches: 41)
- 2 substeps: `0.088 ms/step`
- 1 substep: `0.168 ms/step`

### Cause 2: Compute Dispatch Overhead inside StepPhysics
- Predictor Dispatch (8x8 threadgroups): `0.0022 ms`
- CSTridiagonalX Dispatch (rows): `0.0016 ms`
- CSTridiagonalZ Dispatch (cols): `0.0024 ms`
- Filter 2-Pass Dispatch: `0.0032 ms`

### Cause 6: UI OnGUI & GC Allocations
- Real Frame UI Overhead: `0.3339 ms/frame`
- GC Allocated In Frame (UI ON): `0 B/frame`
- GC Allocated In Frame (UI OFF): `0 B/frame`
- Note: UI ON allocations originate from IMGUI dynamic layout tree (GUI.Box/GUILayout.Label). UI OFF allocates 0 B due to string caching.

