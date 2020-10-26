using NLog;
using NLog.Config;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Logger
{
    public class NlogConfig
    {
        public static LoggingConfiguration getNewConfig()
        {
            var config = new LoggingConfiguration();

            // Targets where to log to: File and Console
            var logfile = new NLog.Targets.FileTarget("file")
            {
                FileName = "${basedir}/Logs/${shortdate}.${level}.log",
                Layout = "${longdate} ${logger} ${message}${exception:format=ToString}"
            };

            var logconsole = new NLog.Targets.ConsoleTarget("console")
            {
                Layout = "${date}|${level:uppercase=true}|${message} ${exception} ${all-event-properties}"
            };

            // Rules for mapping loggers to targets
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logconsole);
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logfile);

            return config;
        }
    }
}
