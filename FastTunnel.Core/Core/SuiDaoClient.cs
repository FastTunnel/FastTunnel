using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Extensions;
using System.Timers;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Core
{
    public class FastTunnelClient
    {
        SuiDaoServer _serverConfig;

        Connecter _client;

        ILogger<FastTunnelClient> _logger;

        System.Timers.Timer timer_timeout;
        System.Timers.Timer timer_heart;

        Func<Connecter> login;
        double heartInterval = 10 * 1000; // 10 秒心跳
        DateTime lastHeart;
        Thread th;

        int reTrySpan = 30 * 1000; // 登陆失败后重试间隔

        public FastTunnelClient(ILogger<FastTunnelClient> logger)
        {
            _logger = logger;

            initailTimer();
        }

        private void initailTimer()
        {
            timer_heart = new System.Timers.Timer();
            timer_heart.AutoReset = true;
            timer_heart.Interval = heartInterval;
            timer_heart.Elapsed += HeartElapsed;

            timer_timeout = new System.Timers.Timer();
            timer_timeout.AutoReset = true;
            timer_timeout.Interval = heartInterval + heartInterval / 2;
            timer_timeout.Elapsed += TimeoutElapsed;
        }

        private void TimeoutElapsed(object sender, ElapsedEventArgs e)
        {
            if (lastHeart == null)
                return;

            var timer = sender as System.Timers.Timer;
            var span = (DateTime.Now - lastHeart).TotalMilliseconds;
            if (span > timer.Interval)
            {
                _logger.LogDebug($"上次心跳时间为{span}ms前");

                // 重新登录
                reConnect();
            }
        }

        private void reConnect()
        {
            Close();
            try
            {
                _client = login.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);

                Thread.Sleep(reTrySpan);
                reConnect();
                return;
            }

            LogSuccess(_client.Socket);
        }

        private void HeartElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                _client.Send(new Message<object> { MessageType = MessageType.Heart, Content = null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }
        }

        public void Login(Func<Connecter> fun, SuiDaoServer serverConfig)
        {
            _serverConfig = serverConfig;

            login = fun;
            try
            {
                _client = login.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);

                Thread.Sleep(reTrySpan);
                reConnect();
                return;
            }

            LogSuccess(_client.Socket);
        }

        void Close()
        {
            timer_heart.Stop();
            timer_timeout.Stop();

            try
            {
                if (_client.Socket.Connected)
                {
                    _client.Socket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
            finally
            {
                _client.Socket.Close();
                _logger.LogDebug("已退出登录\n");
            }
        }

        private void LogSuccess(Socket socket)
        {
            _logger.LogDebug("通信已建立");

            // 心跳开始
            timer_heart.Start();
            timer_timeout.Start();

            th = new Thread(ReceiveServer);
            th.Start(socket);
        }

        private void ReceiveServer(object obj)
        {
            var client = obj as Socket;
            byte[] buffer = new byte[1024];

            string lastBuffer = string.Empty;
            int n;

            while (true)
            {
                try
                {
                    n = client.Receive(buffer);
                    if (n == 0)
                    {
                        client.Shutdown(SocketShutdown.Both);
                        break;
                    }
                }
                catch
                {
                    break;
                }

                string words = Encoding.UTF8.GetString(buffer, 0, n);
                if (!string.IsNullOrEmpty(lastBuffer))
                {
                    words = lastBuffer + words;
                    lastBuffer = null;
                }

                var msgs = words.Split("\n");

                try
                {
                    foreach (var item in msgs)
                    {
                        if (string.IsNullOrEmpty(item))
                            continue;

                        if (item.EndsWith("}"))
                        {
                            HandleServerRequest(item);
                        }
                        else
                        {
                            lastBuffer = item;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                    continue;
                }
            }
        }

        private void HandleServerRequest(string words)
        {
            Message<object> Msg;

            try
            {
                Msg = JsonConvert.DeserializeObject<Message<object>>(words);
                switch (Msg.MessageType)
                {
                    case MessageType.Heart:
                        lastHeart = DateTime.Now;
                        break;
                    case MessageType.S_NewCustomer:
                        var request = (Msg.Content as JObject).ToObject<NewCustomerRequest>();
                        var connecter = new Connecter(_serverConfig.ServerAddr, _serverConfig.ServerPort);
                        connecter.Connect();
                        connecter.Send(new Message<SwapMsgModel> { MessageType = MessageType.C_SwapMsg, Content = new SwapMsgModel(request.MsgId) });

                        var localConnecter = new Connecter(request.WebConfig.LocalIp, request.WebConfig.LocalPort);
                        localConnecter.Connect();

                        new SocketSwap(connecter.Socket, localConnecter.Socket).StartSwap();
                        break;
                    case MessageType.S_NewSSH:
                        var request_ssh = (Msg.Content as JObject).ToObject<NewSSHRequest>();
                        var connecter_ssh = new Connecter(_serverConfig.ServerAddr, _serverConfig.ServerPort);
                        connecter_ssh.Connect();
                        connecter_ssh.Send(new Message<SwapMsgModel> { MessageType = MessageType.C_SwapMsg, Content = new SwapMsgModel(request_ssh.MsgId) });

                        var localConnecter_ssh = new Connecter(request_ssh.SSHConfig.LocalIp, request_ssh.SSHConfig.LocalPort);
                        localConnecter_ssh.Connect();

                        new SocketSwap(connecter_ssh.Socket, localConnecter_ssh.Socket).StartSwap();
                        break;
                    case MessageType.Log:
                        var msg = (Msg.Content as JObject).ToObject<LogMsg>();

                        switch (msg.MsgType)
                        {
                            case LogMsgType.Info:
                                _logger.LogInformation("From Server:" + msg.Msg);
                                break;
                            case LogMsgType.Error:
                                _logger.LogError("From Server:" + msg.Msg);
                                break;
                            case LogMsgType.Debug:
                                _logger.LogDebug("From Server:" + msg.Msg);
                                break;
                            default:
                                break;
                        }
                        
                        break;
                    case MessageType.C_SwapMsg:
                    case MessageType.C_LogIn:
                    default:
                        throw new Exception("参数异常");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
                _logger.LogError(words);
            }
        }
    }
}
