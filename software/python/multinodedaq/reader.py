"""Independent streaming log validation and a disposable on-disk sample index."""
from dataclasses import dataclass, replace
from pathlib import Path
import hashlib
import json
import sqlite3
import struct
import tempfile
from .contracts import check, decode, encode, json_object, read_log_header, record_decode


def records(path):
    with Path(path).open('rb') as stream:
        header = stream.read(36)
        read_log_header(header)
        while prefix := stream.read(16):
            at = stream.tell()-len(prefix)
            check(len(prefix) == 16, 'truncated record prefix')
            magic, version, kind, size, payload_size = struct.unpack('<4sHHII', prefix)
            check(magic == b'ELR1' and version == 1 and 20 <= size <= 2097152 and payload_size == size-20, 'record prefix')
            raw = prefix + stream.read(size-16)
            kind, payload = record_decode(raw)
            yield at, kind, payload


def merge(ranges, first, end):
    check(first < end <= 2**64-1, 'range')
    for a, b in ranges:
        check(end <= a or first >= b, 'overlapping samples')
    result=[]
    for a,b in sorted(ranges+[(first,end)]):
        if result and a == result[-1][1]: result[-1]=(result[-1][0],b)
        else: result.append((a,b))
    check(len(result) <= 4096, 'range capacity')
    return result


@dataclass
class SampleBatch:
    codes: object
    counters: object
    flags: object
    config: object
    calibration: object
    timing: object
    gaps: list
    metadata: dict
    unit: str
    acquisition_session: str
    first: int
    count: int
    source_complete: bool = True


