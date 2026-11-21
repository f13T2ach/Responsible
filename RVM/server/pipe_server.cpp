#include "server/pipe_server.h"

#include <sddl.h>

#include <cstddef>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "channel/frame_io.h"
#include "channel/protobuf_codec.h"
#include "core/error.h"
#include "core/log.h"
#include "server/rpc_dispatcher.h"
#include "server/session_manager.h"

namespace rvm::server {
namespace {

std::string ErrorDetail(DWORD code) {
  return "win32 error " + std::to_string(code);
}

bool IsValidPipeName(const std::wstring& name) {
  if (name.empty() || name.size() > 200) return false;
  for (const wchar_t ch : name) {
    if (ch < 0x20) return false;
    if (ch == L'\\' || ch == L'/') return false;
  }
  return true;
}

class PipeSecurity {
 public:
  PipeSecurity() = default;
  ~PipeSecurity() {
    if (descriptor_ != nullptr) LocalFree(descriptor_);
  }

  PipeSecurity(const PipeSecurity&) = delete;
  PipeSecurity& operator=(const PipeSecurity&) = delete;

  core::Status Build() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to open the process token", false,
          ErrorDetail(GetLastError())));
    }

    DWORD required = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &required);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || required == 0) {
      const DWORD error = GetLastError();
      CloseHandle(token);
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to query the process token", false,
          ErrorDetail(error)));
    }

    std::vector<std::uint8_t> buffer(required);
    if (!GetTokenInformation(token, TokenUser, buffer.data(), required, &required)) {
      const DWORD error = GetLastError();
      CloseHandle(token);
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to read the process token", false,
          ErrorDetail(error)));
    }
    CloseHandle(token);

    const auto* user = reinterpret_cast<const TOKEN_USER*>(buffer.data());
    LPWSTR sid_string = nullptr;
    if (!ConvertSidToStringSidW(user->User.Sid, &sid_string)) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to format the user SID", false,
          ErrorDetail(GetLastError())));
    }

    std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GA;;;";
    sddl.append(sid_string);
    sddl.append(L")");
    LocalFree(sid_string);

    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.c_str(), SDDL_REVISION_1, &descriptor_, nullptr)) {
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to build the pipe security descriptor", false,
          ErrorDetail(GetLastError())));
    }

    attributes_.nLength = sizeof(attributes_);
    attributes_.lpSecurityDescriptor = descriptor_;
    attributes_.bInheritHandle = FALSE;
    return core::Status::Ok();
  }

  SECURITY_ATTRIBUTES* attributes() { return &attributes_; }

 private:
  PSECURITY_DESCRIPTOR descriptor_ = nullptr;
  SECURITY_ATTRIBUTES attributes_{};
};

}  // namespace

PipeServer::PipeServer(core::Engine& engine, PipeServerOptions options,
                       SessionManager* sessions)
    : engine_(engine), sessions_(sessions), options_(std::move(options)) {}

PipeServer::~PipeServer() { Stop(); }

core::Status PipeServer::Run() {
  if (!IsValidPipeName(options_.pipe_name)) {
    return core::Status::Failure(
        core::InvalidArgument("pipe name must be non-empty without separators"));
  }
  if (options_.token.empty()) {
    return core::Status::Failure(
        core::InvalidArgument("engine token must not be empty"));
  }

  PipeSecurity security;
  core::Status status = security.Build();
  if (!status.ok()) return status;

  thread_.store(GetCurrentThread());
  const std::wstring full_name = L"\\\\.\\pipe\\" + options_.pipe_name;

  struct ClientSlot {
    std::thread thread;
    std::shared_ptr<std::atomic<bool>> done;
  };
  std::vector<ClientSlot> clients;
  bool first_instance = true;

  while (!stop_.load()) {
    for (auto it = clients.begin(); it != clients.end();) {
      if (it->done->load()) {
        it->thread.join();
        it = clients.erase(it);
      } else {
        ++it;
      }
    }

    const DWORD open_mode =
        PIPE_ACCESS_DUPLEX | (first_instance ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
    HANDLE pipe = CreateNamedPipeW(
        full_name.c_str(), open_mode,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        PIPE_UNLIMITED_INSTANCES, 256 * 1024, 256 * 1024, 0, security.attributes());
    if (pipe == INVALID_HANDLE_VALUE) {
      if (stop_.load()) break;
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to create the engine pipe", false,
          ErrorDetail(GetLastError())));
    }
    first_instance = false;
    if (stop_.load()) {
      CloseHandle(pipe);
      break;
    }

    const BOOL connected = ConnectNamedPipe(pipe, nullptr);
    const DWORD connect_error = connected ? ERROR_SUCCESS : GetLastError();
    if (stop_.load()) {
      CloseHandle(pipe);
      break;
    }
    if (!connected && connect_error != ERROR_PIPE_CONNECTED) {
      CloseHandle(pipe);
      if (connect_error == ERROR_OPERATION_ABORTED) break;
      return core::Status::Failure(core::Error(
          core::ErrorCode::kBackendFailed, "failed to accept a pipe client", false,
          ErrorDetail(connect_error)));
    }

    ULONG client_pid = 0;
    if (!GetNamedPipeClientProcessId(pipe, &client_pid)) client_pid = 0;

    core::LogInfo("pipe", "client connected");
    auto done = std::make_shared<std::atomic<bool>>(false);
    clients.push_back(ClientSlot{
        std::thread([this, pipe, client_pid, done] {
          const HANDLE self = OpenThread(THREAD_TERMINATE, FALSE, GetCurrentThreadId());
          {
            std::lock_guard<std::mutex> lock(client_mutex_);
            if (self != nullptr) client_threads_.push_back(self);
          }
          const core::Status status =
              HandleClient(pipe, static_cast<std::uint32_t>(client_pid));
          FlushFileBuffers(pipe);
          DisconnectNamedPipe(pipe);
          {
            std::lock_guard<std::mutex> lock(client_mutex_);
            if (self != nullptr) {
              std::erase(client_threads_, self);
              CloseHandle(self);
            }
          }
          CloseHandle(pipe);
          core::LogInfo("pipe", "client disconnected");
          if (!status.ok() && !stop_.load()) {
            core::LogWarn("pipe", status.error().message);
          }
          done->store(true);
        }),
        std::move(done)});
  }

  for (ClientSlot& slot : clients) slot.thread.join();
  if (sessions_ != nullptr) sessions_->Shutdown();
  return core::Status::Ok();
}

