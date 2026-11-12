package com.phonecam;

import java.util.function.IntConsumer;

/** Commits a stable quadrant after one second, even if the sensor sends no further angle changes. */
public final class OrientationDebouncer {
    public interface Scheduler {
        void schedule(Runnable action, long delayMillis);
        void cancel(Runnable action);
    }

    private final Scheduler scheduler;
    private final IntConsumer onStable;
    private int stable;
    private int pending = -1;
    private long generation;
    private Runnable pendingCommit;
    private boolean stopped;

    public OrientationDebouncer(int initialOrientation, Scheduler scheduler, IntConsumer onStable) {
        this.stable = initialOrientation;
        this.scheduler = scheduler;
        this.onStable = onStable;
    }

    /** Called on the same thread as the scheduler. Unknown/diagonal readings cancel a pending turn. */
    public void onReading(int degrees) {
        if (stopped) return;
        if (degrees < 0 || degrees >= 360) { cancelPending(); return; }
        int quadrant = ((degrees + 45) / 90 * 90) % 360;
        int distance = Math.abs(((degrees - quadrant + 540) % 360) - 180);
        if (distance > 30 || quadrant == stable) { cancelPending(); return; }
        if (quadrant == pending) return;
        cancelPending();
        pending = quadrant;
        long scheduledGeneration = generation;
        pendingCommit = () -> {
            if (stopped || generation != scheduledGeneration || pending != quadrant) return;
            pendingCommit = null;
            pending = -1;
            stable = quadrant;
            onStable.accept(quadrant);
        };
        scheduler.schedule(pendingCommit, 1000);
    }

    public void stop() {
        stopped = true;
        cancelPending();
    }

    private void cancelPending() {
        generation++;
        if (pendingCommit != null) scheduler.cancel(pendingCommit);
        pendingCommit = null;
        pending = -1;
    }
}
