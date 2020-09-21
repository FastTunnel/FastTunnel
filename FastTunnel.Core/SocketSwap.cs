using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FastTunnel.Core
{
    public class SocketSwap
    {
        private Socket _sockt1;
        private Socket _sockt2;
        bool Swaped = false;

        private class Channel
        {
            public Socket Send { get; set; }

            public Socket Receive { get; set; }
        }

        public SocketSwap(Socket sockt1, Socket sockt2)
        {
            _sockt1 = sockt1;
            _sockt2 = sockt2;
        }

        public void StartSwap()
        {
            Swaped = true;
            ThreadPool.QueueUserWorkItem(swapCallback, new Channel
            {
                Send = _sockt1,
                Receive = _sockt2
            });

            ThreadPool.QueueUserWorkItem(swapCallback, new Channel
            {
                Send = _sockt2,
                Receive = _sockt1
            });
        }

        private void swapCallback(object state)
        {
            var chanel = state as Channel;
            byte[] result = new byte[1024];

            while (true)
            {
                try
                {
                    if (!chanel.Receive.Connected)
                        break;
                    int num = chanel.Receive.Receive(result, result.Length, SocketFlags.None);

                    if (num == 0)
                    {
                        chanel.Receive.Close();

                        try
                        {
                            // Release the socket.//
                            chanel.Send.Shutdown(SocketShutdown.Both);
                        }
                        catch { }
                        finally
                        {
                            chanel.Send.Close();
                        }
                        break;
                    }

                    if (!chanel.Send.Connected)
                        break;

                    // var str = Encoding.UTF8.GetString(result, 0, num);

                    chanel.Send.Send(result, num, SocketFlags.None);
                }
                catch (SocketException)
                {
                    //  Interrupted function call. 10004
                    // An existing connection was forcibly closed by the remote host. 10054
                    try
                    {
                        chanel.Send.Shutdown(SocketShutdown.Both);
                    }
                    catch { }
                    finally
                    {
                        chanel.Send.Close();
                    }

                    try
                    {
                        chanel.Receive.Shutdown(SocketShutdown.Both);
                    }
                    catch { }
                    finally
                    {
                        chanel.Receive.Close();
                    }
                    break;
                }
                catch (Exception ex)
                {
                    Console.Write(ex.ToString());
                    throw;
                }
            }
        }

        internal SocketSwap BeforeSwap(Action fun)
        {
            if (Swaped)
            {
                throw new Exception("BeforeSwap must be invoked before StartSwap!");
            }

            fun?.Invoke();
            return this;
        }
    }
}
