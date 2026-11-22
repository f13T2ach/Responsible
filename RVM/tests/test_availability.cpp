#include <string>

#include "environment/availability.h"
#include "test_registry.h"

using rvm::environment::AvailabilityReport;
using rvm::environment::ProbeResult;
using rvm::environment::ProbeState;

namespace {
AvailabilityReport AllOk() {
  AvailabilityReport report;
  report.appcontainer = {ProbeState::kOk, "ok"};
  report.winfsp = {ProbeState::kOk, "ok"};
  report.vbs = {ProbeState::kOk, "ok"};
  report.hvci = {ProbeState::kOk, "ok"};
  return report;
}
}  // namespace

RVM_TEST(availability_requires_appcontainer) {
  AvailabilityReport report = AllOk();
  RVM_CHECK(report.rvm_available());
  RVM_CHECK(report.reason().empty());

  report.appcontainer = {ProbeState::kNotProbed, "not probed"};
  RVM_CHECK(!report.rvm_available());
  RVM_CHECK(report.reason().find("appcontainer") != std::string::npos);
  RVM_CHECK(report.reason().find("not probed") != std::string::npos);
}

RVM_TEST(availability_reason_only_lists_appcontainer) {
  AvailabilityReport report;
  report.appcontainer = {ProbeState::kFailed, "userenv.dll is missing"};
  report.vbs = {ProbeState::kFailed, "disabled"};
  report.hvci = {ProbeState::kFailed, "disabled"};

  const std::string reason = report.reason();
  RVM_CHECK(reason.find("appcontainer") != std::string::npos);
  RVM_CHECK(reason.find("vbs") == std::string::npos);
  RVM_CHECK(reason.find("hvci") == std::string::npos);
  RVM_CHECK(!report.rvm_available());
}

RVM_TEST(availability_vbs_and_hvci_are_informational) {
  AvailabilityReport report = AllOk();
  report.vbs = {ProbeState::kFailed, "disabled"};
  report.hvci = {ProbeState::kFailed, "memory integrity is off"};
  RVM_CHECK(report.rvm_available());
  RVM_CHECK(report.reason().empty());

  const std::string summary = report.Summary();
  RVM_CHECK(summary.find("vbs=failed") != std::string::npos);
  RVM_CHECK(summary.find("hvci=failed") != std::string::npos);
  RVM_CHECK(summary.find("rvm_available=true") != std::string::npos);
}

RVM_TEST(availability_winfsp_is_reported_but_optional) {
  AvailabilityReport report = AllOk();
  report.winfsp = {ProbeState::kFailed, "WinFsp is not installed"};
  RVM_CHECK(report.rvm_available());
  RVM_CHECK(report.reason().empty());
  RVM_CHECK(report.Summary().find("winfsp=failed") != std::string::npos);
}

RVM_TEST(availability_summary_exposes_availability) {
  AvailabilityReport report = AllOk();
  const std::string summary = report.Summary();
  RVM_CHECK(summary.find("backend=sandbox") != std::string::npos);
  RVM_CHECK(summary.find("rvm_available=true") != std::string::npos);

  report.appcontainer = {ProbeState::kFailed, "appcontainer is unavailable"};
  const std::string failed = report.Summary();
  RVM_CHECK(failed.find("rvm_available=false") != std::string::npos);
  RVM_CHECK(failed.find("appcontainer: failed") != std::string::npos);
}