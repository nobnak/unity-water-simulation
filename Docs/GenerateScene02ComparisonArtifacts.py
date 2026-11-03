#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Generate side-by-side comparison animated GIFs and SVG profile / time-series plots
from Scene 02 visual comparison data (Baseline vs Minmod TVD).
"""

import json
import os
import shutil
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ARTIFACT_DIR = r"C:\Users\nakata\.gemini\antigravity\brain\4116f5d3-ef73-4105-b7e1-dd7be8a568b5"
DATA_PATH = os.path.join("Docs", "VisualComparison_Scene02_Data.json")
CAPTURES_BASE = os.path.join("Captures", "VisualComparison")
DOCS_DIR = "Docs"

def load_data():
    with open(DATA_PATH, "r", encoding="utf-8-sig") as f:
        return json.load(f)

def create_side_by_side_gif(case_id, view_type, out_filename, start_step=0, end_step=480, step_stride=3, fps=10):
    """
    Combine Baseline and Minmod frames into a side-by-side animated GIF at 10 fps (1.0x physical time).
    view_type: 'overview' or 'closeup'
    """
    dir_base = os.path.join(CAPTURES_BASE, f"{case_id}_Baseline")
    dir_minm = os.path.join(CAPTURES_BASE, f"{case_id}_Minmod")

    frames = []
    # Try to load default font
    try:
        font = ImageFont.load_default()
    except Exception:
        font = None

    panel_w, panel_h = 480, 270

    for step in range(start_step, end_step + 1, step_stride):
        fn = f"{view_type}_{step:04d}.png"
        path_base = os.path.join(dir_base, fn)
        path_minm = os.path.join(dir_minm, fn)

        if not os.path.exists(path_base) or not os.path.exists(path_minm):
            continue

        im_base = Image.open(path_base).convert("RGB").resize((panel_w, panel_h), Image.Resampling.LANCZOS)
        im_minm = Image.open(path_minm).convert("RGB").resize((panel_w, panel_h), Image.Resampling.LANCZOS)

        combined = Image.new("RGB", (panel_w * 2, panel_h))
        combined.paste(im_base, (0, 0))
        combined.paste(im_minm, (panel_w, 0))

        draw = ImageDraw.Draw(combined)
        sim_time = step * (1.0 / 30.0)

        # Baseline label
        draw.rectangle([6, 6, 230, 36], fill=(0, 0, 0, 180))
        draw.text((12, 10), f"Baseline (1st Upwind) [Default]", fill=(120, 220, 255), font=font)
        draw.text((12, 22), f"t = {sim_time:5.2f} s", fill=(255, 255, 255), font=font)

        # Minmod label
        draw.rectangle([panel_w + 6, 6, panel_w + 230, 36], fill=(0, 0, 0, 180))
        draw.text((panel_w + 12, 10), f"Candidate Minmod (2nd TVD)", fill=(255, 200, 100), font=font)
        draw.text((panel_w + 12, 22), f"t = {sim_time:5.2f} s", fill=(255, 255, 255), font=font)

        # Dividing line
        draw.line([(panel_w, 0), (panel_w, panel_h)], fill=(80, 80, 80), width=2)

        # Quantize with web palette for smooth compact GIF
        frames.append(combined.quantize(colors=128, method=Image.Resampling.NEAREST))

    if frames:
        out_docs = os.path.join(DOCS_DIR, out_filename)
        duration_ms = int(1000.0 / fps)
        frames[0].save(
            out_docs,
            save_all=True,
            append_images=frames[1:],
            duration=duration_ms,
            loop=0,
            optimize=True
        )
        print(f"Saved GIF: {out_docs} ({len(frames)} frames, {os.path.getsize(out_docs)/1024:.1f} KB)")
        if os.path.exists(ARTIFACT_DIR):
            out_art = os.path.join(ARTIFACT_DIR, out_filename)
            shutil.copy2(out_docs, out_art)
            print(f"Copied to artifact: {out_art}")

def generate_svg_profile(profile_data, title, out_filename, y_range=(-0.8, 1.2)):
    """Generate SVG water surface profile comparison along centerline z = 0..100m."""
    zs = profile_data["zs"]
    depths = profile_data["depths"]
    eta_base = profile_data["etasBaseline"]
    eta_minm = profile_data["etasMinmod"]
    t = profile_data["time"]

    width, height = 900, 420
    pad_l, pad_r, pad_t, pad_b = 70, 40, 50, 50
    plot_w = width - pad_l - pad_r
    plot_h = height - pad_t - pad_b

    z_min, z_max = 0.0, 100.0
    y_min, y_max = y_range

    def to_screen(z, y):
        sx = pad_l + ((z - z_min) / (z_max - z_min)) * plot_w
        sy = pad_t + ((y_max - y) / (y_max - y_min)) * plot_h
        return sx, sy

    svg = []
    P = svg.append
    P(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}">')
    P('<rect width="100%" height="100%" fill="#1a1c23"/>')
    P(f'<text x="{width/2}" y="28" font-family="system-ui, sans-serif" font-size="15" font-weight="bold" fill="#f0f6fc" text-anchor="middle">{title} (t = {t:.1f}s)</text>')

    # Grid lines and axes
    for y_val in np.linspace(y_min, y_max, 5):
        _, sy = to_screen(0, y_val)
        P(f'<line x1="{pad_l}" y1="{sy:.1f}" x2="{width - pad_r}" y2="{sy:.1f}" stroke="#30363d" stroke-width="1"/>')
        P(f'<text x="{pad_l - 8}" y="{sy + 4:.1f}" font-family="monospace" font-size="11" fill="#8b949e" text-anchor="end">{y_val:+.2f}m</text>')

    for z_val in range(0, 101, 20):
        sx, _ = to_screen(z_val, 0)
        P(f'<line x1="{sx:.1f}" y1="{pad_t}" x2="{sx:.1f}" y2="{height - pad_b}" stroke="#30363d" stroke-width="1"/>')
        P(f'<text x="{sx:.1f}" y="{height - pad_b + 18}" font-family="monospace" font-size="11" fill="#8b949e" text-anchor="middle">{z_val}m</text>')

    # Axis labels
    P(f'<text x="{width/2}" y="{height - 12}" font-family="system-ui, sans-serif" font-size="12" fill="#8b949e" text-anchor="middle">Z coordinate (Propagation direction) [m]</text>')
    P(f'<text x="18" y="{height/2}" font-family="system-ui, sans-serif" font-size="12" fill="#8b949e" text-anchor="middle" transform="rotate(-90 18 {height/2})">Water Elevation η [m]</text>')

    # Still water level zero
    _, sy0 = to_screen(0, 0)
    P(f'<line x1="{pad_l}" y1="{sy0:.1f}" x2="{width - pad_r}" y2="{sy0:.1f}" stroke="#58a6ff" stroke-width="1" stroke-dasharray="4,4"/>')

    # Seabed bed level z_b = -h
    bed_pts = []
    for z, d in zip(zs, depths):
        sx, sy = to_screen(z, -d)
        bed_pts.append(f"{sx:.1f},{sy:.1f}")
    if bed_pts:
        P(f'<polyline points="{" ".join(bed_pts)}" fill="none" stroke="#8b7355" stroke-width="2.0"/>')

    # Baseline curve
    base_pts = [f"{to_screen(z, e)[0]:.1f},{to_screen(z, e)[1]:.1f}" for z, e in zip(zs, eta_base)]
    P(f'<polyline points="{" ".join(base_pts)}" fill="none" stroke="#58a6ff" stroke-width="2.2"/>')

    # Minmod curve
    minm_pts = [f"{to_screen(z, e)[0]:.1f},{to_screen(z, e)[1]:.1f}" for z, e in zip(zs, eta_minm)]
    P(f'<polyline points="{" ".join(minm_pts)}" fill="none" stroke="#f0883e" stroke-width="2.2" stroke-dasharray="5,2"/>')

    # Legend
    leg_x = width - pad_r - 280
    leg_y = pad_t + 10
    P(f'<rect x="{leg_x}" y="{leg_y}" width="270" height="75" fill="#21262d" rx="4" stroke="#30363d"/>')
    P(f'<line x1="{leg_x + 12}" y1="{leg_y + 18}" x2="{leg_x + 40}" y2="{leg_y + 18}" stroke="#58a6ff" stroke-width="2.2"/>')
    P(f'<text x="{leg_x + 48}" y="{leg_y + 22}" font-family="system-ui, sans-serif" font-size="12" fill="#58a6ff">Baseline (1st-Order Upwind)</text>')
    P(f'<line x1="{leg_x + 12}" y1="{leg_y + 38}" x2="{leg_x + 40}" y2="{leg_y + 38}" stroke="#f0883e" stroke-width="2.2" stroke-dasharray="5,2"/>')
    P(f'<text x="{leg_x + 48}" y="{leg_y + 42}" font-family="system-ui, sans-serif" font-size="12" fill="#f0883e">Candidate Minmod (2nd-Order TVD)</text>')
    P(f'<line x1="{leg_x + 12}" y1="{leg_y + 58}" x2="{leg_x + 40}" y2="{leg_y + 58}" stroke="#8b7355" stroke-width="2.0"/>')
    P(f'<text x="{leg_x + 48}" y="{leg_y + 62}" font-family="system-ui, sans-serif" font-size="12" fill="#8b7355">Seabed Elevation (-h)</text>')

    P('</svg>')
    out_docs = os.path.join(DOCS_DIR, out_filename)
    with open(out_docs, "w", encoding="utf-8") as f:
        f.write("\n".join(svg))
    print(f"Saved SVG: {out_docs}")
    if os.path.exists(ARTIFACT_DIR):
        out_art = os.path.join(ARTIFACT_DIR, out_filename)
        shutil.copy2(out_docs, out_art)

def generate_svg_timeseries(stations, title, out_filename, y_range=(-0.5, 0.8)):
    """Generate SVG station time-series comparison η(t) across Gauges."""
    width, height = 950, 480
    pad_l, pad_r, pad_t, pad_b = 70, 40, 50, 60
    plot_w = width - pad_l - pad_r
    plot_h = height - pad_t - pad_b

    t_min, t_max = 0.0, 16.0
    y_min, y_max = y_range

    def to_screen(t, y):
        sx = pad_l + ((t - t_min) / (t_max - t_min)) * plot_w
        sy = pad_t + ((y_max - y) / (y_max - y_min)) * plot_h
        return sx, sy

    colors = [("#58a6ff", "#f0883e"), ("#7ee787", "#d29922"), ("#bc8cff", "#ff7b72")]

    svg = []
    P = svg.append
    P(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}">')
    P('<rect width="100%" height="100%" fill="#1a1c23"/>')
    P(f'<text x="{width/2}" y="28" font-family="system-ui, sans-serif" font-size="15" font-weight="bold" fill="#f0f6fc" text-anchor="middle">{title}</text>')

    # Grid
    for y_val in np.linspace(y_min, y_max, 6):
        _, sy = to_screen(0, y_val)
        P(f'<line x1="{pad_l}" y1="{sy:.1f}" x2="{width - pad_r}" y2="{sy:.1f}" stroke="#30363d" stroke-width="1"/>')
        P(f'<text x="{pad_l - 8}" y="{sy + 4:.1f}" font-family="monospace" font-size="11" fill="#8b949e" text-anchor="end">{y_val:+.2f}m</text>')

    for t_val in range(0, 17, 2):
        sx, _ = to_screen(t_val, 0)
        P(f'<line x1="{sx:.1f}" y1="{pad_t}" x2="{sx:.1f}" y2="{height - pad_b}" stroke="#30363d" stroke-width="1"/>')
        P(f'<text x="{sx:.1f}" y="{height - pad_b + 18}" font-family="monospace" font-size="11" fill="#8b949e" text-anchor="middle">{t_val}s</text>')

    P(f'<text x="{width/2}" y="{height - 15}" font-family="system-ui, sans-serif" font-size="12" fill="#8b949e" text-anchor="middle">Physical Simulation Time t [s]</text>')
    P(f'<text x="18" y="{height/2}" font-family="system-ui, sans-serif" font-size="12" fill="#8b949e" text-anchor="middle" transform="rotate(-90 18 {height/2})">Water Elevation η [m]</text>')

    # Plot curves for each station
    for idx, st in enumerate(stations):
        times = st["times"]
        c_base, c_minm = colors[idx % len(colors)]
        base_pts = [f"{to_screen(t, e)[0]:.1f},{to_screen(t, e)[1]:.1f}" for t, e in zip(times, st["etasBaseline"])]
        minm_pts = [f"{to_screen(t, e)[0]:.1f},{to_screen(t, e)[1]:.1f}" for t, e in zip(times, st["etasMinmod"])]
        P(f'<polyline points="{" ".join(base_pts)}" fill="none" stroke="{c_base}" stroke-width="1.8"/>')
        P(f'<polyline points="{" ".join(minm_pts)}" fill="none" stroke="{c_minm}" stroke-width="1.8" stroke-dasharray="4,2"/>')

    # Legend
    leg_x = width - pad_r - 330
    leg_y = pad_t + 10
    P(f'<rect x="{leg_x}" y="{leg_y}" width="320" height="95" fill="#21262d" rx="4" stroke="#30363d"/>')
    for idx, st in enumerate(stations):
        c_base, c_minm = colors[idx % len(colors)]
        ly = leg_y + 18 + idx * 24
        P(f'<line x1="{leg_x + 10}" y1="{ly}" x2="{leg_x + 30}" y2="{ly}" stroke="{c_base}" stroke-width="2.0"/>')
        P(f'<line x1="{leg_x + 35}" y1="{ly}" x2="{leg_x + 55}" y2="{ly}" stroke="{c_minm}" stroke-width="2.0" stroke-dasharray="3,2"/>')
        P(f'<text x="{leg_x + 62}" y="{ly + 4}" font-family="system-ui, sans-serif" font-size="11" fill="#f0f6fc">{st["stationName"]} (z={st["stationZ"]:.0f}m, h={st["depth"]:.1f}m)</text>')

    P(f'<text x="{leg_x + 10}" y="{leg_y + 88}" font-family="monospace" font-size="10" fill="#8b949e">Solid = Baseline (1st Upwind) | Dash = Minmod TVD</text>')
    P('</svg>')

    out_docs = os.path.join(DOCS_DIR, out_filename)
    with open(out_docs, "w", encoding="utf-8") as f:
        f.write("\n".join(svg))
    print(f"Saved SVG: {out_docs}")
    if os.path.exists(ARTIFACT_DIR):
        out_art = os.path.join(ARTIFACT_DIR, out_filename)
        shutil.copy2(out_docs, out_art)

def main():
    data = load_data()
    cases_dict = {c["caseId"]: c for c in data.get("cases", [])}

    # 1. Generate Animated GIFs
    print("Generating animated comparison GIFs (10 fps, 1.0x physical time)...")
    # Submerged Mound (Primary visual demonstration)
    if "CaseB_SubmergedMound_WavePacket" in cases_dict:
        create_side_by_side_gif("CaseB_SubmergedMound_WavePacket", "overview", "Scene02_Mound_WavePacket_Overview.gif")
        create_side_by_side_gif("CaseB_SubmergedMound_WavePacket", "closeup", "Scene02_Mound_WavePacket_Closeup.gif")

    # Flat Bed Control
    if "CaseA_Flat_WavePacket" in cases_dict:
        create_side_by_side_gif("CaseA_Flat_WavePacket", "overview", "Scene02_Flat_WavePacket_Overview.gif")

    # Shallow Shelf
    if "CaseC_ShallowShelf_SinglePulse" in cases_dict:
        create_side_by_side_gif("CaseC_ShallowShelf_SinglePulse", "overview", "Scene02_Shelf_SinglePulse_Overview.gif")
        create_side_by_side_gif("CaseC_ShallowShelf_SinglePulse", "closeup", "Scene02_Shelf_SinglePulse_Closeup.gif")

    # Shoal and Cape standard / steep
    if "Case1_StandardWavemaker" in cases_dict:
        create_side_by_side_gif("Case1_StandardWavemaker", "overview", "Scene02_Shoal_Wavemaker_Overview.gif")
        create_side_by_side_gif("Case1_StandardWavemaker", "closeup", "Scene02_Shoal_Wavemaker_Closeup.gif")
    if "Case2_SteepPlanarSoliton" in cases_dict:
        create_side_by_side_gif("Case2_SteepPlanarSoliton", "overview", "Scene02_Steep_Soliton_Overview.gif")
        create_side_by_side_gif("Case2_SteepPlanarSoliton", "closeup", "Scene02_Steep_Soliton_Closeup.gif")

    # 2. Generate Profile Plots
    print("Generating water surface profile SVG plots...")
    if "CaseB_SubmergedMound_WavePacket" in cases_dict:
        caseB = cases_dict["CaseB_SubmergedMound_WavePacket"]
        profB_t8 = [p for p in caseB["profiles"] if abs(p["time"] - 8.0) < 0.2][0]
        profB_t12 = [p for p in caseB["profiles"] if abs(p["time"] - 12.0) < 0.2][0]
        generate_svg_profile(profB_t8, "Preset B (Submerged Mound): Centerline Profile η(z)", "Plot_Scene02_Profiles_Mound_t8s.svg", y_range=(-3.8, 1.2))
        generate_svg_profile(profB_t12, "Preset B (Submerged Mound): Centerline Profile η(z)", "Plot_Scene02_Profiles_Mound_t12s.svg", y_range=(-3.8, 1.2))

    if "CaseC_ShallowShelf_SinglePulse" in cases_dict:
        caseC = cases_dict["CaseC_ShallowShelf_SinglePulse"]
        profC_t12 = [p for p in caseC["profiles"] if abs(p["time"] - 12.0) < 0.2][0]
        generate_svg_profile(profC_t12, "Preset C (Shallow Shelf): Centerline Profile η(z)", "Plot_Scene02_Profiles_Shelf_t12s.svg", y_range=(-3.8, 1.2))

    if "CaseA_Flat_WavePacket" in cases_dict:
        caseA = cases_dict["CaseA_Flat_WavePacket"]
        profA_t8 = [p for p in caseA["profiles"] if abs(p["time"] - 8.0) < 0.2][0]
        generate_svg_profile(profA_t8, "Preset A (Flat Bed Control): Centerline Profile η(z)", "Plot_Scene02_Profiles_Flat_t8s.svg", y_range=(-3.8, 1.2))

    # 3. Generate Time-series Plots
    print("Generating station time-series SVG plots...")
    if "CaseB_SubmergedMound_WavePacket" in cases_dict:
        generate_svg_timeseries(cases_dict["CaseB_SubmergedMound_WavePacket"]["stations"], "Preset B (Submerged Mound): Station Time Series η(t)", "Plot_Scene02_TimeSeries_Mound.svg", y_range=(-0.5, 0.7))
    if "CaseC_ShallowShelf_SinglePulse" in cases_dict:
        generate_svg_timeseries(cases_dict["CaseC_ShallowShelf_SinglePulse"]["stations"], "Preset C (Shallow Shelf): Station Time Series η(t)", "Plot_Scene02_TimeSeries_Shelf.svg", y_range=(-0.5, 0.8))

    print("All comparison artifacts successfully generated and copied to artifact directory!")

if __name__ == "__main__":
    main()
