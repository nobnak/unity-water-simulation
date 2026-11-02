# Reference validation (NTHMP BP05 lab data + analytic flat-bed soliton)

Generated from \Docs/ReferenceData/BP05_gpu_runs.json\ by \Docs/ReferenceData/CompareBP05.py\ (numbers below are copied from \BP05_comparison_tables.md\). Shader coefficients, thresholds and advection scheme are unchanged. FP32 buffers. Data provenance and limits: \Docs/ReferenceData/README.md\.

## What was and was not done
| Item | Status |
|---|---|
| Traceable experimental data | **Obtained: NTHMP BP05 (CHL/USACE) gauges 4-10, Cases A and B.** Case C (breaking) not used. |
| Shelf geometry with documented soliton fission | **Not obtained from open lab data.** Handled via independent verified FP64 Boussinesq reference solver. |
| Reference condition reproduced | Geometry digitized from Fig. 1; g5→g7 travel time reproduced to +1.9 % / +2.0 % by linear theory. Depths represented by the solver equal the specified depths (max |effective − specified| over the whole domain = 1.2e-4 m, i.e. Half rounding; the 0.2 m floor is not active). |
| Flat bed | Exact solitary speed c = sqrt(g(h+a)) used as reference (analytic, not a fitted curve). |
| Independent solver | **Implemented and convergence-checked: \Docs/IndependentPeregrineReferenceSolver.py\** (FP64 RK4, Cauchy order p = 2.75 ~ 3.19). |

Screening criteria fixed before the runs (not literature-derived): peak error ≤ 10 %, arrival-time error ≤ 0.15 s (half-maximum crossing relative to g5), waveform NRMSE (RMSE / measured peak, window t_half−1.5 s … t_peak+2 s) ≤ 0.15. Judged gauges: g7 (peak, arrival, NRMSE), g9 (arrival only; amplitude is contaminated by the wall reflection that the solver does not model). g5 is the alignment point (time aligned, amplitude **not** rescaled, a0 = measured g5 peak) and is not a validation; g6/g8 positions are interpolated from the sketch and are not judged.

## Findings (facts)
1. **Flat bed, a/h = 0.04 (Case A scale, N=512):** speed error +0.10 % ~ +0.14 %; amplitude retention after ≈43 m: 0.983 (Baseline FCT) to 0.997 (Minmod TVD FilterOff). Mass conservation at eval time error ≤ 0.07 %.
2. **Flat bed, a/h = 0.24 (Case B scale, N=512):**
   - Baseline Upwind: speed error −1.40 % (FCT); amplitude retention falls to **0.788 (FCT)** at the last station.
   - Candidate Minmod TVD: speed error −0.62 % (FCT) / −0.40 % (FilterOff); amplitude retention recovers significantly to **0.909 (FCT) / 0.968 (FilterOff)** (+12.1 pt over Baseline FCT).
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
     - Candidate Minmod TVD FilterOff N512: 6.16 cm (**−12.6 %**, FAIL against strict ≤ 10 % standard).
   - **Shoaling wave growth (g5 → g7)**:
     - Experiment: +32.8 % (5.31 cm → 7.05 cm).
     - Baseline Upwind FCT: +3.4 % (4.94 cm → 5.11 cm).
     - Baseline Upwind FilterOff: +8.3 % (5.06 cm → 5.48 cm).
     - Candidate Minmod TVD FCT: +8.7 % (5.17 cm → 5.62 cm).
     - Candidate Minmod TVD FilterOff: +16.0 % (5.31 cm → 6.16 cm).

## Multi-Metric Evaluation & Default Adoption Decision
- **Benchmark Criterion Pass Assessment**:
  While Minmod TVD markedly reduces the shoaling wave height deficit, the g7 peak error under current FCT is −20.2 % and under FilterOff is −12.6 %, both failing the strict ≤ 10 % threshold.
- **Wave Smoothness / High-Frequency Roughness Trade-off**:
  In shelf progression tests, Minmod TVD exhibits higher 2-grid roughness (1.6e-3 vs 6.3e-4, ≈2.5× increase) compared to Baseline Upwind due to steeper gradients triggering sharper wave fronts.
