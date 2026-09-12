import base64
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from dataclasses import replace
import numpy as np
from scipy import signal
from multinodedaq.contracts import Frame, encode, record_encode, log_header, ContractError, record_decode
from multinodedaq import open_session, export_hdf5
from multinodedaq.export import restore_hdf5
from multinodedaq.spectral import FAM, FAMSettings, psd
from multinodedaq.client import Client, subscribe
from multinodedaq.worker import AnalysisWorker

ROOT=Path(__file__).resolve().parents[2]
UNIT='53000000000000000000000001000000';ACQ='00000000000000000000000000000001'
J=lambda value:json.dumps(value,separators=(',',':')).encode()
def frame(kind,payload,first=0,count=0,config=1,timing=0):
    return Frame(kind,0,bytes.fromhex(UNIT),bytes.fromhex(ACQ),0,first,count,4096,config,0,timing,3 if kind==2 else 0,2 if kind==2 else 0,payload)

def make_log(path):
    path.mkdir();name=f'{UNIT}-{ACQ}-00000.elflog'
    data=bytearray(log_header(bytes.fromhex(ACQ)));n=0
    def record(kind,payload):
        nonlocal n
        data.extend(record_encode(kind,payload));n+=1
    record(2,J(dict(event='recording_started',monotonic_ns='0',details=dict(unit=UNIT,acquisition_session=ACQ,segment=0,prior_ranges=[],prior_extent='0'))))
    hello=frame(1,J(dict(firmware='test',protocol=1,synthetic=True,axes=['X','Y','Z'],encodings=[2],capabilities=['status'],mode='tone')))
    config=frame(5,J(dict(request_id='1',ok=True,state='armed',effective_sample='0',error=None,details=dict(config=dict(id=1,sample_rate_hz=4096,encoding=2,synthetic=True)))))
    record(1,encode(hello));record(1,encode(config))
    timing=frame(6,J(dict(model_id=1,reference_epoch=ACQ,anchor_sample='0',anchor_time_ns='0',period_num_ns='1000000000',period_den=4096,uncertainty_ns='1000',valid_from_sample='0',valid_to_sample_exclusive='10000',source='local',quality='unsynchronized')),timing=1)
    record(1,encode(timing))
    rows=0
    for first,count in [(0,256),(512,256)]:
        codes=np.arange(first*3,(first+count)*3,dtype='<i4').reshape(-1,3)
        record(1,encode(frame(2,codes.tobytes(),first,count,timing=1)));rows+=count
    record(1,encode(frame(7,J(dict(first_sample='256',count='256',reason='test',recoverable=False)),first=256)))
    record(3,J(dict(through_offset=str(len(data)),unit=UNIT,acquisition_session=ACQ,next_sample='256')))
    record(4,J(dict(status='complete',records=str(n),sample_rows=str(rows))))
    (path/name).write_bytes(data)
    (path/'manifest.json').write_bytes(J(dict(state='complete',recording_session=ACQ,segments=[dict(file=name,sha256=hashlib.sha256(data).hexdigest())])))
    return path

