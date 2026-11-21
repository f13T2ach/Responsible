#pragma once

#include <string>
#include <string_view>
#include <vector>

#include "environment/availability.h"

namespace rvm::environment {

struct HcsApiProbe {
  bool available = false;
  std::vector<std::string> missing;
  std::string detail;
};

struct ServiceProbe {
  bool found = false;
  bool running = false;
  std::string detail;
};

struct AppContainerProbe {
  bool available = false;
  std::string detail;
};

HcsApiProbe ProbeHcsApi();
ServiceProbe ProbeService(const std::wstring& name);
ProbeResult ProbeHypervisor();
ProbeResult ProbeVbs();
ProbeResult ProbeHvci();
AppContainerProbe ProbeAppContainerApi();
ProbeResult ProbeWinFsp();
std::string CurrentWindowsBuild();

}  // namespace rvm::environment