void PipeServer::Stop() {
  stop_.store(true);
  WakeAccept();
  HANDLE thread = thread_.exchange(nullptr);
  if (thread != nullptr) CancelSynchronousIo(thread);
  std::lock_guard<std::mutex> lock(client_mutex_);
  for (const HANDLE client : client_threads_) CancelSynchronousIo(client);
}

// A client-requested shutdown leaves Run blocked in ConnectNamedPipe. A short
// self-connection completes that pending accept (the loop then sees stop_ and
// closes the instance); with no pending accept it fails with ERROR_PIPE_BUSY
// and the loop's post-create stop check ends it. Closing the instance from this
// thread instead would block while the accept I/O is still in flight.
void PipeServer::WakeAccept() {
  const std::wstring path = L"\\\\.\\pipe\\" + options_.pipe_name;
  const HANDLE wake = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                  OPEN_EXISTING, 0, nullptr);
  if (wake != INVALID_HANDLE_VALUE) CloseHandle(wake);
}

bool PipeServer::ClientAlive(HANDLE pipe) const {
  DWORD available = 0;
  return PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) != FALSE;
}

core::Status PipeServer::HandleClient(HANDLE pipe, std::uint32_t peer_pid) {
  RpcDispatcher dispatcher(engine_, options_.token, sessions_);
  const rvm::server::StreamSink stream =
      [pipe](const rvm::v1::StreamFrame& frame) -> core::Status {
    core::Result<std::string> bytes = rvm::channel::SerializeMessage(frame);
    if (!bytes.ok()) return core::Status::Failure(bytes.error());
    const std::vector<std::uint8_t> payload(bytes.value().begin(), bytes.value().end());
    return rvm::channel::WriteFrame(pipe, rvm::channel::FrameType::kStream,
                                    frame.request_id(), payload);
  };
  const std::function<bool()> peer_alive = [this, pipe] {
    return !stop_.load() && ClientAlive(pipe);
  };

  while (!stop_.load()) {
    rvm::channel::FrameHeader header;
    std::vector<std::uint8_t> payload;
    core::Status status = rvm::channel::ReadFrame(pipe, header, payload);
    if (!status.ok()) {
      if (status.error().code == core::ErrorCode::kUnavailable) return core::Status::Ok();
      return status;
    }
    if (header.type != rvm::channel::FrameType::kRpcRequest) {
      return core::Status::Failure(
          core::Error(core::ErrorCode::kProtocolError, "expected an RPC request frame"));
    }

    core::Result<DispatchResult> result = dispatcher.Handle(payload, peer_pid, stream, peer_alive);
    if (!result.ok()) return core::Status::Failure(result.error());

    if (!result.value().payload.empty()) {
      status = rvm::channel::WriteFrame(pipe, rvm::channel::FrameType::kRpcResponse,
                                        header.request_id, result.value().payload);
      if (!status.ok()) return status;
    }
    if (result.value().close) {
      stop_.store(true);
      WakeAccept();
      return core::Status::Ok();
    }
  }
  return core::Status::Ok();
}

}  // namespace rvm::server