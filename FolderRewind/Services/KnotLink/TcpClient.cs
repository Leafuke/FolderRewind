using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    public sealed class KlTcpClient : IAsyncDisposable, IDisposable
    {
        private readonly string _heartbeatMessage = "heartbeat";
        private readonly string _heartbeatResponse = "heartbeat_response";
        private readonly TimeSpan _heartbeatInterval;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly CancellationTokenSource _cts = new();
        private Task? _readTask;
        private Task? _heartbeatTask;

        public Func<string, Task>? OnDataReceivedAsync { get; set; }

        public bool Running => _client?.Connected == true && !_cts.IsCancellationRequested;

        public KlTcpClient(TimeSpan? heartbeatInterval = null)
        {
            _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromMinutes(3);
        }

        public async Task<bool> ConnectAsync(string host, int port)
        {
            _client = new TcpClient();
            await _client.ConnectAsync(host, port).ConfigureAwait(false);
            _stream = _client.GetStream();

            _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
            return true;
        }

        public async Task SendAsync(string data, CancellationToken cancellationToken = default)
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("Client is not connected.");
            }

            byte[] buffer = Encoding.UTF8.GetBytes(data);
            await _stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            if (_stream == null)
            {
                return;
            }

            byte[] buffer = new byte[1024];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break; // remote closed
                    }

                    string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (text == _heartbeatResponse)
                    {
                        continue;
                    }

                    if (OnDataReceivedAsync != null)
                    {
                        await OnDataReceivedAsync(text).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on dispose
            }
            catch
            {
                // swallow network errors to allow graceful shutdown
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await SendAsync(_heartbeatMessage, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore heartbeat send errors
                    }

                    await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on dispose
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _readTask?.Wait(); } catch { }
            try { _heartbeatTask?.Wait(); } catch { }

            _stream?.Dispose();
            _client?.Close();
            _cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_readTask != null)
            {
                try { await _readTask.ConfigureAwait(false); } catch { }
            }

            if (_heartbeatTask != null)
            {
                try { await _heartbeatTask.ConfigureAwait(false); } catch { }
            }

            _stream?.Dispose();
            _client?.Close();
            _cts.Dispose();
        }
    }
}
