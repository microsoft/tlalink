event eSet: int;
event eCheck: int;
machine HelperSend {
    var value: int;
    start state Ready {
        entry { send this, eSet, 1; }
        on eSet do (requested: int) { Apply(requested); }
        on eCheck do (expected: int) {
            assert value == expected, "helper-event-state-persistence";
        }
    }
    fun Apply(requested: int) { Publish(requested); }
    fun Publish(requested: int) {
        value = requested;
        send this, eCheck, requested;
    }
}
