#include <Arduino.h>
#include <Arduino_GFX_Library.h>
#include <USB.h>
#include <USBHIDVendor.h>
#include <driver/gpio.h>
#include <tusb.h>

#include <algorithm>
#include <cstring>

#include "display_config.h"
#include "telemetry_protocol.h"

using TelemetryProtocol::Flags;
using TelemetryProtocol::OutputReport;

namespace Colors {
constexpr uint16_t Background = 0x18E3;
constexpr uint16_t Header = 0x2104;
constexpr uint16_t Card = 0x2C0E;
constexpr uint16_t CardInner = 0x340F;
constexpr uint16_t Text = 0xFFFF;
constexpr uint16_t Muted = 0xA534;
constexpr uint16_t Cyan = 0x4D7F;
constexpr uint16_t Lime = 0x9FE3;
constexpr uint16_t Orange = 0xFD45;
constexpr uint16_t Purple = 0xB59F;
constexpr uint16_t Grid = 0x4228;
// Higher-contrast pre-blended sparkline colors for the TFT's limited viewing
// angle and contrast. The fill remains below the foreground line and text.
constexpr uint16_t SparkFill = 0x4536;
constexpr uint16_t SparkLine = 0x96DD;
constexpr uint16_t Offline = 0xF986;
}  // namespace Colors

constexpr uint32_t FrameIntervalMs = 100;
constexpr uint32_t StatusReportIntervalMs = 1000;
constexpr uint32_t OfflineBlankDelayMs = 10000;
constexpr uint32_t UsbUnmountRecoveryDelayMs = 3000;
constexpr uint32_t UsbFirstReportTimeoutMs = 10000;
constexpr uint32_t DisplayRecoveryMagic = 0x4C434452;
constexpr int16_t HeaderGroupLeft = 328;
constexpr int16_t HeaderTextRight = 468;
RTC_NOINIT_ATTR uint32_t displayRecoveryMarker;
RTC_NOINIT_ATTR uint32_t displayRecoveryMarkerInverse;
RTC_NOINIT_ATTR OutputReport retainedReport;
bool displayRecoveryBoot = false;

class VendorSt7796s : public Arduino_ST7796 {
 public:
  using Arduino_ST7796::Arduino_ST7796;

  bool beginPreservingPanel(int32_t speed) {
    // Recreate only the ESP32 LCD bus after a software reset. The ST7796S
    // remained powered, so resending its full initialization would briefly
    // blank the existing framebuffer.
    if (!_bus->begin(speed)) return false;
    setRotation(DisplayRotation);
    setAddrWindow(0, 0, width(), height());
    return true;
  }

  void setRotation(uint8_t rotation) override {
    // This module's scan direction differs from Arduino_GFX's generic ST7796
    // table. In landscape, generic rotation 1 writes 0x68 (MX | MV | BGR),
    // which mirrors the complete UI. The vendor's 0x48 portrait baseline
    // corresponds to this module-specific rotation table.
    Arduino_TFT::setRotation(rotation);
    static constexpr uint8_t madctl[] = {
        0x48,  // portrait
        0x28,  // landscape
        0x88,  // portrait, 180 degrees
        0xE8,  // landscape, 180 degrees
    };

    _bus->beginWrite();
    _bus->writeC8D8(0x36, madctl[rotation & 3U]);
    _bus->endWrite();
  }

