"""Copied binary IPC blocks; independent sockets for control and subscriptions."""
import json
import socket
import struct
from .contracts import check, ipc_encode, ipc_decode, decode, json_object


def exact(sock,count):
    out=bytearray(count);view=memoryview(out);at=0
    while at<count:
        n=sock.recv_into(view[at:]);check(n>0,'IPC closed');at+=n
    return bytes(out)

class Client:
    def __init__(self,token,port=45101,timeout=10):
        self.socket=socket.create_connection(('127.0.0.1',port),timeout=timeout);self.request=0
        try:self.control('authenticate',protocol=1,role='analysis',token=token)
        except BaseException:self.close();raise
    def receive(self):
        prefix=exact(self.socket,12);magic,version,kind,size=struct.unpack('<4sHHI',prefix)
        check(magic==b'ELI1' and version==1 and 28<=size<=2097152,'IPC prefix')
        return ipc_decode(prefix+exact(self.socket,size-12))
    def control(self,op,**args):
        self.request+=1
        self.socket.sendall(ipc_encode(1,self.request,json.dumps(dict(op=op,request_id=str(self.request),args=args),allow_nan=False).encode()))
        kind,request,payload=self.receive();response=json_object(payload)
        check(kind==1 and request==self.request and response['request_id']==str(request),'response correlation')
        check(response['ok'],response.get('error','control error'));return response['details']
    def result(self,value):self.socket.sendall(ipc_encode(3,0,json.dumps(value,allow_nan=False,separators=(',',':')).encode()))
    def close(self):self.socket.close()
    def __enter__(self):return self
    def __exit__(self,*args):self.close()

def subscribe(units,token,port=45101):
    with Client(token,port) as client:
        client.control('subscribe',units=units,stream='samples',history_seconds=0)
        context=None;status={}
        while True:
            kind,request,payload=client.receive()
            check(request==0,'subscription correlation')
            if kind==1:
                value=json_object(payload)
                if value['op']=='context':context=value
                if value['op']=='subscription_status':status=value['details']
            elif kind==2:
                f=decode(payload)
                check(context is not None and f.unit.hex()==context['unit'] and f.session.hex()==context['acquisition_session'],'block context')
                yield f,context,status
