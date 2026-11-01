package translation.controls;

import translation.controls.generated.PEvents;
import pobserve.commons.Parser;
import pobserve.commons.PObserveEvent;
import pobserve.runtime.events.PEvent;
import java.util.stream.Stream;

public final class TranslationParser implements Parser<PEvent<?>> {
    public Stream<PObserveEvent<PEvent<?>>> apply(Object input) {
        if (!(input instanceof String)) throw new IllegalArgumentException("Expected text.");
        String[] fields = ((String) input).split("\t", -1);
        if (fields.length != 4 || !fields[0].equals("cell"))
            throw new IllegalArgumentException("Invalid control record.");
        long time = Long.parseLong(fields[1]);
        long value = Long.parseLong(fields[3]);
        PEvent<?> event;
        switch (fields[2]) {
            case "set": event = new PEvents.eSet(value); break;
            case "check": event = new PEvents.eCheck(value); break;
            default: throw new IllegalArgumentException("Unknown operation.");
        }
        return Stream.of(new PObserveEvent<>(fields[0], time, event, (String) input));
    }
}
