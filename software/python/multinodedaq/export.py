"""HDF5 convenience arrays plus a lossless archive of the authoritative records."""
from pathlib import Path
import hashlib
import json
import tempfile
import numpy as np
import h5py
from .reader import open_session
from .contracts import check


def export_hdf5(session, destination):
    """Create exclusively; failures retain an explicitly incomplete export."""
    destination=Path(destination)
    check(not destination.exists(),'export destination exists')
    with h5py.File(destination,'x') as out:
        out.attrs['format']='MultiNodeDAQ-HDF5-1'
        out.attrs['complete']=False
        archive=out.create_group('archive')
        for path in [session.path/'manifest.json',*[session.path/s['file'] for s in session.manifest['segments']]]:
            dataset=archive.create_dataset(path.name,(path.stat().st_size,),dtype='u1',chunks=True)
            digest=hashlib.sha256()
            with path.open('rb') as f:
                offset=0
                while chunk:=f.read(1048576):
                    digest.update(chunk);dataset[offset:offset+len(chunk)]=np.frombuffer(chunk,dtype='u1');offset+=len(chunk)
            dataset.attrs['sha256']=digest.hexdigest()
            if path.name!='manifest.json':
                expected=next(s['sha256'] for s in session.manifest['segments'] if s['file']==path.name)
                check(digest.hexdigest()==expected,'source changed during export')
        for unit,sid in session.streams:
            group=out.require_group(f'streams/{unit}/{sid}')
            group.attrs['metadata_json']=json.dumps(session.streams[(unit,sid)]['metadata'])
            group.attrs['extent']=str(session.streams[(unit,sid)]['extent'])
            codes=group.create_dataset('codes',(0,3),maxshape=(None,3),chunks=(4096,3),dtype='<i4')
            blocks=group.create_dataset('blocks',(0,8),maxshape=(None,8),chunks=(1024,8),dtype='<u8')
            blocks.attrs['columns']='first,count,row_offset,rate,flags,config,calibration,timing'
            row=0;i=0
            for f in session.frames(unit,sid):
                codes.resize((row+f.count,3));codes[row:row+f.count]=np.frombuffer(f.payload,dtype='<i4').reshape(-1,3)
                blocks.resize((i+1,8));blocks[i]=[f.first_sample,f.count,row,f.rate,f.flags,f.config,f.calibration,f.timing]
                row+=f.count;i+=1
        out.attrs['complete']=True
        out.flush()
    return destination


def restore_hdf5(path,destination):
    """Restore every original record byte, then independently validate the recording."""
    destination=Path(destination);destination.mkdir(exist_ok=False)
    with h5py.File(path,'r') as source:
        check(source.attrs['format']=='MultiNodeDAQ-HDF5-1' and source.attrs['complete'],'incomplete export')
        for name,dataset in source['archive'].items():
            check(Path(name).name==name and '\\' not in name and '/' not in name,'archive path')
            digest=hashlib.sha256()
            with (destination/name).open('xb') as out:
                for offset in range(0,len(dataset),1048576):
                    b=dataset[offset:offset+1048576].tobytes();digest.update(b);out.write(b)
            check(digest.hexdigest()==dataset.attrs['sha256'],'archive integrity')
    with open_session(destination):pass
    return destination
