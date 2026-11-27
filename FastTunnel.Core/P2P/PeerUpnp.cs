using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FastTunnel.Core.P2P;

internal static class PeerUpnp
{
    private const string ServicePrefix = "urn:schemas-upnp-org:service:";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        MaxResponseContentBufferSize = 64 * 1024,
        Timeout = TimeSpan.FromSeconds(2)
    };

    internal static async Task<Control?> DiscoverAsync(IPAddress local, IPAddress gateway, CancellationToken token,
        int ssdpPort = 1900)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var query = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n"
                + "MAN: \"ssdp:discover\"\r\nMX: 1\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
            await udp.SendAsync(query, new IPEndPoint(gateway, ssdpPort), timeout.Token);
            if (ssdpPort == Multicast.Port)
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    await udp.SendAsync(query, Multicast, timeout.Token);
                }
                catch (SocketException) { }
            }

            var locations = new HashSet<Uri>();
            while (locations.Count < 4)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                if (!gateway.Equals(response.RemoteEndPoint.Address) || response.Buffer.Length is < 12 or > 8192)
                    continue;
                var text = Encoding.ASCII.GetString(response.Buffer);
                if (!text.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase))
                    continue;

                var location = text.Split('\n').FirstOrDefault(line => line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
                if (location == null || !Uri.TryCreate(location[9..].Trim(), UriKind.Absolute, out var uri)
                    || !IsGatewayUri(uri, gateway) || !locations.Add(uri))
                    continue;
                using var description = await Http.GetAsync(uri, timeout.Token);
                if (!description.IsSuccessStatusCode)
                    continue;
                var xml = await ReadXmlAsync(description, timeout.Token);
                var baseText = xml.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "URLBase")?.Value;
                var baseUri = Uri.TryCreate(baseText, UriKind.Absolute, out var declared) && IsGatewayUri(declared, gateway)
                    ? declared : uri;
                foreach (var entry in xml.Descendants().Where(element => element.Name.LocalName == "service"))
                {
                    var type = Entry(entry, "serviceType");
                    if (type is not (ServicePrefix + "WANIPConnection:1" or ServicePrefix + "WANIPConnection:2"
                        or ServicePrefix + "WANPPPConnection:1"))
                        continue;
                    var controlUrl = Entry(entry, "controlURL");
                    if (!string.IsNullOrWhiteSpace(controlUrl) && Uri.TryCreate(baseUri, controlUrl, out var control)
                        && IsGatewayUri(control, gateway))
                        return new Control(control, type);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or XmlException or IOException) { }
        return null;
    }

    private static bool IsGatewayUri(Uri uri, IPAddress gateway)
        => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp && uri.UserInfo.Length == 0
            && IPAddress.TryParse(uri.Host, out var host) && host.Equals(gateway);

    internal static async Task<IPEndPoint?> MapAsync(Control control, IPAddress local, int port,
        int? previousExternalPort, uint seconds, CancellationToken token)
    {
        try
        {
            var external = await CallAsync(control, "GetExternalIPAddress", [], token);
            if (!IPAddress.TryParse(external?.Descendants().FirstOrDefault(x => x.Name.LocalName == "NewExternalIPAddress")?.Value,
                out var address) || !PeerUdpHub.IsCandidate(address))
                return null;

            var externalPort = previousExternalPort ?? port;
            var existing = await ReadMappingAsync(control, externalPort, token);
            var owned = IsOwned(existing, control, local, port);
            if (previousExternalPort != null && existing != null && !owned)
                return null;

            XElement[] fields =
            [
                new("NewRemoteHost", ""), new("NewExternalPort", externalPort), new("NewProtocol", "UDP"),
                new("NewInternalPort", port), new("NewInternalClient", local.ToString()), new("NewEnabled", 1),
                new("NewPortMappingDescription", control.Description), new("NewLeaseDuration", seconds)
            ];
            var mapped = existing == null || owned ? await CallAsync(control, "AddPortMapping", fields, token) : null;
            if (mapped != null)
                return new IPEndPoint(address, externalPort);

            if (previousExternalPort == null && control.Type.EndsWith(":2", StringComparison.Ordinal))
            {
                mapped = await CallAsync(control, "AddAnyPortMapping", fields, token);
                if (ushort.TryParse(mapped?.Descendants().FirstOrDefault(x => x.Name.LocalName == "NewReservedPort")?.Value,
                    out var reserved) && reserved != 0)
                    return new IPEndPoint(address, reserved);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or IOException) { }
        return null;
    }

    internal static async Task ReleaseAsync(Control control, IPAddress local, int internalPort,
        int externalPort, CancellationToken token)
    {
        var entry = await ReadMappingAsync(control, externalPort, token);
        if (IsOwned(entry, control, local, internalPort))
            await CallAsync(control, "DeletePortMapping", MappingKey(externalPort), token);
    }

    private static Task<XDocument?> ReadMappingAsync(Control control, int port, CancellationToken token)
        => CallAsync(control, "GetSpecificPortMappingEntry", MappingKey(port), token);

    private static XElement[] MappingKey(int port)
        => [new("NewRemoteHost", ""), new("NewExternalPort", port), new("NewProtocol", "UDP")];

    private static bool IsOwned(XDocument? mapping, Control control, IPAddress local, int port)
    {
        return mapping != null
            && IPAddress.TryParse(MappingField("NewInternalClient"), out var owner) && owner.Equals(local)
            && ushort.TryParse(MappingField("NewInternalPort"), out var mappedPort) && mappedPort == port
            && MappingField("NewPortMappingDescription") == control.Description;

        string? MappingField(string name) => mapping?.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
    }


    private static async Task<XDocument?> CallAsync(Control control, string action, XElement[] fields, CancellationToken token)
    {
        XNamespace ns = control.Type;
        var envelope = new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XElement(Soap + "Body", new XElement(ns + action, new XAttribute(XNamespace.Xmlns + "u", ns), fields)));
        using var request = new HttpRequestMessage(HttpMethod.Post, control.Uri)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{control.Type}#{action}\"");
        using var response = await Http.SendAsync(request, token);

        if (!response.IsSuccessStatusCode)
            return null;
        if (response.Content.Headers.ContentLength == 0 && action is "AddPortMapping" or "DeletePortMapping")
            return new XDocument();
        return await ReadXmlAsync(response, token);
    }

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024
        });
        return await XDocument.LoadAsync(reader, LoadOptions.None, token);
    }

    private static string? Entry(XElement element, string name)
        => element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

    internal sealed record Control(Uri Uri, string Type)
    {
        internal string Description { get; } = "FT-" + Guid.NewGuid().ToString("N")[..12];
    }
}
