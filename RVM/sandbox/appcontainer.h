#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "core/result.h"

namespace rvm::sandbox {

struct AppContainerProfile {
  std::wstring name;
  std::wstring sid;
};

core::Result<AppContainerProfile> CreateProfile(std::wstring_view name);
core::Status DeleteProfile(std::wstring_view name);

// Stable capability SID derived from a name. Grants on tool directories use
// this SID so they survive engine restarts; only processes whose token lists
// the capability can use them.
core::Result<std::wstring> DeriveCapabilitySid(std::wstring_view name);

core::Status GrantDirectory(const std::wstring& sid, const std::wstring& path, bool write,
                            bool inherit);
// Read/execute only: used for user-exposed tool directories so a sandboxed
// process can launch them without gaining write access to the toolchain.
core::Status GrantDirectoryReadExecute(const std::wstring& sid, const std::wstring& path,
                                       bool inherit);
core::Status RevokeDirectory(const std::wstring& sid, const std::wstring& path, bool inherit);
core::Status GrantTraverseAncestors(const std::wstring& sid, const std::wstring& path,
                                    std::vector<std::wstring>* granted);

enum class Containment {
  // Full AppContainer token: DACL isolation, no host profile or host temp
  // access unless an explicit grant exists.
  kAppContainer,
  // Privilege-stripped restricted token inside a job object. Kept only as a
  // fallback for hosts whose WinFsp volume rejects AppContainer opens
  // (winfsp/winfsp#705); it does not provide DACL isolation.
  kRestrictedToken,
};

struct ProcessOptions {
  std::wstring command_line;
  std::wstring working_directory;
  std::wstring appcontainer_sid;
  // Capability SIDs added to the child token (S-1-15-3-*). Persistent tool
  // grants target these so a session only sees the tools the host selected.
  std::vector<std::wstring> capabilities;
  Containment containment = Containment::kAppContainer;
  std::uint32_t timeout_ms = 120000;
  std::uint64_t memory_limit_bytes = 0;
  std::uint32_t max_processes = 0;
  // Bytes written to the child's stdin before the channel closes.
  std::vector<std::uint8_t> stdin_data;
  // Environment for the child; empty inherits this process's environment.
  std::vector<std::pair<std::wstring, std::wstring>> environment;
  // Retained bytes per stream; bytes past the cap are dropped and flagged.
  // 0 keeps everything. The sink still observes only retained bytes.
  std::uint64_t max_output_bytes = 0;
  // Receives every retained chunk; invoked serially from the capture threads.
  std::function<void(bool stderr_stream, const std::uint8_t* data, std::size_t size)> sink;
  // Polled while the child runs; returning true terminates the job early.
  std::function<bool()> should_cancel;
};

struct ProcessResult {
  std::uint32_t exit_code = 0;
  std::uint32_t process_id = 0;
  bool timed_out = false;
  bool cancelled = false;
  bool output_truncated = false;
  bool error_truncated = false;
  std::string output;
  std::string error;
};

core::Result<ProcessResult> RunProcess(const ProcessOptions& options);

}  // namespace rvm::sandbox