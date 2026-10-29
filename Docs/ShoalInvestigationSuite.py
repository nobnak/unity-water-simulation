"""
Shoal Investigation Suite
Investigates the physical and numerical causes of:
1. Wave crest corrugation / sawtooth profile
2. Rapid peak height oscillations
on the 02_Boussinesq Shoal and Cape bathymetry.
"""

import math
import time
import json
import os

class ShoalSimulation:
    def __init__(self, N=128, amp=0.35, dt=0.016, alpha=1.0, sweeps=1,
                 use_advection=True, use_filter=True, fixed_parity=None, linear_flux=False):
        self.N = N
        self.L = 100.0
        self.dx = self.L / N
        self.dz = self.L / N
        self.g = 9.81
        self.amp = amp
        self.freq = 0.15
        self.dt = dt
        self.alpha = alpha
        self.sweeps = sweeps
        self.use_advection = use_advection
        self.use_filter = use_filter
        self.fixed_parity = fixed_parity
        self.linear_flux = linear_flux

        self.h_deep = 3.5
        self.h_shallow = 0.8
        self.wavemaker_y = int(N * 0.08)

        # Generate 2D Bathymetry matching ShoalAndCape
        self.depth = [0.0] * (N * N)
        for y in range(N):
            ny = y / (N - 1)
            base_slope = self.h_deep + (self.h_shallow * 1.5 - self.h_deep) * ny
            for x in range(N):
                nx = x / (N - 1)
                shoal_line = abs((nx - 0.35) - (ny - 0.45) * 0.7)
                shoal_factor = math.exp(-shoal_line * shoal_line / 0.02)
                dist_cape = math.hypot(nx - 0.72, ny - 0.62)
                cape_factor = math.exp(-dist_cape * dist_cape / 0.015)
                d = base_slope + (self.h_shallow - base_slope) * (shoal_factor * 0.75)
                d = d + (self.h_shallow * 0.5 - d) * (cape_factor * 0.85)
                self.depth[y * N + x] = max(0.5, d)

        # Substep CFL matching BoussinesqSimulation.cs
        amp_bound = max(0.1, amp * 1.5)
        h_max = max(0.5, self.h_deep + amp_bound)
        c_max = math.sqrt(self.g * h_max)
        u_bound = c_max * (amp_bound / max(0.5, self.h_shallow))
        max_wave_speed = (c_max + u_bound) * math.sqrt(2.0)
        max_dt = (self.dx / max_wave_speed) * 0.55
        self.sub_steps = max(1, math.ceil(dt / max_dt))
        if (self.sub_steps & 1) != 0:
            self.sub_steps += 1
        self.sub_dt = dt / self.sub_steps

        self.eta = [0.0] * (N * N)
        self.u = [0.0] * (N * N)
        self.w = [0.0] * (N * N)
        self.wavemaker_phase = 0.0
        self.ping_pong = False

        self.inv_dx2 = alpha / (self.dx * self.dx)
        self.inv_dz2 = alpha / (self.dz * self.dz)
        self._precompute_tridiagonal()

    def _precompute_tridiagonal(self):
        N = self.N
        self.c_prime_xn = [0.0] * (N * N)
        self.m_denom_xn = [0.0] * (N * N)
        self.a_sub_xn = [0.0] * (N * N)

        self.c_prime_xt = [0.0] * (N * N)
        self.m_denom_xt = [0.0] * (N * N)
        self.a_sub_xt = [0.0] * (N * N)

        for y in range(1, N - 1):
            row = y * N
            # Normal component: strict Dirichlet (ax = 0) at lateral walls
            self.c_prime_xn[row + 0] = 0.0
            self.m_denom_xn[row + 0] = 1.0
            for x in range(1, N - 1):
                hC = self.depth[row + x]
                hL = self.depth[row + x - 1]
                hR = self.depth[row + x + 1]
                ax = -self.inv_dx2 * (0.5 * hC * hL - (hC * hC) / 6.0)
                bx = 1.0 + self.inv_dx2 * ((2.0 / 3.0) * hC * hC)
                cx = -self.inv_dx2 * (0.5 * hC * hR - (hC * hC) / 6.0)
                self.a_sub_xn[row + x] = ax
                denom = bx - ax * self.c_prime_xn[row + x - 1]
                self.c_prime_xn[row + x] = cx / denom
                self.m_denom_xn[row + x] = 1.0 / denom
            self.m_denom_xn[row + N - 1] = 1.0

            # Tangential component: strict Neumann (d(az)/dx = 0) at lateral walls
            h0 = self.depth[row + 0]
            beta0 = (1.0 / 3.0) * h0 * h0 * self.inv_dx2
            b0 = 1.0 + 2.0 * beta0
            c0 = -2.0 * beta0
            self.c_prime_xt[row + 0] = c0 / b0
            self.m_denom_xt[row + 0] = 1.0 / b0
            for x in range(1, N - 1):
                hC = self.depth[row + x]
                hL = self.depth[row + x - 1]
                hR = self.depth[row + x + 1]
                ax = -self.inv_dx2 * (0.5 * hC * hL - (hC * hC) / 6.0)
                bx = 1.0 + self.inv_dx2 * ((2.0 / 3.0) * hC * hC)
                cx = -self.inv_dx2 * (0.5 * hC * hR - (hC * hC) / 6.0)
                self.a_sub_xt[row + x] = ax
                denom = bx - ax * self.c_prime_xt[row + x - 1]
                self.c_prime_xt[row + x] = cx / denom
                self.m_denom_xt[row + x] = 1.0 / denom
            hE = self.depth[row + N - 1]
            betaE = (1.0 / 3.0) * hE * hE * self.inv_dx2
            aE = -2.0 * betaE
            bE = 1.0 + 2.0 * betaE
            self.a_sub_xt[row + N - 1] = aE
            denomE = bE - aE * self.c_prime_xt[row + N - 2]
            self.m_denom_xt[row + N - 1] = 1.0 / denomE

        # Z tridiagonal: decoupled normal (az: Dirichlet) and tangential (ax: Neumann) at offshore/beach borders
        self.c_prime_zn = [0.0] * (N * N)
        self.m_denom_zn = [0.0] * (N * N)
        self.a_sub_zn = [0.0] * (N * N)

        self.c_prime_zt = [0.0] * (N * N)
        self.m_denom_zt = [0.0] * (N * N)
        self.a_sub_zt = [0.0] * (N * N)

        for x in range(N):
            # Normal component (az): strict Dirichlet (az = 0) at y=0 and y=N-1
            self.c_prime_zn[0 * N + x] = 0.0
            self.m_denom_zn[0 * N + x] = 1.0
            for y in range(1, N - 1):
                hC = self.depth[y * N + x]
                hD = self.depth[(y - 1) * N + x]
                hU = self.depth[(y + 1) * N + x]
                ay = -self.inv_dz2 * (0.5 * hC * hD - (hC * hC) / 6.0)
                by = 1.0 + self.inv_dz2 * ((2.0 / 3.0) * hC * hC)
                cy = -self.inv_dz2 * (0.5 * hC * hU - (hC * hC) / 6.0)
                self.a_sub_zn[y * N + x] = ay
                denom = by - ay * self.c_prime_zn[(y - 1) * N + x]
                self.c_prime_zn[y * N + x] = cy / denom
                self.m_denom_zn[y * N + x] = 1.0 / denom
            self.m_denom_zn[(N - 1) * N + x] = 1.0

            # Tangential component (ax): strict Neumann (d(ax)/dz = 0) at y=0 and y=N-1
            h0 = self.depth[0 * N + x]
            beta0 = (1.0 / 3.0) * h0 * h0 * self.inv_dz2
            b0 = 1.0 + 2.0 * beta0
            c0 = -2.0 * beta0
            self.c_prime_zt[0 * N + x] = c0 / b0
            self.m_denom_zt[0 * N + x] = 1.0 / b0
            for y in range(1, N - 1):
                hC = self.depth[y * N + x]
                hD = self.depth[(y - 1) * N + x]
                hU = self.depth[(y + 1) * N + x]
                ay = -self.inv_dz2 * (0.5 * hC * hD - (hC * hC) / 6.0)
                by = 1.0 + self.inv_dz2 * ((2.0 / 3.0) * hC * hC)
                cy = -self.inv_dz2 * (0.5 * hC * hU - (hC * hC) / 6.0)
                self.a_sub_zt[y * N + x] = ay
                denom = by - ay * self.c_prime_zt[(y - 1) * N + x]
                self.c_prime_zt[y * N + x] = cy / denom
                self.m_denom_zt[y * N + x] = 1.0 / denom
            hE = self.depth[(N - 1) * N + x]
            betaE = (1.0 / 3.0) * hE * hE * self.inv_dz2
            aE = -2.0 * betaE
            bE = 1.0 + 2.0 * betaE
            self.a_sub_zt[(N - 1) * N + x] = aE
            denomE = bE - aE * self.c_prime_zt[(N - 2) * N + x]
            self.m_denom_zt[(N - 1) * N + x] = 1.0 / denomE

        self._work_d_prime = [0.0] * N

    @staticmethod
    def smooth_depth(raw_h):
        d = raw_h - 0.05
        return 0.05 + 0.5 * (d + math.sqrt(d * d + 0.0004))

    def sponge_damp(self, x, y):
        sponge_w = 24.0 * (self.N / 512.0)
        sponge_str = 3.0
        dist = min(y, self.N - 1 - y)
        if dist < sponge_w and sponge_w > 0:
            t = 1.0 - (dist / sponge_w)
            return 1.0 / (1.0 + t * t * sponge_str * self.sub_dt * 15.0)
        return 1.0

    def get_source(self, x, y, h_val, ph):
        if self.amp <= 0.001:
            return 0.0
        dy = float(y - self.wavemaker_y)
        sigma_m = 4.0 * (100.0 / 512.0)
        dy_m = dy * self.dz
        if abs(dy_m) > 18.0 * (100.0 / 512.0):
            return 0.0
        sigma_cells = sigma_m / self.dz
        inv_sqrt_2pi_sigma = 1.0 / (2.506628 * sigma_cells * self.dz)
        gaussian = math.exp(-0.5 * (dy / sigma_cells) ** 2)
        c = math.sqrt(self.g * max(0.5, h_val))
        return 2.0 * c * self.amp * math.sin(ph) * gaussian * inv_sqrt_2pi_sigma

    def solve_tridiag_x(self, rhs_grid, is_tangential=False):
        N = self.N
        sol_grid = [0.0] * (N * N)
        c_p = self.c_prime_xt if is_tangential else self.c_prime_xn
        m_d = self.m_denom_xt if is_tangential else self.m_denom_xn
        a_s = self.a_sub_xt if is_tangential else self.a_sub_xn
        d_p = self._work_d_prime

        for y in range(1, N - 1):
            row = y * N
            d_p[0] = (rhs_grid[row + 0] if is_tangential else 0.0) * m_d[row + 0]
            for x in range(1, N - 1):
                idx = row + x
                d_p[x] = (rhs_grid[idx] - a_s[idx] * d_p[x - 1]) * m_d[idx]
            d_p[N - 1] = ((rhs_grid[row + N - 1] if is_tangential else 0.0) - a_s[row + N - 1] * d_p[N - 2]) * m_d[row + N - 1]

            sol_grid[row + N - 1] = d_p[N - 1]
            for x in range(N - 2, -1, -1):
                sol_grid[row + x] = d_p[x] - c_p[row + x] * sol_grid[row + x + 1]
        return sol_grid

    def solve_tridiag_z(self, rhs_grid, is_tangential=False):
        N = self.N
        sol_grid = [0.0] * (N * N)
        c_p = self.c_prime_zt if is_tangential else self.c_prime_zn
        m_d = self.m_denom_zt if is_tangential else self.m_denom_zn
        a_s = self.a_sub_zt if is_tangential else self.a_sub_zn
        d_p = self._work_d_prime

        for x in range(N):
            d_p[0] = (rhs_grid[0 * N + x] if is_tangential else 0.0) * m_d[0 * N + x]
            for y in range(1, N - 1):
                idx = y * N + x
                d_p[y] = (rhs_grid[idx] - a_s[idx] * d_p[y - 1]) * m_d[idx]
            d_p[N - 1] = ((rhs_grid[(N - 1) * N + x] if is_tangential else 0.0) - a_s[(N - 1) * N + x] * d_p[N - 2]) * m_d[(N - 1) * N + x]

            sol_grid[(N - 1) * N + x] = d_p[N - 1]
            for y in range(N - 2, -1, -1):
                sol_grid[y * N + x] = d_p[y] - c_p[y * N + x] * sol_grid[(y + 1) * N + x]
        return sol_grid

    def solve_dispersion(self, fx, fz):
        # 1-sweep ADI solves (I - beta_z d_z^2)(I - beta_x d_x^2) a = f
        # Decoupled normal (Dirichlet) / tangential (Neumann) slip flume walls matching Boussinesq.compute:
        # ax: normal in X (is_tangential=False), tangential in Z (is_tangential=True)
        # az: tangential in X (is_tangential=True), normal in Z (is_tangential=False)
        ax = self.solve_tridiag_z(self.solve_tridiag_x(fx, is_tangential=False), is_tangential=True)
        az = self.solve_tridiag_z(self.solve_tridiag_x(fz, is_tangential=True), is_tangential=False)

        if self.sweeps > 1:
            for s in range(1, self.sweeps):
                # Defect correction: add cross term beta_x * beta_z * d_x^2 d_z^2 a to f
                N = self.N
                fx_corr = list(fx)
                fz_corr = list(fz)
                inv_dx2 = self.inv_dx2
                inv_dz2 = self.inv_dz2

                for y in range(1, N - 1):
                    for x in range(1, N - 1):
                        idx = y * N + x
                        hC = self.depth[idx]
                        betaX = (1.0 / 3.0) * hC * hC * inv_dx2
                        betaZ = (1.0 / 3.0) * hC * hC * inv_dz2

                        # 4th cross difference
                        d2x_d2z_ax = (ax[(y+1)*N + x+1] - 2*ax[(y+1)*N + x] + ax[(y+1)*N + x-1]
                                    - 2*(ax[y*N + x+1] - 2*ax[y*N + x] + ax[y*N + x-1])
                                    + (ax[(y-1)*N + x+1] - 2*ax[(y-1)*N + x] + ax[(y-1)*N + x-1]))
                        d2x_d2z_az = (az[(y+1)*N + x+1] - 2*az[(y+1)*N + x] + az[(y+1)*N + x-1]
                                    - 2*(az[y*N + x+1] - 2*az[y*N + x] + az[y*N + x-1])
                                    + (az[(y-1)*N + x+1] - 2*az[(y-1)*N + x] + az[(y-1)*N + x-1]))

                        fx_corr[idx] += betaX * betaZ * d2x_d2z_ax
                        fz_corr[idx] += betaZ * betaX * d2x_d2z_az

                ax = self.solve_tridiag_z(self.solve_tridiag_x(fx_corr, is_tangential=False), is_tangential=True)
                az = self.solve_tridiag_z(self.solve_tridiag_x(fz_corr, is_tangential=True), is_tangential=False)

        return ax, az

    def apply_filter_2d(self, field, is_u=False, is_w=False):
        N = self.N
        # Directional-split 1D biharmonic dissipation matching Boussinesq.compute
        kappa_x = min(0.058, 12.0 * self.sub_dt)
        kappa_z = min(0.058, 12.0 * self.sub_dt)

        # Pass 1: X biharmonic filter
        mid = [0.0] * (N * N)
        for y in range(N):
            row = y * N
            mid[row + 0] = field[row + 0] - kappa_x * (2.0 * field[row + 0] - 3.0 * field[row + 1] + field[row + 2])
            mid[row + 1] = field[row + 1] - kappa_x * (-3.0 * field[row + 0] + 6.0 * field[row + 1] - 4.0 * field[row + 2] + field[row + 3])
            for x in range(2, N - 2):
                idx = row + x
                d4 = field[idx - 2] - 4.0 * field[idx - 1] + 6.0 * field[idx] - 4.0 * field[idx + 1] + field[idx + 2]
                mid[idx] = field[idx] - kappa_x * d4
            mid[row + N - 2] = field[row + N - 2] - kappa_x * (field[row + N - 4] - 4.0 * field[row + N - 3] + 6.0 * field[row + N - 2] - 3.0 * field[row + N - 1])
            mid[row + N - 1] = field[row + N - 1] - kappa_x * (field[row + N - 3] - 3.0 * field[row + N - 2] + 2.0 * field[row + N - 1])

        if is_u:
            for y in range(N):
                mid[y * N + 0] = 0.0
                mid[y * N + N - 1] = 0.0

        # Pass 2: Z biharmonic filter
        out = [0.0] * (N * N)
        for x in range(N):
            out[0 * N + x] = mid[0 * N + x] - kappa_z * (2.0 * mid[0 * N + x] - 3.0 * mid[1 * N + x] + mid[2 * N + x])
            out[1 * N + x] = mid[1 * N + x] - kappa_z * (-3.0 * mid[0 * N + x] + 6.0 * mid[1 * N + x] - 4.0 * mid[2 * N + x] + mid[3 * N + x])
            for y in range(2, N - 2):
                idx = y * N + x
                d4 = mid[(y - 2) * N + x] - 4.0 * mid[(y - 1) * N + x] + 6.0 * mid[idx] - 4.0 * mid[(y + 1) * N + x] + mid[(y + 2) * N + x]
                out[idx] = mid[idx] - kappa_z * d4
            out[(N - 2) * N + x] = mid[(N - 2) * N + x] - kappa_z * (mid[(N - 4) * N + x] - 4.0 * mid[(N - 3) * N + x] + 6.0 * mid[(N - 2) * N + x] - 3.0 * mid[(N - 1) * N + x])
            out[(N - 1) * N + x] = mid[(N - 1) * N + x] - kappa_z * (mid[(N - 3) * N + x] - 3.0 * mid[(N - 2) * N + x] + 2.0 * mid[(N - 1) * N + x])

        if is_w:
            for x in range(N):
                out[0 * N + x] = 0.0
                out[(N - 1) * N + x] = 0.0

        return out

    def step(self):
        N = self.N
        dx = self.dx
        dz = self.dz
        g = self.g
        sub_dt = self.sub_dt

        for s in range(self.sub_steps):
            if self.fixed_parity is not None:
                parity = self.fixed_parity
            else:
                parity = 1 if self.ping_pong else 0

            dir_pred = 1 if parity == 0 else -1
            signed_inv_dx_p = float(dir_pred) / dx
            signed_inv_dz_p = float(dir_pred) / dz

            eta_pred = [0.0] * (N * N)
            fx_pred = [0.0] * (N * N)
            fz_pred = [0.0] * (N * N)

            for y in range(N):
                pos_y = min(N - 1, max(0, y + dir_pred))
                row_c = y * N
                row_z = pos_y * N
                for x in range(N):
                    pos_x = min(N - 1, max(0, x + dir_pred))
                    idx_c = row_c + x
                    idx_x = row_c + pos_x
                    idx_z = row_z + x

                    hC = self.depth[idx_c]
                    hX = self.depth[idx_x]
                    hZ = self.depth[idx_z]

                    if self.linear_flux:
                        tot_hC = hC
                        tot_hX = hX
                        tot_hZ = hZ
                    else:
                        tot_hC = self.smooth_depth(hC + self.eta[idx_c])
                        tot_hX = self.smooth_depth(hX + self.eta[idx_x])
                        tot_hZ = self.smooth_depth(hZ + self.eta[idx_z])

                    uC, uX, uZ = self.u[idx_c], self.u[idx_x], self.u[idx_z]
                    wC, wX, wZ = self.w[idx_c], self.w[idx_x], self.w[idx_z]

                    dHudx = (tot_hX * uX - tot_hC * uC) * signed_inv_dx_p
                    dHwdz = (tot_hZ * wZ - tot_hC * wC) * signed_inv_dz_p
                    src_eta = self.get_source(x, y, hC, self.wavemaker_phase)
                    eta_pred[idx_c] = self.eta[idx_c] - sub_dt * (dHudx + dHwdz) + sub_dt * src_eta

                    detadx = (self.eta[idx_x] - self.eta[idx_c]) * signed_inv_dx_p
                    detadz = (self.eta[idx_z] - self.eta[idx_c]) * signed_inv_dz_p

                    if self.use_advection:
                        dudx = (uX - uC) * signed_inv_dx_p
                        dudz = (uZ - uC) * signed_inv_dz_p
                        dwdx = (wX - wC) * signed_inv_dx_p
                        dwdz = (wZ - wC) * signed_inv_dz_p
                        advX = uC * dudx + wC * dudz
                        advZ = uC * dwdx + wC * dwdz
                    else:
                        advX, advZ = 0.0, 0.0

                    fric = 0.005 / tot_hC
                    fx_pred[idx_c] = -g * detadx - advX - fric * uC
                    fz_pred[idx_c] = -g * detadz - advZ - fric * wC

            if self.alpha > 0.001:
                ax_pred, az_pred = self.solve_dispersion(fx_pred, fz_pred)
                u_pred = [0.0] * (N * N)
                w_pred = [0.0] * (N * N)
                for i in range(N * N):
                    damp = self.sponge_damp(i % N, i // N)
                    eta_pred[i] = max(-15.0, min(15.0, eta_pred[i] * damp))
                    u_pred[i] = max(-30.0, min(30.0, (self.u[i] + sub_dt * ax_pred[i]) * damp))
                    w_pred[i] = max(-30.0, min(30.0, (self.w[i] + sub_dt * az_pred[i]) * damp))
            else:
                ax_pred, az_pred = fx_pred, fz_pred
                u_pred = [0.0] * (N * N)
                w_pred = [0.0] * (N * N)
                for i in range(N * N):
                    damp = self.sponge_damp(i % N, i // N)
                    eta_pred[i] = max(-15.0, min(15.0, eta_pred[i] * damp))
                    u_pred[i] = max(-30.0, min(30.0, (self.u[i] + sub_dt * fx_pred[i]) * damp))
                    w_pred[i] = max(-30.0, min(30.0, (self.w[i] + sub_dt * fz_pred[i]) * damp))

            for y in range(N):
                u_pred[y * N + 0] = 0.0
                u_pred[y * N + N - 1] = 0.0
            for x in range(N):
                w_pred[0 * N + x] = 0.0
                w_pred[(N - 1) * N + x] = 0.0

            # Corrector step
            dir_corr = -1 if parity == 0 else 1
            signed_inv_dx_c = float(dir_corr) / dx
            signed_inv_dz_c = float(dir_corr) / dz
            next_phase = self.wavemaker_phase + 2.0 * math.pi * self.freq * sub_dt

            eta_next = [0.0] * (N * N)
            fx_star = [0.0] * (N * N)
            fz_star = [0.0] * (N * N)

            for y in range(N):
                pos_y = min(N - 1, max(0, y + dir_corr))
                row_c = y * N
                row_z = pos_y * N
                for x in range(N):
                    pos_x = min(N - 1, max(0, x + dir_corr))
                    idx_c = row_c + x
                    idx_x = row_c + pos_x
                    idx_z = row_z + x

                    hC = self.depth[idx_c]
                    hX = self.depth[idx_x]
                    hZ = self.depth[idx_z]

                    if self.linear_flux:
                        tot_hC = hC
                        tot_hX = hX
                        tot_hZ = hZ
                    else:
                        tot_hC = self.smooth_depth(hC + eta_pred[idx_c])
                        tot_hX = self.smooth_depth(hX + eta_pred[idx_x])
                        tot_hZ = self.smooth_depth(hZ + eta_pred[idx_z])

                    uC, uX, uZ = u_pred[idx_c], u_pred[idx_x], u_pred[idx_z]
                    wC, wX, wZ = w_pred[idx_c], w_pred[idx_x], w_pred[idx_z]

                    dHudx = (tot_hX * uX - tot_hC * uC) * signed_inv_dx_c
                    dHwdz = (tot_hZ * wZ - tot_hC * wC) * signed_inv_dz_c
                    src_eta = self.get_source(x, y, hC, next_phase)
                    damp = self.sponge_damp(x, y)
                    eta_next[idx_c] = max(-15.0, min(15.0, 0.5 * (self.eta[idx_c] + eta_pred[idx_c] - sub_dt * (dHudx + dHwdz) + sub_dt * src_eta) * damp))

                    detadx = (eta_pred[idx_x] - eta_pred[idx_c]) * signed_inv_dx_c
                    detadz = (eta_pred[idx_z] - eta_pred[idx_c]) * signed_inv_dz_c

                    if self.use_advection:
                        dudx = (uX - uC) * signed_inv_dx_c
                        dudz = (uZ - uC) * signed_inv_dz_c
                        dwdx = (wX - wC) * signed_inv_dx_c
                        dwdz = (wZ - wC) * signed_inv_dz_c
                        advX = uC * dudx + wC * dudz
                        advZ = uC * dwdx + wC * dwdz
                    else:
                        advX, advZ = 0.0, 0.0

                    fric = 0.005 / tot_hC
                    fx_star[idx_c] = -g * detadx - advX - fric * uC
                    fz_star[idx_c] = -g * detadz - advZ - fric * wC

            if self.alpha > 0.001:
                ax_star, az_star = self.solve_dispersion(fx_star, fz_star)
                u_next = [0.0] * (N * N)
                w_next = [0.0] * (N * N)
                for i in range(N * N):
                    a_avg_x = 0.5 * (ax_pred[i] + ax_star[i])
                    a_avg_z = 0.5 * (az_pred[i] + az_star[i])
                    damp = self.sponge_damp(i % N, i // N)
                    u_next[i] = max(-30.0, min(30.0, (self.u[i] + sub_dt * a_avg_x) * damp))
                    w_next[i] = max(-30.0, min(30.0, (self.w[i] + sub_dt * a_avg_z) * damp))
            else:
                u_next = [0.0] * (N * N)
                w_next = [0.0] * (N * N)
                for i in range(N * N):
                    damp = self.sponge_damp(i % N, i // N)
                    u_next[i] = max(-30.0, min(30.0, 0.5 * (self.u[i] + u_pred[i] + sub_dt * fx_star[i]) * damp))
                    w_next[i] = max(-30.0, min(30.0, 0.5 * (self.w[i] + w_pred[i] + sub_dt * fz_star[i]) * damp))

            for y in range(N):
                u_next[y * N + 0] = 0.0
                u_next[y * N + N - 1] = 0.0
            for x in range(N):
                w_next[0 * N + x] = 0.0
                w_next[(N - 1) * N + x] = 0.0

            if self.use_filter:
                self.eta = self.apply_filter_2d(eta_next)
                self.u = self.apply_filter_2d(u_next, is_u=True)
                self.w = self.apply_filter_2d(w_next, is_w=True)
            else:
                self.eta = eta_next
                self.u = u_next
                self.w = w_next

            self.wavemaker_phase += 2.0 * math.pi * self.freq * sub_dt
            self.ping_pong = not self.ping_pong


def run_experiment(name, **kwargs):
    print(f"--- Running Experiment: {name} ---")
    sim = ShoalSimulation(**kwargs)
    total_time = 12.0
    dt = kwargs.get('dt', 0.016)
    total_steps = int(round(total_time / dt))
    N = sim.N

    # Probe indices
    idx_offshore = int(0.15 * (N - 1)) * N + (N // 2)
    idx_slope = int(0.35 * (N - 1)) * N + (N // 2)
    idx_shoal = int(0.52 * (N - 1)) * N + (N // 2)
    idx_cape = int(0.62 * (N - 1)) * N + int(0.72 * (N - 1))

    time_series = []
    peak_track = []
    prev_peak_eta = 0.0
    max_peak_jump = 0.0
    t0 = time.time()

    for step in range(total_steps):
        t = step * dt
        sim.step()

        # Centerline crest tracker
        cx = N // 2
        local_max = -999.0
        local_py = -1
        for y in range(int(0.25 * N), int(0.75 * N)):
            val = sim.eta[y * N + cx]
            if val > local_max:
                local_max = val
                local_py = y

        norm_py = local_py / (N - 1)
        if 0.35 <= norm_py <= 0.65 and step > 10:
            jump = abs(local_max - prev_peak_eta)
            if jump > max_peak_jump:
                max_peak_jump = jump
        prev_peak_eta = local_max

        # Wavefront sawtoothing along crest line at local_py
        max_d2_crest = 0.0
        if local_py > 0:
            row = local_py * N
            for x in range(1, N - 1):
                d2 = abs(sim.eta[row + x + 1] - 2 * sim.eta[row + x] + sim.eta[row + x - 1])
                if d2 > max_d2_crest:
                    max_d2_crest = d2

        if step % 5 == 0:
            time_series.append({
                "t": round(t, 4),
                "offshore_eta": round(sim.eta[idx_offshore], 5),
                "slope_eta": round(sim.eta[idx_slope], 5),
                "shoal_eta": round(sim.eta[idx_shoal], 5),
                "cape_eta": round(sim.eta[idx_cape], 5),
                "peak_y": local_py,
                "peak_eta": round(local_max, 5),
                "peak_depth": round(sim.depth[local_py * N + cx], 3),
                "sawtooth_d2": round(max_d2_crest, 6)
            })

    # Crest slice across shoal at t=10s
    crest_y = int(0.52 * (N - 1))
    crest_slice = [round(sim.eta[crest_y * N + x], 5) for x in range(N)]
    prop_slice = [round(sim.eta[y * N + (N // 2)], 5) for y in range(N)]

    elapsed = time.time() - t0
    print(f"Done in {elapsed:.1f}s. Max Peak Jump: {max_peak_jump:.5f}m, Max Wavefront Sawtooth: {max(ts['sawtooth_d2'] for ts in time_series):.6f}")

    return {
        "name": name,
        "elapsed_s": round(elapsed, 2),
        "sub_steps": sim.sub_steps,
        "sub_dt": round(sim.sub_dt, 5),
        "dx": round(sim.dx, 4),
        "max_peak_jump": round(max_peak_jump, 5),
        "max_sawtooth_d2": round(max(ts['sawtooth_d2'] for ts in time_series), 6),
        "time_series": time_series,
        "crest_slice": crest_slice,
        "propagation_slice": prop_slice
    }


def _run_worker(item):
    name, params = item
    return name, run_experiment(name, **params)

def main():
    os.makedirs("Docs/ShoalInvestigation", exist_ok=True)
    all_results = {}

    configs = [
        ("Baseline",           dict(N=64, amp=0.35, dt=0.016, alpha=1.0, sweeps=1)),
        ("Amp_Half",           dict(N=64, amp=0.175, dt=0.016, alpha=1.0, sweeps=1)),
        ("Amp_Quarter",        dict(N=64, amp=0.08, dt=0.016, alpha=1.0, sweeps=1)),
        ("Dt_Half",            dict(N=64, amp=0.35, dt=0.008, alpha=1.0, sweeps=1)),
        ("Res_128",            dict(N=128, amp=0.35, dt=0.016, alpha=1.0, sweeps=1)),
        ("No_Advection",       dict(N=64, amp=0.35, dt=0.016, alpha=1.0, sweeps=1, use_advection=False)),
        ("Dispersion_Off",     dict(N=64, amp=0.35, dt=0.016, alpha=0.0, sweeps=1)),
        ("No_Filter",          dict(N=64, amp=0.35, dt=0.016, alpha=1.0, sweeps=1, use_filter=False)),
        ("Parity_Fixed",       dict(N=64, amp=0.35, dt=0.016, alpha=1.0, sweeps=1, fixed_parity=0)),
        ("Sweeps_4",           dict(N=64, amp=0.35, dt=0.016, alpha=1.0, sweeps=4)),
        ("Linear_Flux",        dict(N=64, amp=0.35, dt=0.016, alpha=1.0, sweeps=1, linear_flux=True)),
    ]

    try:
        from multiprocessing import Pool
        with Pool() as pool:
            results_list = pool.map(_run_worker, configs)
            for name, res in results_list:
                all_results[name] = res
    except Exception:
        for name, params in configs:
            res = run_experiment(name, **params)
            all_results[name] = res

    out_file = "Docs/ShoalInvestigation/InvestigationResults.json"
    with open(out_file, "w", encoding="utf-8") as f:
        json.dump(all_results, f, indent=2)

    print(f"\nAll experiments complete! Results saved to {out_file}")

    # Print Comparison Table
    print("\n" + "=" * 80)
    print(f"{'Experiment':<18} | {'SubSteps':<8} | {'MaxPeakJump (m)':<16} | {'MaxSawtooth d2':<16} | {'Notes'}")
    print("-" * 80)
    for name, res in all_results.items():
        print(f"{name:<18} | {res['sub_steps']:<8} | {res['max_peak_jump']:<16.5f} | {res['max_sawtooth_d2']:<16.6f} | dt={res['sub_dt']}")
    print("=" * 80)

if __name__ == "__main__":
    main()
