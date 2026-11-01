event eSet: int;
event eCheck: int;
spec TranslationMonitor observes eSet, eCheck {
    var value: int;
    start state Ready {
        on eSet do (requested: int) { value = requested; }
        on eCheck do (expected: int) {
            assert value == expected, "monitor-state-persistence";
        }
    }
}
