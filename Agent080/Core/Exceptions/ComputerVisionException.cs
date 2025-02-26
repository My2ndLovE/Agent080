namespace Agent080.Core.Exceptions
{
    [Serializable]
    public class ComputerVisionException : Exception
    {
        public ComputerVisionException() : base() { }
        public ComputerVisionException(string message) : base(message) { }
        public ComputerVisionException(string message, Exception innerException) : base(message, innerException) { }
    }
}