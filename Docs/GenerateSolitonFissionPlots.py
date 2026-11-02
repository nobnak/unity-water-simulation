#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Generate vector SVG visualization plots for Boussinesq Soliton Fission benchmark.
Outputs:
- Docs/Plot_Independent_Reference_Comparison.svg
- Docs/Plot_Soliton_Fission_Extended.svg
- Docs/Plot_FlatBed_Control.svg
- Docs/Plot_Grid_Convergence.svg
- Docs/Plot_Station_TimeSeries.svg
"""

import json
import math
import os

def create_svg_line_chart(series_list, width=950, height=480, title="", x_label="", y_label="", x_range=None, y_range=None):
    pad_left = 75
    pad_right = 30
    pad_top = 50
    pad_bottom = 60
    plot_w = width - pad_left - pad_right
    plot_h = height - pad_top - pad_bottom

    all_x = [pt[0] for s in series_list for pt in s['points']]
    all_y = [pt[1] for s in series_list for pt in s['points']]

    min_x = x_range[0] if x_range else (min(all_x) if all_x else 0.0)
    max_x = x_range[1] if x_range else (max(all_x) if all_x else 1.0)
    min_y = y_range[0] if y_range else (min(all_y) if all_y else 0.0)
    max_y = y_range[1] if y_range else (max(all_y) if all_y else 1.0)

    if max_x == min_x: max_x += 1.0
    if max_y == min_y: max_y += 1.0

    def to_svg_x(x):
        return pad_left + (x - min_x) / (max_x - min_x) * plot_w

    def to_svg_y(y):
        return pad_top + plot_h - (y - min_y) / (max_y - min_y) * plot_h

    lines = []
    lines.append(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" style="background-color: #ffffff; font-family: -apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif;">')

    # Title
    lines.append(f'  <text x="{width/2}" y="28" text-anchor="middle" font-size="16" font-weight="bold" fill="#2c3e50">{title}</text>')

    # Plot Background & Grid
    lines.append(f'  <rect x="{pad_left}" y="{pad_top}" width="{plot_w}" height="{plot_h}" fill="#fcfcfc" stroke="#bdc3c7" stroke-width="1"/>')

    # X Grid and Ticks
    n_ticks_x = 8
    for i in range(n_ticks_x + 1):
        vx = min_x + (max_x - min_x) * i / n_ticks_x
        sx = to_svg_x(vx)
        lines.append(f'  <line x1="{sx}" y1="{pad_top}" x2="{sx}" y2="{pad_top+plot_h}" stroke="#ecf0f1" stroke-width="1"/>')
        lines.append(f'  <line x1="{sx}" y1="{pad_top+plot_h}" x2="{sx}" y2="{pad_top+plot_h+5}" stroke="#7f8c8d" stroke-width="1"/>')
        lines.append(f'  <text x="{sx}" y="{pad_top+plot_h+18}" text-anchor="middle" font-size="11" fill="#7f8c8d">{vx:.1f}</text>')

    # Y Grid and Ticks
    n_ticks_y = 6
    for i in range(n_ticks_y + 1):
        vy = min_y + (max_y - min_y) * i / n_ticks_y
        sy = to_svg_y(vy)
        lines.append(f'  <line x1="{pad_left}" y1="{sy}" x2="{pad_left+plot_w}" y2="{sy}" stroke="#ecf0f1" stroke-width="1"/>')
        lines.append(f'  <line x1="{pad_left-5}" y1="{sy}" x2="{pad_left}" y2="{sy}" stroke="#7f8c8d" stroke-width="1"/>')
        lines.append(f'  <text x="{pad_left-8}" y="{sy+4}" text-anchor="end" font-size="11" fill="#7f8c8d">{vy:.3f}</text>')

    # Axis Labels
    lines.append(f'  <text x="{pad_left + plot_w/2}" y="{height - 12}" text-anchor="middle" font-size="13" font-weight="bold" fill="#34495e">{x_label}</text>')
    lines.append(f'  <text x="22" y="{pad_top + plot_h/2}" text-anchor="middle" font-size="13" font-weight="bold" fill="#34495e" transform="rotate(-90 22 {pad_top + plot_h/2})">{y_label}</text>')

    # Zero Line if within range
    if min_y < 0 < max_y:
        sy0 = to_svg_y(0.0)
        lines.append(f'  <line x1="{pad_left}" y1="{sy0}" x2="{pad_left+plot_w}" y2="{sy0}" stroke="#95a5a6" stroke-width="1.2" stroke-dasharray="4,4"/>')

    # Data Series
    legend_y = pad_top + 18
    legend_x = pad_left + 15
    leg_offset = 0

    for s in series_list:
        pts = s['points']
        if not pts:
            continue
        color = s.get('color', '#3498db')
        stroke_w = s.get('stroke_width', 2.0)
        dash = s.get('dash', '')
        dash_attr = f' stroke-dasharray="{dash}"' if dash else ''

        # SVG path
        p_str = []
        for i, pt in enumerate(pts):
            sx = to_svg_x(pt[0])
            sy = to_svg_y(pt[1])
            sy_clamped = max(pad_top, min(pad_top + plot_h, sy))
            if i == 0:
                p_str.append(f"M {sx:.2f} {sy_clamped:.2f}")
            else:
                p_str.append(f"L {sx:.2f} {sy_clamped:.2f}")
        path_d = " ".join(p_str)
        lines.append(f'  <path d="{path_d}" fill="none" stroke="{color}" stroke-width="{stroke_w}"{dash_attr}/>')

        # Legend item
        name = s.get('name', '')
        if name:
            ly = legend_y + leg_offset * 18
            lines.append(f'  <line x1="{legend_x}" y1="{ly-3}" x2="{legend_x+25}" y2="{ly-3}" stroke="{color}" stroke-width="{stroke_w}"{dash_attr}/>')
            lines.append(f'  <text x="{legend_x+32}" y="{ly}" font-size="11" font-weight="500" fill="#2c3e50">{name}</text>')
            leg_offset += 1

    lines.append('</svg>')
    return "\n".join(lines)


def get_snap_near(case, target_t):
    if not case: return None
    snaps = case.get('snapshots', [])
    if not snaps: return None
    return min(snaps, key=lambda s: abs(s['time'] - target_t))


def main():
    gpu_json_path = "Docs/BoussinesqSolitonFissionReport.json"
    ref_json_path = "Docs/Reference_Peregrine_FP64_Data.json"

    if not os.path.exists(gpu_json_path):
        print(f"Error: {gpu_json_path} not found")
        return

    with open(gpu_json_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)

    results = {r['caseName']: r for r in data['results']}

    ref_data = None
    if os.path.exists(ref_json_path):
        with open(ref_json_path, "r", encoding="utf-8") as f:
            ref_data = json.load(f)

    # 1. Independent Peregrine FP64 Reference Comparison vs GPU Simulations at t = 14.0s
    c_fct256 = results.get("Shelf_FP32_Baseline_CurrentFCT_N256")
    c_fct512 = results.get("Shelf_FP32_GridConverge_CurrentFCT_N512")
    c_raw256 = results.get("Shelf_FP32_Isolated_FilterOff_N256")
    c_raw512 = results.get("Shelf_FP32_GridConverge_FilterOff_N512")

    if c_fct256 and c_fct512 and c_raw256 and c_raw512:
        s_fct256 = get_snap_near(c_fct256, 14.0)
        s_fct512 = get_snap_near(c_fct512, 14.0)
        s_raw256 = get_snap_near(c_raw256, 14.0)
        s_raw512 = get_snap_near(c_raw512, 14.0)

        bathy_pts = [(p['z'], -p['effectiveDepth'] * 0.15) for p in s_fct256['points']]

        series_ref = [
            {'name': 'Bed Profile (-h*0.15)', 'color': '#7f8c8d', 'points': bathy_pts, 'stroke_width': 1.5, 'dash': '3,3'},
        ]

        if ref_data and 'referenceProfiles' in ref_data:
            ref_z = ref_data['referenceProfiles']['z']
            ref_eta = ref_data['referenceProfiles']['eta_t14']
            ref_pts = list(zip(ref_z, ref_eta))
            series_ref.append({'name': 'Independent Peregrine (1967) FP64 Reference (N=2048, no dissipation, t=14.0s)', 'color': '#2c3e50', 'points': ref_pts, 'stroke_width': 2.8, 'dash': '5,3'})

        series_ref.append({'name': f'CurrentFCT N=512 (2 Peaks resolved, lead H=0.083m, t={s_fct512["time"]:.1f}s)', 'color': '#27ae60', 'points': [(p['z'], p['eta']) for p in s_fct512['points']], 'stroke_width': 2.2})
        series_ref.append({'name': f'CurrentFCT N=256 (1 Peak smoothed, lead H=0.070m, t={s_fct256["time"]:.1f}s)', 'color': '#2980b9', 'points': [(p['z'], p['eta']) for p in s_fct256['points']], 'stroke_width': 2.0})
        series_ref.append({'name': f'Filter OFF N=512 (3 Peaks, lead H=0.095m, t={s_raw512["time"]:.1f}s)', 'color': '#e74c3c', 'points': [(p['z'], p['eta']) for p in s_raw512['points']], 'stroke_width': 1.8, 'dash': '4,2'})
        series_ref.append({'name': f'Filter OFF N=256 (2 Peaks, lead H=0.086m, t={s_raw256["time"]:.1f}s)', 'color': '#e67e22', 'points': [(p['z'], p['eta']) for p in s_raw256['points']], 'stroke_width': 1.6, 'dash': '2,2'})

        svg_ref = create_svg_line_chart(
            series_ref, width=950, height=480,
            title="Soliton Shelf Transformation: Independent FP64 Peregrine vs GPU Solvers (t=14.0s)",
            x_label="Propagation Distance z [m] (Slope: 10m-18m, Shelf: 18m-48m)",
            y_label="Water Surface Elevation η [m]",
            x_range=(0, 48), y_range=(-0.05, 0.35)
        )

        out_ref_svg = "Docs/Plot_Independent_Reference_Comparison.svg"
        with open(out_ref_svg, "w", encoding="utf-8") as out_f:
            out_f.write(svg_ref)
        print(f"Generated: {out_ref_svg}")

    # 2. Extended Shelf Physical Soliton Fission (Independent Reference Solver L=100m)
    if ref_data and 'extendedShelfSolitonFission' in ref_data:
        ext = ref_data['extendedShelfSolitonFission']
        p_summary = ext['peaksSummary']
        # We can plot peak trajectories over time
        times = [14.0, 20.0, 26.0, 32.0]
        lead_pts = []
        sec_pts = []
        third_pts = []
        fourth_pts = []
        for t in times:
            t_str = str(t)
            p_list = p_summary.get(t_str, [])
            def get_zh(p):
                return (p['z'], p['height']) if isinstance(p, dict) else (p[0], p[1])
            if len(p_list) >= 1: lead_pts.append(get_zh(p_list[0]))
            if len(p_list) >= 2: sec_pts.append(get_zh(p_list[1]))
            if len(p_list) >= 3: third_pts.append(get_zh(p_list[2]))
            if len(p_list) >= 4: fourth_pts.append(get_zh(p_list[3]))

        # Also plot full profiles at t=14s and t=20s from referenceProfiles
        ref_z = ref_data['referenceProfiles']['z']
        ref_eta14 = ref_data['referenceProfiles']['eta_t14']
        ref_eta20 = ref_data['referenceProfiles']['eta_t20']
        
        series_ext = [
            {'name': 'Reference Peregrine FP64 Profile at t=14.0s (Undular bore entering shelf)', 'color': '#2980b9', 'points': list(zip(ref_z, ref_eta14)), 'stroke_width': 2.2},
            {'name': 'Reference Peregrine FP64 Profile at t=20.0s (First solitons detaching)', 'color': '#e67e22', 'points': list(zip(ref_z, ref_eta20)), 'stroke_width': 2.2},
            {'name': 'Soliton 1 Peak (t=14s..32s, c ≈ 2.10 m/s)', 'color': '#27ae60', 'points': lead_pts, 'stroke_width': 2.5, 'dash': '4,4'},
            {'name': 'Soliton 2 Peak (t=14s..32s, c ≈ 1.86 m/s)', 'color': '#8e44ad', 'points': sec_pts, 'stroke_width': 2.2, 'dash': '4,4'},
            {'name': 'Soliton 3 Peak (t=14s..32s, c ≈ 1.66 m/s)', 'color': '#d35400', 'points': third_pts, 'stroke_width': 2.0, 'dash': '4,4'},
            {'name': 'Soliton 4 Peak (t=14s..32s, c ≈ 1.53 m/s)', 'color': '#c0392b', 'points': fourth_pts, 'stroke_width': 1.8, 'dash': '4,4'},
        ]

        svg_ext = create_svg_line_chart(
            series_ext, width=950, height=480,
            title="Physical Soliton Fission on Extended Shelf (Independent Peregrine FP64 Solver, L=100m)",
            x_label="Propagation Distance z [m] (Shelf: 18m - 96m)",
            y_label="Water Surface Elevation η [m]",
            x_range=(0, 80), y_range=(-0.05, 0.35)
        )

        out_ext_svg = "Docs/Plot_Soliton_Fission_Extended.svg"
        with open(out_ext_svg, "w", encoding="utf-8") as out_f:
            out_f.write(svg_ext)
        print(f"Generated: {out_ext_svg}")

    # 3. Flat Bed Control (No Slope): Filter OFF vs Current FCT
    c_flat_fct = results.get("FlatBed_FP32_Control_CurrentFCT_N256")
    c_flat_raw = results.get("FlatBed_FP32_Control_FilterOff_N256")
    c_flat_raw512 = results.get("FlatBed_FP32_Control_FilterOff_N512")

    if c_flat_fct and c_flat_raw:
        s_flat_fct = get_snap_near(c_flat_fct, 14.0)
        s_flat_raw = get_snap_near(c_flat_raw, 14.0)
        s_flat_raw512 = get_snap_near(c_flat_raw512, 14.0) if c_flat_raw512 else None

        if s_flat_fct and s_flat_raw:
            pts_fct = [(p['z'], p['eta']) for p in s_flat_fct['points']]
            pts_raw = [(p['z'], p['eta']) for p in s_flat_raw['points']]

            series = [
                {'name': f'Current FCT (N=256, Stable Soliton, c=2.618m/s, ret=0.792, t={s_flat_fct["time"]:.1f}s)', 'color': '#2ecc71', 'points': pts_fct, 'stroke_width': 2.5},
                {'name': f'Filter OFF (N=256, c=2.624m/s, ret=0.866, t={s_flat_raw["time"]:.1f}s)', 'color': '#e67e22', 'points': pts_raw, 'stroke_width': 1.8},
            ]
            if s_flat_raw512:
                series.append({'name': f'Filter OFF (N=512, c=2.638m/s, ret=0.936, t={s_flat_raw512["time"]:.1f}s)', 'color': '#e74c3c', 'points': [(p['z'], p['eta']) for p in s_flat_raw512['points']], 'stroke_width': 1.5})

            svg_content = create_svg_line_chart(
                series, width=950, height=480,
                title="Flat Bed Control FP32 (h=0.60m constant): Stable Solitary Wave Propagation",
                x_label="Propagation Distance z [m]",
                y_label="Water Surface Elevation η [m]",
                x_range=(0, 48), y_range=(-0.04, 0.14)
            )

            out_svg = "Docs/Plot_FlatBed_Control.svg"
            with open(out_svg, "w", encoding="utf-8") as out_f:
                out_f.write(svg_content)
            print(f"Generated: {out_svg}")

    # 4. Grid Convergence: N=128 vs N=256 vs N=512 on Shelf
    c_128 = results.get("Shelf_FP32_GridConverge_CurrentFCT_N128")
    c_256 = results.get("Shelf_FP32_Baseline_CurrentFCT_N256")
    c_512 = results.get("Shelf_FP32_GridConverge_CurrentFCT_N512")

    if c_128 and c_256 and c_512:
        s_128 = get_snap_near(c_128, 14.0)
        s_256 = get_snap_near(c_256, 14.0)
        s_512 = get_snap_near(c_512, 14.0)

        if s_128 and s_256 and s_512:
            series = [
                {'name': f'CurrentFCT N=128 (dx=0.391m: 1 Broad Peak, H=0.063m)', 'color': '#9b59b6', 'points': [(p['z'], p['eta']) for p in s_128['points']], 'stroke_width': 2.0, 'dash': '4,4'},
                {'name': f'CurrentFCT N=256 (dx=0.195m: 1 Peak, H=0.070m)', 'color': '#3498db', 'points': [(p['z'], p['eta']) for p in s_256['points']], 'stroke_width': 2.2},
                {'name': f'CurrentFCT N=512 (dx=0.098m: 2 Distinct Peaks, H1=0.083m, H2=0.055m)', 'color': '#2ecc71', 'points': [(p['z'], p['eta']) for p in s_512['points']], 'stroke_width': 2.5}
            ]

            svg_content = create_svg_line_chart(
                series, width=950, height=480,
                title="Spatial Grid Convergence: Secondary Fission Peak Resolved at N=512",
                x_label="Propagation Distance z [m]",
                y_label="Water Surface Elevation η [m]",
                x_range=(0, 48), y_range=(-0.04, 0.15)
            )

            out_svg = "Docs/Plot_Grid_Convergence.svg"
            with open(out_svg, "w", encoding="utf-8") as out_f:
                out_f.write(svg_content)
            print(f"Generated: {out_svg}")

    # 5. Station Time Series comparison on Mid-Shelf Station (Station 4, z = 29.2m)
    st_idx = 3
    if c_fct256 and c_raw256 and c_fct512:
        st_fct = c_fct256['stationSeries'][st_idx]
        st_raw = c_raw256['stationSeries'][st_idx]
        st_fct512 = c_fct512['stationSeries'][st_idx]

        series = [
            {'name': f'Filter OFF N=256 at z={st_raw["stationZ"]:.1f}m', 'color': '#e74c3c', 'points': list(zip(st_raw['times'], st_raw['etas'])), 'stroke_width': 2.0},
            {'name': f'CurrentFCT N=256 at z={st_fct["stationZ"]:.1f}m', 'color': '#3498db', 'points': list(zip(st_fct['times'], st_fct['etas'])), 'stroke_width': 2.2},
            {'name': f'CurrentFCT N=512 at z={st_fct512["stationZ"]:.1f}m', 'color': '#27ae60', 'points': list(zip(st_fct512['times'], st_fct512['etas'])), 'stroke_width': 2.5}
        ]

        svg_content = create_svg_line_chart(
            series, width=950, height=480,
            title=f"Wave Elevation Time Series on Shallow Shelf (Station at z = {st_fct['stationZ']:.1f}m)",
            x_label="Physical Time t [s]",
            y_label="Surface Elevation η [m]",
            x_range=(0, 22), y_range=(-0.04, 0.12)
        )

        out_svg = "Docs/Plot_Station_TimeSeries.svg"
        with open(out_svg, "w", encoding="utf-8") as out_f:
            out_f.write(svg_content)
        print(f"Generated: {out_svg}")

    print("All SVG plots generated successfully.")

if __name__ == "__main__":
    main()
