#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Vectorized 1D MacCormack simulation to diagnose shoaling height.
Isolates convective advection scheme holding grid, time integration,
continuity, discrete dispersion solver, and bathymetry strictly identical.

Filter notation:
- filter_mode="none": Raw solver (no artificial filter)
- filter_mode="unconstrained_dissipation_4th": Unconstrained 4th-order dissipation filter
  (NOTE: This is unconstrained high-frequency damping, NOT the GPU Flux Corrected Transport).
"""

import numpy as np
import math
from scipy import linalg

g = 9.81

def run_isolated_shoaling_test(N=512, L=50.0, dt=0.004, duration=14.0,
                               advection_mode="upwind",
                               filter_mode="none"):
    """
    Runs 1D Boussinesq simulation with isolated advection & filter settings.
    advection_mode:
      - "upwind": 1st-order upwind advection (used in GPU CSUpdateForces)
      - "maccormack_directional": 2nd-order alternating directional derivative
      - "none": advection term omitted (linearized momentum)
    filter_mode:
      - "none": No artificial dissipation
      - "unconstrained_dissipation_4th": Unconstrained 4th-order biharmonic dissipation
    """
    dz = L / N
    z = (np.arange(N) + 0.5) * dz
    
    h = np.full(N, 0.60)
    slope_mask = (z > 10.0) & (z <= 18.0)
    h[slope_mask] = 0.60 - 0.05 * (z[slope_mask] - 10.0)
    h[z > 18.0] = 0.20

    # Initial condition (matched exactly)
    a0 = 0.12
    z0 = 5.0
    h1 = 0.60
    c0 = math.sqrt(g * (h1 + a0))
    kappa = math.sqrt(3.0 * a0 / (4.0 * h1**3))
    
    arg = kappa * (z - z0)
    sech = 1.0 / np.cosh(np.clip(arg, -25.0, 25.0))
    eta = a0 * sech**2
    w = c0 * eta / (h + eta)
    
    initial_mass = np.sum(eta) * dz
    
    # Exact discrete tridiagonal matrix matching GPU RunTridiagonalZ
    invDz2 = 1.0 / (dz * dz)
    A = np.zeros(N)
    B = np.ones(N)
    C = np.zeros(N)
    
    hC = h[1:-1]
    hD = h[:-2]
    hU = h[2:]
    A[1:-1] = -invDz2 * (0.5 * hC * hD - hC**2 / 6.0)
    B[1:-1] = 1.0 + invDz2 * (2.0 / 3.0 * hC**2)
    C[1:-1] = -invDz2 * (0.5 * hC * hU - hC**2 / 6.0)

    ab = np.zeros((3, N))
    ab[0, 1:] = C[:-1]
    ab[1, :] = B
    ab[2, :-1] = A[1:]
    
    steps = int(round(duration / dt))
    parity = 0
    
    for step in range(steps):
        # 1. Predictor
        dir_val = 1 if parity == 0 else -1
        
        if dir_val == 1:
            eta_nbr = np.append(eta[1:], eta[-1])
            w_nbr = np.append(w[1:], w[-1])
            h_nbr = np.append(h[1:], h[-1])
        else:
            eta_nbr = np.insert(eta[:-1], 0, eta[0])
            w_nbr = np.insert(w[:-1], 0, w[0])
            h_nbr = np.insert(h[:-1], 0, h[0])
            
        totalH = h + eta
        totalH_nbr = h_nbr + eta_nbr
        
        dHw_dz = (totalH_nbr * w_nbr - totalH * w) * (dir_val / dz)
        eta_pred = eta - dt * dHw_dz
        deta_dz = (eta_nbr - eta) * (dir_val / dz)
        
        # Convective advection evaluation
        if advection_mode == "upwind":
            w_left = np.insert(w[:-1], 0, w[0])
            w_right = np.append(w[1:], w[-1])
            dw_dz = np.where(w >= 0.0, (w - w_left) / dz, (w_right - w) / dz)
            advect = w * dw_dz
        elif advection_mode == "maccormack_directional":
            # Directional forward/backward difference matching predictor direction
            advect = w * (w_nbr - w) * (dir_val / dz)
        elif advection_mode == "none":
            advect = 0.0
        else:
            raise ValueError(f"Unknown advection_mode: {advection_mode}")
        
        f_pred = -g * deta_dz - advect
        f_pred[0] = 0.0
        f_pred[-1] = 0.0
        
        a_pred = linalg.solve_banded((1, 1), ab, f_pred)
        w_pred = w + dt * a_pred
        w_pred[0] = 0.0
        w_pred[-1] = 0.0
        
        # 2. Corrector
        dir_c = -dir_val
        if dir_c == 1:
            eta_pred_nbr = np.append(eta_pred[1:], eta_pred[-1])
            w_pred_nbr = np.append(w_pred[1:], w_pred[-1])
            h_nbr_c = np.append(h[1:], h[-1])
        else:
            eta_pred_nbr = np.insert(eta_pred[:-1], 0, eta_pred[0])
            w_pred_nbr = np.insert(w_pred[:-1], 0, w_pred[0])
            h_nbr_c = np.insert(h[:-1], 0, h[0])
            
        totalH_pred = h + eta_pred
        totalH_pred_nbr = h_nbr_c + eta_pred_nbr
        
        dHw_dz_star = (totalH_pred_nbr * w_pred_nbr - totalH_pred * w_pred) * (dir_c / dz)
        eta_next = 0.5 * (eta + eta_pred - dt * dHw_dz_star)
        deta_dz_star = (eta_pred_nbr - eta_pred) * (dir_c / dz)
        
        if advection_mode == "upwind":
            w_p_left = np.insert(w_pred[:-1], 0, w_pred[0])
            w_p_right = np.append(w_pred[1:], w_pred[-1])
            dw_dz_star = np.where(w_pred >= 0.0, (w_pred - w_p_left) / dz, (w_p_right - w_pred) / dz)
            advect_star = w_pred * dw_dz_star
        elif advection_mode == "maccormack_directional":
            advect_star = w_pred * (w_pred_nbr - w_pred) * (dir_c / dz)
        elif advection_mode == "none":
            advect_star = 0.0
            
        f_corr = -g * deta_dz_star - advect_star
        f_corr[0] = 0.0
        f_corr[-1] = 0.0
        
        a_corr = linalg.solve_banded((1, 1), ab, f_corr)
        w_next = w + dt * 0.5 * (a_pred + a_corr)
        w_next[0] = 0.0
        w_next[-1] = 0.0
        
        # 3. Spatial filter (unconstrained 4th-order dissipation)
        if filter_mode == "unconstrained_dissipation_4th":
            kappa4 = min(0.058, 12.0 * dt)
            d4_eta = np.zeros(N)
            d4_eta[2:-2] = eta_next[4:] - 4*eta_next[3:-1] + 6*eta_next[2:-2] - 4*eta_next[1:-3] + eta_next[:-4]
            d4_w = np.zeros(N)
            d4_w[2:-2] = w_next[4:] - 4*w_next[3:-1] + 6*w_next[2:-2] - 4*w_next[1:-3] + w_next[:-4]
            eta_next -= kappa4 * d4_eta
            w_next -= kappa4 * d4_w
            
        eta = eta_next
        w = w_next
        parity = 1 - parity

    # Metrics
    final_mass = np.sum(eta) * dz
    mass_err = abs(final_mass - initial_mass) / initial_mass
    
    # Shelf peaks (z >= 18m)
    shelf_mask = z >= 18.0
    z_shelf = z[shelf_mask]
    eta_shelf = eta[shelf_mask]
    
    # 2-grid oscillation index (roughness)
    d2_eta = np.abs(eta[2:] - 2*eta[1:-1] + eta[:-2])
    roughness = float(np.mean(d2_eta))
    
    max_idx = np.argmax(eta_shelf)
    lead_z = float(z_shelf[max_idx])
    lead_h = float(eta_shelf[max_idx])

    return {
        'z': z,
        'eta': eta,
        'w': w,
        'lead_z': lead_z,
        'lead_h': lead_h,
        'mass_err': float(mass_err),
        'roughness': roughness
    }

def main():
    print("================================================================================")
    print("CONTROLLED ISOLATION EXPERIMENT: Convective Advection & Filter Modes (N=512, dt=0.004s)")
    print("================================================================================")
    
    cases = [
        ("Case 1: MacCormack Directional One-Sided Advection (Alternating Forward/Backward, Combined 2nd-Order), No Filter", "maccormack_directional", "none"),
        ("Case 2: 1st-Order Upwind Advection (Direction-Fixed Single-Stage 1st-Order), No Filter", "upwind", "none"),
        ("Case 3: 1st-Order Upwind Advection + Unconstrained 4th Dissipation", "upwind", "unconstrained_dissipation_4th"),
        ("Case 4: No Advection (Linear Momentum w*w_z = 0), No Filter", "none", "none")
    ]
    
    results = {}
    for label, adv, flt in cases:
        res = run_isolated_shoaling_test(N=512, L=50.0, dt=0.004, duration=14.0,
                                        advection_mode=adv, filter_mode=flt)
        results[label] = res
        print(f"\n{label}:")
        print(f"  Lead peak position: z = {res['lead_z']:.2f} m")
        print(f"  Lead peak height:   H = {res['lead_h']:.4f} m")
        print(f"  Mass conservation error: {res['mass_err']:.4e}")
        print(f"  High-frequency roughness: {res['roughness']:.4e}")
        
    print("\n--- Summary of Advection Effect holding all other operators fixed ---")
    h_mc = results[cases[0][0]]['lead_h']
    h_up = results[cases[1][0]]['lead_h']
    h_flt = results[cases[2][0]]['lead_h']
    
    diff_adv = h_mc - h_up
    diff_flt = h_up - h_flt
    print(f"Delta H due to Convective Advection (MacCormack directional vs Upwind): {diff_adv:+.4f} m ({(diff_adv/h_up)*100:+.1f}%)")
    print(f"Delta H due to Unconstrained 4th Dissipation:                         {diff_flt:+.4f} m ({(diff_flt/h_up)*100:+.1f}%)")

if __name__ == "__main__":
    main()
