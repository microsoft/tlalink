event eCommand: (client: machine, job: int, kind: int, succeeded: bool);
event eReply: (accepted: bool, drained: bool);
event eRun: bool;

// Messages represent atomic lock acquisitions, not source I/O or worker
// completion machinery. Each client waits for its reply before sending again.
machine Dispatcher {
    var states: seq[int];
    var attempts: seq[int];
    var cancelled: seq[bool];
    var outstanding: int;
    var running: int;
    var closing: bool;
    var leak: bool;
    var early: bool;

    start state Ready {
        entry (control: (leak: bool, early: bool)) {
            var i: int;
            leak = control.leak;
            early = control.early;
            while (i < 3) {
                states += (i, 0);
                attempts += (i, 0);
                cancelled += (i, false);
                i = i + 1;
            }
            Check();
        }
        on eCommand do (command: (client: machine, job: int, kind: int, succeeded: bool)) {
            var accepted: bool;
            assert command.job >= 0 && command.job < 3, "valid-job";
            if (command.kind == 0) { accepted = TryAdmit(command.job); }
            else if (command.kind == 1) { accepted = TryStart(command.job); }
            else if (command.kind == 2) { accepted = Complete(command.job, command.succeeded); }
            else if (command.kind == 3) { accepted = Cancel(command.job); }
            else { assert command.kind == 4, "valid-command"; Close(); accepted = true; }
            Check();
            send command.client, eReply, (accepted = accepted, drained = closing && outstanding == 0);
        }
    }

    /* @link {"bind":[{"id":"dispatcher-p::admit","source":{"symbol":"M:DispatcherExample.Dispatcher.TryAdmit(System.Int32)"}}]} */
    fun TryAdmit(job: int): bool {
        if (closing || states[job] != 0 || outstanding == 2) { return false; }
        states[job] = 1;
        outstanding = outstanding + 1;
        return true;
    }

    /* @link {"bind":[{"id":"dispatcher-p::start","source":{"symbol":"M:DispatcherExample.Dispatcher.TryStart(System.Int32)"}}]} */
    fun TryStart(job: int): bool {
        if (states[job] != 1) { return false; }
        states[job] = 2;
        attempts[job] = attempts[job] + 1;
        running = running + 1;
        return true;
    }

    /* @link {"bind":[{"id":"dispatcher-p::complete","source":{"symbol":"M:DispatcherExample.Dispatcher.Complete(System.Int32,System.Boolean)"}}]} */
    fun Complete(job: int, succeeded: bool): bool {
        if (states[job] != 2) { return false; }
        running = running - 1;
        if (cancelled[job]) { states[job] = 4; }
        else if (succeeded) { states[job] = 3; }
        else if (attempts[job] == 2) { states[job] = 5; }
        else { states[job] = 1; }
        if (states[job] != 1 && !leak) { outstanding = outstanding - 1; }
        return true;
    }

    /* @link {"bind":[{"id":"dispatcher-p::cancel","source":{"symbol":"M:DispatcherExample.Dispatcher.Cancel(System.Int32)"}}]} */
    fun Cancel(job: int): bool {
        if (!(states[job] == 1 || states[job] == 2) || cancelled[job]) { return false; }
        cancelled[job] = true;
        if (states[job] == 1) { states[job] = 4; outstanding = outstanding - 1; }
        else if (early) { outstanding = outstanding - 1; }
        return true;
    }

    /* @link {"bind":[{"id":"dispatcher-p::close","source":{"symbol":"M:DispatcherExample.Dispatcher.Close"}}]} */
    fun Close() { closing = true; }

    fun Check() {
        var i: int;
        var live: int;
        var active: int;
        while (i < 3) {
            assert states[i] >= 0 && states[i] <= 5, "state-domain";
            assert attempts[i] >= 0 && attempts[i] <= 2, "retry-bound";
            if (states[i] == 1 || states[i] == 2) { live = live + 1; }
            if (states[i] == 2) { active = active + 1; assert attempts[i] > 0, "running-attempt"; }
            if (states[i] == 0) { assert attempts[i] == 0 && !cancelled[i], "new-state"; }
            if (states[i] == 1) { assert !cancelled[i] && attempts[i] < 2, "legal-retry"; }
            if (states[i] == 5) { assert attempts[i] == 2, "exhausted"; }
            i = i + 1;
        }
        assert outstanding == live, "outstanding-conservation";
        assert running == active, "running-conservation";
        assert 0 <= running && running <= outstanding && outstanding <= 2, "capacity";
    }
}

machine Client {
    var dispatcher: machine;
    var remaining: int;
    start state Issue {
        entry (target: machine) { dispatcher = target; remaining = 8; goto Send; }
    }
    state Send {
        entry {
            var kind: int;
            var job: int;
            if (remaining == 0) { raise halt; }
            job = choose(3);
            kind = choose(5);
            send dispatcher, eCommand, (client = this, job = job, kind = kind, succeeded = $);
        }
        on eReply do (reply: (accepted: bool, drained: bool)) {
            remaining = remaining - 1;
            goto Send;
        }
    }
}

