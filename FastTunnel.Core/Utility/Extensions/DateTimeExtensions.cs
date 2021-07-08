using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Utility.Extensions
{
    public static class DateTimeExtensions
    {
        public static string GetChinaTicks(this DateTime dateTime)
        {
            // 北京时间相差8小时
            DateTime startTime = TimeZoneInfo.ConvertTime(new DateTime(1970, 1, 1, 8, 0, 0, 0), TimeZoneInfo.Local);
            long t = (dateTime.Ticks - startTime.Ticks) / 10000;
            return t.ToString();
        }
    }
}