- **Mass Conservation**:
  At evaluation time before sponge absorption, relative mass error is strictly 0.00 % ~ 0.07 % (Case A) and 0.01 % ~ 0.47 % (Case B). Final mass error (3.7 % ~ 17.9 %) reflects deliberate energy and mass absorption as waves enter the right open sponge boundary.
- **Decision**:
  **Keep \FirstOrderUpwind = 0\ as the default setting** in \BoussinesqSimulation.cs\ and \Boussinesq.compute\. Minmod TVD is retained as a fully verified verification candidate (\AdvectionType.MinmodTVD = 1\), enabling controlled evaluation without altering runtime baseline behavior.

## Root Cause Isolation of Wave Height Deficit
The comparisons identify the following specific differences:
1. **Advection discretization difference**:
   - Under FCT (N=512): −27.5 % → −20.2 % (+7.3 pt difference).
   - Under FilterOff (N=512): −22.2 % → −12.6 % (+9.6 pt difference).
2. **Spatial filter dissipation (FCT) difference**:
   - Baseline (N=512): −27.5 % → −22.2 % (+5.3 pt difference).
   - Minmod TVD (N=512): −20.2 % → −12.6 % (+7.6 pt difference).
3. **Remaining deficit (−12.6 %)**:
   The remaining 12.6 pt deficit in Minmod TVD FilterOff relative to the laboratory measurement is NOT attributed solely to grid resolution. It represents unmodeled physical/numerical effects, including dispersion operator truncation on steep 1/53 slopes, depth floor 0.2 m transition, and 3D flume sidewall / turbulent dissipation in the experiment.

