public final class MonitorTypeMismatch {
    public static final class TranslationMonitor_Machine {
        public boolean deepEquals(TranslationMonitor other) { return true; }
        public boolean equals(Object other) {
            return other instanceof TranslationMonitor_Machine
                && deepEquals((TranslationMonitor) other);
        }
    }
}
