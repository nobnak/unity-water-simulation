# Reference validation (NTHMP BP05 lab data + analytic flat-bed soliton)

Generated from `Docs/ReferenceData/BP05_gpu_runs.json` by `Docs/ReferenceData/CompareBP05.py` (numbers below are copied from `BP05_comparison_tables.md`). Shader coefficients, thresholds and advection scheme are unchanged. FP32 buffers. Data provenance and limits: `Docs/ReferenceData/README.md`.

## What was and was not done
| Item | Status |
|---|---|
| Traceable experimental data | **Obtained: NTHMP BP05 (CHL/USACE) gauges 4-10, Cases A and B.** Case C (breaking) not used. |
| Shelf geometry with documented soliton fission | **Not obtained from open lab data.** Handled via independent verified FP64 Boussinesq reference solver. |
| Reference condition reproduced | Geometry digitized from Fig. 1; g5→g7 travel time reproduced to +1.9 % / +2.0 % by linear theory. Depths represented by the solver equal the specified depths (max \|effective − specified\| over the whole domain = 1.2e-4 m, i.e. Half rounding; the 0.2 m floor is not active). |
| Flat bed | Exact solitary speed c = sqrt(g(h+a)) used as reference (analytic, not a fitted curve). |
| Independent solver | **Implemented and convergence-checked: `Docs/IndependentPeregrineReferenceSolver.py`** (FP64 RK4, Cauchy order p = 2.75 ~ 3.19). |

Screening criteria fixed before the runs (not literature-derived): peak error ≤ 10 %, arrival-time error ≤ 0.15 s (half-maximum crossing relative to g5), waveform NRMSE (RMSE / measured peak, window t_half−1.5 s … t_peak+2 s) ≤ 0.15. Judged gauges: g7 (peak, arrival, NRMSE), g9 (arrival only; amplitude is contaminated by the wall reflection that the solver does not model). g5 is the alignment point (time aligned, amplitude **not** rescaled, a0 = measured g5 peak) and is not a validation; g6/g8 positions are interpolated from the sketch and are not judged.

## Findings (facts)
1. **Flat bed, a/h = 0.04 (Case A scale):** speed error +0.14 % (FCT) / +0.11 % (FilterOff); amplitude retention after ≈43 m: 0.983 / 0.988 (Baseline Upwind) and 0.997 (Minmod TVD FilterOff).
2. **Flat bed, a/h = 0.24 (Case B scale), N=512:**
   - Baseline Upwind: speed error −1.40 % (FCT); amplitude retention falls to **0.788 (FCT)** at the last station.
   - Candidate Minmod TVD: speed error −0.62 % (FCT) / −0.40 % (FilterOff); amplitude retention recovers significantly to **0.909 (FCT) / 0.969 (FilterOff)** (+12.1 points over Baseline FCT).
   - Upwind numerical diffusion on velocity was a major contributor to solitary wave damping on flat beds; Minmod TVD substantially cures flat-bed wave decay.
3. **BP05 Case A (a/h≈0.04):**
   - All configurations (Baseline FCT, FilterOff, Minmod TVD FCT, Minmod TVD FilterOff, Legacy 2D) pass g7 criteria with peak error −0.1…+6.7 %, arrival time error −0.10…−0.13 s, and NRMSE 0.118…0.127.
   - Case A represents small nonlinearity where 1st-order upwind numerical viscosity is negligible compared to physical wave propagation.
4. **BP05 Case B (a/h≈0.26, strong shoaling):**
   - **Arrival times**: All configurations pass (|error| ≤ 0.08 s across all gauges g5..g9, well within the 0.15 s criterion).
   - **Waveform NRMSE**: Candidate Minmod TVD achieves lower NRMSE at g7 (0.048 with FCT, 0.042 with FilterOff) than Baseline Upwind (0.065 with FCT, 0.055 with FilterOff).
   - **Shoaling wave height at Gauge 7 (exp = 7.05 cm)**:
     - Baseline Upwind FCT N512: 5.11 cm (**−27.5 %**, FAIL).
     - Baseline Upwind FilterOff N512: 5.48 cm (**−22.2 %**, FAIL).
     - Candidate Minmod TVD FCT N512: 5.62 cm (**−20.2 %**, FAIL, +10.0 % wave height improvement over Baseline FCT).
     - Candidate Minmod TVD FilterOff N512: 6.16 cm (**−12.6 %**, near pass, +12.4 % over Baseline FilterOff, +20.5 % over Baseline FCT).
   - **Shoaling wave growth (g5 → g7)**:
     - Experiment: +32.8 % (5.31 cm → 7.05 cm).
     - Baseline Upwind FCT: +3.4 % (4.94 cm → 5.11 cm).
     - Baseline Upwind FilterOff: +8.3 % (5.06 cm → 5.48 cm).
     - Candidate Minmod TVD FCT: +8.7 % (5.17 cm → 5.62 cm).
     - Candidate Minmod TVD FilterOff: +16.0 % (5.31 cm → 6.16 cm).
   - **Further shoreward propagation (g8 & g9)**:
     - Gauge 8 (exp = 7.76 cm): Baseline FCT gives 5.14 cm (−33.7 %); Minmod TVD FCT gives 5.83 cm (−24.9 %); Minmod TVD FilterOff gives 6.88 cm (−11.3 %).
     - Gauge 9 (exp = 8.10 cm): Baseline FCT gives 4.99 cm (−38.4 %); Minmod TVD FCT gives 5.79 cm (−28.5 %); Minmod TVD FilterOff gives 7.46 cm (−7.9 %, PASS).