 protected:
  void tftInit() override {
    pinMode(_rst, OUTPUT);
    digitalWrite(_rst, HIGH);
    if (!displayRecoveryBoot) {
      delay(5);
      digitalWrite(_rst, LOW);
      delay(15);
      digitalWrite(_rst, HIGH);
      delay(150);
    }

    _bus->beginWrite();
    send(0xF0, {0xC3});
    send(0xF0, {0x96});
    send(0x36, {0x68});
    send(0x3A, {0x05});
    send(0xB0, {0x80});
    send(0xB6, {0x00, 0x02});
    send(0xB5, {0x02, 0x03, 0x00, 0x04});
    send(0xB1, {0x80, 0x10});
    send(0xB4, {0x00});
    send(0xB7, {0xC6});
    send(0xC5, {0x24});
    send(0xE4, {0x31});
    send(0xE8, {0x40, 0x8A, 0x00, 0x00, 0x29, 0x19, 0xA5, 0x33});
    send(0xC2, {});
    send(0xA7, {});
    send(0xE0, {0xF0, 0x09, 0x13, 0x12, 0x12, 0x2B, 0x3C,
                0x44, 0x4B, 0x1B, 0x18, 0x17, 0x1D, 0x21});
    send(0xE1, {0xF0, 0x09, 0x13, 0x0C, 0x0D, 0x27, 0x3B,
                0x44, 0x4D, 0x0B, 0x17, 0x17, 0x1D, 0x21});
    send(0x36, {0x48});
    send(0xF0, {0xC3});
    send(0xF0, {0x69});
    send(0x13, {});
    send(0x11, {});
    _bus->endWrite();
    delay(150);

    _bus->beginWrite();
    send(0x29, {});
    _bus->endWrite();
    delay(50);
  }

 private:
  void send(uint8_t command, std::initializer_list<uint8_t> data) {
    _bus->writeCommand(command);
    for (uint8_t value : data) _bus->write(value);
  }
};

// ESP32-S3-specific i80 LCD peripheral. The generic ESP32PAR8 implementation
// can compile for the S3 but is not the preferred hardware path on this chip.
Arduino_DataBus* bus = new Arduino_ESP32LCD8(
    DisplayPins::Dc, DisplayPins::Cs, DisplayPins::Wr, DisplayPins::Rd,
    DisplayPins::D0, DisplayPins::D1, DisplayPins::D2, DisplayPins::D3,
    DisplayPins::D4, DisplayPins::D5, DisplayPins::D6, DisplayPins::D7);

// Confirmed panel controller: ST7796S. Arduino_GFX names the class ST7796;
// the S suffix is the same controller family/interface for this purpose.
VendorSt7796s* panel = new VendorSt7796s(bus, DisplayPins::Rst,
                                        DisplayRotation, false);
Arduino_GFX* gfx = panel;
USBHIDVendor hid(TelemetryProtocol::HidPayloadSize);

OutputReport current{};
OutputReport drawn{};
bool hasTelemetry = false;
bool wasOnline = false;
uint32_t lastReportMs = 0;
uint32_t lastFrameMs = 0;
uint32_t lastStatusReportMs = 0;
uint32_t offlineSinceMs = 0;
bool displayBlanked = false;
uint32_t recoveryBootAtMs = 0;
uint32_t usbUnmountedAtMs = 0;
bool usbWasMounted = false;

void drawStaticUi();
void drawConnection(bool online);

void restartUsb() {
  hasTelemetry = false;
  wasOnline = false;
  lastReportMs = 0;
  displayRecoveryMarker = DisplayRecoveryMagic;
  displayRecoveryMarkerInverse = ~DisplayRecoveryMagic;
  retainedReport = current;

  // Recreate the USB controller and endpoints instead of retaining the stalled
  // HID OUT endpoint that Windows can leave behind after sleep.
  hid.end();
  tud_disconnect();

  // Hold inactive LCD control levels through ESP.restart() so the powered TFT
  // never sees a reset pulse or an accidental write while GPIOs reconfigure.
  digitalWrite(DisplayPins::Rst, HIGH);
  digitalWrite(DisplayPins::Cs, HIGH);
  digitalWrite(DisplayPins::Wr, HIGH);
  gpio_hold_en(static_cast<gpio_num_t>(DisplayPins::Rst));
  gpio_hold_en(static_cast<gpio_num_t>(DisplayPins::Cs));
  gpio_hold_en(static_cast<gpio_num_t>(DisplayPins::Wr));
  delay(1500);
  ESP.restart();
}

constexpr size_t SparkPoints = 30U * 60U;

struct SparkHistory {
  uint16_t values[SparkPoints]{};
  size_t count = 0;
  size_t next = 0;
};

