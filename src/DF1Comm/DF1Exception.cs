namespace DF1Comm;

/// <summary>
/// Custom exception for DF1 library errors.
/// </summary>
public class DF1Exception : Exception
{
    public DF1Exception(string message) : base(message) { }

    public DF1Exception(string message, Exception inner) : base(message, inner) { }
}
