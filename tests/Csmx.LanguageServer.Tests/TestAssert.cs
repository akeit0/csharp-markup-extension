namespace Csmx.Tests;

internal static class TestAssert
{
    internal static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void AssertNull(string? value, string message)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"{message} Got: {value}");
        }
    }

    internal static void AssertContains(string? value, string expected, string label)
    {
        if (value is null || !value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} should contain '{expected}'. Got: {value ?? "<null>"}"
            );
        }
    }

    internal static void AssertEqual(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
        }
    }

    internal static void AssertDateEqual(DateTime expected, DateTime actual, string label)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"{label}: expected '{expected:O}', got '{actual:O}'."
            );
        }
    }
}
