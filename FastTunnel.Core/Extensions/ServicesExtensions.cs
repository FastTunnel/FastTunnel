using FastTunnel.Core.Filters;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Extensions
{
    public static class ServicesExtensions
    {
        public static void AddFastTunnel(this IServiceCollection service)
        {
            service.AddTransient<IAuthenticationFilter, DefaultAuthenticationFilter>();
        }
    }
}
