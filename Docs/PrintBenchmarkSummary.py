#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import json
import sys

def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

    with open("Docs/BoussinesqSolitonFissionReport.json", "r", encoding="utf-8-sig") as f:
        d = json.load(f)

    results = d.get("results", [])
    print(f"Total benchmark cases evaluated: {len(results)}\n")
    print(f"{'Case Name':58s} | {'Adv Mode':12s} | {'N':4s} | {'Lead H [m]':11s} | {'Lead z [m]':10s} | {'Peaks':5s} | {'FPS':6s} | Status")
    print("-" * 125)

    for r in results:
        name = r.get("caseName", "")
        adv = r.get("advectionModeName", "Upwind")
        n = r.get("resolutionN", 0)
        h = r.get("leadWaveHeight", 0.0)
        z = r.get("leadWaveZ", 0.0)
        peaks = r.get("numPeaksOnShelf", 0)
        fps = r.get("avgFps", 0.0)
        status = "PASS" if not r.get("hasNaN", False) and not r.get("hasInf", False) else "FAIL"
        print(f"{name:58s} | {adv:12s} | {n:4d} | {h:11.4f} | {z:10.2f} | {peaks:5d} | {fps:6.1f} | {status}")

if __name__ == "__main__":
    main()
