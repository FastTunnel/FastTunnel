using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class ObjectExtensions
    {
        public static string ToJson(this object message)
        {
            return JsonConvert.SerializeObject(message, Formatting.None);
        }
    }
}
