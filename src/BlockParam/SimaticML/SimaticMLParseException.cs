namespace BlockParam.SimaticML;

/// <summary>
/// Thrown when SimaticML XML cannot be parsed or has an unexpected structure.
/// </summary>
public class SimaticMLParseException : Exception
{
    public SimaticMLParseException(string message) : base(message) { }
    public SimaticMLParseException(string message, Exception inner) : base(message, inner) { }
}
