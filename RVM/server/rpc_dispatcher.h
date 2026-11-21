#pragma once

#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <vector>

#include "core/engine.h"
#include "core/result.h"
#include "rvm/v1/rpc.pb.h"

namespace rvm::server {

class SessionManager;

struct DispatchResult {
  std::vector<std::uint8_t> payload;
  bool close = false;
};

// Writes one stream frame to the requesting client. Called only from the
// connection thread that dispatched the request.
using StreamSink = std::function<core::Status(const v1::StreamFrame&)>;

class RpcDispatcher {
 public:
  RpcDispatcher(core::Engine& engine, std::string expected_token,
                SessionManager* sessions = nullptr);

  core::Result<DispatchResult> Handle(std::span<const std::uint8_t> request,
                                      std::uint32_t peer_pid, const StreamSink& stream,
                                      const std::function<bool()>& peer_alive);

  bool authenticated() const { return authenticated_; }

 private:
  core::Engine& engine_;
  std::string expected_token_;
  SessionManager* sessions_ = nullptr;
  bool authenticated_ = false;
};

}  // namespace rvm::server