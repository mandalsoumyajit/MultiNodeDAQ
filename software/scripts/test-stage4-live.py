"""Stage 4 process isolation, live GUI/worker and sixteen-node local-load checks."""
import json, os, pathlib, secrets, subprocess, time
from multinodedaq.client import Client

root=pathlib.Path(__file__).resolve().parents[1]
out=root/'.artifacts'/'stage4'/('live-'+time.strftime('%Y%m%d-%H%M%S'))
out.mkdir(parents=True)
env=dict(os.environ,MULTINODEDAQ_IPC_TOKEN=secrets.token_hex(32),MULTINODEDAQ_IPC_PORT='45221',DOTNET_ROOT=str(root/'.tools/dotnet'))
dotnet=str(root/'.tools/dotnet/dotnet.exe')
scenario=out/'scenario.json'
scenario.write_text(json.dumps(dict(Nodes=16,Seconds=40,Mode='counter')))
processes=[]
def launch(command,name):
    log=open(out/(name+'.log'),'w')
    p=subprocess.Popen(command,cwd=root,env=env,stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
    processes.append((p,log));return p
def gui():
    return launch([str(root/'src/MultiNodeDAQ.Desktop/bin/Release/net10.0-windows/MultiNodeDAQ.Desktop.exe'),'--minimized'],'gui-'+str(len(processes)))
host=launch([dotnet,str(root/'src/MultiNodeDAQ.Host/bin/Release/net10.0/MultiNodeDAQ.Host.dll'),'--port','45220','--ipc-port','45221','--record',str(out/'recording'),'--summary',str(out/'host.json')],'host')
try:
    time.sleep(1)
    simulator=launch([dotnet,str(root/'src/MultiNodeDAQ.Simulator/bin/Release/net10.0/MultiNodeDAQ.Simulator.dll'),'--port','45220','--scenario',str(scenario),'--summary',str(out/'sim.json')],'sim')
    time.sleep(2)
    desktop=gui()
    ages=[]
    with Client(env['MULTINODEDAQ_IPC_TOKEN'],45221) as client:
        s=client.control('status')
        units=s['acquisition']['units'];assert len(units)==16
        unit=units[0]['unit'];sid=units[0]['session']
        worker=launch([str(root/'.venv/Scripts/python.exe'),'-m','multinodedaq.worker','--port','45221','--units',unit],'worker')
        settings=dict(rate=25000,df=50,dalpha=10,max_frequency=2000,max_alpha=500,hop_fraction=.25,pair_batch=256)
        client.control('configure_analysis',settings=settings)
        applied=False
        for i in range(100):
            s=client.control('status')
            p=client.control('preview',unit=unit,acquisition_session=sid,seconds=.1);ages.append(p['age_seconds'])
            if s['analysis']:
                result=s['analysis'][0]['result']
                if result['valid'] and result['parameters']['revision'] in (1,3):
                    applied=True;(out/'analysis.json').write_text(json.dumps(s['analysis'][0]))
            if i==10:client.control('configure_analysis',settings=dict(settings,df=.001))
            if i==20:
                assert s['analysis'][0]['rejection']['rejected_revision']==2,'rejection was lost when old valid results resumed'
                client.control('configure_analysis',settings=settings)
            if i==25:
                assert desktop.poll() is None,'GUI crashed';desktop.kill();desktop.wait()
                baseline=s['acquisition']['units'][0]['rows']
            if i==35:
                assert s['acquisition']['units'][0]['rows']>baseline,'GUI exit stopped receiver'
                desktop=gui()
            if i==55: worker.kill();worker.wait()
            if i==80:
                assert all(a['stale'] for a in s['analysis']),'worker death not stale'
            time.sleep(.2)
        assert desktop.poll() is None,'GUI restart crashed'
        assert applied,'settings not applied'
        before=client.control('status')
        stopped=client.control('stop_recording')
        assert stopped['state']=='complete',stopped
        client.control('shutdown')
    host.wait(timeout=25)
    assert host.returncode==0
    desktop.kill();desktop.wait()
    check=subprocess.run([dotnet,str(root/'src/MultiNodeDAQ.Host/bin/Release/net10.0/MultiNodeDAQ.Host.dll'),'--verify',str(out/'recording'),'--summary',str(out/'verify.json')],cwd=root,env=env,stdout=subprocess.DEVNULL,timeout=60)
    assert check.returncode==0,'record verification failed'
    verified=json.loads((out/'verify.json').read_text())
    assert verified['SampleErrors']==0
    assert all(n['missing_rows']==0 and n['sample_errors']==0 for n in before['acquisition']['units'])
    report=dict(nodes=16,rows=verified['Rows'],preview_age_max_seconds=max(ages),preview_age_median_seconds=sorted(ages)[len(ages)//2],gui_kill_restart=True,worker_stale=True,settings_applied=True,recording_verified=True)
    (out/'report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report));print(out)
finally:
    for p,log in processes:
        if p.poll() is None:p.kill();p.wait()
        log.close()
