// SPDX-License-Identifier: MIT
// Stage 5 counter transport prototype. No ADC or synchronized clock is claimed.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>
#include "pico/stdlib.h"
#include "pico/stdio_usb.h"
#include "pico/unique_id.h"
#include "pico/rand.h"
#include "pico/cyw43_arch.h"
#include "hardware/sync.h"
#include "lwip/tcp.h"
#include "vendor/cjson/cJSON.h"
#include "private_config.h"
#define ROWS 256u
#define SLOTS 32u
#define PAYLOAD (ROWS*9u)
typedef struct { uint64_t first; uint8_t bytes[PAYLOAD]; } block;
static block ring[SLOTS];
static volatile uint32_t produced, consumed;
static volatile uint64_t next_sample, dropped;
static volatile bool sampling;
static uint64_t epoch, epoch_first;
static const char *state="idle";
static uint8_t unit[16], session[16];
static uint64_t sequence, incoming_sequence;
static bool incoming_seen, connected, ready, broken;
static struct tcp_pcb *pcb;
static uint8_t tx[PAYLOAD+100], rx[4096];
static size_t tx_len, tx_written, tx_acked, rx_used;
static bool tx_data;
static uint64_t flight_started, reconnect_at, last_status, last_usb;
static uint32_t crc_table[256];
typedef struct { uint16_t kind; uint64_t first; char json[768]; } control;
static control queue[16];
static unsigned qhead,qtail;
static uint64_t connect_started;
typedef struct {uint64_t id; char request[2049], reply[768];} cached;
static cached cache[8];
static unsigned cache_pos;
static uint64_t highest;
static void put(uint8_t *p,uint64_t v,unsigned n){for(unsigned i=0;i<n;i++)p[i]=(uint8_t)(v>>(8*i));}
static uint64_t get(const uint8_t *p,unsigned n){uint64_t v=0;for(unsigned i=0;i<n;i++)v|=(uint64_t)p[i]<<(8*i);return v;}
static uint32_t crc(const uint8_t *p,size_t n){uint32_t c=~0u;while(n--)c=crc_table[(c^*p++)&255]^(c>>8);return ~c;}
static uint64_t snapshot(uint32_t *buffer,uint64_t *loss){
    uint32_t irq=save_and_disable_interrupts();
    uint64_t n=next_sample;*buffer=(produced-consumed)*ROWS;*loss=dropped;
    restore_interrupts(irq);return n;
}
static bool produce(struct repeating_timer *timer){
    (void)timer;if(!sampling)return true;
    uint64_t due=epoch_first+((time_us_64()-epoch)/10240)*ROWS;
    if(due<=next_sample)return true;
    if(due-next_sample>ROWS){dropped+=due-next_sample-ROWS;next_sample=due-ROWS;}
    uint64_t first=next_sample; next_sample+=ROWS;
    if(produced-consumed==SLOTS){dropped+=ROWS;return true;}
    block *b=&ring[produced%SLOTS];b->first=first;
    for(unsigned i=0;i<ROWS*3;i++){
        uint32_t code=(uint32_t)((first*3+i+MND_SEED)&0xffffff)-8388608u;
        put(b->bytes+i*3,code,3);
    }
    __dmb();produced++;return true;
}
static void enqueue(uint16_t kind,uint64_t first,const char *json){
    if(qhead-qtail==16){broken=true;return;}
    control *c=&queue[qhead++%16];c->kind=kind;c->first=first;
    snprintf(c->json,sizeof c->json,"%s",json);
}
static void status(void){
    uint32_t buffered;uint64_t loss,n=snapshot(&buffered,&loss);char j[768];
    snprintf(j,sizeof j,"{\"state\":\"%s\",\"next_sample\":\"%"PRIu64"\",\"buffer_rows\":%"PRIu32",\"dropped_rows\":\"%"PRIu64"\"}",state,n,buffered,loss);
    enqueue(3,n,j);
}
static bool decimal(const cJSON *v,uint64_t *n){
    if(!cJSON_IsString(v)||!v->valuestring[0])return false;
    *n=0;for(const char *p=v->valuestring;*p;p++){
        if(*p<'0'||*p>'9'||*n>(UINT64_MAX-(unsigned)(*p-'0'))/10)return false;
        *n=*n*10+(unsigned)(*p-'0');
    }return true;
}
static cJSON *field(const cJSON *o,const char *key){return cJSON_GetObjectItemCaseSensitive(o,key);}
static bool unique(const cJSON *v,unsigned depth){
    if(depth>16)return false;
    for(const cJSON *a=v->child;a;a=a->next){
        if(cJSON_IsObject(v))for(const cJSON *b=a->next;b;b=b->next)
            if(!strcmp(a->string,b->string))return false;
        if(!unique(a,depth+1))return false;
    }return true;
}
static bool number(const cJSON *o,const char *k,double value){
    const cJSON *v=field(o,k);return cJSON_IsNumber(v)&&v->valuedouble==value;
}
static void command(char *json,size_t len,uint16_t kind){
    const char *end=NULL;cJSON *o=cJSON_ParseWithLengthOpts(json,len+1,&end,true);
    if(!o||!cJSON_IsObject(o)||!unique(o,0)){cJSON_Delete(o);broken=true;return;}
    uint64_t id;
    if(!decimal(field(o,"request_id"),&id)){cJSON_Delete(o);broken=true;return;}
    if(kind==5){if(id==0&&cJSON_IsTrue(field(o,"ok")))ready=true;else broken=true;cJSON_Delete(o);return;}
    if(id==0){cJSON_Delete(o);broken=true;return;}
    uint32_t buffered;uint64_t loss,n=snapshot(&buffered,&loss);
    for(unsigned i=0;i<8;i++)if(cache[i].id==id){
        if(!strcmp(cache[i].request,json))enqueue(5,n,cache[i].reply);
        else broken=true;
        cJSON_Delete(o);return;
    }
    const char *error=NULL;char details[256]="{}";
    cJSON *op=field(o,"op"),*args=field(o,"args");
    if(id<=highest||!cJSON_IsString(op)||!cJSON_IsObject(args))error="invalid_args";
    else if(!strcmp(op->valuestring,"arm")){
        cJSON *c=field(args,"config");
        if(strcmp(state,"idle")||buffered)error="invalid_state";
        else if(!cJSON_IsObject(c)||!number(c,"id",1)||!number(c,"sample_rate_hz",25000)||!number(c,"encoding",1)||!cJSON_IsTrue(field(c,"synthetic")))error="invalid_args";
        else{state="armed";strcpy(details,"{\"config\":{\"id\":1,\"sample_rate_hz\":25000,\"encoding\":1,\"synthetic\":true}}");}
    }else if(!strcmp(op->valuestring,"start")){
        if(args->child)error="invalid_args";
        else if(strcmp(state,"armed"))error="invalid_state";
        else{uint32_t irq=save_and_disable_interrupts();epoch=time_us_64();epoch_first=next_sample;sampling=true;restore_interrupts(irq);state="sampling";}
    }else if(!strcmp(op->valuestring,"stop")){
        if(args->child)error="invalid_args";
        else{uint32_t irq=save_and_disable_interrupts();sampling=false;restore_interrupts(irq);state="idle";}
    }else if(!strcmp(op->valuestring,"status")){
        if(args->child)error="invalid_args";
        else snprintf(details,sizeof details,"{\"state\":\"%s\",\"next_sample\":\"%"PRIu64"\",\"buffer_rows\":%"PRIu32",\"dropped_rows\":\"%"PRIu64"\"}",state,n,buffered,loss);
    }else error=!strcmp(op->valuestring,"recover")?"unavailable":"unsupported";
    char answer[768];
    snprintf(answer,sizeof answer,"{\"request_id\":\"%"PRIu64"\",\"ok\":%s,\"state\":\"%s\",\"effective_sample\":\"%"PRIu64"\",\"error\":%s%s%s,\"details\":%s}",id,error?"false":"true",state,n,error?"\"":"",error?error:"null",error?"\"":"",details);
    if(id>highest){highest=id;cached *c=&cache[cache_pos++%8];c->id=id;memcpy(c->request,json,len+1);strcpy(c->reply,answer);}
    enqueue(5,n,answer);cJSON_Delete(o);
}
static void received_frame(void){
    uint32_t len=(uint32_t)get(rx+8,4),payload=(uint32_t)get(rx+88,4);
    uint16_t kind=(uint16_t)get(rx+6,2);
    uint64_t seq=get(rx+48,8);
    if(get(rx+4,2)!=1||(kind!=4&&kind!=5)||payload!=len-100||payload>2048||
       get(rx+12,4)||get(rx+64,4)||get(rx+84,4)||get(rx+92,4)||
       memcmp(rx+16,unit,16)||memcmp(rx+32,session,16)||
       (incoming_seen&&seq<=incoming_sequence)||crc(rx,len-4)!=get(rx+len-4,4)){
        broken=true;return;
    }
    incoming_seen=true;incoming_sequence=seq;
    char json[2049];memcpy(json,rx+96,payload);json[payload]=0;
    if(memchr(json,0,payload)){broken=true;return;}
    command(json,payload,kind);
}
static err_t receive(void *arg,struct tcp_pcb *t,struct pbuf *p,err_t err){
    (void)arg;if(!p){broken=true;return ERR_OK;}
    if(err!=ERR_OK){pbuf_free(p);broken=true;return ERR_OK;}
    for(struct pbuf *b=p;b&&!broken;b=b->next)for(unsigned i=0;i<b->len&&!broken;i++){
        if(rx_used==sizeof rx){broken=true;break;}
        rx[rx_used++]=((uint8_t*)b->payload)[i];
        if(rx_used>=12){
            uint32_t n=(uint32_t)get(rx+8,4);
            if(memcmp(rx,"ELD1",4)||n<100||n>sizeof rx){broken=true;break;}
            if(rx_used==n){received_frame();rx_used=0;}
        }
    }
    tcp_recved(t,p->tot_len);pbuf_free(p);return ERR_OK;
}
static err_t sent(void *arg,struct tcp_pcb *t,u16_t len){(void)arg;(void)t;tx_acked+=len;return ERR_OK;}
static void failed(void *arg,err_t err){(void)arg;(void)err;pcb=NULL;broken=true;}
static err_t established(void *arg,struct tcp_pcb *t,err_t err){
    (void)arg;if(err!=ERR_OK){broken=true;return err;}connected=true;tcp_nagle_disable(t);
    char j[768];uint32_t buffered;uint64_t loss,n=snapshot(&buffered,&loss);
    snprintf(j,sizeof j,"{\"firmware\":\"multinodedaq-pico-counter\",\"software_build\":\"%s\",\"protocol\":1,\"synthetic\":true,\"axes\":[\"X\",\"Y\",\"Z\"],\"encodings\":[1],\"capabilities\":[\"status\",\"arm\",\"start\",\"stop\",\"recover\"],\"label\":\"Pico 2 W\",\"mode\":\"counter\",\"seed\":%u,\"preferred_encoding\":1,\"state\":\"%s\",\"next_sample\":\"%"PRIu64"\"}",MND_BUILD,MND_SEED,state,n);
    enqueue(1,n,j);return ERR_OK;
}
static void make_frame(uint16_t kind,uint64_t first,const void *payload,size_t len){
    memset(tx,0,96);memcpy(tx,"ELD1",4);put(tx+4,1,2);put(tx+6,kind,2);put(tx+8,100+len,4);
    memcpy(tx+16,unit,16);memcpy(tx+32,session,16);put(tx+48,sequence++,8);put(tx+56,first,8);
    put(tx+68,25000,4);put(tx+72,1,4);put(tx+88,len,4);
    if(kind==2){put(tx+64,ROWS,4);put(tx+84,3,2);put(tx+86,1,2);}
    memcpy(tx+96,payload,len);put(tx+96+len,crc(tx,96+len),4);
    tx_len=100+len;tx_written=tx_acked=0;tx_data=kind==2;flight_started=time_us_64();
}
static void network(void){
    if(tx_len&&tx_acked==tx_len){if(tx_data){__dmb();consumed++;}tx_len=0;}
    if(!tx_len){
        if(qtail!=qhead){control *c=&queue[qtail++%16];make_frame(c->kind,c->first,c->json,strlen(c->json));}
        else if(ready&&produced!=consumed){__dmb();block *b=&ring[consumed%SLOTS];make_frame(2,b->first,b->bytes,PAYLOAD);}
    }
    if(tx_len&&time_us_64()-flight_started>10000000){broken=true;return;}
    if(tx_written<tx_len){
        size_t n=tx_len-tx_written;if(n>tcp_sndbuf(pcb))n=tcp_sndbuf(pcb);
        if(n){err_t e=tcp_write(pcb,tx+tx_written,(u16_t)n,TCP_WRITE_FLAG_COPY);if(e==ERR_OK){tx_written+=n;tcp_output(pcb);}else if(e!=ERR_MEM)broken=true;}
    }
}
int main(void){
    stdio_init_all();
    for(unsigned i=0;i<256;i++){uint32_t c=i;for(unsigned b=0;b<8;b++)c=(c>>1)^((c&1)?0xedb88320u:0);crc_table[i]=c;}
    pico_unique_board_id_t id;pico_get_unique_board_id(&id);
    memcpy(unit,"MND-PICO",8);memcpy(unit+8,id.id,8);
    put(session,get_rand_64(),8);put(session+8,get_rand_64(),8);
    struct repeating_timer timer;add_repeating_timer_us(-10240,produce,NULL,&timer);
    int init=cyw43_arch_init_with_country(CYW43_COUNTRY_USA);
    if(init){printf("{\"fatal\":\"radio_init\",\"code\":%d}\n",init);while(true)sleep_ms(1000);}
    cyw43_arch_enable_sta_mode();
    cyw43_wifi_pm(&cyw43_state,CYW43_NO_POWERSAVE_MODE);
    cyw43_arch_wifi_connect_async(MND_SSID,MND_PASSWORD,CYW43_AUTH_WPA2_AES_PSK);
    uint64_t wifi_retry=time_us_64()+30000000;
    while(true){
        cyw43_arch_poll();uint64_t now=time_us_64();
        int link=cyw43_tcpip_link_status(&cyw43_state,CYW43_ITF_STA);
        if(pcb&&!connected&&now-connect_started>5000000)broken=true;
        if(broken||(connected&&link!=CYW43_LINK_UP)){
            if(pcb){tcp_arg(pcb,NULL);tcp_err(pcb,NULL);tcp_abort(pcb);pcb=NULL;}
            connected=ready=broken=false;tx_len=rx_used=0;qhead=qtail=0;incoming_seen=false;reconnect_at=now+2000000;
        }
        if(link==CYW43_LINK_UP&&!pcb&&now>=reconnect_at){
            ip_addr_t addr;if(ipaddr_aton(MND_HOST,&addr)){
                connect_started=now;pcb=tcp_new_ip_type(IPADDR_TYPE_V4);
                if(pcb){tcp_recv(pcb,receive);tcp_sent(pcb,sent);tcp_err(pcb,failed);if(tcp_connect(pcb,&addr,MND_PORT,established)!=ERR_OK)broken=true;}
            }reconnect_at=now+5000000;
        }
        if(link<CYW43_LINK_NOIP&&now>=wifi_retry){cyw43_arch_wifi_connect_async(MND_SSID,MND_PASSWORD,CYW43_AUTH_WPA2_AES_PSK);wifi_retry=now+30000000;}
        if(connected){if(ready&&now-last_status>=1000000){status();last_status=now;}network();}
        if(now-last_usb>=1000000){
            last_usb=now;if(stdio_usb_connected())printf("{\"ip\":\"%s\",\"pcb_state\":%d}\n",ip4addr_ntoa(netif_ip4_addr(&cyw43_state.netif[0])),pcb?(int)pcb->state:-1);uint32_t buffered;uint64_t loss,n=snapshot(&buffered,&loss);
            if(stdio_usb_connected())printf("{\"firmware\":\"pico-counter\",\"wifi_link\":%d,\"tcp\":%s,\"ready\":%s,\"state\":\"%s\",\"next_sample\":\"%"PRIu64"\",\"buffer_rows\":%"PRIu32",\"dropped_rows\":\"%"PRIu64"\"}\n",link,connected?"true":"false",ready?"true":"false",state,n,buffered,loss);
            cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN,connected);
        }
        sleep_us(100);
    }
}
