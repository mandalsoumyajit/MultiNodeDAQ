"""Battery-powered multi-Pico counter endurance test with independent verification."""
import argparse
import ctypes
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
from multinodedaq.client import Client

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--connect',required=True,help='Comma-separated Pico IPv4 addresses')
parser.add_argument('--boards',default='895DFE4DF2C37EC4,1A0D3F9F4FDF64D9')
parser.add_argument('--seconds',type=int,default=7200)
parser.add_argument('--port',type=int,default=45230)
parser.add_argument('--host-dll',type=Path,help='Isolated diagnostic host build')
parser.add_argument('--require-diagnostics',action='store_true')
parser.add_argument('--ipc-port',type=int,default=45231)
parser.add_argument('--output',type=Path,required=True)
options=parser.parse_args()
serials=[s.strip().upper() for s in options.boards.split(',')]
expected={'4D4E442D5049434F'+s for s in serials}
assert options.seconds>0 and len(expected)==len(serials)==len(options.connect.split(','))
assert all(len(s)==16 and int(s,16)>0 for s in serials)
root=Path(__file__).resolve().parents[1]
out=options.output.resolve();out.mkdir(parents=True,exist_ok=False)
def save(name,value):
    temp=out/(name+'.tmp');temp.write_text(json.dumps(value,indent=2));os.replace(temp,out/name)
def status(state,**values):
    save('status.json',dict(state=state,updated_at=datetime.now(timezone.utc).isoformat(),duration_seconds=options.seconds,boards=serials,**values))
def nodes_by_unit(snapshot):
    nodes=snapshot['acquisition']['units']
    assert len(nodes)<=len(expected), 'Unexpected extra acquisition session'
    found={n['unit'].upper():n for n in nodes}
    assert len(found)==len(nodes) and set(found)<=expected, 'Unexpected board/session'
    return found
