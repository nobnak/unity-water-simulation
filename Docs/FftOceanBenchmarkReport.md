# FFT Ocean Baseline & Optimization Candidate Benchmark Report

- **Timestamp**: `2026-10-31 13:17:40`
- **Unity Version**: `6000.3.27f1`
- **GPU**: `Intel(R) Arc(TM) 140V GPU (16GB)`

## 1. Packed IFFT 4-Dispatch 基準アーキテクチャ検証

- 解像度: `256 x 256`
- フレームあたり総Dispatch数: **`4`** Dispatches 【解析計算】
- VRAM テクスチャ使用量: **`5.0 MB`** (4x ARGBFloat + 2x ARGBHalf) 【解析計算】

| パス | カーネル名 | 処理内容 | スレッドグループ構成・共有メモリ | メモリトラフィック | 分類 |
| :---: | :--- | :--- | :--- | :--- | :---: |
| Step 1 | `CSTimeEvolution` | Initial spectrum modulation & Phillips dispersion evolution in frequency domain | `32 x 32 x 1 (8x8 threads/group)` | Read InitialSpectrum (RGBA32F) -> Write SpectrumPacked (RGBA32F) | 【解析計算】 |
| Step 2 | `CSFFTRow` | Row-wise 1D IFFT for all N rows using groupshared memory ping-pong | `1 x 256 x 1 (numthreads=512, 16KB shared memory: s_fftDataA[512] + s_fftDataB[512])` | Load Row into Shared Memory -> 8 Cooley-Tukey radix-2 stages -> Store PingPongTex | 【解析計算】 |
| Step 3 | `CSFFTCol` | Column-wise 1D IFFT for all N columns using groupshared memory ping-pong | `256 x 1 x 1 (numthreads=512, 16KB shared memory: s_fftDataA[512] + s_fftDataB[512])` | Load Col into Shared Memory -> 8 Cooley-Tukey radix-2 stages -> Store PingPongTex | 【解析計算】 |
| Step 4 | `CSPackOceanData` | Choppy displacement packing, Jacobian foam calculation, normal evaluation | `32 x 32 x 1 (8x8 threads/group)` | Read IFFT spatial output -> Write WaterDataTex (ARGBHalf) & NormalTex (ARGBHalf) | 【解析計算】 |

> [!NOTE]
> 現行の4 Dispatch構成(TimeEvolution 1, FFTRow 1, FFTCol 1, PackOceanData 1)は、多段バタフライディスパッチ(2 * log2(N) = 16 Dispatches)をgroupsharedメモリ内で完結させて1/4に集約。HLSL実装では MAX_FFT_N = 512 定義により [numthreads(512, 1, 1)] かつ 共有メモリ 16,384 bytes (16 KB) を確保。全軸・全成分(鉛直変位＋水平チョッピー変位2軸)をfp32x4にパックして同期実行する洗練された構成。

## 2. スレッド数・共有メモリ特化候補の比較

| 解像度 | 構成候補 | 分類 | 共有メモリ使用量 | 実測フレーム時間 | Occupancy | 採用判断・備考 |
| :---: | :--- | :---: | :---: | :---: | :---: | :--- |
| 256x256 | Baseline (numthreads=512, MAX_FFT_N=512, 1 row/group) | 【解析計算】/【実装準拠】 | 16384 B (16 KB) | 稼働中 (4 Dispatch) | 実測基準 | 区分 B (採用中・基準方式) (FFTOcean.compute 現行実装。N=256 では前半256スレッドが稼働し、16KB共有メモリ内で全8段バタフライを完結。) |
| 256x256 | Radix-4 Multi-Row (候補) | 【未実施】 | 16384 B (16 KB) | 未計測 | 未計測 | 区分 C (判断保留・未実装) (Radix-4 化によるステップ数半減の仮説はあるが、シェーダー未実装のためGPU実測値・Occupancy測定値なし。) |
| 256x256 | Tile-Transposed Shared Memory (候補) | 【未実施】 | 未計測 | 未計測 | 未計測 | 区分 C (判断保留・未実装) (行列転置を用いた2D FFT集約候補。シェーダー未実装のためGPU実測値・Occupancy測定値なし。) |

## 3. 回転因子 (Twiddle) / 角周波数の事前計算 vs ALUオンザフライ計算の比較

| 方式 | 分類 | 演算命令 | テクスチャ参照数 | フレーム追加帯域 | 実測処理時間 | 評価・分析 |
| :--- | :---: | :--- | :---: | :---: | :---: | :--- |
| ALU On-the-fly Calculation (現行方式) | 【解析計算】/【実装準拠】 | `sincos() on normalized angle (2.0 * PI * j / P)` | 0 回 | 0 KB | 稼働中 (追加帯域 0) | 現行採用方式。GPU超越関数ALUによる直接計算で、追加テクスチャフェッチおよび外部メモリ帯域消費がゼロ。 |
| Precomputed Twiddle Texture Lookup (候補) | 【未実施】 | `UV座標計算 + テクスチャサンプリング (理論値)` | 8 回 | 4096 KB | 未計測 | 未実装のためGPU実測は未実施。以前の「10%遅い」という断定は撤回。理論的にはテクスチャサンプリングとキャッシュ競合の懸念があるが実測未計測。 |

> [!TIP]
> 現行のALUオンザフライ直接計算方式は追加メモリ帯域 0 で動作しており、基準方式として維持。事前計算Twiddleテクスチャルックアップはシェーダー未実装のため【未実施】に分類し、未実測の処理時間差や不採用断定は撤回して判断保留とする。

## 4. 出力等価性・評価時刻一貫性の検証

- 分類: **【実測】**
- 鉛直水面変位 (Displacement) 誤差: `0.0` (完全一致)
- 水平チョッピー変位 (Chop X/Z) 誤差: `0.0` (完全一致)
- 幾何法線 (Normal WS) 誤差: `0.0` (完全一致)
- ヤコビアン白波 (Foam) 誤差: `0.0` (完全一致)
- スペクトル総エネルギー保存誤差: `1.20E-007`
- 評価時刻一貫性: **完全一致 (通常再生/Pause/Step/Resetの全遷移で厳密に一貫)**
- 総合判定: **PASS (FFTEvaluationTimeVerifier 実機検証済み)**

## 5. 深水線形モデルの適用範囲と物理的限界の整理

【深水線形モデルの適用限界と物理的境界】
1. FFTOceanSimulation (Tessendorf 2001) は、深水線形重力波理論（分散関係: omega = sqrt(g * k)）を前提とする統計的分散モデルです。
2. 浅水域(水深 h が小さくなる沿岸部)における波の屈折、浅水変形(Shoaling)、海底摩擦減衰、および底面による位相速度変化(omega = sqrt(g*k*tanh(kh)))は、モデルの基本方程式に含まれていません。
3. したがって、浅瀬での波頭変形や浅水振動のシミュレーションにはScene 02 (Boussinesqモデル)を適用すべきであり、FFTOceanにおける浅水効果の非再現は実装不具合ではなく、深水線形統計モデル自体の物理的適用境界です。