SparkHistory cpuTempHistory;
SparkHistory gpuTempHistory;
SparkHistory cpuPowerHistory;
SparkHistory cpuLoadHistory;
SparkHistory gpuLoadHistory;
SparkHistory gpuPowerHistory;
SparkHistory radFanHistory;
SparkHistory ioFanHistory;
SparkHistory pcieFanHistory;
SparkHistory exhaustFanHistory;

struct Card {
  int16_t x;
  int16_t y;
  int16_t w;
  int16_t h;
  const char* title;
  const char* unit;
  uint16_t accent;
};

constexpr Card CpuTempCard{10, 48, 146, 82, "CPU TEMP", "C", Colors::Orange};
constexpr Card GpuTempCard{10, 141, 146, 78, "GPU TEMP", "C", Colors::Lime};
constexpr Card CpuLoadCard{167, 48, 146, 82, "CPU LOAD", "%", Colors::Orange};
constexpr Card GpuLoadCard{167, 141, 146, 78, "GPU LOAD", "%", Colors::Lime};
constexpr Card CpuPowerCard{324, 48, 146, 82, "CPU POWER", "W", Colors::Orange};
constexpr Card GpuPowerCard{324, 141, 146, 78, "GPU POWER", "W", Colors::Lime};

void drawText(const char* text, int16_t x, int16_t y, uint8_t size,
              uint16_t color) {
  gfx->setTextSize(size);
  gfx->setTextColor(color);
  gfx->setCursor(x, y);
  gfx->print(text);
}

void drawCardFrame(const Card& card) {
  gfx->fillRoundRect(card.x, card.y, card.w, card.h, 7, Colors::Card);
  gfx->drawRoundRect(card.x, card.y, card.w, card.h, 7, card.accent);
  drawText(card.title, card.x + 9, card.y + 9, 1, Colors::Text);
}

void drawStaticUi() {
  gfx->fillScreen(Colors::Background);
  gfx->fillRect(0, 0, DisplayWidth, 38, Colors::Header);
  drawText("PC TELEMETRY", 12, 8, 2, Colors::Text);
  drawCardFrame(CpuTempCard);
  drawCardFrame(GpuTempCard);
  drawCardFrame(CpuPowerCard);
  drawCardFrame(CpuLoadCard);
  drawCardFrame(GpuLoadCard);
  drawCardFrame(GpuPowerCard);

  gfx->fillRoundRect(10, 230, 460, 80, 7, Colors::Card);
  gfx->drawRoundRect(10, 230, 460, 80, 7, Colors::Cyan);
  drawText("FAN OUTPUTS", 19, 239, 1, Colors::Text);
  drawText("RAD", 35, 255, 1, Colors::Muted);
  drawText("IO", 157, 255, 1, Colors::Muted);
  drawText("PCIE", 259, 255, 1, Colors::Muted);
  drawText("EXHAUST", 366, 255, 1, Colors::Muted);
  gfx->drawFastVLine(125, 252, 48, Colors::Grid);
  gfx->drawFastVLine(235, 252, 48, Colors::Grid);
  gfx->drawFastVLine(345, 252, 48, Colors::Grid);
}

void drawConnection(bool online) {
  const char* label = online ? "LIVE" : "OFFLINE";
  const int16_t labelWidth = static_cast<int16_t>(strlen(label) * 6);
  const int16_t labelX = HeaderTextRight - labelWidth;
  const int16_t dotX = labelX - 12;
  constexpr int16_t UsbDisplayWidth = 11 * 6;
  const int16_t usbDisplayX = dotX - 4 - 8 - UsbDisplayWidth;

  gfx->fillRect(HeaderGroupLeft, 4, HeaderTextRight - HeaderGroupLeft + 2,
                28, Colors::Header);
  drawText("USB DISPLAY", usbDisplayX, 13, 1, Colors::Muted);
  gfx->fillCircle(dotX, 17, 4, online ? Colors::Lime : Colors::Offline);
  drawText(label, labelX, 13, 1,
           online ? Colors::Text : Colors::Offline);
}

