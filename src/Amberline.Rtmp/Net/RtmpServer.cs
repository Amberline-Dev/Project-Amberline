using RtmpSharp.IO;
using RtmpSharp.Messaging;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Threading;
using RtmpSharp.Messaging.Messages;

namespace RtmpSharp.Net
{
    public class RtmpServer : IAsyncDisposable
    {
        const int MaxAcceptedConnections = 64;
        public event EventHandler<RemotingMessageReceivedEventArgs> ClientMessageReceived;
        public event EventHandler<CommandMessageReceivedEventArgs> ClientCommandReceived;
        public event EventHandler<ConnectReceivedEventArgs> ClientConnectReceived;
        public event EventHandler ClientDisconnected;

        private TcpListener _listener;
        private IPEndPoint _serverEndPoint;
        private Uri _serverUri;
        private SerializationContext _context;

        private List<RtmpClient> _clients;
        private readonly object _sync = new object();
        private readonly List<TcpClient> _acceptedClients = new List<TcpClient>();
        private readonly List<Task> _connectionTasks = new List<Task>();
        private CancellationTokenSource _stopSource;
        private Task _acceptLoop;
        private Task _stopTask;

        private readonly RemoteCertificateValidationCallback certificateValidator = (sender, certificate, chain, errors) => true;
        private X509Certificate2 Certificate;
        private bool SSL = false;

        public RtmpServer(IPEndPoint ServerEndPoint, SerializationContext context)
        {
            _serverEndPoint = ServerEndPoint;
            _listener = new TcpListener(_serverEndPoint);
            _context = context;

            if (_context == null)
                _context = new SerializationContext();

            _clients = new List<RtmpClient>();

            string ServerUri = string.Format("rtmp://{0}:{1}", _serverEndPoint.Address, _serverEndPoint.Port);
            _serverUri = new Uri(ServerUri);
        }

        public RtmpServer(IPEndPoint ServerEndPoint, SerializationContext context, X509Certificate2 certificate)
            : this(ServerEndPoint, context)
        {
            SSL = true;
            Certificate = certificate;

            string ServerUri = string.Format("rtmps://{0}:{1}", _serverEndPoint.Address, _serverEndPoint.Port);
            _serverUri = new Uri(ServerUri);
        }

