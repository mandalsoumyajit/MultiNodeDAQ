"""Read-only frame/CRC/counter and diagnostic analysis of an interrupted counter run.
This bounded scan is not a replacement for complete recording semantic verification.
"""
import argparse,json,struct,zlib,hashlib
from pathlib import Path
import numpy as np
from multinodedaq.contracts import HEADER,read_log_header,record_decode
p=argparse.ArgumentParser();p.add_argument('run',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
a.output.mkdir(parents=True,exist_ok=False)
manifest=json.loads((a.run/'recording/manifest.json').read_text())
hashes={s['file']:s['sha256'] for s in manifest['segments']}
units={};files=[];statuses=[]
for path in sorted((a.run/'recording').glob('*.elflog')):
 unit,session,segment=path.stem.split('-')
 u=units.setdefault(unit,dict(session=session,rows=0,end=0,gaps=[],counter_errors=0,sequence_errors=0,last_sequence=None,last_status=None))
 assert u['session']==session
 valid=36;error=None;footer=False;rows=0
 with path.open('rb') as f:
  read_log_header(f.read(36))
  while h:=f.read(16):
   at=f.tell()-len(h)
   try:
    assert len(h)==16,'truncated record prefix'
    magic,version,kind,size,ps=struct.unpack('<4sHHII',h)
    assert magic==b'ELR1' and version==1 and 20<=size<=2097152 and ps==size-20,'invalid record prefix'
    raw=h+f.read(size-16)
    assert len(raw)==size and 1<=kind<=4,'record size/kind'
    assert zlib.crc32(raw[:-4])&0xffffffff==struct.unpack_from('<I',raw,size-4)[0],'record CRC'
    payload=raw[16:-4]
    assert not footer,'bytes after footer'
    if kind==1:
     fields=HEADER.unpack_from(payload)
     magic,version,fk,n,flags,uid,sid,seq,first,count,rate,config,cal,timing,channels,encoding,payload_size,reserved=fields
     assert magic==b'ELD1' and version==1 and n==len(payload) and n==100+payload_size and reserved==0,'invalid frame header'
     assert zlib.crc32(payload[:-4])&0xffffffff==struct.unpack_from('<I',payload,len(payload)-4)[0],'frame CRC'
     assert uid.hex()==unit and sid.hex()==session,'frame identity'
     # Segment headers replay cached metadata; check DATA sequence only.
     body=payload[96:-4]
     if fk==1:
      meta=json.loads(body);assert meta['mode']=='counter' and meta['seed']==17,'counter fixture metadata'
     if fk==2:
      assert encoding in (1,2) and channels==3 and rate==25000 and len(body)==count*3*(3 if encoding==1 else 4),'unexpected DATA shape'
      if u['last_sequence'] is not None and seq<=u['last_sequence']:u['sequence_errors']+=1
      u['last_sequence']=seq
      assert first>=u['end'],'overlap/out-of-order DATA'
      if first>u['end']:u['gaps'].append(dict(first=u['end'],end=first,rows=first-u['end']))
      if encoding==1:
       codes=np.frombuffer(body,dtype=np.uint8).reshape(-1,3).astype(np.uint32)
       actual=codes[:,0]|(codes[:,1]<<8)|(codes[:,2]<<16)
      else:
       signed=np.frombuffer(body,dtype='<i4');assert np.all((signed>=-8388608)&(signed<=8388607)),'sample range'
       actual=signed.astype(np.uint32)&0xffffff
      expected=((np.arange(count*3,dtype=np.uint64)+first*3+17-8388608)&0xffffff).astype(np.uint32)
      u['counter_errors']+=int(np.count_nonzero(actual!=expected));u['rows']+=count;rows+=count;u['end']=first+count
     elif fk==3:
      status=json.loads(body);status.update(unit=unit,file=path.name,offset=at)
      statuses.append(status);u['last_status']=status
    elif kind==4:footer=True
    valid=f.tell()
   except Exception as e:error=dict(offset=at,reason=str(e) or type(e).__name__);break
 with path.open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
 files.append(dict(file=path.name,size=path.stat().st_size,valid_prefix_bytes=valid,rows=rows,footer=footer,error=error,sha256=digest,manifest_hash_match=digest==hashes[path.name] if path.name in hashes else None))
 print(path.name,rows,error,flush=True)
for u in units.values():
 u['missing_rows']=sum(g['rows'] for g in u['gaps']);u['sample_extent_seconds']=u['end']/25000
 u['loss_percent']=100*u['missing_rows']/u['end'] if u['end'] else None
metrics=[json.loads(line) for line in (a.run/'metrics.jsonl').open()]
events=[];prev={}
for m in metrics:
 for n in m['acquisition']['units']:
  uid=n['unit'];d=n.get('source_diagnostics') or {};old=prev.get(uid,0)
  if n['reported_drops']!=old:events.append(dict(timestamp=m['acquisition']['timestamp'],elapsed=m['elapsed_seconds'],unit=uid,delta=n['reported_drops']-old,diagnostics=d))
  prev[uid]=n['reported_drops']
summary=dict(scope='record envelopes, frame CRC, counter payloads and monotonic DATA; full semantic verification not performed',manifest_state=manifest['state'],units=units,files=files,drop_observations=events,last_metrics_timestamp=metrics[-1]['acquisition']['timestamp'])
(a.output/'analysis.json').write_text(json.dumps(summary,indent=2))
with (a.output/'source-status.jsonl').open('w') as f:
 for s in statuses:f.write(json.dumps(s)+'\n')
print('Analysis saved',a.output,flush=True)
