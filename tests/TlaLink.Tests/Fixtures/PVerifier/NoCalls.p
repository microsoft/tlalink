event eSet;
machine NoCalls {
    var value: int;
    start state Ready {
        entry { value = 0; }
        on eSet do { value = 1; }
    }
}
