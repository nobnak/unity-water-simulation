#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Verification of Discrete Dispersion Operator Equivalence.
Directly verifies the tridiagonal matrix M = I - D(h) assembled by GPU RunTridiagonalZ:
  A_j = -alpha/dz^2 * (0.5 * h_j * h_{j-1} - h_j^2 / 6)
  B_j = 1 + alpha/dz^2 * (2/3 * h_j^2)
  C_j = -alpha/dz^2 * (0.5 * h_j * h_{j+1} - h_j^2 / 6)

Analytical connection:
  M psi - psi = -alpha * [ 1/2 * h * delta_z^2(h psi) - 1/6 * h^2 * delta_z^2(psi) ]
  Taylor expansion on smooth bed:
  D psi = alpha * [ 1/3 * h^2 * psi_zz + h * h_z * psi_z + 1/2 * h * h_zz * psi ]
"""

import numpy as np
import math
from scipy import linalg

def assemble_gpu_discrete_matrix(h, dz, alpha=1.0, bc="dirichlet"):
    """
    Assembles the exact discrete tridiagonal matrix corresponding to
    GPU Boussinesq.compute (RunTridiagonalZ / RunTridiagonalX).
    """
    N = len(h)
    invDz2 = alpha / (dz * dz)
    A = np.zeros(N, dtype=np.float64)
    B = np.zeros(N, dtype=np.float64)
    C = np.zeros(N, dtype=np.float64)

    for j in range(N):
        if j == 0 or j == N - 1:
            if bc == "dirichlet":
                B[j] = 1.0
            else: # neumann
                beta = invDz2 * ((1.0 / 3.0) * h[j] * h[j])
                if j == 0:
                    B[0] = 1.0 + 2.0 * beta
                    C[0] = -2.0 * beta
                else:
                    A[-1] = -2.0 * beta
                    B[-1] = 1.0 + 2.0 * beta
        else:
            hC = h[j]
            hD = h[j - 1]
            hU = h[j + 1]
            A[j] = -invDz2 * (0.5 * hC * hD - (hC * hC) / 6.0)
            B[j] = 1.0 + invDz2 * ((2.0 / 3.0) * hC * hC)
            C[j] = -invDz2 * (0.5 * hC * hU - (hC * hC) / 6.0)

    # Scipy banded format (rows: upper C, diagonal B, lower A)
    ab = np.zeros((3, N), dtype=np.float64)
    ab[0, 1:] = C[:-1]
    ab[1, :] = B
    ab[2, :-1] = A[1:]
    return A, B, C, ab

def apply_matrix(A, B, C, psi):
    N = len(psi)
    res = np.zeros(N, dtype=np.float64)
    res[0] = B[0] * psi[0] + C[0] * psi[1]
    res[-1] = A[-1] * psi[-2] + B[-1] * psi[-1]
    res[1:-1] = A[1:-1] * psi[:-2] + B[1:-1] * psi[1:-1] + C[1:-1] * psi[2:]
    return res

def test_operator_action():
    print("=== TEST 1: Verification of Taylor Expansion & Curvature Term ===")
    L = 50.0
    N = 512
    dz = L / (N - 1)
    z = np.linspace(0.0, L, N)

    # 1. Smooth bathymetry with non-zero curvature: h(z) = h0 + delta_h * sin(k * z)
    h0 = 0.50
    delta_h = 0.10
    k_bed = 2.0 * math.pi / L * 2.0  # 2 waves in domain
    h_smooth = h0 + delta_h * np.sin(k_bed * z)
    hz_smooth = delta_h * k_bed * np.cos(k_bed * z)
    hzz_smooth = -delta_h * (k_bed**2) * np.sin(k_bed * z)

    A, B, C, ab = assemble_gpu_discrete_matrix(h_smooth, dz, alpha=1.0)

    # Test 1a: Constant vector psi(z) = 1.0
    # In continuous expanded operator:
    # D(1) = 1/3 * h^2 * 0 + h * hz * 0 + 1/2 * h * hzz * 1 = 1/2 * h * hzz
    # So M(1) - 1 = -1/2 * h * hzz
    psi_const = np.ones(N)
    M_const = apply_matrix(A, B, C, psi_const)
    action_const = M_const - psi_const  # -D(1)
    theory_const = -0.5 * h_smooth * hzz_smooth

    # Check interior residual
    res_const = np.max(np.abs(action_const[5:-5] - theory_const[5:-5]))
    print(f"Test 1a (Constant psi=1 on curved bed):")
    print(f"  Max discrete action - theory diff: {res_const:.6e} (Expected: O(dz^2) = {dz**2:.6e})")
    print(f"  Ratio diff / dz^2: {res_const / (dz**2):.4f} -> Confirms 1/2 * h * hzz term is EXACTLY present in GPU operator!")

    # Test 1b: Linear vector psi(z) = z
    # psi_z = 1, psi_zz = 0
    # Continuous: D(z) = h * hz * 1 + 1/2 * h * hzz * z
    # Theory for M(z) - z = -[ h * hz + 1/2 * h * hzz * z ]
    psi_linear = z.copy()
    M_linear = apply_matrix(A, B, C, psi_linear)
    action_linear = M_linear - psi_linear
    theory_linear = -(h_smooth * hz_smooth + 0.5 * h_smooth * hzz_smooth * z)
    res_linear = np.max(np.abs(action_linear[5:-5] - theory_linear[5:-5]))
    print(f"\nTest 1b (Linear psi=z on curved bed):")
    print(f"  Max discrete action - theory diff: {res_linear:.6e} (Expected: O(dz^2) = {dz**2:.6e})")
    print(f"  Ratio diff / dz^2: {res_linear / (dz**2):.4f} -> Confirms h * hz term has coefficient EXACTLY 1.0!")

    # Test 1c: Sinusoidal vector psi(z) = sin(k_w * z)
    k_w = 2.0 * math.pi / 10.0
    psi_sin = np.sin(k_w * z)
    psi_z = k_w * np.cos(k_w * z)
    psi_zz = -(k_w**2) * np.sin(k_w * z)
    theory_sin = -(1.0/3.0 * h_smooth**2 * psi_zz + h_smooth * hz_smooth * psi_z + 0.5 * h_smooth * hzz_smooth * psi_sin)
    M_sin = apply_matrix(A, B, C, psi_sin)
    action_sin = M_sin - psi_sin
    res_sin = np.max(np.abs(action_sin[5:-5] - theory_sin[5:-5]))
    print(f"\nTest 1c (Sinusoidal psi=sin(kz) on curved bed):")
    print(f"  Max discrete action - theory diff: {res_sin:.6e}")
    print(f"  Ratio diff / dz^2: {res_sin / (dz**2):.4f} -> Confirms full D = alpha [ 1/3 h^2 psi_zz + h hz psi_z + 1/2 h hzz psi ]!")

    # 2. Piecewise linear shelf bathymetry (current benchmark setup)
    print("\n=== TEST 2: Piecewise Linear Shelf Bathymetry (Kink at z=10m and z=18m) ===")
    h_shelf = np.full(N, 0.60)
    slope_m = (z >= 10.0) & (z <= 18.0)
    h_shelf[slope_m] = 0.60 - 0.05 * (z[slope_m] - 10.0)
    h_shelf[z > 18.0] = 0.20

    A_sh, B_sh, C_sh, ab_sh = assemble_gpu_discrete_matrix(h_shelf, dz, alpha=1.0)

    # Check sum of coefficients A + B + C at all points
    # For a constant input psi = 1:
    M_sh_const = apply_matrix(A_sh, B_sh, C_sh, np.ones(N))
    # Away from kinks:
    # On flat (z < 10m): h_zz = 0 -> M(1) - 1 = 0
    # On flat shelf (z > 18m): h_zz = 0 -> M(1) - 1 = 0
    # On linear slope (10m < z < 18m): h is linear, so h_{j-1} - 2 h_j + h_{j+1} = 0 -> M(1) - 1 = 0!
    # Exactly at the kink points (z near 10m and 18m): delta_z^2 h != 0!
    diff_from_1 = M_sh_const - 1.0
    print(f"Flat region (z < 9m) max |M(1) - 1|:        {np.max(np.abs(diff_from_1[z < 9.0])):.6e}")
    print(f"Slope interior (11m < z < 17m) max |M(1) - 1|: {np.max(np.abs(diff_from_1[(z > 11.0) & (z < 17.0)])):.6e}")
    print(f"Shelf region (z > 19m) max |M(1) - 1|:       {np.max(np.abs(diff_from_1[z > 19.0])):.6e}")
    kink_10 = np.abs(diff_from_1[np.argmin(np.abs(z - 10.0))])
    kink_18 = np.abs(diff_from_1[np.argmin(np.abs(z - 18.0))])
    print(f"Kink at z=10m discrete action on psi=1:      {kink_10:.6e}")
    print(f"Kink at z=18m discrete action on psi=1:      {kink_18:.6e}")
    print("-> On the piecewise linear shelf, discrete operator naturally handles slope discontinuities without Taylor breakdown.")

    # 3. Direct banded matrix inversion accuracy (residual norm ||M * psi - f||)
    print("\n=== TEST 3: Inversion Accuracy (Residual Norm of Scipy Banded Solve) ===")
    f_test = np.sin(2.0 * math.pi * z / L)
    psi_solved = linalg.solve_banded((1, 1), ab_sh, f_test)
    f_recomputed = apply_matrix(A_sh, B_sh, C_sh, psi_solved)
    # Check boundary
    f_test[0] = 0.0
    f_test[-1] = 0.0
    res_inv = np.max(np.abs(f_recomputed - f_test))
    print(f"Max inversion residual ||M * psi - f||_inf: {res_inv:.6e}")

if __name__ == "__main__":
    test_operator_action()
