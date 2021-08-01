using FastTunnel.Core.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Protocol
{
    public class TunnelProtocol
    {
        string massgeTemp;
        string m_sectionFlag = "\n";

        public IEnumerable<string> HandleBuffer(byte[] buffer, int offset, int count)
        {
            var words = buffer.GetString(offset, count);
            var sum = massgeTemp + words;

            if (sum.Contains(m_sectionFlag))
            {
                var array = (sum).Split(m_sectionFlag);
                massgeTemp = null;
                var fullMsg = words.EndsWith(m_sectionFlag);

                if (!fullMsg)
                {
                    massgeTemp = array[array.Length - 1];
                }

                return array.Take(array.Length - 1);
            }
            else
            {
                massgeTemp = sum;
                return null;
            }
        }
    }
}
