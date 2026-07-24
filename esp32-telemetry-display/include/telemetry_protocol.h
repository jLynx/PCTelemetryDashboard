#pragma once

#include <Arduino.h>

namespace TelemetryProtocol {

constexpr uint8_t Version = 2;
// Espressif's USBHIDVendor descriptor assigns report ID 6. Windows includes
// that byte in its 64-byte buffer; the firmware API adds/removes it itself.
constexpr uint8_t UsbReportId = 6;
constexpr size_t HidPayloadSize = 63;
constexpr size_t HostReportSize = HidPayloadSize + 1;
constexpr uint32_t StaleAfterMs = 5000;

enum Flags : uint8_t {
  CpuTempValid = 1U << 0,
  GpuTempValid = 1U << 1,
  CpuLoadValid = 1U << 2,
  GpuLoadValid = 1U << 3,
  CpuPowerValid = 1U << 4,
  GpuPowerValid = 1U << 5,
  CpuFanValid = 1U << 6,
  GpuFanValid = 1U << 7,
};

enum FanFlags : uint8_t {
  RadFanValid = 1U << 0,
  IoFanValid = 1U << 1,
  PcieFanValid = 1U << 2,
  ExhaustFanValid = 1U << 3,
};

// All fractional values are scaled by ten, avoiding float-format differences.
// The PC pads this structure to a 64-byte USB HID output report.
#pragma pack(push, 1)
struct OutputReport {
  uint8_t protocolVersion;
  uint16_t sequence;
  uint32_t sampleAgeMs;
  uint8_t validFlags;
  int16_t cpuTempX10;
  int16_t gpuTempX10;
  uint16_t cpuLoadX10;
  uint16_t gpuLoadX10;
  uint16_t cpuPowerX10;
  uint16_t gpuPowerX10;
  uint16_t cpuFanRpm;
  uint16_t gpuFanRpm;
  uint8_t fanValidFlags;
  uint16_t radFanPercentX10;
  uint16_t ioFanPercentX10;
  uint16_t pcieFanPercentX10;
  uint16_t exhaustFanPercentX10;
};

struct InputReport {
  uint8_t protocolVersion;
  uint16_t lastSequence;
  uint32_t uptimeMs;
  uint8_t displayOnline;
};
#pragma pack(pop)

static_assert(sizeof(OutputReport) == 33, "Unexpected telemetry report layout");
static_assert(sizeof(InputReport) == 8, "Unexpected status report layout");

}  // namespace TelemetryProtocol
