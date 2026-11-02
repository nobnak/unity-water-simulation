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
1. **Flat bed, a/h = 0.04 (Case A scale):** speed error +0.14 % (FCT) / +0.11 % (FilterOff); amplitude retention after ≈43 m: 0.983 / 0.988.
2. **Flat bed, a/h = 0.24 (Case B scale), N=512:** speed error −1.4 % (FCT) / −1.3 % (FilterOff); amplitude retention falls to **0.788 (FCT) / 0.823 (FilterOff)** at the last station. The loss occurs without any bathymetry and also with the spatial filter off, so a large part of it comes from the base scheme/resolution; FCT adds ≈3.5 points.
3. **BP05 Case A (a/h≈0.04):** g7 passes all three criteria for FCT N256/N512, FilterOff and Legacy 2D (peak error −0.1…+6.1 %, arrival error −0.10…−0.13 s, NRMSE 0.118…0.127). The four settings differ by ≤ 0.5 % in peak height at N=512, i.e. this case does not discriminate between them. g9 arrival error is −0.15…−0.17 s (just outside the 0.15 s criterion; g9 is also the gauge nearest to the unmodelled wall).
4. **BP05 Case B (a/h≈0.26):** arrival times pass (|error| ≤ 0.05 s at g7, N=512 relative to g5), **peak heights FAIL at g7: −27.5 % (FCT N512), −22.2 % (FilterOff), −24.6 % (Legacy 2D)**; FCT N256 gives −49.2 %. The measured peak grows +33 % from g5 to g7 (5.31 → 7.05 cm); the GPU growth is only +3 % (FCT) / +8 % (FilterOff) / +6 % (Legacy). Results change strongly between N=256 and N=512 (g7 peak −49 % → −27.5 %), so **Case B is not grid-converged at N=512** and the severe amplitude deficit is shared across all numerical settings.
   - Note on Arrival Times: Arrival times were time-aligned at g5 peak; this evaluates relative travel speed on the slope, not absolute arrival time from the wave generator.

## Not established
- Whether the amplitude deficit in Case B is due to the FCT, to the first-order upwind advection / MacCormack base scheme, to resolution, or to the replaced beach/wall: not separated (FilterOff shows a smaller but still large deficit; N=512 is not converged).
- The earlier FilterOff multi-peak trains: BP05 shows no multi-peak train in the data, but the FilterOff runs here also show none, so BP05 neither supports nor rejects them as physical.
- "Initial version vs current version": the old commits were not reproduced (FilterOff is raw MacCormack **with the current upwind advection**, not the old central-difference scheme), so no correctness statement about the old version is made.
- The RMSE against the earlier custom curve is not used for any pass/fail decision.

## Interim Status & Answer to original questions
- **Status**: **【未確定（Inconclusive）】**
- **Interim Conclusion**: 実験・独立ソルバーとの比較を開始したが、条件と演算子の不一致、および波高の大きな残差が残る。現行方式の物理的妥当性と分裂抑制の評価は未確定。

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
