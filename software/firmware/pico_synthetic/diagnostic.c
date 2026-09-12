// SPDX-License-Identifier: MIT
// Initial hardware bring-up only: no acquisition or network connection.
#include <stdio.h>
#include "pico/stdlib.h"
#include "pico/stdio_usb.h"
#include "pico/unique_id.h"
#include "pico/cyw43_arch.h"
#include "hardware/clocks.h"

int main(void) {
    stdio_init_all();
    char id[PICO_UNIQUE_BOARD_ID_SIZE_BYTES * 2 + 1];
    pico_get_unique_board_id_string(id, sizeof id);
    int radio_status = cyw43_arch_init();
    bool led = false;
    uint32_t sequence = 0;
    uint64_t next = time_us_64();
    while (true) {
        if (time_us_64() >= next) {
            next = time_us_64() + 1000000;
            led = !led;
            if (radio_status == 0) cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, led);
            if (stdio_usb_connected()) {
                printf("{\"firmware\":\"multinodedaq-diagnostic\",\"version\":\"0.5.0-stage5-bringup\","
                       "\"build\":\"%s\",\"board\":\"%s\",\"sdk\":\"%s\",\"board_id\":\"%s\","
                       "\"sequence\":%lu,\"uptime_ms\":%llu,\"clock_hz\":%lu,"
                       "\"cyw43_init\":%d,\"led\":%s,\"acquisition\":\"not_started\",\"wifi\":\"not_connected\"}\n",
                       MND_BUILD, PICO_BOARD, PICO_SDK_VERSION_STRING, id,
                       (unsigned long)sequence, (unsigned long long)(time_us_64() / 1000),
                       (unsigned long)clock_get_hz(clk_sys), radio_status, led ? "true" : "false");
            }
            sequence++;
        }
        sleep_ms(10);
    }
}
