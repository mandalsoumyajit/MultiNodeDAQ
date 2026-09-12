"""Repeated FAM latency measurement; no claim of whole-fleet RF qualification."""
import json,time,statistics
from pathlib import Path
import numpy as np
from multinodedaq.spectral import FAM
start=time.perf_counter();fam=FAM();plan=time.perf_counter()-start
values=np.random.default_rng(481).normal(size=(fam.count,3))
fam.compute(values)
times=[]
for _ in range(20):
    start=time.perf_counter();result=fam.compute(values);times.append(time.perf_counter()-start)
report=dict(settings=vars(fam.settings),plan_seconds=plan,rows=fam.count,axes=3,pairs=len(fam.pairs),grid=list(result['scf'].shape),repeats=len(times),median_seconds=statistics.median(times),max_seconds=max(times),window_seconds=fam.count/fam.settings.rate,scf_bytes=result['scf'].nbytes,valid_grid_bytes=result['valid_grid'].nbytes)
output=Path('.artifacts/stage3/spectral-benchmark.json');output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,indent=2))
