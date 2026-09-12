"""Real C# sockets/recording with worker restart and a blocked Python subscriber."""
import json,os,secrets,socket,subprocess,sys,time
from pathlib import Path
import numpy as np
from multinodedaq import open_session
from multinodedaq.client import Client
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'.artifacts/stage3'/('live-'+secrets.token_hex(6));OUT.mkdir(parents=True)
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
sensor,ipc=port(),port();env=dict(os.environ,MULTINODEDAQ_IPC_TOKEN=secrets.token_hex(32));token=env['MULTINODEDAQ_IPC_TOKEN']
dotnet=str(ROOT/'.tools/dotnet/dotnet.exe');processes=[];handles=[]
def launch(args,label):
    log=(OUT/(label+'.log')).open('w');handles.append(log)
    p=subprocess.Popen(args,cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0));processes.append(p);return p

def status():
    with Client(token,ipc) as c:return c.control('status')
try:
    host=launch([dotnet,str(ROOT/'src/MultiNodeDAQ.Host/bin/Release/net10.0/MultiNodeDAQ.Host.dll'),'--port',str(sensor),'--ipc-port',str(ipc),'--record',str(OUT/'recording'),'--seconds','28','--summary',str(OUT/'host.json')],'host')
    deadline=time.monotonic()+10
    while True:
        try:status();break
        except (OSError,ValueError):
            if time.monotonic()>deadline:raise
            time.sleep(.1)
    try:
        Client('x'*64,ipc);raise AssertionError('bad authentication accepted')
    except ValueError:pass
    sim=launch([dotnet,str(ROOT/'src/MultiNodeDAQ.Simulator/bin/Release/net10.0/MultiNodeDAQ.Simulator.dll'),'--port',str(sensor),'--nodes','2','--seconds','24','--encoding','2','--summary',str(OUT/'sim.json')],'simulator')
    deadline=time.monotonic()+10
    while len(status()['acquisition']['units'])<2:
        assert time.monotonic()<deadline;time.sleep(.1)
    units=[u['unit'].lower() for u in status()['acquisition']['units']]
    first=status()['acquisition']['units'][0]['rows']
    with Client(token,ipc) as c:
        requested=c.control('configure_analysis',settings=dict(rate=25000,df=20,dalpha=5,max_frequency=4000,max_alpha=1000,hop_fraction=.25,pair_batch=128))
        assert requested['revision']==1
        try:c.control('configure_analysis',settings=dict(df=-1));raise AssertionError('bad settings accepted')
        except ValueError:pass
    for cycle in range(2):
        worker=launch([sys.executable,'-m','multinodedaq.worker','--port',str(ipc),'--units',*units],f'worker-{cycle}')
        deadline=time.monotonic()+8;previous=None
        while True:
            view=status()
            if view['analysis'] and any(not a['stale'] and a['result']['parameters']['revision']==1 for a in view['analysis']):break
            assert worker.poll() is None,(OUT/f'worker-{cycle}.log').read_text()
            assert time.monotonic()<deadline,'no worker result';time.sleep(.1)
        worker.kill();worker.wait(timeout=5)
        time.sleep(3.5)
        view=status();assert view['analysis'] and all(a['stale'] for a in view['analysis']),'worker health not stale'
        assert view['acquisition']['errors']==0
    # A receiver that stops reading is evicted; it cannot delay the separate recorder.
    stalled=Client(token,ipc);stalled.socket.setsockopt(socket.SOL_SOCKET,socket.SO_RCVBUF,1024)
    stalled.control('subscribe',units=units,stream='samples',history_seconds=0)
    time.sleep(4);view=status();stalled.close()
    assert view['acquisition']['units'][0]['rows']>first and view['acquisition']['errors']==0
    sim.wait(timeout=30);host.wait(timeout=15);assert sim.returncode==host.returncode==0
    verification=subprocess.run([dotnet,str(ROOT/'src/MultiNodeDAQ.Host/bin/Release/net10.0/MultiNodeDAQ.Host.dll'),'--verify',str(OUT/'recording'),'--summary',str(OUT/'verification.json')],cwd=ROOT,capture_output=True,text=True,timeout=90)
    assert verification.returncode==0,verification.stdout+verification.stderr
    source=json.loads((OUT/'sim.json').read_text());produced=sum(s['produced_rows'] for s in source['units'])
    rows=0
    with open_session(OUT/'recording') as session:
        for (unit,acq),state in session.streams.items():
            assert state['ranges']==[(0,state['extent'])]
            for f in session.frames(unit,acq):
                values=np.frombuffer(f.payload,dtype='<i4').reshape(-1,3)
                expected=((np.arange(f.count,dtype=np.int64)[:,None]+f.first_sample)*3+np.arange(3)+17)%16777216-8388608
                np.testing.assert_array_equal(values,expected);rows+=f.count
    assert rows==produced
    report=dict(passed=True,nodes=2,seconds=24,rows=rows,worker_kills=2,stalled_subscribers=1,recording_errors=0,independent_sample_match=True)
    (OUT/'report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(dict(directory=str(OUT),**report),indent=2))
finally:
    for p in processes:
        if p.poll() is None:p.kill();p.wait()
    for h in handles:h.close()
