// SPDX-License-Identifier: MIT
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../tcp_pump.h"
typedef struct {
    uint64_t clock, tick;
    size_t space, size, frames, completed, accepted, outputs;
    int write_error, output_error;
    bool unsent, hold_output;
    uint8_t frame[3000], received[20000];
} fake;
static uint64_t now(void *v){fake *f=v;uint64_t t=f->clock;f->clock+=f->tick;return t;}
static bool prepare(void *v,const uint8_t **p,size_t *n){
    fake *f=v;if(f->completed==f->frames)return false;
    for(size_t i=0;i<f->size;i++)f->frame[i]=(uint8_t)(f->completed*f->size+i);
    *p=f->frame;*n=f->size;return true;
}
static size_t space(void *v){return ((fake*)v)->space;}
static int write_bytes(void *v,const uint8_t *p,size_t n){
    fake *f=v;if(f->write_error)return f->write_error;
    assert(n<=f->space && f->accepted+n<=sizeof f->received);
    memcpy(f->received+f->accepted,p,n);f->accepted+=n;f->space-=n;f->unsent=true;return 0;
}
static int output(void *v){
    fake *f=v;f->outputs++;if(!f->output_error && !f->hold_output)f->unsent=false;return f->output_error;
}
static bool unsent(void *v){return ((fake*)v)->unsent;}
static void complete(void *v){((fake*)v)->completed++;}
static const mnd_pump_ops ops={now,prepare,space,write_bytes,output,unsent,complete};
static void exact(fake *f){for(size_t i=0;i<f->accepted;i++)assert(f->received[i]==(uint8_t)i);}
int main(void){
    // Output failure after the last available bytes were queued must recover
    // with zero send space and no further write; acceptance must not duplicate.
    fake f={.clock=100,.space=2404,.size=2404,.frames=1,.output_error=-1};
    mnd_pump p={0};mnd_pump_run(&p,&ops,&f);
    assert(f.completed==1 && p.output_errors==1 && p.queued_bytes==2404);
    f.clock=1099;mnd_pump_run(&p,&ops,&f);assert(f.outputs==1);
    f.clock=1100;f.output_error=0;mnd_pump_run(&p,&ops,&f);
    assert(f.outputs==2 && !p.output_pending && f.accepted==2404);exact(&f);
    // Partial and cumulative ACKs track accepted bytes, not prepared frames.
    assert(mnd_pump_ack(&p,1200,400));
    assert(mnd_pump_ack_wait(&p,1300)==100);
    assert(mnd_pump_ack(&p,1400,2004));
    assert(mnd_pump_ack_wait(&p,9000)==0 && p.max_ack_wait_us==1100);
    assert(!mnd_pump_ack(&p,9000,1) && p.fatal);

    // Zero space does not start an ACK clock. Partial writes and transient
    // allocation failure preserve the exact byte stream and frame boundaries.
    f=(fake){.clock=100,.size=2404,.frames=2};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);assert(p.prepared_frames==1 && p.queued_bytes==0);
    assert(mnd_pump_ack_wait(&p,1000000)==0);
    f.clock=1000000;f.space=1000;mnd_pump_run(&p,&ops,&f);
    assert(p.offset==1000 && !f.completed);
    f.space=5000;f.write_error=1;mnd_pump_run(&p,&ops,&f);
    assert(p.offset==1000 && !p.fatal);
    f.write_error=0;mnd_pump_run(&p,&ops,&f);
    assert(f.completed==2 && f.accepted==4808);exact(&f);
    assert(mnd_pump_ack(&p,1000100,4808));

    // Existing unsent output (e.g. peer window reopens) gets serviced without
    // new frames. ERR_OK with unsent remaining is periodically rechecked.
    f=(fake){.clock=1000,.unsent=true};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);assert(f.outputs==1 && !f.unsent);

    f=(fake){.clock=1000,.unsent=true,.hold_output=true};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);assert(f.outputs==1 && f.unsent && !p.output_errors);
    f.clock=1999;mnd_pump_run(&p,&ops,&f);assert(f.outputs==1);
    f.clock=2000;f.hold_output=false;mnd_pump_run(&p,&ops,&f);
    assert(f.outputs==2 && !f.unsent);

    // Pump work has independent byte, iteration and elapsed-time ceilings.
    f=(fake){.clock=100,.space=20000,.size=2404,.frames=8};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);assert(f.accepted==8192 && f.completed==3);exact(&f);
    f=(fake){.clock=100,.space=20000,.size=1,.frames=100};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);assert(f.completed==16);
    f=(fake){.clock=100,.tick=300,.space=20000,.size=2404,.frames=8};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);assert(f.completed==1);
    // Backoff remains effective even with capacity to queue additional frames.
    f=(fake){.clock=100,.space=5000,.size=2404,.frames=1,.output_error=-1};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);f.frames=2;f.clock=101;
    mnd_pump_run(&p,&ops,&f);assert(f.outputs==1 && p.queued_bytes==4808);
    f.clock=1100;f.output_error=0;mnd_pump_run(&p,&ops,&f);assert(f.outputs==2);exact(&f);
    // Fatal writes stop servicing without claiming acceptance.
    f=(fake){.clock=1,.space=2404,.size=2404,.frames=1,.write_error=-1};p=(mnd_pump){0};
    mnd_pump_run(&p,&ops,&f);assert(p.fatal && p.queued_bytes==0 && !f.completed);
    puts("TCP pump: output retry, backoff, backpressure, byte continuity, ACK accounting and work budgets PASS");
    return 0;
}