#pragma once

#include <initializer_list>
#include <map>
#include <utility>

#include "core/error.h"
#include "core/result.h"

namespace rvm::core {

template <typename State, typename Event>
class StateMachine {
 public:
  struct Transition {
    State from;
    Event event;
    State to;
  };

  StateMachine(State initial, std::initializer_list<Transition> transitions) : state_(initial) {
    for (const Transition& transition : transitions) {
      table_.emplace(Key{transition.from, transition.event}, transition.to);
    }
  }

  State state() const { return state_; }

  bool Can(Event event) const { return table_.find(Key{state_, event}) != table_.end(); }

  Status Apply(Event event) {
    const auto it = table_.find(Key{state_, event});
    if (it == table_.end()) {
      return Status::Failure(Error(ErrorCode::kInvalidState,
                                   "state transition is not allowed in the current state"));
    }
    state_ = it->second;
    return Status::Ok();
  }

 private:
  struct Key {
    State from;
    Event event;

    bool operator<(const Key& other) const {
      if (from != other.from) return from < other.from;
      return event < other.event;
    }
  };

  State state_;
  std::map<Key, State> table_;
};

}  // namespace rvm::core