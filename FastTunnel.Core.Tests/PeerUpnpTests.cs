using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using FastTunnel.Core.P2P;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FastTunnel.Core.Tests;

public class PeerUpnpTests
{
    [Fact]
    public async Task GatewayDiscoveryMapsAndReleasesOnlyTheOwnedUdpPort()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var ssdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        const string type = "urn:schemas-upnp-org:service:WANIPConnection:1";
        string? description = null;
        var deleted = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapGet("/igd", () => "<root><device><serviceList><service><serviceType>" + type
            + "</serviceType><controlURL>/control</controlURL></service></serviceList></device></root>");
        app.MapPost("/control", async context =>
        {
            var action = context.Request.Headers["SOAPAction"].ToString().Trim('"').Split('#').Last();
            var message = await XDocument.LoadAsync(context.Request.Body, LoadOptions.None, context.RequestAborted);
            string Field(string name) => message.Descendants().Single(element => element.Name.LocalName == name).Value;
            if (action == "GetExternalIPAddress")
            {
                await context.Response.WriteAsync("<response><NewExternalIPAddress>203.0.113.9</NewExternalIPAddress></response>");
            }
            else if (action == "GetSpecificPortMappingEntry")
            {
                if (description == null)
                    context.Response.StatusCode = 500;
                else
                    await context.Response.WriteAsync("<response><NewInternalClient>127.0.0.1</NewInternalClient>"
                        + "<NewInternalPort>41234</NewInternalPort><NewPortMappingDescription>" + description
                        + "</NewPortMappingDescription></response>");
            }
            else if (action == "AddPortMapping")
            {
                Assert.Equal("UDP", Field("NewProtocol"));
                Assert.Equal("41234", Field("NewInternalPort"));
                Assert.Equal("300", Field("NewLeaseDuration"));
                description = Field("NewPortMappingDescription");
                await context.Response.WriteAsync("<response/>");
            }
            else if (action == "DeletePortMapping")
            {
                Assert.Equal("41234", Field("NewExternalPort"));
                deleted++;
                await context.Response.WriteAsync("<response/>");
            }
        });
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var lookup = PeerUpnp.DiscoverAsync(IPAddress.Loopback, IPAddress.Loopback, timeout.Token,
            ((IPEndPoint)ssdp.Client.LocalEndPoint!).Port);
        var search = await ssdp.ReceiveAsync(timeout.Token);
        await ssdp.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://127.0.0.2:1/igd\r\n\r\n"),
            search.RemoteEndPoint, timeout.Token);
        await ssdp.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: " + address + "/igd\r\n\r\n"),
            search.RemoteEndPoint, timeout.Token);
        var control = Assert.IsType<PeerUpnp.Control>(await lookup);
        Assert.Equal(type, control.Type);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 41234),
            await PeerUpnp.MapAsync(control, IPAddress.Loopback, 41234, null, 300, timeout.Token));
        var owned = description;
        description = "other-client";
        await PeerUpnp.ReleaseAsync(control, IPAddress.Loopback, 41234, 41234, timeout.Token);
        Assert.Equal(0, deleted);
        description = owned;
        await PeerUpnp.ReleaseAsync(control, IPAddress.Loopback, 41234, 41234, timeout.Token);
        Assert.Equal(1, deleted);
    }
}