class Stage3(unittest.TestCase):
    def test_independent_reader_and_lossless_hdf5(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);source=make_log(root/'source')
            with open_session(source) as session:
                b=session.read_samples(UNIT,ACQ,0,768)
                self.assertEqual(b.gaps,[(256,512)])
                np.testing.assert_array_equal(b.codes[:,0],b.counters*3)
                self.assertTrue(np.all(b.timing==1));self.assertIn('timing:1',b.metadata)
                export_hdf5(session,root/'export.h5')
            restore_hdf5(root/'export.h5',root/'restored')
            for p in source.iterdir():self.assertEqual(p.read_bytes(),(root/'restored'/p.name).read_bytes())
            with open_session(root/'restored') as session:
                np.testing.assert_array_equal(session.read_samples(UNIT,ACQ,0,768).codes,b.codes)
            # Even recomputed manifest hashes cannot hide semantic commit corruption.
            name=next(source.glob('*.elflog'));original=name.read_bytes();raw=bytearray(original[:36]);offset=36
            while offset<len(original):
                size=int.from_bytes(original[offset+8:offset+12],'little');kind,payload=record_decode(original[offset:offset+size])
                if kind==3:payload=payload.replace(b'"next_sample":"256"',b'"next_sample":"768"')
                raw.extend(record_encode(kind,payload));offset+=size
            name.write_bytes(raw)
            m=json.loads((source/'manifest.json').read_bytes());m['segments'][0]['sha256']=hashlib.sha256(raw).hexdigest();(source/'manifest.json').write_bytes(J(m))
            with self.assertRaises(ContractError):open_session(source)

    def test_psd_calibrated_tone_and_seeded_noise(self):
        n=4096;t=np.arange(n)/4096;x=2000*np.cos(2*np.pi*512*t)
        self.assertAlmostEqual(psd(x,4096,.001)['power'][0],2.0,delta=.02)
        rng=np.random.default_rng(819);powers=[psd(rng.normal(size=(n,3)),4096)['power'] for _ in range(20)]
        np.testing.assert_allclose(np.mean(powers,axis=0),np.ones(3),rtol=.04)

    def test_fam_against_direct_accumulation_and_tones(self):
        fam=FAM(FAMSettings(rate=4096,df=64,dalpha=16,max_frequency=1500,max_alpha=512,pair_batch=7))
        t=np.arange(fam.count)/4096;x=np.cos(2*np.pi*512*t)+np.cos(2*np.pi*768*t)
        actual=fam.compute(x)
        fi=np.argmin(abs(actual['frequency_hz']-640));ai=np.argmin(abs(actual['alpha_hz']-256))
        # Independent direct sums for the chosen channel pair and zero residual.
        k,l=768,512;products=[];w=signal.windows.hamming(fam.nfft,sym=False)
        for r in range(fam.frames):
            absolute=np.arange(fam.nfft)+r*fam.hop
            values=x[r*fam.hop:r*fam.hop+fam.nfft]*w
            a=np.sum(values*np.exp(-2j*np.pi*k*absolute/4096));b=np.sum(values*np.exp(-2j*np.pi*l*absolute/4096))
            products.append(a*b.conjugate())
        expected=np.mean(products)/(4096*np.sum(w*w))
        np.testing.assert_allclose(actual['scf'][fi,ai,0],expected,rtol=1e-12,atol=1e-15)
        self.assertAlmostEqual(np.sum(actual['scf'][:,ai,0].real)*fam.df,.25,delta=.0025)
        # Nonzero residual cyclic bin checks the second FFT's sign and indexing.
        off=np.cos(2*np.pi*784*t)+np.cos(2*np.pi*512*t);products=[]
        for r in range(fam.frames):
            absolute=np.arange(fam.nfft)+r*fam.hop
            values=off[r*fam.hop:r*fam.hop+fam.nfft]*w
            a=np.sum(values*np.exp(-2j*np.pi*768*absolute/4096));b=np.sum(values*np.exp(-2j*np.pi*512*absolute/4096))
            products.append(a*b.conjugate()*np.exp(-2j*np.pi*16*r*fam.hop/4096))
        expected_residual=np.mean(products)/(4096*np.sum(w*w))
        residual_result=fam.compute(off)
        np.testing.assert_allclose(residual_result['scf'][fi,17,0],expected_residual,rtol=1e-12,atol=1e-15)
        full=FAM(replace(fam.settings,pair_batch=256)).compute(x)
        np.testing.assert_allclose(actual['scf'],full['scf'],rtol=1e-12,atol=1e-15)
        moved=fam.compute(x,first_sample=2**63+123)
        phase=np.exp(-2j*np.pi*actual['alpha_hz'][ai]*((2**63+123)%(fam.hop*fam.frames))/4096)
        np.testing.assert_allclose(moved['scf'][fi,ai],actual['scf'][fi,ai]*phase,rtol=1e-12)

    def test_live_and_replay_window_match_and_gaps(self):
        settings=FAMSettings(rate=4096,df=64,dalpha=16,max_frequency=1500,max_alpha=512)
        worker=AnalysisWorker(settings);n=worker.fam.count
        x=np.rint(1000*np.cos(2*np.pi*512*np.arange(n)/4096)).astype('<i4');xyz=np.repeat(x[:,None],3,axis=1)
        results=[]
        for start in range(0,n,37):
            values=xyz[start:start+37];results.extend(worker.push(frame(2,values.tobytes(),start,len(values)),{},{}))
        self.assertEqual(len(results),1);self.assertTrue(results[0]['valid'])
        expected=worker.fam.compute(xyz);fi=np.unique(np.linspace(0,len(expected['frequency_hz'])-1,min(32,len(expected['frequency_hz'])),dtype=int));ai=np.unique(np.linspace(0,len(expected['alpha_hz'])-1,min(64,len(expected['alpha_hz'])),dtype=int))
        np.testing.assert_allclose(results[0]['values']['scf_magnitude'],abs(expected['scf'])[fi[:,None],ai[None,:]],rtol=1e-12,atol=1e-12)
        invalid=worker.push(frame(2,xyz[:37].tobytes(),n+10,37),{},{});self.assertFalse(invalid[0]['valid'])

if __name__=='__main__':unittest.main()
