/* @link {"bind":[{"id":"dispatcher-pverifier::admit","source":{"symbol":"M:DispatcherExample.Dispatcher.TryAdmit(System.Int32)"}}]} */
event eAdmit: int;
/* @link {"bind":[{"id":"dispatcher-pverifier::start","source":{"symbol":"M:DispatcherExample.Dispatcher.TryStart(System.Int32)"}}]} */
event eStart: int;
/* @link {"bind":[{"id":"dispatcher-pverifier::complete","source":{"symbol":"M:DispatcherExample.Dispatcher.Complete(System.Int32,System.Boolean)"}}]} */
event eFinish: (job: int, succeeded: bool);
/* @link {"bind":[{"id":"dispatcher-pverifier::cancel","source":{"symbol":"M:DispatcherExample.Dispatcher.Cancel(System.Int32)"}}]} */
event eCancel: int;
/* @link {"bind":[{"id":"dispatcher-pverifier::close","source":{"symbol":"M:DispatcherExample.Dispatcher.Close"}}]} */
event eClose;

// Runtime helper calls are excluded because the pinned PVerifier generator
// can overwrite their effects. Inline handlers retain the same lock-operation
// abstraction. Seed requests exercise the otherwise empty initial buffer.
machine VerifiedDispatcher {
    var states: map[int, int];
    var attempts: map[int, int];
    var cancelled: map[int, bool];
    var outstanding: int;
    var running: int;
    var closing: bool;
    start state Ready {
        entry {
            send this, eAdmit, 0;
            send this, eStart, 0;
            send this, eFinish, (job = 0, succeeded = false);
            send this, eStart, 0;
            send this, eFinish, (job = 0, succeeded = true);
            send this, eCancel, 0;
            send this, eAdmit, 1;
            send this, eStart, 1;
            send this, eFinish, (job = 1, succeeded = false);
            send this, eStart, 1;
            send this, eFinish, (job = 1, succeeded = true);
            send this, eCancel, 1;
            send this, eAdmit, 2;
            send this, eStart, 2;
            send this, eFinish, (job = 2, succeeded = false);
            send this, eStart, 2;
            send this, eFinish, (job = 2, succeeded = true);
            send this, eCancel, 2;
            send this, eClose;
        }
        on eAdmit do (job: int) {
            if (job >= 0 && job < 3 && !closing && states[job] == 0 && outstanding < 2) {
                states[job] = 1;
                outstanding = outstanding + 1;
            }
        }
        on eStart do (job: int) {
            if (job >= 0 && job < 3 && states[job] == 1) {
                states[job] = 2;
                attempts[job] = attempts[job] + 1;
                running = running + 1;
            }
        }
        on eFinish do (result: (job: int, succeeded: bool)) {
            if (result.job >= 0 && result.job < 3 && states[result.job] == 2) {
                running = running - 1;
                if (cancelled[result.job]) { states[result.job] = 4; outstanding = outstanding - 1; }
                else if (result.succeeded) { states[result.job] = 3; outstanding = outstanding - 1; }
                else if (attempts[result.job] == 2) { states[result.job] = 5; outstanding = outstanding - 1; }
                else { states[result.job] = 1; }
            }
        }
        on eCancel do (job: int) {
            if (job >= 0 && job < 3 && (states[job] == 1 || states[job] == 2) && !cancelled[job]) {
                cancelled[job] = true;
                if (states[job] == 1) { states[job] = 4; outstanding = outstanding - 1; }
            }
        }
        on eClose do { closing = true; }
    }
}
init-condition forall (d: VerifiedDispatcher) ::
    !d.closing && d.outstanding == 0 && d.running == 0 &&
    forall (j: int) :: (j in d.states <==> 0 <= j && j < 3) &&
                      (j in d.attempts <==> 0 <= j && j < 3) &&
                      (j in d.cancelled <==> 0 <= j && j < 3) &&
                      (0 <= j && j < 3 ==> d.states[j] == 0 && d.attempts[j] == 0 && !d.cancelled[j]);

pure Live(d: VerifiedDispatcher, j: int): bool = d.states[j] == 1 || d.states[j] == 2;
pure Active(d: VerifiedDispatcher, j: int): bool = d.states[j] == 2;
pure None(a: bool, b: bool, c: bool): bool = !a && !b && !c;
pure One(a: bool, b: bool, c: bool): bool =
    (a && !b && !c) || (!a && b && !c) || (!a && !b && c);
pure Two(a: bool, b: bool, c: bool): bool =
    (a && b && !c) || (a && !b && c) || (!a && b && c);

Theorem Safety {
    invariant domains: forall (d: VerifiedDispatcher, j: int) ::
        (j in d.states <==> 0 <= j && j < 3) &&
        (j in d.attempts <==> 0 <= j && j < 3) &&
        (j in d.cancelled <==> 0 <= j && j < 3);
    invariant capacity: forall (d: VerifiedDispatcher) ::
        0 <= d.running && d.running <= d.outstanding && d.outstanding <= 2;
    invariant outstanding_count: forall (d: VerifiedDispatcher) ::
        (d.outstanding == 0 <==> None(Live(d, 0), Live(d, 1), Live(d, 2))) &&
        (d.outstanding == 1 <==> One(Live(d, 0), Live(d, 1), Live(d, 2))) &&
        (d.outstanding == 2 <==> Two(Live(d, 0), Live(d, 1), Live(d, 2)));
    invariant running_count: forall (d: VerifiedDispatcher) ::
        (d.running == 0 <==> None(Active(d, 0), Active(d, 1), Active(d, 2))) &&
        (d.running == 1 <==> One(Active(d, 0), Active(d, 1), Active(d, 2))) &&
        (d.running == 2 <==> Two(Active(d, 0), Active(d, 1), Active(d, 2)));
    invariant job_state: forall (d: VerifiedDispatcher, j: int) ::
        0 <= j && j < 3 ==>
          0 <= d.states[j] && d.states[j] <= 5 &&
          0 <= d.attempts[j] && d.attempts[j] <= 2 &&
          (d.states[j] == 0 ==> d.attempts[j] == 0 && !d.cancelled[j]) &&
          (d.states[j] == 1 ==> !d.cancelled[j] && d.attempts[j] < 2) &&
          (d.states[j] == 2 ==> d.attempts[j] > 0) &&
          (d.states[j] == 5 ==> d.attempts[j] == 2);
}
Proof DispatcherSafety {
    prove Safety;
    prove default;
}
