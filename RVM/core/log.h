#pragma once

#include <string_view>

namespace rvm::core {

enum class LogLevel {
  kDebug,
  kInfo,
  kWarn,
  kError,
};

void SetLogLevel(LogLevel level);
LogLevel GetLogLevel();

void Log(LogLevel level, std::string_view category, std::string_view message);

inline void LogDebug(std::string_view category, std::string_view message) {
  Log(LogLevel::kDebug, category, message);
}
inline void LogInfo(std::string_view category, std::string_view message) {
  Log(LogLevel::kInfo, category, message);
}
inline void LogWarn(std::string_view category, std::string_view message) {
  Log(LogLevel::kWarn, category, message);
}
inline void LogError(std::string_view category, std::string_view message) {
  Log(LogLevel::kError, category, message);
}

}  // namespace rvm::core