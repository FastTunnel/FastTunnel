using FastTunnel.Core.Config;
using FastTunnel.Core.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FastTunnel.Core.Host
{
    public class Host
    {
        private ServiceCollection ServiceCollection;

        public Host()
        {
            var config = new ConfigurationBuilder()
                 .SetBasePath(System.IO.Directory.GetCurrentDirectory()) //From NuGet Package Microsoft.Extensions.Configuration.Json
                 .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                 .Build();

            Init(config);
        }

        public void Init(IConfiguration config)
        {
            ServiceCollection = new ServiceCollection();
            ServiceCollection.AddLogging(loggingBuilder =>
               {
                   // configure Logging with NLog
                   loggingBuilder.ClearProviders();
                   loggingBuilder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                   loggingBuilder.AddNLog(config);
               });
        }

        public Host Config(Action<ServiceCollection> fun)
        {
            fun.Invoke(ServiceCollection);
            return this;
        }

        public IServiceProvider Build()
        {
            return ServiceCollection.BuildServiceProvider();
        }
    }
}
