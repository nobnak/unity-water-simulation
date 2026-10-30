"""
Numerical Verification Suite for unity-water-simulation
Reproducible verification of dispersion operators, spatial filter, boundary conditions,
and time integration convergence.

Run: python Docs/NumericalVerificationSuite.py
"""
import math
import json
import random

def verify_boussinesq_dispersion():
    """Point 1: Direction-split ADI Peregrine vs 2D standard Peregrine continuum gain,
    residual convergence, and true defect correction (non-accumulating).
    """
    results = []
    for kh in [0.5, 1.0, 1.5]:
        target = 1.0 / (1.0 + (kh**2) / 3.0)
        c_target = math.sqrt(target)
        for sweeps in [1, 2, 3, 4]:
            for deg in [0, 45]:
                rad = math.radians(deg)
                kx_h = kh * math.cos(rad)
                kz_h = kh * math.sin(rad)
                beta_x = (kx_h**2) / 3.0
                beta_z = (kz_h**2) / 3.0
                product = (1.0 + beta_x) * (1.0 + beta_z)
                cross = beta_x * beta_z
                target_helmholtz = 1.0 + (kh**2) / 3.0

                # Shader buffer simulation with _ForceOrigTex:
                # f_orig is immutable non-dispersive acceleration (normalized to 1.0).
                # Sweep 1: RHS = f_orig
                # Sweep s >= 2: RHS = f_orig + defect(a^(s-1)), where defect = cross * a^(s-1)
                f_orig = 1.0
                gain = 0.0
                defect_val = 0.0
                rhs_val = f_orig

                for s in range(1, sweeps + 1):
                    if s == 1:
                        rhs_val = f_orig
                    else:
                        defect_val = cross * gain
                        rhs_val = f_orig + defect_val
                    gain = rhs_val / product

                c_actual = math.sqrt(gain)
                rel_err = (c_actual / c_target - 1.0) * 100.0
                residual = abs(target_helmholtz * gain - f_orig)

                results.append({
                    "kh": kh,
                    "angle_deg": deg,
                    "sweeps": sweeps,
                    "peregrine_gain": target,
                    "implemented_gain": gain,
                    "phase_speed_error_pct": rel_err,
                    "helmholtz_residual": residual,
                    "rhs_final": rhs_val,
                    "defect_final": defect_val
                })
    return results

def verify_filter():
    """Point 2: Directional-Split 4th-Order Biharmonic Spatial Filter (CSFilterX & CSFilterZ)
    Verifies time-step scaled damping, explicit stability limit, exact mass conservation,
    wall boundary projection, and spatial extrema / overshoot characteristics.
    Also retains legacy unsplit 2D filter verification with clear distinction.
    """
    # 1. Mode damping for Current Directional-Split Filter (CSFilterX -> CSFilterZ)
    split_modes = []
    for cells in [8, 16, 32, 64]:
        q = 2.0 * math.pi / cells
        eig = 16.0 * (math.sin(q / 2.0) ** 4)
        for dt in [0.008, 0.004, 0.002, 0.0004, 0.0002, 0.0001]:
            # Directional-split scaling: kappa = min(0.058, 12.0 * dt)
            kappa = min(0.058, 12.0 * dt)
            per_step = 1.0 - kappa * eig
            steps = round(1.0 / dt)
            amp_1s = per_step ** steps
            split_modes.append({
                "cells_per_wavelength": cells,
                "dt": dt,
                "steps_per_sec": steps,
                "per_substep_gain": per_step,
                "amplitude_after_1sec": amp_1s
            })

    # Legacy unsplit filter modes (for clear historical comparison)
    legacy_unsplit_modes = []
    for cells in [8, 16, 32, 64]:
        q = 2.0 * math.pi / cells
        eig = 16.0 * (math.sin(q / 2.0) ** 4)
        for dt in [0.008, 0.004, 0.002, 0.0004, 0.0002, 0.0001]:
            kappa_legacy = min(0.031, 5.0 * dt)
            per_step = 1.0 - kappa_legacy * eig
            steps = round(1.0 / dt)
            legacy_unsplit_modes.append({
                "cells_per_wavelength": cells,
                "dt": dt,
                "steps_per_sec": steps,
                "per_substep_gain": per_step,
                "amplitude_after_1sec": per_step ** steps
            })

    # Conservative zero-flux boundary test for 1D stencil
    w = 32
    s = [0.0] * w
    s[0] = 1.0
    d4_impulse = [0.0] * w
    d4_impulse[0] = 2.0 * s[0] - 3.0 * s[1] + s[2]
    d4_impulse[1] = -3.0 * s[0] + 6.0 * s[1] - 4.0 * s[2] + s[3]
    for x in range(2, w - 2):
        d4_impulse[x] = s[x-2] - 4.0 * s[x-1] + 6.0 * s[x] - 4.0 * s[x+1] + s[x+2]
    d4_impulse[w-2] = s[w-4] - 4.0 * s[w-3] + 6.0 * s[w-2] - 3.0 * s[w-1]
    d4_impulse[w-1] = s[w-3] - 3.0 * s[w-2] + 2.0 * s[w-1]

    # Random field test
    random.seed(42)
    rand_field = [random.uniform(-10.0, 10.0) for _ in range(w)]
    d4_rand = [0.0] * w
    d4_rand[0] = 2.0 * rand_field[0] - 3.0 * rand_field[1] + rand_field[2]
    d4_rand[1] = -3.0 * rand_field[0] + 6.0 * rand_field[1] - 4.0 * rand_field[2] + rand_field[3]
    for x in range(2, w - 2):
        d4_rand[x] = rand_field[x-2] - 4.0 * rand_field[x-1] + 6.0 * rand_field[x] - 4.0 * rand_field[x+1] + rand_field[x+2]
    d4_rand[w-2] = rand_field[w-4] - 4.0 * rand_field[w-3] + 6.0 * rand_field[w-2] - 3.0 * rand_field[w-1]
    d4_rand[w-1] = rand_field[w-3] - 3.0 * rand_field[w-2] + 2.0 * rand_field[w-1]

    # Directional-split 2-pass wall boundary verification
    wall_test_results = verify_filter_wall_boundary_split()

    # Extremum and monotonicity investigation (testing impulse, Gaussian, step, oblique wave)
    extrema_results = verify_filter_extrema_and_monotonicity()

    return {
        "directional_split_modes": split_modes,
        "legacy_unsplit_modes": legacy_unsplit_modes,
        "impulse_mass_before": float(sum(s)),
        "impulse_mass_after": float(sum(s) - 0.058 * sum(d4_impulse)),
        "impulse_d4_sum": float(sum(d4_impulse)),
        "random_field_d4_sum": float(sum(d4_rand)),
        "wall_preservation_split": wall_test_results,
        "filter_extrema_and_monotonicity": extrema_results
    }