void formatWholeX10(char* output, size_t length, uint16_t value) {
  snprintf(output, length, "%u", (value + 5U) / 10U);
}

void formatWholeSignedX10(char* output, size_t length, int16_t value) {
  const int32_t rounded = value >= 0 ? (value + 5) / 10 : (value - 5) / 10;
  snprintf(output, length, "%ld", static_cast<long>(rounded));
}

void appendHistory(SparkHistory& history, uint16_t value, bool valid) {
  if (!valid) return;
  history.values[history.next] = value;
  history.next = (history.next + 1) % SparkPoints;
  history.count = std::min(history.count + 1, SparkPoints);
}

uint16_t historyValue(const SparkHistory& history, size_t chronologicalIndex) {
  const size_t oldest = history.count < SparkPoints ? 0 : history.next;
  return history.values[(oldest + chronologicalIndex) % SparkPoints];
}

void drawSparkline(int16_t x, int16_t y, int16_t w, int16_t h,
                   const SparkHistory& history) {
  if (history.count < 2) return;

  uint16_t minimum = historyValue(history, 0);
  uint16_t maximum = minimum;
  for (size_t i = 1; i < history.count; ++i) {
    const uint16_t value = historyValue(history, i);
    minimum = std::min(minimum, value);
    maximum = std::max(maximum, value);
  }
  const uint16_t span = std::max<uint16_t>(10, maximum - minimum);

  // Downsample the full 30-minute buffer into one average per screen column.
  // This keeps all 1,800 source samples while avoiding overdraw on a 92-130 px
  // wide chart. RGB565 has no alpha, so SparkFill is pre-blended.
  int16_t previousY = y + h - 1;
  for (int16_t column = 0; column < w; ++column) {
    const size_t start = static_cast<size_t>(column) * history.count / w;
    size_t end = static_cast<size_t>(column + 1) * history.count / w;
    end = std::max(end, start + 1);
    end = std::min(end, history.count);

    uint32_t total = 0;
    for (size_t sample = start; sample < end; ++sample) {
      total += historyValue(history, sample);
    }
    const uint16_t average = static_cast<uint16_t>(total / (end - start));
    const int16_t pointY = y + h - 1 -
        static_cast<int16_t>((average - minimum) * (h - 1) / span);
    gfx->drawFastVLine(x + column, pointY, y + h - pointY,
                       Colors::SparkFill);
    if (column > 0) {
      gfx->drawLine(x + column - 1, previousY, x + column, pointY,
                    Colors::SparkLine);
    }
    previousY = pointY;
  }
}

void drawCardValue(const Card& card, const char* value, bool valid,
                   const SparkHistory& history) {
  const int16_t contentHeight = card.h - 36;
  gfx->fillRect(card.x + 7, card.y + 29, card.w - 14, contentHeight, Colors::Card);
  drawSparkline(card.x + 8, card.y + card.h - 30, card.w - 16, 23, history);
  drawText(valid ? value : "--", card.x + 9, card.y + 35, 3,
           valid ? Colors::Text : Colors::Muted);
  drawText(card.unit, card.x + card.w - 21, card.y + 43, 2, card.accent);
}

void drawFanValue(int16_t x, uint16_t valueX10, bool valid,
                  const SparkHistory& history) {
  char text[12];
  gfx->fillRect(x, 270, 92, 27, Colors::Card);
  drawSparkline(x, 272, 92, 24, history);
  if (valid) {
    snprintf(text, sizeof(text), "%u%%", (valueX10 + 5U) / 10U);
  } else {
    snprintf(text, sizeof(text), "--%%");
  }
  drawText(text, x, 273, 2, valid ? Colors::Text : Colors::Muted);
}

