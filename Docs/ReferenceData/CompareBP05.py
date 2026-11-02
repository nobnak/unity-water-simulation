#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Compare GPU runs (Docs/ReferenceData/BP05_gpu_runs.json) with traceable references:

 (1) NTHMP BP05 laboratory data (CHL/USACE, gauges 4-10, Cases A/B): Docs/ReferenceData/NTHMP_BP05/gA.txt, gB.txt
 (2) Flat bed: exact solitary-wave speed c = sqrt(g (h + a)) (Serre/Green-Naghdi; Peregrine's system agrees to O(eps^2)).

Screening criteria, fixed BEFORE looking at the results (not literature-derived, not tuned to the solver):
   peak height error      <= 10 %           (relative to the measured peak)
   arrival-time error     <= 0.15 s (lab)   (half-maximum rising crossing, relative to the gauge-5 crossing; 3 gauge samples)
   waveform NRMSE         <= 0.15           (RMSE / measured peak, window [t_half-1.5 s, t_peak+2.0 s], lab units)
Gauge x positions: g5, g7, g9 are fixed by dimension lines of BP5 Fig.1; g6, g8 are interpolated from the sketch
(not to scale) and are reported but NOT judged. g9 amplitude is contaminated by wall reflection that the solver does
not model (the 1/13 beach + vertical wall are replaced by a flat extension + sponge), so only g9 arrival is judged.
"""
import json, math, os, sys, importlib.util
import numpy as np

D = os.path.join("Docs", "ReferenceData")
G = 9.81
CRIT = dict(peak=0.10, dt=0.15, nrmse=0.15)
JUDGED = {"g7": ("peak", "dt", "nrmse"), "g9": ("dt",), "g5": ()}  # g5 = alignment/initial-condition consistency only


def load_exp(case):
    a = np.loadtxt(os.path.join(D, "NTHMP_BP05", f"g{case}.txt"))
    return a[:, 0], {f"g{4+i}": a[:, 1 + i] for i in range(7)}


def parab(t, y, i):
    if 0 < i < len(t) - 1:
        a, b, c = y[i - 1], y[i], y[i + 1]
        den = a - 2 * b + c
        p = 0.5 * (a - c) / den if abs(den) > 1e-15 else 0.0
        return t[i] + p * (t[1] - t[0]), b - 0.25 * (a - c) * p
    return t[i], y[i]


def peak_in(t, y, t0, t1):
    m = (t >= t0) & (t <= t1)
    idx = np.where(m)[0]
    i = idx[np.argmax(y[idx])]
    return parab(t, y, i)


def half_cross(t, y, peak, t_lo, t_pk):
    m = np.where((t >= t_lo) & (t <= t_pk))[0]
    for j in m:
        if y[j] >= 0.5 * peak and j > 0:
            y0, y1 = y[j - 1], y[j]
            return t[j - 1] + (0.5 * peak - y0) / (y1 - y0) * (t[j] - t[j - 1])
    return float("nan")


def analyse_series(t, y, t5_peak_hint, prev=None):
    """Return dict with peak, t_peak, t_half. Search window after the previous gauge's peak."""
    lo = (prev["t_peak"] - 0.3) if prev else (t5_peak_hint - 1.5)
    hi = (prev["t_peak"] + 4.0) if prev else (t5_peak_hint + 1.5)
    tp, hp = peak_in(t, y, lo, hi)
    th = half_cross(t, y, hp, tp - 4.0, tp)
    return dict(peak=hp, t_peak=tp, t_half=th)


def trailing(t, y, tp, peak):
    m = (t >= tp + 2.0) & (t <= tp + 5.0)
    return float(np.max(y[m]) / peak), float(np.min(y[m]) / peak)


def nrmse(t, ya, yb, t0, t1, peak):
    m = (t >= t0) & (t <= t1)
    return float(math.sqrt(np.mean((ya[m] - yb[m]) ** 2)) / peak)


def main():
    runs = json.load(open(os.path.join(D, "BP05_gpu_runs.json"), "r", encoding="utf-8-sig"))
    out = dict(criteria=CRIT, judged=JUDGED, bp05={}, flat={}, meta={k: runs[k] for k in ("timestamp", "gitCommit", "computeShaderHash", "gpuRenderer")})
    lines = []
    P = lines.append
    tgrid_cache = {}

    for case in "AB":
        te, ge = load_exp(case)
        # experimental analysis (same procedure as for the simulation)
        t5hint, _ = peak_in(te, ge["g5"], 265, 280)
        exp_an = {}
        prev = None
        for g in ("g5", "g6", "g7", "g8", "g9"):
            exp_an[g] = analyse_series(te, ge[g], t5hint, prev)
            prev = exp_an[g]
        out["bp05"][case] = dict(exp=exp_an, runs={})
        for r in [x for x in runs["runs"] if x["kind"] == f"BP05_{case}"]:
            lam = r["lengthScale"]
            st = {s["stationName"]: s for s in r["result"]["stationSeries"]}
            sts = r["result"]["stationSeries"]
            names = r["gaugeNames"]
            sim = {}
            for k, g in enumerate(names):
                ts = np.array(sts[k]["times"]) / math.sqrt(lam)
                ys = np.array(sts[k]["etas"]) / lam
                sim[g] = (ts, ys)
            # align sim g5 peak with experimental g5 peak (time only; amplitude is NOT rescaled)
            t5s, h5s = peak_in(sim["g5"][0], sim["g5"][1], 0.0, sim["g5"][0][-1])
            shift = exp_an["g5"]["t_peak"] - t5s
            res = {}
            prev = None
            for g in ("g5", "g6", "g7", "g8", "g9"):
                ts = sim[g][0] + shift
                ys = np.interp(te, ts, sim[g][1], left=0.0, right=0.0)
                an = analyse_series(te, ys, exp_an["g5"]["t_peak"], prev)
                prev = an
                e = exp_an[g]
                dt_exp = e["t_half"] - exp_an["g5"]["t_half"]
                dt_sim = an["t_half"] - res["g5"]["t_half"] if g != "g5" else 0.0
                tr_e = trailing(te, ge[g], e["t_peak"], e["peak"])
                tr_s = trailing(te, ys, an["t_peak"], an["peak"])
                res[g] = dict(
                    peak_exp_cm=e["peak"] * 100, peak_sim_cm=an["peak"] * 100, peak_rel_err=(an["peak"] - e["peak"]) / e["peak"],
                    t_half=an["t_half"], dt_exp_s=dt_exp, dt_sim_s=dt_sim, dt_err_s=dt_sim - dt_exp,
                    nrmse=nrmse(te, ys, ge[g], e["t_half"] - 1.5, e["t_peak"] + 2.0, e["peak"]),
                    trail_max_exp=tr_e[0], trail_max_sim=tr_s[0], trail_min_exp=tr_e[1], trail_min_sim=tr_s[1],
                    x_lab=r["gaugeXLab"][names.index(g)],
                    _y=ys.tolist())
            for g, jd in JUDGED.items():
                v = res[g]
                v["judged"] = list(jd)
                v["pass"] = {}
                if "peak" in jd: v["pass"]["peak"] = abs(v["peak_rel_err"]) <= CRIT["peak"]
                if "dt" in jd: v["pass"]["dt"] = abs(v["dt_err_s"]) <= CRIT["dt"]
                if "nrmse" in jd: v["pass"]["nrmse"] = v["nrmse"] <= CRIT["nrmse"]
            dv = r["result"]["depthVerification"]
            out["bp05"][case]["runs"][r["label"]] = dict(res=res, shift_s=shift, mode=r["result"]["dissipationMode"], N=r["result"]["resolutionN"],
                                                       domain_eff_minus_spec=dv["maxDomainEffectiveMinusSpecified"], subDt=r["result"]["subDt"], kappa4=r["result"]["kappa4"])

    # ------- flat bed analytic -------
    for case in "AB":
        for r in [x for x in runs["runs"] if x["kind"] == f"FlatBed_{case}"]:
            lam = r["lengthScale"]
            sts = r["result"]["stationSeries"]
            zs, tp, hp = [], [], []
            for s in sts[1:]:  # stations[0] is the initial position
                t = np.array(s["times"]); y = np.array(s["etas"])
                i = int(np.argmax(y)); t_, h_ = parab(t, y, i)
                zs.append(s["stationZ"]); tp.append(t_); hp.append(h_)
            zs, tp, hp = map(np.array, (zs, tp, hp))
            h = r["result"]["a0"] and 0.218 * lam
            speed = float(np.polyfit(tp, zs, 1)[0])
            c_th = math.sqrt(G * (h + r["a0Sim"]))
            out["flat"][r["label"]] = dict(a0=r["a0Sim"], h=h, speed_gpu=speed, speed_theory=c_th, speed_rel_err=(speed - c_th) / c_th,
                                          amp_retention=(hp / r["a0Sim"]).tolist(), station_z=zs.tolist(), mode=r["result"]["dissipationMode"], N=r["result"]["resolutionN"])

    json.dump(out, open(os.path.join(D, "BP05_comparison.json"), "w", encoding="utf-8"), indent=1, default=float)

    # ------- console / markdown -------
    for case in "AB":
        P(f"### BP05 Case {case} (lab units; sim shifted in time so that g5 peak coincides, amplitude not rescaled)")
        P("| run | gauge | peak exp [cm] | peak sim [cm] | err | Δt(g5→gk) exp [s] | sim [s] | err [s] | NRMSE | trail max exp/sim | pass |")
        P("|---|---|---|---|---|---|---|---|---|---|---|")
        for lab, rr in out["bp05"][case]["runs"].items():
            for g in ("g5", "g6", "g7", "g8", "g9"):
                v = rr["res"][g]
                ps = "-" if g in ("g5", "g6", "g8") else ("/".join(f"{k}:{'OK' if b else 'NG'}" for k, b in v["pass"].items()))
                P(f"| {lab} | {g} | {v['peak_exp_cm']:.2f} | {v['peak_sim_cm']:.2f} | {v['peak_rel_err']*100:+.1f}% | {v['dt_exp_s']:.2f} | {v['dt_sim_s']:.2f} | {v['dt_err_s']:+.2f} | {v['nrmse']:.3f} | {v['trail_max_exp']:.3f}/{v['trail_max_sim']:.3f} | {ps} |")
    P("\n### Flat bed vs exact solitary-wave speed")
    P("| run | N | a0 sim [m] | speed GPU [m/s] | c_theory [m/s] | err | amplitude retention at stations |")
    P("|---|---|---|---|---|---|---|")
    for lab, v in out["flat"].items():
        P(f"| {lab} | {v['N']} | {v['a0']:.4f} | {v['speed_gpu']:.4f} | {v['speed_theory']:.4f} | {v['speed_rel_err']*100:+.2f}% | {', '.join(f'{x:.3f}' for x in v['amp_retention'])} |")
    txt = "\n".join(lines)
    open(os.path.join(D, "BP05_comparison_tables.md"), "w", encoding="utf-8").write(txt)
    print(txt)
    for case in "AB":
        for lab, rr in out["bp05"][case]["runs"].items():
            print(lab, "depth(eff-spec) max over domain [m]:", rr["domain_eff_minus_spec"], "subDt", rr["subDt"], "kappa4", rr["kappa4"])

    # ------- plots -------
    spec = importlib.util.spec_from_file_location("gp", os.path.join("Docs", "GenerateSolitonFissionPlots.py"))
    gp = importlib.util.module_from_spec(spec); spec.loader.exec_module(gp)
    colors = {
        "FCT_N256": "#85c1e9",
        "FCT_N512": "#2471a3",
        "FilterOff_N512": "#e74c3c",
        "MinmodTVD_FCT_N256": "#52be80",
        "MinmodTVD_FCT_N512": "#1e8449",
        "MinmodTVD_FilterOff_N512": "#8e44ad",
        "Legacy2D_N512": "#f39c12"
    }
    for case in "AB":
        te, ge = load_exp(case)
        for g in ("g5", "g7", "g9"):
            e_an = out["bp05"][case]["exp"][g]
            c0, c1 = e_an["t_peak"] - 4.0, e_an["t_peak"] + 7.0
            m = (te >= c0) & (te <= c1)
            ser = [dict(name=f"NTHMP BP05 lab data (Case {case}, {g})", color="#000000", points=list(zip((te[m] - c0).tolist(), (ge[g][m] * 100).tolist())), stroke_width=2.4)]
            for lab, rr in out["bp05"][case]["runs"].items():
                tag = lab.split(f"BP05_{case}_")[1]
                y = np.array(rr["res"][g]["_y"])
                c_color = colors.get(tag, "#34495e")
                ser.append(dict(name=f"GPU {tag}", color=c_color, points=list(zip((te[m] - c0).tolist(), (y[m] * 100).tolist())), stroke_width=1.8, dash="" if "FCT" in tag else "4,2"))
            ymax = max(p[1] for s in ser for p in s["points"]) * 1.15
            svg = gp.create_svg_line_chart(ser, width=950, height=450,
                                           title=f"BP05 Case {case} gauge {g}: lab data vs GPU (sim time-aligned at g5; g9 after wall reflection not modelled)",
                                           x_label=f"time since t={c0:.2f} s [s, lab units]", y_label="η [cm]", x_range=(0, c1 - c0), y_range=(-0.1 * ymax, ymax))
            open(os.path.join("Docs", f"Plot_BP05_{case}_{g}.svg"), "w", encoding="utf-8").write(svg)
    print("plots written")


if __name__ == "__main__":
    main()