def verify_filter_wall_boundary_split():
    """Point 2b: Directional-split 2-pass biharmonic filter boundary constraint verification.
    Applies Pass 1 (CSFilterX) then Pass 2 (CSFilterZ) to 2D velocity fields.
    Confirms lateral wall normal velocity u=0 at x=0, W-1 and offshore/beach normal velocity w=0 at y=0, H-1.
    """
    W, H = 16, 16
    dx, dz, dt = 1.0, 1.0, 0.008
    kappa_x = min(0.058, 12.0 * dt)
    kappa_z = min(0.058, 12.0 * dt)

    random.seed(123)
    depth = [[3.5 - (3.5 - 0.8) * (y / (H - 1)) for x in range(W)] for y in range(H)]
    eta = [[0.2 * math.cos(2.0 * math.pi * (x / W + y / H)) for x in range(W)] for y in range(H)]
    u = [[0.0 if (x == 0 or x == W - 1) else random.uniform(-0.5, 0.5) for x in range(W)] for y in range(H)]
    w = [[0.0 if (y == 0 or y == H - 1) else random.uniform(-0.5, 0.5) for x in range(W)] for y in range(H)]

    # Pass 1: X biharmonic filter
    u_mid = [[0.0 for _ in range(W)] for _ in range(H)]
    w_mid = [[0.0 for _ in range(W)] for _ in range(H)]
    for y in range(H):
        for x in range(W):
            # 4th difference in X
            if x == 0:
                d4x_u = 2.0 * u[y][0] - 3.0 * u[y][1] + u[y][2]
                d4x_w = 2.0 * w[y][0] - 3.0 * w[y][1] + w[y][2]
            elif x == 1:
                d4x_u = -3.0 * u[y][0] + 6.0 * u[y][1] - 4.0 * u[y][2] + u[y][3]
                d4x_w = -3.0 * w[y][0] + 6.0 * w[y][1] - 4.0 * w[y][2] + w[y][3]
            elif x == W - 2:
                d4x_u = u[y][W-4] - 4.0 * u[y][W-3] + 6.0 * u[y][W-2] - 3.0 * u[y][W-1]
                d4x_w = w[y][W-4] - 4.0 * w[y][W-3] + 6.0 * w[y][W-2] - 3.0 * w[y][W-1]
            elif x == W - 1:
                d4x_u = u[y][W-3] - 3.0 * u[y][W-2] + 2.0 * u[y][W-1]
                d4x_w = w[y][W-3] - 3.0 * w[y][W-2] + 2.0 * w[y][W-1]
            else:
                d4x_u = u[y][x-2] - 4.0 * u[y][x-1] + 6.0 * u[y][x] - 4.0 * u[y][x+1] + u[y][x+2]
                d4x_w = w[y][x-2] - 4.0 * w[y][x-1] + 6.0 * w[y][x] - 4.0 * w[y][x+1] + w[y][x+2]

            filtered_u = u[y][x] - kappa_x * d4x_u
            if x == 0 or x == W - 1:
                filtered_u = 0.0
            u_mid[y][x] = filtered_u
            w_mid[y][x] = w[y][x] - kappa_x * d4x_w

    # Pass 2: Z biharmonic filter
    u_out = [[0.0 for _ in range(W)] for _ in range(H)]
    w_out = [[0.0 for _ in range(W)] for _ in range(H)]
    for x in range(W):
        for y in range(H):
            # 4th difference in Z
            if y == 0:
                d4z_u = 2.0 * u_mid[0][x] - 3.0 * u_mid[1][x] + u_mid[2][x]
                d4z_w = 2.0 * w_mid[0][x] - 3.0 * w_mid[1][x] + w_mid[2][x]
            elif y == 1:
                d4z_u = -3.0 * u_mid[0][x] + 6.0 * u_mid[1][x] - 4.0 * u_mid[2][x] + u_mid[3][x]
                d4z_w = -3.0 * w_mid[0][x] + 6.0 * w_mid[1][x] - 4.0 * w_mid[2][x] + w_mid[3][x]
            elif y == H - 2:
                d4z_u = u_mid[H-4][x] - 4.0 * u_mid[H-3][x] + 6.0 * u_mid[H-2][x] - 3.0 * u_mid[H-1][x]
                d4z_w = w_mid[H-4][x] - 4.0 * w_mid[H-3][x] + 6.0 * w_mid[H-2][x] - 3.0 * w_mid[H-1][x]
            elif y == H - 1:
                d4z_u = u_mid[H-3][x] - 3.0 * u_mid[H-2][x] + 2.0 * u_mid[H-1][x]
                d4z_w = w_mid[H-3][x] - 3.0 * w_mid[H-2][x] + 2.0 * w_mid[H-1][x]
            else:
                d4z_u = u_mid[y-2][x] - 4.0 * u_mid[y-1][x] + 6.0 * u_mid[y][x] - 4.0 * u_mid[y+1][x] + u_mid[y+2][x]
                d4z_w = w_mid[y-2][x] - 4.0 * w_mid[y-1][x] + 6.0 * w_mid[y][x] - 4.0 * w_mid[y+1][x] + w_mid[y+2][x]

            u_out[y][x] = u_mid[y][x] - kappa_z * d4z_u
            filtered_w = w_mid[y][x] - kappa_z * d4z_w
            if y == 0 or y == H - 1:
                filtered_w = 0.0
            w_out[y][x] = filtered_w

    max_wall_u = max(max(abs(u_out[y][0]), abs(u_out[y][W-1])) for y in range(H))
    max_wall_w = max(max(abs(w_out[0][x]), abs(w_out[H-1][x])) for x in range(W))

    return {
        "max_lateral_wall_u": float(max_wall_u),
        "max_transverse_wall_w": float(max_wall_w),
        "walls_strictly_zero": (max_wall_u == 0.0 and max_wall_w == 0.0)
    }

