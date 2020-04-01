using System;
using System.Collections.Generic;
using System.Text;

namespace SuiDao.Server
{
    public class Tunnel
    {
        public int app_type { get; set; }

        /// <summary>
        /// 用户别名
        /// </summary>
        public string name { get; set; }

        public string sub_domain { get; set; }

        public string local_ip { get; set; }

        public int local_port { get; set; }

        public int remote_port { get; set; }
    }
}
