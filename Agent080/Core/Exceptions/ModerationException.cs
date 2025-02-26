using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent080.Core.Exceptions
{
    [Serializable]
    public class ModerationException : Exception
    {
        public ModerationException() : base() { }
        public ModerationException(string message) : base(message) { }
        public ModerationException(string message, Exception innerException) : base(message, innerException) { }
    }
}