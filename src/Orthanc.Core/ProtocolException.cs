namespace Orthanc.Core;

public sealed class ProtocolException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