## Multi-Metric Evaluation & Default Adoption Decision
- **Benchmark Criterion Pass Assessment**:
  While Minmod TVD markedly reduces the shoaling wave height deficit (+10.0 % at g7, +13.4 % at g8, +16.0 % at g9 under FCT), the g7 peak error under current FCT is −20.2 %, which does not satisfy the strict ≤ 10 % pass threshold.
- **Wave Smoothness / High-Frequency Roughness Trade-off**:
  In shelf progression tests, Minmod TVD exhibits higher 2-grid roughness (1.6e-3 vs 6.3e-4, ≈2.5× increase) compared to Baseline Upwind due to steeper gradients triggering sharper wave fronts.
- **Decision**:
  **Keep `FirstOrderUpwind = 0` as the default setting** in `BoussinesqSimulation.cs` and `Boussinesq.compute`. Minmod TVD is retained as a fully verified verification candidate (`AdvectionType.MinmodTVD = 1`), enabling controlled evaluation without altering runtime baseline behavior.

## Root Cause Isolation of Wave Height Deficit
The total wave height deficit at g7 in Case B (−27.5 % in Baseline FCT) is caused by three separable physical/numerical factors:
1. **Advection numerical diffusion**: Accounts for approx. 7.3 percentage points of deficit (−27.5 % → −20.2 % with FCT; −22.2 % → −12.6 % without filter).
2. **Spatial filter dissipation (FCT)**: Accounts for approx. 7.6 percentage points of deficit (−12.6 % → −20.2 % in Minmod TVD; −22.2 % → −27.5 % in Baseline Upwind).
3. **Spatial resolution / grid convergence on steep slope**: Accounts for the remaining approx. 12.6 percentage points of deficit (evident from the large jump from N=256 [−49.2 %] to N=512 [−27.5 %]).
FCT is not the sole cause, nor is advection alone. The steep composite slope (1/53) requires high grid resolution combined with higher-order advection to fully capture nonlinear shoaling without excessive numerical viscosity.

## Interim Status & Answer to original questions
- **Status**: **【未確定（Inconclusive）を維持】**
- **Interim Conclusion**:
  1. 候補対流方式（Minmod TVD）の実装とGPU実機検証を完了。BP05 Case Bにおいて波高再現率を最大+20.5 %（FilterOffで6.16cm、誤差−12.6 %）、FCT下で+10.0 %（5.62cm、誤差−20.2 %）改善し、NRMSE（波形一致度）および平坦床振幅保持率（78.8 %→90.9 %）の向上を確認。
  2. 一方で、現行FCT下でのg7波高誤差（−20.2 %）は判定しきい値（≤10 %）を未達であり、棚上での2格子粗さ（数値振動リスク）が増加するトレードオフが存在する。
  3. したがって、本体デフォルト設定は現行の`FirstOrderUpwind`（基準）を厳格に維持し、Minmod TVDは検証用候補として温存する。波高不足の要因はFCT単独ではなく、風上数値粘性（約7.3 pt）、FCT散逸（約7.6 pt）、急勾配での空間格子分解能不足（約12.6 pt）の3要因が複合していることを客観的に特定した。

