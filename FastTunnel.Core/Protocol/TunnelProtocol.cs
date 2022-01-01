// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
