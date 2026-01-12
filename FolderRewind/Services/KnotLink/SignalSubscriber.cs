using System;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    public sealed class SignalSubscriber : IAsyncDisposable, IDisposable
    {
        private readonly KlTcpClient _client;
        private readonly string _appId;
        private readonly string _signalId;
        private readonly string _host;
        private readonly int _port;

        public Func<string, Task>? OnSignalAsync { get; set; }

        public SignalSubscriber(string appId, string signalId, string host = "127.0.0.1", int port = 6372)
        {
            _appId = appId;
            _signalId = signalId;
            _host = host;
            _port = port;

            _client = new KlTcpClient();
            _client.OnDataReceivedAsync = HandleSignalAsync;

            ConnectAndSubscribe();
        }

        private void ConnectAndSubscribe()
        {
            _client.ConnectAsync(_host, _port).GetAwaiter().GetResult();
            SubscribeAsync().GetAwaiter().GetResult();
        }

        private async Task SubscribeAsync()
        {
            string key = _appId + "-" + _signalId;
            await _client.SendAsync(key).ConfigureAwait(false);
        }

        private Task HandleSignalAsync(string data)
        {
            if (OnSignalAsync != null)
            {
                return OnSignalAsync(data);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            return _client.DisposeAsync();
        }
    }
}
