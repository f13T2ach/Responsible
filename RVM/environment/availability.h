#pragma once

#include <string>

namespace rvm::environment {

enum class ProbeState {
  kOk,
  kFailed,
  kNotProbed,
};

struct ProbeResult {
  ProbeState state = ProbeState::kNotProbed;
  std::string detail;

  bool ok() const { return state == ProbeState::kOk; }
};

struct AvailabilityReport {
  ProbeResult appcontainer;
  ProbeResult winfsp;
  ProbeResult vbs;
  ProbeResult hvci;

  bool rvm_available() const;
  std::string reason() const;
  std::string Summary() const;
};

class IAvailabilityProbe {
 public:
  virtual ~IAvailabilityProbe() = default;
  virtual AvailabilityReport Probe() const = 0;
};

class SystemAvailabilityProbe final : public IAvailabilityProbe {
 public:
  AvailabilityReport Probe() const override;
};

}  // namespace rvm::environment