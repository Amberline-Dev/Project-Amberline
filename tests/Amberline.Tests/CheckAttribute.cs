namespace Amberline.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CheckAttribute : Attribute
{
    public CheckAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A check name is required.", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// The check touches something shared outside its own process or state it cannot
    /// restore: a network port, a launched process, a fixed file, environment
    /// variables, the working directory, Console redirection, or the AIR/host/server
    /// lifecycle. Serial checks never run beside parallel shards; see
    /// <see cref="CheckIsolation"/> and docs/testing.md.
    /// </summary>
    public bool Serial { get; set; }

    /// <summary>
    /// A slow check's own timeout in seconds. The runner gives the check the larger of this and its <c>--timeout</c> (default 120 s),
    /// so a check whose cost grows with the roster is not cut off in a full parallel run. Zero (the default) keeps the run's timeout.
    /// </summary>
    public double TimeoutSeconds { get; set; }
}
