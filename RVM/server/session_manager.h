#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "core/result.h"
#include "environment/availability.h"
#include "runtime/backend.h"
#include "rvm/v1/session.pb.h"

namespace rvm::server {

// What a shell execution needs from a live session: the workspace path the
// session exposes to tools and the containment identity to spawn under.
struct ShellContext {
  std::wstring host_workspace_root;
  std::wstring workspace_path;
  std::wstring temp_path;
  std::wstring appcontainer_sid;
  bool restricted_token = false;
  std::vector<std::pair<std::wstring, std::wstring>> exposed_environment;
  std::vector<std::wstring> tool_paths;
};

class SessionManager {
 public:
  SessionManager(runtime::IEnvironmentBackend& backend,
                 environment::AvailabilityReport availability);
  ~SessionManager();

  SessionManager(const SessionManager&) = delete;
  SessionManager& operator=(const SessionManager&) = delete;

  core::Result<v1::Session> Create(const v1::CreateSessionRequest& request);
  core::Status Close(std::uint64_t session_id, const std::string& reason);
  core::Result<v1::Session> Get(std::uint64_t session_id) const;
  std::vector<v1::Session> List() const;
  core::Result<ShellContext> DescribeForShell(std::uint64_t session_id) const;
  void Shutdown();

 private:
  struct Entry {
    v1::Session record;
    runtime::EnvironmentFacts facts;
    std::wstring host_workspace_root;
    std::unique_ptr<runtime::IEnvironment> environment;
  };

  mutable std::mutex mutex_;
  runtime::IEnvironmentBackend& backend_;
  environment::AvailabilityReport availability_;
  std::map<std::uint64_t, Entry> sessions_;
  std::uint64_t next_id_ = 1;
};

}  // namespace rvm::server