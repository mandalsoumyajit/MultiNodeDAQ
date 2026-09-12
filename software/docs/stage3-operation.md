# Stage 3: Python access and live SCF analysis

The C# host still records with Python absent. Optional loopback IPC is enabled with `--ipc-port`; its token is supplied through `MULTINODEDAQ_IPC_TOKEN`, never printed in application logs. The GUI is Stage 4; the controls it will use are implemented now.

## Setup and tests

From `software`, use Windows x64 Python 3.12 for the pinned analysis environment:

```powershell
.\scripts\setup-stage3.ps1 -Python C:\path\to\python.exe
.\scripts\test-stage3.ps1
```

`setup-stage3.ps1` installs NumPy 2.2.6, SciPy 1.15.3, h5py 3.14.0 and setuptools 80.9.0 with wheel hashes. `test.ps1` automatically selects `.venv` if present. The contract-only Python tests still need only the standard library: `python -m unittest discover -s python/tests -p test_contracts.py`. Recording does not import or launch Python.

## Live example

Set the same random token in the host and worker terminal environments. Generate it once, transfer it through your local launch configuration, and avoid committing it. Example generation:

```powershell
$env:MULTINODEDAQ_IPC_TOKEN = & .\.venv\Scripts\python.exe -c "import secrets; print(secrets.token_hex(32))"
```

Start the host, then simulator in separate terminals:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/MultiNodeDAQ.Host -c Release --no-build -- --ipc-port 45101 --record C:\data\run-001 --seconds 60
.\.tools\dotnet\dotnet.exe run --project src/MultiNodeDAQ.Simulator -c Release --no-build -- --nodes 2 --seconds 50
```

Copy selected unit IDs from host status. The simulator's first unit is shown below:

```powershell
.\.venv\Scripts\multinodedaq-analysis.exe --port 45101 --units 53000000000000000000000001000000
```

A worker prints a ready handshake, then sends bounded results to the host on its control connection. Its subscription uses a separate socket. Stop/restart the worker independently; a result becomes stale after three seconds without an update. The host retains the last result with its age rather than displaying it as current. Ctrl+C exits the worker; its lifetime never owns the recorder.

## Controls for the future GUI

```python
from multinodedaq.client import Client
with Client(token, port=45101) as client:
    state = client.control('status')
    requested = client.control('configure_analysis', settings={
        'rate': 25000, 'df': 10, 'dalpha': 5,
        'max_frequency': 5000, 'max_alpha': 1000,
        'hop_fraction': 0.25, 'pair_batch': 256,
    })
    desired = client.control('analysis_settings')
```

Settings currently apply globally to attached workers. A requested revision is not proof of application. Workers poll at most twice per second, validate computational limits, discard partial old windows, and include the applied revision and achieved resolutions in every result. A worker reports an invalid result if it rejects a revision; existing settings continue. Compare requested and applied revisions, and show pending/unavailable states if a worker is absent. Selected units are chosen at worker/subscription startup; replacing that process changes selection. The rate must match the node's nominal rate; changing analysis rate does not reconfigure the ADC.

The API supports `status`, `analysis_settings`, `configure_analysis`, session-checked `node_command`, and live `subscribe`. It returns explicit errors for unsupported recording-control or remote range operations. Recording remains controlled by the host CLI. Offline ranges use the direct Python reader. Close a bulk socket to unsubscribe. Only `samples` with initial history zero is currently supported; no pre-subscription backfill is claimed.

## Resource and validity behavior

- At most 16 IPC connections, eight subscriptions, and 16 selected unit IDs per subscription.
- Each subscriber has an 8 MiB queue and at most two seconds of queued samples per unit. Old work is evicted with cumulative `skipped_rows`; a socket write stalled for two seconds disconnects that consumer. Queued data older than two seconds is also discarded at dequeue. Kernel and one in-flight packet are additional bounded buffers.
- Metadata cache: 128 acquisition sessions, up to 1 MiB each. Results: 128 unit/session entries, up to 256 KiB each. Subscriber queues and the recorder's queues are independent.
- Gaps, duplicate/reordered samples, configuration/timing changes, quality flags and rate mismatches invalidate/reset analysis windows. No interpolation conceals a gap. Timing remains explicitly unsynchronized unless a recorded model covers the result interval. Calibration ID zero means no instrument calibration; the optional offline scalar scale is an explicit user conversion, not inferred sensor calibration.
- Full FAM arrays stay in Python. IPC returns a reduced 32-by-64 SCF preview, an alpha profile, reduced PSD/ASD spectra and power/RMS/mean trends. Missing FAM grid cells carry a validity mask. These reduced displays are not classifier training images or substitutes for full-resolution offline arrays.

## Offline reader and export

```python
from multinodedaq import open_session, export_hdf5
with open_session(r'C:\data\run-001') as session:
    unit, acquisition_session = next(iter(session.streams))
    batch = session.read_samples(unit, acquisition_session, first=0, count=4096)
    # batch.codes: int32 XYZ; counters: uint64; gaps: explicit [first,end) intervals
    # flags/config/calibration/timing: per-row IDs; metadata: original definitions
    export_hdf5(session, 'run-001.h5')
```

The independent reader verifies hashes, record CRCs, identity, metadata dependencies, segment chains, overlaps, commits and footers. It requires a complete recording. Stage 2 C# retains explicit incomplete-prefix recovery. Python builds a disposable SQLite index on disk; at most one million requested rows are returned in one batch. Temporary disk space and a full initial scan are required.

HDF5 contains convenient codes/block-index arrays plus a lossless archive of the original manifest and every log byte. Gaps are represented by sample counters, not padded codes. `multinodedaq.export.restore_hdf5(path, new_directory)` restores and revalidates the archive. Failed exports remain marked incomplete; they never change the source or block recorder close. The archive duplicates raw storage in addition to convenient arrays, favoring exact reproducibility over file size.

See [offline notebook](../python/notebooks/offline_scf.ipynb) and [SCF method](spectral-method.md).

## Offline distribution

```powershell
.\.venv\Scripts\python.exe scripts/package-stage3.py --output .artifacts/stage3/python-bundle
```

This creates a ZIP containing wheels, hashes, license information, source-commit provenance and `install.ps1`. Transfer it to a Windows x64 machine with Python 3.12 already installed; installation uses `--no-index` and verifies every wheel hash. No network is required at installation/runtime. This is the optional analysis bundle, not the future complete recorder/GUI installer or a bundled Python interpreter. Never copy a development virtual environment between machines.
