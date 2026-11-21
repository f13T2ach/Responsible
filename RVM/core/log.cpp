#include "core/log.h"

#include <chrono>
#include <ctime>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>

namespace rvm::core {
namespace {

LogLevel g_level = LogLevel::kInfo;
std::mutex g_log_mutex;

std::string_view LevelName(LogLevel level) {
  switch (level) {
    case LogLevel::kDebug:
      return "DEBUG";
    case LogLevel::kInfo:
      return "INFO";
    case LogLevel::kWarn:
      return "WARN";
    case LogLevel::kError:
      return "ERROR";
  }
  return "INFO";
}

std::string UtcTimestamp() {
  const auto now = std::chrono::system_clock::now();
  const std::time_t time = std::chrono::system_clock::to_time_t(now);
  std::tm utc{};
#ifdef _WIN32
  gmtime_s(&utc, &time);
#else
  gmtime_r(&time, &utc);
#endif
  std::ostringstream stream;
  stream << std::put_time(&utc, "%Y-%m-%dT%H:%M:%SZ");
  return stream.str();
}

}  // namespace

void SetLogLevel(LogLevel level) { g_level = level; }

LogLevel GetLogLevel() { return g_level; }

void Log(LogLevel level, std::string_view category, std::string_view message) {
  if (static_cast<int>(level) < static_cast<int>(g_level)) return;
  std::lock_guard<std::mutex> lock(g_log_mutex);
  std::cerr << UtcTimestamp() << ' ' << LevelName(level) << ' ' << category << ": " << message
            << '\n';
  std::cerr.flush();
}

}  // namespace rvm::core