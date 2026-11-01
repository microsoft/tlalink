// Reachability control: admission must not remain permanently absent.
Theorem NoAdmission {
    invariant never_admitted: forall (d: VerifiedDispatcher) :: d.outstanding == 0;
}
Proof AdmissionControl {
    prove NoAdmission using Safety;
}
