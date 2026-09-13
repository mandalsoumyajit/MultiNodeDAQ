"""Independently validate a Stage 5 counter recording with the Python log reader."""
import argparse
import json
from pathlib import Path
import numpy as np
from multinodedaq import open_session

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('run',type=Path,help='counter-TIMESTAMP directory')
options=parser.parse_args()
rows=missing=0
with open_session(options.run/'recording') as session:
    assert len(session.streams)==1, 'Expected one Pico'
    for (unit,acquisition),state in session.streams.items():
        assert unit.upper()=='4D4E442D5049434F895DFE4DF2C37EC4', 'Unexpected board'
        seed=state['metadata']['hello']['seed']
        assert seed==17, 'Unexpected counter seed'
        for first in range(0,state['extent'],100000):
            batch=session.read_samples(unit,acquisition,first,min(100000,state['extent']-first))
            missing+=sum(end-start for start,end in batch.gaps)
            expected=((batch.counters[:,None]*np.uint64(3)+np.arange(3,dtype=np.uint64)+np.uint64(seed)) & np.uint64(0xffffff)).astype(np.int64)-8388608
            np.testing.assert_array_equal(batch.codes,expected)
            rows+=len(batch.codes)
        assert rows+missing==state['extent'], 'Sample extent mismatch'
    result=dict(independent_python_verification='pass',rows=rows,missing_rows=missing,counter_errors=0,lossless=(missing==0),manifest_state=session.manifest['state'])
(options.run/'python-verify.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result))
