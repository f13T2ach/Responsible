#pragma once

#include <optional>
#include <utility>
#include <variant>

#include "core/error.h"

namespace rvm::core {

class Status {
 public:
  Status() = default;
  Status(Error error) : error_(std::move(error)) {}

  static Status Ok() { return Status(); }
  static Status Failure(Error error) { return Status(std::move(error)); }

  bool ok() const { return !error_.has_value(); }
  explicit operator bool() const { return ok(); }
  const Error& error() const { return *error_; }
  Error& error() { return *error_; }

 private:
  std::optional<Error> error_;
};

template <typename T>
class Result {
 public:
  Result(T value) : value_(std::move(value)) {}
  Result(Error error) : value_(std::move(error)) {}

  bool ok() const { return std::holds_alternative<T>(value_); }
  explicit operator bool() const { return ok(); }

  T& value() { return std::get<T>(value_); }
  const T& value() const { return std::get<T>(value_); }
  T Take() { return std::move(std::get<T>(value_)); }

  const Error& error() const { return std::get<Error>(value_); }
  Error& error() { return std::get<Error>(value_); }

 private:
  std::variant<T, Error> value_;
};

template <typename T>
Result<T> Ok(T value) {
  return Result<T>(std::move(value));
}

}  // namespace rvm::core