def verify_filter_extrema_and_monotonicity():
    """Point 3: Rigorous verification of biharmonic spatial filter monotonicity and extrema.
    Evaluates new extrema (undershoot / overshoot) on:
    1. Single impulse (delta function)
    2. Narrow and broad Gaussian bumps
    3. Heaviside step discontinuity
    4. Oblique solitary wave ridge
    5. Crest corrugation damping vs secondary ripple formation
    """
    kappa = 0.058 # maximum explicit stability cap
    N = 64

    # 1. Single Delta Impulse
    f_impulse = [0.0] * N
    f_impulse[N // 2] = 1.0
    out_impulse = _apply_1d_biharmonic(f_impulse, kappa)
    impulse_min = min(out_impulse)
    impulse_max = max(out_impulse)

    # 2. Gaussian bumps of varying widths (sigma = 0.8, 1.0, 1.5, 2.0, 3.0)
    gauss_results = []
    for sigma in [0.8, 1.0, 1.2, 1.5, 2.0, 3.0]:
        f_g = [math.exp(-((x - N // 2) / sigma) ** 2) for x in range(N)]
        out_g = _apply_1d_biharmonic(f_g, kappa)
        gauss_results.append({
            "sigma": sigma,
            "orig_min": min(f_g),
            "orig_max": max(f_g),
            "filtered_min": min(out_g),
            "filtered_max": max(out_g),
            "undershoot_depth": float(max(0.0, -min(out_g)))
        })

    # 3. Heaviside Step Function
    f_step = [1.0 if x >= N // 2 else 0.0 for x in range(N)]
    out_step = _apply_1d_biharmonic(f_step, kappa)
    step_min = min(out_step)
    step_max = max(out_step)

    # 4. 2D Oblique solitary wave ridge: sech^2((x + y - N) / sigma)
    W, H = 32, 32
    oblique_steep = [0.0] * (W * H)
    oblique_broad = [0.0] * (W * H)
    for y in range(H):
        for x in range(W):
            xi_steep = (x + y - 32.0) / 0.8
            xi_broad = (x + y - 32.0) / 2.5
            oblique_steep[y * W + x] = (1.0 / math.cosh(xi_steep)) ** 2
            oblique_broad[y * W + x] = (1.0 / math.cosh(xi_broad)) ** 2

    out_obl_steep = _apply_2d_split_biharmonic(oblique_steep, W, H, kappa, kappa)
    out_obl_broad = _apply_2d_split_biharmonic(oblique_broad, W, H, kappa, kappa)

    # 5. Crest Corrugation test: 1D wave crest with high-frequency sawtooth ripple
    # Smooth crest + 10% 2dx ripple
    f_corrug = [0.0] * N
    for x in range(N):
        macro = math.exp(-((x - N // 2) / 4.0) ** 2)
        ripple = 0.10 * (1.0 if (x % 2 == 0) else -1.0) * macro
        f_corrug[x] = macro + ripple

    out_corrug = _apply_1d_biharmonic(f_corrug, kappa)
    # Measure ripple amplitude before and after: d2 = |f[x+1] - 2*f[x] + f[x-1]|
    d2_before = max(abs(f_corrug[x+1] - 2*f_corrug[x] + f_corrug[x-1]) for x in range(1, N-1))
    d2_after = max(abs(out_corrug[x+1] - 2*out_corrug[x] + out_corrug[x-1]) for x in range(1, N-1))

    return {
        "impulse_test": {
            "input_range": [0.0, 1.0],
            "filtered_min": impulse_min,
            "filtered_max": impulse_max,
            "has_negative_dip": (impulse_min < -1e-6),
            "undershoot_depth": float(-impulse_min)
        },
        "gaussian_tests": gauss_results,
        "step_test": {
            "input_range": [0.0, 1.0],
            "filtered_min": step_min,
            "filtered_max": step_max,
            "overshoot": float(step_max - 1.0),
            "undershoot": float(-step_min)
        },
        "oblique_wave_test": {
            "steep_ridge_orig_min": min(oblique_steep),
            "steep_ridge_filtered_min": min(out_obl_steep),
            "steep_undershoot": float(max(0.0, -min(out_obl_steep))),
            "broad_ridge_orig_min": min(oblique_broad),
            "broad_ridge_filtered_min": min(out_obl_broad),
            "broad_undershoot": float(max(0.0, -min(out_obl_broad)))
        },
        "corrugation_damping_test": {
            "sawtooth_d2_before": float(d2_before),
            "sawtooth_d2_after": float(d2_after),
            "damping_ratio_pct": float((1.0 - d2_after / d2_before) * 100.0)
        },
        "theoretical_monotonicity_summary": (
            "The 4th-order biharmonic stencil [-kappa, 4*kappa, 1-6*kappa, 4*kappa, -kappa] "
            "possesses negative side lobes (-kappa). While Fourier modes satisfy L^2 stability "
            "G(k) in [0, 1], the operator violates spatial monotonicity (TVD / Maximum Principle). "
            "Sharp gradients (sigma < 1.5 cells) and step discontinuities produce O(kappa) "
            "undershoots/overshoots (e.g. -0.058 on unit delta impulse)."
        )
    }

def _apply_1d_biharmonic(f, kappa):
    n = len(f)
    out = [0.0] * n
    d4 = [0.0] * n
    d4[0] = 2.0 * f[0] - 3.0 * f[1] + f[2]
    d4[1] = -3.0 * f[0] + 6.0 * f[1] - 4.0 * f[2] + f[3]
    for x in range(2, n - 2):
        d4[x] = f[x-2] - 4.0 * f[x-1] + 6.0 * f[x] - 4.0 * f[x+1] + f[x+2]
    d4[n-2] = f[n-4] - 4.0 * f[n-3] + 6.0 * f[n-2] - 3.0 * f[n-1]
    d4[n-1] = f[n-3] - 3.0 * f[n-2] + 2.0 * f[n-1]
    for i in range(n):
        out[i] = f[i] - kappa * d4[i]
    return out

def _apply_2d_split_biharmonic(field, W, H, kappa_x, kappa_z):
    mid = [0.0] * (W * H)
    for y in range(H):
        row = y * W
        mid[row + 0] = field[row + 0] - kappa_x * (2.0 * field[row + 0] - 3.0 * field[row + 1] + field[row + 2])
        mid[row + 1] = field[row + 1] - kappa_x * (-3.0 * field[row + 0] + 6.0 * field[row + 1] - 4.0 * field[row + 2] + field[row + 3])
        for x in range(2, W - 2):
            idx = row + x
            d4 = field[idx - 2] - 4.0 * field[idx - 1] + 6.0 * field[idx] - 4.0 * field[idx + 1] + field[idx + 2]
            mid[idx] = field[idx] - kappa_x * d4
        mid[row + W - 2] = field[row + W - 2] - kappa_x * (field[row + W - 4] - 4.0 * field[row + W - 3] + 6.0 * field[row + W - 2] - 3.0 * field[row + W - 1])
        mid[row + W - 1] = field[row + W - 1] - kappa_x * (field[row + W - 3] - 3.0 * field[row + W - 2] + 2.0 * field[row + W - 1])

    out = [0.0] * (W * H)
    for x in range(W):
        out[0 * W + x] = mid[0 * W + x] - kappa_z * (2.0 * mid[0 * W + x] - 3.0 * mid[1 * W + x] + mid[2 * W + x])
        out[1 * W + x] = mid[1 * W + x] - kappa_z * (-3.0 * mid[0 * W + x] + 6.0 * mid[1 * W + x] - 4.0 * mid[2 * W + x] + mid[3 * W + x])
        for y in range(2, H - 2):
            idx = y * W + x
            d4 = mid[(y - 2) * W + x] - 4.0 * mid[(y - 1) * W + x] + 6.0 * mid[idx] - 4.0 * mid[(y + 1) * W + x] + mid[(y + 2) * W + x]
            out[idx] = mid[idx] - kappa_z * d4
        out[(H - 2) * W + x] = mid[(H - 2) * W + x] - kappa_z * (mid[(H - 4) * W + x] - 4.0 * mid[(H - 3) * W + x] + 6.0 * mid[(H - 2) * W + x] - 3.0 * mid[(H - 1) * W + x])
        out[(H - 1) * W + x] = mid[(H - 1) * W + x] - kappa_z * (mid[(H - 3) * W + x] - 3.0 * mid[(H - 2) * W + x] + 2.0 * mid[(H - 1) * W + x])
    return out

# Backward compatibility alias
verify_filter_wall_boundary = verify_filter_wall_boundary_split
verify_full_substep_wall_boundary = verify_filter_wall_boundary_split

def _pcr_solve(n, a_in, b_in, c_in, d_in):
    """Tridiagonal Parallel Cyclic Reduction (PCR) solver matching CSTridiagonalX/Z."""
    a = list(a_in); b = list(b_in); c = list(c_in); d = list(d_in)
    steps = int(math.ceil(math.log2(n)))
    for s in range(steps):
        stride = 1 << s
        a_new = list(a); b_new = list(b); c_new = list(c); d_new = list(d)
        for i in range(n):
            k1 = a[i] / max(1e-8, b[i-stride]) if i >= stride else 0.0
            cL = c[i-stride] if i >= stride else 0.0
            dL = d[i-stride] if i >= stride else 0.0
            a_new[i] = -k1 * a[i-stride] if i >= stride else 0.0

            k2 = c[i] / max(1e-8, b[i+stride]) if i + stride < n else 0.0
            aR = a[i+stride] if i + stride < n else 0.0
            dR = d[i+stride] if i + stride < n else 0.0
            c_new[i] = -k2 * c[i+stride] if i + stride < n else 0.0

            b_new[i] = b[i] - k1 * cL - k2 * aR
            d_new[i] = d[i] - k1 * dL - k2 * dR
        a, b, c, d = a_new, b_new, c_new, d_new
    return [d[i] / max(1e-8, b[i]) for i in range(n)]

def verify_full_substep_simulation_boundary():
    """Simulates 1 complete Boussinesq substep matching the exact Compute Shader pipeline:
    CSPredict -> SolvePeregrineDispersion (PCR X/Z + DefectCorrection Sweeps) -> CSApplyPredictedDispersion ->
    CSCorrect -> SolvePeregrineDispersion -> CSApplyDispersion -> CSFilterHighFrequency -> SolidWallProjection.

    Evaluates 4 cases:
      1. Dispersive + SolidWall + Wavemaker + Slope bathymetry
      2. Dispersive + SideWallSponge + NoWavemaker + Flat bathymetry
      3. NonDispersive + SolidWall + NoWavemaker + Slope bathymetry
      4. Dispersive + SolidWall + PureConservation (sponge=0, friction=0, wavemaker=0, clamp inactive)
    """
    def run_substep(dispersion_enabled=True, side_wall_sponge=False, wavemaker_enabled=False,
                    bathymetry_slope=True, random_velocity=True, friction_enabled=True,
                    sponge_enabled=True, sweeps=2):
        W, H = 16, 16
        dx, dz = 1.0, 1.0
        dt = 0.008
        g = 9.81
        alpha = 1.0 if dispersion_enabled else 0.0
        bed_friction = 0.0025 if friction_enabled else 0.0
        sponge_width = 3.0 if sponge_enabled else 0.0
        sponge_strength = 5.0 if sponge_enabled else 0.0

        if bathymetry_slope:
            depth = [[3.5 - (3.5 - 0.8) * (y / (H - 1)) for x in range(W)] for y in range(H)]
        else:
            depth = [[3.5 for x in range(W)] for y in range(H)]

        random.seed(42)
        eta = [[0.2 * math.cos(2.0 * math.pi * (x / W + y / H)) for x in range(W)] for y in range(H)]
        if random_velocity:
            u = [[0.0 if (x == 0 or x == W - 1) else random.uniform(-0.3, 0.3) for x in range(W)] for y in range(H)]
            w = [[0.0 if (y == 0 or y == H - 1) else random.uniform(-0.3, 0.3) for x in range(W)] for y in range(H)]
        else:
            u = [[0.0 for _ in range(W)] for _ in range(H)]
            w = [[0.0 for _ in range(W)] for _ in range(H)]

        mass_initial = sum(sum(row) for row in eta)

        def sponge_damp(x, y):
            if not sponge_enabled:
                return 1.0
            dist = min(y, H - 1 - y)
            if side_wall_sponge:
                dist = min(dist, min(x, W - 1 - x))
            if dist < sponge_width:
                t = 1.0 - (dist / sponge_width)
                damp_rate = t * t * sponge_strength
                return 1.0 / (1.0 + damp_rate * dt)
            return 1.0

        def wavemaker_sources(x, y, hC, phase):
            if not wavemaker_enabled:
                return 0.0, 0.0
            wavemaker_y = int(H * 0.15)
            dy = abs(y - wavemaker_y)
            if dy <= 2:
                freq = 0.15
                amp = 0.2
                sigma = 1.0
                gaussian = math.exp(-0.5 * (dy / sigma) ** 2)
                c0 = math.sqrt(g * max(0.2, hC))
                src_eta = 2.0 * amp * math.sin(phase) * (c0 / dx) * gaussian
                src_w = 2.0 * amp * math.sin(phase) * (g / c0) * gaussian
                return src_eta, src_w
            return 0.0, 0.0

        # 1. CSPredict
        dir_pred = 1
        signedInvDx = dir_pred / dx
        signedInvDz = dir_pred / dz

        eta_pred = [[0.0 for _ in range(W)] for _ in range(H)]
        fx_pred = [[0.0 for _ in range(W)] for _ in range(H)]
        fz_pred = [[0.0 for _ in range(W)] for _ in range(H)]

        for y in range(H):
            for x in range(W):
                posX_x = min(W - 1, max(0, x + dir_pred))
                posZ_y = min(H - 1, max(0, y + dir_pred))

                hC = depth[y][x]
                hX = depth[y][posX_x]
                hZ = depth[posZ_y][x]

                totHC = max(0.05, hC + eta[y][x])
                totHX = max(0.05, hX + eta[y][posX_x])
                totHZ = max(0.05, hZ + eta[posZ_y][x])

                dHudx = (totHX * u[y][posX_x] - totHC * u[y][x]) * signedInvDx
                dHwdz = (totHZ * w[posZ_y][x] - totHC * w[y][x]) * signedInvDz

                src_eta, src_w = wavemaker_sources(x, y, hC, 0.0)
                ep = eta[y][x] - dt * (dHudx + dHwdz) + dt * src_eta

                detadx = (eta[y][posX_x] - eta[y][x]) * signedInvDx
                detadz = (eta[posZ_y][x] - eta[y][x]) * signedInvDz

                dudx = (u[y][posX_x] - u[y][x]) * signedInvDx
                dudz = (u[posZ_y][x] - u[y][x]) * signedInvDz
                dwdx = (w[y][posX_x] - w[y][x]) * signedInvDx
                dwdz = (w[posZ_y][x] - w[y][x]) * signedInvDz

                advectX = u[y][x] * dudx + w[y][x] * dudz
                advectZ = u[y][x] * dwdx + w[y][x] * dwdz

                friction = bed_friction / totHC

                fx = -g * detadx - advectX - friction * u[y][x]
                fz = -g * detadz - advectZ - friction * w[y][x]

                eta_pred[y][x] = ep
                fx_pred[y][x] = fx
                fz_pred[y][x] = fz

        # Dispersion solver
        def solve_dispersion(fx_in, fz_in):
            f_orig_x = [row[:] for row in fx_in]
            f_orig_z = [row[:] for row in fz_in]
            f_work_x = [row[:] for row in fx_in]
            f_work_z = [row[:] for row in fz_in]
            acc_x = [[0.0 for _ in range(W)] for _ in range(H)]
            acc_z = [[0.0 for _ in range(W)] for _ in range(H)]

            for s in range(sweeps):
                if s > 0:
                    for y in range(H):
                        for x in range(W):
                            l = max(0, x - 1); r = min(W - 1, x + 1)
                            d = max(0, y - 1); u_idx = min(H - 1, y + 1)

                            hC = depth[y][x]
                            betaX = alpha * (hC * hC) / (3.0 * dx * dx)
                            betaZ = alpha * (hC * hC) / (3.0 * dz * dz)

                            for comp, acc, fo, fw in [('x', acc_x, f_orig_x, f_work_x), ('z', acc_z, f_orig_z, f_work_z)]:
                                d2zC = acc[u_idx][x] - 2.0 * acc[y][x] + acc[d][x]
                                d2zL = acc[u_idx][l] - 2.0 * acc[y][l] + acc[d][l]
                                d2zR = acc[u_idx][r] - 2.0 * acc[y][r] + acc[d][r]
                                crossD4 = d2zR - 2.0 * d2zC + d2zL
                                defect = betaX * betaZ * crossD4
                                fw[y][x] = fo[y][x] + defect

                acc_pong_x = [[0.0 for _ in range(W)] for _ in range(H)]
                acc_pong_z = [[0.0 for _ in range(W)] for _ in range(H)]

                for y in range(H):
                    a_x = [0.0] * W; b_x = [1.0] * W; c_x = [0.0] * W; d_x = [0.0] * W
                    a_z = [0.0] * W; b_z = [1.0] * W; c_z = [0.0] * W; d_z = [0.0] * W

                    for x in range(W):
                        hC = depth[y][x]
                        if y == 0 or y == H - 1:
                            d_x[x] = 0.0; d_z[x] = 0.0
                        elif x == 0 or x == W - 1:
                            if side_wall_sponge:
                                d_x[x] = 0.0; d_z[x] = 0.0
                            else:
                                beta = (alpha / (dx * dx)) * ((1.0 / 3.0) * hC * hC)
                                if x == 0:
                                    b_x[x] = 1.0; c_x[x] = 0.0; d_x[x] = 0.0
                                    b_z[x] = 1.0 + 2.0 * beta; c_z[x] = -2.0 * beta; d_z[x] = f_work_z[y][x]
                                else:
                                    a_x[x] = 0.0; b_x[x] = 1.0; d_x[x] = 0.0
                                    a_z[x] = -2.0 * beta; b_z[x] = 1.0 + 2.0 * beta; d_z[x] = f_work_z[y][x]
                        else:
                            hL = depth[y][x - 1]; hR = depth[y][x + 1]
                            invDx2 = alpha / (dx * dx)
                            aVal = -invDx2 * (0.5 * hC * hL - (hC * hC) / 6.0)
                            bVal = 1.0 + invDx2 * ((2.0 / 3.0) * hC * hC)
                            cVal = -invDx2 * (0.5 * hC * hR - (hC * hC) / 6.0)

                            a_x[x] = aVal; b_x[x] = bVal; c_x[x] = cVal; d_x[x] = f_work_x[y][x]
                            a_z[x] = aVal; b_z[x] = bVal; c_z[x] = cVal; d_z[x] = f_work_z[y][x]

                    sol_x = _pcr_solve(W, a_x, b_x, c_x, d_x)
                    sol_z = _pcr_solve(W, a_z, b_z, c_z, d_z)
                    for x in range(W):
                        if not side_wall_sponge and (x == 0 or x == W - 1):
                            sol_x[x] = 0.0
                        acc_pong_x[y][x] = sol_x[x]
                        acc_pong_z[y][x] = sol_z[x]

                for x in range(W):
                    a_x = [0.0] * H; b_x = [1.0] * H; c_x = [0.0] * H; d_x = [0.0] * H
                    a_z = [0.0] * H; b_z = [1.0] * H; c_z = [0.0] * H; d_z = [0.0] * H

                    for y in range(H):
                        hC = depth[y][x]
                        if y == 0 or y == H - 1:
                            beta = (alpha / (dz * dz)) * ((1.0 / 3.0) * hC * hC)
                            if y == 0:
                                b_x[y] = 1.0 + 2.0 * beta; c_x[y] = -2.0 * beta; d_x[y] = acc_pong_x[y][x]
                                b_z[y] = 1.0; c_z[y] = 0.0; d_z[y] = 0.0
                            else:
                                a_x[y] = -2.0 * beta; b_x[y] = 1.0 + 2.0 * beta; d_x[y] = acc_pong_x[y][x]
                                a_z[y] = 0.0; b_z[y] = 1.0; d_z[y] = 0.0
                        elif (x == 0 or x == W - 1) and side_wall_sponge:
                            d_x[y] = 0.0; d_z[y] = 0.0
                        else:
                            hD = depth[y - 1][x]; hU = depth[y + 1][x]
                            invDz2 = alpha / (dz * dz)
                            aVal = -invDz2 * (0.5 * hC * hD - (hC * hC) / 6.0)
                            bVal = 1.0 + invDz2 * ((2.0 / 3.0) * hC * hC)
                            cVal = -invDz2 * (0.5 * hC * hU - (hC * hC) / 6.0)

                            a_x[y] = aVal; b_x[y] = bVal; c_x[y] = cVal; d_x[y] = acc_pong_x[y][x]
                            a_z[y] = aVal; b_z[y] = bVal; c_z[y] = cVal; d_z[y] = acc_pong_z[y][x]

                    sol_x = _pcr_solve(H, a_x, b_x, c_x, d_x)
                    sol_z = _pcr_solve(H, a_z, b_z, c_z, d_z)
                    for y in range(H):
                        if not side_wall_sponge and (x == 0 or x == W - 1):
                            sol_x[y] = 0.0
                        if y == 0 or y == H - 1:
                            sol_z[y] = 0.0
                        acc_x[y][x] = sol_x[y]
                        acc_z[y][x] = sol_z[y]

            return acc_x, acc_z

        u_pred = [[0.0 for _ in range(W)] for _ in range(H)]
        w_pred = [[0.0 for _ in range(W)] for _ in range(H)]

        if dispersion_enabled:
            a1_x, a1_z = solve_dispersion(fx_pred, fz_pred)
            for y in range(H):
                for x in range(W):
                    damp = sponge_damp(x, y)
                    eta_pred[y][x] = max(-15.0, min(15.0, eta_pred[y][x] * damp))
                    u_pred[y][x] = max(-30.0, min(30.0, (u[y][x] + dt * a1_x[y][x]) * damp))
                    w_pred[y][x] = max(-30.0, min(30.0, (w[y][x] + dt * a1_z[y][x]) * damp))
        else:
            for y in range(H):
                for x in range(W):
                    damp = sponge_damp(x, y)
                    eta_pred[y][x] = max(-15.0, min(15.0, eta_pred[y][x] * damp))
                    up = max(-30.0, min(30.0, (u[y][x] + dt * fx_pred[y][x]) * damp))
                    wp = max(-30.0, min(30.0, (w[y][x] + dt * fz_pred[y][x]) * damp))
                    if not side_wall_sponge and (x == 0 or x == W - 1): up = 0.0
                    if y == 0 or y == H - 1: wp = 0.0
                    u_pred[y][x] = up; w_pred[y][x] = wp

        # 2. CSCorrect
        dir_corr = -1
        signedInvDx = dir_corr / dx
        signedInvDz = dir_corr / dz

        eta_next = [[0.0 for _ in range(W)] for _ in range(H)]
        fx_corr = [[0.0 for _ in range(W)] for _ in range(H)]
        fz_corr = [[0.0 for _ in range(W)] for _ in range(H)]

        for y in range(H):
            for x in range(W):
                posX_x = min(W - 1, max(0, x + dir_corr))
                posZ_y = min(H - 1, max(0, y + dir_corr))

                hC = depth[y][x]
                hX = depth[y][posX_x]
                hZ = depth[posZ_y][x]

                totHC = max(0.05, hC + eta_pred[y][x])
                totHX = max(0.05, hX + eta_pred[y][posX_x])
                totHZ = max(0.05, hZ + eta_pred[posZ_y][x])

                dHudxStar = (totHX * u_pred[y][posX_x] - totHC * u_pred[y][x]) * signedInvDx
                dHwdzStar = (totHZ * w_pred[posZ_y][x] - totHC * w_pred[y][x]) * signedInvDz

                src_eta_star, src_w_star = wavemaker_sources(x, y, hC, 2.0 * math.pi * 0.15 * dt)

                en = 0.5 * (eta[y][x] + eta_pred[y][x] - dt * (dHudxStar + dHwdzStar) + dt * src_eta_star)
                damp = sponge_damp(x, y)
                eta_next[y][x] = max(-15.0, min(15.0, en * damp))

                detadxStar = (eta_pred[y][posX_x] - eta_pred[y][x]) * signedInvDx
                detadzStar = (eta_pred[posZ_y][x] - eta_pred[y][x]) * signedInvDz

                dudxStar = (u_pred[y][posX_x] - u_pred[y][x]) * signedInvDx
                dudzStar = (u_pred[posZ_y][x] - u_pred[y][x]) * signedInvDz
                dwdxStar = (w_pred[y][posX_x] - w_pred[y][x]) * signedInvDx
                dwdzStar = (w_pred[posZ_y][x] - w_pred[y][x]) * signedInvDz

                advectXStar = u_pred[y][x] * dudxStar + w_pred[y][x] * dudzStar
                advectZStar = u_pred[y][x] * dwdxStar + w_pred[y][x] * dwdzStar

                frictionStar = bed_friction / totHC

                fxStar = -g * detadxStar - advectXStar - frictionStar * u_pred[y][x]
                fzStar = -g * detadzStar - advectZStar - frictionStar * w_pred[y][x]

                fx_corr[y][x] = fxStar
                fz_corr[y][x] = fzStar

        u_disp = [[0.0 for _ in range(W)] for _ in range(H)]
        w_disp = [[0.0 for _ in range(W)] for _ in range(H)]
        u_disp_pre_project = [[0.0 for _ in range(W)] for _ in range(H)]

        if dispersion_enabled:
            a2_x, a2_z = solve_dispersion(fx_corr, fz_corr)
            for y in range(H):
                for x in range(W):
                    damp = sponge_damp(x, y)
                    aAvgX = 0.5 * (a1_x[y][x] + a2_x[y][x])
                    aAvgZ = 0.5 * (a1_z[y][x] + a2_z[y][x])
                    un = (u[y][x] + dt * aAvgX) * damp
                    wn = (w[y][x] + dt * aAvgZ) * damp
                    u_disp_pre_project[y][x] = un
                    if not side_wall_sponge and (x == 0 or x == W - 1):
                        un = 0.0
                    if y == 0 or y == H - 1:
                        wn = 0.0
                    u_disp[y][x] = max(-30.0, min(30.0, un))
                    w_disp[y][x] = max(-30.0, min(30.0, wn))
        else:
            for y in range(H):
                for x in range(W):
                    damp = sponge_damp(x, y)
                    un = 0.5 * (u[y][x] + u_pred[y][x] + dt * fx_corr[y][x]) * damp
                    wn = 0.5 * (w[y][x] + w_pred[y][x] + dt * fz_corr[y][x]) * damp
                    u_disp_pre_project[y][x] = un
                    if not side_wall_sponge and (x == 0 or x == W - 1):
                        un = 0.0
                    if y == 0 or y == H - 1:
                        wn = 0.0
                    u_disp[y][x] = max(-30.0, min(30.0, un))
                    w_disp[y][x] = max(-30.0, min(30.0, wn))

        # 3. CSFilterHighFrequency
        def apply_filter(field, is_u=False, is_w=False):
            f_out = [[0.0 for _ in range(W)] for _ in range(H)]
            f_pre_proj = [[0.0 for _ in range(W)] for _ in range(H)]
            kappa = min(0.031, 5.0 * dt)
            for y in range(H):
                for x in range(W):
                    if x == 0: d4x = 2.0*field[y][0] - 3.0*field[y][1] + field[y][2]
                    elif x == 1: d4x = -3.0*field[y][0] + 6.0*field[y][1] - 4.0*field[y][2] + field[y][3]
                    elif x == W-2: d4x = field[y][W-4] - 4.0*field[y][W-3] + 6.0*field[y][W-2] - 3.0*field[y][W-1]
                    elif x == W-1: d4x = field[y][W-3] - 3.0*field[y][W-2] + 2.0*field[y][W-1]
                    else: d4x = field[y][x-2] - 4.0*field[y][x-1] + 6.0*field[y][x] - 4.0*field[y][x+1] + field[y][x+2]

                    if y == 0: d4z = 2.0*field[0][x] - 3.0*field[1][x] + field[2][x]
                    elif y == 1: d4z = -3.0*field[0][x] + 6.0*field[1][x] - 4.0*field[2][x] + field[3][x]
                    elif y == H-2: d4z = field[H-4][x] - 4.0*field[H-3][x] + 6.0*field[H-2][x] - 3.0*field[H-1][x]
                    elif y == H-1: d4z = field[H-3][x] - 3.0*field[H-2][x] + 2.0*field[H-1][x]
                    else: d4z = field[y-2][x] - 4.0*field[y-1][x] + 6.0*field[y][x] - 4.0*field[y+1][x] + field[y+2][x]

                    val = field[y][x] - kappa * (d4x + d4z)
                    f_pre_proj[y][x] = val
                    if is_u and not side_wall_sponge and (x == 0 or x == W - 1):
                        val = 0.0
                    if is_w and (y == 0 or y == H - 1):
                        val = 0.0
                    f_out[y][x] = val
            return f_out, f_pre_proj

        eta_final, _ = apply_filter(eta_next)
        u_final, u_filter_pre_proj = apply_filter(u_disp, is_u=True)
        w_final, w_filter_pre_proj = apply_filter(w_disp, is_w=True)

        max_wall_u_post = max(max(abs(u_final[y][0]), abs(u_final[y][W - 1])) for y in range(H))
        max_wall_u_pre_filter = max(max(abs(u_filter_pre_proj[y][0]), abs(u_filter_pre_proj[y][W - 1])) for y in range(H))
        max_wall_u_pre_disp = max(max(abs(u_disp_pre_project[y][0]), abs(u_disp_pre_project[y][W - 1])) for y in range(H))
        max_wall_flux = max(max(abs((depth[y][0] + eta_final[y][0]) * u_final[y][0]),
                                abs((depth[y][W-1] + eta_final[y][W-1]) * u_final[y][W-1])) for y in range(H))

        mass_final = sum(sum(row) for row in eta_final)
        mass_change = mass_final - mass_initial

        has_nan_inf = any(math.isnan(val) or math.isinf(val)
                          for grid in [eta_final, u_final, w_final]
                          for row in grid for val in row)

        return {
            "side_walls_strictly_zero": (max_wall_u_post == 0.0),
            "max_wall_normal_velocity_final": float(max_wall_u_post),
            "max_wall_normal_velocity_pre_filter_projection": float(max_wall_u_pre_filter),
            "max_wall_normal_velocity_pre_dispersion_projection": float(max_wall_u_pre_disp),
            "max_wall_normal_flux": float(max_wall_flux),
            "has_nan_or_inf": has_nan_inf,
            "mass_initial": float(mass_initial),
            "mass_final": float(mass_final),
            "mass_change": float(mass_change)
        }

    c1 = run_substep(dispersion_enabled=True, side_wall_sponge=False, wavemaker_enabled=True, bathymetry_slope=True)
    c2 = run_substep(dispersion_enabled=True, side_wall_sponge=True, wavemaker_enabled=False, bathymetry_slope=False)
    c3 = run_substep(dispersion_enabled=False, side_wall_sponge=False, wavemaker_enabled=False, bathymetry_slope=True)
    c4 = run_substep(dispersion_enabled=True, side_wall_sponge=False, wavemaker_enabled=False,
                     bathymetry_slope=True, friction_enabled=False, sponge_enabled=False)

    return {
        "case1_dispersive_solid_wavemaker_slope": c1,
        "case2_dispersive_sponge_nowavemaker_flat": c2,
        "case3_nondispersive_solid_nowavemaker_slope": c3,
        "case4_dispersive_solid_pure_flux_conservation": c4,
        "pure_flux_mass_conservation_satisfied": abs(c4["mass_change"]) < 1e-12,
        "all_solid_walls_strictly_zero": c1["side_walls_strictly_zero"] and c3["side_walls_strictly_zero"] and c4["side_walls_strictly_zero"],
        "no_nan_or_inf_detected": not (c1["has_nan_or_inf"] or c2["has_nan_or_inf"] or c3["has_nan_or_inf"] or c4["has_nan_or_inf"])
    }

def verify_wave_time_convergence():
    """Point 3: Symplectic Euler kick-drift time integration convergence on (h, v) state pair."""
    n, c, length, mode = 512, 8.0, 50.0, 4
    omega = c * 2.0 * math.sin(math.pi * mode / n) / (length / n)
    duration = 1.0
    exact_h = math.cos(omega * duration)
    exact_v = -math.sin(omega * duration)
    rows = []
    prev_err = None
    for dt in [0.008, 0.004, 0.002, 0.001]:
        h, v = 1.0, 0.0
        steps = round(duration / dt)
        for _ in range(steps):
            v -= dt * (omega ** 2) * h
            h += dt * v
        diff_h = h - exact_h
        diff_v = (v / omega) - exact_v
        err = math.sqrt(diff_h**2 + diff_v**2)
        ratio = (prev_err / err) if prev_err is not None else 0.0
        prev_err = err
        rows.append({
            "dt": dt,
            "steps": steps,
            "error_l2": err,
            "error_reduction_ratio": ratio,
            "order_of_accuracy": math.log2(ratio) if ratio > 0 else 1.0
        })
    return rows

def verify_slip_boundary():
    """Point 5: Decoupled normal (Dirichlet) and tangential (Neumann) sidewall boundary conditions via PCR,
    with subdiagonal interior coefficients correctly set to -beta (matching shader CSTridiagonalX).
    """
    n, beta = 32, 10.0

    # Solve normal via PCR:
    # Interior rows: a = -beta, b = 1 + 2*beta, c = -beta, d = 1.0
    # Wall rows (i=0, n-1): strict Dirichlet 1*a_wall = 0 -> a=0, b=1, c=0, d=0
    a_n = [-beta] * n; b_n = [1.0 + 2.0 * beta] * n; c_n = [-beta] * n; d_n = [1.0] * n
    a_n[0] = 0.0; b_n[0] = 1.0; c_n[0] = 0.0; d_n[0] = 0.0
    a_n[-1] = 0.0; b_n[-1] = 1.0; c_n[-1] = 0.0; d_n[-1] = 0.0

    a = list(a_n); b = list(b_n); c = list(c_n); d = list(d_n)
    for s in range(5):
        stride = 1 << s
        a_new = list(a); b_new = list(b); c_new = list(c); d_new = list(d)
        for i in range(n):
            k1 = a[i] / b[i-stride] if i >= stride else 0.0
            cL = c[i-stride] if i >= stride else 0.0
            dL = d[i-stride] if i >= stride else 0.0
            a_new[i] = -k1 * a[i-stride] if i >= stride else 0.0

            k2 = c[i] / b[i+stride] if i + stride < n else 0.0
            aR = a[i+stride] if i + stride < n else 0.0
            dR = d[i+stride] if i + stride < n else 0.0
            c_new[i] = -k2 * c[i+stride] if i + stride < n else 0.0

            b_new[i] = b[i] - k1 * cL - k2 * aR
            d_new[i] = d[i] - k1 * dL - k2 * dR
        a, b, c, d = a_new, b_new, c_new, d_new
    acc_norm = [d[i]/b[i] for i in range(n)]

    # Solve tangential via PCR:
    # Interior rows: a = -beta, b = 1 + 2*beta, c = -beta, d = 1.0
    # Wall rows: Neumann d(az)/dx = 0 -> b=1+2*beta, c=-2*beta (at i=0); a=-2*beta, b=1+2*beta (at i=n-1)
    a_t = [-beta] * n; b_t = [1.0 + 2.0 * beta] * n; c_t = [-beta] * n; d_t = [1.0] * n
    a_t[0] = 0.0; b_t[0] = 1.0 + 2.0 * beta; c_t[0] = -2.0 * beta; d_t[0] = 1.0
    a_t[-1] = -2.0 * beta; b_t[-1] = 1.0 + 2.0 * beta; c_t[-1] = 0.0; d_t[-1] = 1.0

    a = list(a_t); b = list(b_t); c = list(c_t); d = list(d_t)
    for s in range(5):
        stride = 1 << s
        a_new = list(a); b_new = list(b); c_new = list(c); d_new = list(d)
        for i in range(n):
            k1 = a[i] / b[i-stride] if i >= stride else 0.0
            cL = c[i-stride] if i >= stride else 0.0
            dL = d[i-stride] if i >= stride else 0.0
            a_new[i] = -k1 * a[i-stride] if i >= stride else 0.0

            k2 = c[i] / b[i+stride] if i + stride < n else 0.0
            aR = a[i+stride] if i + stride < n else 0.0
            dR = d[i+stride] if i + stride < n else 0.0
            c_new[i] = -k2 * c[i+stride] if i + stride < n else 0.0

            b_new[i] = b[i] - k1 * cL - k2 * aR
            d_new[i] = d[i] - k1 * dL - k2 * dR
        a, b, c, d = a_new, b_new, c_new, d_new
    acc_tan = [d[i]/b[i] for i in range(n)]

    return {
        "n": n,
        "beta": beta,
        "normal_acceleration_at_wall": float(acc_norm[0]),
        "intended_normal_acceleration": 0.0,
        "normal_acceleration_interior": float(acc_norm[1]),
        "tangential_acceleration_at_wall": float(acc_tan[0]),
        "tangential_acceleration_interior": float(acc_tan[1]),
        "subdiagonal_interior_coefficient": -beta
    }

def verify_relative_depth_regimes():
    """Point 6: Relative depth kh and Boussinesq validity diagnostics."""
    cases = [
        {"desc": "Scene 02 Shallow/Intermediate (default)", "depth": 3.5, "freq": 0.15},
        {"desc": "Scene 02 High Freq (near cutoff)", "depth": 3.5, "freq": 0.40},
        {"desc": "Deep Water Domain (depth=15m)", "depth": 15.0, "freq": 0.25},
    ]
    g = 9.81
    res = []
    for c in cases:
        h = c["depth"]
        f = c["freq"]
        fc = (1.0 / (2.0 * math.pi)) * math.sqrt(3.0 * g / h)
        c0 = math.sqrt(g * h)
        wavelength = c0 / f
        kh = (2.0 * math.pi * h) / wavelength
        h_over_lambda = h / wavelength
        regime = "Shallow (Airy)" if kh < 0.31 else ("Intermediate (Peregrine Valid)" if kh <= 1.0 else ("Deep (Peregrine Accuracy Degraded)" if kh <= 1.5 else "Near Cutoff / Evanescent"))
        res.append({
            "description": c["desc"],
            "depth_m": h,
            "frequency_hz": f,
            "cutoff_frequency_hz": fc,
            "wavelength_m": wavelength,
            "kh": kh,
            "h_over_lambda": h_over_lambda,
            "regime": regime,
            "is_valid_intermediate": kh <= 1.0
        })
    return res

if __name__ == "__main__":
    suite = {
        "boussinesq_dispersion": verify_boussinesq_dispersion(),
        "filter": verify_filter(),
        "wave_time_convergence": verify_wave_time_convergence(),
        "slip_boundary": verify_slip_boundary(),
        "relative_depth_regimes": verify_relative_depth_regimes(),
        "full_substep_simulation_boundary": verify_full_substep_simulation_boundary()
    }
    out_path = r"c:\Users\nakata\Repo\unity-water-simulation\Docs\NumericalVerificationResults.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(suite, f, indent=2)

    print(f"=== Verification Suite Completed. Saved to {out_path} ===")
    print(f"Normal Wall Accel: {suite['slip_boundary']['normal_acceleration_at_wall']:.8f} (intended: 0.0)")
    print(f"Subdiagonal interior coeff: {suite['slip_boundary']['subdiagonal_interior_coefficient']}")
    print(f"Filter Impulse Mass Change: {suite['filter']['impulse_mass_after'] - suite['filter']['impulse_mass_before']:.2e}")
    print(f"Filter Random Field d4 Sum: {suite['filter']['random_field_d4_sum']:.2e}")
    wall_split = suite['filter']['wall_preservation_split']
    print(f"Filter Directional-Split Wall Normal Vel U: {wall_split['max_lateral_wall_u']:.8f}, W: {wall_split['max_transverse_wall_w']:.8f} (strictly zero: {wall_split['walls_strictly_zero']})")
    extrema = suite['filter']['filter_extrema_and_monotonicity']
    print(f"Filter Monotonicity Check: Impulse Dip={extrema['impulse_test']['undershoot_depth']:.4f}, Step Overshoot={extrema['step_test']['overshoot']:.4f}, Corrugation Damping={extrema['corrugation_damping_test']['damping_ratio_pct']:.1f}%")
    full_sub = suite["full_substep_simulation_boundary"]
    print("Full Substep Simulation Boundary:")
    print(f"  All solid walls strictly zero: {full_sub['all_solid_walls_strictly_zero']}")
    print(f"  Pure flux mass conservation satisfied: {full_sub['pure_flux_mass_conservation_satisfied']}")
    print(f"  Case 4 Isolated Flux Mass Change: {full_sub['case4_dispersive_solid_pure_flux_conservation']['mass_change']:.2e}")
    print(f"  Case 1 (Wavemaker) Final Wall U: {full_sub['case1_dispersive_solid_wavemaker_slope']['max_wall_normal_velocity_final']:.8f} (PreFilter: {full_sub['case1_dispersive_solid_wavemaker_slope']['max_wall_normal_velocity_pre_filter_projection']:.4f})")
    print("Wave Time Convergence:")
    for r in suite["wave_time_convergence"]:
        print(f"  dt={r['dt']:.3f}: error={r['error_l2']:.4f}, reduction={r['error_reduction_ratio']:.2f}x (order={r['order_of_accuracy']:.2f})")
    print("Boussinesq kh=1.0 Dispersion (0 deg and 45 deg):")
    for r in suite["boussinesq_dispersion"]:
        if r["kh"] == 1.0:
            print(f"  sweeps={r['sweeps']}, deg={r['angle_deg']}: err={r['phase_speed_error_pct']:+.6f}%, residual={r['helmholtz_residual']:.2e}")
