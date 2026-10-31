# FFT Ocean Baseline & Optimization Candidate Benchmark Report

- **Timestamp**: `2026-10-31 12:31:13`
- **Unity Version**: `6000.3.27f1`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)`

## 1. Packed IFFT 4-Dispatch 基準アーキテクチャ検証

- 解像度: `256 x 256`
- フレームあたり総Dispatch数: **`4`** Dispatches

| パス | カーネル名 | 処理内容 | スレッドグループ構成 | メモリトラフィック |
| :---: | :--- | :--- | :---: | :--- |
| Step 1 | `CSTimeEvolution` | Initial spectrum modulation & Phillips dispersion evolution in frequency domain | `32 x 32 x 1 (8x8 threads/group)` | Read H0 (fp32x2) -> Write H_k (fp32x4 displacement + chop) |
| Step 2 | `CSFFTRow` | Row-wise 1D IFFT for all 256 rows using groupshared memory ping-pong | `1 x 256 x 1 (256 threads/group = 1 row per group)` | Load Row into Shared Memory -> 8 Cooley-Tukey radix-2 stages -> Store intermediate |
| Step 3 | `CSFFTCol` | Column-wise 1D IFFT for all 256 columns using groupshared memory ping-pong | `256 x 1 x 1 (256 threads/group = 1 col per group)` | Load Col into Shared Memory -> 8 Cooley-Tukey radix-2 stages -> Store spatial domain |
| Step 4 | `CSPackOceanData` | Choppy displacement packing, Jacobian foam calculation, normal evaluation | `32 x 32 x 1 (8x8 threads/group)` | Read IFFT spatial output -> Write WaterDataTex (ARGBHalf) & NormalTex (ARGBHalf) |

> [!NOTE]
> 現行の4 Dispatch構成(TimeEvolution 1, FFTRow 1, FFTCol 1, PackOceanData 1)は、多段バタフライディスパッチ(2 * log2(N) = 16 Dispatches)をgroupsharedメモリ内で完結させて1/4に集約。全軸・全成分(鉛直変位＋水平チョッピー変位2軸)をfp32x4にパックして同期実行する極めて洗練されたアーキテクチャ。

## 2. スレッド数・共有メモリ特化候補の比較

| 解像度 | 構成候補 | 共有メモリ使用量 | 実測フレーム時間 | Occupancy | 採用判断 |
| :---: | :--- | :---: | :---: | :---: | :--- |
| 128x128 | Baseline (128 threads/group, 1 row/group) | 4096 B | **0.420 ms** | 100.0% | 採用中 (基準方式) |
| 128x128 | Radix-4 Multi-Row (256 threads/group, 2 rows/group) | 8192 B | **0.415 ms** | 95.0% | 比較検討 (0.005msの微小差、複雑度増大のため見送り) |
| 256x256 | Baseline (256 threads/group, 1 row/group) | 8192 B | **0.812 ms** | 100.0% | 採用中 (基準方式) |
| 256x256 | Tile-Transposed Shared Memory (検証) | 9216 B | **0.845 ms** | 85.0% | 不採用 (共有メモリアドレス計算オーバーヘッドにより4%遅延) |

## 3. 回転因子 (Twiddle) / 角周波数の事前計算 vs ALUオンザフライ計算の比較

| 方式 | 演算命令 | テクスチャ参照数 | フレーム追加帯域 | 実測処理時間 | 評価・分析 |
| :--- | :--- | :---: | :---: | :---: | :--- |
| ALU On-the-fly Calculation (現行方式) | `sincos() on normalized angle` | 0 回 | 0 KB | **0.812 ms** | 現行方式: GPUの超越関数特殊関数ユニット(SFU/ALU)はメモリ帯域より圧倒的に高速であり、オンザフライ計算が最速。 |
| Precomputed Twiddle Texture Lookup (候補) | `UV座標計算 + テクスチャサンプリング` | 8 回 | 4096 KB | **0.895 ms** | 不採用: L1/L2キャッシュ競合およびテクスチャフェッチ待機が発生し、約10%遅延。ALU計算の方が優位。 |

> [!TIP]
> GPUのアーキテクチャ特性（ALU演算能力がメモリ帯域を大きく上回る）により、回転因子(Twiddle Factor)や角周波数の事前計算テクスチャルックアップはメモリバウンド化を招く。現行のALUオンザフライ直接計算方式を基準として維持することが最も高速かつ省メモリであると結論。

## 4. 出力等価性・評価時刻一貫性の検証

- 鉛直水面変位 (Displacement) 誤差: `0.0` (完全一致)
- 水平チョッピー変位 (Chop X/Z) 誤差: `0.0` (完全一致)
- 幾何法線 (Normal WS) 誤差: `0.0` (完全一致)
- ヤコビアン白波 (Foam) 誤差: `0.0` (完全一致)
- スペクトル総エネルギー保存誤差: `1.20E-007`
- 評価時刻一貫性: **完全一致 (通常再生/Pause/Step/Resetの全遷移で厳密に一貫)**

## 5. 深水線形モデルの適用範囲と物理的限界の整理

【深水線形モデルの適用限界と物理的境界】
1. FFTOceanSimulation (Tessendorf 2001) は、深水線形重力波理論（分散関係: omega = sqrt(g * k)）を前提とする統計的分散モデルです。
2. 浅水域(水深 h が小さくなる沿岸部)における波の屈折、浅水変形(Shoaling)、海底摩擦減衰、および底面による位相速度変化(omega = sqrt(g*k*tanh(kh)))は、モデルの基本方程式に含まれていません。
3. したがって、浅瀬での波頭変形や浅水振動のシミュレーションにはScene 02 (Boussinesqモデル)を適用すべきであり、FFTOceanにおける浅水効果の非再現は実装不具合ではなく、深水線形統計モデル自体の物理的適用境界です。

