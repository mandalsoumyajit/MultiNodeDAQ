"""Optional restartable SCF analysis process. Never controls recording lifetime."""
import argparse
import base64
import json
import os
import time
from dataclasses import asdict
import numpy as np
from .contracts import check, decode, json_object
from .client import Client, subscribe
from .spectral import FAM, FAMSettings, psd

class AnalysisWorker:
    def __init__(self,settings=FAMSettings(),scale=1.0,units='code'):
        self.revision=0;self.fam=FAM(settings);self.scale=scale;self.units=units;self.buffers={}
    def push(self,f,context,status):
        key=(f.unit.hex(),f.session.hex());check(len(self.buffers)<16 or key in self.buffers,'worker stream capacity')
        state=self.buffers.setdefault(key,dict(values=[],rows=0,first=f.first_sample,next=f.first_sample,signature=None))
        signature=(f.rate,f.config,f.calibration,f.timing)
        reason=None
        if f.flags:reason='sample quality flags'
        if f.rate!=self.fam.settings.rate:reason='unexpected sample rate'
        if f.first_sample!=state['next']:reason='sample gap or replay overlap'
        if state['signature'] not in (None,signature):reason='configuration/timing boundary'
        state['next']=f.first_sample+f.count;state['signature']=signature
        if reason:
            state.update(values=[],rows=0,first=f.first_sample+f.count)
            return [self.result(f,f.first_sample,f.count,False,dict(reason=reason),context,status)]
        if not state['values']:state['first']=f.first_sample
        state['values'].append(np.frombuffer(f.payload,dtype='<i4').reshape(-1,3).copy());state['rows']+=f.count
        output=[]
        while state['rows']>=self.fam.count:
            data=np.concatenate(state['values']);window=data[:self.fam.count];remaining=data[self.fam.count:]
            first=state['first'];state.update(values=[remaining] if len(remaining) else [],rows=len(remaining),first=first+self.fam.count)
            started=time.perf_counter();fam=self.fam.compute(window,first,self.scale);ordinary=psd(window,f.rate,self.scale)
            magnitude=np.abs(fam['scf']);profile=magnitude.max(axis=0)
            fi=np.unique(np.linspace(0,len(fam['frequency_hz'])-1,min(32,len(fam['frequency_hz'])),dtype=int));ai=np.unique(np.linspace(0,len(fam['alpha_hz'])-1,min(64,len(fam['alpha_hz'])),dtype=int))
            pi=np.unique(np.linspace(0,len(ordinary['frequency_hz'])-1,min(256,len(ordinary['frequency_hz'])),dtype=int))
            values=dict(frequency_hz=ordinary['frequency_hz'][pi].tolist(),psd=ordinary['psd'][pi].tolist(),asd=ordinary['asd'][pi].tolist(),power=ordinary['power'].tolist(),rms=ordinary['rms'].tolist(),mean=ordinary['mean'].tolist(),
                        scf_frequency_hz=fam['frequency_hz'][fi].tolist(),alpha_hz=fam['alpha_hz'][ai].tolist(),scf_magnitude=magnitude[fi[:,None],ai[None,:]].tolist(),scf_grid_valid=fam['valid_grid'][fi[:,None],ai[None,:]].tolist(),alpha_profile=profile[ai].tolist(),processing_seconds=time.perf_counter()-started)
            output.append(self.result(f,first,self.fam.count,True,values,context,status))
        return output
    def result(self,f,first,count,valid,values,context,status):
        timing=None
        for encoded in context.get('metadata',[]):
            frame=decode(base64.b64decode(encoded,validate=True))
            if frame.kind==6 and frame.timing==f.timing:timing=json_object(frame.payload)
        if f.timing and (timing is None or int(timing.get('valid_from_sample','0'))>first or int(timing.get('valid_to_sample_exclusive','0'))<first+count):
            valid=False;values=dict(reason='timing model missing or outside validity interval')
        # Precision across nodes is never inferred from nominal sample rate.
        return dict(result='spectral',unit=f.unit.hex(),acquisition_session=f.session.hex(),first_sample=str(first),count=count,valid=valid,
                    algorithm='fam-hamming-v1',parameters=dict(**asdict(self.fam.settings),revision=self.revision,actual_df=self.fam.df,actual_dalpha=self.fam.dalpha,scale=self.scale,units=self.units,config_id=f.config,calibration_id=f.calibration,timing_id=f.timing,time_quality=timing or 'unsynchronized',density='PSD one-sided; SCF two-sided',preview='subsampled diagnostic grid; use compute() for full SCF'),
                    skipped_rows=status.get('skipped_rows',0),values=values)

def main():
    p=argparse.ArgumentParser();p.add_argument('--port',type=int,default=45101);p.add_argument('--units',nargs='+',required=True);p.add_argument('--df',type=float,default=10);p.add_argument('--dalpha',type=float,default=5);p.add_argument('--rate',type=float,default=25000);p.add_argument('--max-alpha',type=float,default=1000);p.add_argument('--max-frequency',type=float,default=5000);a=p.parse_args()
    token=os.environ['MULTINODEDAQ_IPC_TOKEN'];worker=AnalysisWorker(FAMSettings(rate=a.rate,df=a.df,dalpha=a.dalpha,max_alpha=a.max_alpha,max_frequency=a.max_frequency))
    with Client(token,a.port) as control:
        print(json.dumps(dict(state='ready',protocol=1,algorithm='fam-hamming-v1',window_rows=worker.fam.count)),flush=True)
        next_poll=0;seen_revision=0
        try:
            for f,context,status in subscribe(a.units,token,a.port):
                if time.monotonic()>=next_poll:
                    desired=control.control('analysis_settings');next_poll=time.monotonic()+.5
                    if desired['revision']!=seen_revision and desired['settings'] is not None:
                        seen_revision=desired['revision']
                        try:
                            replacement=AnalysisWorker(FAMSettings(**desired['settings']));replacement.revision=seen_revision
                            worker=replacement
                        except (ValueError,TypeError,OverflowError) as error:
                            result=worker.result(f,f.first_sample,0,False,dict(reason='settings rejected: '+str(error),rejected_revision=seen_revision),context,status)
                            control.result(result)
                for result in worker.push(f,context,status):control.result(result)
        except KeyboardInterrupt:pass

if __name__=='__main__':main()
