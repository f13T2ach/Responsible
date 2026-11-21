#include "environment/availability.h"

#include <sstream>
#include <utility>
#include <vector>

#include "environment/system_probe.h"

namespace rvm::environment {
namespace {

const char* StateName(ProbeState state) {
  switch (state) {
    case ProbeState::kOk:
      return "ok";
    case ProbeState::kFailed:
      return "failed";
    case ProbeState::kNotProbed:
      return "not probed";
  }
  return "unknown";
}

void AppendProblem(std::vector<std::string>& problems, const char* name,
                   const ProbeResult& result) {
  if (result.state == ProbeState::kOk) return;
  std::string entry = std::string(name) + ": " + StateName(result.state);
  if (!result.detail.empty()) entry += " (" + result.detail + ")";
  problems.push_back(std::move(entry));
}

}  // namespace

bool AvailabilityReport::rvm_available() const {
  return appcontainer.ok() && vbs.ok() && hvci.ok();
}

std::string AvailabilityReport::reason() const {
  std::vector<std::string> problems;
  AppendProblem(problems, "appcontainer", appcontainer);
  AppendProblem(problems, "vbs", vbs);
  AppendProblem(problems, "hvci", hvci);

  std::ostringstream stream;
  for (std::size_t i = 0; i < problems.size(); ++i) {
    if (i != 0) stream << "; ";
    stream << problems[i];
  }
  return stream.str();
}

std::string AvailabilityReport::Summary() const {
  std::ostringstream stream;
  stream << "backend=sandbox\n";
  stream << "appcontainer=" << StateName(appcontainer.state) << '\n';
  stream << "winfsp=" << StateName(winfsp.state) << '\n';
  stream << "vbs=" << StateName(vbs.state) << '\n';
  stream << "hvci=" << StateName(hvci.state) << '\n';
  stream << "rvm_available=" << (rvm_available() ? "true" : "false") << '\n';
  stream << "reason=" << reason() << '\n';
  return stream.str();
}

AvailabilityReport SystemAvailabilityProbe::Probe() const {
  AvailabilityReport report;

  const AppContainerProbe appcontainer = ProbeAppContainerApi();
  report.appcontainer =
      ProbeResult{appcontainer.available ? ProbeState::kOk : ProbeState::kFailed,
                  appcontainer.detail};
  report.winfsp = ProbeWinFsp();
  report.vbs = ProbeVbs();
  report.hvci = ProbeHvci();
  return report;
}

}  // namespace rvm::environment