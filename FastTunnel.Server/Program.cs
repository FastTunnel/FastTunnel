using Microsoft.Extensions.Configuration;
using FastTunnel.Core;
using FastTunnel.Core.Logger;
using FastTunnel.Core.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Server Start!");

            var conf = new ConfigurationBuilder()
              .SetBasePath(Directory.GetCurrentDirectory())
              .AddJsonFile("appsettings.json", true, true)
              .Build();

            var settings = conf.Get<Appsettings>();
            Run(settings);
        }

        private static void Run(Appsettings settings)
        {
            var server = new FastTunnelServer(settings.ServerSettings, new ConsoleLogger());
            server.Run();

            Console.ReadLine();
        }
    }
}
