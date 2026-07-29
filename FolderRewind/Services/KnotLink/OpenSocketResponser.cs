/*
 * KnotLink SDK - C#
 * Copyright (c) 2024-2026 KnotLink Contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    public sealed class OpenSocketResponser : IAsyncDisposable, IDisposable
    {
        private readonly KlTcpClient _client;
        private readonly string _appId;
        private readonly string _openSocketId;
        private readonly string _host;
        private readonly int _port;

        public Func<string, Task<string>>? OnQuestionAsync { get; set; }
        public Func<Exception, Task>? OnErrorAsync
        {
            get => _client.OnErrorAsync;
            set => _client.OnErrorAsync = value;
        }
        public OpenSocketResponser(
            string appId,
            string openSocketId,
            string host = "127.0.0.1",
            int port = 6378,
            Func<string, Task<string>>? onQuestionAsync = null)
        {
            _appId = appId;
            _openSocketId = openSocketId;
            _host = host;
            _port = port;
            OnQuestionAsync = onQuestionAsync;
            _client = new KlTcpClient();
            _client.OnDataReceivedAsync = HandleDataAsync;

            ConnectAndRegister();
        }

        private void ConnectAndRegister()
        {
            _client.ConnectAsync(_host, _port).GetAwaiter().GetResult();
            RegisterAsync().GetAwaiter().GetResult();
        }

        private async Task RegisterAsync()
        {
            string key = _appId + "-" + _openSocketId;
            await _client.SendAsync(key).ConfigureAwait(false);
        }

        private async Task HandleDataAsync(string data)
        {
            string[] parts = data.Split(new[] { "&*&" }, 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                return;
            }

            string questionId = parts[0];
            string payload = parts[1];

            if (OnQuestionAsync == null)
                throw new InvalidOperationException("OnQuestionAsync callback is not set. Call SetRecvFunc or set OnQuestionAsync before receiving data.");

            string reply = await OnQuestionAsync(payload).ConfigureAwait(false);
            string response = questionId + "&*&" + reply;
            await _client.SendAsync(response).ConfigureAwait(false);
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
