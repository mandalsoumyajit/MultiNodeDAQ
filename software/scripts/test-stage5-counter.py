"""One-board Stage 5 bench smoke test; requires provisioned Pico on the same LAN."""
import argparse, json, os, pathlib, secrets, subprocess, time
from multinodedaq.client import Client
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--address',default='192.168.1.180')
parser.add_argument('--connect',help='Initiate outbound TCP to a listening Pico IPv4 address')
parser.add_argument('--port',type=int,default=45230)
parser.add_argument('--ipc-port',type=int,default=45231)
options=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1]
out=root/'.artifacts/stage5'/('counter-'+time.strftime('%Y%m%d-%H%M%S'))
out.mkdir(parents=True)
env=dict(os.environ,MULTINODEDAQ_IPC_TOKEN=secrets.token_hex(32),DOTNET_ROOT=str(root/'.tools/dotnet'))
dotnet=str(root/'.tools/dotnet/dotnet.exe')
hostdll=str(root/'src/MultiNodeDAQ.Host/bin/Release/net10.0/MultiNodeDAQ.Host.dll')
log=open(out/'host.log','w')
host=subprocess.Popen([dotnet,hostdll,*( ['--connect',options.connect] if options.connect else ['--address',options.address] ),'--port',str(options.port),'--ipc-port',str(options.ipc_port),'--record',str(out/'recording'),'--summary',str(out/'host.json')],env=env,cwd=root,stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
print(str(out),flush=True)
try:
    time.sleep(1)
    with Client(env['MULTINODEDAQ_IPC_TOKEN'],options.ipc_port) as client:
        deadline=time.monotonic()+150
        while True:
            s=client.control('status')
            nodes=s['acquisition']['units']
            if nodes and nodes[0]['rows']>0: break
            assert host.poll() is None,'host exited'
            if time.monotonic()>deadline: raise TimeoutError('No Pico samples within 150 seconds')
            time.sleep(1)
        print('Pico streaming; measuring 60 seconds',flush=True)
        first=nodes[0]['rows'];start=time.monotonic()
        for _ in range(6):
            time.sleep(10)
            s=client.control('status')
            n=s['acquisition']['units'][0]
            print(json.dumps({k:n[k] for k in ('rows','missing_rows','sample_errors','reported_drops','state')}),flush=True)
        rate=(n['rows']-first)/(time.monotonic()-start)
        stop=client.control('stop_recording')
        assert stop['state']=='complete',stop
        final=client.control('status')
        client.control('shutdown')
    host.wait(timeout=25)
    check=subprocess.run([dotnet,hostdll,'--verify',str(out/'recording'),'--summary',str(out/'verify.json')],env=env,cwd=root,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=60)
    verified=json.loads((out/'verify.json').read_text())
    n=final['acquisition']['units'][0]
    report=dict(rows=verified['Rows'],observed_rows_per_second=rate,missing_rows=n['missing_rows'],sample_errors=n['sample_errors'],reported_drops=n['reported_drops'],verify_exit=check.returncode,recording_state=stop['state'])
    (out/'report.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report),flush=True)
    assert check.returncode==0 and verified['SampleErrors']==0
    assert n['missing_rows']==0 and n['sample_errors']==0 and n['reported_drops']==0
    assert 24500<rate<25500,rate
finally:
    if host.poll() is None:
        try:
            with Client(env['MULTINODEDAQ_IPC_TOKEN'],options.ipc_port) as cleanup:
                cleanup.control('stop_recording')
                cleanup.control('shutdown')
            host.wait(timeout=15)
        except Exception:
            host.kill();host.wait()
    log.close()
