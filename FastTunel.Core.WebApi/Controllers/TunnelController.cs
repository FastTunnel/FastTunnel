using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FastTunel.Core.WebApi.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class TunnelController : ControllerBase
    {
        FastTunnelServer _fastTunnelServer;

        public TunnelController(FastTunnelServer fastTunnelServer)
        {
            _fastTunnelServer = fastTunnelServer;
        }

        [HttpGet]
        public int GetWebCount()
        {
            return _fastTunnelServer.WebList.Count;
        }
    }
}
