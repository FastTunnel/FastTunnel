// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Linq;
using FastTunnel.Api.Models;
using FastTunnel.Core.Server;
using Microsoft.AspNetCore.Mvc;

namespace FastTunnel.Api.Controllers
{
    public class SystemController : BaseController
    {
        readonly FastTunnelServer fastTunnelServer;

        public SystemController(FastTunnelServer fastTunnelServer)
        {
            this.fastTunnelServer = fastTunnelServer;
        }

        /// <summary>
        /// 获取当前等待响应的请求数
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Obsolete("使用 GetResponseTempList 替换")]
        public ApiResponse GetResponseTempCount()
        {
            ApiResponse.data = fastTunnelServer.ResponseTasks.Count;
            return ApiResponse;
        }

        /// <summary>
        /// 获取当前等待响应的请求
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ApiResponse GetResponseTempList()
        {
            ApiResponse.data = new
            {
                Count = fastTunnelServer.ResponseTasks.Count,
                Rows = fastTunnelServer.ResponseTasks.Select(x => new
                {
                    x.Key
                })
            };

            return ApiResponse;
        }

        /// <summary>
        /// 获取当前映射的所有站点信息
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ApiResponse GetAllWebList()
        {
            ApiResponse.data = new
            {
                Count = fastTunnelServer.WebList.Count,
                Rows = fastTunnelServer.WebList.Select(x => new { x.Key, x.Value.WebConfig.LocalIp, x.Value.WebConfig.LocalPort })
            };

            return ApiResponse;
        }

        /// <summary>
        /// 获取服务端配置信息
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ApiResponse GetServerOption()
        {
            ApiResponse.data = fastTunnelServer.ServerOption;
            return ApiResponse;
        }

        /// <summary>
        /// 获取所有端口转发映射列表
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ApiResponse GetAllForwardList()
        {
            ApiResponse.data = new
            {
                Count = fastTunnelServer.ForwardList.Count,
                Rows = fastTunnelServer.ForwardList.Select(x => new { x.Key, x.Value.SSHConfig.LocalIp, x.Value.SSHConfig.LocalPort, x.Value.SSHConfig.RemotePort })

            };

            return ApiResponse;
        }

        /// <summary>
        /// 获取当前客户端在线数量
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ApiResponse GetOnlineClientCount()
        {
            ApiResponse.data = fastTunnelServer.ConnectedClientCount;
            return ApiResponse;
        }
    }
}
