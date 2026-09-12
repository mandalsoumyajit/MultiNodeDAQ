# Stage 4 plotting decision

Use the built-in WPF DrawingContext/RenderTargetBitmap stack on net10.0-windows.

The GUI draws bounded C# min/max envelopes (400 bins per XYZ axis), PSD/ASD lines (up to 256 frequency bins), and reduced SCF cells (32 x 64). It does not send every sample to a UI control or depend on Python for basic plots. Draw calls are limited to a 5 Hz polling schedule. The UI thread only draws reduced data; receiver, disk and replay scans run separately.

This choice avoids an additional plotting package for the initial operator interface. Our PlotView source is covered by the repository MIT license. The self-contained package redistributes the Microsoft .NET/Windows Desktop runtime under its included license/notices. WPF is Windows-specific; no cross-platform desktop claim is made.

Representative test: 800 x 220 pixel render target, 50 repeated renders after arranging the control. The initial SCF benchmark was 7.31 ms median and 20.24 ms maximum, within the 200 ms update interval. This measures the rendered component, not complete end-to-end acquisition latency. Test artifacts retain the actual run's measurements. DPI/layout and basic pointer readout are supported; advanced publication formatting belongs in offline Python.

Other plotting libraries can be evaluated later if scientific cursor tools or export requirements outgrow this small renderer. No unverified third-party compatibility or licensing claim is needed to ship this implementation.
