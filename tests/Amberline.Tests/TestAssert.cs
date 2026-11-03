namespace Amberline.Tests;

public static class TestAssert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected true.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        var equal = expected is System.Collections.IEnumerable expectedItems &&
                    actual is System.Collections.IEnumerable actualItems &&
                    expected is not string && actual is not string
            ? expectedItems.Cast<object?>().SequenceEqual(actualItems.Cast<object?>())
            : EqualityComparer<T>.Default.Equals(expected, actual);
        if (!equal)
        {
            // A named assertion still says what it compared: a failure in a log is read without the check at hand.
            throw new InvalidOperationException(message is null ? $"Expected {expected}, got {actual}." : $"{message}: expected {expected}, got {actual}.");
        }
    }

    public static void Near(double expected, double actual, double tolerance, string? message = null)
    {
        if (tolerance < 0 || double.IsNaN(tolerance) || double.IsNaN(expected) ||
            double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message ?? "Values differ"}: expected {expected}, got {actual} (tolerance {tolerance}).");
        }
    }

    public static void Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(message ??
                $"Expected {typeof(TException).Name}, got {exception.GetType().Name}.", exception);
        }

        throw new InvalidOperationException(message ?? $"Expected {typeof(TException).Name}.");
    }
}
