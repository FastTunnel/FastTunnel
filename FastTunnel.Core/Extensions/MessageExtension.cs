using Newtonsoft.Json;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class MessageExtension
    {
        public static string ToJson<T>(this Message<T> message)
        {
            return JsonConvert.SerializeObject(message, Formatting.None);
        }
    }
}
