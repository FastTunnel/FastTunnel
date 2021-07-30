using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastTunnel.Core.Sockets
{
    public interface ISocketSwap
    {
        /// <summary>
        /// 前置操作
        /// </summary>
        /// <param name="fun"></param>
        /// <returns></returns>
        ISocketSwap BeforeSwap(Action fun);

        void StartSwap();
    }
}