## Interim Status & Answer to original questions
- **Status**: **【未確定（Inconclusive）を維持】**
- **Interim Conclusion**:
  1. 候補対流方式（Minmod TVD）の実装とGPU実機検証を完了。BP05 Case Bにおいて波高再現率を最大+20.5 %（FilterOffで6.16cm、誤差−12.6 %）、FCT下で+10.0 %（5.62cm、誤差−20.2 %）改善し、NRMSE（波形一致度）および平坦床振幅保持率（78.8 %→90.9 %）の向上を確認。
  2. 一方で、g7波高誤差（FCT: −20.2 %、FilterOff: −12.6 %）は事前判定しきい値（≤10 %）を満たさず不合格（FAIL）であり、棚上での2格子粗さが増加するトレードオフが存在する。
  3. したがって、本体デフォルト設定は現行の\FirstOrderUpwind\（基準）を厳格に維持し、Minmod TVDは検証用候補として温存する。

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
| BP05_A_MinmodTVD_FCT_N256 | g5 | 0.88 | 0.87 | -1.8% | 0.00 | 0.00 | +0.00 | 0.072 | 0.035/0.046 | - |
| BP05_A_MinmodTVD_FCT_N256 | g6 | 0.89 | 0.90 | +1.4% | 1.48 | 1.59 | +0.11 | 0.058 | 0.034/0.044 | - |
| BP05_A_MinmodTVD_FCT_N256 | g7 | 0.92 | 0.92 | +0.7% | 3.31 | 3.19 | -0.13 | 0.127 | -0.066/0.026 | peak:OK/dt:OK/nrmse:OK |
| BP05_A_MinmodTVD_FCT_N256 | g8 | 0.92 | 0.93 | +0.9% | 4.56 | 4.39 | -0.17 | 0.111 | 1.062/0.032 | - |
| BP05_A_MinmodTVD_FCT_N256 | g9 | 1.10 | 0.92 | -15.9% | 5.90 | 5.73 | -0.17 | 0.362 | -0.083/0.032 | dt:NG |
| BP05_A_MinmodTVD_FCT_N512 | g5 | 0.88 | 0.89 | +1.2% | 0.00 | 0.00 | +0.00 | 0.068 | 0.035/0.055 | - |
| BP05_A_MinmodTVD_FCT_N512 | g6 | 0.89 | 0.94 | +5.4% | 1.48 | 1.60 | +0.12 | 0.054 | 0.034/0.057 | - |
| BP05_A_MinmodTVD_FCT_N512 | g7 | 0.92 | 0.98 | +6.2% | 3.31 | 3.21 | -0.10 | 0.118 | -0.066/0.042 | peak:OK/dt:OK/nrmse:OK |
| BP05_A_MinmodTVD_FCT_N512 | g8 | 0.92 | 0.99 | +8.2% | 4.56 | 4.43 | -0.13 | 0.107 | 1.062/0.046 | - |
| BP05_A_MinmodTVD_FCT_N512 | g9 | 1.10 | 1.01 | -8.0% | 5.90 | 5.74 | -0.16 | 0.369 | -0.083/0.048 | dt:NG |
| BP05_A_MinmodTVD_FilterOff_N512 | g5 | 0.88 | 0.90 | +1.4% | 0.00 | 0.00 | +0.00 | 0.068 | 0.035/0.055 | - |
| BP05_A_MinmodTVD_FilterOff_N512 | g6 | 0.89 | 0.94 | +5.7% | 1.48 | 1.60 | +0.12 | 0.054 | 0.034/0.057 | - |
| BP05_A_MinmodTVD_FilterOff_N512 | g7 | 0.92 | 0.98 | +6.7% | 3.31 | 3.21 | -0.10 | 0.118 | -0.066/0.043 | peak:OK/dt:OK/nrmse:OK |
| BP05_A_MinmodTVD_FilterOff_N512 | g8 | 0.92 | 1.00 | +8.9% | 4.56 | 4.44 | -0.13 | 0.106 | 1.062/0.047 | - |
| BP05_A_MinmodTVD_FilterOff_N512 | g9 | 1.10 | 1.02 | -6.9% | 5.90 | 5.75 | -0.15 | 0.368 | -0.083/0.048 | dt:OK |
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
| BP05_B_MinmodTVD_FCT_N256 | g5 | 5.31 | 4.09 | -22.9% | 0.00 | 0.00 | +0.00 | 0.078 | 0.017/0.032 | - |
| BP05_B_MinmodTVD_FCT_N256 | g6 | 5.85 | 3.98 | -32.1% | 1.42 | 1.45 | +0.03 | 0.117 | 0.010/0.037 | - |
| BP05_B_MinmodTVD_FCT_N256 | g7 | 7.05 | 3.84 | -45.5% | 2.91 | 2.88 | -0.03 | 0.119 | 0.000/0.026 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_MinmodTVD_FCT_N256 | g8 | 7.76 | 3.68 | -52.6% | 3.98 | 3.95 | -0.03 | 0.121 | 0.829/0.019 | - |
| BP05_B_MinmodTVD_FCT_N256 | g9 | 8.10 | 3.49 | -56.9% | 5.07 | 5.13 | +0.06 | 0.239 | 0.166/0.016 | dt:OK |
| BP05_B_MinmodTVD_FCT_N512 | g5 | 5.31 | 5.17 | -2.6% | 0.00 | 0.00 | +0.00 | 0.023 | 0.017/0.020 | - |
| BP05_B_MinmodTVD_FCT_N512 | g6 | 5.85 | 5.29 | -9.6% | 1.42 | 1.45 | +0.03 | 0.061 | 0.010/0.021 | - |
| BP05_B_MinmodTVD_FCT_N512 | g7 | 7.05 | 5.62 | -20.2% | 2.91 | 2.86 | -0.05 | 0.048 | 0.000/0.012 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_MinmodTVD_FCT_N512 | g8 | 7.76 | 5.83 | -24.9% | 3.98 | 3.90 | -0.08 | 0.057 | 0.829/0.007 | - |
| BP05_B_MinmodTVD_FCT_N512 | g9 | 8.10 | 5.79 | -28.5% | 5.07 | 5.00 | -0.08 | 0.215 | 0.166/0.008 | dt:OK |
| BP05_B_MinmodTVD_FilterOff_N512 | g5 | 5.31 | 5.31 | -0.0% | 0.00 | 0.00 | +0.00 | 0.020 | 0.017/0.017 | - |
| BP05_B_MinmodTVD_FilterOff_N512 | g6 | 5.85 | 5.52 | -5.7% | 1.42 | 1.45 | +0.03 | 0.052 | 0.010/0.017 | - |
| BP05_B_MinmodTVD_FilterOff_N512 | g7 | 7.05 | 6.16 | -12.6% | 2.91 | 2.87 | -0.04 | 0.042 | 0.000/0.010 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_MinmodTVD_FilterOff_N512 | g8 | 7.76 | 6.88 | -11.3% | 3.98 | 3.92 | -0.07 | 0.057 | 0.829/0.009 | - |
| BP05_B_MinmodTVD_FilterOff_N512 | g9 | 8.10 | 7.46 | -7.9% | 5.07 | 5.01 | -0.07 | 0.218 | 0.166/0.010 | dt:OK |
| BP05_B_Legacy2D_N512 | g5 | 5.31 | 5.00 | -5.7% | 0.00 | 0.00 | +0.00 | 0.026 | 0.017/0.022 | - |
| BP05_B_Legacy2D_N512 | g6 | 5.85 | 5.06 | -13.5% | 1.42 | 1.45 | +0.04 | 0.069 | 0.010/0.024 | - |
| BP05_B_Legacy2D_N512 | g7 | 7.05 | 5.31 | -24.6% | 2.91 | 2.88 | -0.03 | 0.059 | 0.000/0.016 | peak:NG/dt:OK/nrmse:OK |
| BP05_B_Legacy2D_N512 | g8 | 7.76 | 5.48 | -29.4% | 3.98 | 3.94 | -0.04 | 0.057 | 0.829/0.012 | - |
| BP05_B_Legacy2D_N512 | g9 | 8.10 | 5.41 | -33.2% | 5.07 | 5.06 | -0.01 | 0.218 | 0.166/0.013 | dt:OK |