status('starting')
env=dict(os.environ,MULTINODEDAQ_IPC_TOKEN=secrets.token_hex(32),DOTNET_ROOT=str(root/'.tools/dotnet'))
dotnet=str(root/'.tools/dotnet/dotnet.exe')
hostdll=str(options.host_dll.resolve() if options.host_dll else root/'src/MultiNodeDAQ.Host/bin/Release/net10.0/MultiNodeDAQ.Host.dll')
log=(out/'host.log').open('w')
host=None
try:
    # Thread-scoped request is released on exit; no persistent power policy change.
    if os.name=="nt" and not ctypes.windll.kernel32.SetThreadExecutionState(0x80000001):
        raise OSError("Could not hold the PC awake for acquisition")
    host=subprocess.Popen([dotnet,hostdll,'--connect',options.connect,'--port',str(options.port),'--ipc-port',str(options.ipc_port),'--record',str(out/'recording'),'--summary',str(out/'host.json')],env=env,cwd=root,stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
    save('run.json',dict(host_pid=host.pid,test_pid=os.getpid(),connect=options.connect,boards=serials,seconds=options.seconds,started_at=datetime.now(timezone.utc).isoformat()))
    time.sleep(1)
    with Client(env['MULTINODEDAQ_IPC_TOKEN'],options.ipc_port,timeout=30) as client:
        deadline=time.monotonic()+150
        while True:
            snapshot=client.control('status');nodes=nodes_by_unit(snapshot)
            if set(nodes)==expected and all(n['rows']>0 and n['connected'] for n in nodes.values()):break
            assert host.poll() is None, 'Receiver exited before both boards connected'
            if time.monotonic()>deadline:raise TimeoutError('Both boards did not stream within 150 seconds')
            status('waiting_for_boards',units=list(nodes.values()))
            time.sleep(1)
        if options.require_diagnostics:
            assert all(isinstance(n.get('source_diagnostics'),dict) and all(k in n['source_diagnostics'] for k in ['timer_dropped','queue_dropped','peak_buffer_rows','max_ack_wait_us','max_loop_gap_us']) for n in nodes.values()), 'Diagnostic firmware and receiver required'
        first={u:n['rows'] for u,n in nodes.items()}
        sessions={u:n['session'] for u,n in nodes.items()}
        peaks={u:n['source_buffer_rows'] for u,n in nodes.items()}
        started=time.monotonic();start_utc=datetime.now(timezone.utc)
        expected_end=(start_utc+timedelta(seconds=options.seconds)).isoformat()
        print(f'Both boards streaming; measuring {options.seconds} seconds; expected end {expected_end}',flush=True)
        with (out/'metrics.jsonl').open('w') as metrics:
            while True:
                elapsed=time.monotonic()-started
                status('running',elapsed_seconds=elapsed,expected_measurement_end=expected_end,units=list(nodes.values()),sampled_peak_source_buffer_rows=peaks)
                metrics.write(json.dumps(dict(elapsed_seconds=elapsed,acquisition=snapshot['acquisition'],recording=snapshot['recording']))+'\n');metrics.flush()
                if elapsed>=options.seconds:break
                time.sleep(min(10,options.seconds-elapsed))
                snapshot=client.control('status');nodes=nodes_by_unit(snapshot)
                assert set(nodes)==expected and all(nodes[u]['session']==sessions[u] for u in expected), 'Board restarted or session disappeared'
                for u,n in nodes.items():peaks[u]=max(peaks[u],n['source_buffer_rows'])
                assert host.poll() is None, 'Receiver exited during measurement'
        rates={u:(n['rows']-first[u])/elapsed for u,n in nodes.items()}
        status('draining',elapsed_seconds=elapsed)
        stopped=client.control('stop_recording')
        final=client.control('status');final_nodes=nodes_by_unit(final)
        client.control('shutdown')
    host.wait(timeout=30)
    status('verifying',elapsed_seconds=elapsed)
    check=subprocess.run([dotnet,hostdll,'--verify',str(out/'recording'),'--summary',str(out/'verify.json')],env=env,cwd=root,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=1800)
    python_check=subprocess.run([sys.executable,str(root/'scripts/verify-stage5-counter.py'),str(out),'--boards',options.boards],cwd=root,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,timeout=1800)
    (out/'python-verify.log').write_text(python_check.stdout)
    independent=json.loads((out/'python-verify.json').read_text()) if python_check.returncode==0 else None
    summaries=[]
    for u,n in final_nodes.items():
        summaries.append(dict(unit=u,session=n['session'],rows=n['rows'],observed_rows_per_second=rates[u],missing_rows=n['missing_rows'],sample_errors=n['sample_errors'],reported_drops=n['reported_drops'],reconnects=n['reconnects'],sampled_peak_source_buffer_rows=peaks[u],source_diagnostics=n.get('source_diagnostics')))
    passed=(host.returncode==0 and check.returncode==0 and python_check.returncode==0 and independent['lossless'] and stopped['state']=='complete' and final['acquisition']['errors']==0 and set(final_nodes)==expected and all(n['missing_rows']==n['sample_errors']==n['reported_drops']==0 and 24500<rates[u]<25500 and n['state']=='idle' and n['source_buffer_rows']==0 for u,n in final_nodes.items()))
    report=dict(passed=passed,measurement_seconds=elapsed,units=summaries,total_rows=sum(n['rows'] for n in final_nodes.values()),recording_state=stopped['state'],host_exit=host.returncode,verify_exit=check.returncode,python_verify_exit=python_check.returncode,acquisition_errors=final['acquisition']['errors'])
    if independent:assert report['total_rows']==independent['rows'], 'Independent row total mismatch'
    save('report.json',report);status('complete' if passed else 'failed',report=report)
    print(json.dumps(report),flush=True)
    if not passed:sys.exit(1)
except BaseException as exc:
    if not (out/'report.json').exists():status('failed',error=f'{type(exc).__name__}: {exc}')
    raise
finally:
    if host is not None and host.poll() is None:
        try:
            with Client(env['MULTINODEDAQ_IPC_TOKEN'],options.ipc_port,timeout=30) as cleanup:
                cleanup.control('stop_recording');cleanup.control('shutdown')
            host.wait(timeout=30)
        except Exception:
            host.kill();host.wait()
    log.close()
    if os.name=="nt":ctypes.windll.kernel32.SetThreadExecutionState(0x80000000)
