#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#include "core/engine.h"
#include "core/result.h"

namespace rvm::server {

class SessionManager;

struct PipeServerOptions {
  std::wstring pipe_name;
  std::string token;
};

// One pipe instance per client so the host UI and every tool bridge can hold a
// connection at the same time. Requests are still serialized per connection;
// sessions, jobs, and the engine are shared.
class PipeServer {
 public:
  PipeServer(core::Engine& engine, PipeServerOptions options,
             SessionManager* sessions = nullptr);
  ~PipeServer();

  PipeServer(const PipeServer&) = delete;
  PipeServer& operator=(const PipeServer&) = delete;

  core::Status Run();
  void Stop();

 private:
  core::Status HandleClient(HANDLE pipe, std::uint32_t peer_pid);
  bool ClientAlive(HANDLE pipe) const;
  // Closes the unconnected instance the accept loop is blocked on so an
  // engine shutdown requested by a client wakes Run (never a serviced client).
  void WakeAccept();

  core::Engine& engine_;
  SessionManager* sessions_ = nullptr;
  PipeServerOptions options_;
  std::atomic<bool> stop_{false};
  std::atomic<HANDLE> thread_{nullptr};
  // Native handles of connection threads, so Stop can abort their pending reads.
  std::mutex client_mutex_;
  std::vector<HANDLE> client_threads_;
};

}  // namespace rvm::server