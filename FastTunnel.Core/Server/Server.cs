// Implements the connection logic for the socket server.
// After accepting a connection, all data read from the client
// is sent back to the client. The read and echo back to the client pattern
// is continued until the client disconnects.
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Utility.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FastTunnel.Core.Server
{
    public class Server
    {
        private int m_numConnections;   // the maximum number of connections the sample is designed to handle simultaneously
        private int m_receiveBufferSize;// buffer size to use for each socket I/O operation

        BufferManager m_bufferManager;  // represents a large reusable set of buffers for all socket operations
        const int opsToPreAlloc = 2;    // read, write (don't alloc buffer space for accepts)
        Socket listenSocket;            // the socket used to listen for incoming connection requests
                                        // pool of reusable SocketAsyncEventArgs objects for write, read and accept socket operations
        SocketAsyncEventArgsPool m_readWritePool;
        //int m_totalBytesRead;           // counter of the total # bytes received by the server
        int m_numConnectedSockets;      // the total number of clients connected to the server
        Semaphore m_maxNumberAcceptedClients;

        Func<AsyncUserToken, string, bool> m_handller;
        string m_sectionFlag;
        IPEndPoint _localEndPoint;
        bool m_isHttpServer;

        ILogger _logger;

        // Create an uninitialized server instance.
        // To start the server listening for connection requests
        // call the Init method followed by Start method
        //
        // <param name="numConnections">the maximum number of connections the sample is designed to handle simultaneously</param>
        // <param name="receiveBufferSize">buffer size to use for each socket I/O operation</param>
        public Server(int numConnections, int receiveBufferSize, bool isHttpServer, ILogger logger)
        {
            m_isHttpServer = isHttpServer;
            _logger = logger;
            //m_totalBytesRead = 0;
            m_numConnectedSockets = 0;
            m_numConnections = numConnections;
            m_receiveBufferSize = receiveBufferSize;
            // allocate buffers such that the maximum number of sockets can have one outstanding read and
            // write posted to the socket simultaneously
            m_bufferManager = new BufferManager(receiveBufferSize * numConnections * opsToPreAlloc,
                receiveBufferSize);

            m_readWritePool = new SocketAsyncEventArgsPool(numConnections);

            m_maxNumberAcceptedClients = new Semaphore(numConnections, numConnections);
        }

        // Initializes the server by preallocating reusable buffers and
        // context objects.  These objects do not need to be preallocated
        // or reused, but it is done this way to illustrate how the API can
        // easily be used to create reusable objects to increase server performance.
        //
        public void Init()
        {
            // Allocates one large byte buffer which all I/O operations use a piece of.  This gaurds
            // against memory fragmentation
            m_bufferManager.InitBuffer();

            // preallocate pool of SocketAsyncEventArgs objects
            SocketAsyncEventArgs readWriteEventArg;

            for (int i = 0; i < m_numConnections; i++)
            {
                //Pre-allocate a set of reusable SocketAsyncEventArgs
                readWriteEventArg = new SocketAsyncEventArgs();
                readWriteEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                readWriteEventArg.UserToken = new AsyncUserToken();

                // assign a byte buffer from the buffer pool to the SocketAsyncEventArg object
                m_bufferManager.SetBuffer(readWriteEventArg);

                // add SocketAsyncEventArg to the pool
                m_readWritePool.Push(readWriteEventArg);
            }
        }

        // Starts the server such that it is listening for
        // incoming connection requests.
        //
        // <param name="localEndPoint">The endpoint which the server will listening
        // for connection requests on</param>
        public void Start(IPEndPoint localEndPoint, string sectionFlag, Func<AsyncUserToken, string, bool> handller)
        {
            m_handller = handller;
            m_sectionFlag = sectionFlag;
            _localEndPoint = localEndPoint;

            // create the socket which listens for incoming connections
            listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(localEndPoint);
            // start the server with a listen backlog of 100 connections
            listenSocket.Listen();

            // post accepts on the listening socket
            StartAccept(null);
        }

        // Begins an operation to accept a connection request from the client
        //
        // <param name="acceptEventArg">The context object to use when issuing
        // the accept operation on the server's listening socket</param>
        public void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(AcceptEventArg_Completed);
            }
            else
            {
                // socket must be cleared since the context object is being reused
                acceptEventArg.AcceptSocket = null;
            }

            m_maxNumberAcceptedClients.WaitOne();

            bool willRaiseEvent = listenSocket.AcceptAsync(acceptEventArg);
            if (!willRaiseEvent)
            {
                ProcessAccept(acceptEventArg);
            }
        }

        // This method is the callback method associated with Socket.AcceptAsync
        // operations and is invoked when an accept operation is complete
        void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            Interlocked.Increment(ref m_numConnectedSockets);
            _logger.LogInformation($"[当前连接数]:{_localEndPoint.Port} | {m_numConnectedSockets}");
            _logger.LogDebug($"leftPool: {m_readWritePool.Count}");

            try
            {
                // Get the socket for the accepted client connection and put it into the
                // ReadEventArg object user token
                SocketAsyncEventArgs readEventArgs = m_readWritePool.Pop();
                if (readEventArgs == null)
                {
                    _logger.LogCritical($"Pop result is Null {m_readWritePool.Count}");
                    release(e);
                    return;
                }

                var token = readEventArgs.UserToken as AsyncUserToken;
                token.Socket = e.AcceptSocket;
                token.MassgeTemp = null;
                token.Recived = null;

                // 客户端请求不需要分配msgid
                if (m_isHttpServer)
                {
                    token.RequestId = $"{DateTime.Now.GetChinaTicks()}_{Guid.NewGuid().ToString().Replace("-", string.Empty)}";
                    _logger.LogDebug($"Accept {token.RequestId}");
                }

                // As soon as the client is connected, post a receive to the connection
                bool willRaiseEvent = e.AcceptSocket.ReceiveAsync(readEventArgs);
                if (!willRaiseEvent)
                {
                    ProcessReceive(readEventArgs);
                }

                // Accept the next connection request
                StartAccept(e);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "[ProcessAccept error]");
                release(e);
            }
        }

        // This method is called whenever a receive or send operation is completed on a socket
        //
        // <param name="e">SocketAsyncEventArg associated with the completed receive operation</param>
        void IO_Completed(object sender, SocketAsyncEventArgs e)
        {
            // determine which type of operation just completed and call the associated handler
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }
        }

        // This method is invoked when an asynchronous receive operation completes.
        // If the remote host closed the connection, then the socket is closed.
        // If data was received then the data is echoed back to the client.
        //
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            AsyncUserToken token = (AsyncUserToken)e.UserToken;
            _logger.LogDebug($"[ProcessReceive]: {_localEndPoint.Port} | {token.RequestId}");
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                bool needRecive = false;
                var words = e.Buffer.GetString(e.Offset, e.BytesTransferred);
                var sum = token.MassgeTemp + words;

                // 只有http请求需要对已发送字节进行存储
                if (m_isHttpServer)
                {
                    if (token.Recived != null)
                    {
                        byte[] resArr = new byte[token.Recived.Length + e.BytesTransferred];
                        token.Recived.CopyTo(resArr, 0);
                        Array.Copy(e.Buffer, e.Offset, resArr, token.Recived.Length, e.BytesTransferred);
                        token.Recived = resArr;
                    }
                    else
                    {
                        byte[] resArr = new byte[e.BytesTransferred];
                        Array.Copy(e.Buffer, e.Offset, resArr, 0, e.BytesTransferred);
                        token.Recived = resArr;
                    }
                }

                if (sum.Contains(m_sectionFlag))
                {
                    var array = (sum).Split(m_sectionFlag);
                    token.MassgeTemp = null;
                    var fullMsg = words.EndsWith(m_sectionFlag);

                    if (!fullMsg)
                    {
                        token.MassgeTemp = array[array.Length - 1];
                    }

                    for (int i = 0; i < array.Length - 1; i++)
                    {
                        needRecive = m_handller(token, array[i]);
                        if (needRecive)
                        {
                            continue;
                        }
                        else
                        {
                            // ÊÍ·Å×ÊÔ´
                            release(e);
                            return;
                        }
                    }
                }
                else
                {
                    token.MassgeTemp = sum;
                }

                bool willRaiseEvent = token.Socket.ReceiveAsync(e);
                if (!willRaiseEvent)
                {
                    ProcessReceive(e);
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }

        // This method is invoked when an asynchronous send operation completes.
        // The method issues another receive on the socket to read any additional
        // data sent from the client
        //
        // <param name="e"></param>
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                // done echoing data back to the client
                AsyncUserToken token = (AsyncUserToken)e.UserToken;
                // read the next block of data send from the client
                e.SetBuffer(e.Offset, m_receiveBufferSize);
                bool willRaiseEvent = token.Socket.ReceiveAsync(e);
                if (!willRaiseEvent)
                {
                    ProcessReceive(e);
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }

        private void CloseClientSocket(SocketAsyncEventArgs e)
        {
            AsyncUserToken token = e.UserToken as AsyncUserToken;

            // close the socket associated with the client
            try
            {
                token.Socket.Shutdown(SocketShutdown.Send);
            }
            // throws if client process has already closed
            catch (Exception) { }
            token.Socket.Close();

            release(e);
        }

        private void release(SocketAsyncEventArgs e)
        {
            // decrement the counter keeping track of the total number of clients connected to the server
            Interlocked.Decrement(ref m_numConnectedSockets);

            _logger.LogInformation($"[SocketCount]:{_localEndPoint.Port} | {m_numConnectedSockets}");
            // Free the SocketAsyncEventArg so they can be reused by another client
            m_readWritePool.Push(e);

            m_maxNumberAcceptedClients.Release();

            _logger.LogDebug($"release ok");
        }
    }
}
