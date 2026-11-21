#pragma once

#include <memory>

#include "runtime/backend.h"

namespace rvm::hyperv {

std::unique_ptr<runtime::IEnvironmentBackend> CreateHcsBackend();

}  // namespace rvm::hyperv