        public IPEndPoint BoundEndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public int PendingCallbackCount
        {
            get
            {
                lock (_sync)
                    return _clients.Sum(client => client.PendingCallbackCount);
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_acceptLoop != null)
                    throw new InvalidOperationException("The server has already been started.");

                _listener.Start();
                _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _acceptLoop = AcceptLoopAsync(_stopSource.Token);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            lock (_sync)
                return _stopTask ?? (_stopTask = StopCoreAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        async Task StopCoreAsync()
        {
            CancellationTokenSource stopSource;
            Task acceptLoop;
            TcpClient[] acceptedClients;
            RtmpClient[] clients;

            lock (_sync)
            {
                stopSource = _stopSource;
                acceptLoop = _acceptLoop;
                acceptedClients = _acceptedClients.ToArray();
                clients = _clients.ToArray();
            }

            if (stopSource != null)
                stopSource.Cancel();

            _listener.Stop();

            foreach (var client in acceptedClients)
                client.Close();

            foreach (var client in clients)
                client.Close();

            if (acceptLoop != null)
                await acceptLoop.ConfigureAwait(false);

            Task[] connectionTasks;
            lock (_sync)
            {
                acceptedClients = _acceptedClients.ToArray();
                clients = _clients.ToArray();
                connectionTasks = _connectionTasks.ToArray();
            }

            foreach (var client in acceptedClients)
                client.Close();

            foreach (var client in clients)
                client.Close();

            await Task.WhenAll(connectionTasks).ConfigureAwait(false);
        }

        async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (SocketException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    lock (_sync)
                    {
                        if (_acceptedClients.Count >= MaxAcceptedConnections)
                        {
                            client.Dispose();
                            continue;
                        }
                        _acceptedClients.Add(client);
                    }

                    var connectionTask = HandleClientAsync(client, cancellationToken);
                    lock (_sync)
                        _connectionTasks.Add(connectionTask);
                    _ = RemoveCompletedConnectionAsync(connectionTask, client);
                }
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        async Task RemoveCompletedConnectionAsync(Task connectionTask, TcpClient client)
        {
            try
            {
                await connectionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                lock (_sync)
                {
                    _acceptedClients.Remove(client);
                    _connectionTasks.Remove(connectionTask);
                }
            }
        }

        async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            Stream stream = null;
            RtmpClient rtmpClient = null;
            try
            {
                stream = await GetRtmpStreamAsync(client).ConfigureAwait(false);

                // read c0+c1
                var c01 = await RtmpHandshake.ReadAsync(stream, true).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (c01.Version != 3) throw new InvalidDataException("Unsupported RTMP version.");

                var random = new Random();
                var randomBytes = new byte[1528];
                random.NextBytes(randomBytes);

                // write s0+s1+s2
                var s01 = new RtmpHandshake()
                {
                    Version = 3,
                    Time = (uint)Environment.TickCount,
                    Time2 = 0,
                    Random = randomBytes
                };
                var s02 = c01.Clone();
                s02.Time2 = (uint)Environment.TickCount;
                await RtmpHandshake.WriteAsync(stream, s01, s02, true).ConfigureAwait(false);

                // read c02
                var c02 = await RtmpHandshake.ReadAsync(stream, false).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

                rtmpClient = new RtmpClient(_serverUri, _context, stream);
                rtmpClient.RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                rtmpClient.ServerMessageReceived += ServerMessageReceived;
                rtmpClient.ServerCommandReceived += ServerCommandReceived;
                rtmpClient.ServerConnectReceived += ServerConnectReceived;
                var disconnected = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler onDisconnected = (sender, args) => disconnected.TrySetResult(null);
                rtmpClient.Disconnected += onDisconnected;
                lock (_sync)
                    _clients.Add(rtmpClient);

                try
                {
                    await disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    rtmpClient.Close();
                }
                finally
                {
                    rtmpClient.Disconnected -= onDisconnected;
                    lock (_sync)
                        _clients.Remove(rtmpClient);
                    ClientDisconnected?.Invoke(rtmpClient, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (TimeoutException)
            {
                // A peer that never completes the handshake must release its connection slot.
            }
            finally
            {
                if (stream != null)
                    stream.Dispose();
                client.Dispose();
            }
        }

        void ServerConnectReceived(object sender, ConnectReceivedEventArgs e)
        {
            ClientConnectReceived?.Invoke(sender, e);
        }

        void ServerCommandReceived(object sender, CommandMessageReceivedEventArgs e)
        {
            RtmpClient client = (RtmpClient)sender;
            ClientCommandReceived?.Invoke(sender, e);

            if (e.Error != null)
            {
                client.InvokeError(e.InvokeId, e.Error);
                return;
            }

            if (e.Handled || e.Message.Operation == CommandOperation.ClientPing)
            {
                var acknowledgement = new AcknowledgeMessageExt
                {
                    Body = e.Result,
                    CorrelationId = e.Message.MessageId,
                    MessageId = Uuid.NewUuid(),
                    ClientId = e.Message.Operation == CommandOperation.Subscribe ||
                               e.Message.Operation == CommandOperation.Unsubscribe
                        ? e.Message.ClientId
                        : Uuid.NewUuid()
                };
                if (e.Message.Operation == CommandOperation.Login)
                {
                    acknowledgement.Headers = new AsObject
                    {
                        { FlexMessageHeaders.FlexMessagingVersion, 1.0 },
                        { FlexMessageHeaders.FlexClientId, string.IsNullOrEmpty(e.DSId) ? client.ClientId : e.DSId }
                    };
                }

                client.InvokeResult(e.InvokeId, acknowledgement);
                return;
            }

            client.InvokeError(
                e.InvokeId,
                e.Message.MessageId,
                "Server.Error.CommandNotFound",
                $"Unsupported command operation {e.Message.Operation}.");
        }

        void ServerMessageReceived(object sender, RemotingMessageReceivedEventArgs e)
        {
            if (ClientMessageReceived != null)
            {
                ClientMessageReceived(sender, e);
                if (e.ReturnRequired)
                {
                    RtmpClient client = (RtmpClient)sender;
                    client.InvokeResult(e.InvokeId, e.MessageId, e.Data);
                }
            }
        }



        public void Listen()
        {
            StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        async Task<Stream> GetRtmpStreamAsync(TcpClient client)
        {
            var stream = client.GetStream();

            if (SSL && Certificate != null)
            {
                var ssl = new SslStream(stream, false, certificateValidator);
                await ssl.AuthenticateAsServerAsync(Certificate);
                return ssl;
            }
            else
            {
                return stream;
            }
        }
    }
}
