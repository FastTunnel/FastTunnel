using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FastTunnel.Core;
using FastTunnel.Core.Client;
using FastTunnel.Core.Logger;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Client Start!");

            var conf = new ConfigurationBuilder()
              .SetBasePath(Directory.GetCurrentDirectory())
              .AddJsonFile("appsettings.json", true, true)
              .Build();

            var settings = conf.Get<Appsettings>();

            Run(settings);
        }

        private static void Run(Appsettings settings)
        {
            var FastTunnelClient = new FastTunnelClient(settings.ClientSettings, new ConsoleLogger());
            FastTunnelClient.Login();
        }
    }
}