void drawFans() {
  using TelemetryProtocol::FanFlags;
  appendHistory(radFanHistory, current.radFanPercentX10,
                current.fanValidFlags & FanFlags::RadFanValid);
  appendHistory(ioFanHistory, current.ioFanPercentX10,
                current.fanValidFlags & FanFlags::IoFanValid);
  appendHistory(pcieFanHistory, current.pcieFanPercentX10,
                current.fanValidFlags & FanFlags::PcieFanValid);
  appendHistory(exhaustFanHistory, current.exhaustFanPercentX10,
                current.fanValidFlags & FanFlags::ExhaustFanValid);
  drawFanValue(27, current.radFanPercentX10,
               current.fanValidFlags & FanFlags::RadFanValid, radFanHistory);
  drawFanValue(137, current.ioFanPercentX10,
               current.fanValidFlags & FanFlags::IoFanValid, ioFanHistory);
  drawFanValue(247, current.pcieFanPercentX10,
               current.fanValidFlags & FanFlags::PcieFanValid, pcieFanHistory);
  drawFanValue(357, current.exhaustFanPercentX10,
               current.fanValidFlags & FanFlags::ExhaustFanValid, exhaustFanHistory);
}

void drawTelemetry() {
  char text[16];

  appendHistory(cpuTempHistory, std::max<int16_t>(0, current.cpuTempX10),
                current.validFlags & Flags::CpuTempValid);
  appendHistory(gpuTempHistory, std::max<int16_t>(0, current.gpuTempX10),
                current.validFlags & Flags::GpuTempValid);
  appendHistory(cpuPowerHistory, current.cpuPowerX10,
                current.validFlags & Flags::CpuPowerValid);
  appendHistory(cpuLoadHistory, current.cpuLoadX10,
                current.validFlags & Flags::CpuLoadValid);
  appendHistory(gpuLoadHistory, current.gpuLoadX10,
                current.validFlags & Flags::GpuLoadValid);
  appendHistory(gpuPowerHistory, current.gpuPowerX10,
                current.validFlags & Flags::GpuPowerValid);

  formatWholeSignedX10(text, sizeof(text), current.cpuTempX10);
  drawCardValue(CpuTempCard, text, current.validFlags & Flags::CpuTempValid,
                cpuTempHistory);
  formatWholeSignedX10(text, sizeof(text), current.gpuTempX10);
  drawCardValue(GpuTempCard, text, current.validFlags & Flags::GpuTempValid,
                gpuTempHistory);
  formatWholeX10(text, sizeof(text), current.cpuPowerX10);
  drawCardValue(CpuPowerCard, text, current.validFlags & Flags::CpuPowerValid,
                cpuPowerHistory);
  formatWholeX10(text, sizeof(text), current.cpuLoadX10);
  drawCardValue(CpuLoadCard, text, current.validFlags & Flags::CpuLoadValid,
                cpuLoadHistory);
  formatWholeX10(text, sizeof(text), current.gpuLoadX10);
  drawCardValue(GpuLoadCard, text, current.validFlags & Flags::GpuLoadValid,
                gpuLoadHistory);
  formatWholeX10(text, sizeof(text), current.gpuPowerX10);
  drawCardValue(GpuPowerCard, text, current.validFlags & Flags::GpuPowerValid,
                gpuPowerHistory);

  drawFans();
  drawn = current;
}

void pollHid() {
  while (hid.available() >= static_cast<int>(TelemetryProtocol::HidPayloadSize)) {
    uint8_t packet[TelemetryProtocol::HidPayloadSize]{};
    const int count = hid.read(packet, sizeof(packet));
    if (count < static_cast<int>(sizeof(OutputReport))) continue;

    OutputReport incoming{};
    memcpy(&incoming, packet, sizeof(incoming));
    if (incoming.protocolVersion != TelemetryProtocol::Version) {
      continue;
    }

    current = incoming;
    hasTelemetry = true;
    lastReportMs = millis();
    displayRecoveryMarker = 0;
    displayRecoveryMarkerInverse = 0;
  }
}

void sendStatus() {
  TelemetryProtocol::InputReport report{
      TelemetryProtocol::Version,
      current.sequence,
      millis(),
      static_cast<uint8_t>(wasOnline),
  };
  hid.write(reinterpret_cast<const uint8_t*>(&report), sizeof(report));
}

