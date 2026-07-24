# ESP32-S3 PC Telemetry Display

<img width="1695" height="1271" alt="image" src="https://github.com/user-attachments/assets/c9a94108-d6d0-4d57-91d2-d131c3782f02" />

The panel was confirmed as ST7796S using the manufacturer's initialization
sequence. The successful direct-GPIO bring-up program is preserved at
`backup/display_bringup.cpp.disabled`. The production firmware uses the
ESP32-S3 hardware i80 peripheral for substantially faster drawing.

Native USB HID firmware for the 3.95-inch 480x320 8-bit parallel TFT shield.
It presents as `PC Telemetry Display`; CDC serial and Wi-Fi are disabled.

The lower display strip shows four case-fan outputs: combined radiator fans,
IO fan, PCIe fan, and exhaust fan. The radiator value is the average of System
Fan #2 and System Fan #3.

Displayed values are rounded to whole numbers. Each main metric and fan output
keeps 1,800 one-second samples, representing the last 30 minutes, in a circular
buffer. The full history is downsampled to the available screen columns and
drawn as a shaded sparkline.

## Wiring

| TFT shield | ESP32-S3 Super Mini |
|---|---:|
| LCD_D0 | GPIO1 |
| LCD_D1 | GPIO2 |
| LCD_D2 | GPIO3 |
| LCD_D3 | GPIO4 |
| LCD_D4 | GPIO5 |
| LCD_D5 | GPIO6 |
| LCD_D6 | GPIO7 |
| LCD_D7 | GPIO8 |
| LCD_CS | GPIO9 |
| LCD_RS / DC | GPIO10 |
| LCD_WR | GPIO11 |
| LCD_RST | GPIO12 |
| LCD_RD | 3V3 (tie high) |
| 5V | 5V / VBUS |
| GND | GND |

Leave the shield's `3V3`, `SD_*`, touch, and `NC` pins disconnected. The TFT
can draw substantial backlight current; if the board's USB/VBUS path is not
rated for it, use a separate regulated 5V supply and join the grounds.

## Build and upload

Install PlatformIO, connect the board, then run from this directory:

```powershell
pio run
pio run --target upload
```

The first upload may require holding **BOOT**, tapping **RESET**, starting the
upload, and then releasing **BOOT**. Normal firmware exposes HID only. Entering
the ROM bootloader still provides the board's normal USB flashing interface.

## Display controller

The shield's confirmed controller is ST7796S, configured through Arduino_GFX's
`Arduino_ST7796` driver. At boot it displays red, green, and blue for 350 ms
each before drawing the dashboard. A permanently white panel therefore means
the controller is not accepting initialization or pixel writes; it is
independent of the USB telemetry connection.

## HID protocol

The device uses TinyUSB vendor HID. On Windows each report is 64 bytes: byte 0
is Espressif's vendor report ID (`6`) and bytes 1-63 are the payload. The
firmware's HID API automatically removes/adds byte 0, so the packed structures
in `include/telemetry_protocol.h` begin with the protocol version. Values ending
in `X10` are fixed-point integers (for example, `611` means `61.1`).

The display changes to **OFFLINE** if no valid report arrives for five seconds.
No COM port, network connection, or USB driver installation is required.

Because the USB port remains powered during PC sleep, the firmware watches for
TinyUSB becoming unmounted without an electrical reset. It then detaches and
reboots the USB controller. A mounted device that receives no first telemetry
report within ten seconds uses the same recovery, handling the case where
Windows reports successful HID writes that never reach the ESP32-S3.

USB recovery retains the latest telemetry report in RTC memory and skips the
panel reset pulse during recovery-only reboots. This avoids the white flash and
redraws the previous values with an **OFFLINE** status until fresh data arrives.
