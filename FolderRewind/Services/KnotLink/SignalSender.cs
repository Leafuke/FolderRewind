using System;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    public sealed class SignalSender : IAsyncDisposable, IDisposable
    {
        private readonly KlTcpClient _client;
        private string _appId;
        private string _signalId;

        public SignalSender(string appId, string signalId, string host = "127.0.0.1", int port = 6370)
        {
            _appId = appId;
            _signalId = signalId;
            _client = new KlTcpClient();
            _client.ConnectAsync(host, port).GetAwaiter().GetResult();
        }

        public void SetConfig(string appId, string signalId)
        {
            _appId = appId;
            _signalId = signalId;
        }

        public Task EmitAsync(string data)
        {
            if (string.IsNullOrEmpty(_appId) || string.IsNullOrEmpty(_signalId))
            {
                throw new InvalidOperationException("AppId and SignalId must be set before emitting.");
            }

            string sKey = _appId + "-" + _signalId + "&*&";
            return _client.SendAsync(sKey + data);
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