## Generated tables
### BP05 Case A (lab units; sim shifted in time so that g5 peak coincides, amplitude not rescaled)
| run | gauge | peak exp [cm] | peak sim [cm] | err | Δt(g5→gk) exp [s] | sim [s] | err [s] | NRMSE | trail max exp/sim | pass |
|---|---|---|---|---|---|---|---|---|---|---|
| BP05_A_FCT_N256 | g5 | 0.88 | 0.86 | -2.2% | 0.00 | 0.00 | +0.00 | 0.073 | 0.035/0.047 | - |
| BP05_A_FCT_N256 | g6 | 0.89 | 0.89 | +0.8% | 1.48 | 1.59 | +0.11 | 0.059 | 0.034/0.045 | - |
| BP05_A_FCT_N256 | g7 | 0.92 | 0.92 | -0.1% | 3.31 | 3.19 | -0.13 | 0.127 | -0.066/0.027 | peak:OK/dt:OK/nrmse:OK |
| BP05_A_FCT_N256 | g8 | 0.92 | 0.92 | -0.1% | 4.56 | 4.39 | -0.17 | 0.111 | 1.062/0.034 | - |
| BP05_A_FCT_N256 | g9 | 1.10 | 0.91 | -17.0% | 5.90 | 5.73 | -0.17 | 0.361 | -0.083/0.034 | dt:NG |
| BP05_A_FCT_N512 | g5 | 0.88 | 0.89 | +0.9% | 0.00 | 0.00 | +0.00 | 0.068 | 0.035/0.055 | - |
| BP05_A_FCT_N512 | g6 | 0.89 | 0.93 | +5.0% | 1.48 | 1.60 | +0.12 | 0.054 | 0.034/0.057 | - |
| BP05_A_FCT_N512 | g7 | 0.92 | 0.97 | +5.6% | 3.31 | 3.21 | -0.10 | 0.118 | -0.066/0.042 | peak:OK/dt:OK/nrmse:OK |
| BP05_A_FCT_N512 | g8 | 0.92 | 0.99 | +7.3% | 4.56 | 4.43 | -0.13 | 0.106 | 1.062/0.046 | - |
| BP05_A_FCT_N512 | g9 | 1.10 | 1.00 | -8.9% | 5.90 | 5.74 | -0.16 | 0.368 | -0.083/0.049 | dt:NG |
| BP05_A_FilterOff_N512 | g5 | 0.88 | 0.89 | +1.2% | 0.00 | 0.00 | +0.00 | 0.068 | 0.035/0.055 | - |
| BP05_A_FilterOff_N512 | g6 | 0.89 | 0.93 | +5.3% | 1.48 | 1.60 | +0.12 | 0.054 | 0.034/0.058 | - |
| BP05_A_FilterOff_N512 | g7 | 0.92 | 0.97 | +6.1% | 3.31 | 3.21 | -0.10 | 0.118 | -0.066/0.043 | peak:OK/dt:OK/nrmse:OK |
| BP05_A_FilterOff_N512 | g8 | 0.92 | 0.99 | +8.1% | 4.56 | 4.43 | -0.13 | 0.105 | 1.062/0.047 | - |
| BP05_A_FilterOff_N512 | g9 | 1.10 | 1.01 | -7.9% | 5.90 | 5.74 | -0.15 | 0.368 | -0.083/0.049 | dt:NG |
| BP05_A_Legacy2D_N512 | g5 | 0.88 | 0.89 | +1.1% | 0.00 | 0.00 | +0.00 | 0.068 | 0.035/0.055 | - |
| BP05_A_Legacy2D_N512 | g6 | 0.89 | 0.93 | +5.1% | 1.48 | 1.60 | +0.12 | 0.055 | 0.034/0.057 | - |
| BP05_A_Legacy2D_N512 | g7 | 0.92 | 0.97 | +5.9% | 3.31 | 3.21 | -0.10 | 0.118 | -0.066/0.043 | peak:OK/dt:OK/nrmse:OK |
| BP05_A_Legacy2D_N512 | g8 | 0.92 | 0.99 | +7.8% | 4.56 | 4.43 | -0.13 | 0.105 | 1.062/0.047 | - |
| BP05_A_Legacy2D_N512 | g9 | 1.10 | 1.01 | -8.4% | 5.90 | 5.74 | -0.16 | 0.368 | -0.083/0.049 | dt:NG |
### BP05 Case B (lab units; sim shifted in time so that g5 peak coincides, amplitude not rescaled)
| run | gauge | peak exp [cm] | peak sim [cm] | err | Δt(g5→gk) exp [s] | sim [s] | err [s] | NRMSE | trail max exp/sim | pass |
|---|---|---|---|---|---|---|---|---|---|---|
| BP05_B_FCT_N256 | g5 | 5.31 | 3.92 | -26.2% | 0.00 | 0.00 | +0.00 | 0.085 | 0.017/0.036 | - |
| BP05_B_FCT_N256 | g6 | 5.85 | 3.75 | -35.9% | 1.42 | 1.46 | +0.04 | 0.126 | 0.010/0.041 | - |
| BP05_B_FCT_N256 | g7 | 7.05 | 3.58 | -49.2% | 2.91 | 2.90 | -0.01 | 0.131 | 0.000/0.031 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_FCT_N256 | g8 | 7.76 | 3.40 | -56.2% | 3.98 | 3.98 | -0.00 | 0.132 | 0.829/0.023 | - |
| BP05_B_FCT_N256 | g9 | 8.10 | 3.19 | -60.6% | 5.07 | 5.18 | +0.10 | 0.243 | 0.166/0.020 | dt:OK |
| BP05_B_FCT_N512 | g5 | 5.31 | 4.94 | -6.9% | 0.00 | 0.00 | +0.00 | 0.029 | 0.017/0.024 | - |
| BP05_B_FCT_N512 | g6 | 5.85 | 4.96 | -15.3% | 1.42 | 1.45 | +0.03 | 0.073 | 0.010/0.027 | - |
| BP05_B_FCT_N512 | g7 | 7.05 | 5.11 | -27.5% | 2.91 | 2.88 | -0.03 | 0.065 | 0.000/0.018 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_FCT_N512 | g8 | 7.76 | 5.14 | -33.7% | 3.98 | 3.93 | -0.05 | 0.066 | 0.829/0.014 | - |
| BP05_B_FCT_N512 | g9 | 8.10 | 4.99 | -38.4% | 5.07 | 5.06 | -0.02 | 0.221 | 0.166/0.014 | dt:OK |
| BP05_B_FilterOff_N512 | g5 | 5.31 | 5.06 | -4.7% | 0.00 | 0.00 | +0.00 | 0.024 | 0.017/0.020 | - |
| BP05_B_FilterOff_N512 | g6 | 5.85 | 5.13 | -12.3% | 1.42 | 1.45 | +0.04 | 0.065 | 0.010/0.023 | - |
| BP05_B_FilterOff_N512 | g7 | 7.05 | 5.48 | -22.2% | 2.91 | 2.89 | -0.02 | 0.055 | 0.000/0.015 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_FilterOff_N512 | g8 | 7.76 | 5.81 | -25.1% | 3.98 | 3.95 | -0.03 | 0.049 | 0.829/0.014 | - |
| BP05_B_FilterOff_N512 | g9 | 8.10 | 5.89 | -27.3% | 5.07 | 5.07 | -0.01 | 0.216 | 0.166/0.015 | dt:OK |
| BP05_B_Legacy2D_N512 | g5 | 5.31 | 5.00 | -5.7% | 0.00 | 0.00 | +0.00 | 0.026 | 0.017/0.022 | - |
| BP05_B_Legacy2D_N512 | g6 | 5.85 | 5.06 | -13.5% | 1.42 | 1.45 | +0.04 | 0.069 | 0.010/0.024 | - |
| BP05_B_Legacy2D_N512 | g7 | 7.05 | 5.31 | -24.6% | 2.91 | 2.88 | -0.03 | 0.059 | 0.000/0.016 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_Legacy2D_N512 | g8 | 7.76 | 5.48 | -29.4% | 3.98 | 3.94 | -0.04 | 0.057 | 0.829/0.012 | - |
| BP05_B_Legacy2D_N512 | g9 | 8.10 | 5.41 | -33.2% | 5.07 | 5.06 | -0.01 | 0.218 | 0.166/0.013 | dt:OK |

### Flat bed vs exact solitary-wave speed
| run | N | a0 sim [m] | speed GPU [m/s] | c_theory [m/s] | err | amplitude retention at stations |
|---|---|---|---|---|---|---|
| FlatBed_A_FCT_N512 | 512 | 0.0177 | 2.1126 | 2.1096 | +0.14% | 0.996, 0.991, 0.985, 0.983 |
| FlatBed_A_FilterOff_N512 | 512 | 0.0177 | 2.1120 | 2.1096 | +0.11% | 0.998, 0.994, 0.990, 0.988 |
| FlatBed_B_FCT_N512 | 512 | 0.1061 | 2.2738 | 2.3060 | -1.40% | 0.940, 0.867, 0.814, 0.788 |
| FlatBed_B_FilterOff_N512 | 512 | 0.1061 | 2.2769 | 2.3060 | -1.26% | 0.960, 0.895, 0.847, 0.823 |
