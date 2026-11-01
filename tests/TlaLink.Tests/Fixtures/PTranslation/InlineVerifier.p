event eSet;
event eAudit;
machine InlineVerifier {
    var value: int;
    var phase: int;
    start state Ready {
        entry { send this, eSet; }
        on eSet do {
            value = 1;
            phase = 1;
            send this, eAudit;
        }
        on eAudit do { }
    }
}
init-condition forall (cell: InlineVerifier) :: cell.value == 0 && cell.phase == 0;
Theorem InlineSafety {
    invariant initialized: forall (cell: InlineVerifier) ::
        (cell.phase == 0 && cell.value == 0) || (cell.phase == 1 && cell.value == 1);
}
Proof InlineStateSafety {
    prove InlineSafety;
    prove default;
}
