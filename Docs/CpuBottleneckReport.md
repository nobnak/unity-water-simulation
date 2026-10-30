# CPU Bottleneck Profiling & Real Frame Performance Report

- **Timestamp**: `2026-10-30 23:05:17`
- **Measurement Context**: Unity PlayerLoop (PlayMode Coroutine across real frames)
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)` (Direct3D11)
- **CPU**: `Intel(R) Core(TM) Ultra 7 258V` (8 logical cores)
- **Unity Version**: `6000.3.27f1`

## 1. Real Frame Performance Across Sample Scenes (PlayerLoop PlayMode)

| Scene | Resolution | SubSteps | Total Frame (Mean) | P95 | P99 | FPS | UI Off (Mean) | UI Overhead | Order Bias | CPU Main Thread | GPU Frame | GC Alloc (UI On) | GC Alloc (UI Off) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Scene 01 (WaveEquation) | 512x512 | 3 | 0.129 ms | 0.154 ms | 0.178 ms | 7752.5 | 0.213 ms | -0.084 ms | 0.071 ms | 0.057 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1888 B | 1889 B |
| Scene 02 (Boussinesq) | 512x512 | 4 | 0.185 ms | 0.367 ms | 0.626 ms | 5418.0 | 0.214 ms | -0.029 ms | 0.213 ms | 0.085 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1888 B | 1906 B |
| Scene 03 (FFTOcean) | 256x256 | 1 | 0.179 ms | 0.267 ms | 0.377 ms | 5589.4 | 0.158 ms | 0.021 ms | 0.008 ms | 0.087 ms (実測) | 取得不可 (batchmodeスワップチェーン非アクティブ/ドライバ非公開) | 1888 B | 1889 B |

## 2. In-Depth Scene 02 Causal Breakdown

### Cause 1: CFL Substep Count (Synchronous Throughput Benchmark)
- 4 substeps (default): `0.105 ms/step` (Dispatches: 64)
- 2 substeps: `0.088 ms/step`
- 1 substep: `0.087 ms/step`

### Cause 2: Compute Dispatch Overhead inside StepPhysics
- Predictor Dispatch (8x8 threadgroups): `0.0020 ms`
- CSTridiagonalX Dispatch (rows): `0.0015 ms`
- CSTridiagonalZ Dispatch (cols): `0.0015 ms`
- Filter 2-Pass Dispatch: `0.0039 ms`

### Cause 6: UI OnGUI & GC Allocations
- Real Frame UI Overhead: `-0.0290 ms/frame`
- GC Allocated In Frame (UI ON): `1888 B/frame`
- GC Allocated In Frame (UI OFF): `0 B/frame`
- Allocation Source: WaterSampleUI.OnGUI IMGUI layout and string allocation (GUIStyle, GUIContent, Rect boxing)

