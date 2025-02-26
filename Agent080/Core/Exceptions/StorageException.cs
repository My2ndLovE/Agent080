using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent080.Core.Exceptions
{
    [Serializable]
    public class StorageException : Exception
    {
        public StorageException() : base() { }
        public StorageException(string message) : base(message) { }
        public StorageException(string message, Exception innerException) : base(message, innerException) { }
    }
}