using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FastTunnel.Server.Models
{
    public class ApiResponse
    {
        /// <summary>
        /// 错误码
        /// 0 成功，其他为失败
        /// </summary>
        public ErrorCodeEnum errorCode { get; set; }

        public string errorMessage { get; set; }

        public object data { get; set; }
    }

    public enum ErrorCodeEnum
    {
        NONE = 0,
    }
}
