using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace FastTunnel.Server.Controllers
{
    public class OperateContoller : Controller
    {
        [Route("restart")]
        public string Restart()
        {
            // TODO:Restart FastTunnel
            return "FastTunnel Will Restart";
        }
    }
}
