import unittest,json,hashlib,sys
from pathlib import Path
from dataclasses import replace
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from multinodedaq.contracts import *
FIX=ROOT.parent/'fixtures'
class Contracts(unittest.TestCase):
    def test_fixtures(self):
        cases=json.loads((FIX/'manifest.json').read_text())['cases']
        for c in cases:
            with self.subTest(c=c['file']):
                b=(FIX/c['file']).read_bytes()
                self.assertEqual(hashlib.sha256(b).hexdigest(),c['sha256'])
                def read():
                    if c['format']=='wire':
                        f=decode(b)
                        self.assertEqual(encode(f),b)
                        if 'samples' in c:self.assertEqual(samples(f),c['samples'])
                    elif c['format']=='header':self.assertEqual(log_header(read_log_header(b)),b)
                    elif c['format']=='record':self.assertEqual(record_encode(*record_decode(b)),b)
                    else:self.assertEqual(ipc_encode(*ipc_decode(b)),b)
                if c['valid']:read()
                else:
                    with self.assertRaises(ContractError):read()
    def test_other_envelope_truncations(self):
        for name,read in [('log_header.bin',read_log_header),('log_record.bin',record_decode),('ipc_block.bin',ipc_decode)]:
            b=(FIX/name).read_bytes()
            for n in range(len(b)):
                with self.assertRaises(ContractError):read(b[:n])
    def test_all_message_kinds(self):
        found=set()
        for c in json.loads((FIX/'manifest.json').read_text())['cases']:
            if c['valid'] and c['format']=='wire':found.add(decode((FIX/c['file']).read_bytes()).kind)
        self.assertEqual(found,set(range(1,8)))
    def test_crc_known_vector(self):self.assertEqual(crc(b'123456789'),0xcbf43926)
    def test_identity_and_axis_order(self):
        f=decode((FIX/'data_i24.bin').read_bytes())
        self.assertEqual(f.unit.hex(),'00112233445566778899aabbccddeeff')
        self.assertEqual(f.first_sample,1234567890123)
        self.assertEqual(samples(f)[:3],[-8388608,-1,0])
        self.assertEqual(encode(normalize(f)),(FIX/'data_i32.bin').read_bytes())
    def test_every_truncation_and_prefix(self):
        b=(FIX/'data_i24.bin').read_bytes()
        for n in range(len(b)):
            with self.assertRaises(ContractError):decode(b[:n])
        self.assertEqual(peek_length(b[:12]),len(b))
    def test_encoder_rejects_invalid_shape(self):
        f=decode((FIX/'data_i24.bin').read_bytes())
        for bad in [replace(f,channels=2),replace(f,count=4097),replace(f,rate=0),replace(f,unit=b''),replace(f,flags=16)]:
            with self.assertRaises(ContractError):encode(bad)
    def test_max_counter_policy(self):
        f=decode((FIX/'data_i24.bin').read_bytes())
        self.assertEqual(decode(encode(replace(f,first_sample=2**64-3))).first_sample,2**64-3)
        with self.assertRaises(ContractError):encode(replace(f,first_sample=2**64-2))
if __name__=='__main__':unittest.main(verbosity=2)