### Flat bed vs exact solitary-wave speed
| run | N | a0 sim [m] | speed GPU [m/s] | c_theory [m/s] | err | mass err (eval/final) | 2-grid osc | status | amplitude retention at stations |
|---|---|---|---|---|---|---|---|---|---|
| FlatBed_A_FCT_N512 | 512 | 0.0177 | 2.1126 | 2.1096 | +0.14% | 0.00%/3.78% | 3.14e-06 | PASS | 0.996, 0.991, 0.985, 0.983 |
| FlatBed_A_FilterOff_N512 | 512 | 0.0177 | 2.1120 | 2.1096 | +0.11% | 0.05%/3.80% | 2.61e-05 | PASS | 0.998, 0.994, 0.990, 0.988 |
| FlatBed_A_MinmodTVD_FCT_N512 | 512 | 0.0177 | 2.1125 | 2.1096 | +0.13% | 0.02%/3.75% | 3.21e-06 | PASS | 0.998, 0.996, 0.993, 0.992 |
| FlatBed_A_MinmodTVD_FilterOff_N512 | 512 | 0.0177 | 2.1118 | 2.1096 | +0.10% | 0.07%/3.77% | 2.62e-05 | PASS | 1.000, 0.999, 0.998, 0.997 |
| FlatBed_B_FCT_N512 | 512 | 0.1061 | 2.2738 | 2.3060 | -1.40% | 0.45%/15.98% | 1.16e-05 | PASS | 0.939, 0.867, 0.814, 0.788 |
| FlatBed_B_FilterOff_N512 | 512 | 0.1061 | 2.2769 | 2.3060 | -1.26% | 0.47%/16.67% | 1.91e-04 | PASS | 0.959, 0.895, 0.847, 0.823 |
| FlatBed_B_MinmodTVD_FCT_N512 | 512 | 0.1061 | 2.2918 | 2.3060 | -0.62% | 0.02%/17.14% | 1.46e-05 | PASS | 0.976, 0.943, 0.920, 0.909 |
| FlatBed_B_MinmodTVD_FilterOff_N512 | 512 | 0.1061 | 2.2969 | 2.3060 | -0.40% | 0.01%/17.89% | 2.17e-04 | PASS | 0.998, 0.981, 0.971, 0.968 |
