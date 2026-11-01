package dispatcher.sample;

import generatedOutput.pobserve.PEvents;
import pobserve.commons.Parser;
import pobserve.commons.PObserveEvent;
import pobserve.runtime.events.PEvent;
import java.util.stream.Stream;

public final class DispatcherParser implements Parser<PEvent<?>> {
    @Override
    public Stream<PObserveEvent<PEvent<?>>> apply(Object input) {
        if (!(input instanceof String)) throw new IllegalArgumentException("Expected a text log record.");
        String line = (String) input;
        String[] fields = line.split("\t", -1);
        if (fields.length != 4 ||
            !(fields[0].equals("dispatcher-0") || fields[0].equals("dispatcher-1")))
            throw new IllegalArgumentException("Invalid dispatcher log shape or partition.");
        long time = Long.parseLong(fields[1]);
        long job = Long.parseLong(fields[3]);
        if (time < 0 || job < 0 || job > 2)
            throw new IllegalArgumentException("Invalid timestamp or job.");
        PEvent<?> event;
        switch (fields[2]) {
            case "admit": event = new PEvents.eAdmit(job); break;
            case "start": event = new PEvents.eStart(job); break;
            case "success": event = new PEvents.eSuccess(job); break;
            case "failure": event = new PEvents.eFailure(job); break;
            case "cancel": event = new PEvents.eCancel(job); break;
            case "close":
                if (job != 0) throw new IllegalArgumentException("Close requires the unused job field to be zero.");
                event = new PEvents.eClose(); break;
            default: throw new IllegalArgumentException("Unknown dispatcher operation.");
        }
        return Stream.of(new PObserveEvent<>(fields[0], time, event, line));
    }
}
