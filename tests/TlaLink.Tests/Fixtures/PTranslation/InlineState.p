event eSet: int;
event eCheck: int;
machine InlineState {
    var value: int;
    var phase: int;
    start state Ready {
        entry { send this, eSet, 1; }
        on eSet do (requested: int) {
            value = requested;
            phase = 1;
            send this, eCheck, requested;
        }
        on eCheck do (expected: int) {
            assert phase == 1 && value == expected, "inline-event-state-persistence";
        }
    }
}
