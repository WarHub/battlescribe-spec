package bsspec.uiagent;

import javafx.scene.control.Spinner;
import javafx.scene.control.SpinnerValueFactory;

/**
 * Putting a number into a JavaFX {@link Spinner}.
 *
 * <p><b>The value has to be boxed as the type the spinner's own value factory declares.</b> The
 * factory's type parameter is erased, so {@code setValue} accepts any object and the mismatch
 * surfaces nowhere near the call: {@code Spinner}'s constructor registers a listener that pushes
 * every new value through the factory's {@code StringConverter} to refresh the editor text, and
 * that converter's synthetic bridge method casts to its declared type. An {@code Integer} handed to
 * a {@code DoubleSpinnerValueFactory} therefore throws {@link ClassCastException} out of a property
 * listener — which JavaFX does not hand back to the caller, but routes to the thread's
 * uncaught-exception handler (see {@link FxExceptionMonitor}).
 *
 * <p>So the write appears to succeed. The model takes the value and the action reports success; the
 * casualties are the things nobody was looking at. The editor is left displaying the PREVIOUS value
 * — a later commit of that stale text would put it back — and the FX event dispatch that was
 * running is abandoned where it threw, so whatever it had not done yet does not happen.
 *
 * <p>It throws TWICE per write, which is how much gets skipped: once from the control's text
 * listener, and once from the range check {@code DoubleSpinnerValueFactory} registers on its own
 * value in its constructor. That second one is not cosmetic — while it was throwing, <b>a value
 * outside the spinner's min/max was never clamped</b>, so the control held something it cannot
 * hold. The roster lane measured exactly twenty of these per run, ten writes' worth, all from
 * {@link RosterActions}'s New Roster cost-limit spinner being handed an {@code int}.
 *
 * <p><b>Neither type can be assumed</b>, which is why the factory decides rather than the call
 * site: the roster dialogs' cost limits are {@code Double}, while the Data Editor's
 * {@code spnRevision} is {@code Integer} and rejects a {@code Double} the same way in reverse.
 */
final class Spinners {

    private Spinners() {
    }

    /**
     * Sets {@code spinner} to {@code value}, boxed as its value factory's type, rounding to the
     * nearest whole number for an integer factory.
     *
     * <p>Must be called from the FX thread.
     */
    @SuppressWarnings({"unchecked", "rawtypes"})
    static void setValue(Spinner<?> spinner, double value) {
        SpinnerValueFactory factory = spinner.getValueFactory();
        if (factory == null) {
            throw new RuntimeException("Spinner has no value factory");
        }
        factory.setValue(boxed(factory, value));
    }

    /**
     * {@code value} as the factory's own type.
     *
     * <p>The factory's CLASS is the authority, not its current value: a factory still holding null
     * says nothing about the type it wants, and a freshly built dialog is exactly where that
     * happens. The current value is consulted only for a factory that is neither of the two numeric
     * ones JavaFX ships — a custom or list-backed one — where the class says nothing either.
     */
    private static Object boxed(SpinnerValueFactory<?> factory, double value) {
        if (factory instanceof SpinnerValueFactory.DoubleSpinnerValueFactory) {
            return value;
        }
        if (factory instanceof SpinnerValueFactory.IntegerSpinnerValueFactory) {
            return (int) Math.round(value);
        }
        Object current = factory.getValue();
        if (current instanceof Double || current instanceof Float) {
            return value;
        }
        return (int) Math.round(value);
    }
}
