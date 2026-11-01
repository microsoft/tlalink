machine HelperCall {
    var value: int;
    start state Ready {
        entry { SetValue(); }
    }
    fun SetValue() { value = 1; }
}