void setup() {
  displayRecoveryBoot =
      displayRecoveryMarker == DisplayRecoveryMagic &&
      displayRecoveryMarkerInverse == ~DisplayRecoveryMagic &&
      retainedReport.protocolVersion == TelemetryProtocol::Version;

  if (displayRecoveryBoot) {
    // Configure the pins as driven outputs before releasing their retained
    // levels, avoiding a floating interval during recovery startup.
    pinMode(DisplayPins::Rst, OUTPUT);
    pinMode(DisplayPins::Cs, OUTPUT);
    pinMode(DisplayPins::Wr, OUTPUT);
    gpio_hold_dis(static_cast<gpio_num_t>(DisplayPins::Rst));
    gpio_hold_dis(static_cast<gpio_num_t>(DisplayPins::Cs));
    gpio_hold_dis(static_cast<gpio_num_t>(DisplayPins::Wr));
    digitalWrite(DisplayPins::Rst, HIGH);
    digitalWrite(DisplayPins::Cs, HIGH);
    digitalWrite(DisplayPins::Wr, HIGH);
  }

  // Start conservatively. Short jumper wiring can be raised later after the
  // display/controller combination is proven stable.
  if (displayRecoveryBoot) {
    panel->beginPreservingPanel(10000000);
  } else {
    gfx->begin(10000000);
  }
  gfx->setTextWrap(false);

  if (displayRecoveryBoot) {
    current = retainedReport;
    // The panel may have already been painted black before USB recovery. Build
    // the complete retained dashboard before showing OFFLINE so the recovery
    // boot never leaves only the top-right connection badge visible.
    drawStaticUi();
    drawTelemetry();
  } else {
    drawStaticUi();
  }
  drawConnection(false);

  hid.begin();
  USB.productName("PC Telemetry Display");
  USB.manufacturerName("PC Telemetry Dashboard");
  USB.begin();

  recoveryBootAtMs = millis();
  usbWasMounted = tud_mounted();
}

void loop() {
  pollHid();
  const uint32_t now = millis();
  const bool usbMounted = tud_mounted();

  // USB remains powered during PC sleep. Recover if Windows drops the mount
  // without electrically resetting the ESP32-S3.
  if (usbMounted) {
    usbWasMounted = true;
    usbUnmountedAtMs = 0;
  } else if (usbWasMounted && usbUnmountedAtMs == 0) {
    usbUnmountedAtMs = now;
  }

  if (usbWasMounted && !usbMounted && usbUnmountedAtMs != 0 &&
      now - usbUnmountedAtMs >= UsbUnmountRecoveryDelayMs) {
    restartUsb();
  }

  // Windows can report a healthy, mounted HID device and complete host writes
  // even though no OUT report reaches TinyUSB after sleep. The only trustworthy
  // acknowledgement is a packet parsed by pollHid(). Recover if none arrives
  // during the startup grace period.
  if (usbMounted && !hasTelemetry &&
      now - recoveryBootAtMs >= UsbFirstReportTimeoutMs) {
    restartUsb();
  }

  const bool online = hasTelemetry && now - lastReportMs < TelemetryProtocol::StaleAfterMs;

  if (online != wasOnline) {
    wasOnline = online;
    if (online) {
      if (displayBlanked) {
        displayBlanked = false;
        drawStaticUi();
        drawConnection(true);
        drawTelemetry();
        lastFrameMs = now;
      } else {
        drawConnection(true);
      }
    } else {
      offlineSinceMs = now;
      drawConnection(false);
    }
  }

  if (!online && !displayBlanked &&
      now - offlineSinceMs >= OfflineBlankDelayMs) {
    gfx->fillScreen(BLACK);
    displayBlanked = true;
  }

  if (online && now - lastFrameMs >= FrameIntervalMs &&
      current.sequence != drawn.sequence) {
    lastFrameMs = now;
    drawTelemetry();
  }

  if (now - lastStatusReportMs >= StatusReportIntervalMs) {
    lastStatusReportMs = now;
    sendStatus();
  }

  delay(2);
}
