# Reference data provenance

## NTHMP BP05 (laboratory: solitary wave on composite beach) — `NTHMP_BP05/`
- **Experiment**: US Army Corps of Engineers, Coastal & Hydraulics Laboratory (CHL) tsunami flume, "Tsunami Benchmark Problem #3 (runup of solitary waves on a vertical wall)"; NTHMP Benchmark Problem 5.
- **Retrieved from**: https://github.com/rjleveque/nthmp-benchmark-problems (`BP05-ElenaT-Solitary_wave_on_composite_beach_laboratory/`), 2026-11-01. Original data location cited in `BP5_description.pdf`: CHL `tsunami3_runup.zip`.
- **Files**: `gA.txt`, `gB.txt`, `gC.txt` (gauges 4-10 free-surface elevation [m], 0.05 s sampling, t = 265-295 s), `zip/` (paddle trajectories, runup, readme), `BP5_description.pdf` (+ extracted text).
- **Conditions used**: still-water depth 0.218 m at the paddle side; flat 15.04 m, 1/53 slope 4.36 m, 1/150 slope 2.93 m, 1/13 slope 0.90 m to a vertical wall, total 23.23 m (dimension lines of Fig. 1). Target H/d = 0.05 (A), 0.30 (B), 0.70 (C); actual (run3abc.txt) 0.039 / 0.264 / 0.696.
- **Time origin**: the paddle stroke is not tied to a known t=0 (PDF §0.1); the GPU series is therefore aligned in time at gauge 5 and not rescaled in amplitude.
- **Gauge positions**: g5 (15.04 m), g7 (19.40 m), g9 (22.33 m) follow from dimension lines. g6 and g8 are interpolated from the sketch pixels (sketch not to scale, ±~0.2 m) and are reported but not judged. **g4 position is not published as a number and differs between cases (PDF §0.3)** → not used.
- **Consistency check of the digitized geometry** against the data: linear-theory travel time g5→g7 with the digitized depths, c = sqrt(g(d+η)): 3.25 s (A) / 2.85 s (B) vs measured half-maximum crossing difference 3.31 s / 2.91 s (+1.9 % / +2.0 %).
- **Model differences / limits**: solution is 1-D in a flume, our solver is a 2-D Boussinesq grid (periodic/uniform in x). `SampleDepth` floors depth at 0.2 m, so lengths are Froude-scaled by λ=2 (time ×√2, η ×2); the 1/13 beach and the wall are replaced by a flat extension plus sponge, so g9 amplitudes after the wall reflection are not comparable, and Case C (breaking) is not used.
- **Does it contain soliton fission?** No documented fission (gentle slopes, H/d ≤ 0.26 over non-breaking propagation). It validates non-breaking solitary-wave propagation/shoaling only.

## Not obtained
No openly downloadable, digitally published laboratory data set for a solitary wave on a shelf/step with fission (e.g. Seabra-Santos et al. 1987, Madsen & Mei 1969, Losada et al. 1989) was found; Zenodo/GitHub searches returned only internal-wave or unrelated sets. Figure digitization from the paywalled papers was not performed, so **no shelf-fission reference exists in this repository**.
