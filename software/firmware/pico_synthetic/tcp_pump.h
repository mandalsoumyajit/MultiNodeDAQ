// SPDX-License-Identifier: MIT
#ifndef MND_TCP_PUMP_H
#define MND_TCP_PUMP_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

// All calls run in the single polling context. write returns 0 on acceptance,
// 1 for transient resource pressure, -1 for a fatal error. output never implies ACK.
typedef struct {
    uint64_t (*now)(void *);
    bool (*prepare)(void *, const uint8_t **, size_t *);
    size_t (*space)(void *);
    int (*write)(void *, const uint8_t *, size_t);
    int (*output)(void *);
    bool (*unsent)(void *);
    void (*complete)(void *);
} mnd_pump_ops;
typedef struct {
    const uint8_t *data;
    size_t length, offset;
    uint64_t prepared_frames, queued_bytes, acked_bytes;
    uint64_t ack_progress_at, max_ack_wait_us, last_queued_at;
    uint64_t retry_at, output_calls, output_errors;
    int last_output_error;
    bool output_pending, fatal;
} mnd_pump;
#define MND_PUMP_BYTES 8192u
#define MND_PUMP_STEPS 16u
#define MND_PUMP_US 500u
#define MND_OUTPUT_RETRY_US 1000u
static uint64_t mnd_pump_ack_wait(const mnd_pump *p, uint64_t now) {
    return p->queued_bytes != p->acked_bytes ? now-p->ack_progress_at : 0;
}
static bool mnd_pump_ack(mnd_pump *p, uint64_t now, size_t bytes) {
    if (bytes > p->queued_bytes-p->acked_bytes) { p->fatal=true; return false; }
    if (bytes) {
        uint64_t wait=mnd_pump_ack_wait(p,now);
        if (wait>p->max_ack_wait_us) p->max_ack_wait_us=wait;
        p->acked_bytes+=bytes; p->ack_progress_at=now;
        // ACK progress may permit output, but does not override an error backoff.
    }
    return true;
}
static void mnd_pump_run(mnd_pump *p, const mnd_pump_ops *ops, void *ctx) {
    uint64_t start=ops->now(ctx);
    size_t budget=MND_PUMP_BYTES;
    // Retry previously queued output even when tcp_sndbuf is zero or no new
    // frame can be accepted. lwIP still owns congestion/retransmission policy.
    if ((p->output_pending || ops->unsent(ctx)) && start>=p->retry_at) {
        int e=ops->output(ctx); p->output_calls++; p->last_output_error=e;
        p->output_pending=e!=0;
        if (e) p->output_errors++;
        p->retry_at=start+MND_OUTPUT_RETRY_US;
    }
    bool wrote=false;
    for (unsigned step=0; step<MND_PUMP_STEPS && budget && !p->fatal; step++) {
        if (ops->now(ctx)-start>=MND_PUMP_US) break;
        if (!p->length) {
            if (!ops->prepare(ctx,&p->data,&p->length)) break;
            if (!p->length) { p->fatal=true; break; }
            p->offset=0; p->prepared_frames++;
        }
        size_t n=p->length-p->offset, space=ops->space(ctx);
        if (n>space) n=space;
        if (n>budget) n=budget;
        if (!n) break;
        int e=ops->write(ctx,p->data+p->offset,n);
        if (e) { if (e<0) p->fatal=true; break; }
        uint64_t now=ops->now(ctx);
        if (p->queued_bytes==p->acked_bytes) p->ack_progress_at=now;
        p->queued_bytes+=n; p->last_queued_at=now;
        p->offset+=n; budget-=n; wrote=true;
        if (p->offset==p->length) {
            ops->complete(ctx); p->length=p->offset=0;
        }
    }
    // New accepted bytes prompt output immediately, except during an output
    // error backoff. Never re-submit bytes already accepted by tcp_write.
    if (wrote) {
        uint64_t now=ops->now(ctx);
        if (!p->output_pending || now>=p->retry_at) {
            int e=ops->output(ctx); p->output_calls++; p->last_output_error=e;
            p->output_pending=e!=0;
            if (e) p->output_errors++;
            p->retry_at=now+MND_OUTPUT_RETRY_US;
        }
    }
}
#endif
