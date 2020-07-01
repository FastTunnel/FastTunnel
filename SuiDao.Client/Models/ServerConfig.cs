using System;
using System.Collections.Generic;
using System.Text;

namespace SuiDao.Client.Models
{
    public class SuiDaoServerConfig
    {
        public SuiDaoServerInfo[] servers { get; set; }
    }

    public class SuiDaoServerInfo
    {
        public string ip { get; set; }

        public int bind_port { get; set; }

        public string server_name { get; set; }

        public long server_id { get; set; }
    }
}