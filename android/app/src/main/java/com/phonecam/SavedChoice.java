package com.phonecam;

/** Restores a saved numeric choice without replacing valid early entries with the default. */
public final class SavedChoice {
    private SavedChoice() {}

    public static int indexOrDefault(int[] options, int savedValue, int defaultIndex) {
        for (int index = 0; index < options.length; index++) {
            if (options[index] == savedValue) return index;
        }
        return defaultIndex;
    }
}
