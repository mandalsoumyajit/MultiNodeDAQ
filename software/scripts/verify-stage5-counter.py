"""Independently validate one or more Stage 5 counter recordings."""
import argparse
import json
from pathlib import Path
import numpy as np
from multinodedaq import open_session

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('run',type=Path,help='Run directory containing recording/')
parser.add_argument('--boards',default='895DFE4DF2C37EC4',help='Comma-separated board serials')
options=parser.parse_args()
serials=[s.strip().upper() for s in options.boards.split(',')]
assert serials and len(serials)==len(set(serials)) and all(len(s)==16 and int(s,16)>0 for s in serials)
expected_units={'4D4E442D5049434F'+s for s in serials}
rows=missing=0
streams=[]
with open_session(options.run/'recording') as session:
    assert len(session.streams)==len(expected_units), 'Unexpected acquisition session count'
    assert {unit.upper() for unit,_ in session.streams}==expected_units, 'Unexpected board identities'
    for (unit,acquisition),state in session.streams.items():
        stream_rows=stream_missing=0
        seed=state['metadata']['hello']['seed']
        assert seed==17, 'Unexpected counter seed'
        for first in range(0,state['extent'],100000):
            batch=session.read_samples(unit,acquisition,first,min(100000,state['extent']-first))
            stream_missing+=sum(end-start for start,end in batch.gaps)
            expected=((batch.counters[:,None]*np.uint64(3)+np.arange(3,dtype=np.uint64)+np.uint64(seed)) & np.uint64(0xffffff)).astype(np.int64)-8388608
            np.testing.assert_array_equal(batch.codes,expected)
            stream_rows+=len(batch.codes)
        assert stream_rows+stream_missing==state['extent'], 'Sample extent mismatch'
        streams.append(dict(unit=unit.upper(),session=acquisition,rows=stream_rows,missing_rows=stream_missing,counter_errors=0))
        rows+=stream_rows;missing+=stream_missing
    result=dict(independent_python_verification='pass',rows=rows,missing_rows=missing,counter_errors=0,lossless=(missing==0),manifest_state=session.manifest['state'],units=streams)
(options.run/'python-verify.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result))
