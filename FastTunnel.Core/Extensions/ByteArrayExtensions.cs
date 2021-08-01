using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Extensions
{
    public static class ByteArrayExtensions
    {
        public static string GetString(this byte[] buffer, int offset, int count)
        {
            return Encoding.UTF8.GetString(buffer, offset, count);
        }
    }
}
