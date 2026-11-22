#pragma once

#include <atomic>
#include <cstdint>
#include <set>
#include <string>
#include <thread>

namespace rvm::server {

// Samples the TCP/UDP owner tables for one command's process tree and appends
// every newly observed outbound endpoint to <archive>\network.log. This is a
// real observation of the host tables, not a reconstruction of tool output;
// very short-lived connections can still fall between samples.
class NetworkAudit {
 public:
  explicit NetworkAudit(std::wstring log_path);
  ~NetworkAudit();

  NetworkAudit(const NetworkAudit&) = delete;
  NetworkAudit& operator=(const NetworkAudit&) = delete;

  void Start(std::uint64_t session_id, std::uint32_t root_pid);
  void Stop();

 private:
  void Run();
  void WriteLine(const std::string& line);

  std::wstring log_path_;
  std::uint64_t session_id_ = 0;
  std::uint32_t root_pid_ = 0;
  bool enabled_ = false;
  std::atomic<bool> stop_{false};
  std::thread worker_;
  std::set<std::string> seen_;
};

}  // namespace rvm::server