class Session:
    def __init__(self, directory):
        self.path=Path(directory).resolve()
        self._temp=tempfile.TemporaryDirectory(prefix='multinodedaq-index-')
        self.db=sqlite3.connect(str(Path(self._temp.name)/'index.sqlite'))
        self.db.execute('CREATE TABLE blocks (unit TEXT, session TEXT, first TEXT, last TEXT, file TEXT, offset INTEGER, digest BLOB, PRIMARY KEY(unit,session,first))')
        self.streams={}
        try: self._scan()
        except BaseException:
            self.close()
            raise

    def _scan(self):
        manifest_bytes=(self.path/'manifest.json').read_bytes()
        check(len(manifest_bytes)<=16*1024*1024,'manifest size')
        self.manifest=json_object(manifest_bytes)
        check(self.manifest['state']=='complete','complete recording required')
        segments=self.manifest['segments'];check(0<len(segments)<=4096,'segment count')
        names=set(); recording=None
        for segment in sorted(segments,key=lambda s:s['file']):
            name=segment['file']
            check(Path(name).name==name and '/' not in name and '\\' not in name and name not in names and name.endswith('.elflog'),'segment path')
            names.add(name);path=self.path/name
            check(not path.is_symlink(),'symlink segment')
            with path.open('rb') as f: digest=hashlib.file_digest(f,'sha256').hexdigest()
            check(digest==segment['sha256'],'segment hash')
            with path.open('rb') as f: sid=read_log_header(f.read(36)).hex()
            recording=recording or sid
            check(sid==recording==self.manifest['recording_session'],'recording identity')
            started=False;closed=False;count=0;rows=0;state=None;commit=0;hello=None;configs={};timings={}
            for offset, kind, payload in records(path):
                check(not closed,'bytes after footer')
                if kind==2:
                    event=json_object(payload)
                    if event['event']=='recording_started':
                        check(not started and count==0,'start ordering');started=True;d=event['details']
                        unit,acq=d['unit'],d['acquisition_session'];key=(unit,acq)
                        check(len(unit)==32 and len(acq)==32 and any(bytes.fromhex(unit)) and any(bytes.fromhex(acq)),'stream identity')
                        check(key in self.streams or len(self.streams)<128,'stream capacity')
                        state=self.streams.setdefault(key,dict(ranges=[],extent=0,segment=0,metadata={}))
                        check(d['segment']==state['segment'] and name==f"{unit}-{acq}-{state['segment']:05d}.elflog",'segment order')
                        check([(int(r['first']),int(r['end'])) for r in d['prior_ranges']]==state['ranges'] and int(d['prior_extent'])==state['extent'],'segment chain')
                        state['segment']+=1
                elif kind==1:
                    check(started,'frame before start');f=decode(payload)
                    check((f.unit.hex(),f.session.hex())==key,'frame identity')
                    if f.kind==2:
                        check(hello is not None and f.config in configs and configs[f.config]['sample_rate_hz']==f.rate and f.calibration==0 and (f.timing==0 or f.timing in timings),'undefined metadata')
                        normalized=encode(replace(f,sequence=0));digest=hashlib.sha256(normalized).digest()
                        old=self.db.execute('SELECT last,digest FROM blocks WHERE unit=? AND session=? AND first=?',(*key,f'{f.first_sample:020d}')).fetchone()
                        if old:
                            check(old==(f'{f.first_sample+f.count:020d}',digest),'conflicting duplicate')
                        else:
                            state['ranges']=merge(state['ranges'],f.first_sample,f.first_sample+f.count)
                            state['extent']=max(state['extent'],f.first_sample+f.count);rows+=f.count
                            self.db.execute('INSERT INTO blocks VALUES (?,?,?,?,?,?,?)',(*key,f'{f.first_sample:020d}',f'{f.first_sample+f.count:020d}',name,offset,digest))
                    else:
                        o=json_object(f.payload)
                        if f.kind==1: hello=o
                        if f.kind==5 and o['ok'] and 'config' in o['details']:
                            c=o['details']['config'];check(c['id']>0 and c['encoding'] in (1,2) and 1<=c['sample_rate_hz']<=1000000,'config')
                            check(c['id'] not in configs or c==configs[c['id']],'config mutation');configs[c['id']]=c
                        if f.kind==6:
                            check(o['model_id']==f.timing and f.timing>0,'timing definition')
                            check(f.timing not in timings or timings[f.timing]==o,'timing mutation');timings[f.timing]=o
                        if f.kind==7: state['extent']=max(state['extent'],int(o['first_sample'])+int(o['count']))
                        if f.kind==3 and o['state']=='idle' and o['buffer_rows']==0: state['extent']=max(state['extent'],int(o['next_sample']))
                        if f.kind in (1,6) or (f.kind==5 and o['ok'] and 'config' in o['details']):
                            mid='hello' if f.kind==1 else 'timing:'+str(f.timing) if f.kind==6 else 'config:'+str(o['details']['config']['id'])
                            prior=state['metadata'].get(mid)
                            if mid!='hello':check(prior is None or prior==o,'cross-segment metadata mutation')
                            state['metadata'][mid]=o
                            check(len(state['metadata'])<=130 and sum(len(json.dumps(x)) for x in state['metadata'].values())<=1048576,'metadata bounds')
                elif kind==3:
                    check(started,'commit before start');o=json_object(payload)
                    contiguous=state['ranges'][0][1] if state['ranges'] and state['ranges'][0][0]==0 else 0
                    check(int(o['through_offset'])==offset and (o['unit'],o['acquisition_session'])==key and int(o['next_sample'])==contiguous>=commit,'commit semantics');commit=contiguous
                elif kind==4:
                    check(started,'footer before start');o=json_object(payload)
                    check(o['status']=='complete' and int(o['records'])==count and int(o['sample_rows'])==rows,'footer semantics');closed=True
                count+=1
            check(started and closed,'incomplete segment')
        check(names=={p.name for p in self.path.glob('*.elflog')},'unlisted segments')
        self.db.commit()

    def frames(self, unit, acquisition_session, first=0, count=2**64-1):
        key=(unit.lower(),acquisition_session.lower());check(key in self.streams,'unknown stream')
        for name,offset in self.db.execute('SELECT file,offset FROM blocks WHERE unit=? AND session=? AND last>? AND first<? ORDER BY first',(*key,f'{first:020d}',f'{first+count:020d}')):
            with (self.path/name).open('rb') as f:
                f.seek(offset);prefix=f.read(16);size=struct.unpack_from('<I',prefix,8)[0]
                _,p=record_decode(prefix+f.read(size-16));frame=decode(p)
            a,b=max(first,frame.first_sample),min(first+count,frame.first_sample+frame.count)
            yield replace(frame,first_sample=a,count=b-a,payload=frame.payload[(a-frame.first_sample)*12:(b-frame.first_sample)*12])

    def read_samples(self, unit, acquisition_session, first=0, count=4096):
        import numpy as np
        check(isinstance(first,int) and isinstance(count,int) and 0<=first<=2**64-1-count and 1<=count<=1000000,'sample range')
        codes=np.empty((count,3),dtype='<i4');counters=np.empty(count,dtype=np.uint64)
        fields=[np.empty(count,dtype=np.uint32) for _ in range(4)];gaps=[];cursor=first;row=0
        for f in self.frames(unit,acquisition_session,first,count):
            if f.first_sample>cursor:gaps.append((cursor,f.first_sample))
            cursor=f.first_sample+f.count;stop=row+f.count
            check(stop<=count,'range row bound')
            codes[row:stop]=np.frombuffer(f.payload,dtype='<i4').reshape(-1,3)
            counters[row:stop]=np.arange(f.count,dtype=np.uint64)+np.uint64(f.first_sample)
            for out,value in zip(fields,(f.flags,f.config,f.calibration,f.timing)):out[row:stop]=value
            row=stop
        if cursor<first+count:gaps.append((cursor,first+count))
        return SampleBatch(codes[:row],counters[:row],*[x[:row] for x in fields],gaps,self.streams[(unit.lower(),acquisition_session.lower())]['metadata'],unit.lower(),acquisition_session.lower(),first,count)

    def close(self):
        self.db.close();self._temp.cleanup()
    def __enter__(self): return self
    def __exit__(self,*args): self.close()


def open_session(path): return Session(path)
def read_samples(session,*args,**kwargs): return session.read_samples(*args,**kwargs)
