"""
Numerical Verification Suite for unity-water-simulation
Reproducible verification of dispersion operators, spatial filter, boundary conditions,
and time integration convergence.

Run: python Docs/NumericalVerificationSuite.py
"""
import math
import json

def verify_boussinesq_dispersion():
    """Point 1: Direction-split ADI Peregrine vs 2D standard Peregrine continuum gain and defect correction."""
    results = []
    for kh in [0.5, 1.0, 1.5]:
        target = 1.0 / (1.0 + (kh**2) / 3.0)
        c_target = math.sqrt(target)
        for sweeps in [1, 2, 3]:
            for deg in [0, 45]:
                rad = math.radians(deg)
                kx_h = kh * math.cos(rad)
                kz_h = kh * math.sin(rad)
                g_x = 1.0 / (1.0 + (kx_h**2) / 3.0)
                g_z = 1.0 / (1.0 + (kz_h**2) / 3.0)
                g_adi = g_x * g_z

                # Defect correction iterative gain
                g = 0.0
                for _ in range(sweeps):
                    res = 1.0 - (1.0 + (kh**2) / 3.0) * g
                    g = g + g_adi * res

                c_actual = math.sqrt(g)
                rel_err = (c_actual / c_target - 1.0) * 100.0
                results.append({
                    "kh": kh,
                    "angle_deg": deg,
                    "sweeps": sweeps,
                    "peregrine_gain": target,
                    "implemented_gain": g,
                    "phase_speed_error_pct": rel_err
                })
    return results

def verify_filter():
    """Point 2: Time-step scaled damping and exact mass conservation of zero-flux biharmonic stencil."""
    modes = []
    for cells in [8, 16, 32, 64]:
        q = 2.0 * math.pi / cells
        eig = 16.0 * (math.sin(q / 2.0) ** 4)
        for dt in [0.008, 0.004, 0.002]:
            kappa = 0.025 * (dt / 0.008)
            per_step = 1.0 - kappa * eig
            steps = round(1.0 / dt)
            amp_1s = per_step ** steps
            modes.append({
                "cells_per_wavelength": cells,
                "dt": dt,
                "steps_per_sec": steps,
                "per_substep_gain": per_step,
                "amplitude_after_1sec": amp_1s
            })

    # Conservative zero-flux boundary test
    w = 32
    # Single impulse test
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
    import random
    random.seed(42)
    rand_field = [random.uniform(-10.0, 10.0) for _ in range(w)]
    d4_rand = [0.0] * w
    d4_rand[0] = 2.0 * rand_field[0] - 3.0 * rand_field[1] + rand_field[2]
    d4_rand[1] = -3.0 * rand_field[0] + 6.0 * rand_field[1] - 4.0 * rand_field[2] + rand_field[3]
    for x in range(2, w - 2):
        d4_rand[x] = rand_field[x-2] - 4.0 * rand_field[x-1] + 6.0 * rand_field[x] - 4.0 * rand_field[x+1] + rand_field[x+2]
    d4_rand[w-2] = rand_field[w-4] - 4.0 * rand_field[w-3] + 6.0 * rand_field[w-2] - 3.0 * rand_field[w-1]
    d4_rand[w-1] = rand_field[w-3] - 3.0 * rand_field[w-2] + 2.0 * rand_field[w-1]

    return {
        "periodic_modes": modes,
        "impulse_mass_before": float(sum(s)),
        "impulse_mass_after": float(sum(s) - 0.025 * sum(d4_impulse)),
        "impulse_d4_sum": float(sum(d4_impulse)),
        "random_field_d4_sum": float(sum(d4_rand))
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
    """Point 5: Decoupled normal (Dirichlet) and tangential (Neumann) sidewall boundary conditions via PCR."""
    n, beta = 32, 10.0

    # Solve normal via PCR:
    a_n = [0.0]*n; b_n = [1.0 + 2.0*beta]*n; c_n = [-beta]*n; d_n = [1.0]*n
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
    a_t = [0.0]*n; b_t = [1.0 + 2.0*beta]*n; c_t = [-beta]*n; d_t = [1.0]*n
    a_t[0] = 0.0; b_t[0] = 1.0 + 2.0*beta; c_t[0] = -2.0*beta; d_t[0] = 1.0
    a_t[-1] = -2.0*beta; b_t[-1] = 1.0 + 2.0*beta; c_t[-1] = 0.0; d_t[-1] = 1.0

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
        "tangential_acceleration_interior": float(acc_tan[1])
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
        "relative_depth_regimes": verify_relative_depth_regimes()
    }
    out_path = r"c:\Users\nakata\Repo\unity-water-simulation\Docs\NumericalVerificationResults.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(suite, f, indent=2)

    print(f"=== Verification Suite Completed. Saved to {out_path} ===")
    print(f"Normal Wall Accel: {suite['slip_boundary']['normal_acceleration_at_wall']:.8f} (intended: 0.0)")
    print(f"Filter Impulse Mass Change: {suite['filter']['impulse_mass_after'] - suite['filter']['impulse_mass_before']:.2e}")
    print(f"Filter Random Field d4 Sum: {suite['filter']['random_field_d4_sum']:.2e}")
    print("Wave Time Convergence:")
    for r in suite["wave_time_convergence"]:
        print(f"  dt={r['dt']:.3f}: error={r['error_l2']:.4f}, reduction={r['error_reduction_ratio']:.2f}x (order={r['order_of_accuracy']:.2f})")
    print("Boussinesq kh=1.0 Dispersion:")
    for r in suite["boussinesq_dispersion"]:
        if r["kh"] == 1.0:
            print(f"  sweeps={r['sweeps']}, deg={r['angle_deg']}: err={r['phase_speed_error_pct']:+.4f}%")
