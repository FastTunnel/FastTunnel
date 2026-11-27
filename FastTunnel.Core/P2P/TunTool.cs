using System.Diagnostics;

namespace FastTunnel.Core.P2P;

/// <summary>
/// 创建虚拟网卡需要的参数，全部来自服务端组网目录和客户端配置。
/// <paramref name="Dns"/> 为真时，平台实现还要把这块网卡的解析指向 <paramref name="Address"/>，
/// 让 <c>.ft</c> 域名能直接查到成员的虚拟地址。
/// </summary>
internal readonly record struct TunSetup(string Name, string Address, string NetMask, int PrefixLength, int Mtu, bool Dns);

/// <summary>
/// 调系统命令配置网卡。参数一律走 <see cref="ProcessStartInfo.ArgumentList"/>，
/// 不拼命令串，接口名也在 <see cref="PeerTun"/> 里校验过。
/// </summary>
internal static class TunProcess
{
    internal static (int Code, string Error) Run(string file, string[] args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(file)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start())
            {
                return (-1, "进程未能启动");
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "执行超时");
            }

            return (process.ExitCode, process.StandardError.ReadToEnd().Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (-1, ex.Message);
        }
    }
}
