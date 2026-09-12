"""Independent Python implementation of the documented v1 envelopes."""
import json
import struct
import zlib
from dataclasses import dataclass

class ContractError(ValueError):
    pass

def check(ok, why):
    if not ok:
        raise ContractError(why)

def crc(data):
    return zlib.crc32(data) & 0xffffffff

def _unique(pairs):
    d = {}
    for k, v in pairs:
        check(k not in d, 'duplicate json key')
        d[k] = v
    return d

def json_object(data):
    def bad(value):
        raise ContractError('non-finite json')
    def depth(v, n=0):
        if isinstance(v, (dict, list)):
            check(n < 16, 'json depth')
            for item in (v.values() if isinstance(v, dict) else v):
                depth(item, n+1)
    try:
        obj = json.loads(data.decode('utf-8'), object_pairs_hook=_unique, parse_constant=bad)
        check(isinstance(obj, dict), 'json object')
        depth(obj)
        return obj
    except (ValueError, UnicodeError, RecursionError) as exc:
        raise ContractError('json') from exc

@dataclass(frozen=True)
class Frame:
    kind: int
    flags: int
    unit: bytes
    session: bytes
    sequence: int
    first_sample: int
    count: int
    rate: int
    config: int
    calibration: int
    timing: int
    channels: int
    encoding: int
    payload: bytes

HEADER = struct.Struct('<4sHHII16s16sQQIIIIIHHII')
assert HEADER.size == 96
MAX_FRAME = 1048576

def samples(f):
    check(f.kind == 2 and f.encoding in (1, 2), 'not samples')
    width = 3 if f.encoding == 1 else 4
    check(len(f.payload) % width == 0, 'sample bytes')
    return [int.from_bytes(f.payload[i:i+width], 'little', signed=True)
            for i in range(0, len(f.payload), width)]

def validate(f):
    check(1 <= f.kind <= 7, 'kind')
    check(len(f.unit)==16 and any(f.unit) and len(f.session)==16 and any(f.session), 'identity')
    check(0 <= f.flags <= 15 and len(f.payload) <= MAX_FRAME-100, 'flags/size')
    check(0 <= f.sequence < 2**64 and 0 <= f.first_sample < 2**64, 'u64')
    check(all(0 <= x < 2**32 for x in (f.count,f.rate,f.config,f.calibration,f.timing)), 'u32')
    if f.kind == 2:
        check(f.encoding in (1,2) and f.channels==3 and 1<=f.count<=4096 and 1<=f.rate<=1000000, 'data shape')
        check(f.config != 0 and f.first_sample + f.count <= 2**64-1, 'config/counter')
        check(len(f.payload)==f.count*3*(3 if f.encoding==1 else 4), 'payload size')
        check(all(-8388608<=v<=8388607 for v in samples(f)), 'sample range')
    else:
        check(f.encoding==0 and f.channels==0 and f.count==0 and f.flags==0, 'json shape')
        json_object(f.payload)

def peek_length(data):
    check(len(data)>=12,'prefix incomplete')
    magic,version,kind,n=struct.unpack_from('<4sHHI',data)
    check(magic==b'ELD1' and version==1 and 1<=kind<=7,'prefix')
    check(100<=n<=MAX_FRAME,'length')
    return n

def encode(f):
    validate(f)
    b=HEADER.pack(b'ELD1',1,f.kind,100+len(f.payload),f.flags,f.unit,f.session,
                  f.sequence,f.first_sample,f.count,f.rate,f.config,f.calibration,
                  f.timing,f.channels,f.encoding,len(f.payload),0)+f.payload
    return b+struct.pack('<I',crc(b))

def decode(b):
    n=peek_length(b)
    check(len(b)==n,'exact length')
    fields=HEADER.unpack_from(b)
    check(fields[-1]==0 and fields[-2]==n-100,'reserved/size')
    check(struct.unpack_from('<I',b,n-4)[0]==crc(b[:-4]),'crc')
    f=Frame(fields[2],*fields[4:-2],b[96:-4])
    validate(f)
    return f

def normalize(f):
    from dataclasses import replace
    validate(f)
    if f.kind!=2: return f
    return replace(f,encoding=2,payload=b''.join(v.to_bytes(4,'little',signed=True) for v in samples(f)))

def _envelope(magic,kind,payload,request=None):
    if magic==b'ELI1':
        check(1<=kind<=3 and len(payload)<=2097152-28,'ipc kind/size')
        if kind==2:
            f=decode(payload);check(f.kind==2 and f.encoding==2,'ipc array')
        else: json_object(payload)
        h=struct.pack('<4sHHIQI',magic,1,kind,len(payload)+28,request,len(payload))
    else:
        check(1<=kind<=4 and len(payload)<=2097152-20,'record kind/size')
        if kind==1:
            f=decode(payload);check(f.kind!=2 or f.encoding==2,'log array')
        else: json_object(payload)
        h=struct.pack('<4sHHII',magic,1,kind,len(payload)+20,len(payload))
    b=h+payload
    return b+struct.pack('<I',crc(b))

def ipc_encode(kind,request,payload): return _envelope(b'ELI1',kind,payload,request)
def record_encode(kind,payload): return _envelope(b'ELR1',kind,payload)

def _read_envelope(b,ipc):
    hs=24 if ipc else 16
    check(hs+4<=len(b)<=2097152,'envelope length')
    magic,version,kind,n=struct.unpack_from('<4sHHI',b)
    check(magic==(b'ELI1' if ipc else b'ELR1') and version==1 and n==len(b),'envelope prefix')
    plen=struct.unpack_from('<I',b,hs-4)[0]
    check(plen==len(b)-hs-4 and struct.unpack_from('<I',b,len(b)-4)[0]==crc(b[:-4]),'envelope size/crc')
    p=b[hs:-4]
    request=struct.unpack_from('<Q',b,12)[0] if ipc else None
    check(_envelope(magic,kind,p,request)==b,'envelope roundtrip')
    return (kind,request,p) if ipc else (kind,p)

def ipc_decode(b): return _read_envelope(b,True)
def record_decode(b): return _read_envelope(b,False)

def log_header(session):
    check(len(session)==16 and any(session),'log session')
    b=struct.pack('<8sHHI16s',b'ELFLOG1\0',1,0,36,session)
    return b+struct.pack('<I',crc(b))

def read_log_header(b):
    check(len(b)==36,'header length')
    magic,version,flags,n,session=struct.unpack('<8sHHI16s',b[:32])
    check(magic==b'ELFLOG1\0' and version==1 and flags==0 and n==36,'header fields')
    check(log_header(session)==b,'header crc')
    return session
