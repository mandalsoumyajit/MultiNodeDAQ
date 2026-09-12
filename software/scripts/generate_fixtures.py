"""Deliberate fixture regeneration only; tests never regenerate expected bytes."""
from pathlib import Path
import sys,json,hashlib,struct
from dataclasses import replace
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'python'))
from multinodedaq.contracts import *
OUT=ROOT/'fixtures';OUT.mkdir(exist_ok=True)
manifest={'version':1,'crc_check_hex':'cbf43926','cases':[]}
unit=bytes.fromhex('00112233445566778899aabbccddeeff')
session=bytes.fromhex('102132435465768798a9bacbdcedfe0f')
values=[-8388608,-1,0,1,8388607,42]
f=Frame(2,0,unit,session,7,1234567890123,2,25000,1,0,0,3,1,b''.join(v.to_bytes(3,'little',signed=True) for v in values))
def add(name,format,data,valid=True,expected=None):
 (OUT/name).write_bytes(data)
 d={'file':name,'format':format,'valid':valid,'sha256':hashlib.sha256(data).hexdigest()}
 if expected is not None:d['samples']=expected
 manifest['cases'].append(d)
def damage(b,offset,value,repair=True):
 a=bytearray(b);a[offset:offset+len(value)]=value
 if repair:a[-4:]=struct.pack('<I',crc(a[:-4]))
 return bytes(a)
a=encode(f);b=encode(normalize(f))
add('data_i24.bin','wire',a,expected=values)
add('data_i32.bin','wire',b,expected=values)
full=[((i*104729)%16777216)-8388608 for i in range(256*3)]
add('data_256.bin','wire',encode(replace(f,count=256,payload=b''.join(v.to_bytes(3,'little',signed=True) for v in full))),expected=full)
hello=b'{"firmware":"synthetic/0.1","protocol":1,"synthetic":true,"axes":["X","Y","Z"],"encodings":[1,2],"capabilities":["status","arm","start","stop"],"label":"test unit"}'
h=encode(replace(f,kind=1,flags=0,sequence=0,first_sample=0,count=0,channels=0,encoding=0,payload=hello))
add('hello.bin','wire',h)

messages={
 3: {'state':'sampling','next_sample':'1234567890125','buffer_rows':0,'dropped_rows':'0'},
 4: {'request_id':'42','op':'arm','args':{'config':{'id':1,'sample_rate_hz':25000,'encoding':1,'synthetic':True}}},
 5: {'request_id':'42','ok':True,'state':'armed','effective_sample':'0','error':None,'details':{'config':{'id':1,'sample_rate_hz':25000,'encoding':1,'synthetic':True}}},
 6: {'model_id':1,'reference_epoch':session.hex(),'anchor_sample':'1234567890123','anchor_time_ns':'0','period_num_ns':'40000','period_den':1,'uncertainty_ns':'1000000','valid_from_sample':'1234567890123','valid_to_sample_exclusive':'1234567915123'},
 7: {'first_sample':'1234567890123','count':'2','reason':'producer_overflow','recoverable':False}}
for kind,obj in messages.items():
 payload=json.dumps(obj,separators=(',',':')).encode()
 jf=replace(f,kind=kind,flags=0,count=0,channels=0,encoding=0,timing=1 if kind==6 else 0,payload=payload)
 add({3:'status',4:'command',5:'ack',6:'timing',7:'gap'}[kind]+'.bin','wire',encode(jf))
for name,offset,val in [('bad_version',4,b'\x02\x00'),('bad_kind',6,b'\xff\x00'),('reserved',92,b'\x01\x00\x00\x00'),('bad_channels',84,b'\x02\x00'),('bad_encoding',86,b'\x03\x00'),('bad_count',64,struct.pack('<I',3)),('zero_config',72,b'\0'*4),('unknown_flag',12,struct.pack('<I',16)),('counter_overflow',56,struct.pack('<Q',2**64-1)),('zero_unit',16,b'\0'*16),('payload_length',88,struct.pack('<I',17)),('oversize_length',8,struct.pack('<I',1048577))]:
 add(name+'.bin','wire',damage(a,offset,val),False)
add('bad_crc.bin','wire',damage(a,100,b'\x11',False),False)
add('out_of_range_i32.bin','wire',damage(b,96,struct.pack('<i',8388608)),False)
add('truncated.bin','wire',a[:-1],False)
add('trailing.bin','wire',a+b'\0',False)
# Build JSON envelope manually so malformed JSON bypasses the encoder.
def jsonwire(payload):
 x=bytearray(h[:96]);struct.pack_into('<I',x,8,100+len(payload));struct.pack_into('<I',x,88,len(payload));x+=payload;x+=struct.pack('<I',crc(x));return bytes(x)
for name,p in [('duplicate_json',b'{"a":1,"a":2}'),('invalid_utf8',b'{"a":"\xff"}'),('nonobject_json',b'[]'),('nonfinite_json',b'{"a":NaN}')]:add(name+'.bin','wire',jsonwire(p),False)
header=log_header(session);record=record_encode(1,b)
add('log_header.bin','header',header)
add('log_record.bin','record',record)
add('log_event.bin','record',record_encode(2,b'{"event":"recording_started","monotonic_ns":"0"}'))
add('log_commit.bin','record',record_encode(3,b'{"through_offset":"36","next_sample":"0","unit":"00112233445566778899aabbccddeeff","acquisition_session":"102132435465768798a9bacbdcedfe0f"}'))
add('log_footer.bin','record',record_encode(4,b'{"status":"complete","records":"0","sample_rows":"0"}'))
add('log_header_crc.bin','header',damage(header,16,b'\x99',False),False)
add('log_record_crc.bin','record',damage(record,20,b'\x99',False),False)
add('log_truncated.bin','record',record[:-1],False)
add('ipc_control.bin','ipc',ipc_encode(1,42,b'{"op":"status","request_id":"42"}'))
add('ipc_block.bin','ipc',ipc_encode(2,0,b))
add('ipc_result.bin','ipc',ipc_encode(3,0,b'{"result":"health","unit":"00112233445566778899aabbccddeeff","acquisition_session":"102132435465768798a9bacbdcedfe0f","first_sample":"1234567890123","count":2,"valid":true,"algorithm":"fixture/1","parameters":{},"values":{"rms_codes":4843164.798}}'))
ipc=ipc_encode(2,0,b)
add('ipc_bad_length.bin','ipc',damage(ipc,20,struct.pack('<I',1)),False)
add('ipc_bad_kind.bin','ipc',damage(ipc,6,b'\x04\x00'),False)
add('ipc_truncated.bin','ipc',ipc[:-1],False)
(OUT/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(f'Generated {len(manifest["cases"])} fixtures; review before committing changed expectations.')