machine Driver {
    var d: machine;
    var commands: seq[(job: int, kind: int, succeeded: bool, accepted: bool, drained: bool)];
    var cursor: int;
    var finite: bool;
    start state Await {
        on eRun do (bounded: bool) {
            finite = bounded;
            Plan(0, 0, true, true, false);
            Plan(1, 0, true, true, false);
            Plan(2, 0, true, false, false);
            Plan(0, 0, true, false, false);
            Plan(0, 1, true, true, false);
            Plan(0, 2, false, true, false);
            Plan(0, 1, true, true, false);
            Plan(0, 2, true, true, false);
            Plan(0, 2, true, false, false);
            Plan(1, 3, true, true, false);
            Plan(2, 0, true, true, false);
            Plan(2, 1, true, true, false);
            Plan(2, 3, true, true, false);
            Plan(0, 4, true, true, false);
            Plan(2, 2, true, true, true);
            Plan(0, 0, true, false, true);
            Plan(0, 0, true, true, false);
            Plan(0, 1, true, true, false);
            Plan(0, 2, false, true, false);
            Plan(0, 1, true, true, false);
            Plan(0, 2, false, true, false);
            Plan(0, 1, true, false, false);
            d = new Dispatcher((leak = false, early = false));
            goto Send;
        }
    }
    state Send {
        entry {
            if (cursor == 16) { d = new Dispatcher((leak = false, early = false)); }
            if (cursor == sizeof(commands)) {
                d = new Dispatcher((leak = false, early = false));
                if (finite) {
                    new RaceClient((target = d, role = 0));
                    new RaceClient((target = d, role = 1));
                    new RaceClient((target = d, role = 2));
                } else {
                    new Client(d);
                    new Client(d);
                    new Client(d);
                }
                raise halt;
            }
            send d, eCommand, (client = this, job = commands[cursor].job,
                kind = commands[cursor].kind, succeeded = commands[cursor].succeeded);
        }
        on eReply do (reply: (accepted: bool, drained: bool)) {
            assert reply.accepted == commands[cursor].accepted, "scripted-acceptance";
            assert reply.drained == commands[cursor].drained, "scripted-drain";
            cursor = cursor + 1;
            goto Send;
        }
    }
    fun Plan(job: int, kind: int, succeeded: bool, accepted: bool, drained: bool) {
        commands += (sizeof(commands), (job = job, kind = kind, succeeded = succeeded,
            accepted = accepted, drained = drained));
    }
}
machine RaceClient {
    var d: machine;
    var role: int;
    var step: int;
    start state Send {
        entry (setup: (target: machine, role: int)) {
            d = setup.target;
            role = setup.role;
            goto Issue;
        }
    }
    state Issue {
        entry {
            var kind: int;
            if (role == 0) { kind = 3; }
            else if (role == 1) {
                if (step == 0) { kind = 0; }
                else if (step == 1) { kind = 1; }
                else { kind = 2; }
            } else { kind = 4; }
            send d, eCommand, (client = this, job = 0, kind = kind, succeeded = true);
        }
        on eReply do (reply: (accepted: bool, drained: bool)) {
            step = step + 1;
            if (role != 1 || step == 3) { raise halt; }
            goto Issue;
        }
    }
}
machine RandomMain {
    start state Run {
        entry {
            var driver: machine;
            driver = new Driver();
            send driver, eRun, false;
        }
    }
}
machine FiniteMain {
    start state Run {
        entry {
            var driver: machine;
            driver = new Driver();
            send driver, eRun, true;
        }
    }
}
machine LeakDriver {
    var d: machine;
    start state Run {
        entry {
            d = new Dispatcher((leak = true, early = false));
            send d, eCommand, (client = this, job = 0, kind = 0, succeeded = true);
        }
        on eReply goto Start;
    }
    state Start {
        entry { send d, eCommand, (client = this, job = 0, kind = 1, succeeded = true); }
        on eReply goto Finish;
    }
    state Finish {
        entry { send d, eCommand, (client = this, job = 0, kind = 2, succeeded = true); }
        on eReply do { raise halt; }
    }
}
machine EarlyDriver {
    var d: machine;
    start state Run {
        entry {
            d = new Dispatcher((leak = false, early = true));
            send d, eCommand, (client = this, job = 0, kind = 0, succeeded = true);
        }
        on eReply goto Start;
    }
    state Start {
        entry { send d, eCommand, (client = this, job = 0, kind = 1, succeeded = true); }
        on eReply goto Cancel;
    }
    state Cancel {
        entry { send d, eCommand, (client = this, job = 0, kind = 3, succeeded = true); }
        on eReply do { raise halt; }
    }
}
test tcDispatcher [main = RandomMain]: { RandomMain, Driver, Client, RaceClient, Dispatcher };
test tcFiniteDispatcher [main = FiniteMain]: { FiniteMain, Driver, Client, RaceClient, Dispatcher };
test tcLeakedCredit [main = LeakDriver]: { LeakDriver, Dispatcher };
test tcEarlyRelease [main = EarlyDriver]: { EarlyDriver, Dispatcher };
