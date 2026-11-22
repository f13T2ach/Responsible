#include "server/network_audit.h"

#include <winsock2.h>
#include <windows.h>

#include <iphlpapi.h>
#include <tlhelp32.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <set>
#include <string>
#include <type_traits>
#include <utility>
#include <vector>

namespace rvm::server {
namespace {

std::string Timestamp() {
  SYSTEMTIME utc{};
  GetSystemTime(&utc);
  char buffer[40] = {};
  std::snprintf(buffer, sizeof(buffer), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
                utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond,
                utc.wMilliseconds);
  return buffer;
}

std::set<DWORD> ProcessTree(DWORD root) {
  std::set<DWORD> tree;
  if (root == 0) return tree;
  tree.insert(root);
  const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if (snapshot == INVALID_HANDLE_VALUE) return tree;

  std::vector<std::pair<DWORD, DWORD>> parents;
  PROCESSENTRY32W entry{};
  entry.dwSize = sizeof(entry);
  if (Process32FirstW(snapshot, &entry)) {
    do {
      parents.emplace_back(entry.th32ProcessID, entry.th32ParentProcessID);
    } while (Process32NextW(snapshot, &entry));
  }
  CloseHandle(snapshot);

  // Snapshot order is not guaranteed to be parent-before-child; repeat until
  // no new descendants are discovered.
  bool added = true;
  while (added) {
    added = false;
    for (const auto& [pid, parent] : parents) {
      if (tree.count(pid) != 0) continue;
      if (tree.count(parent) == 0) continue;
      tree.insert(pid);
      added = true;
    }
  }
  return tree;
}

std::string Endpoint(DWORD address, DWORD port) {
  const DWORD host = ntohl(address);
  char buffer[48] = {};
  std::snprintf(buffer, sizeof(buffer), "%u.%u.%u.%u:%u", static_cast<unsigned>((host >> 24) & 0xff),
                static_cast<unsigned>((host >> 16) & 0xff),
                static_cast<unsigned>((host >> 8) & 0xff), static_cast<unsigned>(host & 0xff),
                static_cast<unsigned>(ntohs(static_cast<u_short>(port))));
  return buffer;
}

template <typename Table>
std::vector<BYTE> QueryTable(ULONG family, TCP_TABLE_CLASS tcp_class, Table** table) {
  DWORD size = 0;
  DWORD status = GetExtendedTcpTable(nullptr, &size, FALSE, family, tcp_class, 0);
  if (status != ERROR_INSUFFICIENT_BUFFER || size == 0) return {};
  std::vector<BYTE> buffer(size);
  status = GetExtendedTcpTable(buffer.data(), &size, FALSE, family, tcp_class, 0);
  if (status != NO_ERROR) return {};
  *table = reinterpret_cast<Table*>(buffer.data());
  return buffer;
}

template <typename Row>
void AddTcpRows(const std::set<DWORD>& tree, const std::vector<BYTE>& buffer, bool v6,
                std::set<std::string>* current, std::vector<std::string>* fresh) {
  if (buffer.empty()) return;
  const DWORD count = *reinterpret_cast<const DWORD*>(buffer.data());
  const auto* rows = reinterpret_cast<const Row*>(buffer.data() + sizeof(DWORD));
  for (DWORD i = 0; i < count; ++i) {
    const Row& row = rows[i];
    if (tree.count(row.dwOwningPid) == 0) continue;
    if (row.dwRemotePort == 0) continue;
    char remote[64] = {};
    if constexpr (std::is_same_v<Row, MIB_TCP6ROW_OWNER_PID>) {
      const auto* address = reinterpret_cast<const BYTE*>(row.ucRemoteAddr);
      std::snprintf(remote, sizeof(remote),
                    "[%02x%02x:%02x%02x:%02x%02x:%02x%02x:%02x%02x:%02x%02x:%02x%02x:%02x%02x]:%u",
                    address[0], address[1], address[2], address[3], address[4], address[5],
                    address[6], address[7], address[8], address[9], address[10], address[11],
                    address[12], address[13], address[14], address[15],
                    ntohs(static_cast<u_short>(row.dwRemotePort)));
    } else {
      const std::string endpoint = Endpoint(row.dwRemoteAddr, row.dwRemotePort);
      std::snprintf(remote, sizeof(remote), "%s", endpoint.c_str());
    }
    const std::string key = std::string(v6 ? "tcp6|" : "tcp|") + std::to_string(row.dwOwningPid) +
                            "|" + remote + "|" + std::to_string(row.dwState);
    current->insert(key);
    if (fresh->size() < 256) fresh->push_back(key);
  }
}

}  // namespace

NetworkAudit::NetworkAudit(std::wstring log_path) : log_path_(std::move(log_path)) {
  enabled_ = !log_path_.empty();
}

NetworkAudit::~NetworkAudit() { Stop(); }

void NetworkAudit::Start(std::uint64_t session_id, std::uint32_t root_pid) {
  if (!enabled_ || root_pid == 0 || worker_.joinable()) return;
  session_id_ = session_id;
  root_pid_ = root_pid;
  stop_.store(false);
  seen_.clear();
  WriteLine(std::string("{\"at\":\"") + Timestamp() + "\",\"session\":" +
            std::to_string(session_id_) + ",\"event\":\"network-session\",\"mode\":\"outbound-only\"," +
            "\"port_mask\":\"host-loopback,inbound\"}");
  worker_ = std::thread([this] { Run(); });
}

void NetworkAudit::Stop() {
  if (!worker_.joinable()) return;
  stop_.store(true);
  worker_.join();
}

void NetworkAudit::Run() {
  while (!stop_.load()) {
    const std::set<DWORD> tree = ProcessTree(root_pid_);
    std::set<std::string> current;
    std::vector<std::string> fresh;

    MIB_TCPTABLE_OWNER_PID* tcp4 = nullptr;
    auto tcp4_buffer = QueryTable(AF_INET, TCP_TABLE_OWNER_PID_ALL, &tcp4);
    AddTcpRows<MIB_TCPROW_OWNER_PID>(tree, tcp4_buffer, false, &current, &fresh);

    MIB_TCP6TABLE_OWNER_PID* tcp6 = nullptr;
    auto tcp6_buffer = QueryTable(AF_INET6, TCP_TABLE_OWNER_PID_ALL, &tcp6);
    AddTcpRows<MIB_TCP6ROW_OWNER_PID>(tree, tcp6_buffer, true, &current, &fresh);

    for (const std::string& key : fresh) {
      if (seen_.count(key) != 0) continue;
      const auto first = key.find('|');
      const auto second = key.find('|', first + 1);
      if (first == std::string::npos || second == std::string::npos) continue;
      const std::string protocol = key.substr(0, first);
      const std::string pid = key.substr(first + 1, second - first - 1);
      std::string remote = key.substr(second + 1);
      const auto third = remote.rfind('|');
      const std::string state = third == std::string::npos ? "0" : remote.substr(third + 1);
      if (third != std::string::npos) remote.resize(third);
      WriteLine(std::string("{\"at\":\"") + Timestamp() + "\",\"session\":" +
                std::to_string(session_id_) + ",\"event\":\"connect\",\"protocol\":\"" +
                protocol + "\",\"pid\":" + pid + ",\"remote\":\"" + remote +
                "\",\"state\":" + state + "}");
    }
    seen_ = std::move(current);

    for (int i = 0; i < 5 && !stop_.load(); ++i) {
      std::this_thread::sleep_for(std::chrono::milliseconds(60));
    }
  }
}

void NetworkAudit::WriteLine(const std::string& line) {
  if (!enabled_) return;
  std::ofstream file(std::filesystem::path(log_path_), std::ios::app | std::ios::binary);
  if (file) file << line << '\n';
}

}  // namespace rvm::server