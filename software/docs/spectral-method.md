# Paper-based spectral processing

Method source: *ELF Passive Radio Sensing and AI-Perception of Micro-UAS*, local manuscript `jsen.tex`, SCF/FAM section. The implementation follows windowed FFT channelization, demodulation, conjugate channel products, and FFT accumulation. The manuscript cites [Roberts, Brown and Loomis, 1991](https://doi.org/10.1109/79.81008); the channel-product formulation is also described in [Fixed-point FPGA Implementation of the FFT Accumulation Method for Real-time Cyclostationary Analysis](https://doi.org/10.1145/3567429). No manuscript images, training data or classifier weights are distributed here.

## Explicit interpretation and settings

The manuscript describes 50% overlap alongside `L = Np/4`; these conflict. We use the explicit quarter-window hop (75% overlap) by default and expose `hop_fraction` for comparison. The prose also describes replication and input multiplication, without complete indexing or normalization. This is a conventional conjugate-channel FAM implementation, not a claim of byte-identical reproduction of an unavailable original Python program or published classifier images.

At the current 25 kSPS rate, defaults request spectral resolution 10 Hz and cyclic resolution 5 Hz:

- `Np = next_fast_len(ceil(fs / df)) = 2500`.
- `L = round(Np * hop_fraction) = 625`.
- `P = next_fast_len(ceil(fs / (L * dalpha))) = 8` (at least four frames).
- Actual bin spacings: `fs/Np = 10 Hz`, `fs/(LP) = 5 Hz`.
- Input span: `(P-1)L + Np = 6875` samples, or 275 ms. No fabricated zero padding extends a short record. FFT accumulation spans frame-start positions over `LP/fs`; input support additionally includes the channelizer window tail.

These are bin spacings, not claims that the Hamming spectral main lobe has 10 Hz width. The paper's 3.46 MSPS ADC setting is not copied into a 25 kSPS stream. High-rate data must be properly anti-alias filtered/decimated before using fine resolutions within the working limits; automatic decimation is not implemented.

## Estimator

For frame `r`, let `X_r[k]` be the centered FFT of samples beginning at `rL`, tapered by a periodic Hamming window `w`. Demodulate as `D_r[k] = X_r[k] exp(-j 2 pi f_k rL/fs)`. Then compute

```
S[k,l,q] = sum_r D_r[k] conj(D_r[l]) exp(-j 2 pi q r/P)
           / (P fs sum_n w[n]^2)
alpha = f_k - f_l + q fs/(LP)
f_center = (f_k + f_l)/2
```

The slow-time taper is rectangular. Keep residual cyclic frequencies in `[-df/2, df/2)` to select the nearest coarse channel-difference cell. Grid coordinates are `df/2` and achieved `dalpha`; only supported coordinates are marked valid. Noncommensurate settings can leave additional unsupported coordinates, which are not interpolated. Repeated estimates at one grid coordinate are averaged. Complex SCF values are preserved; magnitude is a derived display.

The manuscript uses an asymmetric-lag CAF, `x[n] conj(x[n-l])`. Its spectral coordinate equals `f_center - alpha/2`. The API labels the conventional centered coordinate explicitly; conversion is necessary when comparing plots from another convention. Global sample-counter phase is retained without converting a 64-bit counter to a floating-point timestamp. Timing between separate unsynchronized nodes is not inferred from this phase convention.

SCF is a two-sided density in squared input units/Hz. Alpha zero is the corresponding averaged two-sided spectrum at supported cells. Conventional PSD/ASD diagnostics use a separately labeled one-sided Hamming periodogram with no detrending; do not apply its factor of two indiscriminately to the SCF. Linear amplitude conversion by `scale` produces quadratic density scaling.

## Efficiency and limits

A reusable `FAM` plan caches the taper, demodulation kernel and eligible channel pairs. Window extraction uses a strided view; FFTs and pair products are vectorized over all three axes. Pairs are processed in configurable batches (default 256), so no full channel-pair-by-time tensor is allocated. Only the requested `f` and `alpha` bands are evaluated. Defaults produce a 1001-by-201-by-3 complex array with a separate validity mask, using 50,551 eligible pairs.

Limits include one million input rows, 16,384 channelizer points, 4,096 accumulation frames, 128 MiB planned channelizer storage, one million output grid cells and 250,000 channel pairs. A requested configuration exceeding these bounds is rejected; the GUI can ask for coarser resolution or narrower bands. The implementation uses one FFT worker per call to avoid competing thread pools across selected units. Default compute latency was about 36 ms for three axes in the initial laptop measurement; see the saved benchmark for repeated timings. This does not establish full sixteen-node simultaneous analysis capacity.

## Numerical verification

Tests compare one SCF estimate against independently evaluated complex demodulates and a direct accumulation; require batched/full-batch and live/offline windows to agree within `rtol=1e-12` (absolute tolerance covers values near zero). Two exact channel-bin tones verify the nonzero-alpha cross-density integral to 1%. A calibrated tone verifies one-sided integrated power to 1%; 20 seeded broadband trials use 4% tolerance on mean power. Tests also cover sample counters above 2^63, gap reset and lossless recording export.

The SCF extraction is implemented here. CNN inference, image normalization matching the original training pipeline, and drone-classification accuracy claims require the original preprocessing specification and trained model, and remain separate work.
