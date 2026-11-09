package fixtures.syntax;

import java.util.List;

/** A small, read-only highlighting fixture. Never compile or execute it in the app. */
@Deprecated(since = "21")
public sealed interface ReviewExample permits SafeReview {
    int DEFAULT_COUNT = 1_000;
    long MASK = 0xCA_FE_BA_BEL;
    int FLAGS = 0b1010_0011;
    double RATE = .5e+2D;
    float HEX_RATE = 0x1.fp2F;
    char QUOTE = '\'';
    String URL = "https://example.invalid/review";
    String MESSAGE = "say \"// not a comment\"";

    List<String> compare(List<String> input) throws IllegalArgumentException;

    default boolean isReady() {
        return true;
    }
}

final class SafeReview implements ReviewExample {
    private transient int count = 077;
    private volatile boolean canceled = false;

    @Override
    public synchronized List<String> compare(List<String> input) throws IllegalArgumentException {
        assert input != null : "input is required";
        try {
            for (Object value : input) {
                if (value instanceof String text) {
                    count += text.length();
                }
            }
            return switch (count) {
                case 0 -> List.of();
                default -> input;
            };
        } catch (RuntimeException error) {
            throw error; // Preserve the original failure.
        }
    }

    void $helper() {}
}

record PatchResult(boolean clean, double score) {}
@interface ReviewTag {}
class Überprüfung {}
