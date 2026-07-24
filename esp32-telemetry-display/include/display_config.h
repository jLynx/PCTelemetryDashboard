#pragma once

// 3.95-inch 480x320 Arduino/Mega shield, wired as an 8-bit parallel display.
// LCD_RD is tied directly to 3V3 and therefore does not consume a GPIO.
// Do not connect the shield's 3V3 pin; power it from 5V and GND.
namespace DisplayPins {
constexpr int D0 = 1;
constexpr int D1 = 2;
constexpr int D2 = 3;
constexpr int D3 = 4;
constexpr int D4 = 5;
constexpr int D5 = 6;
constexpr int D6 = 7;
constexpr int D7 = 8;
constexpr int Cs = 9;
constexpr int Dc = 10;  // LCD_RS on the shield
constexpr int Wr = 11;  // LCD_WR (the shield photo can look like LCD_UR)
constexpr int Rst = 12;
constexpr int Rd = -1;
}  // namespace DisplayPins

constexpr int DisplayWidth = 480;
constexpr int DisplayHeight = 320;
constexpr int DisplayRotation = 1;
