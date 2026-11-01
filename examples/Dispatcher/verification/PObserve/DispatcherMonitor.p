/* @link {"bind":[{"id":"dispatcher-observe::admit","source":{"symbol":"M:DispatcherExample.Dispatcher.TryAdmit(System.Int32)"}}]} */
event eAdmit: int;
/* @link {"bind":[{"id":"dispatcher-observe::start","source":{"symbol":"M:DispatcherExample.Dispatcher.TryStart(System.Int32)"}}]} */
event eStart: int;
/* @link {"bind":[{"id":"dispatcher-observe::success","source":{"symbol":"M:DispatcherExample.Dispatcher.Complete(System.Int32,System.Boolean)"}}]} */
event eSuccess: int;
/* @link {"bind":[{"id":"dispatcher-observe::failure","source":{"symbol":"M:DispatcherExample.Dispatcher.Complete(System.Int32,System.Boolean)"}}]} */
event eFailure: int;
/* @link {"bind":[{"id":"dispatcher-observe::cancel","source":{"symbol":"M:DispatcherExample.Dispatcher.Cancel(System.Int32)"}}]} */
event eCancel: int;
/* @link {"bind":[{"id":"dispatcher-observe::close","source":{"symbol":"M:DispatcherExample.Dispatcher.Close"}}]} */
event eClose;

// A single serial owner emits only successful operations for one dispatcher.
// The monitor checks legal transitions, not C# execution or log completeness.
spec DispatcherMonitor observes eAdmit, eStart, eSuccess, eFailure, eCancel, eClose {
    var states: map[int, int];
    var attempts: map[int, int];
    var cancelled: map[int, bool];
    var outstanding: int;
    var running: int;
    var closing: bool;
    start state Ready {
        on eAdmit do (job: int) {
            assert job >= 0 && job < 3 && !closing && outstanding < 2, "admission-guard";
            assert !(job in states), "one-shot-admission";
            states[job] = 1;
            attempts[job] = 0;
            cancelled[job] = false;
            outstanding = outstanding + 1;
            Check();
        }
        on eStart do (job: int) {
            assert job in states && states[job] == 1, "queued-start";
            states[job] = 2;
            attempts[job] = attempts[job] + 1;
            running = running + 1;
            Check();
        }
        on eSuccess do (job: int) { Complete(job, true); }
        on eFailure do (job: int) { Complete(job, false); }
        on eCancel do (job: int) {
            assert job in states && (states[job] == 1 || states[job] == 2) && !cancelled[job], "live-cancel";
            cancelled[job] = true;
            if (states[job] == 1) { states[job] = 4; outstanding = outstanding - 1; }
            Check();
        }
        on eClose do { closing = true; Check(); }
    }
    fun Complete(job: int, succeeded: bool) {
        assert job in states && states[job] == 2, "one-shot-completion";
        running = running - 1;
        if (cancelled[job]) { states[job] = 4; }
        else if (succeeded) { states[job] = 3; }
        else if (attempts[job] == 2) { states[job] = 5; }
        else { states[job] = 1; }
        if (states[job] != 1) { outstanding = outstanding - 1; }
        Check();
    }
    fun Check() {
        var job: int;
        var live: int;
        var active: int;
        foreach (job in keys(states)) {
            if (states[job] == 1 || states[job] == 2) { live = live + 1; }
            if (states[job] == 2) { active = active + 1; }
            assert attempts[job] >= 0 && attempts[job] <= 2, "attempt-bound";
            if (states[job] == 1) { assert !cancelled[job] && attempts[job] < 2, "legal-retry"; }
        }
        assert outstanding == live && running == active, "conservation";
        assert running >= 0 && running <= outstanding && outstanding <= 2, "capacity";
    }
}
