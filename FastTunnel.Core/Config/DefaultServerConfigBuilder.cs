using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Config
{
    public class DefaultServerConfigBuilder
    {
        readonly DefaultServerConfig _options = new DefaultServerConfig();

        public DefaultServerConfigBuilder WithBindInfo(string host, int port)
        {
            _options.BindAddr = host;
            _options.BindPort = port;
            return this;
        }

        public DefaultServerConfigBuilder WithWebDomain(string domain)
        {
            _options.WebDomain = domain;
            return this;
        }

        public DefaultServerConfigBuilder WithSSHEnabled(bool enbaled)
        {
            _options.SSHEnabled = enbaled;
            return this;
        }

        public DefaultServerConfigBuilder WithHasNginxProxy(bool has)
        {
            _options.WebHasNginxProxy = has;
            return this;
        }

        public DefaultServerConfigBuilder WithWebAllowAccessIps(string[] ips)
        {
            _options.WebAllowAccessIps = ips;
            return this;
        }

        public DefaultServerConfigBuilder WithHTTPPort(int port)
        {
            _options.WebProxyPort = port;
            return this;
        }

        public DefaultServerConfig Build()
        {
            if (string.IsNullOrEmpty(_options.BindAddr))
                throw new ArgumentNullException("You must use WithBindInfo to set host");

            if (_options.BindPort == 0)
                throw new ArgumentNullException("You must use WithBindInfo to set port");

            if (string.IsNullOrEmpty(_options.WebDomain))
                throw new ArgumentNullException("You must use WithWebDomain to set domain");

            return _options;
        }
    }
}
