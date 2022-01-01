// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

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

        AuthError = 1,

        Exception = 2,

        NoAccount = 3,
    }
}
