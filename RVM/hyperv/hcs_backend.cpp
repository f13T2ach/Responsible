#include "hyperv/hcs_backend.h"

#include "environment/system_probe.h"
#include "hyperv/hcs_api.h"
#include "hyperv/hcs_environment.h"
#include "hyperv/hcs_layer.h"

namespace rvm::hyperv {
namespace {

std::string Detail(const environment::ProbeResult& api, const environment::ProbeResult& service,
                   const environment::ProbeResult& base_layer) {
  std::string detail;
  auto append = [&](const char* name, const environment::ProbeResult& result) {
    if (result.state == environment::ProbeState::kOk) return;
    if (!detail.empty()) detail += "; ";
    detail += name;
    detail += ": ";
    detail += result.detail.empty() ? "failed" : result.detail;
  };
  append("api", api);
  append("service", service);
  append("base_layer", base_layer);
  return detail;
}

struct HcsProbeResults {
  environment::ProbeResult api;
  environment::ProbeResult service;
  environment::ProbeResult base_layer;

  bool ok() const { return api.ok() && service.ok() && base_layer.ok(); }
};

HcsProbeResults ProbeHcs() {
  const environment::HcsApiProbe api = environment::ProbeHcsApi();
  const environment::ServiceProbe service = environment::ProbeService(L"vmcompute");

  HcsProbeResults probe;
  probe.api = {api.available ? environment::ProbeState::kOk : environment::ProbeState::kFailed,
               api.detail};
  probe.service = {service.running ? environment::ProbeState::kOk
                                   : environment::ProbeState::kFailed,
                   service.detail};

  if (probe.api.ok() && probe.service.ok()) {
    auto hcs = HcsApi::Load();
    if (!hcs.ok()) {
      probe.api = {environment::ProbeState::kFailed, hcs.error().detail};
    } else {
      const auto properties =
          hcs.value().GetServiceProperties(R"({"PropertyTypes":["Basic"]})");
      if (!properties.ok()) {
        probe.service = {environment::ProbeState::kFailed, properties.error().detail};
      }
    }
  }

  const auto layer = BaseLayerStore::Default().Find();
  if (layer.ok()) {
    probe.base_layer = {environment::ProbeState::kOk,
                        "base layer " + layer.value().id + " for build " +
                            layer.value().windows_build};
  } else {
    probe.base_layer = {environment::ProbeState::kFailed,
                        layer.error().message + " (" + layer.error().detail + ")"};
  }
  return probe;
}

}  // namespace

runtime::BackendProbe HcsBackend::Probe() const {
  const HcsProbeResults results = ProbeHcs();
  return {results.ok(), Detail(results.api, results.service, results.base_layer)};
}

core::Result<std::unique_ptr<runtime::IEnvironment>> HcsBackend::Create(
    const runtime::EnvironmentSpec& spec) {
  const HcsProbeResults results = ProbeHcs();
  if (!results.ok()) {
    return core::Error(core::ErrorCode::kHcsUnavailable, "HCS backend is not ready", false,
                       Detail(results.api, results.service, results.base_layer));
  }
  std::unique_ptr<runtime::IEnvironment> environment =
      std::make_unique<HcsEnvironment>(spec, BaseLayerStore::Default());
  return environment;
}

std::unique_ptr<runtime::IEnvironmentBackend> CreateHcsBackend() {
  return std::make_unique<HcsBackend>();
}

}  // namespace rvm::hyperv