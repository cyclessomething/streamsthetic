package com.phonecam;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

/** Dependency-free regressions for the actual app helpers. Run with run-behavior-checks.ps1. */
public final class BehaviorChecks {
    private static int checks;

    private static void check(boolean result, String message) {
        if (!result) throw new AssertionError(message);
        checks++;
        System.out.println("PASS: " + message);
    }

    private static final class Clock implements OrientationDebouncer.Scheduler {
        private record Scheduled(long due, Runnable action) {}
        private long now;
        private final List<Scheduled> jobs = new ArrayList<>();

        @Override public void schedule(Runnable action, long delayMillis) { jobs.add(new Scheduled(now + delayMillis, action)); }
        @Override public void cancel(Runnable action) { jobs.removeIf(job -> job.action == action); }
        void advance(long milliseconds) {
            long until = now + milliseconds;
            while (true) {
                var next = jobs.stream().filter(job -> job.due <= until).min(Comparator.comparingLong(Scheduled::due));
                if (next.isEmpty()) break;
                jobs.remove(next.get());
                now = next.get().due;
                next.get().action.run();
            }
            now = until;
        }
    }

    public static void main(String[] args) {
        int[] choices = {0, 10, 30, 60};
        for (int index = 0; index < choices.length; index++) {
            check(SavedChoice.indexOrDefault(choices, choices[index], 2) == index,
                "saved auto-off " + choices[index] + " minutes restores its own selection");
        }
        check(SavedChoice.indexOrDefault(choices, -1, 2) == 2 && SavedChoice.indexOrDefault(choices, 999, 2) == 2,
            "only invalid auto-off values use the 30-minute fallback");

        var clock = new Clock();
        var seen = new ArrayList<Integer>();
        var turns = new OrientationDebouncer(270, clock, seen::add);
        turns.onReading(0);
        clock.advance(999);
        check(seen.isEmpty(), "new orientation is not committed before one second");
        clock.advance(1);
        check(seen.equals(List.of(0)), "perfectly still new orientation commits without another sensor callback");
        clock.advance(2000);
        check(seen.size() == 1 && clock.jobs.isEmpty(), "one stable turn commits once and leaves no polling callback");

        turns.onReading(88);
        clock.advance(600);
        turns.onReading(92);
        clock.advance(400);
        check(seen.equals(List.of(0, 90)), "small movements inside the same quadrant do not restart its stable interval");

        turns.onReading(180);
        clock.advance(700);
        turns.onReading(-1);
        clock.advance(1000);
        check(seen.equals(List.of(0, 90)), "unknown or flat orientation cancels a pending turn");
        turns.onReading(180);
        clock.advance(700);
        turns.onReading(225);
        clock.advance(1000);
        check(seen.equals(List.of(0, 90)), "diagonal orientation cancels a pending turn");
        turns.onReading(180);
        clock.advance(700);
        turns.onReading(90);
        clock.advance(1000);
        check(seen.equals(List.of(0, 90)), "returning to the original orientation cancels a pending turn");

        turns.onReading(180);
        clock.advance(600);
        Runnable stale = clock.jobs.get(0).action;
        turns.onReading(270);
        stale.run();
        clock.advance(999);
        check(seen.equals(List.of(0, 90)), "replacement quadrant gets a full second and ignores a stale canceled callback");
        clock.advance(1);
        check(seen.equals(List.of(0, 90, 270)), "replacement quadrant commits at its own deadline");
        turns.onReading(359);
        clock.advance(1000);
        check(seen.equals(List.of(0, 90, 270, 0)), "359-degree wraparound correctly returns to portrait");

        turns.onReading(180);
        Runnable stoppedCallback = clock.jobs.get(0).action;
        turns.stop();
        stoppedCallback.run();
        turns.onReading(90);
        clock.advance(5000);
        check(seen.equals(List.of(0, 90, 270, 0)) && clock.jobs.isEmpty(), "stop cancels pending work and rejects future or stale callbacks");

        // The sensor helper must keep tracking while the consumer locks its displayed/manual rotation.
        var manualClock = new Clock();
        int[] sensed = {270};
        int[] displayed = {180};
        boolean[] auto = {false};
        var manual = new OrientationDebouncer(sensed[0], manualClock, angle -> {
            sensed[0] = angle;
            if (auto[0]) displayed[0] = angle;
        });
        manual.onReading(0);
        manualClock.advance(1000);
        check(sensed[0] == 0 && displayed[0] == 180, "manual display lock does not stop stable sensor tracking");
        auto[0] = true;
        displayed[0] = sensed[0];
        manual.onReading(180);
        manualClock.advance(1000);
        check(displayed[0] == 180 && sensed[0] == 180, "stable sensor events continue when automatic mode resumes");
        manual.stop();
        System.out.println("ANDROID BEHAVIOR CHECKS PASS (" + checks + " checks)");
    }
}
